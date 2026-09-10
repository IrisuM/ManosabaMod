using System;
using System.IO;
using System.IO.Compression;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 纯托管的 PNG alpha 通道读取器（不经过 Unity）。
    ///
    /// 为什么需要它：mod 贴图通过 ImageConversion.LoadImage 交给 Unity 后，再读回像素
    /// 需要 GetPixels32 / GetRawTextureData 这类返回数组的 wrapper，而这些 wrapper 在本游戏的
    /// IL2CPP interop 下不可靠（见 ModObjectionCutInLoader 顶部注释与项目记忆）。
    /// 我们本来就持有 PNG 原始字节，直接在托管侧解出 alpha 即可。
    ///
    /// 支持：非交错 PNG，颜色类型 0/2/3/4/6，位深 1/2/4/8/16（16 位取高字节）。
    /// 交错（Adam7）或格式异常 → 返回 null，调用方回退到 Unity 自动网格。
    /// 输出的 Alpha 数组按行优先、第 0 行为图片顶部。
    /// </summary>
    internal static class PngAlphaReader
    {
        public sealed class AlphaMask
        {
            public int Width;
            public int Height;
            /// <summary>Width*Height 字节，行优先，第 0 行 = 顶部。</summary>
            public byte[] Alpha;
        }

        private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static AlphaMask TryRead(byte[] png, out string error)
        {
            error = null;
            try
            {
                if (png == null || png.Length < 33) { error = "too short"; return null; }
                for (int i = 0; i < 8; i++) if (png[i] != Signature[i]) { error = "not a PNG"; return null; }

                int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
                byte[] palette = null, trns = null;
                var idat = new MemoryStream();
                int pos = 8;
                bool sawIhdr = false;
                while (pos + 8 <= png.Length)
                {
                    int len = ReadBE32(png, pos);
                    string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                    int dataStart = pos + 8;
                    if (len < 0 || dataStart + len + 4 > png.Length) { error = "truncated chunk " + type; return null; }
                    switch (type)
                    {
                        case "IHDR":
                            width = ReadBE32(png, dataStart);
                            height = ReadBE32(png, dataStart + 4);
                            bitDepth = png[dataStart + 8];
                            colorType = png[dataStart + 9];
                            interlace = png[dataStart + 12];
                            sawIhdr = true;
                            break;
                        case "PLTE":
                            palette = new byte[len];
                            Buffer.BlockCopy(png, dataStart, palette, 0, len);
                            break;
                        case "tRNS":
                            trns = new byte[len];
                            Buffer.BlockCopy(png, dataStart, trns, 0, len);
                            break;
                        case "IDAT":
                            idat.Write(png, dataStart, len);
                            break;
                    }
                    if (type == "IEND") break;
                    pos = dataStart + len + 4; // + CRC
                }

                if (!sawIhdr) { error = "no IHDR"; return null; }
                if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024) { error = "bad size"; return null; }
                if (interlace != 0) { error = "interlaced PNG not supported"; return null; }

                int channels;
                switch (colorType)
                {
                    case 0: channels = 1; break; // gray
                    case 2: channels = 3; break; // rgb
                    case 3: channels = 1; break; // palette
                    case 4: channels = 2; break; // gray + alpha
                    case 6: channels = 4; break; // rgba
                    default: error = "color type " + colorType; return null;
                }
                if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8 && bitDepth != 16) { error = "bit depth " + bitDepth; return null; }

                int bitsPerPixel = channels * bitDepth;
                int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
                int stride = (width * bitsPerPixel + 7) / 8;

                // zlib stream: 2-byte header, then deflate
                idat.Position = 0;
                if (idat.Length < 2) { error = "no IDAT"; return null; }
                idat.Position = 2;
                var raw = new byte[(stride + 1) * height];
                using (var inflater = new DeflateStream(idat, CompressionMode.Decompress, leaveOpen: true))
                {
                    int read = 0;
                    while (read < raw.Length)
                    {
                        int n = inflater.Read(raw, read, raw.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < raw.Length) { error = "IDAT too short"; return null; }
                }

                // unfilter in place
                var prev = new byte[stride];
                var cur = new byte[stride];
                var alpha = new byte[width * height];
                bool hasTrnsPalette = colorType == 3 && trns != null;
                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * (stride + 1);
                    int filter = raw[rowStart];
                    Buffer.BlockCopy(raw, rowStart + 1, cur, 0, stride);
                    switch (filter)
                    {
                        case 0: break;
                        case 1:
                            for (int i = bytesPerPixel; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bytesPerPixel]);
                            break;
                        case 2:
                            for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]);
                            break;
                        case 3:
                            for (int i = 0; i < stride; i++)
                            {
                                int left = i >= bytesPerPixel ? cur[i - bytesPerPixel] : 0;
                                cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                            }
                            break;
                        case 4:
                            for (int i = 0; i < stride; i++)
                            {
                                int a = i >= bytesPerPixel ? cur[i - bytesPerPixel] : 0;
                                int b = prev[i];
                                int c = i >= bytesPerPixel ? prev[i - bytesPerPixel] : 0;
                                int p = a + b - c;
                                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                                int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                                cur[i] = (byte)(cur[i] + pred);
                            }
                            break;
                        default:
                            error = "bad filter " + filter; return null;
                    }

                    int o = y * width;
                    switch (colorType)
                    {
                        case 6: // RGBA
                            if (bitDepth == 8) for (int x = 0; x < width; x++) alpha[o + x] = cur[x * 4 + 3];
                            else for (int x = 0; x < width; x++) alpha[o + x] = cur[x * 8 + 6];
                            break;
                        case 4: // GA
                            if (bitDepth == 8) for (int x = 0; x < width; x++) alpha[o + x] = cur[x * 2 + 1];
                            else for (int x = 0; x < width; x++) alpha[o + x] = cur[x * 4 + 2];
                            break;
                        case 3: // palette (+ optional tRNS)
                            for (int x = 0; x < width; x++)
                            {
                                int idx = ReadSample(cur, x, bitDepth);
                                alpha[o + x] = hasTrnsPalette && idx < trns.Length ? trns[idx] : (byte)255;
                            }
                            break;
                        default: // gray / rgb without alpha
                            for (int x = 0; x < width; x++) alpha[o + x] = 255;
                            break;
                    }

                    var tmp = prev; prev = cur; cur = tmp;
                }

                return new AlphaMask { Width = width, Height = height, Alpha = alpha };
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static int ReadBE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static int ReadSample(byte[] row, int x, int bitDepth)
        {
            switch (bitDepth)
            {
                case 8: return row[x];
                case 4: return (row[x >> 1] >> ((1 - (x & 1)) * 4)) & 0xF;
                case 2: return (row[x >> 2] >> ((3 - (x & 3)) * 2)) & 0x3;
                case 1: return (row[x >> 3] >> (7 - (x & 7))) & 0x1;
                default: return row[x * 2]; // 16-bit palette index does not exist; defensive
            }
        }
    }
}
