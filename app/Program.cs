using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WallpaperManager
{
    static class Program
    {
        // 单实例锁：双击 exe / build.bat 多次时，不会开出第二个托盘图标，
        // 而是把已有窗口叫到前面来。
        static Mutex singleInstance;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [STAThread]
        static void Main()
        {
            bool isNew;
            singleInstance = new Mutex(true, "WallpaperManager_SingleInstance_v1", out isNew);

            if (!isNew)
            {
                IntPtr h = FindWindow(null, "壁纸管家");
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, 9);          // SW_RESTORE：从托盘/最小化恢复
                    SetForegroundWindow(h);
                }
                return;
            }

            try
            {
                Desk.EnableDpiAwareness();
                App app = new App();
                InstallCrashHandlers(app);
                app.Initialize();
                app.Run();
            }
            catch (Exception ex)
            {
                try
                {
                    string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.log");
                    File.WriteAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex);
                }
                catch { }
                MessageBox.Show("启动失败：\r\n" + ex.Message + "\r\n\r\n详细堆栈见 startup-error.log",
                    "壁纸管家", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GC.KeepAlive(singleInstance);
            }
        }

        // ---------------- 11-16 全局异常兜底 ----------------
        // 以前只有启动那一下包在 try 里：Run() 期间任何未捕获异常都直接让进程消失，
        // 没有提示、没有日志，用户只看到「程序没了」。播放器早就注册了
        // AppDomain.UnhandledException，管理器反而没有，这里补齐两层：
        //   界面线程 —— 接住并继续跑（只弹一次可读提示，点掉不影响后续操作）
        //   后台线程 —— CLR 会结束进程，至少把现场写进日志与独立 crash 文件
        static bool crashDialogShown;

        static void InstallCrashHandlers(App app)
        {
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                LogCrash("界面线程", e.Exception);
                e.Handled = true;                     // 吃掉这一下，程序不退出
                if (crashDialogShown) return;
                crashDialogShown = true;
                try
                {
                    MessageBox.Show(
                        "壁纸管家遇到一个内部错误，已记录到日志，程序会继续运行。\r\n\r\n"
                        + (e.Exception == null ? "" : e.Exception.Message)
                        + "\r\n\r\n如果之后操作异常，请右键托盘图标 →「打开日志」把日志发出来。",
                        "壁纸管家", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogCrash("后台线程", e.ExceptionObject as Exception);
            };
        }

        static void LogCrash(string where, Exception ex)
        {
            try
            {
                string msg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]\r\n"
                           + (ex == null ? "（无异常对象）" : ex.ToString());
                Log.Note("崩溃：" + where + " · " + (ex == null ? "" : ex.Message));
                string p = Path.Combine(Path.GetTempPath(),
                    "wallpaper-crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                File.WriteAllText(p, msg, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    public class App : Application
    {
        public AppConfig Config;
        public List<WallpaperItem> Items = new List<WallpaperItem>();
        public bool ReallyExit;
        public string ResourcesDir;

        MainWindow win;
        Forms.NotifyIcon tray;
        DispatcherTimer rotateTimer;
        int cursor = -1;
        bool trayTipShown;
        readonly Random rnd = new Random();

        GreetingWindow currentGreeting;
        bool wtsRegistered;
        IntPtr sessionHwnd;
        DispatcherTimer fullscreenTimer;
        bool dynamicPausedByFullscreen;

        // 11-16 功耗守卫：见 CheckPower()。pausedByPower 保证只暂停/恢复一次，
        // 不会每 60 秒都往播放器发一遍暂停消息。
        DispatcherTimer powerTimer;
        bool pausedByPower;

        // 11-14 深夜：切换壁纸后「▶ 播放中」定格在上一张卡片上。
        // 根因是 DynamicEnabled / LastDynamicId / cursor / 界面角标各写各的，
        // 失败路径还整个不清理。下面这一组是唯一的收口，所有路径都必须走它。
        // 自检专用注入：不让 --badgetest 碰用户正在播的播放器进程。
        public int SelfTestAliveOverride = -1;   // -1=看真实进程 0=强制没在播 1=强制在播
        public bool SelfTestNoPlayerOps;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll")]
        static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        [DllImport("shell32.dll")]
        static extern int SHQueryUserNotificationState(out int state);

        const int NOTIFY_FOR_THIS_SESSION = 0;
        const int WM_WTSSESSION_CHANGE = 0x02B1;
        const int WTS_SESSION_UNLOCK = 0x8;

        /// <summary>
        /// 11-15：跑 --*test / --probe* 自检时，不继承真实配置里的「上次动态壁纸」。
        /// 以前每个自检进程一启动就 RestoreDynamic 拉起播放器：既把自检和用户桌面
        /// 绑在一起，残留进程还会让下一项 --launchtest 误报「开播前有残留播放器」。
        /// --dynvideo / --stopdynamic 这类真实入口不受影响。
        /// </summary>
        static bool SelfTestArgs()
        {
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 1; i < a.Length; i++)
            {
                string s = a[i];
                if (!s.StartsWith("--", StringComparison.Ordinal)) continue;
                string t = s.ToLowerInvariant();
                if (t.EndsWith("test", StringComparison.Ordinal)
                    || t.StartsWith("--probe", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        public void Initialize()
        {
            ResourcesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources");
            Greeting.Load(ResourcesDir);

            Config = ConfigStore.Load();
            // 数据目录落点必须能自查：搬过盘与没搬过，日志里一眼能分出来。
            Log.Note("数据目录：" + ConfigStore.Dir
                     + (ConfigStore.Relocated ? "（已按 data-root.txt 迁出默认位置）" : "（默认 %APPDATA%\\wallpaper）"));
            // 代理：空 = 直连（和以前完全一致）；填了就走代理。所有联网点都读这一处。
            if (!Net.Configure(Config.Proxy)) Log.Note("代理地址解析失败，已退回直连：" + Config.Proxy);
            else if (Net.Current.Length > 0) Log.Note("网络代理：" + Net.Current);
            // 8 = 微软 24H2 官方口径（Progman 分层子窗口 + DefView 之下），
            // 11-11 12:22 实测：视频铺满桌面，桌面图标全部可见可用。
            // 0-7 各档都已被实测否决，老配置一律升级到 8。
            if (Config.MountMode != 8) Config.MountMode = 8;
            Dynamic.MountMode = Config.MountMode;

            // 中文名：先查手写词典，再查上次翻好的缓存，都没有才联网补，补完落盘到库根
            Translate.Init(Path.Combine(ResourcesDir, "titles-zh.json"),
                           Path.Combine(Config.LibRoot, "zh-cache.json"));

            // 合成图缓存按体积上限收一次：换过 4K 屏 / 长期使用后这里能涨到几百 MB
            int trimmed = WallpaperFit.TrimTo(Config.CacheLimitMb * 1024L * 1024L);
            if (trimmed > 0) Log.Note("启动清理合成图缓存 " + trimmed + " 张（上限 " + Config.CacheLimitMb + "MB）");

            ReloadLibrary();

            win = new MainWindow(this);
            this.MainWindow = win;

            SetupTray();
            SetupTimer();
            SetupFullscreenWatch();
            SetupPowerWatch();

            win.Show();
            RestoreLastWallpaper();
            RestoreDynamic();
            HookSession();

            // 探测/验证时不播欢迎动画：它是全屏窗口，会把桌面整个盖住，
            // 而且全屏检测还会顺手把动态壁纸暂停掉 —— 验证动态壁纸时等于自己挡自己。
            // （--dynvideo 就是「自动开播第一个视频」的一键验证开关，同样跳过）
            // 11-16：--*test / --probe* 一律不播欢迎动画。以前这里是一长串显式名单，
            // 新加自检忘了补名字，就会在自检时弹全屏欢迎窗、还会顺手暂停动态壁纸，
            // 把播放状态的断言整个带偏。统一走 SelfTestArgs()，以后加自检不用再改这里。
            bool skipGreeting = SelfTestArgs();
            foreach (string s in Environment.GetCommandLineArgs())
            {
                if (s.StartsWith("--probe", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--dynvideo", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--stopdynamic", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--rotatetest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--fxselftest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--cloudtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--dailytest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--statictest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--settingstest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--menutest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--genapicheck", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--searchtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--greettest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--deltest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--arrivetest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--uitest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--gencmdtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--cloudcfgtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--cloudflowtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--importexporttest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--proxytest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--videotest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--relatedvideotest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--orphantest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--clouddialogtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--contenttest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--badgetest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--launchtest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--buddyhinttest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--switchmotiontest", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("--autogentest", StringComparison.OrdinalIgnoreCase))
                {
                    skipGreeting = true;
                    break;
                }
            }

            if (Config.WelcomeOnBoot && !skipGreeting)
            {
                DispatcherTimer once = new DispatcherTimer();
                once.Interval = TimeSpan.FromSeconds(1.2);
                once.Tick += delegate { once.Stop(); ShowGreeting(); };
                once.Start();
            }

            StartWatchdog();

            // 带 --probe 启动：窗口起来后自动跑一次可见性探测，省掉「右键托盘菜单」这一步
            foreach (string s in Environment.GetCommandLineArgs())
            {
                if (string.Equals(s, "--probe2", StringComparison.OrdinalIgnoreCase))
                {
                    DispatcherTimer auto2 = new DispatcherTimer();
                    auto2.Interval = TimeSpan.FromSeconds(1.5);
                    auto2.Tick += delegate { auto2.Stop(); RunProbe2(); };
                    auto2.Start();
                    break;
                }
                if (string.Equals(s, "--probe", StringComparison.OrdinalIgnoreCase))
                {
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(1.5);
                    auto.Tick += delegate { auto.Stop(); RunProbe(); };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--dynvideo", StringComparison.OrdinalIgnoreCase))
                {
                    // 库里第一个视频直接开播：用来一键验证动态壁纸链路
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(1.2);
                    auto.Tick += delegate { auto.Stop(); StartFirstVideo(); };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--rotatetest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「托盘换一张」：3 秒后连点两次，中间隔 5 秒。
                    // 判定标准（看日志时序）：每次换台都应该是
                    //   动态壁纸启动 X（交接模式） → 新播放器「首帧提交」 → 旧播放器才「播放结束」
                    // 以及全程只有一个 WallpaperPlayer 进程；切到静态图时顺序是
                    //   「设为静态壁纸」 → 「收到停止指令」，而不是反过来。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        Log.Note("自检 --rotatetest：第 1 次换一张（当前 " + (cursor >= 0 && cursor < Items.Count ? Items[cursor].Title : "无") + "）");
                        RotateNext();

                        DispatcherTimer t2 = new DispatcherTimer();
                        t2.Interval = TimeSpan.FromSeconds(5);
                        t2.Tick += delegate
                        {
                            t2.Stop();
                            Log.Note("自检 --rotatetest：第 2 次换一张（当前 " + (cursor >= 0 && cursor < Items.Count ? Items[cursor].Title : "无") + "）");
                            RotateNext();

                            DispatcherTimer t3 = new DispatcherTimer();
                            t3.Interval = TimeSpan.FromSeconds(6);
                            t3.Tick += delegate
                            {
                                t3.Stop();
                                Log.Note("自检 --rotatetest：结束（动态=" + Config.DynamicEnabled
                                         + " 播放器=" + Dynamic.Alive + " 窗口=" + Dynamic.WindowAlive + "）");
                            };
                            t3.Start();
                        };
                        t2.Start();
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--cloudtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「云端动效」整条链路：3 秒后给库里第一张静态图下单，8 秒后报到货状态，
                    // 14 秒后停掉。人工（或脚本）只要在 8 秒内把 mp4 放进 videos\<id>.mp4，
                    // 就能看到「自动入库 → 自动上桌」。
                    // 判定标准：日志里「云端生成：已下单」「云端生成：到货」「动态壁纸已开始 …云端飘动视频」。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        RunCloudTest(0);
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--statictest", StringComparison.OrdinalIgnoreCase))
                {
                    // 复现主人报的那个 bug：「新下载一张图设为壁纸，没几秒自己弹走」。
                    //   3s  先让一张图以动态方式播起来（模拟上次在播动态壁纸）
                    //   9s  换成另一张纯静态图（模拟用户点新下载的那张）
                    //   24s 检查：动态是否真的关了、播放器是否退出、看门狗有没有偷偷重启
                    // 判定标准：24s 那条日志里 动态=False 播放器=False 看门狗重启=0，
                    // 并且期间日志里没有「看门狗：动态壁纸不见了」「恢复动态壁纸」。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        WallpaperItem dyn = null;
                        for (int i = 0; i < Items.Count; i++)
                        {
                            if (!Items[i].IsVideo) { dyn = Items[i]; break; }
                        }
                        if (dyn == null) { Log.Note("自检 --statictest：库里没有静态图"); return; }

                        SetMotionFx(dyn, 0);
                        Log.Note("自检 --statictest(开播)：图=" + dyn.Id + " 动态=" + Config.DynamicEnabled
                                 + " 播放器=" + Dynamic.Alive);

                        DispatcherTimer t9 = new DispatcherTimer();
                        t9.Interval = TimeSpan.FromSeconds(6);
                        t9.Tick += delegate
                        {
                            t9.Stop();
                            WallpaperItem plain = null;
                            for (int i = 0; i < Items.Count; i++)
                            {
                                if (Items[i] == dyn || Items[i].IsVideo) continue;
                                if (MotionCapable(Items[i])) continue;     // 要挑一张纯静态的
                                plain = Items[i];
                                break;
                            }
                            if (plain == null)
                            {
                                string dbg = "";
                                for (int i = 0; i < Items.Count && i < 8; i++)
                                    dbg += Items[i].Id + "(" + (Items[i].IsVideo ? "v" : "i")
                                         + ",m=" + Items[i].Motion + ") ";
                                Log.Note("自检 --statictest：没有第二张纯静态图，跳过（共 " + Items.Count
                                         + " 条 前几条 " + dbg + "）");
                                return;
                            }

                            Log.Note("自检 --statictest(设静态前)：动态=" + Config.DynamicEnabled
                                     + " 播放器=" + Dynamic.Alive + " 待设=" + plain.Id);
                            ApplyItem(plain);

                            DispatcherTimer t24 = new DispatcherTimer();
                            t24.Interval = TimeSpan.FromSeconds(15);
                            t24.Tick += delegate
                            {
                                t24.Stop();
                                string wp = "";
                                try
                                {
                                    using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser
                                               .OpenSubKey(@"Control Panel\Desktop"))
                                    {
                                        if (k != null) wp = (string)k.GetValue("WallPaper") ?? "";
                                    }
                                }
                                catch { }
                                Log.Note("自检 --statictest(15s后)：动态=" + Config.DynamicEnabled
                                         + " 播放器=" + Dynamic.Alive + " 窗口=" + Dynamic.WindowAlive
                                         + " 看门狗重启=" + watchRestarts + " 当前壁纸=" + wp);
                            };
                            t24.Start();
                        };
                        t9.Start();
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--dailytest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「日常便利」三件事，结果全写在日志里：
                    //   应用两张 → 历史栈；回滚 → 当前回到上一张；
                    //   --dailytest <文件夹> 还能顺带验证导入（把两个文件复制进库）。
                    string impDir = "";
                    string[] cmd = Environment.GetCommandLineArgs();
                    if (cmd.Length > 2 && string.Equals(cmd[1], "--dailytest", StringComparison.OrdinalIgnoreCase))
                        impDir = cmd[2];
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        if (Items.Count < 2) { Log.Note("自检 --dailytest：库里不足 2 条"); return; }

                        ApplyItem(Items[0]);
                        ApplyItem(Items[1]);
                        Log.Note("自检 --dailytest(历史)：栈深=" + history.Count);

                        DispatcherTimer t5 = new DispatcherTimer();
                        t5.Interval = TimeSpan.FromSeconds(5);
                        t5.Tick += delegate
                        {
                            t5.Stop();
                            string before = IdAt(cursor);
                            BackToPrevious();
                            Log.Note("自检 --dailytest(回滚)：从 " + before + " 回到 " + IdAt(cursor)
                                     + "  栈深=" + history.Count);

                            if (impDir.Length > 0) ImportPaths(new string[] { impDir });
                            Log.Note("自检 --dailytest(导入)：库里现有 " + Items.Count + " 条");

                            DispatcherTimer t12 = new DispatcherTimer();
                            t12.Interval = TimeSpan.FromSeconds(5);
                            t12.Tick += delegate
                            {
                                t12.Stop();
                                Log.Note("自检 --dailytest(结束)：库里 " + Items.Count + " 条");
                            };
                            t12.Start();
                        };
                        t5.Start();
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--fxselftest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「动态播放」这条唯一路径（右键/预览/顶栏最终都走 SetMotionFx 或 GenCloud）：
                    // 3 秒后挑一张已经有云端视频的静态图播起来，8 秒后报快照并停掉。
                    // 判定标准：日志里有「动效 = 2（飘动视频）」，期间播放器与窗口都活着。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        WallpaperItem pick = null;
                        for (int i = 0; i < Items.Count; i++)
                        {
                            if (!Items[i].IsVideo && VideoFor(Items[i]) != null) { pick = Items[i]; break; }
                        }
                        if (pick == null) { Log.Note("自检 --fxselftest：库里没有配好云端视频的静态图"); return; }

                        Log.Note("自检 --fxselftest：选中 " + pick.Id + " 原 effect=" + pick.Effect);
                        SetMotionFx(pick, 2);

                        DispatcherTimer t8 = new DispatcherTimer();
                        t8.Interval = TimeSpan.FromSeconds(8);
                        t8.Tick += delegate
                        {
                            t8.Stop();
                            Log.Note("自检 --fxselftest(8s)：动态=" + Config.DynamicEnabled
                                     + " 播放器=" + Dynamic.Alive + " 窗口=" + Dynamic.WindowAlive
                                     + " effect=" + pick.Effect);
                            StopDynamic(true);
                            Log.Note("自检 --fxselftest(结束)：动态=" + Config.DynamicEnabled
                                     + " 播放器=" + Dynamic.Alive);
                        };
                        t8.Start();
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--genapicheck", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检：只构造两家服务商的请求体、**不发送**，把 body 落到临时文件里供校验。
                    // 目的很实在：没有真 Key 的情况下，至少保证 base64 传图、字段名、时长参数拼对了，
                    // 主人填上 Key 之后不该再倒在「请求格式不对」这种问题上。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        WallpaperItem pick = null;
                        for (int i = 0; i < Items.Count; i++)
                            if (!Items[i].IsVideo) { pick = Items[i]; break; }
                        if (pick == null) { Log.Note("自检 --genapicheck：库里没有静态图"); return; }

                        string img = ResolveMotionImage(pick);
                        if (string.IsNullOrEmpty(img) || !File.Exists(img))
                        {
                            Log.Note("自检 --genapicheck：找不到图片文件");
                            return;
                        }

                        AppConfig probe = new AppConfig();
                        probe.GenApiKey = "TEST-KEY-NOT-REAL";
                        probe.GenModel = "";
                        probe.GenResolution = "720P";
                        int sec = Config.GenSeconds;

                        string[] provs = { "wanx", "ark" };
                        for (int i = 0; i < provs.Length; i++)
                        {
                            probe.GenProvider = provs[i];
                            GenApi.BodyResult br = GenApi.BuildBody(probe, img, CloudGen.BuildPrompt(pick, sec), sec);
                            if (!br.Ok)
                            {
                                Log.Note("自检 --genapicheck(" + provs[i] + ")：构造失败 —— " + br.Msg);
                                continue;
                            }
                            string outFile = Path.Combine(Path.GetTempPath(), "genapi-" + provs[i] + ".json");
                            try
                            {
                                File.WriteAllText(outFile, br.Body, new System.Text.UTF8Encoding(false));
                            }
                            catch (Exception ex) { Log.Note("!! 写不了 " + outFile + "：" + ex.Message); }
                            Log.Note("自检 --genapicheck(" + provs[i] + ")：url=" + br.Url
                                     + "｜extra=" + (br.Extra.Length == 0 ? "无" : br.Extra)
                                     + "｜图片JPEG=" + (br.ImageBytes / 1024) + "KB"
                                     + "｜body=" + br.Body.Length + " 字符 → " + outFile);
                        }
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--menutest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检：把「动态播放」子菜单原样打进日志。桌面只留两项（云端生成 / 回退静态），
                    // 这台机器前台焦点会被抢，点不到菜单，直接读菜单对象最可靠。
                    DispatcherTimer autoM = new DispatcherTimer();
                    autoM.Interval = TimeSpan.FromSeconds(3);
                    autoM.Tick += delegate
                    {
                        autoM.Stop();
                        WallpaperItem noVid = null, hasVid = null, aVideo = null;
                        for (int i = 0; i < Items.Count; i++)
                        {
                            WallpaperItem x = Items[i];
                            if (x.IsVideo) { if (aVideo == null) aVideo = x; }
                            else if (VideoFor(x) != null) { if (hasVid == null) hasVid = x; }
                            else { if (noVid == null) noVid = x; }
                        }
                        if (win != null)
                        {
                            if (noVid != null) win.LogMenuFor(noVid);
                            if (hasVid != null) win.LogMenuFor(hasVid);
                            if (aVideo != null) win.LogMenuFor(aVideo);
                        }
                    };
                    autoM.Start();
                    break;
                }
                if (string.Equals(s, "--settingstest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检：打开设置窗口并让应用自己渲染一张图，方便核对「云端生成」那段布局
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string p = Path.Combine(Path.GetTempPath(), "wallpaper-settings.png");
                        // 内容比窗口高，光截顶部看不到「云端生成」那段，第二个参数 bottom 滚到底再拍
                        string[] cl = Environment.GetCommandLineArgs();
                        Log.Note("自检 --settingstest：渲染设置窗口 → " + p);
                        SnapSettings(p, cl.Length > 2 && cl[2].Equals("bottom", StringComparison.OrdinalIgnoreCase));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--stopdynamic", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「停止并恢复静态壁纸」这条路（托盘那条菜单点完后走的就是它）。
                    // 判定标准：日志里 stop 之后不能再出现「动态壁纸启动」，配置里
                    // dynamicEnabled 必须是 false，桌面上要能看到静态壁纸。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(10);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        Log.Note("自检 --stopdynamic：stop 前 动态=" + Config.DynamicEnabled +
                                 " 播放器=" + Dynamic.Alive + " cursor=" + cursor);
                        StopDynamic(true);
                        Log.Note("自检 --stopdynamic：stop 后 动态=" + Config.DynamicEnabled +
                                 " 播放器=" + Dynamic.Alive);
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--searchtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「搜索 / 筛选 / 排序」：跑的是界面里真正在用的 FilterLocal，
                    // 不是另写一套逻辑，否则测过了界面上仍然可能不好使。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        if (win == null) { Log.Note("!! 自检 --searchtest：窗口还没建好"); return; }
                        string report = win.SelfTestSearch();
                        Log.Note(report);
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--gencmdtest", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "--cloudcfgtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-13 起脚本路线放弃，这个开关改验「设置 → 云端模型」：
                    // 真实操作控件，核对服务商联动、模型下拉、费用/等待、1080P 加价、配置往返。
                    // 保留 --gencmdtest 旧名字只是兼容已有回归脚本，日志统一写 --cloudcfgtest。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail = "× 未执行";
                        try
                        {
                            SettingsWindow sw = new SettingsWindow(this);
                            detail = sw.SelfTestCloudModels();
                            sw.Close();
                        }
                        catch (Exception ex) { detail = "× 异常 " + ex.Message; }
                        bool ok = detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0;
                        Log.Note("自检 --cloudcfgtest：\r\n" + detail);
                        Log.Note("自检 --cloudcfgtest：" + (ok ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--cloudflowtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 没有真 Key 也要证明「点击了能用」：本机模拟服务端 + 真 HTTP + 真文件落盘
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2.5);
                    auto.Tick += delegate { auto.Stop(); RunCloudFlowTest(); };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--videotest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：飘动视频主文件丢失但备份还在时，应用应自动恢复而不是让主人重新生成。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.VideoRecovery(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --videotest：\r\n" + detail);
                        Log.Note("自检 --videotest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--relatedvideotest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：主人说「只要关联文件夹里有相关视频就该播，别让我重新生成浪费钱」。
                    // 这里在隔离目录里逐种摆法验证「有片 → 零工单、零 HTTP 提交、直接播」。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.RelatedVideoGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --relatedvideotest：\r\n" + detail);
                        Log.Note("自检 --relatedvideotest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--orphantest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：主人「video 文件夹下有视频啊，怎么还要生成」——
                    // 专测没带 id 的云端原始出片也能被复用，且纯读不往库里写文件。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.OrphanGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --orphantest：\r\n" + detail);
                        Log.Note("自检 --orphantest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--clouddialogtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：主人「点完就一个生成中，还是假的」——
                    // 验没配 API 时必须弹窗说清缺什么，且不写工单、不报 Queued。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.CloudConfigGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --clouddialogtest：\r\n" + detail);
                        Log.Note("自检 --clouddialogtest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--contenttest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14 深夜：主人要求按画面区分「图片与视频是不是同一张」。
                    // 只读真实库，输出正 / 负样本与散片距离矩阵，用来标定指纹阈值。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.ContentGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --contenttest：\r\n" + detail);
                        Log.Note("自检 --contenttest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--badgetest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14 深夜：「切换了壁纸，上一个壁纸的动态视频还显示播放中」。
                    // 全隔离复现状态链，不设系统壁纸、不起播放器、不碰外部数据。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.SwitchGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --badgetest：\r\n" + detail);
                        Log.Note("自检 --badgetest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--launchtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-15：真跑一次播放器，确认「窗口没上屏就报失败」没把正常开播搞坏。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.LaunchGuard(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --launchtest：\r\n" + detail);
                        Log.Note("自检 --launchtest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--buddyhinttest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-15：验「WorkBuddy 免费模型」的界面口径与代出片指令，
                    // 并确认纯函数零下单、零请求、零文件写入。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.BuddyHint(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --buddyhinttest：\r\n" + detail);
                        Log.Note("自检 --buddyhinttest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--switchmotiontest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-15：载入索引误清动效标记，导致有片的图切换时不自动开播。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.SwitchMotion(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --switchmotiontest：\r\n" + detail);
                        Log.Note("自检 --switchmotiontest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--guardtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-16：功耗守卫判定、回收站删除、配置字段往返。
                    // 全隔离：临时文件只落在 TEMP（自检时已指向仓库内 .selftest-tmp）。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.GuardTest(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --guardtest：\r\n" + detail);
                        Log.Note("自检 --guardtest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--autogentest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-15：一键「点击生成 → 确认 → 自动出片自动播放」全链路自检：
                    // BuddyBridge 三态检测、不可用申请启动、一次确认自动提交、工单字段。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.AutoGenFlow(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --autogentest：\r\n" + detail);
                        Log.Note("自检 --autogentest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--importexporttest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：真点「导出配置…/导入配置…」按钮，验整条链路与零污染。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.ImportExport(this); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --importexporttest：\r\n" + detail);
                        Log.Note("自检 --importexporttest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--proxytest", StringComparison.OrdinalIgnoreCase))
                {
                    // 11-14：代理不只验解析，让 HttpWebRequest / WebClient 真的经本机代理走一趟。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        string detail;
                        try { detail = SelfTestExtra.Proxy(); }
                        catch (Exception ex)
                        {
                            detail = "× 异常 " + ex.GetType().Name + "：" + ex.Message + "  FAIL";
                        }
                        Log.Note("自检 --proxytest：\r\n" + detail);
                        Log.Note("自检 --proxytest："
                                 + (detail.IndexOf("FAIL") < 0 && detail.IndexOf("×") < 0 ? "PASS" : "FAIL"));
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--configtest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「配置导出/导入」和「代理」两条路。
                    // 不弹文件对话框（会抢前台、也没法自动点），直接验证底层那两条：
                    //   写出去 → 读回来 的字段是否一致；代理地址解析成不成立。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        bool ok = true;
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();

                        string tmp = Path.Combine(Path.GetTempPath(), "wallpaper-config-roundtrip.json");
                        try
                        {
                            AppConfig t = new AppConfig();
                            t.LibRoot = Config.LibRoot;
                            t.IntervalMinutes = 42;
                            t.Proxy = "127.0.0.1:7890";
                            t.CacheLimitMb = 321;
                            t.DynamicFps = 25;
                            t.FilterPortrait = true;

                            if (!ConfigStore.WriteTo(t, tmp))
                            {
                                ok = false;
                                sb.Append("自检 --configtest：写配置失败 ×\r\n");
                            }
                            else
                            {
                                AppConfig back = ConfigStore.LoadFrom(tmp);
                                bool same = back.IntervalMinutes == 42
                                         && back.CacheLimitMb == 321
                                         && back.DynamicFps == 25
                                         && back.FilterPortrait
                                         && back.Proxy == "127.0.0.1:7890"
                                         && string.Equals(back.LibRoot, t.LibRoot, StringComparison.OrdinalIgnoreCase);
                                if (!same) ok = false;
                                sb.Append("自检 --configtest：配置往返 ").Append(tmp)
                                  .Append(" 轮换=").Append(back.IntervalMinutes)
                                  .Append(" 缓存上限=").Append(back.CacheLimitMb)
                                  .Append(" fps=").Append(back.DynamicFps)
                                  .Append(" 代理=").Append(back.Proxy)
                                  .Append(same ? "  √" : "  ×").Append("\r\n");
                            }
                        }
                        catch (Exception ex)
                        {
                            ok = false;
                            sb.Append("自检 --configtest：往返异常 × ").Append(ex.Message).Append("\r\n");
                        }

                        // 代理：空串=直连，host:port 和带协议头都要成立，坏地址要安全退回直连
                        bool p1 = Net.Configure("");
                        bool direct = p1 && Net.Proxy == null && Net.Current == "";
                        bool p2 = Net.Configure("127.0.0.1:7890");
                        bool hostPort = p2 && Net.Proxy != null;
                        bool p3 = Net.Configure("http://127.0.0.1:8080");
                        bool withScheme = p3 && Net.Proxy != null;
                        bool p4 = Net.Configure("::::");
                        bool badSafe = (p4 && Net.Proxy == null) || (!p4 && Net.Proxy == null);
                        Net.Configure(Config.Proxy);     // 回到用户真实配置

                        bool proxyOk = direct && hostPort && withScheme && badSafe;
                        if (!proxyOk) ok = false;
                        sb.Append("自检 --configtest：代理 直连=").Append(direct)
                          .Append(" host:port=").Append(hostPort)
                          .Append(" 带协议=").Append(withScheme)
                          .Append(" 坏地址退回=").Append(badSafe)
                          .Append(" → 当前=").Append(Net.Current.Length == 0 ? "直连" : Net.Current)
                          .Append(proxyOk ? "  √" : "  ×").Append("\r\n");

                        sb.Append("自检 --configtest：").Append(ok ? "PASS" : "FAIL");
                        Log.Note(sb.ToString());
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--greettest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「每张壁纸都有贴题的欢迎语」：逐条打印 id → 实际会显示的那句，
                    // 没收录的标 ×；再验一次「未收录 id 必须退回按分类拼」不能出空；
                    // 最后把第一张按真实排版渲染成 PNG（--greettest <id> 可指定看哪张）。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        int own = 0;
                        for (int i = 0; i < Items.Count; i++)
                        {
                            WallpaperItem it = Items[i];
                            bool has = Greeting.HasPerId(it.Id);
                            if (has) own++;
                            sb.Append("自检 --greettest：").Append(has ? "√ " : "× ")
                              .Append(it.Id).Append("  ").Append(TagNames.Cn(it.Tag))
                              .Append("  「").Append(Greeting.Compose(it.Id, it.Tag, new Random(i * 7 + 3)))
                              .Append("」\r\n");
                        }

                        bool ok = Items.Count > 0 && own == Items.Count;
                        sb.Append("自检 --greettest：专属文案 ").Append(own).Append("/").Append(Items.Count)
                          .Append(ok ? "  √" : "  ×（有壁纸没写到，会退成通用句）").Append("\r\n");

                        string fb = Greeting.Compose("__not_in_library__", "city", new Random(1));
                        bool fbOk = !string.IsNullOrEmpty(fb) && fb.Contains("欢迎");
                        if (!fbOk) ok = false;
                        sb.Append("自检 --greettest：未收录 id 回退「").Append(fb).Append("」")
                          .Append(fbOk ? "  √" : "  ×").Append("\r\n");

                        // 生成器：新下载的壁纸没有手工文案，靠这三条兜住。
                        // 判定：句子有画面 + 有收尾（逗号），互不相同，长度可控。
                        string g1 = WelcomeGen.Compose("Rainy Neon Alley", "city", ColorTag.Cool, null, new Random(7));
                        string g2 = WelcomeGen.Compose("Snowy Forest Cabin", "nature", ColorTag.Bright, null, new Random(8));
                        string g3 = WelcomeGen.Compose("Untitled 2026", "local", ColorTag.Dark, null, new Random(9));
                        bool g1ok = g1 != null && g1.Contains("，") && g1.Length >= 6 && g1.Length <= 26;
                        bool g2ok = g2 != null && g2.Contains("，") && g2.Length >= 6 && g2.Length <= 26;
                        bool g3ok = g3 != null && g3.Contains("，") && g3.Length >= 6 && g3.Length <= 26;
                        bool genOk = g1ok && g2ok && g3ok && g1 != g2 && g2 != g3 && g1 != g3;
                        if (!genOk) ok = false;
                        sb.Append("自检 --greettest：生成器 霓虹「").Append(g1)
                          .Append("」 雪林「").Append(g2)
                          .Append("」 无关键词「").Append(g3).Append("」")
                          .Append(genOk ? "  √" : "  ×").Append("\r\n");

                        // 落盘往返：写 → 重新加载读得回 → 删掉 → 读不回来（模拟「下载一张新图」）
                        string probeId = "__greettest__";
                        string probeLine;
                        bool wrote = Greeting.Ensure(probeId, "Test Rainy Neon", "city", ColorTag.Cool, out probeLine);
                        bool seen = Greeting.Reload() && Greeting.HasPerId(probeId);
                        bool removed = Greeting.RemoveAuto(probeId) && Greeting.Reload() && !Greeting.HasPerId(probeId);
                        bool rtOk = wrote && seen && removed;
                        if (!rtOk) ok = false;
                        sb.Append("自检 --greettest：落盘往返 写=").Append(wrote)
                          .Append(" 重读=").Append(seen).Append(" 清理=").Append(removed)
                          .Append(" →「").Append(probeLine).Append("」")
                          .Append(rtOk ? "  √" : "  ×").Append("\r\n");

                        sb.Append("自检 --greettest：").Append(ok ? "PASS" : "FAIL");
                        Log.Note(sb.ToString());

                        string[] cmd = Environment.GetCommandLineArgs();
                        string want = cmd.Length > 2 ? cmd[2] : null;
                        WallpaperItem pick = null;
                        for (int i = 0; i < Items.Count; i++)
                        {
                            bool hit = want == null
                                ? i == 0
                                : string.Equals(Items[i].Id, want, StringComparison.OrdinalIgnoreCase);
                            if (hit) { pick = Items[i]; break; }
                        }
                        if (pick == null && Items.Count > 0) pick = Items[0];
                        if (pick != null)
                        {
                            string text = Greeting.Compose(pick.Id, pick.Tag, rnd);
                            string sub = TagNames.Cn(pick.Tag) + " · " + KindOf(pick)
                                       + "　·　" + Clip(pick.Title, 26);
                            SnapGreeting(Path.Combine(Path.GetTempPath(), "wallpaper-greeting.png"), text, sub);
                        }
                    };
                    auto.Start();
                    break;
                }
                if (string.Equals(s, "--deltest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「删壁纸时欢迎语跟着走」：造一张临时图 → 入库自动补文案 →
                    // 走界面删除按钮真正调用的 DeleteItem → 三条都得干净：
                    // 磁盘没文件、列表没条目、auto 文件里没这句。用完不留痕。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();

                        string id = "zz-del-test";
                        string imgDir = Path.Combine(Config.LibRoot, "images");
                        string dst = Path.Combine(imgDir, id + ".jpg");
                        string src = null;
                        try
                        {
                            if (Directory.Exists(imgDir))
                            {
                                string[] fs = Directory.GetFiles(imgDir, "*.jpg");
                                if (fs.Length > 0) src = fs[0];
                            }
                        }
                        catch { }

                        bool made = false;
                        if (src != null)
                        {
                            try { File.Copy(src, dst, true); made = true; }
                            catch (Exception ex) { Log.Note("自检 --deltest：造测试图失败 " + ex.Message); }
                        }

                        ReloadLibrary();
                        WallpaperItem probe = null;
                        for (int i = 0; i < Items.Count; i++)
                            if (string.Equals(Items[i].Id, id, StringComparison.OrdinalIgnoreCase)) { probe = Items[i]; break; }

                        bool indexed = probe != null;
                        bool hadLine = probe != null && Greeting.HasPerId(id);

                        if (probe != null) DeleteItem(probe);

                        bool fileGone = !File.Exists(dst);
                        bool listGone = true;
                        for (int i = 0; i < Items.Count; i++)
                            if (string.Equals(Items[i].Id, id, StringComparison.OrdinalIgnoreCase)) listGone = false;
                        bool lineGone = !Greeting.HasPerId(id);

                        bool ok = made && indexed && hadLine && fileGone && listGone && lineGone;
                        Log.Note("自检 --deltest：造图=" + made + " 入库=" + indexed + " 有文案=" + hadLine
                                 + " 文件已删=" + fileGone + " 列表已移=" + listGone + " 文案已清=" + lineGone
                                 + (ok ? "  √ PASS" : "  × FAIL"));
                    };
                    auto.Start();
                    break;
                }

                if (string.Equals(s, "--arrivetest", StringComparison.OrdinalIgnoreCase))
                {
                    // 自检「片子到货 → 浮条弹提示」这条：拿一张临时图下单，再把一段现成 mp4
                    // 按约定名丢进 videos\，看管理器会不会自己收货、浮条会不会变绿。
                    // 全程用 zz-arrive-test 这个假 id，跑完把图、片子、工单一起清掉。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(3);
                    auto.Tick += delegate
                    {
                        auto.Stop();

                        string id = "zz-arrive-test";
                        string imgDir = Path.Combine(Config.LibRoot, "images");
                        string img = Path.Combine(imgDir, id + ".jpg");
                        string vid = CloudGen.VideoPath(Config.LibRoot, id);
                        string shot = Path.Combine(Path.GetTempPath(), "wallpaper-cloud-arrive.png");

                        try
                        {
                            string[] fs = Directory.GetFiles(imgDir, "*.jpg");
                            if (fs.Length > 0) File.Copy(fs[0], img, true);
                        }
                        catch (Exception ex) { Log.Note("自检 --arrivetest：造图失败 " + ex.Message); }

                        ReloadLibrary();
                        WallpaperItem probe = FindById(id);
                        if (probe == null) { Log.Note("自检 --arrivetest：× 临时图没进库"); return; }

                        // 只写工单，不走 QueueCloudGen：那条会把这单设成「待换上」，
                        // 收货时就会真把主人的桌面切到这张测试片上。这里只要验收货链路。
                        string msg;
                        bool queued = CloudGen.Enqueue(Config, probe, LocalImageFor(probe), out msg);
                        Log.Note("自检 --arrivetest：下单=" + queued + "  " + msg);

                        // 1.5 秒后模拟「外部生成器出片」：按 videos\<id>.mp4 的约定丢进去
                        DispatcherTimer drop = new DispatcherTimer();
                        drop.Interval = TimeSpan.FromMilliseconds(1500);
                        drop.Tick += delegate
                        {
                            drop.Stop();
                            try
                            {
                                string src = null;
                                string[] vs = Directory.GetFiles(Path.Combine(Config.LibRoot, "videos"), "*.mp4");
                                long best = long.MaxValue;
                                for (int i = 0; i < vs.Length; i++)
                                {
                                    long len = new FileInfo(vs[i]).Length;
                                    if (len < best) { best = len; src = vs[i]; }
                                }
                                if (src != null) File.Copy(src, vid, true);
                            }
                            catch (Exception ex) { Log.Note("自检 --arrivetest：投放片子失败 " + ex.Message); }

                            // 收货循环 3 秒一轮，等它扫到。
                            // 11-13：整条自检必须在外部 12 秒超时之前跑完（含清理），
                            // 否则进程被强杀会在库里留下 zz-arrive-test 这条假卡片。
                            DispatcherTimer wait = new DispatcherTimer();
                            wait.Interval = TimeSpan.FromSeconds(4);
                            wait.Tick += delegate
                            {
                                wait.Stop();
                                WallpaperItem now = FindById(id);
                                bool ready = now != null && CloudGen.StateOf(Config, now) == CloudGen.State.Ready;
                                bool doneFile = File.Exists(Path.Combine(CloudGen.DoneDir, id + ".json"));
                                bool queueCleared = !File.Exists(CloudGen.JobPath(id));

                                Log.Note("自检 --arrivetest：验收 有片=" + ready + " 工单归档=" + doneFile
                                         + " 队列已清=" + queueCleared
                                         + ((ready && doneFile && queueCleared) ? "  √ PASS" : "  × FAIL"));

                                SnapMain(shot);

                                // 收尾：把这次测试留下的东西全撤掉（半秒后即可，别拖到超时之后）
                                DispatcherTimer clean = new DispatcherTimer();
                                clean.Interval = TimeSpan.FromMilliseconds(500);
                                clean.Tick += delegate
                                {
                                    clean.Stop();
                                    try
                                    {
                                        // 走 LibraryStore.Delete：收货时 SetMotion 会往 index.json
                                        // 补一条，光删文件会让库里留个空壳卡片。
                                        WallpaperItem cur2 = FindById(id);
                                        if (cur2 != null)
                                        {
                                            string derr;
                                            LibraryStore.Delete(cur2, Config.LibRoot, out derr);
                                        }
                                        if (File.Exists(vid)) File.Delete(vid);
                                        if (File.Exists(img)) File.Delete(img);
                                        string[] marks = new string[]
                                        {
                                            CloudGen.JobPath(id),
                                            Path.Combine(CloudGen.DoneDir, id + ".json"),
                                            Path.Combine(CloudGen.FailDir, id + ".json")
                                        };
                                        for (int i = 0; i < marks.Length; i++)
                                            if (File.Exists(marks[i])) File.Delete(marks[i]);
                                        if (Config.PendingCloudId == id)
                                        {
                                            Config.PendingCloudId = "";
                                            ConfigStore.Save(Config);
                                        }
                                    }
                                    catch (Exception ex) { Log.Note("自检 --arrivetest：清理失败 " + ex.Message); }
                                    Log.Note("自检 --arrivetest：已清理临时图 / 测试片 / 工单");
                                };
                                clean.Start();
                            };
                            wait.Start();
                        };
                        drop.Start();
                    };
                    auto.Start();
                    break;
                }

                if (string.Equals(s, "--uitest", StringComparison.OrdinalIgnoreCase))
                {
                    // 把主窗口自己渲染成 PNG。这台机器上外部截屏老抓到编辑器的窗口，
                    // RenderTargetBitmap 是应用自己画的，窗口被挡住也能出图 —— 用来核对布局。
                    DispatcherTimer auto = new DispatcherTimer();
                    auto.Interval = TimeSpan.FromSeconds(2);
                    auto.Tick += delegate
                    {
                        auto.Stop();
                        // 可选第二个参数：all / video / static / motion（切筛选），或 q:关键词（走搜索框）
                        string[] cmd = Environment.GetCommandLineArgs();
                        string shot = Path.Combine(Path.GetTempPath(), "wallpaper-ui.png");
                        if (cmd.Length > 2 && win != null)
                        {
                            if (cmd[2].StartsWith("q:", StringComparison.OrdinalIgnoreCase))
                            {
                                string kw = cmd[2].Substring(2);
                                win.SetSearchForTest(kw);
                                Log.Note("自检 --uitest：搜索「" + kw + "」（走真实输入链路）");
                                // 打字是防抖的，等它刷完再截图
                                DispatcherTimer wait = new DispatcherTimer();
                                wait.Interval = TimeSpan.FromMilliseconds(500);
                                wait.Tick += delegate { wait.Stop(); SnapMain(shot); };
                                wait.Start();
                                return;
                            }
                            if (cmd[2].Equals("cloud", StringComparison.OrdinalIgnoreCase))
                            {
                                // 渲染右下角那条云端浮条（排队态），用来核对文案与排布
                                CloudSnap sn = CloudSnapshot();
                                win.CloudQueued(sn.Id);
                                Log.Note("自检 --uitest：云端浮条 排队=" + sn.Pending
                                         + " 代表=" + sn.Id + " 已等=" + sn.WaitSeconds + " 秒"
                                         + " 通道=" + sn.Channel);
                                DispatcherTimer w2 = new DispatcherTimer();
                                w2.Interval = TimeSpan.FromMilliseconds(600);
                                w2.Tick += delegate { w2.Stop(); SnapMain(shot); };
                                w2.Start();
                                return;
                            }
                            win.SetTypeFilter(cmd[2]);
                            Log.Note("自检 --uitest：类型筛选 = " + cmd[2]);
                        }
                        Log.Note("自检 --uitest：渲染主窗口 → " + shot);
                        SnapMain(shot);
                    };
                    auto.Start();
                    break;
                }
            }
        }

        // ---------------- 动态壁纸看门狗 ----------------

        DispatcherTimer watchdog;
        int watchRestarts;

        /// <summary>
        /// 动态壁纸会莫名其妙掉：换一张壁纸、Explorer 重绘桌面、播放器自己崩，
        /// 都可能让桌面层里的那个子窗口消失。用户看到的就是「动态背景突然没了」。
        /// 这里每 4 秒看一眼进程和窗口，不在就按上次的视频重新拉起来，
        /// 用户不需要自己去点一次「重新应用」。
        /// </summary>
        void StartWatchdog()
        {
            // 播放器一旦退出就立刻补上：崩溃恢复不用等看门狗那一轮（否则桌面会空 4 秒）
            Dynamic.Died += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (ReallyExit || !Config.DynamicEnabled) return;
                    if (Environment.TickCount < watchdogReadyAt) return;
                    if (Dynamic.Alive) return;              // 已经有人（比如看门狗）拉起来了
                    Log.Note("检测到播放器退出，立即恢复动态壁纸");
                    RestartPlayer();
                }));
            };

            watchdogReadyAt = Environment.TickCount + 8000;   // 先给启动流程 8 秒，别抢着开播
            watchdog = new DispatcherTimer();
            watchdog.Interval = TimeSpan.FromSeconds(4);
            watchdog.Tick += delegate { WatchdogTick(); };
            watchdog.Start();

            // 云端动效：每 3 秒看一眼工单有没有变成片子。和看门狗分开，动态关掉时也要收货。
            DispatcherTimer cloud = new DispatcherTimer();
            cloud.Interval = TimeSpan.FromSeconds(3);
            cloud.Tick += delegate { SweepCloudJobs(); };
            cloud.Start();
            CloudGen.EnsureDirs();
        }

        int watchdogReadyAt;

        /// <summary>连续判定「窗口不在」的次数。启动/换台瞬间窗口还没建好，
        /// 单次判定不可信，连续两轮（约 8 秒）都不在才真的重启。</summary>
        int watchMisses;

        void WatchdogTick()
        {
            if (ReallyExit) return;
            // v0.9.1：每轮把角标跟真实播放器进程对一次齐。哪怕哪条路径漏了
            // 收口，最多 4 秒「播放中」也会自己消失，不会再定格在上一张卡片上。
            if (win != null) win.RefreshBadges();
            if (Environment.TickCount < watchdogReadyAt) return;
            if (!Config.DynamicEnabled) return;          // 用户主动停了就不复活
            if (SelfTestArgs()) return;                  // 自检进程不自动复活真实桌面

            // 刚启动/刚换台：播放器从 process start 到首帧上屏要 250~400ms，
            // 这段时间进程在、窗口还没挂上。旧代码正好在这一刻判定「掉了」，
            // 于是又补一个播放器 —— 换一张壁纸起两次播放器，桌面就会闪两下。
            if (Environment.TickCount - Dynamic.LastLaunchTick < 4000)
            {
                watchMisses = 0;
                return;
            }

            bool alive = Dynamic.Alive;
            bool windowOk = alive && Dynamic.WindowAlive;
            if (windowOk) { watchRestarts = 0; watchMisses = 0; return; }

            if (++watchMisses < 2) return;               // 先记一次，下一轮还不在于动手

            // 连续重启 5 次还回不来，说明不是"掉一下"，是真起不来，别无限刷
            if (watchRestarts >= 5)
            {
                StopPlayback();
                MarkStopped();
                Log.Note("看门狗：连续 5 次重启都失败，先关掉动态壁纸（可在托盘里重新开）");
                if (win != null) win.SetStatus("动态壁纸反复掉线，已自动关闭");
                return;
            }

            Log.Note("看门狗：动态壁纸不见了（进程=" + alive + " 窗口=" + windowOk + "）");
            watchMisses = 0;
            RestartPlayer();
        }

        /// <summary>按上次播的视频重新拉起播放器。看门狗和"进程退出"回调共用这一处</summary>
        void RestartPlayer()
        {
            if (SelfTestArgs()) return;
            WallpaperItem target = null;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Id == Config.LastDynamicId && MotionCapable(Items[i])) { target = Items[i]; break; }
            }
            if (target == null && cursor >= 0 && cursor < Items.Count && MotionCapable(Items[cursor]))
                target = Items[cursor];
            if (target == null) return;

            watchRestarts++;
            Log.Note("恢复动态壁纸（第 " + watchRestarts + " 次）：" + target.Title);

            if (!SelfTestNoPlayerOps && Dynamic.Alive) Dynamic.Stop();   // 进程在但窗口没了，先收干净再起
            string err;
            if (!StartMotion(target, out err))
            {
                Log.Note("恢复失败：" + err);
                // 恢复不起来时别让界面继续挂着「播放中」：刷一次角标，
                // 它会按播放器进程是否真的活着把假状态收掉。
                if (win != null) win.RefreshBadges();
            }
        }

        public void ReloadLibrary()
        {
            Items = LibraryStore.Load(Config.LibRoot);
            cursor = -1;

            // 图片是扫描目录收进来的（不写索引），拖拽导入也只在本地复制一份，
            // 这两条路不经过 AppendIndex，所以在这里统一兜一次：谁没有专属文案就现写。
            int added = Greeting.Backfill(Items);
            if (added > 0)
                Log.Note("欢迎语：为 " + added + " 张新壁纸自动补写（新文件 " + Greeting.AutoPath + "）");
        }

        void RestoreLastWallpaper()
        {
            if (Items.Count == 0) return;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Id == Config.LastId)
                {
                    cursor = i;
                    win.SetCurrent(Items[i]);
                    return;
                }
            }
        }

        /// <summary>上次退出时如果开着动态壁纸，这次启动接着播</summary>
        void RestoreDynamic()        {
            if (SelfTestArgs()) return;          // 自检进程不接管用户桌面
            if (!Config.DynamicEnabled || string.IsNullOrEmpty(Config.LastDynamicId)) return;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Id != Config.LastDynamicId) continue;
                if (!MotionCapable(Items[i])) return;
                string err;
                if (!StartMotion(Items[i], out err)) Log.Note("恢复动态壁纸失败：" + err);
                return;
            }
        }

        /// <summary>直接拿库里的第一个视频试播（--dynvideo，方便一键验证动态壁纸链路）</summary>
        public void StartFirstVideo()
        {
            // 启动时 RestoreDynamic 可能已经把上次的视频拉起来了，
            // 这里再起一次会白白闪一下黑（播放器要重建窗口）。
            if (Dynamic.WindowAlive)
            {
                Log.Note("--dynvideo：动态壁纸已经在播，跳过重复启动");
                return;
            }
            for (int i = 0; i < Items.Count; i++)
            {
                if (!Items[i].IsVideo) continue;
                StartDynamic(Items[i]);
                return;
            }
            if (win != null) win.SetStatus("壁纸库里没有视频，先导入一个 mp4 再试动态壁纸");
            Log.Note("--dynvideo：库里没有视频文件");
        }

        // ---------------- 会话监听：开机 + 解锁播欢迎 ----------------

        void HookSession()
        {
            try
            {
                sessionHwnd = new WindowInteropHelper(win).Handle;
                if (sessionHwnd == IntPtr.Zero) return;

                wtsRegistered = WTSRegisterSessionNotification(sessionHwnd, NOTIFY_FOR_THIS_SESSION);
                Log.Note("会话监听注册=" + wtsRegistered);

                HwndSource src = HwndSource.FromHwnd(sessionHwnd);
                if (src != null) src.AddHook(SessionProc);
            }
            catch (Exception ex)
            {
                Log.Note("会话监听失败：" + ex.Message);
            }
        }

        IntPtr SessionProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WTSSESSION_CHANGE && wParam.ToInt32() == WTS_SESSION_UNLOCK)
            {
                Log.Note("检测到解锁事件");
                if (Config.WelcomeOnUnlock)
                {
                    // 解锁瞬间系统还在收锁屏，稍等一下再淡入
                    DispatcherTimer delay = new DispatcherTimer();
                    delay.Interval = TimeSpan.FromMilliseconds(450);
                    delay.Tick += delegate { delay.Stop(); ShowGreeting(); };
                    delay.Start();
                }
            }
            return IntPtr.Zero;
        }

        // ---------------- 托盘 ----------------
        void SetupTray()
        {
            tray = new Forms.NotifyIcon();
            tray.Text = "壁纸管家";
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "app.ico");
                tray.Icon = File.Exists(ico) ? new Drawing.Icon(ico) : Drawing.SystemIcons.Application;
            }
            catch { tray.Icon = Drawing.SystemIcons.Application; }
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMain(); };

            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();

            // 第一行只显示状态，点不动：托盘里一眼能看出现在是「动态」还是「静态」，
            // 省得点了菜单却不知道生效没生效。
            Forms.ToolStripMenuItem miState = new Forms.ToolStripMenuItem("当前：—");
            miState.Enabled = false;

            Forms.ToolStripMenuItem miShow = new Forms.ToolStripMenuItem("打开主界面");
            miShow.Click += delegate { Log.Note("托盘：打开主界面"); ShowMain(); };

            Forms.ToolStripMenuItem miNext = new Forms.ToolStripMenuItem("换一张");
            miNext.Click += delegate { Log.Note("托盘：换一张"); RotateNext(); };

            // 桌面被系统/其他软件冲掉时，一键把当前这张重新贴回去（静态图或动态项都行）
            Forms.ToolStripMenuItem miReapply = new Forms.ToolStripMenuItem("重新应用当前壁纸");
            miReapply.Click += delegate
            {
                if (cursor < 0 || cursor >= Items.Count)
                {
                    if (win != null) win.SetStatus("还没有当前壁纸，先在图库里选一张");
                    return;
                }
                Log.Note("托盘：重新应用当前壁纸 " + Items[cursor].Title);
                ApplyItem(Items[cursor]);
            };

            Forms.ToolStripMenuItem miRotate = new Forms.ToolStripMenuItem("暂停定时轮换");
            miRotate.Click += delegate
            {
                Config.AutoRotate = !Config.AutoRotate;
                ConfigStore.Save(Config);
                miRotate.Text = Config.AutoRotate ? "暂停定时轮换" : "继续定时轮换";
                Log.Note("托盘：定时轮换 = " + Config.AutoRotate);
                if (win != null) win.SetStatus(Config.AutoRotate ? "已恢复定时轮换" : "已暂停定时轮换");
            };

            // 动态壁纸
            Forms.ToolStripMenuItem miDyn = new Forms.ToolStripMenuItem("动态壁纸");
            Forms.ToolStripMenuItem miDynProbe = new Forms.ToolStripMenuItem("跑一次可见性探测（6 秒）");
            miDynProbe.Click += delegate { RunProbe(); };

            Forms.ToolStripMenuItem miDynPause = new Forms.ToolStripMenuItem("暂停播放");
            miDynPause.Click += delegate
            {
                Dynamic.Pause();
                Log.Note("托盘：暂停动态壁纸");
                if (win != null) win.SetStatus("动态壁纸已暂停");
            };

            Forms.ToolStripMenuItem miDynResume = new Forms.ToolStripMenuItem("继续播放");
            miDynResume.Click += delegate
            {
                Dynamic.Resume();
                Log.Note("托盘：继续播放动态壁纸");
                if (win != null) win.SetStatus("动态壁纸已继续");
            };

            Forms.ToolStripMenuItem miDynStop = new Forms.ToolStripMenuItem("停止并恢复静态壁纸");
            miDynStop.Click += delegate { Log.Note("托盘：停止并恢复静态壁纸"); StopDynamic(true); };

            // 挂载层级：24H2 桌面改造后，"视频画在哪一层"必须可切。
            // 11:xx 一整轮实测的结论（别再重复踩）：
            //   0/1/2/3 —— Progman 的子窗口，ULW 提交成功但一个像素都不合成（Progman 是
            //              WS_EX_NOREDIRECTIONBITMAP，子树没有可合成表面），屏幕完全没反应；
            //   4      —— 同上，先置顶再压图标层，也没用；
            //   5      —— 顶层窗口插到 Progman 之后：能上屏，但落在 Progman 之上，桌面图标被整个盖住；
            //   6      —— 顶层窗口 HWND_BOTTOM：沉到桌面之下，完全不合成；
            //   7      —— SetWindowBand(ZBID_DESKTOP)：被拒（err=5），无效；
            //   8      —— 微软 24H2 官方口径：Progman 的 WS_EX_LAYERED 子窗口 +
            //              SetLayeredWindowAttributes(alpha=255) + GDI 直画，Z 序卡在
            //              DefView 之下、WorkerW 之上。**视频铺满桌面且图标完好的唯一档位。**
            Forms.ToolStripMenuItem miLayer = new Forms.ToolStripMenuItem("桌面层级");
            int[] layerModes = { 8, 1, 5, 6 };
            string[] layerNames =
            {
                "壁纸位 · 图标可见（默认，官方口径）",
                "图标之上（能上屏，会盖住图标）",
                "顶层窗口 · 排在桌面之后（同盖图标）",
                "顶层窗口 · HWND_BOTTOM（不上屏）"
            };
            Forms.ToolStripMenuItem[] layerItems = new Forms.ToolStripMenuItem[layerModes.Length];
            for (int li = 0; li < layerModes.Length; li++)
            {
                int idx = layerModes[li];
                Forms.ToolStripMenuItem it = new Forms.ToolStripMenuItem(layerNames[li]);
                it.Checked = (Config.MountMode == idx);
                it.Click += delegate
                {
                    SetMountMode(idx);
                    for (int k = 0; k < layerItems.Length; k++)
                        if (layerItems[k] != null) layerItems[k].Checked = (k == li);
                };
                layerItems[li] = it;
                miLayer.DropDownItems.Add(it);
            }

            miDyn.DropDownItems.Add(miDynPause);
            miDyn.DropDownItems.Add(miDynResume);
            miDyn.DropDownItems.Add(miDynStop);
            miDyn.DropDownItems.Add(new Forms.ToolStripSeparator());
            miDyn.DropDownItems.Add(miLayer);
            miDyn.DropDownItems.Add(miDynProbe);

            Forms.ToolStripMenuItem miGreet = new Forms.ToolStripMenuItem("预览欢迎动画");
            miGreet.Click += delegate { ShowGreeting(); };

            Forms.ToolStripMenuItem miSettings = new Forms.ToolStripMenuItem("设置…");
            miSettings.Click += delegate { OpenSettings(); };

            // 11-16：日志一直在 %TEMP%，界面没有入口，出问题只能人工翻目录。
            Forms.ToolStripMenuItem miLog = new Forms.ToolStripMenuItem("打开日志");
            miLog.Click += delegate { OpenLog(); };

            Forms.ToolStripMenuItem miExit = new Forms.ToolStripMenuItem("退出");
            miExit.Click += delegate { Log.Note("托盘：退出"); ExitApp(); };

            // 菜单打开时按真实配置刷新文案/可用状态，不用自己记上次点了什么
            menu.Opening += delegate
            {
                WallpaperItem cur = cursor >= 0 && cursor < Items.Count ? Items[cursor] : null;
                // 菜单文案也按「播放器真的在跑」来，别拿残留配置骗人
                bool dynNow = IsPlayingNow();
                miState.Text = "当前：" + (dynNow
                    ? "动态播放 " + (cur == null ? "" : cur.Title)
                    : (cur == null ? "没有壁纸" : "静态壁纸 " + cur.Title));
                miRotate.Text = Config.AutoRotate ? "暂停定时轮换" : "继续定时轮换";
                miDyn.Text = dynNow ? "动态壁纸（播放中）" : "动态壁纸";
                miDynPause.Enabled = dynNow;
                miDynResume.Enabled = dynNow;
                miDynStop.Enabled = dynNow || (cur != null && cur.Motion);
                for (int k = 0; k < layerItems.Length; k++)
                    if (layerItems[k] != null) layerItems[k].Checked = (layerModes[k] == Config.MountMode);
            };

            menu.Items.Add(miState);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(miShow);
            menu.Items.Add(miNext);
            menu.Items.Add(miReapply);
            menu.Items.Add(miRotate);
            menu.Items.Add(miDyn);
            menu.Items.Add(miGreet);
            menu.Items.Add(miSettings);
            menu.Items.Add(miLog);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(miExit);
            tray.ContextMenuStrip = menu;
            UpdateTrayTip();
        }

        /// <summary>托盘图标的悬浮提示跟着当前状态走，鼠标放上去就知道现在是动态还是静态</summary>
        public void UpdateTrayTip()
        {
            if (tray == null) return;
            string s = "壁纸管家";
            if (IsPlayingNow()) s += " · 动态播放中（点右键可停止）";
            else if (cursor >= 0 && cursor < Items.Count) s += " · 静态壁纸：" + Items[cursor].Title;
            if (s.Length > 63) s = s.Substring(0, 63);      // NotifyIcon.Text 上限
            try { tray.Text = s; } catch { }
        }

        /// <summary>
        /// 打开运行日志。日志由 Log 按天归档、只留 7 天，所以「今天的文件不在」
        /// 时退而打开它所在的目录，而不是弹一句「找不到」什么都不给。
        /// </summary>
        public void OpenLog()
        {
            try
            {
                string p = Log.Path0;
                if (File.Exists(p))
                {
                    Process.Start("notepad.exe", "\"" + p + "\"");
                    return;
                }
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                else if (win != null) win.SetStatus("还没生成过日志文件");
            }
            catch (Exception ex)
            {
                if (win != null) win.SetStatus("打开日志失败：" + ex.Message);
            }
        }

        /// <summary>跑播放器自检，结果直接弹窗（不用你去翻日志）</summary>
        public void RunProbe()
        {
            if (win != null) win.SetStatus("正在一次跑完全部 9 种桌面层方案，约 25-40 秒（桌面会短暂出现棋盘格，跑完自动恢复）…");
            Task.Run(delegate
            {
                // 20 秒预算 → 管理器超时 60 秒、播放器看门狗 50 秒，够跑完 9 组
                string result = Dynamic.Probe(20);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (win != null) win.SetStatus("探测完成，结果见弹窗");
                    MessageBox.Show(result, "动态壁纸可见性探测", MessageBoxButton.OK, MessageBoxImage.Information);
                }));
            });
        }

        /// <summary>桌面层探测：每个变体都截图，结果看图说话（截图在 %TEMP%\wp3）</summary>
        public void RunProbe2()
        {
            // 把自己最小化：管理器窗口会挡住桌面中间，截图判定容易被干扰
            if (win != null)
            {
                win.WindowState = WindowState.Minimized;
                win.SetStatus("正在探测桌面层（2 组对照 + 4 个变体，每个都截图），约 20 秒…");
            }
            Task.Run(delegate
            {
                string result = Dynamic.Probe2();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (win != null)
                    {
                        win.WindowState = WindowState.Normal;
                        win.Activate();
                        win.SetStatus("桌面层探测完成");
                    }
                    MessageBox.Show(result + "\r\n\r\n截图目录：" +
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wp3"),
                        "桌面层探测 v4", MessageBoxButton.OK, MessageBoxImage.Information);
                }));
            });
        }

        // ---------------- 播放状态唯一收口（v0.9.1） ----------------

        /// <summary>播放器进程是否真的活着；自检时可以注入，避免杀掉用户正在播的壁纸。</summary>
        public bool PlayingNow()
        {
            if (SelfTestAliveOverride == 0) return false;
            if (SelfTestAliveOverride == 1) return true;
            return Dynamic.Alive;
        }

        /// <summary>「动态壁纸现在真的在桌面上」。配置残留但进程没了，就不算在播。</summary>
        public bool IsPlayingNow()
        {
            return Config.DynamicEnabled && PlayingNow();
        }

        /// <summary>这张是不是正在铺在桌面上。除了配置，还必须播放器真的活着。</summary>
        public bool IsPlaying(WallpaperItem it, bool alive)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) return false;
            return Config.DynamicEnabled
                   && !string.IsNullOrEmpty(Config.LastDynamicId)
                   && Config.LastDynamicId == it.Id
                   && alive;
        }

        /// <summary>开播成功的唯一入口：状态、光标、界面、托盘一次对齐。</summary>
        public void MarkPlaying(WallpaperItem it)
        {
            if (it == null) return;
            Config.DynamicEnabled = true;
            Config.LastDynamicId = it.Id;
            Config.LastId = it.Id;          // 下次启动恢复的是「正在播的这个视频」
            cursor = IndexOf(it);
            ConfigStore.Save(Config);
            if (win != null)
            {
                win.SetCurrent(it);
                win.RefreshBadges();
            }
            UpdateTrayTip();
        }

        /// <summary>
        /// 一切「不再播动态」的路径都走这里：关总闸、清掉上次那个 id、刷角标和托盘。
        /// 以前只把 DynamicEnabled 置 false，LastDynamicId 还留着上一张 ——
        /// 看门狗或下次启动一旦把总闸打开，旧片就被拉回来了。
        /// </summary>
        public bool MarkStopped()
        {
            bool changed = Config.DynamicEnabled || !string.IsNullOrEmpty(Config.LastDynamicId);
            Config.DynamicEnabled = false;
            Config.LastDynamicId = "";
            if (changed) ConfigStore.Save(Config);
            if (win != null) win.RefreshBadges();
            UpdateTrayTip();
            return changed;
        }

        /// <summary>收播放器。隔离自检时统一跳过，不碰真实桌面进程。</summary>
        void StopPlayback()
        {
            if (SelfTestNoPlayerOps) return;
            Dynamic.Stop();
        }

        public MainWindow SelfTestWindow { get { return win; } }
        public string SelfTestTrayText()
        {
            try { return tray == null ? "" : (tray.Text ?? ""); }
            catch { return ""; }
        }

        public void StopDynamic(bool backToStatic)
        {
            Log.Note("停止动态壁纸（恢复静态=" + backToStatic + "）");

            if (backToStatic && cursor >= 0 && cursor < Items.Count)
            {
                WallpaperItem cur = Items[cursor];

                // 只停「现在」，不动这张图的动效标记：标记是用户的显式选择，
                // 要永久改回静态请走卡片右键的「改回静态壁纸」。
                // 关键是下面这个 false —— 以前这里调 ApplyItem(cur)，它一看到
                // Motion=true 又把播放器拉起来，日志里 stop 之后 60ms 就多了一条
                // 「动态壁纸启动」，用户点「停止并恢复静态壁纸」等于毫无反应。
                //
                // 顺序也变了：不再先 Dynamic.Stop()，而是让 DoSet 先把系统壁纸
                // 换成这张，再收播放器 —— 先停的话桌面会空一下、露出上一张系统壁纸。
                MarkStopped();              // 先关闸，别让看门狗在 DoSet 之前把旧片拉回来
                ApplyItem(cur, false);      // false = 这次必须静态，不许再自动开播
                if (win != null) win.SetStatus("动态壁纸已停止，已恢复静态壁纸：" + cur.Title);
            }
            else
            {
                StopPlayback();
                MarkStopped();
                if (win != null) win.SetStatus("动态壁纸已停止");
            }
            UpdateTrayTip();
        }

        /// <summary>各挂载档位的中文名（下标 = 档位号，与 WallpaperPlayer 的 mountMode 一一对应）</summary>
        static readonly string[] MountNames =
        {
            "DefView 最底层（已证不可见）",
            "图标之上（Progman 最上层，会盖住图标）",
            "Progman 内 · DefView 之下（实测不上屏）",
            "WorkerW 最上层（实测不上屏）",
            "先置顶再压图标层（实测不上屏）",
            "顶层窗口 · 排在桌面之后（会盖住图标）",
            "顶层窗口 · HWND_BOTTOM（不上屏）",
            "顶层窗口 · ZBID_DESKTOP band（被系统拒绝）",
            "壁纸位 · 图标可见（默认，官方口径）"
        };

        /// <summary>切换动态壁纸挂载层级，并立刻用新层级重启播放器</summary>
        public void SetMountMode(int m)
        {
            // 以前这里写的是 m > 4，把 8（真正能用的默认档）直接挡在门外，
            // 于是托盘里点「桌面层级」任何一项都没反应。
            if (m < 0 || m >= MountNames.Length) return;
            string[] names = MountNames;

            Config.MountMode = m;
            ConfigStore.Save(Config);
            Dynamic.MountMode = m;
            Log.Note("切换挂载层级 = " + m + "（" + names[m] + "）");
            if (win != null) win.SetStatus("动态壁纸层级已切到「" + names[m] + "」，正在重启播放器…");

            if (Config.DynamicEnabled && cursor >= 0 && cursor < Items.Count && MotionCapable(Items[cursor]))
                StartDynamic(Items[cursor]);
        }

        public void StartDynamic(WallpaperItem it)        {
            if (it == null) return;
            string err;
            if (StartMotion(it, out err))
            {
                MarkPlaying(it);
                if (win != null)
                {
                    string what = it.IsVideo ? "（" + Config.DynamicFps + "fps 上限）" : "（云端飘动视频）";
                    win.SetStatus("动态壁纸已开始：" + it.Title + what);
                }
            }
            else
            {
                // v0.9.1：换台失败绝不能留着上一张的视频继续播、角标继续写「播放中」。
                // 以前这里只弹一句状态栏，DynamicEnabled/LastDynamicId/播放器全都不动，
                // 用户切了壁纸看到的还是上一张在「播放中」。
                StopPlayback();
                MarkStopped();
                if (win != null) win.SetStatus("动态壁纸启动失败：" + err + "（已停掉上一张的视频）");
                Log.Note("动态壁纸启动失败，已停止播放：" + err);
            }
            UpdateTrayTip();
        }

        /// <summary>这一条能不能走动态壁纸：真视频，或者「标了动态且云端视频已经在盘上」的静态图。
        /// 不再有本机算法兜底，所以没有片子就不算能动态。</summary>
        public bool MotionCapable(WallpaperItem it)
        {
            if (it == null) return false;
            if (it.IsVideo) return true;
            return it.Motion && VideoFor(it) != null;
        }

        /// <summary>按条目类型拉起播放器：真视频，或静态图配好的云端飘动视频。
        /// 本机算法动效已全部删除，没有片子就明确报错，不再拿别的效果顶替。</summary>
        bool StartMotion(WallpaperItem it, out string err)
        {
            if (it == null) { err = "条目为空"; return false; }
            if (it.IsVideo)
            {
                string abs = Path.Combine(Config.LibRoot, it.File.Replace('/', '\\'));
                return Dynamic.Start(abs, Config.DynamicFps, out err);
            }
            string cloudVid = VideoFor(it);
            if (cloudVid != null) return Dynamic.Start(cloudVid, Config.DynamicFps, out err);

            err = "这张连相关的视频文件都没找到，才需要点「云端生成」";
            return false;
        }

        /// <summary>
        /// 这张静态图有没有「能直接播的相关视频」。
        ///
        /// 11-14 之前只认 videos\&lt;id&gt;.mp4；后来补了 .mp4.bak* 自愈；主人明确说还不够：
        /// 「只要关联的文件夹里有相关图片的视频就应该播放，而不是让我重新生成浪费钱」。
        /// 现在统一交给 RelatedVideo 去找：主文件、备份、变体命名、关联子文件夹、
        /// 图片同目录、工单 out 都算；找到不在规范位置的会自动归一到 videos\&lt;id&gt;.mp4。
        /// 全程只读本机文件，零联网、零费用。
        /// </summary>
        string VideoFor(WallpaperItem it)
        {
            if (it == null || it.IsVideo || string.IsNullOrEmpty(it.Id)) return null;
            return RelatedVideo.Find(Config.LibRoot, it, LocalImageFor(it));
        }

        /// <summary>界面用：这张图有没有配好的云端飘动视频（决定菜单文案）</summary>
        public bool HasCloudVideo(WallpaperItem it)
        {
            return VideoFor(it) != null;
        }

        /// <summary>找出能给播放器用的图片文件：优先已下载封面，其次库里的原图，最后才联网补</summary>
        string ResolveMotionImage(WallpaperItem it)
        {
            if (it == null) return null;

            if (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath))
                return it.ImagePath;

            // 本地图片/gif 只是「文件就在库里」，ImagePath 往往没填
            if (!it.IsVideo && !string.IsNullOrEmpty(it.File))
            {
                string own = Path.Combine(Config.LibRoot, it.File.Replace('/', '\\'));
                if (File.Exists(own)) return own;
            }

            if (!it.IsVideo && !string.IsNullOrEmpty(it.ThumbAbs) && File.Exists(it.ThumbAbs))
                return it.ThumbAbs;

            return LibraryStore.EnsureCover(it, Config.LibRoot);
        }

        /// <summary>
        /// 卡片/预览上的「动态播放」开关：静态图打开后立即以当前动效方式播放，
        /// 关掉就退回系统静态壁纸。选择写进 index.json，下次启动还认。
        /// </summary>
        public void ToggleMotion(WallpaperItem it)
        {
            if (it == null || it.IsVideo) return;

            bool next = !it.Motion;
            // 现在只剩云端动效一档，老条目里的 0/1 一律归成 2，别把废弃编号传下去
            it.Effect = 2;
            string err;
            if (!LibraryStore.SetMotion(it.Id, Config.LibRoot, next, it.Effect, out err))
            {
                if (win != null) win.SetStatus("改不了动态播放： " + err);
                return;
            }

            it.Motion = next;
            Log.Note("「" + it.Title + "」动态播放 = " + next + "（动效 " + it.Effect + "）");

            if (next) ApplyItem(it);              // 直接开播
            else
            {
                StopPlayback();
                MarkStopped();
                ApplyItem(it, false);              // 强制退回静态壁纸
            }

            UpdateTrayTip();
            if (win != null) win.RefreshList();
        }

        /// <summary>动效方式的中文名。只剩一档：2 = 云端飘动视频。
        /// 0/1 是老编号（场景动效、缓慢缩放），两种实现都已删除，只作兼容用。</summary>
        public static string FxName(int fx)
        {
            return fx == 2 ? "飘动视频" : "静态";
        }

        /// <summary>
        /// 11-14 深夜：主人「云端生成功能按钮怎么没有了？」
        /// 有片时旧版把菜单 / 大图预览的入口整个换成「播放这段视频」，想重出一片就没地方点。
        /// 这里给一个明确「会重新花钱」的独立入口：先弹确认，再走确认浮条 → StartCloud(force)。
        /// force 只跳过「有片就播」那道闸，仍要过「没配 API 不许写假工单」的总闸。
        /// </summary>
        string forceRegenId = "";
        internal static bool SelfTestNoRegenDialogs = false;
        internal static MessageBoxResult SelfTestRegenAnswer = MessageBoxResult.No;
        internal static string LastRegenDialog = "";
        internal static int RegenDialogCount = 0;

        public void RegenCloud(WallpaperItem it)
        {
            if (it == null) return;
            // 11-15：积分通道只需一次确认。生成前检测、积分量级、无水印规范
            // 全在 StartBuddyGen 那个「确认生成」框里，这里不再叠第二层确认。
            if (IsBuddyChannel())
            {
                forceRegenId = "";
                StartBuddyGen(it, true);
                return;
            }
            string cur = VideoFor(it);
            string body = "这张已经有飘动视频了，重新生成会再花一次钱。\r\n\r\n"
                + "当前片子：" + (string.IsNullOrEmpty(cur) ? "（没找到）" : cur) + "\r\n\r\n"
                + "确认后会走一遍完整下单流程；没配好服务商 / API Key 会弹窗说明缺什么，\r\n"
                + "不会假装「生成中」，也不会偷偷扣费。\r\n"
                + "旧片会保留一份 .bak-regen-* 备份，新片到货后自动换上。\r\n\r\n"
                + "要继续吗？";
            LastRegenDialog = body;
            RegenDialogCount++;
            MessageBoxResult r = SelfTestNoRegenDialogs
                ? SelfTestRegenAnswer
                : MessageBox.Show(body, "壁纸管家 · 重新生成飘动视频",
                                  MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                if (win != null) win.SetStatus("已取消重新生成，现有片子保留");
                return;
            }

            // 这里是显式切成「服务商 API Key」之后的付费直连重出路径，
            // 仍要过 AskCloudConfig「没配 Key 不许写假工单」的总闸。
            forceRegenId = it.Id;
            if (win == null)
            {
                string msg;
                QueueCloudGen(it, LocalImageFor(it), true, out msg);
                return;
            }
            GenApi.Provider p = GenApi.ParseProvider(Config.GenProvider);
            bool ready = GenApi.Ready(Config);
            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                    ? Config.GenSeconds : CloudGen.MaxSeconds;
            win.CloudChoose(it.Id, it.Title, ready, p, Config.GenModel, Config.GenResolution, sec);
            win.SetStatus("重新生成：确认后会重新计费，旧片先留 .bak-regen-* 备份");
        }

        /// <summary>
        /// 11-15：右键「复制交给 WorkBuddy 的指令」。
        /// 只把一段话放进剪贴板：不下单、不写工单、不向任何服务商发请求、不扣费，
        /// 也不触碰壁纸库里的任何文件。真正的出片由主人在 WorkBuddy 会话里看到积分提示后确认。
        /// </summary>
        public void CopyBuddyRequest(WallpaperItem it)
        {
            if (it == null) return;
            string img = LocalImageFor(it);
            if (string.IsNullOrEmpty(img))
            {
                if (win != null) win.SetStatus("找不到这张图的本地文件，先把图下载下来");
                return;
            }
            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                    ? Config.GenSeconds : CloudGen.MaxSeconds;
            string text = CloudGen.BuildBuddyRequest(it, img, Config.LibRoot, sec);
            LastCopiedText = text;
            try
            {
                Clipboard.SetText(text);
                if (win != null) win.SetStatus(
                    "已复制指令（手动兜底，一般用不到）：正常点「云端生成」一次确认就会自动出片；"
                    + "粘到 WorkBuddy 会话也可代出片（消耗积分，不是 Hy4/Hy3 对话免费额度）");
                Log.Note("已复制 WorkBuddy 代出片指令：" + it.Id + "（未下单、未发请求、未扣费）");
            }
            catch (Exception ex)
            {
                if (win != null) win.SetStatus("复制失败：" + ex.Message);
                Log.Note("!! 复制 WorkBuddy 指令失败：" + ex.Message);
            }
        }

        /// <summary>默认出片通道：优先 WorkBuddy 积分；只有显式切成 "api" 才走按秒付费直连。</summary>
        public bool IsBuddyChannel()
        {
            return Config == null || Config.GenChannel != "api";
        }

        /// <summary>
        /// 11-15 改版：云端生成的**默认通道** —— 一次确认即全自动。
        /// 主人明确要求「点击生成 → 只需确认是否生成 → 确认后直接出片自动播放」，
        /// 且「生成之前要检测 WorkBuddy 是否启动可用，可用直接生成，不可用申请启动」。
        ///
        /// 流程：生成前 BuddyBridge.Check() 三态检测 → 弹**唯一一次**「确认生成」
        /// （检测结果、积分量级、无水印规范都写在这个框里）→ 不可用则点「是」时先申请
        /// 启动客户端 → 自动登记待领工单（独立目录，绝不被直连线程误提交）
        /// → 卡片「等 Buddy 出片」→ 客户端自动领单出片 → 片子落到 videos\&lt;id&gt;.mp4
        /// 后 SweepBuddyJobs 自动收货、标动态、自动换上桌面并播放。
        ///
        /// 不再写系统剪贴板、不再要求复制粘贴、不再需要手动开任务。
        /// 这一步不向服务商发请求、不写 queue 工单、不扣任何积分；
        /// 真出片由客户端里的 WorkBuddy 会话完成，程序里这一次确认就是授权，不再二次确认。
        /// 启动失败 / 提交失败都如实提示，绝不留下假「生成中」。
        /// </summary>
        public void StartBuddyGen(WallpaperItem it)
        {
            StartBuddyGen(it, false);
        }

        public void StartBuddyGen(WallpaperItem it, bool regen)
        {
            if (it == null) return;
            string img = LocalImageFor(it);
            if (string.IsNullOrEmpty(img))
            {
                if (win != null) win.SetStatus("找不到这张图的本地文件，先把图下载下来");
                return;
            }
            if (!regen)
            {
                string existing = VideoFor(it);
                if (existing != null)
                {
                    string useMsg;
                    UseExistingVideo(it, existing, out useMsg);
                    return;
                }
            }
            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                    ? Config.GenSeconds : CloudGen.MaxSeconds;
            string text = CloudGen.BuildBuddyRequest(it, img, Config.LibRoot, sec);
            string target = CloudGen.VideoPath(Config.LibRoot, it.Id);

            // 11-15：生成之前先检测，可用直接生成，不可用在同一次确认里申请启动
            string readyReason;
            BuddyBridge.ReadyState ready = BuddyBridge.Check(out readyReason);
            LastReadyState = ready.ToString();
            // 上一次领单会话到底是好的还是被挡回去了 —— 直接写进这次确认框，
            // 别再让主人点了生成之后干等却不知道发生了什么（429 额度就是这样被藏起来的）。
            string pickerFail = BuddyBridge.LastPickerFailure(24 * 60);
            string action;
            if (ready == BuddyBridge.ReadyState.NoClient)
            {
                action = "点「是」：先申请启动 WorkBuddy 客户端，再自动提交工单；\r\n"
                       + "          客户端就绪后由自动领单任务生成 mp4，到货自动换上桌面并播放。\r\n"
                       + "          启动失败会如实提示，不写工单、不扣积分。\r\n";
            }
            else if (ready == BuddyBridge.ReadyState.ClientOnly)
            {
                action = "点「是」：立即提交工单。注意自动领单任务本轮没有心跳，\r\n"
                       + "          可能要到下一次领单才动；等不到就在卡片右键看「工单没人领」。\r\n"
                       + "          到货自动换上桌面并播放。\r\n";
            }
            else
            {
                action = "点「是」：立即提交工单，领单任务在跑、马上就能领；\r\n"
                       + "          出片后自动换上桌面并播放，不用复制指令、不用手动开任务。\r\n";
            }

            string body = (regen
                    ? "「重新生成一片」会再消耗一次 WorkBuddy 积分，旧片保留备份。\r\n\r\n" : "")
                + "出片通道：优先 WorkBuddy 积分（不花服务商的钱、不用填 API Key）。\r\n"
                + "生成前检测：" + readyReason + "\r\n"
                + (string.IsNullOrEmpty(pickerFail)
                    ? "上次领单结局：没读到失败记录\r\n"
                    : "上次领单结局：失败 —— " + pickerFail + "\r\n"
                      + "          （不是没提交，是领单会话被挡回去了；这一次照样会提交）\r\n")
                + "\r\n"
                + action
                + "点「否」：取消本次生成，什么都不做、不扣积分。\r\n\r\n"
                + "费用：视频生成走多模态模型，会消耗 WorkBuddy 积分"
                + "（约 50~100 积分 / 5 秒，3 秒片按比例，实际以客户端提示为准）；"
                + "Hy4 / Hy3 的对话免费额度不包含出片。\r\n"
                + "出片规范：" + CloudGen.NoWatermarkRuleCn + "\r\n"
                + "通道：工单排队\r\n"
                + "现在这一步只提交工单：不写系统剪贴板、不向服务商发请求、不扣任何积分、\r\n"
                + "不改壁纸库文件；真出片由客户端里的 WorkBuddy 会话完成。\r\n"
                + "你在这次确认里已经授权本次出片，客户端不会再向你要第二次确认。\r\n"
                + "积分通道依赖 WorkBuddy 客户端在运行（自动领单任务在客户端里跑）；"
                + "想完全不打开它也能出片，请在设置里切成「服务商 API Key」按秒付费直连。\r\n\r\n"
                + "目标文件：" + target + "\r\n"
                + "待领工单目录：" + CloudGen.BuddyDir;
            LastBuddyDialog = body;
            BuddyDialogCount++;
            Log.Note("WorkBuddy 积分通道：生成前检测=" + ready + "（" + it.Id + "），已告知积分消耗，未扣费");

            MessageBoxResult r = SelfTestNoBuddyDialogs
                ? SelfTestBuddyAnswer
                : MessageBox.Show(body, "壁纸管家 · 确认生成（WorkBuddy 积分出片）",
                                  MessageBoxButton.YesNo, MessageBoxImage.Information,
                                  MessageBoxResult.Yes);
            if (r != MessageBoxResult.Yes)
            {
                if (win != null) win.SetStatus("已取消生成：没提交、没扣积分、没改文件");
                return;
            }

            // 不可用：这一下「是」同时授权申请启动客户端；起不来就不写工单，绝不留假状态
            if (ready == BuddyBridge.ReadyState.NoClient)
            {
                string detail;
                if (!BuddyBridge.TryLaunch(Config, out detail))
                {
                    LastLaunchDetail = detail;
                    if (win != null) win.SetStatus("已取消出片：" + detail);
                    Log.Note("!! WorkBuddy 不可用且申请启动未成功，未提交工单：" + detail);
                    return;
                }
                LastLaunchDetail = detail;
                Log.Note("生成前检测：客户端未运行，" + detail);
            }
            else LastLaunchDetail = "无需启动（客户端已就绪）";

            string err;
            bool wrote = CloudGen.WriteBuddyJob(Config, it, img, sec, out err);
            LastSubmitText = text;   // 只记录文本，不再写系统剪贴板
            pendingApplyId = it.Id;  // 到货自动换桌面，纯内存，不写 config
            // 提交成功 ≠ 会出片：只有领单心跳新鲜才敢说「会自动出片」，
            // 否则如实说「排着队但没人领」，别再造一次 02:50 那种干等。
            bool workerAlive = BuddyBridge.HeartbeatFresh();
            if (win != null)
            {
                win.SetStatus(!wrote
                    ? "自动提交失败：" + err
                    : workerAlive
                        ? "已自动提交：领单任务在跑，出片后自动换上桌面播放（没扣积分）"
                        : "工单已排队，但当前没有领单会话在跑：卡片会标「⚠ 未领单」，右键看原因");
                win.RefreshList();   // 让卡片立刻显示「等 Buddy 出片」/「未领单」
            }
            Log.Note("WorkBuddy 积分通道：" + it.Id + " 检测=" + ready
                     + " 工单=" + wrote + " 领单心跳=" + workerAlive
                     + " 指令=" + text.Length + " 字符；未提交服务商、未扣积分");
        }

        /// <summary>
        /// 11-15 晚饭后：主人问「为啥没有会话领单，点完只显示等待，两边都没反应」。
        /// 查清的真原因是两件事叠在一起：
        ///   ① 自动领单任务只在客户端运行期间才会跑，客户端关着的那几轮全部作废（记成 missed）；
        ///   ② 客户端今天 19:23 启动时补跑了一次，但会话 21 秒就被模型侧 429 额度限流打回，
        ///      一句话都没执行 —— 工单没人动、心跳没刷新，程序还挂着「等 Buddy 出片」。
        /// 所以这个方法把三件事一次说清：客户端在不在、心跳几点、上次领单到底报了什么。
        ///
        /// 11-15 21:00 主人补一句「触发未领单后要能申请重新生成，现在做不到」——
        /// 之前客户端开着时这里只有一个 OK，是死路。现在两种情形都给「重新提交」：
        /// 点「是」= 作废旧工单 → 必要时拉起客户端 → 重新排队（角标立刻从⚠回到⏳）。
        /// </summary>
        public void ShowBuddyStuck(WallpaperItem it)
        {
            if (it == null) return;
            bool client = BuddyBridge.ClientAlive();
            bool hb = BuddyBridge.HeartbeatFresh();
            double age = CloudGen.BuddyJobAgeMinutes(it.Id);
            string fail = BuddyBridge.LastPickerFailure(24 * 60);
            LastStuckDialog = "";
            string body = "工单：" + it.Id + "\r\n"
                + "排队时长：" + (age < 1 ? "刚提交" : ((int)age) + " 分钟") + "\r\n\r\n"
                + "WorkBuddy 客户端：" + (client ? "在运行" : "没在运行") + "\r\n"
                + "自动领单心跳：" + (hb ? "新鲜（领单任务在跑）" : "不新鲜（领单任务没跑过）") + "\r\n"
                + "上次领单结局：" + (string.IsNullOrEmpty(fail) ? "没读到失败记录" : fail) + "\r\n\r\n"
                + "结论：" + (client
                    ? (string.IsNullOrEmpty(fail)
                        ? "客户端开着但领单任务没跑；等它下一次运行就会动。"
                        : "客户端开着，但领单会话被挡回去了 —— 片子没有生成，工单还在。")
                    : "客户端没在运行，所以没有任何会话能领这张单（自动领单是客户端里的任务，"
                      + "程序自身不能生成视频）。")
                + "\r\n\r\n"
                + "点「是」：重新提交这张出片工单 —— 作废旧的"
                + (client ? "，按当前状态重新排队，等客户端领单。\r\n"
                          : "，先把 WorkBuddy 拉起来，再重新排队。\r\n")
                + "          重新提交等于重新出片，会再次消耗积分；"
                + (string.IsNullOrEmpty(fail) ? "" : "额度类限流请等上面提示的重置时间。")
                + "\r\n点「否」：什么都不做，工单继续留着。\r\n";
            LastStuckDialog = body;

            MessageBoxResult r = SelfTestNoBuddyDialogs
                ? SelfTestBuddyAnswer
                : MessageBox.Show(body, "壁纸管家 · 这张工单为什么没出片",
                                  MessageBoxButton.YesNo,
                                  MessageBoxImage.Warning, MessageBoxResult.No);
            Log.Note("查看待领工单状态：" + it.Id + " 客户端=" + client + " 心跳=" + hb
                     + " 排队=" + (int)age + "分 上次结局=" + (string.IsNullOrEmpty(fail) ? "无" : fail));

            if (r == MessageBoxResult.Yes) ResubmitBuddyJob(it);
        }

        /// <summary>
        /// 11-15 21:00：把「重新提交」做成真动作，而不是又弹一个说明框。
        /// 顺序很重要：先按**提交那一刻**的真实检测决定要不要拉起客户端，
        /// 再作废旧工单（作废后队列时间从零算，角标才会从「⚠ 未领单」回到「⏳ 等 Buddy 出片」），
        /// 最后重新写一张工单。任何一步失败都如实说，不留假状态。
        /// 启动失败时不作废旧工单 —— 旧单还在，至少还能等下一轮，不会把主人手里的东西弄丢。
        /// 已有片子的情况不该走这里，交给 RegenCloud（先备份旧片、过 API 总闸）。
        /// </summary>
        public void ResubmitBuddyJob(WallpaperItem it)
        {
            if (it == null) return;
            if (VideoFor(it) != null) { RegenCloud(it); return; }

            string img = LocalImageFor(it);
            if (string.IsNullOrEmpty(img))
            {
                if (win != null) win.SetStatus("找不到这张图的本地文件，没法重新提交");
                return;
            }

            string readyReason;
            BuddyBridge.ReadyState ready = BuddyBridge.Check(out readyReason);
            LastReadyState = ready.ToString();
            if (ready == BuddyBridge.ReadyState.NoClient)
            {
                string detail;
                if (!BuddyBridge.TryLaunch(Config, out detail))
                {
                    LastLaunchDetail = detail;
                    if (win != null) win.SetStatus("没能重新提交：" + detail);
                    Log.Note("!! 重新提交前申请启动客户端失败，旧工单保留：" + detail);
                    return;
                }
                LastLaunchDetail = detail;
            }
            else LastLaunchDetail = "无需启动（客户端已就绪）";

            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                    ? Config.GenSeconds : CloudGen.MaxSeconds;

            CloudGen.RemoveBuddyJob(it.Id);          // 先作废，队列时间从零开始
            string err;
            bool wrote = CloudGen.WriteBuddyJob(Config, it, img, sec, out err);
            if (!wrote)
            {
                if (win != null) win.SetStatus("重新提交失败：" + err);
                Log.Note("!! 重新提交出片工单失败：" + err);
                return;
            }

            pendingApplyId = it.Id;
            bool worker = BuddyBridge.HeartbeatFresh();
            if (win != null)
            {
                win.SetStatus(worker
                    ? "已重新提交：领单任务在跑，出片后自动换上桌面播放（没扣积分）"
                    : "已重新提交并重新排队：卡片回到「等 Buddy 出片」，领单任务一跑就会动");
                win.RefreshList();
            }
            Log.Note("WorkBuddy 积分通道：重新提交出片工单 " + it.Id
                     + " 检测=" + ready + " 领单心跳=" + worker + "；未提交服务商、未扣积分");
        }

        /// <summary>
        /// 界面入口：点「云端生成」先亮出确认浮条 —— 用哪个模型、大概花多少钱、预计等多久。
        /// 确认按钮才调 StartCloud 真下单。没配好 Key 时浮条给「去设置」，不写工单、不假排队。
        /// 没有窗口（自检/后台调用）时保留原来的直接下单行为。
        ///
        /// 11-14 新增最前面这道闸：只要库里已经有跟这张图相关的视频，一律直接播，
        /// 连确认浮条都不弹 —— 主人的要求是不该让他再花一次钱。
        /// 唯一例外：想主动重出一片时走 RegenCloud（会先确认再花钱）。
        /// </summary>
        public void GenCloud(WallpaperItem it)
        {
            if (it == null) return;
            string existing = VideoFor(it);
            if (existing != null)
            {
                string useMsg;
                UseExistingVideo(it, existing, out useMsg);
                return;
            }
            if (win == null)
            {
                string msg;
                QueueCloudGen(it, LocalImageFor(it), out msg);
                return;
            }
            // 11-15：默认优先消耗 WorkBuddy 积分，不再一上来就要求填服务商 Key。
            if (IsBuddyChannel()) { StartBuddyGen(it); return; }
            GenApi.Provider p = GenApi.ParseProvider(Config.GenProvider);
            bool ready = GenApi.Ready(Config);
            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                    ? Config.GenSeconds : CloudGen.MaxSeconds;
            win.CloudChoose(it.Id, it.Title, ready, p, Config.GenModel, Config.GenResolution, sec);
            win.SetStatus(ready ? "已选好云端模型，确认后提交（会显示费用与预计等待）"
                                : "云端生成需要先在设置里选服务商并填 API Key");
        }

        /// <summary>确认浮条上点了「开始生成」：把选中的模型写回配置，然后走真实下单链路</summary>
        public void StartCloud(string id, string modelId, string resolution)
        {
            WallpaperItem it = FindById(id);
            if (it == null) { if (win != null) win.SetStatus("这张已经不在库里了"); return; }

            // 11-14 深夜：主人在「重新生成」里明确确认过，这一单就是要重出一片，
            // 不能再被「有片就播」拦回去；其余路径一律保持拦截。
            bool force = string.Equals(forceRegenId, id, StringComparison.OrdinalIgnoreCase);
            forceRegenId = "";
            // 确认浮条亮着的时候，片子可能刚落到盘上（外部生成器 / 手工复制）。
            // 再拦一次：点了确认也不许重复提交一份要花钱的工单。
            string existing = VideoFor(it);
            if (!force && existing != null)
            {
                string useMsg;
                UseExistingVideo(it, existing, out useMsg);
                return;
            }
            GenApi.Provider p = GenApi.ParseProvider(Config.GenProvider);
            Config.GenModel = GenModels.Resolve(p, modelId).Id;
            if (!string.IsNullOrEmpty(resolution)) Config.GenResolution = resolution;
            ConfigStore.Save(Config);
            string msg;
            QueueCloudGen(it, LocalImageFor(it), force, out msg);
        }

        /// <summary>自检用：下单后清掉「到货自动换桌面」标记，避免测试片真把主人桌面换掉</summary>
        public void ClearPendingCloud()
        {
            pendingApplyId = null;
            if (Config != null) { Config.PendingCloudId = ""; ConfigStore.Save(Config); }
        }

        /// <summary>
        /// 11-14：自检要验「有片不下单」，但不能顺手把主人的桌面换成测试片。
        /// 只在 --relatedvideotest 里打开，跑完 finally 一定还原。
        /// </summary>
        internal static bool SelfTestNoPlay = false;

        /// <summary>
        /// 11-14：没配好服务商 / API Key 时，把缺的东西一次说清，并直接带去设置。
        ///
        /// 老行为是照样写一张工单、把界面切成「生成中」，可没有任何进程会去提交它 ——
        /// 主人原话：「就一个生产中还是假的」。现在所有下单入口都先过这里：
        /// 不写工单、不改状态、不显示生成中，也不会向任何服务商发请求、不会扣费。
        /// 自检用 SelfTestNoDialogs / SelfTestCloudAnswer 注入，不弹真窗口。
        /// </summary>
        // 11-15：WorkBuddy 积分通道的弹窗 / 剪贴板注入，生产路径一律关着
        internal static bool SelfTestNoBuddyDialogs = false;
        internal static MessageBoxResult SelfTestBuddyAnswer = MessageBoxResult.Cancel;
        internal static string LastBuddyDialog = "";
        internal static string LastStuckDialog = "";     // 11-15：「为什么没出片」原因框正文，供自检核对
        internal static int BuddyDialogCount = 0;
        internal static bool SelfTestNoClipboard = false;
        internal static string LastCopiedText = "";      // 只给「手动兜底：复制指令」用
        internal static string LastSubmitText = "";      // 11-15：自动提交的指令文本（不写剪贴板）
        internal static string LastReadyState = "";      // 生成前检测结果，供界面 / 自检核对
        internal static string LastLaunchDetail = "";    // 「申请启动」结果，失败也不藏

        internal static bool SelfTestNoDialogs = false;
        internal static MessageBoxResult SelfTestCloudAnswer = MessageBoxResult.No;
        internal static string LastCloudDialog = "";
        internal static int CloudDialogCount = 0;

        bool AskCloudConfig(WallpaperItem it, out string msg)
        {
            return AskCloudConfig(it, false, out msg);
        }

        bool AskCloudConfig(WallpaperItem it, bool regen, out string msg)
        {
            GenApi.Provider p = GenApi.ParseProvider(Config.GenProvider);
            StringBuilder miss = new StringBuilder();
            if (p == GenApi.Provider.None)
                miss.Append("  · 服务商：还没选（可选 通义万相 DashScope / 火山方舟 Ark）\r\n");
            if (string.IsNullOrEmpty(Config.GenApiKey))
                miss.Append("  · API Key：还没填\r\n");
            if (string.IsNullOrEmpty(Config.GenModel))
                miss.Append("  · 模型：还没选（选好服务商会自动带出默认模型）\r\n");

            string body = (regen ? "重新生成一片需要先配好：\r\n\r\n"
                                 : "这张还没有飘动视频。自动出片需要先配好：\r\n\r\n")
                + miss.ToString() + "\r\n"
                + "现在不会向任何服务商发请求，也不会扣费。\r\n"
                + "可选两家：通义万相 wan2.6-i2v-flash（720P 约 ¥0.15/秒）、"
                + "火山方舟 Doubao-Seedance（约 ¥0.2~0.6/秒）。\r\n\r\n"
                + "点「是」打开设置填好服务商和 API Key；点「否」先不生成。\r\n"
                + "已经有片子的话，把 mp4 改名成 " + it.Id + ".mp4 放进 videos\\ 就会自动识别并播放。\r\n"
                + "当前出片通道是「服务商 API Key（按秒付费）」。想改成优先消耗 WorkBuddy 积分、"
                + "不用填 Key：去「设置 → 出片通道」切回默认项即可；多模态出片会消耗 WorkBuddy 积分，"
                + "不属于 Hy4 / Hy3 的对话免费额度。\r\n\r\n"
                + "工单目录：" + CloudGen.QueueDir;

            LastCloudDialog = body;
            CloudDialogCount++;
            Log.Note("云端生成：配置不全，已弹窗说明（服务商=" + Config.GenProvider
                     + "，Key=" + (string.IsNullOrEmpty(Config.GenApiKey) ? "空" : "已填")
                     + "），未下单、未扣费");

            MessageBoxResult r = SelfTestNoDialogs
                ? SelfTestCloudAnswer
                : MessageBox.Show(body, "壁纸管家 · 云端生成还没配好",
                                  MessageBoxButton.YesNo, MessageBoxImage.Information);

            msg = "云端生成还没配好（没选服务商 / 没填 API Key）：没有下单、没有扣费，"
                + "也不会自动出片。去「设置 → 云端生成」填好即可。";
            if (r == MessageBoxResult.Yes) OpenSettings();
            return r == MessageBoxResult.Yes;
        }

        /// <summary>11-15：WorkBuddy 积分通道的待领工单也要看得见、摸得着。</summary>
        public void OpenBuddyFolder()
        {
            try
            {
                CloudGen.EnsureDirs();
                Process.Start("explorer.exe", CloudGen.BuddyDir);
                if (win != null) win.SetStatus("待领工单目录：" + CloudGen.BuddyDir);
            }
            catch (Exception ex)
            {
                if (win != null) win.SetStatus("打开待领工单目录失败：" + ex.Message);
            }
        }

        /// <summary>「我下单在哪能看到？」—— 直接打开工单目录并选中这张单。</summary>
        public void OpenJobFolder(string id)
        {
            try
            {
                string dir = CloudGen.QueueDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string job = string.IsNullOrEmpty(id) ? "" : CloudGen.JobPath(id);
                string arg = File.Exists(job) ? "/select,\"" + job + "\"" : "\"" + dir + "\"";
                Process.Start("explorer.exe", arg);
                if (win != null) win.SetStatus("工单目录：" + dir
                    + (File.Exists(job) ? "（已选中 " + Path.GetFileName(job) + "）" : ""));
                Log.Note("云端生成：打开工单目录 " + dir + " id=" + id);
            }
            catch (Exception ex)
            {
                if (win != null) win.SetStatus("打不开工单目录：" + ex.Message);
            }
        }

        /// <summary>
        /// 统一的「已有相关视频」处理：记日志、状态栏说清楚、直接播，绝不进下单流程。
        /// 所有能走到花钱那一步的入口（GenCloud / StartCloud / QueueCloudGen /
        /// SetMotionFx / CloudGen.Enqueue）都先过这道闸。
        /// </summary>
        string UseExistingVideo(WallpaperItem it, string videoPath, out string msg)
        {
            string name = string.IsNullOrEmpty(videoPath) ? "" : Path.GetFileName(videoPath);
            msg = "这张已经有相关视频了，直接播，不用重新生成（" + name + "）";
            Log.Note("云端生成已拦截：" + it.Id + " → 复用现成视频 " + videoPath);

            if (!SelfTestNoPlay)
            {
                // SetMotionFx 内部会写动效标记、真正开播并刷新列表。
                try { SetMotionFx(it, 2); }
                catch (Exception ex) { Log.Note("!! 复用现成视频后播放失败：" + ex.Message); }
                if (win != null)
                {
                    try { win.SetStatus(msg); }
                    catch { }
                }
            }
            return videoPath;
        }

        /// <summary>
        /// 队列快照：界面右下角那条浮条每秒读一次。
        ///
        /// 为什么要它：主人点了「飘动视频」只看到状态栏一句话，然后就没了下文 ——
        /// 到底在生成、卡住了、还是根本没有生成通道，界面上看不出来。
        /// 有了快照，浮条能报「已排队 03:12」和「靠谁生成」。
        /// </summary>
        public sealed class CloudSnap
        {
            public int Pending;
            public string Id = "";
            public string Title = "";
            public long WaitSeconds;
            public string Channel = "";
            public string Phase = "";         // 提交中 / 已接单 / 模型生成中 / 下载中 / 完成
            public string EstimateText = "";  // 大概花费 + 预计还需等多久
            public string InfoLine = "";      // 模型 / 分辨率 / 秒数 / 服务商
        }

        /// <summary>当前出片通道。脚本路线已放弃，只剩云端模型一条</summary>
        public string CloudChannel()
        {
            GenApi.Provider p = GenApi.ParseProvider(Config == null ? "" : Config.GenProvider);
            if (GenApi.Ready(Config))
            {
                GenModelInfo m = GenModels.Resolve(p, Config.GenModel);
                return "云端模型 · " + GenApi.ProviderName(p) + " / " + m.Id
                     + " · " + Config.GenResolution + "（用你的 Key 直连提交）";
            }
            if (p == GenApi.Provider.None)
                return "未配置云端模型：还没选服务商（去设置选通义万相 / 火山方舟）"
                     + "｜工单目录 " + CloudGen.QueueDir;
            return "未配置云端模型：" + GenApi.ProviderName(p) + " 还没填 API Key（去设置填好即可出片）"
                 + "｜工单目录 " + CloudGen.QueueDir;
        }

        /// <summary>后台线程上报当前阶段；浮条每秒读它，主人才能分清「排队中」还是「生成中」</summary>
        volatile string cloudPhase = "";

        /// <summary>队列里排得最久那单已等多少秒；费用倒计时要基于它，不能每次都从 0 算</summary>
        long OldestQueuedWait()
        {
            try
            {
                if (!Directory.Exists(CloudGen.QueueDir)) return 0;
                string[] jobs = Directory.GetFiles(CloudGen.QueueDir, "*.json");
                long oldest = long.MaxValue;
                for (int i = 0; i < jobs.Length; i++)
                {
                    string id, image;
                    int sec;
                    long created;
                    if (!CloudGen.ReadJob(jobs[i], out id, out image, out sec, out created)) continue;
                    if (created > 0 && created < oldest) oldest = created;
                }
                if (oldest == long.MaxValue || oldest <= 0) return 0;
                long w = CloudGen.NowSeconds() - oldest;
                return w < 0 ? 0 : w;
            }
            catch { return 0; }
        }

        public CloudSnap CloudSnapshot()
        {
            CloudSnap s = new CloudSnap();
            s.Channel = CloudChannel();
            s.Phase = cloudPhase == null ? "" : cloudPhase;
            long waited = OldestQueuedWait();
            s.WaitSeconds = waited;

            GenApi.Provider p = GenApi.ParseProvider(Config == null ? "" : Config.GenProvider);
            GenModelInfo m = GenModels.Resolve(p, Config == null ? "" : Config.GenModel);
            string res = (Config == null || string.IsNullOrEmpty(Config.GenResolution))
                ? "720P" : Config.GenResolution;
            int secCfg = (Config != null && Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                ? Config.GenSeconds : CloudGen.MaxSeconds;

            s.InfoLine = "模型 " + m.Id + " · " + res + " · " + secCfg + " 秒　｜　"
                       + GenApi.ProviderName(p) + "　｜　价格按公开单价估算，以账单为准";

            long left = (long)m.WaitTypicalSec - waited;
            string remain = left > 0 ? "预计还需约 " + GenModels.ElapsedText(left)
                                     : "比预计稍慢，仍在生成";
            s.EstimateText = "大概花费 " + GenModels.CostText(m, res, secCfg)
                           + "（" + m.Price(res).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                           + " 元/秒 × " + secCfg + " 秒）　·　" + remain;
            try
            {
                if (!Directory.Exists(CloudGen.QueueDir)) return s;
                string[] jobs = Directory.GetFiles(CloudGen.QueueDir, "*.json");
                s.Pending = jobs.Length;
                if (s.Pending == 0) return s;

                // 取「排得最久」的那单当代表：浮条只放得下一张，先说等得最久的
                long oldest = long.MaxValue;
                string oldestId = "";
                for (int i = 0; i < jobs.Length; i++)
                {
                    string id, image;
                    int sec;
                    long created;
                    if (!CloudGen.ReadJob(jobs[i], out id, out image, out sec, out created)) continue;
                    if (created <= 0) created = 0;
                    if (created < oldest) { oldest = created; oldestId = id; }
                }

                if (oldestId.Length == 0) return s;
                s.Id = oldestId;
                WallpaperItem one = FindById(oldestId);
                s.Title = one == null ? oldestId : one.Title;
                if (oldest > 0 && oldest != long.MaxValue) s.WaitSeconds = CloudGen.NowSeconds() - oldest;
            }
            catch (Exception ex) { Log.Note("!! 读队列快照失败：" + ex.Message); }
            return s;
        }

        /// <summary>取消一单：删工单、把这条的「动态播放」标回静态，别留个假的生成中</summary>
        public void CancelCloud(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                string job = CloudGen.JobPath(id);
                if (File.Exists(job)) File.Delete(job);

                WallpaperItem it = FindById(id);
                // 11-14：以前只看规范主文件，主文件被移走/只剩备份时取消订单会把动效清成静态，
                // 于是界面又变成「要重新生成」。现在按「有没有相关视频」来判断。
                if (it != null && VideoFor(it) == null)
                {
                    string err;
                    LibraryStore.SetMotion(id, Config.LibRoot, false, 0, out err);
                    it.Motion = false;
                    it.Effect = 0;
                }
                else if (it != null)
                {
                    Log.Note("云端生成：取消订单但保留动效标记（已有相关视频）" + id);
                }

                if (pendingApplyId == id) pendingApplyId = null;
                if (Config.PendingCloudId == id)
                {
                    Config.PendingCloudId = "";
                    ConfigStore.Save(Config);
                }

                Log.Note("云端生成：已取消 " + id);
                if (win != null)
                {
                    win.RefreshList();
                    win.SetStatus("已取消这单云端生成");
                }
            }
            catch (Exception ex)
            {
                Log.Note("!! 取消云端生成失败：" + ex.Message);
                if (win != null) win.SetStatus("取消失败：" + ex.Message);
            }
        }

        /// <summary>浮条上的「重试」：重新下一单（会先清掉上次的失败标记）</summary>
        public void RetryCloud(string id)
        {
            WallpaperItem it = FindById(id);
            if (it == null) { if (win != null) win.SetStatus("这张已经不在库里了"); return; }
            GenCloud(it);
        }

        /// <summary>刚下单的那一条：视频到货时如果还是它，就自动换上，不用主人再点一次</summary>
        string pendingApplyId;

        /// <summary>
        /// --cloudtest 的实现：挑一张「还没有片子」的静态图下单，8 秒后报到货状态，14 秒后收工。
        ///
        /// 挑不到图不一定是真没图：应用刚启动时先恢复动态壁纸，库可能还没读完。
        /// 所以挑不到且库里是空的时候再等 2 秒重来，连续三次才认输 ——
        /// 不然一条「库里没有静态图」的假失败会让人以为链路断了，白查半天。
        /// </summary>
        void RunCloudTest(int attempt)
        {
            WallpaperItem pick = null;
            for (int i = 0; i < Items.Count; i++)
            {
                // 已经有片子的不算：Enqueue 会直接拒（「这张已经有云端视频了」），
                // 那样测的是拒绝分支，不是「下单 → 外部出片 → 收货」这条真链路。
                if (!Items[i].IsVideo && VideoFor(Items[i]) == null) { pick = Items[i]; break; }
            }

            if (pick == null)
            {
                if (Items.Count == 0 && attempt < 3)
                {
                    Log.Note("自检 --cloudtest：库还没读完（第 " + (attempt + 1) + " 次挑图），2 秒后重试");
                    DispatcherTimer again = new DispatcherTimer();
                    again.Interval = TimeSpan.FromSeconds(2);
                    again.Tick += delegate { again.Stop(); RunCloudTest(attempt + 1); };
                    again.Start();
                    return;
                }
                Log.Note("自检 --cloudtest：没有可测的静态图（库里共 " + Items.Count + " 条）");
                return;
            }

            string msg;
            bool ok = QueueCloudGen(pick, LocalImageFor(pick), out msg);
            Log.Note("自检 --cloudtest：下单" + (ok ? "成功" : "失败") + "：" + msg
                     + "  条目=" + pick.Id + "  队列中=" + CloudGen.PendingCount());

            DispatcherTimer t8 = new DispatcherTimer();
            t8.Interval = TimeSpan.FromSeconds(8);
            t8.Tick += delegate
            {
                t8.Stop();
                Log.Note("自检 --cloudtest(8s)：状态=" + CloudGen.StateOf(Config, pick)
                         + " 动态=" + Config.DynamicEnabled + " 播放器=" + Dynamic.Alive
                         + " 窗口=" + Dynamic.WindowAlive + " effect=" + pick.Effect
                         + "  队列中=" + CloudGen.PendingCount());

                DispatcherTimer t14 = new DispatcherTimer();
                t14.Interval = TimeSpan.FromSeconds(6);
                t14.Tick += delegate
                {
                    t14.Stop();
                    StopDynamic(true);
                    Log.Note("自检 --cloudtest(结束)：动态=" + Config.DynamicEnabled
                             + " 播放器=" + Dynamic.Alive);
                };
                t14.Start();
            };
            t8.Start();
        }

        /// <summary>
        /// --cloudflowtest：在**没有真 API Key** 的前提下，把「点击云端生成」跑到真的出片。
        ///
        /// 主人 11-13 的话：「重点是这个功能点击了能用，不是说只是做个页面」。
        /// 所以这里不是截图验收，而是让应用内那套 HttpWebRequest / WebClient 原封不动地
        /// 打到一个本机模拟服务端上：提交 → 轮询 → 下载真 mp4 → SweepCloudJobs 收货入库
        /// → 浮条从「等待中」变「已到货」，逐项断言请求头、请求体、文件、状态机。
        ///
        /// 隔离：ConfigStore.DirOverride 把 config.json / jobs 挪到临时目录，
        /// LibRoot 也指向临时库，假 Key 和测试片都不会碰到主人的真实配置与壁纸库；
        /// ClearPendingCloud 保证测试片不会真把桌面换掉。跑完全部还原。
        /// </summary>
        void RunCloudFlowTest()
        {
            string id = "zz-cloudflow-test";
            string savedDirOverride = ConfigStore.DirOverride;
            string savedLibRoot = Config.LibRoot;
            string savedProvider = Config.GenProvider;
            string savedKey = Config.GenApiKey;
            string savedModel = Config.GenModel;
            string savedRes = Config.GenResolution;
            string savedPending = Config.PendingCloudId;
            int savedSeconds = Config.GenSeconds;
            string savedEndpoint = GenApi.EndpointOverride;
            int savedPoll = GenApi.PollIntervalMs;
            string tmp = "";
            MockCloudServer mock = new MockCloudServer();
            bool completed = false;
            string shotNoKey = Path.Combine(Path.GetTempPath(), "wallpaper-cloud-0-nokey.png");
            string shotChoose = Path.Combine(Path.GetTempPath(), "wallpaper-cloud-1-choose.png");
            string shotWait = Path.Combine(Path.GetTempPath(), "wallpaper-cloud-2-wait.png");
            string shotArrived = Path.Combine(Path.GetTempPath(), "wallpaper-cloud-3-arrived.png");
            bool noKeyOk = false;

            Action restore = delegate
            {
                Config.LibRoot = savedLibRoot;
                Config.GenProvider = savedProvider;
                Config.GenApiKey = savedKey;
                Config.GenModel = savedModel;
                Config.GenResolution = savedRes;
                Config.PendingCloudId = savedPending;
                Config.GenSeconds = savedSeconds;
                ConfigStore.DirOverride = savedDirOverride;
                GenApi.PollIntervalMs = savedPoll;
                cloudPhase = "";
                if (completed)
                {
                    // 只有流程确实跑完了，后台线程才一定已经退出，这时才能安全切回官方地址
                    GenApi.EndpointOverride = savedEndpoint;
                    mock.Stop();
                }
                else
                {
                    // 万一超时，保留本机地址，避免残留线程带着假 Key 去打真实服务商
                    Log.Note("自检 --cloudflowtest：未跑完，基址仍指向本机，进程退出后自动释放");
                }
                try { Greeting.Load(ResourcesDir); } catch { }
                try { ReloadLibrary(); } catch { }
                try { if (tmp.Length > 0 && Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Log.Note("自检 --cloudflowtest：现场已还原（真实配置 / 库 / 队列不受影响）");
            };

            try
            {
                // ---- 0) 主人当前的状态：没填 Key。点击必须给明确引导，且绝不写假工单 ----
                // 11-14：这一步必须挑一张「确实没有相关视频」的条目。库里那几张静态图
                // 现在都有片子了，拿它们会被「有片就播」的总闸拦下 —— 那是正确行为，
                // 但不是这条要测的分支，所以用一个只存在于内存里的假 id。
                WallpaperItem real = new WallpaperItem();
                real.Id = "zz-cloudflow-nokey";
                real.Title = "未配 Key 的测试条目";
                real.File = "images/zz-cloudflow-nokey.jpg";
                int jobsBefore = CloudGen.PendingCount();
                if (win != null)
                {
                    GenCloud(real);
                    string nk = win.SelfTestCloudState();
                    noKeyOk = nk.IndexOf("mode=3") >= 0
                           && nk.IndexOf("主按钮=去设置") >= 0
                           && nk.IndexOf("还没填 API Key") >= 0
                           && CloudGen.PendingCount() == jobsBefore;
                    Log.Note("自检 --cloudflowtest：未配 Key 点击 → 去设置且不写工单 "
                        + (noKeyOk ? "√" : "×") + " " + nk.Replace("\n", "｜"));
                    SnapCloudCorner(shotNoKey);
                    win.HideCloudToastForTest();
                }
                else
                {
                    Log.Note("自检 --cloudflowtest：未配 Key 分支 × 无窗口");
                }

                tmp = Path.Combine(Path.GetTempPath(), "wallpaper-cloudflow-" + Environment.TickCount);
                string lib = Path.Combine(tmp, "lib");
                Directory.CreateDirectory(Path.Combine(lib, "images"));
                Directory.CreateDirectory(Path.Combine(lib, "videos"));
                Directory.CreateDirectory(Path.Combine(lib, "thumbs"));
                Directory.CreateDirectory(Path.Combine(tmp, "cfg"));

                string srcImg = "", srcVid = "";
                try
                {
                    string[] imgs = Directory.GetFiles(Path.Combine(savedLibRoot, "images"), "*.jpg");
                    if (imgs.Length > 0) srcImg = imgs[0];
                    string[] vids = Directory.GetFiles(Path.Combine(savedLibRoot, "videos"), "*.mp4");
                    if (vids.Length > 0) srcVid = vids[0];
                }
                catch { }
                if (srcImg.Length == 0 || srcVid.Length == 0)
                {
                    Log.Note("自检 --cloudflowtest：FAIL 真实库里找不到 jpg / mp4 素材");
                    completed = true; restore(); return;
                }
                File.Copy(srcImg, Path.Combine(lib, "images", id + ".jpg"), true);
                string mockVideo = Path.Combine(tmp, "clip.mp4");
                File.Copy(srcVid, mockVideo, true);

                if (!mock.Start(mockVideo))
                {
                    Log.Note("自检 --cloudflowtest：FAIL 模拟服务端起不来 " + mock.LastError);
                    completed = true; restore(); return;
                }

                ConfigStore.DirOverride = Path.Combine(tmp, "cfg");
                Config.LibRoot = lib;
                Config.GenProvider = "wanx";
                Config.GenApiKey = "MOCK-KEY-FOR-SELFTEST";
                Config.GenModel = "wan2.6-i2v-flash";
                Config.GenResolution = "720P";
                Config.GenSeconds = 3;
                Config.PendingCloudId = "";
                GenApi.EndpointOverride = mock.BaseUrl;
                GenApi.PollIntervalMs = 700;

                ReloadLibrary();
                WallpaperItem pick = FindById(id);
                if (pick == null)
                {
                    Log.Note("自检 --cloudflowtest：FAIL 临时图没进库");
                    restore(); return;
                }

                // ---- 1) 真实点击「云端生成」：应出现确认浮条，写明模型 / 花费 / 等待 ----
                GenCloud(pick);
                string s0 = win == null ? "× 无窗口" : win.SelfTestCloudState();
                bool chooseOk = s0.IndexOf("mode=3") >= 0
                             && s0.IndexOf("约 ¥0.45") >= 0
                             && s0.IndexOf("预计等待") >= 0;
                Log.Note("自检 --cloudflowtest：确认浮条 " + (chooseOk ? "√" : "×") + " " + s0.Replace("\n", "｜"));

                // ---- 2) 真的换一个模型，金额必须跟着变 ----
                string pickLine = win == null ? "× 无窗口" : win.SelfTestCloudModelPick("wan2.6-i2v");
                bool pickOk = pickLine.StartsWith("√") && pickLine.IndexOf("约 ¥1.80") >= 0;
                Log.Note("自检 --cloudflowtest：切换模型 " + pickLine.Replace("\n", "｜"));
                if (win != null) win.SelfTestCloudModelPick("wan2.6-i2v-flash");
                SnapCloudCorner(shotChoose);     // 目视核对：模型 / 花费 / 等待是否都看得见

                // ---- 3) 真实点击「开始生成」：进入等待态并触发后台 HTTP ----
                string confirm = win == null ? "× 无窗口" : win.SelfTestCloudConfirm();
                bool confirmOk = confirm.StartsWith("√") && confirm.IndexOf("mode=0") >= 0;
                Log.Note("自检 --cloudflowtest：点击开始生成 " + confirm);
                ClearPendingCloud();     // 测试片不许换掉主人的桌面

                bool sawWaitUi = false, sawPhaseUi = false;
                System.Text.StringBuilder report = new System.Text.StringBuilder();
                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(900);
                int ticks = 0;
                t.Tick += delegate
                {
                    ticks++;
                    try { SweepCloudJobs(); } catch { }
                    if (ticks == 2) SnapCloudCorner(shotWait);   // 等待中：阶段 / 已等 / 预计剩余 / 花费

                    string st = win == null ? "" : win.SelfTestCloudState();
                    if (st.IndexOf("已等 ") >= 0 && st.IndexOf("大概花费") >= 0) sawWaitUi = true;
                    if (st.IndexOf("已接单") >= 0 || st.IndexOf("模型生成中") >= 0
                        || st.IndexOf("下载中") >= 0) sawPhaseUi = true;

                    string vidPath = Path.Combine(Config.LibRoot, "videos", id + ".mp4");
                    bool hasVideo = File.Exists(vidPath) && new FileInfo(vidPath).Length > 1024;
                    bool archived = File.Exists(Path.Combine(CloudGen.DoneDir, id + ".json"));
                    bool arrived = st.IndexOf("mode=1") >= 0 && st.IndexOf("已到货") >= 0;
                    WallpaperItem it2 = FindById(id);
                    bool indexed = it2 != null && it2.Effect == 2 && it2.Motion;
                    bool httpOk = mock.SubmitCount >= 1 && mock.PollCount >= 3 && mock.DownloadCount >= 1;

                    if (!(hasVideo && archived && arrived && indexed && httpOk) && ticks < 30) return;
                    t.Stop();

                    bool bodyOk = mock.LastBody.IndexOf("\"duration\":3") >= 0
                               && mock.LastBody.IndexOf("\"model\":\"wan2.6-i2v-flash\"") >= 0
                               && mock.LastBody.IndexOf("data:image/jpeg;base64,") >= 0
                               && mock.LastBody.IndexOf("\"watermark\":false") >= 0;
                    bool authOk = mock.LastAuth.IndexOf("MOCK-KEY-FOR-SELFTEST") >= 0
                               && mock.LastAsyncHeader == "enable";
                    bool[] checks = new bool[] { noKeyOk, chooseOk, pickOk, confirmOk, sawWaitUi, sawPhaseUi,
                                                 httpOk, bodyOk, authOk, hasVideo, archived, arrived, indexed };
                    string[] names = new string[] { "未配 Key 提示去设置且不写假工单", "确认浮条", "切换模型改价",
                        "点击后进入等待", "等待态显示花费/已等", "等待态显示阶段", "真 HTTP 提交+轮询+下载",
                        "请求体字段", "鉴权头/异步头", "mp4 真实落盘", "工单归档 done", "浮条到货", "条目入库 effect=2" };
                    bool all = true;
                    for (int i = 0; i < checks.Length; i++)
                    {
                        if (!checks[i]) all = false;
                        report.Append("  ").Append(checks[i] ? "√ " : "× ").Append(names[i]).Append("\r\n");
                    }
                    report.Append("  证据：提交=").Append(mock.SubmitCount)
                          .Append(" 轮询=").Append(mock.PollCount)
                          .Append(" 下载=").Append(mock.DownloadCount)
                          .Append(" body=").Append(mock.LastBody.Length).Append(" 字符\r\n");
                    SnapCloudCorner(shotArrived);    // 到货：浮条变绿、显示模型与花费
                    Log.Note("自检 --cloudflowtest：\r\n" + report.ToString());
                    Log.Note("自检 --cloudflowtest：" + (all ? "PASS" : "FAIL"));
                    completed = all && hasVideo && archived && arrived && indexed && httpOk;
                    restore();
                };
                t.Start();
            }
            catch (Exception ex)
            {
                Log.Note("自检 --cloudflowtest：FAIL 异常 " + ex.GetType().Name + "：" + ex.Message);
                restore();
            }
        }

        /// <summary>
        /// 给这张图下一单「云端动效」。下单同时把库里这条标成 effect=2，
        /// 这样视频到货的那一刻它已经是「云端动效」了，收货逻辑只管换上。
        /// </summary>
        public bool QueueCloudGen(WallpaperItem it, string imagePath, out string msg)
        {
            return QueueCloudGen(it, imagePath, false, out msg);
        }

        /// <summary>
        /// force=true 只来自 RegenCloud（主人明确点了「重新生成一片」并确认过会再花钱）。
        /// 它跳过「有片就播」，但绝不跳过「没配 API 不许写假工单」这道闸。
        /// </summary>
        public bool QueueCloudGen(WallpaperItem it, string imagePath, bool force, out string msg)
        {
            if (it == null) { msg = "条目为空"; return false; }
            if (it.IsVideo) { msg = "这本来就是视频，不用生成"; return false; }

            // 11-14 总闸：任何想下单的路径最后都会到这里。只要库里已经有相关视频，
            // 直接播，绝不写工单、绝不拉起 RunApiJob、绝不向服务商发请求。
            if (!force)
            {
                string existing = VideoFor(it);
                if (existing != null)
                {
                    UseExistingVideo(it, existing, out msg);
                    return false;
                }
            }

            // 11-14：没配好 API 就不许写工单、不许显示「生成中」。
            // 这是总闸 —— 卡片「动态播放」、右键菜单、确认浮条、重新生成、后台调用全都会到这儿。
            // 老逻辑在这里无条件写 JSON，可 GenApi.Ready=false 时没有任何线程会处理它，
            // 界面却切成「☁ 生成中」，主人以为在跑、其实要挂两天才超时。
            if (!GenApi.Ready(Config))
            {
                AskCloudConfig(it, force, out msg);
                if (win != null) win.SetStatus(msg);
                return false;
            }

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                imagePath = LocalImageFor(it);

            bool ok = CloudGen.Enqueue(Config, it, imagePath, force, out msg);
            if (!ok)
            {
                if (win != null)
                {
                    win.SetStatus(msg);
                    // 只扔一句状态栏等于没反馈。这条还在队列里就把浮条亮出来，
                    // 让人看见「排了多久、靠谁生成」；上次失败则给重试入口。
                    CloudGen.State st = CloudGen.StateOf(Config, it);
                    if (st == CloudGen.State.Queued) win.CloudQueued(it.Id);
                    else if (st == CloudGen.State.Manual) win.CloudManual(it.Id, it.Title);
                    else if (st == CloudGen.State.Failed) win.CloudFailed(it.Id, it.Title, "上次没出片");
                }
                return false;
            }

            string err;
            if (LibraryStore.SetMotion(it.Id, Config.LibRoot, true, 2, out err))
            {
                it.Motion = true;
                it.Effect = 2;
            }
            pendingApplyId = it.Id;
            Config.PendingCloudId = it.Id;
            ConfigStore.Save(Config);
            cloudPhase = "";            // 新的一单从「等待中」开始，不残留上单的「完成」

            // 配了 API Key 就在应用里直接生成（不需要外部工具）；没配就还是等外部来取工单。
            // 这条分支就是「打包给别人也能用」的关键：别人填他自己的 Key 就行。
            if (GenApi.Ready(Config))
            {
                string jid = it.Id;
                string jimg = imagePath;
                System.Threading.Thread th = new System.Threading.Thread(
                    new System.Threading.ThreadStart(delegate { RunApiJob(jid, jimg); }));
                th.IsBackground = true;
                th.Start();
            }

            if (win != null)
            {
                GenModelInfo mm = GenModels.Resolve(GenApi.ParseProvider(Config.GenProvider), Config.GenModel);
                int s2 = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                       ? Config.GenSeconds : CloudGen.MaxSeconds;
                win.SetStatus(msg + " · " + mm.Id + " · "
                    + GenModels.CostText(mm, Config.GenResolution, s2) + " · " + CloudChannel()
                    + " · 工单：" + CloudGen.JobPath(it.Id));   // 主人要能自己找到这单
                win.CloudQueued(it.Id);      // 点了就得看得见：阶段、已等、预计剩余、费用
                win.RefreshList();
            }
            return true;
        }

        /// <summary>
        /// 后台跑一次云端生成：提交 → 每 5 秒轮询 → 下载到 videos\id.mp4。
        ///
        /// 落盘之后**不在这里换台**：SweepCloudJobs 每 3 秒扫一次队列，看到片子到货会照常
        /// 入库并自动换上。这样 API 线和「外部工单线」共用同一段收货逻辑，行为不会分叉。
        /// </summary>
        void RunApiJob(string id, string imagePath)
        {
            // 11-14 最后一道闸：真正会掏钱的是下面的 GenApi.Submit。
            // 万一有别的代码绕过了 QueueCloudGen，发请求之前再看一眼有没有现成片子。
            WallpaperItem guardItem = FindById(id);
            if (guardItem != null && VideoFor(guardItem) != null)
            {
                Log.Note("!! 云端(API) 已拦截：这张已有相关视频，绝不重复提交 " + id);
                return;
            }
            string job = CloudGen.JobPath(id);
            int sec = (Config.GenSeconds >= 1 && Config.GenSeconds <= CloudGen.MaxSeconds)
                      ? Config.GenSeconds : CloudGen.MaxSeconds;
            WallpaperItem it = FindById(id);
            string prompt = CloudGen.BuildPrompt(it, sec);
            string who = GenApi.ProviderName(GenApi.ParseProvider(Config.GenProvider));

            cloudPhase = "提交中";
            Log.Note("云端(API)：开始生成 " + id + "（" + who + "，" + sec + " 秒）");
            SetStatusSafe("云端生成中（" + who + "，" + sec + " 秒）…");

            GenApi.SubmitResult sr = GenApi.Submit(Config, imagePath, prompt, sec);
            if (!sr.Ok)
            {
                cloudPhase = "提交失败";
                Log.Note("!! 云端(API) 提交失败：" + sr.Msg);
                SetStatusSafe("云端生成失败：" + sr.Msg + "（详见日志）");
                try { if (File.Exists(job)) CloudGen.MarkFailed(job, id); } catch { }
                return;
            }
            cloudPhase = "已接单，排队中";
            SetStatusSafe("云端已接单（" + sr.TaskId + "），等出片…");

            int start = Environment.TickCount;
            int limitMs = CloudGen.ApiTimeoutMinutes * 60 * 1000;
            int lastLogSec = -100;

            while (Environment.TickCount - start < limitMs)
            {
                System.Threading.Thread.Sleep(GenApi.PollIntervalMs);
                cloudPhase = "模型生成中";

                string vid, msg;
                GenApi.PollState st = GenApi.Poll(Config, sr.TaskId, out vid, out msg);

                if (st == GenApi.PollState.Done)
                {
                    cloudPhase = "下载中";
                    string dst = CloudGen.VideoPath(Config.LibRoot, id);
                    string dmsg;
                    if (!GenApi.Download(vid, dst, out dmsg))
                    {
                        cloudPhase = "下载失败";
                        Log.Note("!! 云端(API) 下载失败：" + dmsg);
                        SetStatusSafe("片子生成了但下载失败：" + dmsg);
                        try { if (File.Exists(job)) CloudGen.MarkFailed(job, id); } catch { }
                        return;
                    }
                    cloudPhase = "完成，等待换上";
                    Log.Note("云端(API)：出片 " + id + " → " + dst + "（" + dmsg + "）");
                    SetStatusSafe("云端生成好了，正在换上");
                    return;     // 剩下的交给 SweepCloudJobs
                }

                if (st == GenApi.PollState.Failed)
                {
                    cloudPhase = "生成失败";
                    Log.Note("!! 云端(API) 失败：" + msg);
                    SetStatusSafe("云端生成失败：" + msg);
                    try { if (File.Exists(job)) CloudGen.MarkFailed(job, id); } catch { }
                    return;
                }

                int el = (Environment.TickCount - start) / 1000;
                if (el - lastLogSec >= 30)
                {
                    lastLogSec = el;
                    Log.Note("云端(API)：等待中 " + el + "s（" + msg + "）");
                }
            }

            cloudPhase = "超时未出片";
            Log.Note("!! 云端(API) 超时（" + CloudGen.ApiTimeoutMinutes + " 分钟）");
            SetStatusSafe("云端生成超时了（片子可能还在排队，稍后可以重试）");
            try { if (File.Exists(job)) CloudGen.MarkFailed(job, id); } catch { }
        }

        /// <summary>后台线程改状态栏：必须回到 UI 线程，不然 WPF 会抛跨线程访问</summary>
        public void SetStatusSafe(string msg)
        {
            try
            {
                if (win == null) return;
                win.Dispatcher.BeginInvoke(new Action(delegate { win.SetStatus(msg); }));
            }
            catch { }
        }

        /// <summary>
        /// 每几秒扫一遍生成队列：视频到货就入库、标云端动效、必要时直接换上；
        /// 超过 30 分钟还没到货就当失败，别让队列永远挂着一条「生成中」。
        /// </summary>
        /// <summary>
        /// 11-15：WorkBuddy 积分通道的收货。WorkBuddy 会话把 mp4 按约定放进 videos\
        /// （规范名 / 备份 / 同画面散片都算），这里就收掉待领工单、把卡片标成动态、
        /// 需要的话自动换上桌面。没到货时什么都不做，绝不假装正在生成。
        /// </summary>
        void SweepBuddyJobs()
        {
            try
            {
                if (!Directory.Exists(CloudGen.BuddyDir)) return;
                string[] jobs = Directory.GetFiles(CloudGen.BuddyDir, "*.json");
                for (int i = 0; i < jobs.Length; i++)
                {
                    string id, image;
                    int sec;
                    long created;
                    if (!CloudGen.ReadJob(jobs[i], out id, out image, out sec, out created)) continue;
                    WallpaperItem it = FindById(id);
                    string vid = it == null ? "" : VideoFor(it);
                    if (string.IsNullOrEmpty(vid) || !File.Exists(vid)) continue;

                    CloudGen.RemoveBuddyJob(id);
                    Log.Note("WorkBuddy 积分通道：到货 " + id + " → " + vid);
                    if (it == null) continue;

                    // 11-15：到货自动换桌面有三种授权来源 ——
                    // ① 本次点击生成时记下的 pendingApplyId（App 没重启）；
                    // ② 付费直连的 PendingCloudId；
                    // ③ 当前桌面上贴的正是这张静态图（跨重启的积压工单也该自动升级成动态）。
                    bool want = (pendingApplyId == id) || (Config.PendingCloudId == id)
                                || (Config.LastId == id);
                    it.Motion = true;
                    it.Effect = 2;
                    // 自检里跑收货时不能真去设系统壁纸 / 拉起播放器
                    if (want && !SelfTestNoPlay)
                    {
                        pendingApplyId = null;
                        if (Config.PendingCloudId == id)
                        {
                            Config.PendingCloudId = "";
                            ConfigStore.Save(Config);
                        }
                        try { SetMotionFx(it, 2); }
                        catch (Exception ex) { Log.Note("!! 到货自动播放失败：" + ex.Message); }
                    }
                    if (win != null)
                    {
                        win.RefreshList();
                        win.SetStatus("WorkBuddy 出片已到货：" + it.Title + "（待领工单已收）");
                    }
                }
            }
            catch (Exception ex) { Log.Note("!! 扫描 WorkBuddy 待领工单失败：" + ex.Message); }
        }

        public void SweepCloudJobs()
        {
            SweepBuddyJobs();       // 积分通道的收货不依赖 queue\ 是否存在
            try
            {
                if (!Directory.Exists(CloudGen.QueueDir)) return;
                string[] jobs = Directory.GetFiles(CloudGen.QueueDir, "*.json");
                for (int i = 0; i < jobs.Length; i++)
                {
                    string id, image;
                    int sec;
                    long created;
                    if (!CloudGen.ReadJob(jobs[i], out id, out image, out sec, out created)) continue;

                    string vid = CloudGen.VideoPath(Config.LibRoot, id);
                    if (!File.Exists(vid))
                    {
                        // 11-14：片子落在别的名字 / _raw / 没带 id 的原始出片，都算到货。
                        // VideoFor 返回的就是能播的那份（有的层不复制归一），工单不用白挂着等超时。
                        // 老代码这里又把 vid 赋回规范路径，等于什么都没做 —— 已修。
                        WallpaperItem pending = FindById(id);
                        if (pending != null)
                        {
                            string found = VideoFor(pending);
                            if (!string.IsNullOrEmpty(found)) vid = found;
                        }
                    }
                    if (File.Exists(vid))
                    {
                        CloudGen.MarkDone(jobs[i], id);
                        Log.Note("云端生成：到货 " + id + " → " + vid
                                 + "（" + new FileInfo(vid).Length / 1024 + "KB）");

                        WallpaperItem it = FindById(id);
                        bool wantThis = (Config.PendingCloudId == id) || (pendingApplyId == id);
                        Log.Note("云端生成：到货处理 id=" + id + "  找到条目=" + (it != null)
                                 + "  要自动换=" + wantThis);
                        if (it != null)
                        {
                            string err;
                            LibraryStore.SetMotion(it.Id, Config.LibRoot, true, 2, out err);
                            it.Motion = true;
                            it.Effect = 2;

                            // 片子到货也走一遍欢迎语：新视频可能是刚下载、从没进过界面的，
                            // 这时候还没有文案；已经有的话就沿用，不覆盖手工那批。
                            string gl;
                            bool newLine = Greeting.Ensure(it.Id, it.Title, it.Tag,
                                ColorTag.Of(!string.IsNullOrEmpty(it.ImagePath) ? it.ImagePath : it.ThumbAbs),
                                out gl);
                            Log.Note("欢迎语：云端出片 " + it.Id + (newLine ? " 新写「" + gl + "」" : " 已有文案，沿用"));

                            if (wantThis)
                            {
                                pendingApplyId = null;
                                Config.PendingCloudId = "";
                                ConfigStore.Save(Config);
                                Log.Note("云端生成：换上新生成的飘动视频（" + it.Title + "）");
                                ApplyItem(it);
                            }
                            else if (win != null)
                            {
                                win.RefreshList();
                                win.SetStatus("云端动效已就绪：" + it.Title);
                            }

                            if (win != null) win.CloudArrived(it.Title, wantThis);
                        }
                        continue;
                    }

                    if (created > 0 && CloudGen.NowSeconds() - created > CloudGen.JobTimeoutMinutes * 60)
                    {
                        CloudGen.MarkFailed(jobs[i], id);
                        Log.Note("云端生成：超时未到货，标记失败 " + id);
                        if (pendingApplyId == id) pendingApplyId = null;
                        if (Config.PendingCloudId == id)
                        {
                            Config.PendingCloudId = "";
                            ConfigStore.Save(Config);
                        }
                        if (win != null)
                        {
                            win.SetStatus("云端生成超时了（超过 " + CloudGen.JobTimeoutMinutes / 60 + " 小时）");
                            WallpaperItem to = FindById(id);
                            win.CloudFailed(id, to == null ? id : to.Title, "超过 " + CloudGen.JobTimeoutMinutes / 60 + " 小时没到货");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Note("!! 扫描生成队列失败：" + ex.Message);
            }
        }

        WallpaperItem FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }


        /// <summary>自检用：当前光标指向的条目 id</summary>
        string IdAt(int i)
        {
            if (i < 0 || i >= Items.Count) return "?";
            return Items[i].Id;
        }

        /// <summary>只找本地已有的图片，不联网（界面要即时返回，不能等下载）</summary>
        string LocalImageFor(WallpaperItem it)
        {
            if (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath)) return it.ImagePath;
            if (!it.IsVideo && !string.IsNullOrEmpty(it.File))
            {
                string own = Path.Combine(Config.LibRoot, it.File.Replace('/', '\\'));
                if (File.Exists(own)) return own;
            }
            if (!string.IsNullOrEmpty(it.ThumbAbs) && File.Exists(it.ThumbAbs)) return it.ThumbAbs;
            return null;
        }

        /// <summary>
        /// 把这张图设成「动态播放」并立刻生效。预览按钮、卡片右键、顶栏都走这里，
        /// 现在只有一档：云端飘动的 3 秒真视频。
        /// 还没生成过就先下单（不先把 effect 改成 2 干等，否则桌面毫无变化，主人以为点了没用）。
        /// </summary>
        public void SetMotionFx(WallpaperItem it, int fx)
        {
            if (it == null || it.IsVideo) return;
            fx = 2;

            if (VideoFor(it) == null)
            {
                string why;
                QueueCloudGen(it, LocalImageFor(it), out why);
                return;
            }

            string err;
            if (!LibraryStore.SetMotion(it.Id, Config.LibRoot, true, fx, out err))
            {
                if (win != null) win.SetStatus("改不了动效： " + err);
                return;
            }

            it.Motion = true;
            it.Effect = fx;
            Log.Note("「" + it.Title + "」动效 = " + fx + "（" + FxName(fx) + "）");

            ApplyItem(it);
            UpdateTrayTip();
            if (win != null) win.RefreshList();
        }

        public void OpenSettings()
        {
            SettingsWindow s = new SettingsWindow(this);
            s.Owner = win;
            s.ShowDialog();
        }

        /// <summary>
        /// 自检用：把设置窗口自己渲染成一张 PNG 存盘。
        /// 为什么要这么绕：这台机器上编辑器会反复抢前台，外部截屏经常抓到别人的窗口，
        /// RenderTargetBitmap 是应用自己画的，窗口被挡住甚至没显示都能出图。
        /// </summary>
        public void SnapSettings(string outPath)
        {
            SnapSettings(outPath, false);
        }

        public void SnapSettings(string outPath, bool scrollToBottom)
        {
            SettingsWindow s = new SettingsWindow(this);
            try { s.Owner = win; } catch { }
            s.Show();
            s.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    if (scrollToBottom) { s.ScrollToEndForTest(); s.UpdateLayout(); }
                    int w = (int)Math.Round(s.ActualWidth);
                    int h = (int)Math.Round(s.ActualHeight);
                    if (w < 10 || h < 10) { Log.Note("!! 设置窗口还没布局好（" + w + "x" + h + "）"); return; }
                    System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                        new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(s);
                    System.Windows.Media.Imaging.PngBitmapEncoder enc =
                        new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (FileStream fs = new FileStream(outPath, FileMode.Create)) enc.Save(fs);
                    Log.Note("自检 --settingstest：已存图 " + outPath + "（" + w + "x" + h + "）");
                }
                catch (Exception ex)
                {
                    Log.Note("!! 设置窗口存图失败：" + ex.GetType().Name + "：" + ex.Message);
                }
                finally
                {
                    try { s.Close(); } catch { }
                }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle, null);
        }

        /// <summary>
        /// 自检用：把主窗口自己渲染成一张 PNG 存盘。
        /// 这台机器上编辑器会反复抢前台，外部截屏经常抓到别人的窗口；
        /// RenderTargetBitmap 是应用自己画的，窗口被挡住甚至没显示都能出图。
        /// </summary>
        public void SnapMain(string outPath)
        {
            if (win == null) return;
            win.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    int w = (int)Math.Round(win.ActualWidth);
                    int h = (int)Math.Round(win.ActualHeight);
                    if (w < 10 || h < 10) { Log.Note("!! 主窗口还没布局好（" + w + "x" + h + "）"); return; }

                    System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                        new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(win);
                    System.Windows.Media.Imaging.PngBitmapEncoder enc =
                        new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (FileStream fs = new FileStream(outPath, FileMode.Create)) enc.Save(fs);
                    Log.Note("自检 --uitest：已存图 " + outPath + "（" + w + "x" + h + "）");
                }
                catch (Exception ex)
                {
                    Log.Note("!! 主窗口存图失败：" + ex.GetType().Name + "：" + ex.Message);
                }
            }), System.Windows.Threading.DispatcherPriority.ContextIdle, null);
        }

        /// <summary>
        /// 自检用：同步渲染主窗口右下角（云端浮条所在位置）成 PNG。
        /// 同步是为了截图和状态严格对应 —— 异步 BeginInvoke 会拍到下一状态的浮条。
        /// </summary>
        public void SnapCloudCorner(string outPath)
        {
            if (win == null) return;
            try
            {
                win.UpdateLayout();
                int w = (int)Math.Round(win.ActualWidth);
                int h = (int)Math.Round(win.ActualHeight);
                if (w < 10 || h < 10) return;

                System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                    new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(win);

                int cw = Math.Min(470, w), ch = Math.Min(360, h);
                int x = Math.Max(0, w - cw - 4), y = Math.Max(0, h - ch - 4);
                System.Windows.Media.Imaging.CroppedBitmap crop =
                    new System.Windows.Media.Imaging.CroppedBitmap(rtb, new Int32Rect(x, y, cw, ch));
                System.Windows.Media.Imaging.PngBitmapEncoder enc =
                    new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(crop));
                using (FileStream fs = new FileStream(outPath, FileMode.Create)) enc.Save(fs);
                Log.Note("自检 --cloudflowtest：已存浮条截图 " + outPath + "（" + cw + "x" + ch + "）");
            }
            catch (Exception ex)
            {
                Log.Note("!! 浮条存图失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }

        /// <summary>设置保存后统一生效：轮换间隔、库路径、标题缓存</summary>
        public void ApplySettings(bool rootChanged)
        {
            if (rotateTimer != null)
            {
                rotateTimer.Stop();
                rotateTimer.Interval = TimeSpan.FromMinutes(Config.IntervalMinutes);
                rotateTimer.Start();
            }
            Translate.Init(Path.Combine(ResourcesDir, "titles-zh.json"),
                           Path.Combine(Config.LibRoot, "zh-cache.json"));
            if (rootChanged)
            {
                ReloadLibrary();
                RestoreLastWallpaper();
            }
            if (win != null)
            {
                win.RefreshList();
                win.SetStatus("设置已保存");
            }
        }

        public void NotifyMinimized()
        {
            if (trayTipShown || tray == null) return;
            trayTipShown = true;
            try
            {
                tray.BalloonTipTitle = "壁纸管家仍在运行";
                tray.BalloonTipText = string.Format("已最小化到托盘，每 {0} 分钟自动换一张。", Config.IntervalMinutes);
                tray.ShowBalloonTip(2000);
            }
            catch { }
        }

        void ShowMain()
        {
            if (win == null) return;
            win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }

        void ExitApp()
        {
            ReallyExit = true;
            try
            {
                if (wtsRegistered && sessionHwnd != IntPtr.Zero) WTSUnRegisterSessionNotification(sessionHwnd);
            }
            catch { }
            try { Dynamic.Stop(); } catch { }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            base.OnExit(e);
        }

        // ---------------- 轮换 ----------------
        void SetupTimer()
        {
            rotateTimer = new DispatcherTimer();
            rotateTimer.Interval = TimeSpan.FromMinutes(Config.IntervalMinutes);
            rotateTimer.Tick += delegate { if (Config.AutoRotate) RotateNext(); };
            rotateTimer.Start();
        }

        /// <summary>
        /// 11-16 功耗守卫：每 60 秒看一次电源状态。
        ///
        /// 只在「电池供电 + 电量不高于阈值」时暂停播放器解码，插电或电量回升自动恢复；
        /// 暂停用的是播放器自己的 Pause 消息，不销毁播放器、不改 index.json 的动效标记，
        /// 所以恢复时不用重新走一遍「找片子 → 开播」。
        ///
        /// 自检不启动这个定时器：否则电池低电会把播放器暂停，
        /// --launchtest / --badgetest 这类断言播放状态的用例会无故漂移。
        /// </summary>
        void SetupPowerWatch()
        {
            if (SelfTestArgs()) return;
            powerTimer = new DispatcherTimer();
            powerTimer.Interval = TimeSpan.FromSeconds(60);
            powerTimer.Tick += delegate { CheckPower(); };
            powerTimer.Start();
            CheckPower();      // 启动时先判一次：插着电没影响，拔着电且电量低就别开播
        }

        public void CheckPower()
        {
            if (!Dynamic.Running)
            {
                pausedByPower = false;
                return;
            }

            string why;
            bool pause = PowerGuard.ShouldPause(Config.PauseOnBattery, Config.BatteryPausePercent, out why);

            if (pause && !pausedByPower)
            {
                Dynamic.Pause();
                pausedByPower = true;
                Log.Note("功耗守卫：暂停动态壁纸解码（" + why + "）");
                if (win != null) win.SetStatus("功耗守卫：" + why + "，已暂停动态壁纸（插电自动恢复）");
            }
            else if (!pause && pausedByPower)
            {
                Dynamic.Resume();
                pausedByPower = false;
                Log.Note("功耗守卫：电源恢复，继续播放动态壁纸");
                if (win != null) win.SetStatus("已恢复动态壁纸播放");
            }
        }

        /// <summary>
        /// 全屏游戏 / 放映 / D3D 全屏时把动态壁纸停一停，退出全屏自动恢复，
        /// 避免和独占全屏抢 GPU 与帧率。
        /// SHQueryUserNotificationState：2=有全屏程序 3=D3D 独占全屏 4=演示模式
        /// </summary>
        void SetupFullscreenWatch()
        {
            fullscreenTimer = new DispatcherTimer();
            fullscreenTimer.Interval = TimeSpan.FromSeconds(3);
            fullscreenTimer.Tick += delegate { CheckFullscreen(); };
            fullscreenTimer.Start();
        }

        void CheckFullscreen()
        {
            if (!Dynamic.Running) return;

            // 自己弹的欢迎动画、大图预览也是全屏窗口，别把它当"用户在全屏看别的"，
            // 否则开机 3 秒就把动态壁纸暂停一次、动画结束再恢复，白白闪一下。
            if (currentGreeting != null) return;
            if (win != null && win.PreviewShowing) return;

            int state = 0;
            if (SHQueryUserNotificationState(out state) != 0) return;

            bool full = (state == 2 || state == 3 || state == 4);
            if (full && !dynamicPausedByFullscreen)
            {
                Dynamic.Pause();
                dynamicPausedByFullscreen = true;
                Log.Note("全屏检测 state=" + state + "：暂停动态壁纸");
                if (win != null) win.SetStatus("检测到全屏程序，动态壁纸已暂停");
            }
            else if (!full && dynamicPausedByFullscreen)
            {
                Dynamic.Resume();
                dynamicPausedByFullscreen = false;
                Log.Note("全屏检测 state=" + state + "：继续动态壁纸");
                if (win != null) win.SetStatus("已退出全屏，动态壁纸继续");
            }
        }

        /// <summary>
        /// 把一条壁纸从库里删掉（磁盘文件一起删）。
        /// 正在播的动态壁纸要先停 —— 不然播放器占着文件句柄，删不掉；
        /// 删的正好是当前壁纸时，自动换到下一张，不留一张"已删除"的空桌面。
        /// </summary>
        public void DeleteItem(WallpaperItem it)
        {
            if (it == null) return;

            bool wasDynamic = (Config.LastDynamicId == it.Id);
            if (wasDynamic)
            {
                StopPlayback();
                MarkStopped();
            }

            string err;
            if (!LibraryStore.Delete(it, Config.LibRoot, out err))
            {
                if (win != null) win.SetStatus("删除失败：" + err);
                Log.Note("删除壁纸失败：" + it.Id + " · " + err);
                return;
            }

            int idx = IndexOf(it);
            Items.Remove(it);
            if (idx >= 0 && cursor >= idx) cursor--;

            // 欢迎语跟着壁纸走：图都删了还留着句子，auto 文件会越攒越脏；
            // 更麻烦的是同 id 再下载回来时，会拿一句旧文案冒充"刚生成的"。
            // 只删自动那份，resources 里手写的谁都不许动。
            if (Greeting.RemoveAuto(it.Id))
                Log.Note("欢迎语：随壁纸删除 " + it.Id);

            bool wasCurrent = (Config.LastId == it.Id);
            if (wasCurrent)
            {
                Config.LastId = "";
                if (win != null) win.SetCurrent(null);

                if (Items.Count > 0)
                {
                    int next = cursor < 0 ? 0 : Math.Min(cursor, Items.Count - 1);
                    cursor = next;
                    ApplyItem(Items[next]);      // 顺手换上一张，桌面不留空
                }
            }

            ConfigStore.Save(Config);
            Log.Note("已删除壁纸：" + it.Id + "（" + it.Title + "）");
            if (win != null)
            {
                win.RefreshList();
                win.SetStatus("已删除：" + it.Title);
            }
        }

        // ---------------- 日常便利：收藏 / 历史回滚 / 导入 / 暂停 ----------------

        /// <summary>最近应用过的壁纸（最多 24 条），F5 一键回滚到上一张</summary>
        readonly List<string> history = new List<string>();

        void PushHistory(WallpaperItem it)
        {
            if (it == null || string.IsNullOrEmpty(it.Id)) return;
            if (history.Count > 0 && history[history.Count - 1] == it.Id) return;
            history.Add(it.Id);
            if (history.Count > 24) history.RemoveAt(0);
        }

        /// <summary>
        /// 回到上一张。连续按就一直往回退（history 里当前这张已经被弹掉了，
        /// ApplyItem 里的 PushHistory 会因为「和末尾相同」而不重复入栈）。
        /// </summary>
        public void BackToPrevious()
        {
            if (history.Count < 2)
            {
                if (win != null) win.SetStatus("还没有可回滚的上一张（这次运行还没换过）");
                return;
            }

            history.RemoveAt(history.Count - 1);          // 当前这张
            string prevId = history[history.Count - 1];
            WallpaperItem it = FindById(prevId);
            if (it == null)
            {
                if (win != null) win.SetStatus("上一张已经不在库里了");
                return;
            }
            ApplyItem(it);
            if (win != null) win.SetStatus("已回滚到上一张：" + it.Title + "（还可继续按 F5 往前退）");
        }


        bool userPaused;

        /// <summary>暂停 / 继续动态壁纸（全局快捷键用）</summary>
        public void TogglePause()
        {
            if (!Config.DynamicEnabled || !Dynamic.Alive)
            {
                if (win != null) win.SetStatus("现在没有在播动态壁纸，先设一张再暂停");
                return;
            }
            userPaused = !userPaused;
            if (userPaused) Dynamic.Pause(); else Dynamic.Resume();
            if (win != null) win.SetStatus(userPaused ? "动态壁纸已暂停（再按一次继续）" : "动态壁纸已继续");
        }

        /// <summary>
        /// 把本地文件 / 文件夹导入库。文件夹会往里走两层，最多收 300 个文件 ——
        /// 防止手一抖把整个桌面或者一个素材盘拖进来。
        /// </summary>
        public void ImportPaths(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            List<string> files = new List<string>();
            for (int i = 0; i < paths.Length; i++)
            {
                try
                {
                    if (Directory.Exists(paths[i]))
                    {
                        files.AddRange(CollectImages(paths[i], 0));
                    }
                    else if (File.Exists(paths[i]))
                    {
                        files.Add(paths[i]);
                    }
                }
                catch { }
                if (files.Count >= 300) break;
            }

            if (files.Count == 0)
            {
                if (win != null) win.SetStatus("这几项里没有能导入的图片或视频（支持 jpg/png/bmp/mp4）");
                return;
            }

            int img = 0, vid = 0, fail = 0;
            string lastErr = "";
            for (int i = 0; i < files.Count; i++)
            {
                string err;
                string id = LibraryStore.ImportFile(files[i], Config.LibRoot, out err);
                if (id == null) { fail++; lastErr = err; continue; }
                if (id.Length > 0)
                {
                    string low = files[i].ToLowerInvariant();
                    if (low.EndsWith(".mp4") || low.EndsWith(".mov") || low.EndsWith(".webm")) vid++;
                    else img++;
                }
            }

            string msg = "导入完成：图片 " + img + " 张";
            if (vid > 0) msg += "、视频 " + vid + " 个";
            if (fail > 0) msg += "，跳过 " + fail + " 个（" + lastErr + "）";
            Log.Note(msg);

            // 拖拽是后台线程进来的：重扫库和刷新界面必须回到 UI 线程
            if (win != null)
            {
                win.Dispatcher.BeginInvoke(new Action(delegate
                {
                    ReloadLibrary();
                    win.RefreshList();
                    win.SetStatus(msg);
                }));
            }
        }

        List<string> CollectImages(string dir, int depth)
        {
            List<string> got = new List<string>();
            if (depth > 2 || got.Count >= 300) return got;
            try
            {
                string[] exts = new string[] { ".jpg", ".jpeg", ".png", ".bmp", ".mp4", ".mov" };
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    for (int i = 0; i < exts.Length; i++)
                        if (exts[i] == ext) { got.Add(f); break; }
                    if (got.Count >= 300) return got;
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    got.AddRange(CollectImages(d, depth + 1));
                    if (got.Count >= 300) return got;
                }
            }
            catch { }
            return got;
        }

        public void RotateNext()
        {
            if (Items.Count == 0)
            {
                win.SetStatus("图库为空，先在 D:\\Wallpaper 放点壁纸再试");
                return;
            }
            cursor = (cursor + 1) % Items.Count;
            ApplyItem(Items[cursor]);
        }

        // ---------------- 设置壁纸 ----------------
        public void ApplyItem(WallpaperItem it)
        {
            ApplyItem(it, true);
        }

        /// <summary>
        /// 应用一条壁纸。
        /// allowMotion=false 是「强制静态」：不管这张有没有动效标记、是不是视频，
        /// 都只设系统静态壁纸（视频退到它的封面图）。托盘的「停止并恢复静态壁纸」
        /// 和关闭动效都走这条路。
        /// </summary>
        public void ApplyItem(WallpaperItem it, bool allowMotion)
        {
            if (it == null) return;
            PushHistory(it);
            cursor = IndexOf(it);

            if (!allowMotion)
            {
                // 这里不要立刻 Dynamic.Stop()：停掉的那一刻桌面会露出上一张系统壁纸
                // （往往正是刚下载的那张），然后才被新壁纸覆盖 —— 看起来就是
                // 「先弹出别的图再切换」。真正的收尾放在 DoSet 里，
                // 等系统壁纸已经换成目标图之后再停。
                // 但状态必须先收干净：以前只把 DynamicEnabled 置 false，
                // LastDynamicId 还指着上一张，角标和看门狗都还认旧片。
                MarkStopped();
            }
            // 视频项不是「一张图」：直接开动态壁纸。
            // 以前这里不分视频/图片，视频项也走 DoSet（把封面 jpg 设成壁纸），
            // 结果点了视频既没有动态壁纸、桌面还被换成了封面图 ——
            // 「换一张图片之后就没动态背景」就是这条路径。
            //
            // 静态图被标成「动态播放」且片子确实在盘上：交给播放器播那段视频，
            // 不走系统壁纸（系统壁纸天生不会动）。没片子就正常当静态图设下去。
            //
            // 11-15：判定统一收口到 MotionCapable —— 它和 LibraryStore.Load 里
            // 保留/降级动效标记用的是同一套 RelatedVideo 发现规则，界面自检也验它，
            // 不再出现「角标说有片、切过去却只设静态壁纸」的两套口径。
            else if (MotionCapable(it))
            {
                StartDynamic(it);
                return;
            }

            // 换成静态图：动态层要在「系统壁纸已经换成新图」之后再停，
            // 这一句就挪到 DoSet 里去了 —— 提前停会让桌面先露出上一张系统壁纸，
            // 看起来像「先弹出别的图再切换」。

            if (!string.IsNullOrEmpty(it.ImagePath) && File.Exists(it.ImagePath))
            {
                DoSet(it, it.ImagePath);
                return;
            }

            if (!it.IsVideo && !string.IsNullOrEmpty(it.ThumbAbs) && File.Exists(it.ThumbAbs))
            {
                DoSet(it, it.ThumbAbs);
                return;
            }

            WallpaperItem captured = it;
            win.SetStatus("正在获取 4K 封面：" + it.Title + " …");
            Task.Run(delegate
            {
                string p = LibraryStore.EnsureCover(captured, Config.LibRoot);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!string.IsNullOrEmpty(p))
                    {
                        captured.ImagePath = p;
                        DoSet(captured, p);
                        if (win != null) win.RefreshList();
                    }
                    else if (!string.IsNullOrEmpty(captured.ThumbAbs) && File.Exists(captured.ThumbAbs))
                    {
                        DoSet(captured, captured.ThumbAbs);
                        win.SetStatus("封面没下下来（可能断网），已用本地缩略图代替");
                    }
                    else
                    {
                        win.SetStatus("设置失败：这张没有可用图片");
                    }
                }));
            });
        }

        public void FetchCover(WallpaperItem it)
        {
            if (it == null) return;
            WallpaperItem captured = it;
            win.SetStatus("正在下载 4K 封面：" + it.Title + " …");
            Task.Run(delegate
            {
                string p = LibraryStore.EnsureCover(captured, Config.LibRoot);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!string.IsNullOrEmpty(p))
                    {
                        captured.ImagePath = p;
                        win.RefreshList();
                        win.SetStatus("封面已保存：" + p);
                    }
                    else
                    {
                        win.SetStatus("封面下载失败，检查网络后重试");
                    }
                }));
            });
        }

        void DoSet(WallpaperItem it, string path)
        {
            // 竖屏图 / 超宽图不能无脑用「填充」：填充按屏幕比例裁切，
            // 9:16 的图会被放大 3~4 倍只剩中间一条。这里先让 WallpaperFit
            // 算出一条合适的路 —— 比例刚好就用原图，不一样就合成一张
            // 「原图完整居中 + 模糊底衬」的屏幕尺寸图。
            int fit = 10;
            string note = "";
            string real = path;
            try
            {
                real = WallpaperFit.Prepare(path, out fit, out note);
            }
            catch (Exception ex)
            {
                Log.Note("壁纸自适应异常，退回填充： " + ex.Message);
                fit = 10;
                real = path;
                note = "";
            }

            bool ok = Desk.SetWallpaper(real, fit);
            if (ok)
            {
                // 动态层等系统壁纸换好了再收：收掉的那一瞬间桌面直接露出新图，
                // 不会先让上一张系统壁纸抢一下镜。
                if (!SelfTestNoPlayerOps && Dynamic.Alive) Dynamic.Stop();

                // 关键：静态壁纸一旦落地，必须关总闸并清掉上次那个 id。
                // 之前这里只停了播放器、没动 DynamicEnabled，看门狗每 4 秒查一次，
                // 连续两次（约 8 秒）看到「动态开着、进程没了」就按 LastDynamicId
                // 把上一张动态壁纸重新拉起来 —— 用户的体感就是
                // 「新图设好没几秒自己弹走，壁纸怎么都换不掉」。
                if (MarkStopped())
                    Log.Note("回到静态壁纸，已关掉自动恢复（防止看门狗把旧动态壁纸拉回来）");

                Config.LastId = it.Id;
                ConfigStore.Save(Config);
                win.SetCurrent(it);
                Log.Note("设为静态壁纸：" + it.Title + "  fit=" + fit +
                         (string.IsNullOrEmpty(note) ? "" : "  (" + note + ")"));

                string msg = "已设为壁纸：" + it.Title + "　分类 " + TagNames.Cn(it.Tag);
                if (!string.IsNullOrEmpty(note)) msg += "　（" + note + "）";
                win.SetStatus(msg);
            }
            else
            {
                // 连静态壁纸都没设上，更不能把上一张的动态视频留在桌面上
                StopPlayback();
                MarkStopped();
                Log.Note("设置壁纸失败：" + real);
                win.SetStatus("设置壁纸失败，路径：" + real);
            }
            UpdateTrayTip();
        }

        int IndexOf(WallpaperItem it)
        {
            for (int i = 0; i < Items.Count; i++) if (Items[i].Id == it.Id) return i;
            return -1;
        }

        // ---------------- 欢迎动画 ----------------
        public void ShowGreeting()
        {
            try
            {
                WallpaperItem cur = cursor >= 0 && cursor < Items.Count ? Items[cursor] : null;
                string tag = cur == null ? "" : cur.Tag;

                string text = Greeting.Compose(cur == null ? "" : cur.Id, tag, rnd);
                string sub;
                if (cur == null) sub = "本地图库 · " + Items.Count + " 张";
                else sub = TagNames.Cn(cur.Tag) + " · " + KindOf(cur) + "　·　" + Clip(cur.Title, 26);

                if (currentGreeting != null)
                {
                    try { currentGreeting.Close(); } catch { }
                    currentGreeting = null;
                }

                GreetingWindow g = new GreetingWindow(text, sub);
                g.ShowActivated = false;      // 解锁后不要抢焦点，盖一下就走
                g.Show();
                currentGreeting = g;
            }
            catch (Exception ex)
            {
                Log.Note("欢迎动画失败：" + ex.Message);
            }
        }

        /// <summary>副标题里那半句「这张是什么」：视频 / 静态图 / 静态图但配了飘动短片</summary>
        static string KindOf(WallpaperItem it)
        {
            if (it.IsVideo) return "动态视频";
            return it.Motion ? "静态图 · 可飘动" : "静态图";
        }

        /// <summary>副标题里的标题可能很长（B 站那几条带全角括号的），超长截断加省略号</summary>
        static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>自检用：把欢迎动画按真实排版渲染成 PNG（不等人眼看屏幕，窗口被挡也能出图）</summary>
        public void SnapGreeting(string outPath, string text, string sub)
        {
            GreetingWindow g = new GreetingWindow(text, sub);
            g.ShowActivated = false;
            g.Show();

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(900);   // 等淡入走完（0.55s）
            t.Tick += delegate
            {
                t.Stop();
                try
                {
                    int w = (int)Math.Round(g.ActualWidth > 0 ? g.ActualWidth : g.Width);
                    int h = (int)Math.Round(g.ActualHeight > 0 ? g.ActualHeight : g.Height);
                    System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                        new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                            System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(g);
                    System.Windows.Media.Imaging.PngBitmapEncoder enc =
                        new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (FileStream fs = new FileStream(outPath, FileMode.Create)) enc.Save(fs);
                    Log.Note("自检 --greettest：已存图 " + outPath + "（" + w + "x" + h + "）");
                }
                catch (Exception ex)
                {
                    Log.Note("!! 欢迎动画存图失败：" + ex.GetType().Name + "：" + ex.Message);
                }
                try { g.Close(); } catch { }
            };
            t.Start();
        }
    }
}
