//! KiriKiri TLG5/TLG6 decoder, ported from GameRes (morkt) to pure System APIs.
//! Original: Copyright (C) 2000-2005 W.Dee <dee@kikyou.info> and contributors
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace TlgCodec;

internal sealed class TlgMetaData
{
    public int Version;
    public int DataOffset;
    public int Width;
    public int Height;
    public int BPP;
    public string FileName = string.Empty;
    public int OffsetX;
    public int OffsetY;
}

internal sealed class TlgDecoded
{
    public int Width;
    public int Height;
    public int BPP;
    public byte[] Pixels = Array.Empty<byte>(); // BGRA, stride = Width*4
}

/// <summary>TLG5/6 decoder. All methods are static and side-effect free.</summary>
internal static class TlgDecoder
{
    // Safety cap: reject absurd dimensions before allocating (1G pixels ≈ 4 GiB BGRA).
    private const long MaxPixels = 0x10000000;

    public static TlgMetaData? ReadMetaData(byte[] data)
    {
        if (data.Length < 0x26) return null;

        var h = new byte[0x26];
        Buffer.BlockCopy(data, 0, h, 0, 0x26);

        int offset = 0xf;
        if (!AsciiEqual(h, 0, "TLG0.0\x00sds\x1a"))
            offset = 0;
        if (!AsciiEqual(h, offset + 6, "\x00raw\x1a"))
            return null;
        if (0xAB == h[offset])
            h[offset] = (byte)'T';

        int version;
        if (AsciiEqual(h, offset, "TLG6.0")) version = 6;
        else if (AsciiEqual(h, offset, "TLG5.0")) version = 5;
        else if (AsciiEqual(h, offset, "XXXYYY"))
        {
            version = 5;
            h[offset + 0x0C] ^= 0xAB;
            h[offset + 0x10] ^= 0xAC;
        }
        else if (AsciiEqual(h, offset, "XXXZZZ"))
        {
            version = 6;
            h[offset + 0x0F] ^= 0xAB;
            h[offset + 0x13] ^= 0xAC;
        }
        else if (AsciiEqual(h, offset, "JKMXE8"))
        {
            version = 5;
            h[offset + 0x0C] ^= 0x1A;
            h[offset + 0x10] ^= 0x1C;
        }
        else return null;

        int colors = h[offset + 11];
        if (6 == version)
        {
            if (1 != colors && 4 != colors && 3 != colors) return null;
            if (h[offset + 12] != 0 || h[offset + 13] != 0 || h[offset + 14] != 0) return null;
            offset += 15;
        }
        else
        {
            if (4 != colors && 3 != colors) return null;
            offset += 12;
        }

        return new TlgMetaData
        {
            Width = ToInt32(h, offset),
            Height = ToInt32(h, offset + 4),
            BPP = colors * 8,
            Version = version,
            DataOffset = offset + 8,
        };
    }

    /// <summary>Decode a whole TLG file. Returns null when the file is not TLG
    /// or when a "tags" delta/blend applies and the base image is unavailable.</summary>
    public static TlgDecoded? Decode(byte[] data, string filePath)
    {
        var meta = ReadMetaData(data);
        if (meta == null) return null;
        meta.FileName = filePath;

        if (meta.Width <= 0 || meta.Height <= 0 ||
            (long)meta.Width * meta.Height > MaxPixels) return null;

        var reader = new ByteReader(data);
        byte[] image;
        try { image = ReadTlg(reader, meta); }
        catch { return null; }

        // Optional KiriKiri "tags" delta feature: blend over a base image from disk.
        int tail_size = (int)Math.Min(reader.Length - reader.Position, 512);
        if (tail_size > 8)
        {
            try
            {
                var tail = reader.ReadBytes(tail_size);
                var blended = ApplyTags(image, meta, tail);
                if (blended != null) return blended;
            }
            catch { /* tags are best-effort; fall back to the raw image */ }
        }

        return new TlgDecoded { Width = meta.Width, Height = meta.Height, BPP = meta.BPP, Pixels = image };
    }

    // ============================== ByteReader ==============================

    private sealed class ByteReader
    {
        private readonly byte[] _d;
        private int _p;
        public ByteReader(byte[] data) { _d = data; }
        public int Position { get => _p; set => _p = value; }
        public long Length => _d.Length;
        public long Remaining => _d.Length - _p;

