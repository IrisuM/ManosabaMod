using System;
using System.Collections.Generic;

namespace ManosabaLoader.Utils
{
    /// <summary>
    /// 从 alpha 遮罩生成贴合轮廓的 sprite 网格（轴对齐矩形分解）。
    ///
    /// 为什么需要它：Sprite.Create 在运行时生成的 Tight 网格很粗糙——轮廓外留有 6–20 px 的余量，
    /// 并用直线跨过细小凹陷（例如荆棘之间的缝隙）。原版 Glasses_0Fix 着色器完全不读贴图 alpha
    /// （扫过之后 alpha 恒为 1），玻璃带的形状完全由网格裁出，所以余量区域会以贴图透明像素的
    /// RGB 原样显示出来（白底 → 灰边）。原版资源靠编辑器导入生成的精细网格（余量 3–5 px）
    /// 加上外扩 12 px 的边缘色来掩盖这一点；mod 贴图没有这两样。
    ///
    /// 做法：以 alpha ≥ threshold 为"实心"，按 block×block 像素块扫描，每行的连续实心块合并成
    /// 一段，相邻行 x 范围相同的段再纵向合并成矩形，每个矩形 4 顶点 2 三角形。block 从 1 起，
    /// 直到顶点数不超过 maxVertices（ushort 索引上限 65535）。block=1 时裁切精确到像素。
    /// 顶点坐标为 sprite rect 像素坐标（y 向上，原点左下），可直接交给 Sprite.OverrideGeometry。
    /// 三角形绕向与原版 sprite 网格一致（y 向上坐标系下顺时针）。
    /// </summary>
    internal static class SpriteMeshBuilder
    {
        public sealed class MeshData
        {
            /// <summary>x0,y0,x1,y1,… 像素坐标，y 向上。</summary>
            public float[] Vertices;
            public ushort[] Triangles;
            public int RectCount;
            public int BlockSize;
        }

        private static readonly int[] BlockSizes = { 1, 2, 4, 8, 16, 32 };

        /// <param name="alpha">行优先 alpha，第 0 行 = 顶部（PngAlphaReader 的输出）。</param>
        public static MeshData Build(byte[] alpha, int width, int height, byte threshold, int maxVertices = 60000)
        {
            if (alpha == null || width <= 0 || height <= 0 || alpha.Length < width * height) return null;
            foreach (int block in BlockSizes)
            {
                var rects = BuildRects(alpha, width, height, threshold, block);
                if (rects.Count == 0) return null; // 全透明：交给调用方决定
                if (rects.Count * 4 <= maxVertices) return ToMesh(rects, width, height, block);
            }
            return null;
        }

        private struct Rect { public int C0, C1, R0, R1; } // 块坐标，闭区间

        private static List<Rect> BuildRects(byte[] alpha, int width, int height, byte threshold, int block)
        {
            int cols = (width + block - 1) / block;
            int rows = (height + block - 1) / block;
            var result = new List<Rect>();
            var open = new Dictionary<long, int>();      // key(c0,c1) → index into openRects
            var openRects = new List<Rect>();
            var rowOn = new bool[cols];

            for (int r = 0; r < rows; r++)
            {
                int y0 = r * block, y1 = Math.Min(height, y0 + block);
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * block, x1 = Math.Min(width, x0 + block);
                    bool on = false;
                    for (int y = y0; y < y1 && !on; y++)
                    {
                        int rowBase = y * width;
                        for (int x = x0; x < x1; x++)
                            if (alpha[rowBase + x] >= threshold) { on = true; break; }
                    }
                    rowOn[c] = on;
                }

                var nextOpen = new Dictionary<long, int>();
                var nextOpenRects = new List<Rect>();
                int cStart = -1;
                for (int c = 0; c <= cols; c++)
                {
                    bool on = c < cols && rowOn[c];
                    if (on && cStart < 0) cStart = c;
                    if (!on && cStart >= 0)
                    {
                        int c0 = cStart, c1 = c - 1;
                        cStart = -1;
                        long key = ((long)c0 << 32) | (uint)c1;
                        if (open.TryGetValue(key, out int idx))
                        {
                            var rect = openRects[idx];
                            rect.R1 = r;
                            openRects[idx] = rect;
                            nextOpen[key] = nextOpenRects.Count;
                            nextOpenRects.Add(rect);
                            openRects[idx] = new Rect { C0 = -1 }; // 标记为已迁移
                        }
                        else
                        {
                            nextOpen[key] = nextOpenRects.Count;
                            nextOpenRects.Add(new Rect { C0 = c0, C1 = c1, R0 = r, R1 = r });
                        }
                    }
                }
                // 未延续到本行的矩形关闭
                foreach (var rect in openRects) if (rect.C0 >= 0) result.Add(rect);
                open = nextOpen;
                openRects = nextOpenRects;
            }
            foreach (var rect in openRects) if (rect.C0 >= 0) result.Add(rect);
            return result;
        }

        private static MeshData ToMesh(List<Rect> rects, int width, int height, int block)
        {
            var verts = new float[rects.Count * 8];
            var tris = new ushort[rects.Count * 6];
            for (int i = 0; i < rects.Count; i++)
            {
                var rc = rects[i];
                float x0 = rc.C0 * block;
                float x1 = Math.Min(width, (rc.C1 + 1) * block);
                float yTop = rc.R0 * block;                          // 从顶部数
                float yBottom = Math.Min(height, (rc.R1 + 1) * block);
                float yU0 = height - yBottom;                        // y 向上
                float yU1 = height - yTop;

                int v = i * 4, o = i * 8;
                verts[o + 0] = x0; verts[o + 1] = yU0; // 0 = 左下
                verts[o + 2] = x1; verts[o + 3] = yU0; // 1 = 右下
                verts[o + 4] = x1; verts[o + 5] = yU1; // 2 = 右上
                verts[o + 6] = x0; verts[o + 7] = yU1; // 3 = 左上

                int t = i * 6; // 顺时针（y 向上）：左下 → 左上 → 右上，左下 → 右上 → 右下
                tris[t + 0] = (ushort)(v + 0); tris[t + 1] = (ushort)(v + 3); tris[t + 2] = (ushort)(v + 2);
                tris[t + 3] = (ushort)(v + 0); tris[t + 4] = (ushort)(v + 2); tris[t + 5] = (ushort)(v + 1);
            }
            return new MeshData { Vertices = verts, Triangles = tris, RectCount = rects.Count, BlockSize = block };
        }
    }
}
