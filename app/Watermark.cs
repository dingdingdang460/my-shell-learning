using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WallpaperManager
{
    /// <summary>
    /// 「AI生成」合规角标的抽帧检测与局部修补（11-16，对应的仍是 11-15 主人拍板的口径）。
    ///
    /// 背景：视频生成平台按《人工智能生成合成内容标识办法》会在画面左上角硬打角标，
    /// 提示词去不掉 —— 所以规则是「先生成，出片后抽帧检查，再 delogo 修补干净才交付」。
    /// 之前这一步只写在给 WorkBuddy 会话的指令里，程序自己不验；主人换机器、
    /// 会话没照做，带角标的片子就直接进库了。现在把它做成程序能力。
    ///
    /// 全部依赖本机已有的 ffmpeg（主人指定：引用、不随包分发，也绝不下载）。
    /// 找不到 ffmpeg 时**明确报告「未验收」，不假装干净**，见 VerifyAndRepair 的返回值。
    ///
    /// 判定依据（用真实库 24 个视频标定，见 docs/PROGRESS.md 第二十二节）：
    /// 角标是一个圆角矩形描边，几何位置和尺寸相对帧大小固定。所以不比对模板、
    /// 也不猜颜色，只找它唯一的结构特征 —— **上下两条等长的长横线**：
    ///   · 逐行求「高于局部背景 T 个灰阶」的最长连续段长度；
    ///   · 门槛 L = 0.035 × 帧宽（1344 宽 → 47px）；
    ///   · 存在两行都 ≥ L，且行距落在 0.035H~0.070H 之间 → 认定角标。
    /// 实测：4 个真带角标的片子全部命中（含一张对背景几乎同色的浅色角标），
    /// 20 个干净片子（含 1080P 素材、半修补残迹）零误报。
    /// </summary>
    public static class Watermark
    {
        // ---- 角标几何：相对帧大小的比例（1344×768 实测 x10..91 / y10..47 → 82×38）----
        const double Rx = 0.0074;
        const double Ry = 0.0130;
        const double Rw = 0.0612;
        const double Rh = 0.0495;
        const int Pad = 4;                 // 四周留白，delogo 要覆盖描边外侧的过渡像素

        const int DiffThreshold = 8;       // 高于局部背景多少灰阶算「笔画」
        const int BlurK = 15;              // 局部背景：15×15 均值
        const double LineFactor = 0.035;   // 长横线门槛 = 0.035 × 帧宽
        const double GapLo = 0.035;        // 两行行距下限 = 0.035 × 帧高
        const double GapHi = 0.070;        // 上限
        const double MinPercent = 2.5;     // 角标区域里「笔画像素」至少占 2.5%

        // ---------------------------------------------------------------- ffmpeg

        /// <summary>设置里手填的 ffmpeg 路径；空 = 自动发现。由 App 启动时从 config 灌进来。</summary>
        public static string CustomPath = "";

        static string cached;
        static bool cacheTried;

        public static void ResetFfmpegCache()
        {
            cached = null;
            cacheTried = false;
        }

        /// <summary>
        /// 找 ffmpeg：手填 → 程序目录 → PATH → WorkBuddy 自带 Python 的 imageio-ffmpeg。
        /// 只认真的能跑起来的（`-version` 退出码 0），不认「文件存在但坏了」。
        /// </summary>
        public static string Resolve(out string why)
        {
            if (cacheTried)
            {
                why = cached == null ? (foundedWhy.Length > 0 ? foundedWhy : "没找到 ffmpeg") : "";
                return cached;
            }
            cacheTried = true;
            foundedWhy = "";
            cached = null;

            List<string> cand = new List<string>();
            if (!string.IsNullOrEmpty(CustomPath)) cand.Add(CustomPath.Trim());
            try
            {
                string self = Path.GetDirectoryName(typeof(Watermark).Assembly.Location);
                if (!string.IsNullOrEmpty(self))
                    cand.Add(Path.Combine(self, "ffmpeg.exe"));
            }
            catch { }

            // PATH（cmd 的 where 比手工拆 PATH 更靠谱，能处理 PATHEXT）
            try
            {
                string o, e;
                if (RunCapture("cmd.exe", "/c where ffmpeg 2>nul", 4000, out o, out e))
                {
                    string[] lines = o.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0) cand.Add(lines[0].Trim());
                }
            }
            catch { }

            // WorkBuddy 自带的 Python 里装的 imageio-ffmpeg（本机实测就在这儿）
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string root = Path.Combine(home, @".workbuddy\binaries\python\versions");
                if (Directory.Exists(root))
                {
                    string[] vers = Directory.GetDirectories(root);
                    Array.Sort(vers);
                    Array.Reverse(vers);                    // 新版本优先
                    for (int i = 0; i < vers.Length; i++)
                    {
                        string bin = Path.Combine(vers[i],
                            @"Lib\site-packages\imageio_ffmpeg\binaries");
                        if (!Directory.Exists(bin)) continue;
                        string[] exes = Directory.GetFiles(bin, "ffmpeg*.exe");
                        Array.Sort(exes);
                        Array.Reverse(exes);
                        for (int j = 0; j < exes.Length; j++) cand.Add(exes[j]);
                    }
                }
            }
            catch { }

            for (int i = 0; i < cand.Count; i++)
            {
                string p = cand[i];
                if (string.IsNullOrEmpty(p)) continue;
                try { if (!File.Exists(p)) continue; }
                catch { continue; }
                if (SmokeTest(p)) { cached = p; why = ""; return cached; }
            }

            foundedWhy = cand.Count == 0
                ? "没找到 ffmpeg：设置里填一下 ffmpeg.exe 的完整路径"
                : "发现的 ffmpeg 都跑不起来（" + cand.Count + " 个候选）";
            why = foundedWhy;
            return null;
        }

        static string foundedWhy = "";

        static bool SmokeTest(string exe)
        {
            try
            {
                string o, e;
                if (!RunCapture(exe, "-hide_banner -version", 6000, out o, out e)) return false;
                return (o + e).IndexOf("ffmpeg version", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public static bool Available(out string why)
        {
            string p = Resolve(out why);
            return p != null;
        }

        // ---------------------------------------------------------------- 进程

        /// <summary>跑一个进程把输出读干净；超时就杀掉。返回是否 0 退出。</summary>
        static bool RunCapture(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            // out 参数不能进 lambda（C# 不允许捕获 ref/out），所以先落本地变量，最后统一赋值
            string so = "", se = "";
            bool ok = false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    // 必须异步读：两边都不等对方，否则管道填满就死锁
                    System.Threading.Thread th = new System.Threading.Thread(delegate ()
                    {
                        try { so = p.StandardOutput.ReadToEnd(); } catch { }
                    });
                    th.IsBackground = true;
                    th.Start();
                    try { se = p.StandardError.ReadToEnd(); } catch { }
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        ok = false;
                    }
                    else
                    {
                        try { th.Join(2000); } catch { }
                        ok = p.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                se = ex.Message;
                ok = false;
            }
            stdout = so;
            stderr = se;
            return ok;
        }

        // ---------------------------------------------------------------- 帧尺寸

        public static bool ProbeSize(string mp4, out int w, out int h, out string err)
        {
            w = 0; h = 0; err = "";
            if (string.IsNullOrEmpty(mp4) || !File.Exists(mp4)) { err = "文件不存在"; return false; }
            string why;
            string ff = Resolve(out why);
            if (ff == null) { err = why; return false; }

            string o, e;
            // 不写输出文件名，ffmpeg 会把输入信息打到 stderr 然后以「没输出」报错 —— 这正好
            RunCapture(ff, "-hide_banner -i \"" + mp4 + "\"", 15000, out o, out e);
            Match m = Regex.Match(e, @"Video:.*?,\s*(\d{2,5})x(\d{2,5})");
            if (!m.Success) { err = "读不出分辨率：" + FirstLine(e); return false; }
            w = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            h = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (w <= 0 || h <= 0) { err = "分辨率异常 " + w + "x" + h; return false; }
            return true;
        }

        static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "（无输出）";
            int i = s.IndexOfAny(new char[] { '\r', '\n' });
            string one = i >= 0 ? s.Substring(0, i) : s;
            return one.Length > 160 ? one.Substring(0, 160) : one;
        }

        /// <summary>角标检测框（手工给帧尺寸即可算，纯函数）。</summary>
        public static void Rect(int frameW, int frameH, out int x, out int y, out int w, out int h)
        {
            x = (int)Math.Round(frameW * Rx) - Pad;
            y = (int)Math.Round(frameH * Ry) - Pad;
            w = (int)Math.Round(frameW * Rw) + Pad * 2;
            h = (int)Math.Round(frameH * Rh) + Pad * 2;
            if (x < 2) x = 2;
            if (y < 2) y = 2;
            if (x + w > frameW - 2) w = frameW - 2 - x;
            if (y + h > frameH - 2) h = frameH - 2 - y;
            if (w < 8) w = 8;
            if (h < 8) h = 8;
        }

        // ---------------------------------------------------------------- 检测

        public sealed class Badge
        {
            public int FrameW, FrameH;
            public int X, Y, W, H;
            public double Percent;      // 笔画像素占比
            public int MaxRun;          // 最长的横向连续段
            public int LineThreshold;   // 长横线门槛 L
            public List<int> Lines = new List<int>();
            public bool Pair;           // 上下两条长横线同时存在（角标的结构特征）

            public bool Found { get { return Pair && Percent >= MinPercent; } }

            public string Text()
            {
                if (Found) return "检出 AI 角标（" + X + "," + Y + " " + W + "x" + H + "，笔画 "
                                + Percent.ToString("0.0", CultureInfo.InvariantCulture) + "%，长线 "
                                + MaxRun + "px）";
                return "未见 AI 角标（笔画 " + Percent.ToString("0.0", CultureInfo.InvariantCulture)
                     + "%，长线 " + MaxRun + "px）";
            }
        }

        /// <summary>
        /// 从一帧灰度像素里判定有没有角标。**纯函数**，自检用合成图直接断言，
        /// 不碰 ffmpeg、不读真实视频。gray 是 frameW×frameH 或角标框大小的灰度。
        /// </summary>
        public static Badge Analyze(int frameW, int frameH, double[,] grayInRect)
        {
            Badge b = new Badge();
            b.FrameW = frameW; b.FrameH = frameH;
            int x, y, w, h;
            Rect(frameW, frameH, out x, out y, out w, out h);
            b.X = x; b.Y = y; b.W = w; b.H = h;
            if (grayInRect == null) return b;

            int gh = grayInRect.GetLength(0), gw = grayInRect.GetLength(1);
            double[,] bg = Blur(grayInRect, BlurK);

            bool[,] mask = new bool[gh, gw];
            int hits = 0;
            for (int yy = 0; yy < gh; yy++)
                for (int xx = 0; xx < gw; xx++)
                {
                    if (grayInRect[yy, xx] - bg[yy, xx] > DiffThreshold) { mask[yy, xx] = true; hits++; }
                }
            b.Percent = gw * gh == 0 ? 0 : 100.0 * hits / (double)(gw * gh);

            int L = (int)Math.Round(frameW * LineFactor);
            if (L < 8) L = 8;
            b.LineThreshold = L;

            int best = 0;
            for (int yy = 0; yy < gh; yy++)
            {
                int run = 0, rowBest = 0;
                for (int xx = 0; xx < gw; xx++)
                {
                    if (mask[yy, xx]) { run++; if (run > rowBest) rowBest = run; }
                    else run = 0;
                }
                if (rowBest > best) best = rowBest;
                if (rowBest >= L) b.Lines.Add(yy);
            }
            b.MaxRun = best;

            int lo = (int)Math.Round(frameH * GapLo);
            int hi = (int)Math.Round(frameH * GapHi);
            for (int i = 0; i < b.Lines.Count && !b.Pair; i++)
                for (int j = i + 1; j < b.Lines.Count; j++)
                {
                    int d = b.Lines[j] - b.Lines[i];
                    if (d >= lo && d <= hi) { b.Pair = true; break; }
                }
            return b;
        }

        static double[,] Blur(double[,] a, int k)
        {
            int h = a.GetLength(0), w = a.GetLength(1), p = k / 2;
            double[,] r = new double[h, w];
            double inv = 1.0 / (k * k);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int dy = -p; dy <= p; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0) yy = 0; else if (yy >= h) yy = h - 1;
                        for (int dx = -p; dx <= p; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0) xx = 0; else if (xx >= w) xx = w - 1;
                            s += a[yy, xx];
                        }
                    }
                    r[y, x] = s * inv;
                }
            return r;
        }

        // ---------------------------------------------------------------- 测量真实视频

        /// <summary>
        /// 抽一帧量角标。atSec 默认 1 秒；片子比 1 秒短就退到 0 秒。
        /// 失败返回 null 并给出原因（调用方要如实显示，不能当成「干净」）。
        /// </summary>
        public static Badge Measure(string mp4, double atSec, out string err)
        {
            err = "";
            int fw, fh;
            if (!ProbeSize(mp4, out fw, out fh, out err)) return null;

            int x, y, w, h;
            Rect(fw, fh, out x, out y, out w, out h);

            double[,] g = GrabGray(mp4, atSec, x, y, w, h, out err);
            if (g == null && atSec > 0.01)
            {
                err = "";
                g = GrabGray(mp4, 0.0, x, y, w, h, out err);
            }
            if (g == null) return null;
            return Analyze(fw, fh, g);
        }

        static double[,] GrabGray(string mp4, double atSec, int x, int y, int w, int h, out string err)
        {
            err = "";
            string why;
            string ff = Resolve(out why);
            if (ff == null) { err = why; return null; }

            string tmp = Path.Combine(Path.GetTempPath(),
                "wmframe-" + Guid.NewGuid().ToString("N") + ".raw");
            try
            {
                string at = atSec.ToString("0.###", CultureInfo.InvariantCulture);
                string vf = "crop=" + w + ":" + h + ":" + x + ":" + y + ",format=gray";
                string args = "-hide_banner -loglevel error -y -ss " + at + " -i \"" + mp4 + "\""
                            + " -frames:v 1 -vf \"" + vf + "\" -f rawvideo \"" + tmp + "\"";
                string o, e;
                RunCapture(ff, args, 30000, out o, out e);
                if (!File.Exists(tmp)) { err = "抽帧失败：" + FirstLine(e); return null; }
                byte[] buf = File.ReadAllBytes(tmp);
                if (buf.Length < w * h) { err = "抽帧尺寸不足：" + buf.Length + " < " + (w * h); return null; }

                double[,] g = new double[h, w];
                for (int yy = 0; yy < h; yy++)
                    for (int xx = 0; xx < w; xx++)
                        g[yy, xx] = buf[yy * w + xx];
                return g;
            }
            catch (Exception ex) { err = "抽帧异常：" + ex.Message; return null; }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        // ---------------------------------------------------------------- 修补

        public sealed class Outcome
        {
            public bool Checked;        // 到底有没有真验过（没 ffmpeg 时是 false）
            public bool Clean;          // 最终交付的这一份是干净的
            public bool Repaired;       // 中途动过手（有角标且修好了）
            public string Msg = "";
            public Badge Before, After;
        }

        /// <summary>
        /// 出片后的验收：量角标 → 有就 delogo 修一遍 → 再量 → 修干净才算过。
        /// 就地改 mp4，原文件留 &lt;id&gt;.mp4.bak-wm 备份（和既有 .bak-* 自愈机制一致）。
        ///
        /// 返回值语义（调用方必须照着说，不许含糊）：
        ///   Checked=false → 本机没 ffmpeg，**没验**；片子留着但状态里要写明。
        ///   Clean=true    → 验过，干净（或者修干净了）。
        ///   Clean=false   → 有角标且没修干净，调用方**不许入库**。
        /// </summary>
        public static Outcome VerifyAndRepair(string mp4)
        {
            Outcome r = new Outcome();
            string why;
            string ff = Resolve(out why);
            if (ff == null)
            {
                r.Checked = false;
                r.Clean = false;
                r.Msg = "没找到 ffmpeg，这条的 AI 角标**没验收**：" + why;
                return r;
            }

            string err;
            Badge before = Measure(mp4, 1.0, out err);
            if (before == null)
            {
                r.Checked = false;
                r.Clean = false;
                r.Msg = "读不出画面，角标没验收：" + err;
                return r;
            }

            r.Checked = true;
            r.Before = before;
            if (!before.Found)
            {
                r.Clean = true;
                r.Msg = before.Text() + "，不用处理";
                return r;
            }

            string tmp = mp4 + ".wm-tmp.mp4";
            try
            {
                string vf = "delogo=x=" + before.X + ":y=" + before.Y
                          + ":w=" + before.W + ":h=" + before.H;
                string args = "-hide_banner -loglevel error -y -i \"" + mp4 + "\""
                            + " -map 0:v:0 -vf \"" + vf + "\""
                            + " -c:v libx264 -crf 18 -preset slow -pix_fmt yuv420p -an"
                            + " \"" + tmp + "\"";
                string o, e;
                if (!RunCapture(ff, args, 10 * 60 * 1000, out o, out e) || !File.Exists(tmp))
                {
                    r.Clean = false;
                    r.Msg = before.Text() + "；但 delogo 失败：" + FirstLine(e);
                    return r;
                }

                Badge after = Measure(tmp, 1.0, out err);
                r.After = after;
                if (after == null || after.Found)
                {
                    r.Clean = false;
                    r.Msg = before.Text() + "；修补后仍有痕迹（"
                          + (after == null ? "读不出画面" : after.Text()) + "），不入库";
                    return r;
                }

                string bak = mp4 + ".bak-wm";
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                File.Move(mp4, bak);
                File.Move(tmp, mp4);
                r.Clean = true;
                r.Repaired = true;
                r.Msg = before.Text() + " → 已 delogo 修补，复查干净（"
                      + after.Percent.ToString("0.0", CultureInfo.InvariantCulture)
                      + "%，原片备份 .bak-wm）";
                return r;
            }
            catch (Exception ex)
            {
                r.Clean = false;
                r.Msg = before.Text() + "；修补异常：" + ex.Message;
                return r;
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
    }
}