        public byte ReadUInt8() => _d[_p++];
        public int ReadInt32()
        {
            int v = _d[_p] | (_d[_p + 1] << 8) | (_d[_p + 2] << 16) | (_d[_p + 3] << 24);
            _p += 4;
            return v;
        }
        public byte[] ReadBytes(int count)
        {
            if (count < 0 || count > Remaining) throw new EndOfStreamException();
            var b = new byte[count];
            Buffer.BlockCopy(_d, _p, b, 0, count);
            _p += count;
            return b;
        }
        public void Read(byte[] buffer, int offset, int count)
        {
            if (count < 0 || count > Remaining) throw new EndOfStreamException();
            Buffer.BlockCopy(_d, _p, buffer, offset, count);
            _p += count;
        }
        public void Seek(int offset, SeekOrigin origin)
        {
            _p = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _p + offset,
                _ => _d.Length + offset,
            };
        }
    }

    // ============================== top-level ==============================

    private static byte[] ReadTlg(ByteReader src, TlgMetaData info)
    {
        src.Position = info.DataOffset;
        return 6 == info.Version ? ReadV6(src, info) : ReadV5(src, info);
    }

    // ============================== TLG6 ==============================

    private const int TVP_TLG6_H_BLOCK_SIZE = 8;
    private const int TVP_TLG6_W_BLOCK_SIZE = 8;
    private const int TVP_TLG6_GOLOMB_N_COUNT = 4;
    private const int TVP_TLG6_LeadingZeroTable_BITS = 12;
    private const int TVP_TLG6_LeadingZeroTable_SIZE = 1 << TVP_TLG6_LeadingZeroTable_BITS;

    private static byte[] ReadV6(ByteReader src, TlgMetaData info)
    {
        int width = info.Width, height = info.Height;
        int colors = info.BPP / 8;
        int max_bit_length = src.ReadInt32();

        int x_block_count = ((width - 1) / TVP_TLG6_W_BLOCK_SIZE) + 1;
        int y_block_count = ((height - 1) / TVP_TLG6_H_BLOCK_SIZE) + 1;
        int main_count = width / TVP_TLG6_W_BLOCK_SIZE;
        int fraction = width - main_count * TVP_TLG6_W_BLOCK_SIZE;

        var image_bits = new uint[height * width];
        var bit_pool = new byte[max_bit_length / 8 + 5];
        var pixelbuf = new uint[width * TVP_TLG6_H_BLOCK_SIZE + 1];
        var filter_types = new byte[x_block_count * y_block_count];
        var zeroline = new uint[width];
        var LZSS_text = new byte[4096];

        uint zerocolor = 3 == colors ? 0xff000000 : 0x00000000;
        for (int i = 0; i < width; ++i) zeroline[i] = zerocolor;

        uint[] prevline = zeroline;
        int prevline_index = 0;

        int p = 0;
        for (uint i = 0; i < 32 * 0x01010101; i += 0x01010101)
        {
            for (uint j = 0; j < 16 * 0x01010101; j += 0x01010101)
            {
                LZSS_text[p++] = (byte)(i & 0xff);
                LZSS_text[p++] = (byte)((i >> 8) & 0xff);
                LZSS_text[p++] = (byte)((i >> 16) & 0xff);
                LZSS_text[p++] = (byte)((i >> 24) & 0xff);
                LZSS_text[p++] = (byte)(j & 0xff);
                LZSS_text[p++] = (byte)((j >> 8) & 0xff);
                LZSS_text[p++] = (byte)((j >> 16) & 0xff);
                LZSS_text[p++] = (byte)((j >> 24) & 0xff);
            }
        }

        int inbuf_size = src.ReadInt32();
        if (inbuf_size < 0 || inbuf_size > src.Remaining) return null!;
        byte[] inbuf = src.ReadBytes(inbuf_size);
        TVPTLG5DecompressSlide(filter_types, inbuf, inbuf_size, LZSS_text, 0);

        for (int y = 0; y < height; y += TVP_TLG6_H_BLOCK_SIZE)
        {
            int ylim = y + TVP_TLG6_H_BLOCK_SIZE;
            if (ylim >= height) ylim = height;
            int pixel_count = (ylim - y) * width;

            for (int c = 0; c < colors; c++)
            {
                int bit_length = src.ReadInt32();
                int method = (bit_length >> 30) & 3;
                bit_length &= 0x3fffffff;
                int byte_length = bit_length / 8;
                if (0 != (bit_length % 8)) byte_length++;
                if (byte_length > bit_pool.Length || byte_length > src.Remaining) return null!;
                src.Read(bit_pool, 0, byte_length);

                switch (method)
                {
                    case 0:
                        if (c == 0 && colors != 1)
                            TVPTLG6DecodeGolombValuesForFirst(pixelbuf, pixel_count, bit_pool);
                        else
                            TVPTLG6DecodeGolombValues(pixelbuf, c * 8, pixel_count, bit_pool);
                        break;
                    default:
                        return null!; // entropy method not implemented
                }
            }

            int ft = (y / TVP_TLG6_H_BLOCK_SIZE) * x_block_count;
            int skipbytes = (ylim - y) * TVP_TLG6_W_BLOCK_SIZE;

            for (int yy = y; yy < ylim; yy++)
            {
                int curline = yy * width;
                int dir = (yy & 1) ^ 1;
                int oddskip = ((ylim - yy - 1) - (yy - y));
                if (0 != main_count)
                {
                    int start = ((width < TVP_TLG6_W_BLOCK_SIZE) ? width : TVP_TLG6_W_BLOCK_SIZE) * (yy - y);
                    TVPTLG6DecodeLineGeneric(prevline, prevline_index, image_bits, curline,
                        width, 0, main_count, filter_types, ft, skipbytes, pixelbuf, start,
                        zerocolor, oddskip, dir);
                }
                if (main_count != x_block_count)
                {
                    int ww = fraction;
                    if (ww > TVP_TLG6_W_BLOCK_SIZE) ww = TVP_TLG6_W_BLOCK_SIZE;
                    int start = ww * (yy - y);
                    TVPTLG6DecodeLineGeneric(prevline, prevline_index, image_bits, curline,
                        width, main_count, x_block_count, filter_types, ft, skipbytes, pixelbuf, start,
                        zerocolor, oddskip, dir);
                }
                prevline = image_bits;
                prevline_index = curline;
            }
        }

        int stride = width * 4;
        var pixels = new byte[height * stride];
        Buffer.BlockCopy(image_bits, 0, pixels, 0, pixels.Length);
        return pixels;
    }

    private static void TVPTLG6DecodeLineGeneric(uint[] prevline, int prevline_index,
        uint[] curline, int curline_index, int width, int start_block, int block_limit,
        byte[] filtertypes, int filtertypes_index, int skipblockbytes,
        uint[] inbuf, int inbuf_index, uint initialp, int oddskip, int dir)
    {
        uint p, up;

        if (0 != start_block)
        {
            prevline_index += start_block * TVP_TLG6_W_BLOCK_SIZE;
            curline_index += start_block * TVP_TLG6_W_BLOCK_SIZE;
            p = curline[curline_index - 1];
            up = prevline[prevline_index - 1];
        }
        else
        {
            p = up = initialp;
        }

        inbuf_index += skipblockbytes * start_block;
        int step = 0 != (dir & 1) ? 1 : -1;

        for (int i = start_block; i < block_limit; i++)
        {
            int w = width - i * TVP_TLG6_W_BLOCK_SIZE;
            if (w > TVP_TLG6_W_BLOCK_SIZE) w = TVP_TLG6_W_BLOCK_SIZE;
            int ww = w;
            if (step == -1) inbuf_index += ww - 1;
            if (0 != (i & 1)) inbuf_index += oddskip * ww;

            int ftype = filtertypes[filtertypes_index + i];
            do
            {
                uint u = prevline[prevline_index];
                uint vv = TransformV(ftype, inbuf[inbuf_index]);
                p = 0 == (ftype & 1) ? tvp_med(p, u, up, vv) : tvp_avg(p, u, up, vv);
                up = u;
                curline[curline_index] = p;
                curline_index++;
                prevline_index++;
                inbuf_index += step;
            } while (0 != --w);

            if (step == 1) inbuf_index += skipblockbytes - ww;
            else inbuf_index += skipblockbytes + 1;
            if (0 != (i & 1)) inbuf_index -= oddskip * ww;
        }
    }

    /// <summary>Computes the per-filter-type transformed residual for the MED/AVG predictors.
    /// Even types use MED, odd types use AVG (identical v' math).</summary>
    private static uint TransformV(int type, uint v)
    {
        switch (type)
        {
            case 0: case 1: return v;
            case 2: case 3: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + ((v >> 8) & 0xff)) << 16)) + (((v >> 8) & 0xff) << 8) + (0xff & ((v & 0xff) + ((v >> 8) & 0xff))) + (v & 0xff000000));
            case 4: case 5: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff) + ((v >> 8) & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + (v & 0xff)) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            case 6: case 7: return (uint)((0xff0000 & (((v >> 16) & 0xff) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 16) & 0xff) + ((v >> 8) & 0xff))) + (v & 0xff000000));
            case 8: case 9: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff) + ((v >> 16) & 0xff) + ((v >> 8) & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + (v & 0xff) + ((v >> 16) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 16) & 0xff))) + (v & 0xff000000));
            case 10: case 11: return (uint)((0xff0000 & (((v >> 16) & 0xff) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + (v & 0xff) + ((v >> 16) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 16) & 0xff))) + (v & 0xff000000));
            case 12: case 13: return (uint)((0xff0000 & (((v >> 16) & 0xff) << 16)) + (0xff00 & (((v >> 8) & 0xff) << 8)) + (0xff & ((v & 0xff) + ((v >> 8) & 0xff))) + (v & 0xff000000));
            case 14: case 15: return (uint)((0xff0000 & (((v >> 16) & 0xff) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + (v & 0xff)) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            case 16: case 17: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + ((v >> 8) & 0xff)) << 16)) + (0xff00 & (((v >> 8) & 0xff) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            case 18: case 19: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff) + (v & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 8) & 0xff) + ((v >> 16) & 0xff) + (v & 0xff))) + (v & 0xff000000));
            case 20: case 21: return (uint)((0xff0000 & (((v >> 16) & 0xff) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 16) & 0xff))) + (v & 0xff000000));
            case 22: case 23: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + (v & 0xff)) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            case 24: case 25: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff) + (v & 0xff)) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            case 26: case 27: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff) + ((v >> 8) & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff) + (v & 0xff) + ((v >> 8) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 8) & 0xff))) + (v & 0xff000000));
            case 28: case 29: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + (v & 0xff) + ((v >> 8) & 0xff) + ((v >> 16) & 0xff)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v >> 16) & 0xff)) << 8)) + (0xff & ((v & 0xff) + ((v >> 8) & 0xff) + ((v >> 16) & 0xff))) + (v & 0xff000000));
            case 30: case 31: return (uint)((0xff0000 & ((((v >> 16) & 0xff) + ((v & 0xff) << 1)) << 16)) + (0xff00 & ((((v >> 8) & 0xff) + ((v & 0xff) << 1)) << 8)) + (0xff & (v & 0xff)) + (v & 0xff000000));
            default: return v;
        }
    }

    private static void TVPTLG6DecodeGolombValuesForFirst(uint[] pixelbuf, int pixel_count, byte[] bit_pool)
    {
        int bit_pool_index = 0;
        int n = TVP_TLG6_GOLOMB_N_COUNT - 1;
        int a = 0;
        int bit_pos = 1;
        bool zero = 0 == (bit_pool[bit_pool_index] & 1);

        for (int pixel = 0; pixel < pixel_count;)
        {
            int count;
            {
                uint t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                int b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                int bit_count = b;
                while (0 == b)
                {
                    bit_count += TVP_TLG6_LeadingZeroTable_BITS;
                    bit_pos += TVP_TLG6_LeadingZeroTable_BITS;
                    bit_pool_index += bit_pos >> 3;
                    bit_pos &= 7;
                    t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                    b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                    bit_count += b;
                }
                bit_pos += b;
                bit_pool_index += bit_pos >> 3;
                bit_pos &= 7;

                bit_count--;
                count = 1 << bit_count;
                count += ((ToInt32(bit_pool, bit_pool_index) >> bit_pos) & (count - 1));

                bit_pos += bit_count;
                bit_pool_index += bit_pos >> 3;
                bit_pos &= 7;
            }
            if (zero)
            {
                do { pixelbuf[pixel++] = 0; } while (0 != --count);
                zero = !zero;
            }
            else
            {
                do
                {
                    int k = TVP_Tables.TVPTLG6GolombBitLengthTable[a, n];
                    int v, sign;

                    uint t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                    int bit_count, b;
                    if (0 != t)
                    {
                        b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                        bit_count = b;
                        while (0 == b)
                        {
                            bit_count += TVP_TLG6_LeadingZeroTable_BITS;
                            bit_pos += TVP_TLG6_LeadingZeroTable_BITS;
                            bit_pool_index += bit_pos >> 3;
                            bit_pos &= 7;
                            t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                            b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                            bit_count += b;
                        }
                        bit_count--;
                    }
                    else
                    {
                        bit_pool_index += 5;
                        bit_count = bit_pool[bit_pool_index - 1];
                        bit_pos = 0;
                        t = ToUInt32(bit_pool, bit_pool_index);
                        b = 0;
                    }

                    v = (int)((bit_count << k) + ((t >> b) & ((1 << k) - 1)));
                    sign = (v & 1) - 1;
                    v >>= 1;
                    a += v;
                    pixelbuf[pixel++] = (byte)((v ^ sign) + sign + 1);

                    bit_pos += b;
                    bit_pos += k;
                    bit_pool_index += bit_pos >> 3;
                    bit_pos &= 7;

                    if (--n < 0)
                    {
                        a >>= 1;
                        n = TVP_TLG6_GOLOMB_N_COUNT - 1;
                    }
                } while (0 != --count);
                zero = !zero;
            }
        }
    }

    private static void TVPTLG6DecodeGolombValues(uint[] pixelbuf, int offset, int pixel_count, byte[] bit_pool)
    {
        uint mask = (uint)~(0xff << offset);
        int bit_pool_index = 0;
        int n = TVP_TLG6_GOLOMB_N_COUNT - 1;
        int a = 0;
        int bit_pos = 1;
        bool zero = 0 == (bit_pool[bit_pool_index] & 1);

        for (int pixel = 0; pixel < pixel_count;)
        {
            int count;
            {
                uint t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                int b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                int bit_count = b;
                while (0 == b)
                {
                    bit_count += TVP_TLG6_LeadingZeroTable_BITS;
                    bit_pos += TVP_TLG6_LeadingZeroTable_BITS;
                    bit_pool_index += bit_pos >> 3;
                    bit_pos &= 7;
                    t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                    b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                    bit_count += b;
                }
                bit_pos += b;
                bit_pool_index += bit_pos >> 3;
                bit_pos &= 7;

                bit_count--;
                count = 1 << bit_count;
                count += (int)((ToUInt32(bit_pool, bit_pool_index) >> bit_pos) & (count - 1));

                bit_pos += bit_count;
                bit_pool_index += bit_pos >> 3;
                bit_pos &= 7;
            }
            if (zero)
            {
                do { pixelbuf[pixel++] &= mask; } while (0 != --count);
                zero = !zero;
            }
            else
            {
                do
                {
                    int k = TVP_Tables.TVPTLG6GolombBitLengthTable[a, n];
                    int v, sign;

                    uint t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                    int bit_count, b;
                    if (0 != t)
                    {
                        b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                        bit_count = b;
                        while (0 == b)
                        {
                            bit_count += TVP_TLG6_LeadingZeroTable_BITS;
                            bit_pos += TVP_TLG6_LeadingZeroTable_BITS;
                            bit_pool_index += bit_pos >> 3;
                            bit_pos &= 7;
                            t = ToUInt32(bit_pool, bit_pool_index) >> bit_pos;
                            b = TVP_Tables.TVPTLG6LeadingZeroTable[t & (TVP_TLG6_LeadingZeroTable_SIZE - 1)];
                            bit_count += b;
                        }
                        bit_count--;
                    }
                    else
                    {
                        bit_pool_index += 5;
                        bit_count = bit_pool[bit_pool_index - 1];
                        bit_pos = 0;
                        t = ToUInt32(bit_pool, bit_pool_index);
                        b = 0;
                    }

                    v = (int)((bit_count << k) + ((t >> b) & ((1 << k) - 1)));
                    sign = (v & 1) - 1;
                    v >>= 1;
                    a += v;
                    uint c = (uint)((pixelbuf[pixel] & mask) | (uint)((byte)((v ^ sign) + sign + 1) << offset));
                    pixelbuf[pixel++] = c;

                    bit_pos += b;
                    bit_pos += k;
                    bit_pool_index += bit_pos >> 3;
                    bit_pos &= 7;

                    if (--n < 0)
                    {
                        a >>= 1;
                        n = TVP_TLG6_GOLOMB_N_COUNT - 1;
                    }
                } while (0 != --count);
                zero = !zero;
            }
        }
    }

    private static class TVP_Tables
    {
        public static readonly byte[] TVPTLG6LeadingZeroTable = new byte[TVP_TLG6_LeadingZeroTable_SIZE];
        public static readonly sbyte[,] TVPTLG6GolombBitLengthTable =
            new sbyte[TVP_TLG6_GOLOMB_N_COUNT * 2 * 128, TVP_TLG6_GOLOMB_N_COUNT];

        private static readonly short[,] TVPTLG6GolombCompressed = new short[TVP_TLG6_GOLOMB_N_COUNT, 9]
        {
            { 3, 7, 15, 27, 63, 108, 223, 448, 130 },
            { 3, 5, 13, 24, 51, 95, 192, 384, 257 },
            { 2, 5, 12, 21, 39, 86, 155, 320, 384 },
            { 2, 3, 9, 18, 33, 61, 129, 258, 511 },
        };

        static TVP_Tables()
        {
            for (int i = 0; i < TVP_TLG6_LeadingZeroTable_SIZE; i++)
            {
                int cnt = 0;
                int j;
                for (j = 1; j != TVP_TLG6_LeadingZeroTable_SIZE && 0 == (i & j); j <<= 1, cnt++) ;
                cnt++;
                if (j == TVP_TLG6_LeadingZeroTable_SIZE) cnt = 0;
                TVPTLG6LeadingZeroTable[i] = (byte)cnt;
            }

            for (int n = 0; n < TVP_TLG6_GOLOMB_N_COUNT; n++)
            {
                int a = 0;
                for (int i = 0; i < 9; i++)
                {
                    for (int j = 0; j < TVPTLG6GolombCompressed[n, i]; j++)
                        TVPTLG6GolombBitLengthTable[a++, n] = (sbyte)i;
                }
                if (a != TVP_TLG6_GOLOMB_N_COUNT * 2 * 128)
                    throw new InvalidOperationException("Invalid table data initialization");
            }
        }
    }

    // ============================== TLG5 ==============================

    private static byte[] ReadV5(ByteReader src, TlgMetaData info)
    {
        int width = info.Width, height = info.Height;
        int colors = info.BPP / 8;
        int blockheight = src.ReadInt32();
        if (blockheight <= 0 || blockheight > height) return null!;
        int blockcount = (height - 1) / blockheight + 1;

        if (src.Remaining < blockcount * 4L) return null!;
        src.Seek(blockcount * 4, SeekOrigin.Current);

        int stride = width * 4;
        var image_bits = new byte[height * stride];
        var text = new byte[4096];
        var inbuf = new byte[blockheight * width + 10];
        var outbuf = new byte[colors][];
        for (int i = 0; i < colors; i++)
            outbuf[i] = new byte[blockheight * width + 10];

        int z = 0;
        int prevline = -1;
        for (int y_blk = 0; y_blk < height; y_blk += blockheight)
        {
            for (int c = 0; c < colors; c++)
            {
                byte mark = src.ReadUInt8();
                int size = src.ReadInt32();
                if (size < 0 || size > inbuf.Length || size > src.Remaining) return null!;
                if (mark == 0)
                {
                    byte[] comp = src.ReadBytes(size);
                    z = TVPTLG5DecompressSlide(outbuf[c], comp, size, text, z);
                }
                else
                {
                    src.Read(outbuf[c], 0, size);
                }
            }

            int y_lim = y_blk + blockheight;
            if (y_lim > height) y_lim = height;
            int outbuf_pos = 0;
            for (int y = y_blk; y < y_lim; y++)
            {
                int current = y * stride;
                int current_org = current;
                if (prevline >= 0)
                {
                    if (3 == colors)
                        TVPTLG5ComposeColors3To4(image_bits, current, prevline, outbuf, outbuf_pos, width);
                    else
                        TVPTLG5ComposeColors4To4(image_bits, current, prevline, outbuf, outbuf_pos, width);
                }
                else
                {
                    if (3 == colors)
                    {
                        for (int pr = 0, pg = 0, pb = 0, x = 0; x < width; x++)
                        {
                            int b = outbuf[0][outbuf_pos + x];
                            int g = outbuf[1][outbuf_pos + x];
                            int r = outbuf[2][outbuf_pos + x];
                            b += g; r += g;
                            image_bits[current++] = (byte)(pb += b);
                            image_bits[current++] = (byte)(pg += g);
                            image_bits[current++] = (byte)(pr += r);
                            image_bits[current++] = 0xff;
                        }
                    }
                    else
                    {
                        for (int pr = 0, pg = 0, pb = 0, pa = 0, x = 0; x < width; x++)
                        {
                            int b = outbuf[0][outbuf_pos + x];
                            int g = outbuf[1][outbuf_pos + x];
                            int r = outbuf[2][outbuf_pos + x];
                            int a = outbuf[3][outbuf_pos + x];
                            b += g; r += g;
                            image_bits[current++] = (byte)(pb += b);
                            image_bits[current++] = (byte)(pg += g);
                            image_bits[current++] = (byte)(pr += r);
                            image_bits[current++] = (byte)(pa += a);
                        }
                    }
                }
                outbuf_pos += width;
                prevline = current_org;
            }
        }
        return image_bits;
    }

    private static void TVPTLG5ComposeColors3To4(byte[] outp, int outp_index, int upper,
        byte[][] buf, int bufpos, int width)
    {
        byte pc0 = 0, pc1 = 0, pc2 = 0;
        byte c0, c1, c2;
        for (int x = 0; x < width; x++)
        {
            c0 = buf[0][bufpos + x];
            c1 = buf[1][bufpos + x];
            c2 = buf[2][bufpos + x];
            c0 += c1; c2 += c1;
            outp[outp_index++] = (byte)(((pc0 += c0) + outp[upper + 0]) & 0xff);
            outp[outp_index++] = (byte)(((pc1 += c1) + outp[upper + 1]) & 0xff);
            outp[outp_index++] = (byte)(((pc2 += c2) + outp[upper + 2]) & 0xff);
            outp[outp_index++] = 0xff;
            upper += 4;
        }
    }

    private static void TVPTLG5ComposeColors4To4(byte[] outp, int outp_index, int upper,
        byte[][] buf, int bufpos, int width)
    {
        byte pc0 = 0, pc1 = 0, pc2 = 0, pc3 = 0;
        byte c0, c1, c2, c3;
        for (int x = 0; x < width; x++)
        {
            c0 = buf[0][bufpos + x];
            c1 = buf[1][bufpos + x];
            c2 = buf[2][bufpos + x];
            c3 = buf[3][bufpos + x];
            c0 += c1; c2 += c1;
            outp[outp_index++] = (byte)(((pc0 += c0) + outp[upper + 0]) & 0xff);
            outp[outp_index++] = (byte)(((pc1 += c1) + outp[upper + 1]) & 0xff);
            outp[outp_index++] = (byte)(((pc2 += c2) + outp[upper + 2]) & 0xff);
            outp[outp_index++] = (byte)(((pc3 += c3) + outp[upper + 3]) & 0xff);
            upper += 4;
        }
    }

    private static int TVPTLG5DecompressSlide(byte[] outbuf, byte[] inbuf, int inbuf_size,
        byte[] text, int initialr)
    {
        int r = initialr;
        uint flags = 0;
        int o = 0;
        for (int i = 0; i < inbuf_size;)
        {
            if (((flags >>= 1) & 256) == 0)
                flags = (uint)(inbuf[i++] | 0xff00);
            if (0 != (flags & 1))
            {
                int mpos = inbuf[i] | ((inbuf[i + 1] & 0xf) << 8);
                int mlen = (inbuf[i + 1] & 0xf0) >> 4;
                i += 2;
                mlen += 3;
                if (mlen == 18) mlen += inbuf[i++];

                while (0 != mlen--)
                {
                    outbuf[o++] = text[r++] = text[mpos++];
                    mpos &= (4096 - 1);
                    r &= (4096 - 1);
                }
            }
            else
            {
                byte c = inbuf[i++];
                outbuf[o++] = c;
                text[r++] = c;
                r &= (4096 - 1);
            }
        }
        return r;
    }

    // ============================== packed-byte predictors ==============================

    private static uint tvp_make_gt_mask(uint a, uint b)
    {
        uint tmp2 = ~b;
        uint tmp = ((a & tmp2) + (((a ^ tmp2) >> 1) & 0x7f7f7f7f)) & 0x80808080;
        tmp = ((tmp >> 7) + 0x7f7f7f7f) ^ 0x7f7f7f7f;
        return tmp;
    }

    private static uint tvp_packed_bytes_add(uint a, uint b)
    {
        uint tmp = (uint)((((a & b) << 1) + ((a ^ b) & 0xfefefefe)) & 0x01010100);
        return a + b - tmp;
    }

    private static uint tvp_med2(uint a, uint b, uint c)
    {
        uint aa_gt_bb = tvp_make_gt_mask(a, b);
        uint a_xor_b_and_aa_gt_bb = (a ^ b) & aa_gt_bb;
        uint aa = a_xor_b_and_aa_gt_bb ^ a;
        uint bb = a_xor_b_and_aa_gt_bb ^ b;
        uint n = tvp_make_gt_mask(c, bb);
        uint nn = tvp_make_gt_mask(aa, c);
        uint m = ~(n | nn);
        return (n & aa) | (nn & bb) | ((bb & m) - (c & m) + (aa & m));
    }

    private static uint tvp_med(uint a, uint b, uint c, uint v)
        => tvp_packed_bytes_add(tvp_med2(a, b, c), v);

    private static uint tvp_avg(uint a, uint b, uint c, uint v)
        => tvp_packed_bytes_add((((a & b) + (((a ^ b) & 0xfefefefe) >> 1)) + ((a ^ b) & 0x01010101)), v);

    // ============================== "tags" delta/blend ==============================

    private static TlgDecoded? ApplyTags(byte[] image, TlgMetaData meta, byte[] tail)
    {
        int i = tail.Length - 8;
        while (i >= 0)
        {
            if ('s' == tail[i + 3] && 'g' == tail[i + 2] && 'a' == tail[i + 1] && 't' == tail[i])
                break;
            --i;
        }
        if (i < 0) return null;

        var tags = new TagsParser(tail, i + 4);
        if (!tags.Parse()) return null;

        var base_name = tags.GetString(1);
        meta.OffsetX = tags.GetInt(2) & 0xFFFF;
        meta.OffsetY = tags.GetInt(3) & 0xFFFF;
        if (string.IsNullOrEmpty(base_name)) return null;

        int method = 1;
        if (tags.HasKey(4)) method = tags.GetInt(4);

        base_name = Path.Combine(Path.GetDirectoryName(meta.FileName) ?? string.Empty, base_name);
        if (base_name == meta.FileName) return null;

        byte[] baseFile;
        try { baseFile = File.ReadAllBytes(base_name); }
        catch { return null; } // base image missing → skip blending

        var base_info = ReadMetaData(baseFile);
        if (base_info == null) return null;
        base_info.FileName = base_name;
        byte[] base_image = ReadTlg(new ByteReader(baseFile), base_info);
        var pixels = BlendImage(base_image, base_info, image, meta, method);

        return new TlgDecoded { Width = base_info.Width, Height = base_info.Height, BPP = base_info.BPP, Pixels = pixels };
    }

    private static byte[] BlendImage(byte[] base_image, TlgMetaData base_info,
        byte[] overlay, TlgMetaData overlay_info, int method)
    {
        int dst_stride = base_info.Width * 4;
        int src_stride = overlay_info.Width * 4;
        int dst = overlay_info.OffsetY * dst_stride + overlay_info.OffsetX * 4;
        int src = 0;
        int gap = dst_stride - src_stride;
        for (int y = 0; y < overlay_info.Height; ++y)
        {
            for (int x = 0; x < overlay_info.Width; ++x)
            {
                byte src_alpha = overlay[src + 3];
                if (2 == method)
                {
                    base_image[dst] ^= overlay[src];
                    base_image[dst + 1] ^= overlay[src + 1];
                    base_image[dst + 2] ^= overlay[src + 2];
                    base_image[dst + 3] ^= src_alpha;
                }
                else if (src_alpha != 0)
                {
                    if (0xFF == src_alpha || 0 == base_image[dst + 3])
                    {
                        base_image[dst] = overlay[src];
                        base_image[dst + 1] = overlay[src + 1];
                        base_image[dst + 2] = overlay[src + 2];
                        base_image[dst + 3] = src_alpha;
                    }
                    else
                    {
                        base_image[dst + 0] = (byte)((overlay[src + 0] * src_alpha
                            + base_image[dst + 0] * (0xFF - src_alpha)) / 0xFF);
                        base_image[dst + 1] = (byte)((overlay[src + 1] * src_alpha
                            + base_image[dst + 1] * (0xFF - src_alpha)) / 0xFF);
                        base_image[dst + 2] = (byte)((overlay[src + 2] * src_alpha
                            + base_image[dst + 2] * (0xFF - src_alpha)) / 0xFF);
                        base_image[dst + 3] = (byte)Math.Max(src_alpha, base_image[dst + 3]);
                    }
                }
                dst += 4;
                src += 4;
            }
            dst += gap;
        }
        return base_image;
    }

    private sealed class TagsParser
    {
        private readonly byte[] _tags;
        private readonly Dictionary<int, (int pos, int len)> _map = new();
        private int _offset;

        public TagsParser(byte[] tags, int offset) { _tags = tags; _offset = offset; }

        public bool Parse()
        {
            int length = ToInt32(_tags, _offset);
            _offset += 4;
            if (length <= 0 || length > _tags.Length - _offset) return false;
            while (_offset < _tags.Length)
            {
                int key_len = ParseInt();
                if (key_len < 0) return false;
                int key;
                switch (key_len)
                {
                    case 1: key = _tags[_offset]; break;
                    case 2: key = ToUInt16(_tags, _offset); break;
                    case 4: key = ToInt32(_tags, _offset); break;
                    default: return false;
                }
                _offset += key_len + 1;
                int value_len = ParseInt();
                if (value_len < 0) return false;
                _map[key] = (_offset, value_len);
                _offset += value_len + 1;
            }
            return _map.Count > 0;
        }

        private int ParseInt()
        {
            int colon = Array.IndexOf(_tags, (byte)':', _offset);
            if (-1 == colon) return -1;
            var len_str = Encoding.ASCII.GetString(_tags, _offset, colon - _offset);
            _offset = colon + 1;
            return int.Parse(len_str);
        }

        public bool HasKey(int key) => _map.ContainsKey(key);

        public int GetInt(int key)
        {
            var val = _map[key];
            switch (val.len)
            {
                case 0: return 0;
                case 1: return _tags[val.pos];
                case 2: return ToUInt16(_tags, val.pos);
                case 4: return ToInt32(_tags, val.pos);
                default: throw new FormatException("bad tag value length");
            }
        }

        public string GetString(int key)
        {
            var val = _map[key];
            return Cp932.GetString(_tags, val.pos, val.len);
        }
    }

    private static class Cp932
    {
        private static readonly Encoding? _enc = Create();

        private static Encoding? Create()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932);
            }
            catch { return null; }
        }

        public static string GetString(byte[] data, int index, int count)
        {
            if (_enc != null) return _enc.GetString(data, index, count);
            return Encoding.Latin1.GetString(data, index, count);
        }
    }

    // ============================== byte helpers ==============================

    private static bool AsciiEqual(byte[] data, int offset, string s)
    {
        if (offset < 0 || offset + s.Length > data.Length) return false;
        for (int i = 0; i < s.Length; i++)
            if (data[offset + i] != (byte)s[i]) return false;
        return true;
    }

    private static int ToInt32(byte[] b, int i)
        => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);

    private static uint ToUInt32(byte[] b, int i)
        => (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

    private static int ToUInt16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
}