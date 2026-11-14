using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace WallpaperManager
{
    /// <summary>
    /// 按缩略图估一个「色系」，给筛选条用（冷 / 暖 / 暗 / 亮）。
    ///
    /// 为什么不用 index.json 存：它是**观感属性**，同一张图重新下封面就该重算，
    /// 存进索引反而会出现「图换了颜色没变」的怪事。这里按 路径+修改时间 缓存，
    /// 图一变缓存自然失效，也不用改索引格式。
    ///
    /// 只缩到 12x12 取平均：壁纸的主色靠的是大片区域，不需要逐像素统计，
    /// 12x12 已经足够稳，而且 144 次采样对界面完全不构成负担。
    /// </summary>
    public static class ColorTag
    {
        public const string All = "all";
        public const string Cool = "cool";
        public const string Warm = "warm";
        public const string Dark = "dark";
        public const string Bright = "bright";

        static readonly Dictionary<string, string> Cache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Cn(string key)
        {
            if (key == Cool) return "冷色";
            if (key == Warm) return "暖色";
            if (key == Dark) return "暗色";
            if (key == Bright) return "亮色";
            return "全部";
        }

        /// <summary>取一张图的色系。读不出来就返回 all（等于不参与筛选，不会把图藏起来）</summary>
        public static string Of(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return All;

            string key = path;
            try { key += "|" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch { }

            lock (Cache)
            {
                string hit;
                if (Cache.TryGetValue(key, out hit)) return hit;
            }

            string tag = Compute(path);

            lock (Cache)
            {
                if (Cache.Count > 4000) Cache.Clear();
                Cache[key] = tag;
            }
            return tag;
        }

        static string Compute(string path)
        {
            try
            {
                double r = 0, g = 0, b = 0;
                int n = 0;

                using (Bitmap small = new Bitmap(12, 12))
                {
                    using (Graphics gfx = Graphics.FromImage(small))
                    {
                        gfx.InterpolationMode = InterpolationMode.Bilinear;
                        gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        using (Image src = Image.FromFile(path))
                            gfx.DrawImage(src, 0, 0, 12, 12);
                    }

                    for (int y = 0; y < 12; y++)
                    {
                        for (int x = 0; x < 12; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            r += c.R; g += c.G; b += c.B;
                            n++;
                        }
                    }
                }
                if (n == 0) return All;

                double rr = r / n / 255.0, gg = g / n / 255.0, bb = b / n / 255.0;
                double max = Math.Max(rr, Math.Max(gg, bb));
                double min = Math.Min(rr, Math.Min(gg, bb));
                double l = (max + min) / 2.0;
                double s = (max - min) / (1.0 - Math.Abs(2 * l - 1) + 1e-9);
                if (s > 1) s = 1;

                double h = 0;
                if (max - min > 1e-6)
                {
                    if (max == rr) h = 60 * (((gg - bb) / (max - min)) % 6);
                    else if (max == gg) h = 60 * (((bb - rr) / (max - min)) + 2);
                    else h = 60 * (((rr - gg) / (max - min)) + 4);
                    if (h < 0) h += 360;
                }

                // 先看明暗，再看冷暖：一张深蓝的夜景，主人会先觉得它「暗」
                if (l < 0.26) return Dark;
                if (l > 0.74 && s < 0.45) return Bright;
                if (s < 0.12) return l < 0.5 ? Dark : Bright;
                return (h > 90 && h < 270) ? Cool : Warm;
            }
            catch { return All; }
        }
    }
}
