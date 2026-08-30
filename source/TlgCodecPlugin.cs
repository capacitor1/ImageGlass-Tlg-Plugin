//! ImageGlass v10 native codec plugin for KiriKiri TLG5/TLG6.
//! Decode-only: implements metadata + static raster decode quadrants; all
//! encode / animation entry points stay null.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImageGlass.SDK.Plugins;

namespace TlgCodec;

internal static unsafe class TlgCodecPlugin
{
    // ------------------------------ Static buffers ------------------------------
    private const string PluginIdString = "Plugin_TlgCodec";
    private const string PluginNameString = "KiriKiri TLG Codec";
    private const string VersionString = "1.0.0";
    private const string CodecIdString = "plugin.tlg.codec";
    private const string CodecNameString = "KiriKiri TLG";

    private static readonly string[] DecodeExtensions = [".tlg"];

    private static IGPluginApi* _pluginApi;
    private static IGCodecApi* _codecApi;
    private static IGCodecCapability* _capability;
    private static IGHostApi* _hostApi;

    private static char* _bufPluginId;
    private static char* _bufPluginName;
    private static char* _bufVersion;
    private static char* _bufCodecId;
    private static char* _bufCodecName;
    private static IGStringRef* _decExtArray;

    private static readonly object _bufLock = new();
    private static readonly System.Collections.Generic.Dictionary<nint, nint> _liveBuffers = new();

    // ------------------------------ Entry point ------------------------------
    [UnmanagedCallersOnly(EntryPoint = IGNativeAbi.ENTRY_POINT_NAME, CallConvs = [typeof(CallConvCdecl)])]
    public static IGPluginApi* GetApi(int hostAbiVersion, IGHostApi* hostApi)
    {
        if (hostAbiVersion / 1_000_000 != IGNativeAbi.IG_PLUGIN_ABI_MAJOR) return null;
        if (hostApi == null) return null;

        if (_pluginApi != null) return _pluginApi;
        _hostApi = hostApi;

        try
        {
            InitStrings();
            InitCapability();
            InitCodecApi();
            InitPluginApi();
        }
        catch
        {
            return null;
        }
        return _pluginApi;
    }

