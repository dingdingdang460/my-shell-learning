using System;
using System.IO;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 落盘日志。之前网络失败只写状态栏，而状态栏又被预览层盖住，
    /// 排查时等于什么都没有；现在凡是「本来该报错却看不见」的地方都写这里。
    /// 位置：%TEMP%\wallpaper-debug.log（当天）；跨天时自动归档成
    /// wallpaper-debug-YYYYMMDD.log，只留最近 7 天 —— 单文件到点就清空的话，
    /// 昨天的问题今天再去查就一行都剩不下了。
    /// 当前文件路径始终不变，所以 tail / LastRun / Tail 这些工具不用改。
    /// </summary>
    public static class Log
    {
        static readonly object Gate = new object();

        /// <summary>归档日志保留几天</summary>
        const int KeepDays = 7;

        const string Stamp = "wallpaper-debug-";
        const string Ext = ".log";

        public static string Path0
        {
            get { return Path.Combine(Path.GetTempPath(), "wallpaper-debug.log"); }
        }

        public static void Note(string msg)
        {
            try
            {
                lock (Gate)
                {
                    string p = Path0;
                    Roll(p);
                    FileInfo fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(p,
                        DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + "  " + msg + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        /// <summary>
        /// 跨天就把前一天的文件挪成归档名。判断依据是文件的**最后写入日期**，
        /// 不是「今天有没有写过」—— 应用可能整天没跑，那也要在下一次写日志时归档。
        /// </summary>
        static void Roll(string current)
        {
            try
            {
                FileInfo fi = new FileInfo(current);
                if (!fi.Exists) return;
                if (fi.LastWriteTime.Date >= DateTime.Today) return;

                string dir = Path.GetDirectoryName(current);
                string arch = Path.Combine(dir, Stamp + fi.LastWriteTime.ToString("yyyyMMdd") + Ext);
                if (File.Exists(arch)) File.Delete(arch);
                File.Move(current, arch);
                Prune(dir);
            }
            catch { }
        }

        /// <summary>删掉超过 KeepDays 天的归档（只动自己命名的文件，别碰别人的临时文件）</summary>
        static void Prune(string dir)
        {
            try
            {
                DateTime cutoff = DateTime.Today.AddDays(-KeepDays);
                foreach (string f in Directory.GetFiles(dir, Stamp + "*" + Ext))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    string digits = name.Substring(Stamp.Length);
                    DateTime day;
                    if (digits.Length != 8) continue;
                    if (!DateTime.TryParseExact(digits, "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out day)) continue;
                    if (day < cutoff) File.Delete(f);
                }
            }
            catch { }
        }

        /// <summary>
        /// 只取「最近一次播放器运行」的那一段（从最后一次 === WallpaperPlayer 启动 开始）。
        /// 探测日志一次四十多行，用 Tail 截尾会把开头的环境信息切掉，排查时正好缺最关键的那几行。
        /// </summary>
        public static string LastRun()
        {
            try
            {
                if (!File.Exists(Path0)) return "（没有日志）";
                string[] all = File.ReadAllLines(Path0, Encoding.UTF8);
                int start = 0;
                for (int i = all.Length - 1; i >= 0; i--)
                {
                    if (all[i].Contains("=== WallpaperPlayer 启动")) { start = i; break; }
                }
                StringBuilder sb = new StringBuilder();
                for (int i = start; i < all.Length; i++) sb.Append(all[i]).Append("\r\n");
                string s = sb.ToString().TrimEnd();
                return s.Length == 0 ? Tail(30) : s;
            }
            catch (Exception ex)
            {
                return "（读日志失败：" + ex.Message + "）";
            }
        }

        /// <summary>读最后几行，用于把测试结果直接摆到界面上</summary>
        public static string Tail(int lines)
        {
            try
            {
                if (!File.Exists(Path0)) return "（没有日志）";
                string[] all = File.ReadAllLines(Path0, Encoding.UTF8);
                int start = all.Length - lines;
                if (start < 0) start = 0;
                StringBuilder sb = new StringBuilder();
                for (int i = start; i < all.Length; i++)
                {
                    sb.Append(all[i]).Append("\r\n");
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "（读日志失败：" + ex.Message + "）";
            }
        }
    }
}
