# ImageGlass KiriKiri TLG Codec Plugin

Native **decode-only** codec plugin for ImageGlass v10 that adds `.tlg` support, ported from the GameRes C# TLG5/6 decoder (W.Dee / morkt).

| Extension | Read | Write |
| --------- | ---- | ----- |
| `.tlg` | ✅ TLG5 & TLG6, all variants | ❌ |

Supports: 24/32bpp images, 8bpp (1-plane) TLG6, XOR-obfuscated headers
(`XXXYYY`, `XXXZZZ`, `JKMXE8`, `0xAB`-prefixed), and the KiriKiri `tags`
delta/blend feature when the referenced base image exists next to the file.

No SkiaSharp or any third-party dependency — the decoder is pure C#, so the
package stays tiny and fully cross-platform.