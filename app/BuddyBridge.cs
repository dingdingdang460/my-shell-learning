using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WallpaperManager
{
    /// <summary>
    /// 11-15：主人要求「生成之前要检测 WorkBuddy 是否启动可用，可用直接生成，不可用申请启动」。
    ///
    /// 独立 exe 拿不到 WorkBuddy 客户端的模型凭证 / 积分，出片只能由客户端里的
    /// WorkBuddy 会话（自动领单任务）完成。所以「可用」= 客户端主窗口在跑，
    /// 并且自动领单任务的心跳是新鲜的：
    ///   Ok         客户端在跑 + 心跳新鲜 → 确认后直接提交工单，客户端自动出片、程序自动播放
    ///   ClientOnly 客户端在跑但没有心跳 → 仍然提交，如实提示领单任务可能没启用
    ///   NoClient   客户端不在 → 同一个确认框里点「是」先申请启动客户端，再提交
    ///
    /// 心跳 ready.json 由自动领单任务每次运行刷新（无工单也刷新），是纯只读检测输入；
    /// 程序自己不写这个文件，避免「自己给自己盖章说可用」。
    /// </summary>
    static class BuddyBridge
    {
        public enum ReadyState { Ok, ClientOnly, NoClient }

        // 自检注入：不能让自检真的去拉起 GUI 客户端，也不能依赖本机恰好开着客户端
        internal static bool SelfTestNoLaunch = false;
        internal static bool SelfTestNoExe = false;   // 强制「找不到客户端 exe」走失败分支，不依赖本机装没装
        internal static int LaunchCount = 0;
        internal static string LastLaunchPath = "";
        internal static bool? SelfTestClientAlive = null;
        internal static bool? SelfTestHeartbeatFresh = null;
        // 自检注入：把心跳文件指到仓库内临时目录，不写也不读真实 jobs\buddy\ready.json
        internal static string SelfTestHeartbeatPath = null;
        // 自检注入：不让自检去扫真实的项目会话目录
        internal static string SelfTestPickerFailure = null;

        // 领单任务每小时跑一次（平台 recurring 最细粒度）。窗口给「一轮间隔 + 10 分钟容错」：
        // 90 分钟的窗口太松 —— 整整一轮没跑也照样判成就绪，正是 11-15 那次
        // 「程序显示等待、其实没人领」的成因之一。70 分钟只容忍几分钟延迟。
        // 真漏跑的情况由 CloudGen.BuddyJobStuck（工单排队超 75 分钟 + 心跳不新鲜）兜住。
        public const int HeartbeatFreshMinutes = 70;

        // 工单躺多久还没人领就算「没人管」：略大于一轮领单间隔。
        public const int JobStuckMinutes = 75;

        // 心跳放 jobs 根目录（jobs\buddy-ready.json），不放 jobs\buddy\ 里 ——
        // buddy 目录被 App 的工单计数 / 领单扫描按 *.json 枚举，混进去会被当成工单。
        public static string HeartbeatPath
        {
            get { return Path.Combine(CloudGen.Dir, "buddy-ready.json"); }
        }

        /// <summary>生成前的唯一检测入口，返回三态与可直接展示给主人的中文原因。</summary>
        public static ReadyState Check(out string reason)
        {
            bool client = ClientAlive();
            bool worker = HeartbeatFresh();
            if (client && worker)
            {
                reason = "已检测到 WorkBuddy 客户端正在运行，自动领单任务就绪（心跳新鲜）";
                return ReadyState.Ok;
            }
            if (client)
            {
                reason = "已检测到 WorkBuddy 客户端正在运行；暂未读到自动领单心跳，"
                       + "仍会自动提交工单，客户端上线后自动出片";
                return ReadyState.ClientOnly;
            }
            reason = "没检测到运行中的 WorkBuddy 客户端（需要带主窗口的客户端进程）";
            return ReadyState.NoClient;
        }

        /// <summary>
        /// 客户端进程名实测为 WorkBuddy（D:\WorkBuddy\WorkBuddy.exe），
        /// Electron 会起多个子进程，只有带主窗口的那个算「客户端真的开着」。
        /// </summary>
        public static bool ClientAlive()
        {
            if (SelfTestClientAlive.HasValue) return SelfTestClientAlive.Value;
            try
            {
                Process[] ps = Process.GetProcessesByName("WorkBuddy");
                for (int i = 0; i < ps.Length; i++)
                {
                    try { if (ps[i].MainWindowHandle != IntPtr.Zero) return true; }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        /// <summary>自动领单任务每次运行会写 {"ts": unix秒}；领单每小时一次，窗口见 HeartbeatFreshMinutes。</summary>
        public static bool HeartbeatFresh()
        {
            if (SelfTestHeartbeatFresh.HasValue) return SelfTestHeartbeatFresh.Value;
            try
            {
                string hb = string.IsNullOrEmpty(SelfTestHeartbeatPath) ? HeartbeatPath : SelfTestHeartbeatPath;
                if (!File.Exists(hb)) return false;
                string raw = File.ReadAllText(hb);
                Dictionary<string, object> d = Json.AsDict(Json.Parse(raw));
                long ts = d == null ? 0 : Json.Long(d, "ts");
                if (ts <= 0) return false;
                long now = (long)(DateTime.UtcNow
                    - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                return ts <= now + 300 && (now - ts) <= HeartbeatFreshMinutes * 60L;
            }
            catch { return false; }
        }

        /// <summary>
        /// 11-15 晚饭后：主人问「为啥没有会话领单」。查下来真正原因是——
        /// 自动领单任务确实补跑了一次（客户端 19:23 启动时触发 missed 补跑），
        /// 但会话在 21 秒内被模型侧 **429 额度限流**打回，一句话都没执行，因此：
        /// 工单没人动、心跳没刷新、程序还挂着「等 Buddy 出片」。
        /// 程序原先根本读不到这个结局，只会干等。所以这里直接去读客户端里
        /// **最近一次领单会话**的真实收尾消息，把「为什么没出片」摆到台面上：
        ///   429 / 额度 / 限流 / 失败 → 原样返回给界面
        ///   正常收尾（无错误字样）→ 返回空串，表示上次是好的
        /// 只读 %USERPROFILE%\.workbuddy\projects 下最近的 .jsonl，不碰数据库、不改任何文件。
        /// </summary>
        public static string LastPickerFailure(int withinMinutes)
        {
            if (SelfTestPickerFailure != null) return SelfTestPickerFailure;
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".workbuddy", "projects");
                if (!Directory.Exists(root)) return "";
                string[] files = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
                if (files.Length == 0) return "";
                Array.Sort(files, delegate(string a, string b)
                {
                    try { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); }
                    catch { return 0; }
                });

                DateTime limit = DateTime.UtcNow.AddMinutes(-Math.Abs(withinMinutes));
                int cap = files.Length < 12 ? files.Length : 12;
                for (int i = 0; i < cap; i++)
                {
                    DateTime mtime;
                    try { mtime = File.GetLastWriteTimeUtc(files[i]); }
                    catch { continue; }
                    if (mtime < limit) break;                 // 更老的不必再看

                    string head = ReadChunk(files[i], 0, 16384);
                    if (head.IndexOf(PickerMarker, StringComparison.Ordinal) < 0) continue;

                    string msg = LastAssistantText(ReadChunk(files[i], -131072, 131072));
                    return LooksLikeFailure(msg) ? msg : "";
                }
            }
            catch { }
            return "";
        }

        // 领单会话的第一条用户消息就是自动领单任务的提示词，用这一句认出它
        internal const string PickerMarker = "积分出片待领工单";

        static string ReadChunk(string path, long offset, int count)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = offset < 0 ? Math.Max(0, fs.Length + offset) : offset;
                    if (start > fs.Length) return "";
                    long left = fs.Length - start;
                    int want = (int)(left < count ? left : count);
                    if (want <= 0) return "";
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[want];
                    int read = 0;
                    while (read < want)
                    {
                        int n = fs.Read(buf, read, want - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return System.Text.Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch { return ""; }
        }

        /// <summary>取最后一条 assistant 文本；会话被限流时这里就是那句 429。</summary>
        static string LastAssistantText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int at = text.LastIndexOf("\"role\": \"assistant\"", StringComparison.Ordinal);
            if (at < 0) at = text.LastIndexOf("\"role\":\"assistant\"", StringComparison.Ordinal);
            if (at < 0) return "";
            int k = text.IndexOf("\"text\"", at, StringComparison.Ordinal);
            if (k < 0) return "";
            k = text.IndexOf('"', text.IndexOf(':', k) + 1);
            if (k < 0) return "";
            k++;                                           // 跳过开头引号
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = k; i < text.Length && sb.Length < 400; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length) { i++; c = text[i]; }
                else if (c == '"') break;
                sb.Append(c == '\n' ? ' ' : c);
            }
            return sb.ToString().Trim();
        }

        static bool LooksLikeFailure(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            string[] marks = new string[] {
                "429", "限流", "频率限制", "额度", "超出", "失败", "错误", "error", "Error", "异常", "中断"
            };
            for (int i = 0; i < marks.Length; i++)
                if (msg.IndexOf(marks[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 找客户端 exe：自定义配置 → 正在运行的进程路径 → 常见安装位置 → 开始菜单快捷方式。
        /// 找不到就返回空串，绝不瞎猜一个路径去启动。
        /// </summary>
        public static string FindClientExe(AppConfig cfg)
        {
            if (SelfTestNoExe) return "";   // 自检强制失败分支，不依赖本机装没装 WorkBuddy
            if (cfg != null && !string.IsNullOrEmpty(cfg.BuddyExe)
                && File.Exists(cfg.BuddyExe)) return cfg.BuddyExe;

            try
            {
                Process[] ps = Process.GetProcessesByName("WorkBuddy");
                for (int i = 0; i < ps.Length; i++)
                {
                    try
                    {
                        string p = ps[i].MainModule.FileName;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            catch { }

            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] cands = new string[] {
                Path.Combine(la, "Programs", "WorkBuddy", "WorkBuddy.exe"),
                Path.Combine(la, "WorkBuddy", "WorkBuddy.exe"),
                Path.Combine(pf, "WorkBuddy", "WorkBuddy.exe"),
                "C:\\Program Files\\WorkBuddy\\WorkBuddy.exe",
                "C:\\Program Files (x86)\\WorkBuddy\\WorkBuddy.exe",
                "D:\\WorkBuddy\\WorkBuddy.exe"
            };
            for (int i = 0; i < cands.Length; i++)
                if (File.Exists(cands[i])) return cands[i];

            // 开始菜单快捷方式：用 WScript.Shell 反射解析 .lnk，不引入 COM 引用
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null)
                {
                    object shell = Activator.CreateInstance(t);
                    string sm = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                    string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
                    string[] roots = new string[] {
                        sm,
                        string.IsNullOrEmpty(common) ? "" : Path.Combine(common, "Programs")
                    };
                    for (int r = 0; r < roots.Length; r++)
                    {
                        if (string.IsNullOrEmpty(roots[r]) || !Directory.Exists(roots[r])) continue;
                        string[] lnks = Directory.GetFiles(roots[r], "*.lnk", SearchOption.AllDirectories);
                        for (int i = 0; i < lnks.Length; i++)
                        {
                            if (lnks[i].IndexOf("workbuddy", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                                       null, shell, new object[] { lnks[i] });
                            string target = t.InvokeMember("TargetPath", BindingFlags.GetProperty,
                                                           null, sc, null) as string;
                            if (!string.IsNullOrEmpty(target) && File.Exists(target)) return target;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>「不可用申请启动」：由确认框那一次点击授权，尝试拉起客户端。</summary>
        public static bool TryLaunch(AppConfig cfg, out string detail)
        {
            LaunchCount++;
            string exe = FindClientExe(cfg);
            if (string.IsNullOrEmpty(exe))
            {
                detail = "没找到 WorkBuddy 客户端程序。请在「设置 → WorkBuddy 客户端路径」"
                       + "填上 WorkBuddy.exe 的完整路径，或手动打开 WorkBuddy 后重试。";
                return false;
            }
            LastLaunchPath = exe;
            if (SelfTestNoLaunch)
            {
                detail = "自检模式：已定位客户端 " + exe + "（未真正启动）";
                return true;
            }
            try
            {
                ProcessStartInfo pi = new ProcessStartInfo();
                pi.FileName = exe;
                pi.WorkingDirectory = Path.GetDirectoryName(exe);
                pi.UseShellExecute = true;
                Process.Start(pi);
                detail = "已申请启动 WorkBuddy：" + exe + "；客户端就绪后会自动领单出片";
                Log.Note("WorkBuddy 客户端未运行，已申请启动：" + exe);
                return true;
            }
            catch (Exception ex)
            {
                detail = "启动 WorkBuddy 失败：" + ex.Message + "；请手动打开客户端后重试";
                Log.Note("!! 申请启动 WorkBuddy 失败：" + ex.Message);
                return false;
            }
        }
    }
}
