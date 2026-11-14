using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace WallpaperManager
{
    /// <summary>
    /// 壁纸比例自适应。
    ///
    /// 之前一律用系统「填充」(WallpaperStyle=10)。填充是按屏幕比例裁切的：
    /// 横图（16:9）基本无感，但竖屏图（9:16）会被放大三四倍、只露出中间一条，
    /// 用户看到的「放大后比例很奇怪、看不到全图」就是这条路径。
    ///
    /// 现在的规则：
    ///   1. 图片宽高比和屏幕差 2% 以内 —— 原图直接填充，零损失、不变画质。
    ///   2. 差得多（竖图 / 超宽图 / 方图）—— 合成一张「正好屏幕尺寸」的图：
    ///      原图完整居中(contain) 当主体，同一张图放大模糊压暗当底衬。
    ///      这样整张图都看得见，又不会留黑边，设成填充后 Windows 也不用再裁。
    /// 顺带认 EXIF 方向（手机竖拍图的像素其实是横的 + 一条旋转标记，
    /// 不认的话竖图会被当成横图，比例更是彻底乱掉）。
    /// </summary>
    public static class WallpaperFit
    {
        /// <summary>宽高比差异在 2% 以内就当「刚好是屏幕比例」</summary>
        const double RatioTolerance = 0.02;

        /// <summary>缓存最多留这么多张，超了删最旧的</summary>
        const int MaxCached = 80;

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        /// <summary>
        /// 算出「该往系统壁纸设置里传什么」。
        /// 返回真正要设置的图片路径；fitMode 是配套的 WallpaperStyle。
        /// note 非空表示做了适配，可以显示给用户看。
        /// </summary>
        public static string Prepare(string srcPath, out int fitMode, out string note)
        {
            fitMode = 10;      // 填充
            note = "";

            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return srcPath;

            int pxW, pxH, orient;
            if (!Probe(srcPath, out pxW, out pxH, out orient)) return srcPath;

            int sw, sh;
            if (!ScreenSize(out sw, out sh)) return srcPath;

            // EXIF 2/3/4 只翻转，5~8 是旋转 90°，宽高要对调才是「看上去的」尺寸
            int visW = pxW, visH = pxH;
            if (orient >= 5 && orient <= 8) { visW = pxH; visH = pxW; }

            double srcAr = (double)visW / visH;
            double scrAr = (double)sw / sh;

            bool sameRatio = Math.Abs(srcAr - scrAr) <= scrAr * RatioTolerance;
            if (sameRatio && orient <= 1)
            {
                // 天生就是这个比例，原图最清晰
                return srcPath;
            }

            string dir = CacheDir();
            string dst = Path.Combine(dir, Key(srcPath, sw, sh) + ".jpg");

            try
            {
                if (File.Exists(dst) && new FileInfo(dst).Length > 4096)
                {
                    note = visH > visW ? "竖屏图已自适应（完整显示）" : "已按屏幕比例自适应";
                    return dst;
                }
            }
            catch { }

            try
            {
                Compose(srcPath, orient, sw, sh, dst);
                Trim(dir);
                note = visH > visW ? "竖屏图已自适应（完整显示）" : "已按屏幕比例自适应";
                Log.Note("[壁纸适配] " + Path.GetFileName(srcPath) + " " + visW + "x" + visH
                         + " → " + sw + "x" + sh + "  orient=" + orient + "  输出 " + dst);
                return dst;
            }
            catch (Exception ex)
            {
                // 合成失败也不能让壁纸设不上：退回「适应」让系统缩小完整显示，
                // 最多留一圈边，总比裁掉大半张图好。
                Log.Note("[壁纸适配] 合成失败，改用「适应」： " + ex.Message);
                note = "自适应失败，已改为完整缩放";
                fitMode = 6;
                return srcPath;
            }
        }

        // ---------------- 读图信息 ----------------

        static bool Probe(string path, out int w, out int h, out int orient)
        {
            w = 0; h = 0; orient = 1;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs, true, false))
                {
                    w = img.Width;
                    h = img.Height;
                    try
                    {
                        PropertyItem[] props = img.PropertyItems;
                        for (int i = 0; i < props.Length; i++)
                        {
                            if (props[i].Id == 0x0112 && props[i].Len >= 2)
                            {
                                orient = props[i].Value[0];
                                break;
                            }
                        }
                    }
                    catch { }
                }
                return w > 0 && h > 0;
            }
            catch (Exception ex)
            {
                Log.Note("[壁纸适配] 读不出图片尺寸，按原图设置： " + ex.Message);
                return false;
            }
        }

        static bool ScreenSize(out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                System.Windows.Forms.Screen s = System.Windows.Forms.Screen.PrimaryScreen;
                if (s != null)
                {
                    w = s.Bounds.Width;
                    h = s.Bounds.Height;
                }
            }
            catch { }

            if (w <= 0 || h <= 0)
            {
                w = GetSystemMetrics(0);   // SM_CXSCREEN
                h = GetSystemMetrics(1);   // SM_CYSCREEN
            }
            return w > 0 && h > 0;
        }

        // ---------------- 合成 ----------------

        static void Compose(string srcPath, int orient, int sw, int sh, string dst)
        {
            string tmp = dst + ".tmp";
            try
            {
                using (FileStream fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image raw = Image.FromStream(fs, true, false))
                using (Bitmap src = Upright(raw, orient))
                using (Bitmap canvas = new Bitmap(sw, sh, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;

                        // 底衬：同一张图 cover 满屏 + 大半径模糊 + 压暗，
                        // 这样竖图两侧不是死黑，而是画面的延续。
                        using (Bitmap blur = BlurFill(src, sw, sh))
                        {
                            g.DrawImage(blur, new Rectangle(0, 0, sw, sh));
                        }
                        int dimAlpha = src.Height >= src.Width ? 176 : 150;
                        using (SolidBrush dim = new SolidBrush(Color.FromArgb(dimAlpha, 0, 0, 0)))
                        {
                            g.FillRectangle(dim, 0, 0, sw, sh);
                        }

                        // 主体：整张图完整居中，四周留一点点缝让底衬透出来
                        int pad = (int)Math.Round(Math.Min(sw, sh) * 0.014);
                        if (pad < 6) pad = 6;
                        Rectangle r = Contain(src.Width, src.Height, sw, sh, pad);
                        g.DrawImage(src, r);
                    }

                    SaveJpeg(canvas, tmp, 94L);
                }

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        /// <summary>
        /// 把 source 缩到 1/28 再放回整屏 —— 三趟缩放，等效一次很大半径的模糊。
        /// 只缩到 1/10 时还能认出画面里的轮廓（腿、头发），看起来像重影；
        /// 缩到 1/28 再放大，就只剩柔和的色块，才像「背景虚化」。
        /// </summary>
        static Bitmap BlurFill(Image source, int sw, int sh)
        {
            int qw = Math.Max(16, sw / 4);
            int qh = Math.Max(16, sh / 4);
            int bw = Math.Max(12, sw / 28);
            int bh = Math.Max(12, sh / 28);
            int mw = Math.Max(bw * 2, sw / 8);
            int mh = Math.Max(bh * 2, sh / 8);

            using (Bitmap quarter = new Bitmap(qw, qh, PixelFormat.Format24bppRgb))
            using (Bitmap tiny = new Bitmap(bw, bh, PixelFormat.Format24bppRgb))
            using (Bitmap mid = new Bitmap(mw, mh, PixelFormat.Format24bppRgb))
            {
                using (Graphics g1 = Graphics.FromImage(quarter))
                {
                    g1.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g1.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g1.CompositingMode = CompositingMode.SourceCopy;
                    g1.DrawImage(source, Cover(source.Width, source.Height, qw, qh));
                }
                using (Graphics g2 = Graphics.FromImage(tiny))
                {
                    g2.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g2.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    g2.CompositingMode = CompositingMode.SourceCopy;
                    g2.DrawImage(quarter, new Rectangle(0, 0, bw, bh));
                }
                using (Graphics g3 = Graphics.FromImage(mid))
                {
                    g3.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g3.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g3.CompositingMode = CompositingMode.SourceCopy;
                    g3.DrawImage(tiny, new Rectangle(0, 0, mw, mh));
                }

                Bitmap big = new Bitmap(sw, sh, PixelFormat.Format24bppRgb);
                using (Graphics gb = Graphics.FromImage(big))
                {
                    gb.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    gb.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    gb.DrawImage(mid, new Rectangle(0, 0, sw, sh));
                }
                return big;
            }
        }

        /// <summary>铺满目标框（可能溢出被裁），保持比例</summary>
        static Rectangle Cover(int iw, int ih, int tw, int th)
        {
            double s = Math.Max((double)tw / iw, (double)th / ih);
            int w = Math.Max(tw, (int)Math.Ceiling(iw * s));
            int h = Math.Max(th, (int)Math.Ceiling(ih * s));
            return new Rectangle((tw - w) / 2, (th - h) / 2, w, h);
        }

        /// <summary>装进目标框并居中（完整可见），小图允许适度放大但不能糊成一片</summary>
        static Rectangle Contain(int iw, int ih, int tw, int th, int pad)
        {
            int availW = tw - pad * 2;
            int availH = th - pad * 2;
            if (availW < 8) availW = tw;
            if (availH < 8) availH = th;

            double s = Math.Min((double)availW / iw, (double)availH / ih);
            if (s > 3.0) s = 3.0;
            int w = Math.Max(1, (int)Math.Round(iw * s));
            int h = Math.Max(1, (int)Math.Round(ih * s));
            return new Rectangle((tw - w) / 2, (th - h) / 2, w, h);
        }

        static Bitmap Upright(Image raw, int orient)
        {
            Bitmap b = new Bitmap(raw);
            switch (orient)
            {
                case 2: b.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                case 3: b.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 4: b.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                case 5: b.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                case 6: b.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 7: b.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                case 8: b.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }
            return b;
        }

        static void SaveJpeg(Bitmap bmp, string path, long quality)
        {
            ImageCodecInfo jpg = null;
            try
            {
                ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].FormatID == ImageFormat.Jpeg.Guid) { jpg = all[i]; break; }
                }
            }
            catch { }

            if (jpg == null)
            {
                bmp.Save(path, ImageFormat.Jpeg);
                return;
            }

            using (EncoderParameters p = new EncoderParameters(1))
            {
                p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bmp.Save(path, jpg, p);
            }
        }

        // ---------------- 缓存 ----------------

        public static string CacheDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WallpaperManager", "fit");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        /// <summary>算法版本。改了合成规则就要 +1，否则大家会一直命中旧缓存看不到变化</summary>
        const string AlgoVersion = "v2";

        /// <summary>路径 + 修改时间 + 屏幕尺寸 的 FNV-1a 指纹，图换了/换屏幕了缓存自然失效</summary>
        static string Key(string srcPath, int sw, int sh)
        {
            string sig = AlgoVersion + "|" + srcPath.ToLowerInvariant() + "|" + sw + "x" + sh;
            try
            {
                FileInfo fi = new FileInfo(srcPath);
                if (fi.Exists) sig += "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch { }

            ulong h = 1469598103934665603UL;
            for (int i = 0; i < sig.Length; i++)
            {
                h ^= sig[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        /// <summary>缓存目录当前占多少字节（设置里显示用）</summary>
        public static long CacheBytes()
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(CacheDir(), "*.jpg"))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>
        /// 按总大小裁剪缓存：从最旧的开始删，直到不超过 maxBytes。
        /// 和按张数裁剪（Trim）是两套口径：一张 1080p 合成图 1~2MB，张数够用；
        /// 但换到 4K 屏后单张能到 8MB，就得按体积兜底。返回删掉的文件数。
        /// </summary>
        public static int TrimTo(long maxBytes)
        {
            int removed = 0;
            try
            {
                if (maxBytes <= 0) return 0;
                string dir = CacheDir();
                string[] files = Directory.GetFiles(dir, "*.jpg");
                if (files.Length == 0) return 0;

                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });

                long total = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    try { total += new FileInfo(files[i]).Length; } catch { }
                }

                for (int i = 0; i < files.Length && total > maxBytes; i++)
                {
                    long len = 0;
                    try { len = new FileInfo(files[i]).Length; } catch { }
                    try
                    {
                        File.Delete(files[i]);
                        total -= len;
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        static void Trim(string dir)
        {            try
            {
                string[] files = Directory.GetFiles(dir, "*.jpg");
                if (files.Length <= MaxCached) return;

                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });

                int drop = files.Length - MaxCached;
                for (int i = 0; i < drop; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
    }
}
