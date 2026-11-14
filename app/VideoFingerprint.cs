using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace WallpaperManager
{
    /// <summary>
    /// 图片 / 视频「是不是同一个画面」的内容判定（11-14 深夜）。
    ///
    /// 主人原话：「不要就近认领，是否是同一张图片不止能从名字分别吧，
    ///            你要做到区分图片与视频是否为同一张图片背景」。
    /// 所以不再拿文件名 / 下单时间 / 出片时间瞎猜，直接比像素：
    ///   · 图片：WPF 解码后缩到 33x32 灰度
    ///   · 视频：复用项目自带的 MfVideo.Reader（Media Foundation）抽第一帧，同样缩到 33x32
    ///   · 指纹：z-score 归一化灰度（消亮度 / 对比度差）+ 8x8 dHash（消压缩差）
    ///   · 判定：归一化平均绝对差 MAD 和汉明距离同时低于阈值才算同一张
    ///
    /// 全程只读本机文件，零联网、零费用、零 NuGet。结果按「路径 + mtime + size」缓存，
    /// 界面每次刷新都会问一遍 HasCloudVideo，不能反复解码视频。
    /// </summary>
    public static class VideoFingerprint
    {
        const int FW = 33;      // 33 列：最后一列留给 dHash 的横向差分
        const int FH = 32;

        // 阈值由 --contenttest 拿真实库的正 / 负样本标定，见 docs/PROGRESS.md。
        // 两个条件必须同时成立：画面结构（MAD）和轮廓（dHash）都像，才算同一张。
        // 真实库标定：同一画面 MAD 0.067~0.256 / hash 6~12；
        // 不同画面 MAD >= 0.781（多数 > 0.9）/ hash >= 16。
        // 阈值取中间安全带，两边都留足余量，且要求 MAD 与 hash 同时达标。
        public static double MaxMad = 0.45;
        public static int MaxHashDist = 18;

        public sealed class Fp
        {
            public double[] Z;          // FW*FH 归一化灰度
            public ulong Hash;         // 64 bit dHash
            public string Source = "";
        }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Fp> Cache =
            new Dictionary<string, Fp>(StringComparer.OrdinalIgnoreCase);

        public static void Invalidate()
        {
            lock (Gate) { Cache.Clear(); }
        }

        static string Key(string path, string kind)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return "";
                return kind + "|" + path.ToLowerInvariant() + "|"
                     + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length;
            }
            catch { return ""; }
        }

        static Fp Remember(string k, Fp fp)
        {
            lock (Gate) { Cache[k] = fp; }
            return fp;
        }

        // ---------------- 图片 ----------------

        public static Fp Image(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string k = Key(path, "I");
            if (k.Length == 0) return null;
            lock (Gate)
            {
                Fp hit;
                if (Cache.TryGetValue(k, out hit)) return hit;
            }
            Fp fp = null;
            try { fp = MakeImage(path); }
            catch (Exception ex)
            {
                Log.Note("[指纹] 图片解码失败 " + Path.GetFileName(path) + "：" + ex.Message);
                fp = null;
            }
            return Remember(k, fp);
        }

        static Fp MakeImage(string path)
        {
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(Path.GetFullPath(path));
            bi.DecodePixelWidth = 256;              // 指纹只要缩略图，别把 4K 原图整张解出来
            bi.EndInit();
            WriteableBitmap wb = new WriteableBitmap(bi);
            int w = wb.PixelWidth, h = wb.PixelHeight;
            if (w < 8 || h < 8) return null;
            byte[] px = new byte[w * h * 4];
            wb.CopyPixels(px, w * 4, 0);
            return FromBgra(px, w, h, path);
        }

        // ---------------- 视频首帧 ----------------

        public static Fp Video(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string k = Key(path, "V");
            if (k.Length == 0) return null;
            lock (Gate)
            {
                Fp hit;
                if (Cache.TryGetValue(k, out hit)) return hit;
            }
            Fp fp = null;
            try { fp = MakeVideo(path); }
            catch (Exception ex)
            {
                Log.Note("[指纹] 视频抽帧失败 " + Path.GetFileName(path) + "：" + ex.Message);
                fp = null;
            }
            return Remember(k, fp);
        }

        static Fp MakeVideo(string path)
        {
            bool savedQuiet = MfVideo.Quiet;
            MfVideo.Quiet = true;                  // 抽帧别把 [MF] 诊断日志刷满
            try
            {
                MfVideo.Reader r = MfVideo.Reader.Open(path);
                try
                {
                    int w = r.Width, h = r.Height;
                    if (w < 8 || h < 8 || w > 8192 || h > 8192) return null;
                    int stride = w * 4;
                    int bytes = checked(stride * h);
                    IntPtr dib = Marshal.AllocHGlobal(bytes);
                    try
                    {
                        long ts;
                        if (!r.NextFrame(dib, stride, out ts)) return null;
                        byte[] px = new byte[bytes];
                        Marshal.Copy(dib, px, 0, bytes);
                        return FromBgra(px, w, h, path);
                    }
                    finally { Marshal.FreeHGlobal(dib); }
                }
                finally { r.Dispose(); }
            }
            finally { MfVideo.Quiet = savedQuiet; }
        }

        // ---------------- 指纹与距离 ----------------

        /// <summary>把紧密排列的 BGRA 帧缩成 33x32 灰度，做 z-score 归一化和 dHash。</summary>
        static Fp FromBgra(byte[] px, int w, int h, string source)
        {
            double[] g = new double[FW * FH];
            for (int y = 0; y < FH; y++)
            {
                int sy = y * h / FH;
                if (sy >= h) sy = h - 1;
                for (int x = 0; x < FW; x++)
                {
                    int sx = x * w / FW;
                    if (sx >= w) sx = w - 1;
                    int o = (sy * w + sx) * 4;
                    // BGRA 顺序：B, G, R
                    g[y * FW + x] = px[o] * 0.114 + px[o + 1] * 0.587 + px[o + 2] * 0.299;
                }
            }

            double mean = 0;
            for (int i = 0; i < g.Length; i++) mean += g[i];
            mean /= g.Length;
            double var = 0;
            for (int i = 0; i < g.Length; i++) { double d = g[i] - mean; var += d * d; }
            double sd = Math.Sqrt(var / g.Length);
            if (sd < 1.0) sd = 1.0;

            Fp fp = new Fp();
            fp.Z = new double[FW * FH];
            fp.Source = source ?? "";
            for (int i = 0; i < g.Length; i++)
            {
                double z = (g[i] - mean) / sd;
                if (z > 4.0) z = 4.0;
                if (z < -4.0) z = -4.0;
                fp.Z[i] = z;
            }

            byte[] small = new byte[9 * 8];
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 9; x++)
                {
                    double v = g[(y * FH / 8) * FW + (x * (FW - 1) / 8)];
                    if (v < 0) v = 0;
                    if (v > 255) v = 255;
                    small[y * 9 + x] = (byte)v;
                }
            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    if (small[y * 9 + x] < small[y * 9 + x + 1]) hash |= (1UL << bit);
                    bit++;
                }
            fp.Hash = hash;
            return fp;
        }

        /// <summary>返回归一化灰度平均绝对差，同时给出 dHash 汉明距离。</summary>
        public static double Compare(Fp a, Fp b, out int hashDist)
        {
            hashDist = 64;
            if (a == null || b == null || a.Z == null || b.Z == null) return double.MaxValue;
            double sum = 0;
            int n = a.Z.Length < b.Z.Length ? a.Z.Length : b.Z.Length;
            for (int i = 0; i < n; i++) sum += Math.Abs(a.Z[i] - b.Z[i]);
            double mad = sum / n;
            ulong x = a.Hash ^ b.Hash;
            int d = 0;
            while (x != 0) { d += (int)(x & 1UL); x >>= 1; }
            hashDist = d;
            return mad;
        }

        public static bool Same(double mad, int hashDist)
        {
            return mad <= MaxMad && hashDist <= MaxHashDist;
        }

        public static string PairText(Fp a, Fp b)
        {
            int hd;
            double mad = Compare(a, b, out hd);
            if (mad == double.MaxValue) return "指纹不可用";
            return "MAD=" + mad.ToString("0.000") + " hash=" + hd
                 + (Same(mad, hd) ? " → 同一画面" : " → 不同画面");
        }
    }
}
