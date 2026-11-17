using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 动态壁纸由独立的 WallpaperPlayer.exe 承担：它要往桌面层挂窗口 + 用 D3D/DComp 画帧，
    /// 一旦某个 P/Invoke 签名写错就是 AccessViolation，进程直接没——放独立进程里，
    /// 管理器最多丢一个播放器，不会把主界面一起带走。
    ///
    /// 控制走窗口消息：播放器窗口类名固定 WpPlayerWnd，
    /// WM_APP+1 = 暂停，WM_APP+2 = 继续，WM_APP+3 = 退出。
    /// </summary>
    public static class Dynamic
    {
        public const string PlayerClass = "WpPlayerWnd";
        const int WM_APP = 0x8000;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr lp);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lp);

        [DllImport("user32.dll")]
        static extern bool IsWindow(IntPtr hWnd);

        delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        static Process proc;

        /// <summary>播放器进程意外退出时触发（主动 Stop 不触发）。用来立刻恢复，别让桌面空等 4 秒。</summary>
        public static event Action Died;

        /// <summary>上一次主动停止的时间点。Exited 是异步回调，靠它区分"自己关的"和"崩了"</summary>
        static int lastStopTick;

        /// <summary>
        /// 最近一次启动播放器的时刻。看门狗用它给「进程刚起、窗口还没建好」这段留宽限：
        /// 播放器从 Process.Start 到首帧上屏大约 250~400ms，这期间进程在、窗口不在，
        /// 看门狗要是正好这时候睁眼，就会判定「掉线了」再拉一个进程，
        /// 于是换一张壁纸要起两次播放器，桌面多闪一下。
        /// </summary>
        public static int LastLaunchTick;

        /// <summary>挂进桌面层的方式。8 = Progman 分层子窗口 + DefView 之下（官方口径，唯一「能上屏 + 图标可见」的档位）</summary>
        public static int MountMode = 8;

        public static string PlayerPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WallpaperPlayer.exe"); }
        }

        public static bool PlayerExists
        {
            get { return File.Exists(PlayerPath); }
        }

        public static bool Running
        {
            get
            {
                try { return proc != null && !proc.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>
        /// 播放器进程是否还活着。不走 proc 字段：管理器自己可能重启过，
        /// 那时 proc 是空的，但桌面上其实还有一个孤儿播放器在跑。
        /// </summary>
        public static bool Alive
        {
            get
            {
                try { return Process.GetProcessesByName("WallpaperPlayer").Length > 0; }
                catch { return Running; }
            }
        }

        /// <summary>桌面层里的播放器窗口是否还在（进程活着但窗口被销毁也要能发现）</summary>
        public static bool WindowAlive
        {
            get { return ControlHandle() != IntPtr.Zero; }
        }

        static void Kill()
        {
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill();
                    // 等进程真正退出、松开 mp4 句柄再返回。否则紧接着删片子会撞 sharing
                    // violation：条目从 index.json 抹掉，视频文件却留在磁盘上成孤儿。
                    // 正常退出路径上面已 PostMessage + Sleep(150)，走到这里多半是播放器卡住，
                    // 1.5 秒上限换来删除成功，值得。
                    try { proc.WaitForExit(1500); } catch { }
                }
            }
            catch { }
            // Process 持有非托管句柄，每次换台都覆盖 proc 却不 Dispose 会慢慢累积。
            try { if (proc != null) proc.Dispose(); } catch { }
            try { proc = null; } catch { }
        }

        /// <summary>启动播放器播视频。失败返回 false，err 里是原因</summary>
        public static bool Start(string videoAbs, int fps, out string err)
        {
            err = "";
            if (!PlayerExists)
            {
                err = "找不到 WallpaperPlayer.exe（请先跑一次 build.bat）";
                return false;
            }
            if (string.IsNullOrEmpty(videoAbs) || !File.Exists(videoAbs))
            {
                err = "视频文件不存在：" + videoAbs;
                return false;
            }

            return Launch("--video \"" + videoAbs + "\" --fps " + fps
                        + " --class " + PlayerClass + " --mount=" + MountMode,
                          videoAbs + " fps=" + fps, out err);
        }

        static bool Launch(string arguments, string desc, out string err)
        {
            err = "";

            // ---------------------------------------------------------------
            // 换台顺序：以前是 Stop() → Start()。
            // Stop 那一瞬间旧的播放器窗口已经销毁，而新进程还要 250~400ms 才画上首帧，
            // 这半秒桌面没有任何动态层，直接露出系统静态壁纸 —— 也就是用户看到的
            // 「点换一张，先弹出我刚下载的那张，然后再切过去」。
            //
            // 现在改成交接（handoff）：先起新的（--keep-old 让新播放器不要清旧窗口），
            // 等新窗口真的挂进桌面层、画上首帧，再收掉旧的。
            // 中间旧窗口一直顶着桌面，视觉上就是旧图 → 新图，没有空档。
            // ---------------------------------------------------------------
            Process oldProc = proc;
            IntPtr oldHwnd = ControlHandle();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = PlayerPath;
                psi.Arguments = arguments + " --keep-old";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(PlayerPath);
                proc = Process.Start(psi);

                int newPid = 0;
                try { newPid = proc.Id; } catch { }

                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += delegate
                    {
                        // 主动停止（Stop/Kill/切换层级）时不要当成崩溃；
                        // Exited 回调是线程池线程，3 秒窗口足够覆盖
                        if (Environment.TickCount - lastStopTick < 3000) return;
                        try { if (Died != null) Died(); } catch { }
                    };
                }
                catch { }

                LastLaunchTick = Environment.TickCount;
                Log.Note("动态壁纸启动：" + desc + " mount=" + MountMode + "（交接模式）");

                // 等新窗口上屏再收旧的。正常 250~400ms；起不来最多等 2.5 秒，
                // 之后无论如何都要把旧的收掉，不能留两个播放器抢桌面。
                bool shown = WaitPlayerWindow(newPid, proc, 2500);
                StopOld(oldProc, oldHwnd, newPid);

                if (!shown)
                {
                    // v0.9.1：以前窗口没亮出来也 return true，上层照样把
                    // 「正在播新图」写进配置、卡片亮起「▶ 播放中」，可桌面其实
                    // 什么动态都没有 —— 或者更糟，旧的还在播。现在如实报失败，
                    // 让上层停干净、清状态，不再留假的播放中。
                    err = "播放器窗口 2.5 秒内没上屏（视频解不开或桌面层挂载失败）";
                    Log.Note("动态壁纸启动失败：" + err + " pid=" + newPid);
                    lastStopTick = Environment.TickCount;   // 主动收的，不算崩溃
                    try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                    try { if (proc != null) proc.WaitForExit(1500); } catch { }
                    try { if (proc != null) proc.Dispose(); } catch { }
                    proc = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                Log.Note("动态壁纸启动失败：" + ex.Message);
                // 新进程都没起来，旧的还留着当兜底，别把桌面搞空
                return false;
            }
        }

        /// <summary>收掉上一台播放器：窗口先发退出消息，进程最多等 900ms，不走就强杀</summary>
        static void StopOld(Process oldProc, IntPtr oldHwnd, int newPid)
        {
            bool hasWindow = oldHwnd != IntPtr.Zero;
            if (hasWindow)
            {
                uint pid;
                GetWindowThreadProcessId(oldHwnd, out pid);
                if ((int)pid == newPid) hasWindow = false;      // 已经是新窗口了，别误杀
            }
            if (!hasWindow && (oldProc == null || SafeHasExited(oldProc))) return;

            lastStopTick = Environment.TickCount;               // 这是主动收的，不算崩溃
            if (hasWindow) PostMessage(oldHwnd, WM_APP + 3, IntPtr.Zero, IntPtr.Zero);

            if (oldProc != null && !SafeHasExited(oldProc))
            {
                try { if (!oldProc.WaitForExit(900)) oldProc.Kill(); }
                catch { }
                return;
            }

            // 孤儿窗口（管理器重启前留下的播放器，没有进程句柄）：给它 300ms 自己退
            if (hasWindow)
            {
                Thread.Sleep(300);
                if (IsWindow(oldHwnd)) PostMessage(oldHwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
        }

        static bool SafeHasExited(Process p)
        {
            try { return p == null || p.HasExited; }
            catch { return true; }
        }

        /// <summary>等「属于这个新进程」的播放器窗口出现（不能只按类名找，旧窗口同名）</summary>
        static bool WaitPlayerWindow(int pid, Process p, int timeoutMs)
        {
            if (pid <= 0) return false;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (WindowOfPid(pid) != IntPtr.Zero) return true;
                if (SafeHasExited(p)) return false;      // 新进程已经挂了，别白等
                Thread.Sleep(35);
            }
            return WindowOfPid(pid) != IntPtr.Zero;
        }

        static int wantPid;
        static IntPtr foundHwnd;

        static bool MatchPlayerWindow(IntPtr h, IntPtr lp)
        {
            if (foundHwnd != IntPtr.Zero) return false;

            StringBuilder sb = new StringBuilder(128);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != PlayerClass) return true;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if ((int)pid == wantPid)
            {
                foundHwnd = h;
                return false;
            }
            return true;
        }

        static IntPtr WindowOfPid(int pid)
        {
            if (pid <= 0) return IntPtr.Zero;

            wantPid = pid;
            foundHwnd = IntPtr.Zero;

            // 顶层（mount 5/6/7 是顶层窗口）
            EnumWindows(MatchPlayerWindow, IntPtr.Zero);
            if (foundHwnd != IntPtr.Zero) return foundHwnd;

            // 桌面层（mount 8 / 默认档是 Progman 的子窗口，EnumChildWindows 会递归到底）
            IntPtr pm = FindWindow("Progman", null);
            if (pm != IntPtr.Zero) EnumChildWindows(pm, MatchPlayerWindow, IntPtr.Zero);

            return foundHwnd;
        }

        /// <summary>跑一次可见性探测：等它自己退出，再把日志尾巴交给弹窗；绝不让进程留在桌面上</summary>
        public static string Probe(int seconds)
        {
            return ProbeCore("--probe " + seconds, seconds * 2 + 20, 30);
        }

        /// <summary>第二轮探测：原生子窗口 + 每个变体截图（Probe2.cs）。约 15 秒</summary>
        public static string Probe2()
        {
            return ProbeCore("--probe2", 90, 90);
        }

        static string ProbeCore(string arguments, int timeoutSec, int busySec)
        {
            if (!PlayerExists) return "找不到 WallpaperPlayer.exe（请先跑一次 build.bat）";

            // 连点两次会起两个播放器：前一个卡住时它的窗口会压住桌面图标。
            // 用原子标志挡掉第二次，并先清理掉任何残留进程。
            if (Interlocked.CompareExchange(ref probeBusy, 1, 0) != 0)
                return "上一次探测还在跑，等它退出后再试（最多约 " + busySec + " 秒）。";

            try
            {
                KillStrays();

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = PlayerPath;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = Path.GetDirectoryName(PlayerPath);

                int code = 0;
                bool timedOut = false;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutSec * 1000))
                    {
                        timedOut = true;
                        try { p.Kill(); } catch { }
                    }
                    else
                    {
                        try { code = p.ExitCode; } catch { code = 0; }
                    }
                }

                // 不管正常退出、崩溃还是被强杀，都让 Explorer 重画一次桌面，
                // 这样即使播放器留下过窗口，图标也会恢复
                RefreshDesktop();

                if (timedOut)
                    return "探测进程超时，已强制结束并刷新桌面。\r\n\r\n" + Log.LastRun();

                if (code == 2)
                    return "探测被播放器自己的看门狗强制结束：有系统调用卡住了。"
                         + "下面 !!! 那一行写着卡在哪一步。\r\n\r\n" + Log.LastRun();

                if (code != 0)
                    return "播放器异常退出，退出码 " + code
                         + "（下面日志里找 !!! 开头的行，那是异常类型与堆栈）\r\n\r\n"
                         + Log.LastRun();

                return Log.LastRun();
            }
            catch (Exception ex)
            {
                RefreshDesktop();
                return "启动探测失败：" + ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref probeBusy, 0);
            }
        }

        static int probeBusy;

        /// <summary>清掉任何残留的播放器进程——它们的窗口可能还挂在桌面上压着图标</summary>
        static void KillStrays()
        {
            try
            {
                Process[] all = Process.GetProcessesByName("WallpaperPlayer");
                foreach (Process x in all)
                {
                    try { x.Kill(); } catch { }
                }
                if (all.Length > 0)
                {
                    Log.Note("清理残留播放器进程 " + all.Length + " 个");
                    Thread.Sleep(300);
                }
            }
            catch { }
            proc = null;
        }

        /// <summary>请求 Explorer 刷新桌面（图标被盖住后靠它恢复）</summary>
        static void RefreshDesktop()
        {
            try
            {
                IntPtr pm = FindWindow("Progman", null);
                if (pm != IntPtr.Zero) PostMessage(pm, 0x111 /* WM_COMMAND */, new IntPtr(0x7402), IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// 找控制窗口。窗口现在是 **Progman 的子窗口**（插在 SHELLDLL_DefView 下方），
        /// 而 FindWindow 只枚举顶层窗口，所以必须再用 FindWindowEx 在 Progman 下面找一遍。
        /// </summary>
        static IntPtr ControlHandle()
        {
            IntPtr h = FindWindow(PlayerClass, null);
            if (h != IntPtr.Zero) return h;

            IntPtr pm = FindWindow("Progman", null);
            if (pm == IntPtr.Zero) return IntPtr.Zero;

            h = FindWindowEx(pm, IntPtr.Zero, PlayerClass, null);
            if (h != IntPtr.Zero) return h;

            // 默认挂载方式是把播放器窗口塞进 SHELLDLL_DefView（图标层底下），得去那一层找
            IntPtr dv = FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero)
            {
                h = FindWindowEx(dv, IntPtr.Zero, PlayerClass, null);
                if (h != IntPtr.Zero) return h;
                h = FindWindowEx(dv, IntPtr.Zero, null, "WallpaperPlayerVideo");
                if (h != IntPtr.Zero) return h;
            }

            return FindWindowEx(pm, IntPtr.Zero, null, "WallpaperPlayerVideo");
        }

        public static void Pause()
        {
            IntPtr h = ControlHandle();
            if (h != IntPtr.Zero) PostMessage(h, WM_APP + 1, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Resume()
        {
            IntPtr h = ControlHandle();
            if (h != IntPtr.Zero) PostMessage(h, WM_APP + 2, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Stop()
        {
            lastStopTick = Environment.TickCount;
            IntPtr h = ControlHandle();
            if (h != IntPtr.Zero)
            {
                PostMessage(h, WM_APP + 3, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(150);
            }
            bool hadProc = Running;
            Kill();
            // 窗口和进程句柄都没有，说明是上一次运行留下的孤儿进程，按名字收掉
            if (h == IntPtr.Zero && !hadProc) KillStrays();
            RefreshDesktop();
        }
    }
}