    // ------------------------------ Plugin API callbacks ------------------------------
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnInitialize() => IGStatus.OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShutdown() { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnGetCodec(int index, IGCodecApi** outCodecApi)
    {
        if (outCodecApi == null) return IGStatus.InvalidArg;
        if (index != 0) { *outCodecApi = null; return IGStatus.InvalidArg; }
        *outCodecApi = _codecApi;
        return IGStatus.OK;
    }

    // ------------------------------ Codec API callbacks ------------------------------
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecGetCapability(IGCodecCapability** outCap)
    {
        if (outCap == null) return IGStatus.InvalidArg;
        *outCap = _capability;
        return IGStatus.OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CodecCanHandleExtension(IGStringRef ext)
    {
        if (ext.Data == null || ext.Length <= 0) return 0;
        var s = new ReadOnlySpan<char>(ext.Data, ext.Length);
        foreach (var supported in DecodeExtensions)
        {
            if (s.Equals(supported, StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CodecCanHandleSignature(byte* signature, int length)
    {
        if (signature == null || length < 3) return 0;
        byte b0 = signature[0], b1 = signature[1], b2 = signature[2];
        // "TLG5.0"/"TLG6.0"/"TLG0.0", obfuscated "XXXYYY"/"XXXZZZ", "JKMXE8",
        // and the 0xAB-prefixed variant where the leading 'T' is XOR-mangled.
        if (b0 == (byte)'T' && b1 == (byte)'L' && b2 == (byte)'G') return 1;
        if (b0 == (byte)'X' && b1 == (byte)'X' && b2 == (byte)'X') return 1;
        if (b0 == (byte)'J' && b1 == (byte)'K' && b2 == (byte)'M') return 1;
        if (b0 == 0xAB && b1 == (byte)'L' && b2 == (byte)'G') return 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecLoadMetadata(IGStringRef filePath, IGImageInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;
        try
        {
            return LoadMetaInternal(filePath, cancellation, outInfo);
        }
        catch (Exception ex)
        {
            Log(4, $"TlgCodec: LoadMetadata failed. {ex}");
            return IGStatus.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecDecodeStaticRaster(IGStringRef filePath, int frameIndex, IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;
        if (frameIndex != 0) return IGStatus.InvalidArg; // single still image

        try
        {
            return DecodeStaticInternal(filePath, cancellation, outBuf);
        }
        catch (Exception ex)
        {
            Log(4, $"TlgCodec: DecodeStaticRaster failed. {ex}");
            return IGStatus.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CodecFreePixelBuffer(IGPixelBuffer* buf)
    {
        if (buf == null || buf->Data == null) return;

        try
        {
            nint key = (nint)buf->Data;
            nint pixels;
            lock (_bufLock)
            {
                // Only free buffers we allocated during decode. An ENCODE input
                // buffer (host-owned) or an already-freed pointer is refused.
                if (!_liveBuffers.Remove(key, out pixels)) return;
            }
            NativeMemory.Free((void*)pixels);
            buf->Data = null;
            buf->ReleaseContext = null;
        }
        catch { }
    }

    // ------------------------------ Metadata path ------------------------------
    private static IGStatus LoadMetaInternal(IGStringRef filePath, void* cancellation, IGImageInfo* outInfo)
    {
        if (filePath.Data == null || filePath.Length <= 0) return IGStatus.InvalidArg;
        var path = new string(filePath.Data, 0, filePath.Length);
        if (IsCanceled(cancellation)) return IGStatus.Canceled;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { Log(4, $"TlgCodec: failed to read '{path}' ({ex.Message})."); return IGStatus.IoError; }
        catch (UnauthorizedAccessException ex) { Log(4, $"TlgCodec: access denied '{path}' ({ex.Message})."); return IGStatus.IoError; }
        catch { return IGStatus.Internal; }

        var meta = TlgDecoder.ReadMetaData(bytes);
        if (meta == null)
        {
            Log(4, $"TlgCodec: '{Path.GetFileName(path)}' is not a valid TLG file.");
            return IGStatus.DecodeFailed;
        }

        FillImageInfo(outInfo, meta.Width, meta.Height, meta.BPP, bytes.Length);
        return IGStatus.OK;
    }

    // ------------------------------ Decode path ------------------------------
    private static IGStatus DecodeStaticInternal(IGStringRef filePath, void* cancellation, IGPixelBuffer* outBuf)
    {
        if (filePath.Data == null || filePath.Length <= 0) return IGStatus.InvalidArg;
        var path = new string(filePath.Data, 0, filePath.Length);
        if (IsCanceled(cancellation)) return IGStatus.Canceled;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (IOException ex) { Log(4, $"TlgCodec: failed to read '{path}' ({ex.Message})."); return IGStatus.IoError; }
        catch (UnauthorizedAccessException ex) { Log(4, $"TlgCodec: access denied '{path}' ({ex.Message})."); return IGStatus.IoError; }
        catch { return IGStatus.Internal; }

        TlgDecoded? decoded;
        try { decoded = TlgDecoder.Decode(bytes, path); }
        catch (Exception ex) { Log(4, $"TlgCodec: decode failed for '{Path.GetFileName(path)}'. {ex}"); return IGStatus.DecodeFailed; }

        if (decoded == null)
        {
            Log(4, $"TlgCodec: '{Path.GetFileName(path)}' could not be decoded (corrupt or unsupported variant).");
            return IGStatus.DecodeFailed;
        }
        if (IsCanceled(cancellation)) return IGStatus.Canceled;

        int w = decoded.Width, h = decoded.Height;
        if (w <= 0 || h <= 0) return IGStatus.DecodeFailed;

        ulong stride = (ulong)w * 4UL;
        ulong size = stride * (ulong)h;
        if (size > int.MaxValue || size > (ulong)decoded.Pixels.Length) return IGStatus.OutOfMemory;

        var pixels = (byte*)NativeMemory.Alloc((nuint)size);
        try
        {
            Unsafe.CopyBlock(pixels, Unsafe.AsPointer(ref decoded.Pixels[0]), (uint)decoded.Pixels.Length);
        }
        catch
        {
            NativeMemory.Free(pixels);
            return IGStatus.OutOfMemory;
        }

        outBuf->Data = pixels;
        outBuf->Width = w;
        outBuf->Height = h;
        outBuf->Stride = (int)stride;
        outBuf->PixelFormat = (int)IGPixelFormat.Bgra8Unorm; // TLG decodes natively to BGRA
        outBuf->ReleaseContext = pixels;

        lock (_bufLock)
        {
            _liveBuffers[(nint)pixels] = (nint)pixels;
        }
        return IGStatus.OK;
    }

    private static void FillImageInfo(IGImageInfo* info, int w, int h, int bpp, long fileSize)
    {
        info->Width = w;
        info->Height = h;
        info->PixelFormat = (int)IGPixelFormat.Bgra8Unorm;
        info->HasAlpha = bpp == 32 ? 1 : 0;
        info->HdrTransferFn = (int)IGHdrTransferFn.None;
        info->ColorSpace = (int)IGColorSpace.Srgb;
        info->Orientation = 1;
        info->FrameCount = 1;
        info->FileSizeBytes = fileSize;
        info->IccProfileData = null;
        info->IccProfileSize = 0;
    }

    // ------------------------------ Helpers ------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCanceled(void* cancellation)
    {
        if (cancellation == null || _hostApi == null || _hostApi->Core == null) return false;
        var fn = _hostApi->Core->IsCancellationRequested;
        if (fn == null) return false;
        return fn(cancellation) != 0;
    }

    private static void Log(int level, string message)
    {
        if (_hostApi == null || _hostApi->Core == null) return;
        var fn = _hostApi->Core->Log;
        if (fn == null) return;
        fixed (char* pMsg = message)
        {
            fn(level, new IGStringRef { Data = pMsg, Length = message.Length });
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IGStringRef MakeStringRef(char* data, int len) => new() { Data = data, Length = len };

    private static void InitStrings()
    {
        _bufPluginId = AllocUtf16(PluginIdString);
        _bufPluginName = AllocUtf16(PluginNameString);
        _bufVersion = AllocUtf16(VersionString);
        _bufCodecId = AllocUtf16(CodecIdString);
        _bufCodecName = AllocUtf16(CodecNameString);
        _decExtArray = AllocExtensionArray(DecodeExtensions);
    }

    private static IGStringRef* AllocExtensionArray(string[] extensions)
    {
        var array = (IGStringRef*)NativeMemory.AllocZeroed((nuint)(sizeof(IGStringRef) * extensions.Length));
        for (var i = 0; i < extensions.Length; i++)
        {
            array[i] = MakeStringRef(AllocUtf16(extensions[i]), extensions[i].Length);
        }
        return array;
    }

    private static void InitCapability()
    {
        _capability = (IGCodecCapability*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecCapability));
        _capability->StructSize = sizeof(IGCodecCapability);
        _capability->CodecId = MakeStringRef(_bufCodecId, CodecIdString.Length);
        _capability->CodecName = MakeStringRef(_bufCodecName, CodecNameString.Length);

        _capability->MetadataPriority = 200;
        _capability->DecodePriority = 200;
        _capability->EncodePriority = 0;

        _capability->SupportsMetadata = 1;
        _capability->SupportsColorProfiles = 0;
        _capability->SupportsStaticRasterDecoding = 1;
        _capability->SupportsAnimationDecoding = 0;
        _capability->SupportsStaticRasterEncoding = 0;
        _capability->SupportsMultiFrameEncoding = 0;

        _capability->DecodeExtensionCount = DecodeExtensions.Length;
        _capability->DecodeExtensions = _decExtArray;
        _capability->EncodeExtensionCount = 0;
        _capability->EncodeExtensions = null;
    }

    private static void InitCodecApi()
    {
        _codecApi = (IGCodecApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecApi));
        _codecApi->StructSize = sizeof(IGCodecApi);
        _codecApi->GetCapability = &CodecGetCapability;
        _codecApi->CanHandleExtension = &CodecCanHandleExtension;
        _codecApi->CanHandleSignature = &CodecCanHandleSignature;
        _codecApi->LoadMetadata = &CodecLoadMetadata;
        _codecApi->DecodeStaticRaster = &CodecDecodeStaticRaster;
        _codecApi->FreePixelBuffer = &CodecFreePixelBuffer;

        // Animation decode: not supported.
        _codecApi->GetAnimationInfo = null;
        _codecApi->FreeAnimationInfo = null;
        _codecApi->DecodeAnimationFrame = null;

        // Encode: not supported (no TLG encoder).
        _codecApi->EncodeStaticRaster = null;
        _codecApi->BeginEncodeMultiFrame = null;
        _codecApi->EncodeFrame = null;
        _codecApi->EndEncodeMultiFrame = null;

        // Scaled decode: optional; null lets the host fall back to full decode + downscale.
        _codecApi->DecodeStaticRasterScaled = null;
    }

    private static void InitPluginApi()
    {
        _pluginApi = (IGPluginApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGPluginApi));
        _pluginApi->StructSize = sizeof(IGPluginApi);
        _pluginApi->AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION;
        _pluginApi->Info = new IGPluginInfo
        {
            PluginId = MakeStringRef(_bufPluginId, PluginIdString.Length),
            Name = MakeStringRef(_bufPluginName, PluginNameString.Length),
            Version = MakeStringRef(_bufVersion, VersionString.Length),
            AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION,
            CodecCount = 1,
        };
        _pluginApi->GetCodec = &OnGetCodec;
        _pluginApi->Initialize = &OnInitialize;
        _pluginApi->Shutdown = &OnShutdown;
        _pluginApi->SelfTest = null;
    }

    private static char* AllocUtf16(string s)
    {
        var buf = (char*)NativeMemory.Alloc((nuint)((s.Length + 1) * sizeof(char)));
        for (var i = 0; i < s.Length; i++) buf[i] = s[i];
        buf[s.Length] = '\0';
        return buf;
    }
}