using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WallpaperManager
{
    /// <summary>
    /// 动态壁纸播放器（独立进程）。
    ///
    /// 实机逐步确认的事实（Win11 26200 = 24H2 之后的桌面架构）：
    ///   ① Progman 扩展样式 = 0x00200080，含 WS_EX_NOREDIRECTIONBITMAP
    ///      → Progman 自己**没有 GDI 重定向表面**，挂在它下面用 GDI 画不出来。
    ///   ② Progman 的子窗口只有两个：SHELLDLL_DefView（图标层，WS_EX_LAYERED 0x00080000）
    ///      与 WorkerW 1920x1080（壁纸宿主，就在 Progman 内部，EnumWindows 看不到）。
    ///   ③ 11-11 10:18 那轮探测：A（WorkerW + GDI 子窗口）、B（WorkerW + UpdateLayeredWindow）、
    ///      C（Progman + UpdateLayeredWindow + 最底层）、D（Progman + GDI 子窗口）全部不可见。
    ///      其中 B/C 在「挂载前」洋红就只有 2.4% / 12.6% —— 说明 **UpdateLayeredWindow 这条路
    ///      本身就没画出东西**，那两组等于没测。真正有效的只有 A、D，都是普通 GDI 子窗口。
    ///
    /// 微软对 24H2 桌面改版的官方口径（raised desktop）：
    ///   应用要自己创建一个 **WS_EX_LAYERED 的子 HWND**，父窗口是 Progman，
    ///   Z 序要**在 DefView 之下、WorkerW 之上**，并且用
    ///   **SetLayeredWindowAttributes(bAlpha = 0xFF)** 而不是 UpdateLayeredWindow，
    ///   这样才能往这个窗口做 DX/GDI 呈现而不掉性能。
    ///   反证：SHELLDLL_DefView 本身就是 Progman 下的 WS_EX_LAYERED 子窗口，它显示得出来，
    ///   说明「分层子窗口能被合成」这条在本机是成立的。
    ///
    /// 11-11 10:30 那轮的两个实验都是无效的（务必记住，别再重复）：
    ///   M1 用 CreateWindowEx(WS_CHILD, parent=Progman) 直接建 → 返回 NULL，err=183；
    ///   M2 同法建出了窗口，但 SetWindowLong 改样式读回还是 0x00000000，
    ///   SetLayeredWindowAttributes 随即报 err=87 —— 那个窗口不由我们掌控，改不动样式。
    ///   结论：**跨进程父子关系只能在建窗之后用 SetParent 建立**，创建时指定父窗口不可用。
    ///   所以本版改成：本进程建弹窗（挂载前不显示，不闪红屏）→ SetParent → 补 WS_CHILD → 设 alpha → 插 Z 序。
    ///
    /// 11-11 10:37 那轮抓到的真正卡点（务必记住）：
    ///   SetParent 文档写得很清楚 —— 为了兼容，它**不会改 WS_CHILD / WS_POPUP 位**。
    ///   所以在改样式之前 GetParent() 返回的是 owner（没设 owner 就是 0x0），
    ///   上一版据此把「挂载成功」误判成「挂载失败」，两组实验全在采样前就 return 了。
    ///   现在顺序固定为：SetParent → SetWindowLong(WS_CHILD，清 WS_POPUP) → 再用
    ///   GetParent + 枚举 Progman 子窗口双重确认。
    /// 绘制统一走 GDI StretchDIBits（一块 32bpp DIB），视频模式复用同一套绘制。
    /// </summary>
    static class PlayerProgram
    {
        internal const string ClsProbe = "WpPlayerProbe";
        internal const string ClsVideo = "WpPlayerWnd";     // 与 Dynamic.PlayerClass 一致

        const uint WS_CHILD = 0x40000000;
        const uint WS_VISIBLE = 0x10000000;
        const uint WS_CLIPSIBLINGS = 0x04000000;
        const uint WS_CLIPCHILDREN = 0x02000000;
        const uint WS_POPUP = 0x80000000;

        const uint WS_EX_TOOLWINDOW = 0x00000080;
        const uint WS_EX_NOACTIVATE = 0x08000000;
        const uint WS_EX_LAYERED = 0x00080000;
        const uint WS_EX_TRANSPARENT = 0x00000020;   // 鼠标穿透：否则桌面右键/框选会被壁纸窗口吃掉
        const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

        const int GWL_STYLE = -16;
        const int GWL_EXSTYLE = -20;

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_FRAMECHANGED = 0x0020;

        const uint WM_CLOSE = 0x0010;
        const uint WM_PAINT = 0x000F;
        const uint WM_COMMAND = 0x0111;
        const int CMD_REFRESH_DESKTOP = 0x7402;

        const uint LWA_ALPHA = 0x00000002;
        const uint ULW_ALPHA = 0x00000002;

        const uint RDW_INVALIDATE = 0x0001;
        const uint RDW_ERASE = 0x0004;
        const uint RDW_ALLCHILDREN = 0x0080;

        const uint SRCCOPY = 0x00CC0020;
        const uint CAPTUREBLT = 0x40000000;

        const int WM_APP = 0x8000;

        // ---------------- 全局状态 ----------------

        internal static IntPtr progman = IntPtr.Zero;
        internal static IntPtr defView = IntPtr.Zero;
        internal static IntPtr worker = IntPtr.Zero;
        internal static int scrW;
        internal static int scrH;

        static IntPtr inst = IntPtr.Zero;
        static IntPtr ourWindow = IntPtr.Zero;
        static bool classReady;
        static volatile string stage = "启动";

        // 视频模式的控制位（由 WndProc 写，解码线程读）
        internal static volatile bool paused;
        internal static volatile bool stopRequested;

        internal static bool Paused { get { return paused; } }
        internal static bool StopRequested
        {
            get { return stopRequested; }
            set { stopRequested = value; }
        }

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        static WndProcDelegate procRef;    // 必须抓引用，否则委托被 GC 后收到消息直接崩进程

        // 唯一一块 32bpp DIB：探测画洋红，视频画解码帧
        internal static IntPtr frameBmp = IntPtr.Zero;
        internal static IntPtr frameBits = IntPtr.Zero;
        internal static int frameW;
        internal static int frameH;
        internal static int FrameW { get { return frameW; } }
        internal static int FrameH { get { return frameH; } }
        internal static int FrameStride { get { return frameW * 4; } }
        internal static IntPtr FrameBits { get { return frameBits; } }
        internal static int ScreenW { get { return scrW; } }
        internal static int ScreenH { get { return scrH; } }
        static BITMAPINFO frameBmi;
        static readonly object paintLock = new object();

        // ---- 比例不符时的「模糊底衬」：1/10 屏幕大小的 32bpp DIB ----
        // 竖屏视频直接按填充画会被裁掉大半，改成「底衬铺满 + 整帧完整居中」。
        // 底衬就是当前帧缩到 1/10 再放大回来（天然模糊），放大那一趟顺手压暗，
        // 成本是每帧多一次小图缩放 + 一次全屏 blit，1080p30 实测无压力。
        static IntPtr blurBmp = IntPtr.Zero;
        static IntPtr blurBits = IntPtr.Zero;
        static IntPtr blurDC = IntPtr.Zero;
        static IntPtr blurOldBmp = IntPtr.Zero;
        static int blurW, blurH;
        static BITMAPINFO blurBmi;
        static bool fitModeWarned;

        // ---- 上屏通道：UpdateLayeredWindow（11-11 11:21 实测唯一能显示在桌面层的方式）----
        static IntPtr ulwDib = IntPtr.Zero;      // 屏幕大小的 32bpp DIB
        static IntPtr ulwBits = IntPtr.Zero;
        static IntPtr ulwDc = IntPtr.Zero;       // 常驻内存 DC，避免每帧 CreateCompatibleDC
        static IntPtr ulwOldBmp = IntPtr.Zero;
        static int ulwW, ulwH;
        static BITMAPINFO ulwBmi;
        static byte ulwAlphaFormat;              // 由 UlwSelfCheck 实测决定（0=AC_SRC_OVER 1=AC_SRC_ALPHA）
        static bool ulwActive;                   // 走 ULW 通道时 WM_PAINT 里不要再叠一层 GDI

        /// <summary>
        /// 挂载方式（--mount=N）。默认 1：Progman 最上层——
        /// 这是 11:21 实测唯一「人眼确认能看到画面」的位置（黄色 V3）。
        /// 0=DefView 最底层（已证不可见）2=DefView 之下 3=WorkerW 4=先置顶再让图标层上来。
        /// </summary>
        internal static int mountMode = 1;

        /// <summary>当前挂载档位（供 PlayerVideo 选择绘制通道）</summary>
        internal static int MountMode { get { return mountMode; } }

        /// <summary>
        /// 交接模式（--keep-old）：换壁纸时管理器先起新播放器、等新窗口画上首帧，
        /// 再收掉旧的。这段时间新旧两个播放器窗口是并存的，所以这里**不能**去清残留
        /// —— 清掉的就是还在给用户撑画面的那一个，等于把桌面提前打回系统静态壁纸，
        /// 看起来就是「先弹出别的图（系统壁纸）再切换」。
        /// </summary>
        internal static bool KeepOld = false;

        // ---------------- 结构体 ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public int fVisible;
            public int fSourceClientAreaOnly;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
        }

        // ---------------- P/Invoke ----------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

        // GW_HWNDPREV=3（Z 序上方）、GW_HWNDNEXT=2（Z 序下方）
        [DllImport("user32.dll")]
        static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        // ---- 未文档化的 Z 序 band（user32.dll 里确认有导出）----
        // 桌面自己就在 ZBID_DESKTOP 这一层，把窗口放进去就能落在「壁纸之上、图标之下」。
        const uint ZBID_DEFAULT = 0;
        const uint ZBID_DESKTOP = 1;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowBand(IntPtr hWnd, IntPtr hWndInsertAfter, uint dwBand);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool GetWindowBand(IntPtr hWnd, out uint pdwBand);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style,
                                            int x, int y, int w, int h,
                                            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hWnd);

        // 跨进程挂载只能靠它：先在本进程建弹窗，再 SetParent 到 Progman。
        // （创建时就把 Progman 当父窗口会失败：一组直接 err=183，一组建出来但改不了样式）
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWnd, IntPtr newParent);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        // 诊断用：DWM 是否把这个窗口「挂起」（cloaked=1 时无论怎么画都看不见）
        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        // 分层窗口的「正规」呈现通道（DefView 这类能看到的分层窗口走的就是这条）
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                                               IntPtr hdcSrc, ref POINT pptSrc, uint crKey,
                                               ref BLENDFUNCTION pblend, uint dwFlags);

        // DWM 缩略图：把「某个窗口的实时内容」投到另一个窗口上（跨进程可用）
        [DllImport("dwmapi.dll")]
        static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")]
        static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")]
        static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        // 关键：24H2 口径要的是这个，不是 UpdateLayeredWindow
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr hWnd, IntPtr rc, bool erase);

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr hWnd, IntPtr rcUpdate, IntPtr rgnUpdate, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // GDI：下面这些导出全在 gdi32.dll（写成 user32 会 EntryPointNotFoundException）
        [DllImport("gdi32.dll", SetLastError = true)]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
                                              out IntPtr bits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern uint GetPixel(IntPtr hdc, int x, int y);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern int StretchDIBits(IntPtr hdc, int xDest, int yDest, int wDest, int hDest,
                                        int xSrc, int ySrc, int wSrc, int hSrc,
                                        IntPtr bits, ref BITMAPINFO bmi, uint usage, uint rop);

        [DllImport("gdi32.dll", SetLastError = true)]
        static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        // ---------------- 入口 ----------------

        [STAThread]
        static void Main(string[] args)
        {
            // winexe 没有控制台，异常一抛进程就静默消失、日志停在最后一行，所以顶层必须兜底
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Note("!!! 未处理异常：" + (ex == null ? "非托管/未知" : ex.GetType().FullName + "：" + ex.Message));
                if (ex != null && ex.StackTrace != null) Log.Note(ex.StackTrace);
            };

            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                Log.Note("!!! 运行中断（阶段：" + stage + "）：" + ex.GetType().FullName + "：" + ex.Message);
                if (ex.StackTrace != null) Log.Note(ex.StackTrace);
            }
        }

        static void Run(string[] args)
        {
            try { SetProcessDPIAware(); }
            catch { }

            Log.Note("=== WallpaperPlayer 启动（参数：" + string.Join(" ", args) + "）===");

            // --mount=N 决定视频窗口挂在哪一层（见 CreateLayerChild 注释）
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--mount="))
                {
                    int m;
                    if (int.TryParse(args[i].Substring(8), out m) && m >= 0 && m <= 8) mountMode = m;
                }
                else if (args[i] == "--keep-old")
                {
                    KeepOld = true;         // 换台交接：别把还在撑画面的旧窗口清掉
                }
            }
            if (mountMode != 0) Log.Note("挂载模式 = " + mountMode + "（" + ZDesc() + "）");

            if (args.Length > 0 && args[0] == "--video")
            {
                StartVideo(args);
                return;
            }

            // 上屏通道自检：顶层分层窗 + UlwPresent(AC_SRC_OVER)
            if (args.Length > 0 && args[0] == "--ulwcheck")
            {
                UlwCheck();
                return;
            }

            // 第二轮探测：原生 WS_CHILD 建窗 + 每个变体真截图（Probe2.cs）
            if (args.Length > 0 && args[0] == "--probe2")
            {
                Probe2.Run();
                return;
            }

            int probeSeconds = 6;
            if (args.Length >= 2 && args[0] == "--probe")
            {
                int v;
                if (int.TryParse(args[1], out v) && v > 0) probeSeconds = v;
            }
            RunProbe(probeSeconds);
        }

        /// <summary>
        /// 视频模式在独立文件 PlayerVideo.cs + MfVideo.cs 里。
        /// 用反射调用，是为了「万一那次构建没把视频文件编进来，探测模式照样能跑」，
        /// build.bat 里有对应退路：视频那三个文件编不过就只编探测。
        /// </summary>
        static void StartVideo(string[] args)
        {
            string path = "";
            int fps = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--video") path = args[i + 1];
                else if (args[i] == "--fps") int.TryParse(args[i + 1], out fps);
            }
            Log.Note("视频模式：path=" + path + " fps=" + fps);
            if (path.Length == 0 || !System.IO.File.Exists(path))
            {
                Log.Note("!!! 视频文件不存在，放弃播放");
                return;
            }

            Type t = Type.GetType("WallpaperManager.PlayerVideo");
            if (t == null)
            {
                Log.Note("!!! 本次构建未包含视频模式（PlayerVideo.cs / MfVideo.cs 没编进来），只有探测可用");
                return;
            }
            System.Reflection.MethodInfo mi = t.GetMethod("Play",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (mi == null)
            {
                Log.Note("!!! 视频模式入口 Play() 缺失");
                return;
            }
            try
            {
                mi.Invoke(null, new object[] { path, fps });
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException != null ? ex.InnerException : ex;
                Log.Note("!!! 视频模式异常：" + inner.GetType().FullName + "：" + inner.Message);
                if (inner.StackTrace != null) Log.Note(inner.StackTrace);
            }
        }

        // ---------------- 桌面环境 ----------------

        /// <summary>
        /// 上屏通道自检：建一个**顶层**分层窗口，用 UlwPresent 提交整屏洋红，再抓屏看命中率。
        /// 顶层窗口能被 StretchBlt 抓到，所以这一条能独立回答「AlphaFormat=0 提交的内容
        /// 到底是不是不透明的」，跟桌面层那些抓不到的变体无关。
        /// </summary>
        static void UlwCheck()
        {
            if (!InitDesktop()) return;
            if (!BufferInit(scrW, scrH)) { Log.Note("!! 帧缓冲创建失败"); return; }
            FillBuffer(255, 0, 255);                  // 洋红（B=255 G=0 R=255）
            if (!UlwPrepare(scrW, scrH)) { Log.Note("!! 呈现缓冲创建失败"); return; }

            IntPtr hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                                         ClsProbe, "WpUlwCheck", WS_POPUP, 0, 0, scrW, scrH,
                                         IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Note("!! 建窗失败 err=" + Marshal.GetLastWin32Error());
                return;
            }
            ourWindow = hwnd;

            bool ok = UlwPresent(hwnd);
            Log.Note("[ULW] 首次提交 " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
            ShowWindow(hwnd, 4 /* SW_SHOWNA */);
            Pump(500);
            ok = UlwPresent(hwnd);
            Log.Note("[ULW] 二次提交 " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
            Pump(600);

            double pct = SamplePercent("ULW 自检");
            Log.Note("[ULW] 屏幕洋红 " + pct.ToString("0.0") + "%"
                     + (pct > 40 ? "（通道正常：AC_SRC_OVER 提交的内容不透明、能上屏）"
                                 : "（!! 提交成功但看不见，说明这个混合参数不对，得换）"));

            Cleanup();
            UlwRelease();
            RefreshDesktop();
        }

        /// <summary>找 Progman / DefView / 壁纸宿主 WorkerW，注册窗口类。成功返回 true</summary>
        internal static bool InitDesktop()
        {
            stage = "环境信息";
            scrW = GetSystemMetrics(0);
            scrH = GetSystemMetrics(1);
            Log.Note("[1] 屏幕 " + scrW + "x" + scrH);

            progman = FindWindow("Progman", null);
            Log.Note("[1] Progman=0x" + progman.ToString("x"));
            if (progman == IntPtr.Zero)
            {
                Log.Note("结论：找不到 Progman，桌面没起来");
                return false;
            }

            // 老系统靠这条消息拆出 WorkerW；24H2 上它不产生新窗口，发了也不亏
            IntPtr dummy;
            SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1200, out dummy);

            uint pex = (uint)GetWindowLong(progman, GWL_EXSTYLE);
            Log.Note("[1] Progman 扩展样式=0x" + pex.ToString("x8")
                     + (((pex & WS_EX_NOREDIRECTIONBITMAP) != 0)
                        ? "（含 NOREDIRECTIONBITMAP → 它自己没 GDI 表面，只能靠分层子窗口）"
                        : "（不含 NOREDIRECTIONBITMAP）"));

            defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            Log.Note("[1] DefView=0x" + defView.ToString("x"));
            if (defView == IntPtr.Zero)
                Log.Note("    !! 找不到 SHELLDLL_DefView：Z 序只能插到最上层，那样会盖住桌面图标（结果仅供参考）");

            DumpProgmanOrder("[1] 初始");

            // 壁纸宿主就在 Progman 内部，按「类名 WorkerW + 铺满屏幕」筛
            stage = "枚举 Progman 子窗口";
            IntPtr c = IntPtr.Zero;
            for (int i = 0; i < 16; i++)
            {
                c = FindWindowEx(progman, c, null, null);
                if (c == IntPtr.Zero) break;
                if (ClassOf(c) != "WorkerW") continue;
                RECT r;
                GetWindowRect(c, out r);
                if (r.Right - r.Left >= scrW - 8 && r.Bottom - r.Top >= scrH - 8)
                {
                    worker = c;
                    break;
                }
            }
            Log.Note("[1] 壁纸宿主 WorkerW=0x" + worker.ToString("x")
                     + (worker == IntPtr.Zero ? "（没找到，不影响：我们要插在 DefView 正下方）" : ""));

            stage = "注册窗口类";
            return EnsureClass();
        }

        static bool EnsureClass()
        {
            if (classReady) return true;

            inst = GetModuleHandle(null);
            procRef = new WndProcDelegate(WndProc);

            ushort a1 = RegisterWindowClass(ClsProbe);
            ushort a2 = RegisterWindowClass(ClsVideo);
            Log.Note("[2] RegisterClassEx Probe=" + a1 + " Video=" + a2);
            if (a1 == 0 || a2 == 0)
            {
                Log.Note("结论：ERROR —— 窗口类注册失败，err=" + Marshal.GetLastWin32Error());
                return false;
            }
            classReady = true;
            return true;
        }

        static ushort RegisterWindowClass(string name)
        {
            WNDCLASSEXW wc = new WNDCLASSEXW();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
            wc.style = 0x0002 | 0x0020;          // CS_HREDRAW | CS_VREDRAW
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procRef);
            wc.hInstance = inst;
            wc.hbrBackground = IntPtr.Zero;      // 自己画，不要系统擦背景
            wc.lpszClassName = name;
            return RegisterClassEx(ref wc);
        }

        // ---------------- 探测 ----------------

        class Attempt
        {
            public string Key;          // A/B/...，汇总表里用
            public string Label;
            public int Mode;
            public int Target;          // 0=Progman  1=WorkerW  2=SHELLDLL_DefView
            public Attempt(string key, string label, int mode, int target)
            {
                Key = key;
                Label = label;
                Mode = mode;
                Target = target;
            }
        }

        // 九条路线，各用一次真实呈现来判定，不再「一轮只验一个假设」
        const int M_LAYER_FIRST = 1;   // 建窗即带 LAYERED → 挂载 → SWA(255) → GDI 画
        const int M_LAYER_AFTER = 2;   // 挂载后再补 LAYERED → SWA(255) → GDI 画
        const int M_ULW = 3;           // 挂载后每帧 UpdateLayeredWindow（走 DWM 分层表面）
        const int M_DIRECT_DC = 4;     // 不建子窗口，直接往 Progman 的 DC 画
        const int M_THUMB = 5;         // DwmRegisterThumbnail 把本进程窗口投影到 Progman

        static void RunProbe(int seconds)
        {
            // 9 组方案，每组约 3-4 秒。看门狗 = 预算 + 30 秒，必须早于管理器超时（预算*2+20），
            // 否则「卡在哪一步」这条关键信息会被管理器的强杀吞掉。
            StartWatchdog(seconds + 30);

            if (!InitDesktop()) return;

            // 上一轮残留的播放器窗口会压住图标层，先收掉
            stage = "清残留窗口";
            CloseLeftovers();

            stage = "建帧缓冲";
            if (!BufferInit(scrW, scrH))
            {
                Log.Note("结论：ERROR —— 帧缓冲创建失败，绘制通道不可用");
                return;
            }
            FillChecker();                // 洋红/深灰棋盘格：命中时人眼一看就知道

            // [自检]：顶层普通弹窗。这一句验证「StretchDIBits 绘制 + 采样」本身没问题，
            // 后面如果不可见，就能确定是「合成」的问题，而不是「没画上」。
            stage = "[自检]";
            ourWindow = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClsProbe, "WpProbeSelf",
                                       WS_POPUP | WS_VISIBLE, 0, 0, scrW, scrH,
                                       IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (ourWindow == IntPtr.Zero)
            {
                Log.Note("[自检]窗建不出来 err=" + Marshal.GetLastWin32Error() + "，继续往下试");
            }
            else
            {
                PaintWindow(ourWindow);
                Pump(300);
                double pre = SamplePercent("自检");
                Log.Note("[自检]棋盘格洋红 " + pre.ToString("0.0") + "%（顶层弹窗，应接近 50%）");
                if (pre < 25)
                    Log.Note("    !! 自检就偏低：说明绘制或采样通道本身有问题，后面的『不可见』不能全信");
                DestroyWindow(ourWindow);
                ourWindow = IntPtr.Zero;
            }

            // 九条路线一次跑完，逐项独立判定；任何一组失败或抛异常都不影响后面的组
            List<Attempt> list = new List<Attempt>();
            list.Add(new Attempt("A", "Progman 子窗 · 建窗即带 LAYERED · SWA + GDI", M_LAYER_FIRST, 0));
            list.Add(new Attempt("B", "Progman 子窗 · 每帧 UpdateLayeredWindow", M_ULW, 0));
            list.Add(new Attempt("C", "WorkerW 子窗 · 建窗即带 LAYERED · SWA + GDI", M_LAYER_FIRST, 1));
            list.Add(new Attempt("D", "WorkerW 子窗 · 每帧 UpdateLayeredWindow", M_ULW, 1));
            list.Add(new Attempt("E", "DefView 子窗 · 建窗即带 LAYERED · SWA + GDI", M_LAYER_FIRST, 2));
            list.Add(new Attempt("F", "不建子窗口 · 直接往 Progman 的 DC 上 StretchDIBits", M_DIRECT_DC, 0));
            list.Add(new Attempt("G", "DwmRegisterThumbnail：把本进程顶层窗口投影到 Progman", M_THUMB, 0));
            list.Add(new Attempt("H", "Progman 子窗 · 挂载后补 LAYERED（已证失败，留作对照）", M_LAYER_AFTER, 0));
            list.Add(new Attempt("I", "不建子窗口 · 直接往 WorkerW（壁纸宿主）的 DC 上画", M_DIRECT_DC, 1));

            string winner = null;
            StringBuilder summary = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                Attempt a = list[i];
                bool ok = false;
                try
                {
                    if (a.Mode == M_DIRECT_DC)
                    {
                        IntPtr dcTarget = a.Target == 1 ? worker : progman;
                        ok = RunDirectDC(scrW, scrH, dcTarget, a.Target == 1 ? "WorkerW" : "Progman");
                    }
                    else if (a.Mode == M_THUMB) ok = RunThumbnail(scrW, scrH);
                    else ok = RunAttempt(a, scrW, scrH, i, list.Count);
                }
                catch (Exception ex)
                {
                    Log.Note("  ✘ 这一组抛异常：" + ex.GetType().Name + "：" + ex.Message);
                }
                finally
                {
                    stage = "收尾 " + a.Key;
                    try { Cleanup(); } catch { }
                    try { RefreshDesktop(); } catch { }
                }

                if (ok && winner == null) winner = a.Key + " · " + a.Label;
                summary.Append(a.Key).Append(ok ? "=可见  " : "=不可见  ");
            }

            stage = "收尾";
            Log.Note("");
            Log.Note("[汇总] " + summary.ToString().TrimEnd());
            Log.Note("");
            if (winner != null)
            {
                Log.Note("结论：VISIBLE —— 可用方案：" + winner);
                Log.Note("下一步：我按这条方案接视频帧（同一套挂载 + 呈现），你就能看到动态壁纸了");
            }
            else
            {
                Log.Note("结论：NOT_VISIBLE —— 九条路全不可见，GDI 与分层表面都进不了桌面层");
                Log.Note("下一步：把这份日志发我，我改用 D3D11 + DirectComposition 交换链（视频帧本来就在 GPU 上）");
            }

            Cleanup();
            RefreshDesktop();
            Log.Note("=== 探测结束（阶段=" + stage + "）===");
        }

        static bool RunAttempt(Attempt a, int sw, int sh, int idx, int total)
        {
            string tag = "[" + a.Key + "]";
            Log.Note("");
            Log.Note("===== " + tag + " " + a.Label + " =====");

            // 目标父窗口
            IntPtr target = a.Target == 1 ? worker : (a.Target == 2 ? defView : progman);
            string tname = a.Target == 1 ? "WorkerW" : (a.Target == 2 ? "DefView" : "Progman");
            if (target == IntPtr.Zero)
            {
                Log.Note("  目标窗口 " + tname + " 不存在，跳过");
                return false;
            }

            // 一律「本进程建弹窗 → SetParent 挂载」。创建时直接指定跨进程父窗口会被系统拒绝。
            stage = tag + " 建顶层弹窗";
            uint ex = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (a.Mode == M_LAYER_AFTER ? 0u : WS_EX_LAYERED);
            IntPtr hwnd = CreateWindowEx(ex, ClsProbe, "WpProbe",
                                         WS_POPUP, 0, 0, sw, sh,          // 先不 WS_VISIBLE：挂载前不闪屏
                                         IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Note("  建窗失败 err=" + Marshal.GetLastWin32Error() + "，跳过这组");
                return false;
            }
            ourWindow = hwnd;
            Log.Note("  弹窗=0x" + hwnd.ToString("x") + " 扩展样式=0x"
                     + ((uint)GetWindowLong(hwnd, GWL_EXSTYLE)).ToString("x8")
                     + " 样式=0x" + ((uint)GetWindowLong(hwnd, GWL_STYLE)).ToString("x8"));

            if (a.Mode == M_LAYER_FIRST)
            {
                bool okA0 = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                Log.Note("  挂载前 SetLayeredWindowAttributes " + (okA0 ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
            }

            // 挂载
            stage = tag + " SetParent(" + tname + ")";
            IntPtr prev = SetParent(hwnd, target);
            Log.Note("  SetParent(" + tname + ") 返回旧父=0x" + prev.ToString("x") + " err="
                     + Marshal.GetLastWin32Error() + "（旧的父是桌面窗口，非 0 属正常）");

            // ★ SetParent 文档明写「为兼容不改 WS_CHILD / WS_POPUP 位」：
            //   传完 parent 时窗口仍是 WS_POPUP，GetParent 返回的是 owner（没 owner 就是 0x0）。
            //   所以必须先把样式改成 WS_CHILD（同时清掉 WS_POPUP），再判断父窗口。
            stage = tag + " 改 WS_CHILD";
            int want = (int)(WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN);
            int oldStyle = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, want);
            int nowStyle = GetWindowLong(hwnd, GWL_STYLE);
            Log.Note("  样式 0x" + ((uint)oldStyle).ToString("x8") + " -> 0x"
                     + ((uint)nowStyle).ToString("x8")
                     + (((nowStyle & (int)WS_CHILD) != 0) ? "（WS_CHILD 已生效）" : "（!! WS_CHILD 没生效）"));

            IntPtr par = GetParent(hwnd);
            bool inTarget = IsChildOf(target, hwnd);
            Log.Note("  父窗口=0x" + par.ToString("x") + "，枚举 " + tname + " 子窗口"
                     + (inTarget ? "能查到本窗口" : "查不到本窗口"));
            bool mounted = (par == target) || inTarget;
            if (!mounted)
            {
                Log.Note("  ✘ 没挂上 " + tname + "，跳过这组");
                Cleanup();
                return false;
            }

            if (a.Mode == M_LAYER_AFTER)
            {
                stage = tag + " 补 WS_EX_LAYERED";
                int ex0 = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, (int)((uint)ex0 | WS_EX_LAYERED));
                int ex1 = GetWindowLong(hwnd, GWL_EXSTYLE);
                Log.Note("  扩展样式 0x" + ((uint)ex0).ToString("x8") + " -> 0x" + ((uint)ex1).ToString("x8"));
            }

            if (a.Mode != M_ULW)
            {
                // 分层窗口必须先设属性，否则系统认为「没内容」而不显示
                stage = tag + " SetLayeredWindowAttributes(255)";
                bool okA = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                int errA = Marshal.GetLastWin32Error();
                Log.Note("  SetLayeredWindowAttributes(0,255,LWA_ALPHA) " + (okA ? "成功" : "失败 err=" + errA));
                if (!okA)
                {
                    Log.Note("  ✘ 这组判失败（没有 LAYERED 位，后面的采样没有意义）");
                    Cleanup();
                    return false;
                }
            }

            // Z 序：挂 Progman 时插到 DefView 之后（图标层下方、壁纸之上 = 壁纸该在的那一层）；
            // 挂 WorkerW / DefView 时直接置顶，先保证「能不能看见」这个判定成立。
            stage = tag + " 插入 Z 序";
            IntPtr insertAfter = a.Target == 0 ? defView : IntPtr.Zero;
            bool okP = SetWindowPos(hwnd, insertAfter, 0, 0, sw, sh,
                                    SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            int errP = Marshal.GetLastWin32Error();
            RECT rc;
            GetWindowRect(hwnd, out rc);
            Log.Note("  SetWindowPos " + (okP ? "成功" : "失败 err=" + errP)
                     + "　矩形 " + rc.Left + "," + rc.Top + " " + (rc.Right - rc.Left) + "x" + (rc.Bottom - rc.Top));
            DumpProgmanOrder(tag + " 插入后");

            stage = tag + " 绘制";
            PaintByMode(a, hwnd, sw, sh);

            // 诊断：DWM 若把这个窗口 cloaked（挂起），画得再好也不会出现在屏幕上
            try
            {
                int cloaked;
                int hr = DwmGetWindowAttribute(hwnd, 14 /*DWMWA_CLOAKED*/, out cloaked, 4);
                Log.Note("  DWM 状态 hr=0x" + hr.ToString("x") + " cloaked=" + cloaked
                         + (hr == 0 && cloaked != 0 ? "（!! 被 DWM 挂起 → 根本不会显示）" : ""));
            }
            catch { }

            bool hit = false;
            for (int i = 0; i < 2 && !hit; i++)
            {
                Pump(i == 0 ? 450 : 650);
                stage = tag + " 采样 " + (i + 1);
                double pct = SamplePercent("第 " + (i + 1) + " 次");
                Log.Note("  第 " + (i + 1) + " 次采样：屏幕洋红 " + pct.ToString("0.0") + "%");
                if (pct > 5.0) hit = true;
                else if (i == 0) PaintByMode(a, hwnd, sw, sh);
            }

            // 原位看不见 → 再插到最上层试一次。
            // 这一步只为把两种失败分开：插到图标层之上还看不见＝「根本不被合成」；
            // 看得见＝「合成没问题，只是 Z 序 / 被壁纸层盖住」。
            if (!hit && a.Target == 0)
            {
                stage = tag + " 置顶复查";
                Log.Note("  Z 序改成最上层再采一次（会盖住图标约 700ms，用来区分『不被合成』和『Z 序不对』）");
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, sw, sh,
                             SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                DumpProgmanOrder(tag + " 置顶后");
                PaintByMode(a, hwnd, sw, sh);
                Pump(700);
                double pct2 = SamplePercent("置顶后");
                Log.Note("  置顶后采样：屏幕洋红 " + pct2.ToString("0.0") + "%");
                hit = pct2 > 5.0;
            }

            // 命中时别急着销毁：留 2 秒让用户用眼睛直接确认（棋盘格盖住壁纸，非常显眼）
            if (hit)
            {
                stage = tag + " 留窗确认";
                FillChecker();
                PaintByMode(a, hwnd, sw, sh);
                Log.Note("  ★ 请看桌面：接下来 2 秒应出现「紫/深灰棋盘格」");
                Pump(2000);
            }

            // 无论成败立刻销毁，绝不留窗口压住桌面图标
            stage = tag + " 销毁窗口";
            Cleanup();

            Log.Note(hit ? "  ✔ 命中：这组能显示在桌面层" : "  ✘ 不可见");
            return hit;
        }

        /// <summary>按方案选择呈现方式：SWA+GDI，或者每帧 UpdateLayeredWindow</summary>
        static void PaintByMode(Attempt a, IntPtr hwnd, int sw, int sh)
        {
            if (a.Mode == M_ULW)
            {
                bool ok = UlwPaint(hwnd, sw, sh);
                Log.Note("  UpdateLayeredWindow " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
            }
            else
            {
                PaintWindow(hwnd);
                InvalidateRect(hwnd, IntPtr.Zero, false);
            }
        }

        /// <summary>用 UpdateLayeredWindow 把整块 DIB 交给 DWM（分层窗口的「正规」呈现通道）</summary>
        static bool UlwPaint(IntPtr hwnd, int w, int h)
        {
            if (frameBmp == IntPtr.Zero) return false;
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            IntPtr mem = CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screen); return false; }
            IntPtr old = SelectObject(mem, frameBmp);
            try
            {
                POINT dst; dst.X = 0; dst.Y = 0;
                SIZE sz; sz.CX = w; sz.CY = h;
                POINT src; src.X = 0; src.Y = 0;
                BLENDFUNCTION bf;
                bf.BlendOp = 0;                 // AC_SRC_OVER
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = 1;             // AC_SRC_ALPHA
                return UpdateLayeredWindow(hwnd, screen, ref dst, ref sz, mem, ref src, 0, ref bf, ULW_ALPHA);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---------------- 常驻上屏通道（视频每帧走这里）----------------
        //
        // 11-11 11:21 实机确认：普通 GDI 子窗口（不管挂 Progman / WorkerW / DefView、
        // 不管 Z 序）屏幕上 0 像素；**只有 UpdateLayeredWindow 提交的位图会被合成**。
        // 所以视频帧改走这条通道：解码 → 呈现缓冲 → ULW。

        /// <summary>建一块屏幕大小的常驻 32bpp 呈现缓冲（只建一次，不每帧分配）</summary>
        internal static bool UlwPrepare(int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            if (ulwDib != IntPtr.Zero && ulwW == w && ulwH == h) return true;
            UlwRelease();

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;      // 自上而下，与视频帧一致
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;  // BI_RGB

                IntPtr bits;
                IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    Log.Note("  CreateDIBSection(呈现缓冲) 失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                IntPtr dc = CreateCompatibleDC(screen);
                if (dc == IntPtr.Zero)
                {
                    DeleteObject(dib);
                    Log.Note("  CreateCompatibleDC(呈现缓冲) 失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }

                ulwDib = dib;
                ulwBits = bits;
                ulwDc = dc;
                ulwOldBmp = SelectObject(dc, dib);
                ulwW = w;
                ulwH = h;
                ulwBmi = bmi;
                ulwActive = true;
                Log.Note("  呈现缓冲就绪 " + w + "x" + h + "（UpdateLayeredWindow 通道）");
                return true;
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        internal static void UlwRelease()
        {
            try
            {
                if (ulwDc != IntPtr.Zero)
                {
                    if (ulwOldBmp != IntPtr.Zero) SelectObject(ulwDc, ulwOldBmp);
                    DeleteDC(ulwDc);
                }
            }
            catch { }
            try { if (ulwDib != IntPtr.Zero) DeleteObject(ulwDib); }
            catch { }
            ulwDc = IntPtr.Zero; ulwOldBmp = IntPtr.Zero;
            ulwDib = IntPtr.Zero; ulwBits = IntPtr.Zero;
            ulwW = 0; ulwH = 0;
            ulwActive = false;
        }

        /// <summary>
        /// 把当前帧交给 DWM：先把帧缩放进呈现缓冲，再一次 UpdateLayeredWindow。
        /// AlphaFormat 用 AC_SRC_OVER(0) 而不是 AC_SRC_ALPHA(1)：解码出来的 RGB32 第 4 字节
        /// 一般是 0，用逐像素 alpha 会整块全透明；AC_SRC_OVER 只认 SourceConstantAlpha=255，
        /// 结果是整块不透明，正好适合壁纸。
        /// </summary>
        /// <summary>
        /// 统一的一帧提交入口。
        ///   mountMode 8：官方口径的 WS_EX_LAYERED 子窗口 —— 走 GDI StretchDIBits 直画窗口 DC；
        ///   其它档位：走 UpdateLayeredWindow 提交整屏位图。
        /// 这两条路互斥：同一个窗口一旦用过 ULW，就再也不能靠 GDI 画进去了。
        /// </summary>
        internal static bool PresentFrame(IntPtr hwnd)
        {
            if (mountMode == 8)
            {
                PaintWindow(hwnd);
                return true;
            }
            return UlwPresent(hwnd);
        }

        internal static bool UlwPresent(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || ulwDc == IntPtr.Zero || frameBits == IntPtr.Zero) return false;

            lock (paintLock) { PaintTo(ulwDc, ulwW, ulwH); }

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                POINT dst; dst.X = 0; dst.Y = 0;
                SIZE sz; sz.CX = ulwW; sz.CY = ulwH;
                POINT src; src.X = 0; src.Y = 0;
                BLENDFUNCTION bf;
                bf.BlendOp = 0;                 // AC_SRC_OVER
                bf.BlendFlags = 0;
                bf.SourceConstantAlpha = 255;
                bf.AlphaFormat = ulwAlphaFormat;

                bool ok = UpdateLayeredWindow(hwnd, screen, ref dst, ref sz, ulwDc, ref src, 0, ref bf, ULW_ALPHA);
                if (!ok)
                {
                    int e1 = Marshal.GetLastWin32Error();
                    bf.AlphaFormat = (byte)(ulwAlphaFormat == 0 ? 1 : 0);
                    ok = UpdateLayeredWindow(hwnd, screen, ref dst, ref sz, ulwDc, ref src, 0, ref bf, ULW_ALPHA);
                    Log.Note("  ULW 提交失败 err=" + e1 + "，换混合参数重试 → " + (ok ? "成功" : "仍失败 err=" + Marshal.GetLastWin32Error()));
                }
                return ok;
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>
        /// 上屏通道实测：拿一块**顶层**分层窗口分别用两种混合参数提交整屏洋红，看哪种真的出现在屏幕上。
        /// 顶层窗口能被 StretchBlt 抓到，判定可靠；桌面层那些变体抓不到，所以不能这么测。
        /// 结论写进 ulwAlphaFormat，视频每帧按它提交。整段约 0.6 秒。
        /// </summary>
        internal static bool UlwSelfCheck()
        {
            if (!EnsureClass()) return false;
            if (!BufferInit(scrW, scrH)) return false;
            if (!UlwPrepare(scrW, scrH)) return false;

            FillBuffer(255, 0, 255);               // 洋红：颜色极端，抓屏判定最稳

            IntPtr hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED,
                                         ClsProbe, "WpUlwCheck", WS_POPUP, 0, 0, scrW, scrH,
                                         IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Log.Note("  ULW 自检：建窗失败 err=" + Marshal.GetLastWin32Error());
                return false;
            }

            bool ok = false;
            try
            {
                // 只用 AC_SRC_OVER(0) + SourceConstantAlpha=255：这条语义是「整块不透明」，
                // 跟帧里第 4 字节是什么无关。AC_SRC_ALPHA(1) 依赖逐像素 alpha，
                // MF 解出的 RGB32 里 alpha 常常是 0，用它就会整块全透明（表现为视频在播但屏幕全黑）。
                // 所以这里不「自动挑一个」，固定用 0，自检只负责确认通道能上屏。
                ulwAlphaFormat = 0;
                bool posted = UlwPresent(hwnd);
                ShowWindow(hwnd, 4 /* SW_SHOWNA */);
                Pump(220);
                double pct = SamplePercent("ULW 自检");
                // 判定只看「提交调用是否成功」：桌面层/分层窗口的合成结果从本进程抓屏
                // 抓不到（11:21 实测：屏幕上明明有洋红，抓回来还是原壁纸），拿百分比判会误报。
                ok = posted;
                Log.Note("  ULW 自检：提交" + (posted ? "成功（屏幕这 0.2 秒的洋红就是它，说明通道是通的）"
                                                    : "失败 err=" + Marshal.GetLastWin32Error())
                         + "，抓屏洋红 " + pct.ToString("0.0") + "%（这个数不可信，仅记录）");
            }
            finally
            {
                DestroyWindow(hwnd);
            }

            FillBuffer(0, 0, 0);
            RefreshDesktop();
            return ok;
        }

        /// <summary>方案 F / I：完全不建子窗口，直接往桌面窗口的 DC 上画（老式「画桌面」）</summary>
        static bool RunDirectDC(int sw, int sh, IntPtr target, string name)
        {
            Log.Note("");
            Log.Note("===== [直画 " + name + "] 不建子窗口 · 往 " + name + " 的 DC 上 StretchDIBits =====");
            if (target == IntPtr.Zero) { Log.Note("  没有 " + name + "，跳过"); return false; }

            FillChecker();
            bool drew = false;
            for (int i = 0; i < 3; i++)
            {
                stage = "直画 " + name + " DC " + (i + 1);
                IntPtr dc = GetDC(target);
                if (dc == IntPtr.Zero)
                {
                    Log.Note("  GetDC(" + name + ") 失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                try { PaintTo(dc, sw, sh); drew = true; }
                finally { ReleaseDC(target, dc); }
                Pump(180);
            }
            Log.Note("  已往 " + name + " 的 DC 画了 3 帧：" + (drew ? "是" : "否"));

            Pump(250);
            double pct = SamplePercent("直画后");
            Log.Note("  采样：屏幕洋红 " + pct.ToString("0.0") + "%");
            bool hit = pct > 5.0;

            if (hit)
            {
                Log.Note("  ★ 请看桌面：再留 2 秒棋盘格");
                IntPtr dc2 = GetDC(target);
                if (dc2 != IntPtr.Zero) { try { FillChecker(); PaintTo(dc2, sw, sh); } finally { ReleaseDC(target, dc2); } }
                Pump(2000);
            }
            RefreshDesktop();
            Log.Note(hit ? "  ✔ 命中：直接画 " + name + " 的 DC 能留在屏幕上" : "  ✘ 不可见（画上去就被桌面重绘吃掉）");
            return hit;
        }

        /// <summary>方案 G：DWM 缩略图 —— 把本进程的顶层窗口内容投到 Progman 上</summary>
        static bool RunThumbnail(int sw, int sh)
        {
            Log.Note("");
            Log.Note("===== [G] DwmRegisterThumbnail：把本进程顶层窗口投影到 Progman =====");
            if (progman == IntPtr.Zero) { Log.Note("  没有 Progman，跳过"); return false; }

            // 源窗口放在屏幕右侧外面：不占屏、不干扰判定，但 DWM 仍有它的内容
            stage = "[G] 建源窗口";
            IntPtr src = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, ClsProbe, "WpThumb",
                                        WS_POPUP | WS_VISIBLE, sw, 0, sw, sh,
                                        IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (src == IntPtr.Zero)
            {
                Log.Note("  源窗口建不出来 err=" + Marshal.GetLastWin32Error());
                return false;
            }
            ourWindow = src;
            FillChecker();
            PaintWindow(src);
            Pump(300);
            Log.Note("  源窗口=0x" + src.ToString("x") + "（位于 x=" + sw + "，屏幕外）");

            stage = "[G] DwmRegisterThumbnail";
            IntPtr thumb = IntPtr.Zero;
            int hr = DwmRegisterThumbnail(progman, src, out thumb);
            Log.Note("  DwmRegisterThumbnail(Progman ← 源窗口) hr=0x" + hr.ToString("x"));
            if (hr != 0 || thumb == IntPtr.Zero)
            {
                Log.Note("  ✘ 注册失败（Progman 这个窗口不接受缩略图），这组不可行");
                Cleanup();
                return false;
            }

            DWM_THUMBNAIL_PROPERTIES p = new DWM_THUMBNAIL_PROPERTIES();
            p.dwFlags = 0x1 | 0x4 | 0x8;     // RECTDESTINATION | OPACITY | VISIBLE
            p.rcDestination.Left = 0; p.rcDestination.Top = 0;
            p.rcDestination.Right = sw; p.rcDestination.Bottom = sh;
            p.opacity = 255;
            p.fVisible = 1;
            p.fSourceClientAreaOnly = 0;
            hr = DwmUpdateThumbnailProperties(thumb, ref p);
            Log.Note("  DwmUpdateThumbnailProperties hr=0x" + hr.ToString("x"));

            Pump(800);
            double pct = SamplePercent("缩略图后");
            Log.Note("  采样：屏幕洋红 " + pct.ToString("0.0") + "%");
            bool hit = pct > 5.0;

            if (hit)
            {
                Log.Note("  ★ 请看桌面：再留 2 秒棋盘格");
                Pump(2000);
            }

            DwmUnregisterThumbnail(thumb);
            Cleanup();
            RefreshDesktop();
            Log.Note(hit ? "  ✔ 命中：缩略图能投到桌面层" : "  ✘ 不可见");
            return hit;
        }

        // ---------------- 绘制 ----------------

        /// <summary>建 32bpp 顶朝下 DIB（唯一一块，尺寸随内容变）</summary>
        /// <summary>读帧缓冲里的像素，返回 0xRRGGBB（用来判断"画面本身"是不是已经错了）</summary>
        internal static uint BufferPixel(int x, int y)
        {
            if (frameBits == IntPtr.Zero || x < 0 || y < 0 || x >= frameW || y >= frameH) return 0;
            IntPtr p = new IntPtr(frameBits.ToInt64() + (long)y * frameW * 4 + x * 4);
            uint b = (uint)Marshal.ReadByte(p);
            uint g = (uint)Marshal.ReadByte(p, 1);
            uint r = (uint)Marshal.ReadByte(p, 2);
            return (r << 16) | (g << 8) | b;
        }

        /// <summary>读窗口表面上的像素，返回 0xRRGGBB。用来判断"窗口里实际是什么"</summary>
        internal static uint WindowPixel(IntPtr hwnd, int x, int y)
        {
            if (hwnd == IntPtr.Zero) return 0xFFFFFF;
            IntPtr dc = GetDC(hwnd);
            if (dc == IntPtr.Zero) return 0xFFFFFE;
            try
            {
                uint c = GetPixel(dc, x, y);
                if (c == 0xFFFFFFFF) return 0xFFFFFD;
                return ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
            }
            finally { ReleaseDC(hwnd, dc); }
        }

        internal static bool BufferInit(int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            if (frameBits != IntPtr.Zero && frameW == w && frameH == h) return true;

            BufferRelease();

            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero)
            {
                Log.Note("  GetDC(屏幕) 失败");
                return false;
            }
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;      // 负数 = 自上而下，与视频帧一致
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;  // BI_RGB

                IntPtr bits;
                frameBmp = CreateDIBSection(screenDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (frameBmp == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    Log.Note("  CreateDIBSection 失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                frameBits = bits;
                frameW = w;
                frameH = h;
                frameBmi = bmi;
                Log.Note("  帧缓冲就绪 " + w + "x" + h + "（" + (w * h * 4) + " 字节）");
                return true;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        internal static void BufferRelease()
        {
            try { if (frameBmp != IntPtr.Zero) DeleteObject(frameBmp); }
            catch { }
            frameBmp = IntPtr.Zero;
            frameBits = IntPtr.Zero;
            frameW = 0;
            frameH = 0;
        }

        /// <summary>
        /// 探测用的棋盘格：一半洋红（好判定）+ 一半深灰（不像「红屏报错」，人眼一看就知道是测试图）。
        /// 8×6 大格子铺满屏幕，命中时用户能直接用眼睛确认。
        /// </summary>
        internal static void FillChecker()
        {
            if (frameBits == IntPtr.Zero) return;
            const int cols = 8, rows = 6;
            int total = frameW * frameH * 4;
            byte[] px = new byte[total];
            int cw = Math.Max(1, frameW / cols);
            int ch = Math.Max(1, frameH / rows);
            for (int y = 0; y < frameH; y++)
            {
                int ry = y / ch;
                for (int x = 0; x < frameW; x++)
                {
                    int rx = x / cw;
                    bool mag = ((rx + ry) & 1) == 0;
                    int o = (y * frameW + x) * 4;
                    if (mag) { px[o] = 255; px[o + 1] = 0; px[o + 2] = 255; }
                    else     { px[o] = 32;  px[o + 1] = 28; px[o + 2] = 40; }
                    px[o + 3] = 255;
                }
            }
            Marshal.Copy(px, 0, frameBits, total);
        }

        /// <summary>整块填成同一个颜色（探测用洋红；B/G/R 顺序是 DIB 的内存顺序）</summary>
        internal static void FillBuffer(byte b, byte g, byte r)
        {
            if (frameBits == IntPtr.Zero) return;
            int total = frameW * frameH * 4;
            byte[] px = new byte[total];
            for (int i = 0; i < total; i += 4)
            {
                px[i] = b;
                px[i + 1] = g;
                px[i + 2] = r;
                px[i + 3] = 255;
            }
            Marshal.Copy(px, 0, frameBits, total);
        }

        /// <summary>把帧缓冲画到指定 DC。比例和屏幕一致就铺满；不一致就完整居中 + 模糊底衬。
        /// （以前一律「填充裁切」：竖屏视频会被放大三四倍只剩中间一条，看不到全图）</summary>
        internal static void PaintTo(IntPtr hdc, int dw, int dh)
        {
            if (hdc == IntPtr.Zero || frameBits == IntPtr.Zero || dw <= 0 || dh <= 0) return;
            lock (paintLock)
            {
                int sw = frameW;
                int sh = frameH;
                if (sw <= 0 || sh <= 0) return;

                double scrAr = dw / (double)dh;
                double srcAr = sw / (double)sh;

                if (Math.Abs(srcAr - scrAr) <= scrAr * 0.02)
                {
                    // 天生同比例：一次拉伸就是精确铺满，不裁也不变形
                    StretchDIBits(hdc, 0, 0, dw, dh, 0, 0, sw, sh, frameBits, ref frameBmi, 0, SRCCOPY);
                    return;
                }

                // 比例差得多：底衬（模糊 + 压暗）铺满，整帧完整居中
                if (!EnsureBlur())
                {
                    // 底衬建不出来（内存紧张）就退回老的填充满屏，至少画面是动的
                    int cx = 0, cy = 0, cw2 = sw, ch2 = sh;
                    double f = Math.Max(dw / (double)sw, dh / (double)sh);
                    cw2 = (int)Math.Round(dw / f);
                    ch2 = (int)Math.Round(dh / f);
                    if (cw2 > sw) cw2 = sw;
                    if (ch2 > sh) ch2 = sh;
                    cx = (sw - cw2) / 2;
                    cy = (sh - ch2) / 2;
                    StretchDIBits(hdc, 0, 0, dw, dh, cx, cy, cw2, ch2, frameBits, ref frameBmi, 0, SRCCOPY);
                    return;
                }

                // 1) 整帧缩进 1/10 的底衬表面
                StretchDIBits(blurDC, 0, 0, blurW, blurH, 0, 0, sw, sh, frameBits, ref frameBmi, 0, SRCCOPY);
                // 2) 在小图上压暗（两万来个像素，可以忽略）
                DarkenBlur();
                // 3) 底衬放大铺满整屏
                StretchDIBits(hdc, 0, 0, dw, dh, 0, 0, blurW, blurH, blurBits, ref blurBmi, 0, SRCCOPY);

                // 4) 主体：整帧等比装进屏幕并居中
                int pad = (int)Math.Round(Math.Min(dw, dh) * 0.014);
                if (pad < 6) pad = 6;
                double s = Math.Min((dw - pad * 2) / (double)sw, (dh - pad * 2) / (double)sh);
                int w = Math.Max(1, (int)Math.Round(sw * s));
                int h = Math.Max(1, (int)Math.Round(sh * s));
                int x = (dw - w) / 2;
                int y = (dh - h) / 2;
                StretchDIBits(hdc, x, y, w, h, 0, 0, sw, sh, frameBits, ref frameBmi, 0, SRCCOPY);
            }
        }

        /// <summary>建/复用那块 1/10 屏幕大小的模糊底衬表面</summary>
        static bool EnsureBlur()
        {
            int bw = Math.Max(24, scrW / 10);
            int bh = Math.Max(24, scrH / 10);
            if (blurBits != IntPtr.Zero && blurW == bw && blurH == bh) return true;

            ReleaseBlur();

            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = bw;
                bmi.bmiHeader.biHeight = -bh;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;

                IntPtr bits;
                IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    Log.Note("  [适配] 底衬 DIB 创建失败 err=" + Marshal.GetLastWin32Error());
                    return false;
                }
                IntPtr dc = CreateCompatibleDC(screen);
                if (dc == IntPtr.Zero)
                {
                    DeleteObject(dib);
                    return false;
                }

                blurBmp = dib;
                blurBits = bits;
                blurDC = dc;
                blurOldBmp = SelectObject(dc, dib);
                blurW = bw;
                blurH = bh;
                blurBmi = bmi;
                Log.Note("  [适配] 竖屏/异形比例画面：底衬就绪 " + bw + "x" + bh);
                return true;
            }
            catch (Exception ex)
            {
                Log.Note("  [适配] 底衬异常：" + ex.Message);
                return false;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>底衬压暗，避免模糊的亮色抢主体</summary>
        static void DarkenBlur()
        {
            if (blurBits == IntPtr.Zero || blurW <= 0 || blurH <= 0) return;
            int total = blurW * blurH * 4;
            byte[] px = new byte[total];
            Marshal.Copy(blurBits, px, 0, total);
            for (int i = 0; i < total; i += 4)
            {
                px[i] = (byte)(px[i] * 45 / 100);
                px[i + 1] = (byte)(px[i + 1] * 45 / 100);
                px[i + 2] = (byte)(px[i + 2] * 45 / 100);
            }
            Marshal.Copy(px, 0, blurBits, total);
        }

        static void ReleaseBlur()
        {
            try { if (blurDC != IntPtr.Zero && blurOldBmp != IntPtr.Zero) SelectObject(blurDC, blurOldBmp); }
            catch { }
            try { if (blurDC != IntPtr.Zero) DeleteDC(blurDC); } catch { }
            try { if (blurBmp != IntPtr.Zero) DeleteObject(blurBmp); } catch { }
            blurDC = IntPtr.Zero;
            blurBmp = IntPtr.Zero;
            blurBits = IntPtr.Zero;
            blurOldBmp = IntPtr.Zero;
            blurW = 0;
            blurH = 0;
        }

        /// <summary>直接往窗口客户区画一帧（不走 WM_PAINT，视频解码线程也能安全调用）</summary>
        internal static void PaintWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            IntPtr dc = GetDC(hwnd);
            if (dc == IntPtr.Zero) return;
            try { PaintTo(dc, scrW, scrH); }
            finally { ReleaseDC(hwnd, dc); }
        }

        // ---------------- 视频模式要用到的公共件 ----------------

        /// <summary>
        /// 建一块挂进桌面层的分层窗口：先在本进程建弹窗（带 LAYERED），再 SetParent。
        /// 建窗时直接把跨进程窗口当父窗口会被系统拒绝（返回 NULL / 样式改不动），这条早就验过。
        ///
        /// mountMode 决定父窗口与 Z 序：
        ///   0 = SHELLDLL_DefView 最底层 —— 视频落在壁纸位置，桌面图标仍画在它上面（默认）
        ///   1 = Progman 最上层 —— 11-11 11:21 实测能显示，但会盖住图标
        ///   2 = Progman 内、DefView 之下（微软 24H2 文档口径；本机实测不显示，留作对照）
        ///   3 = WorkerW 最上层
        ///
        /// 建完**先不显示**：由调用方先 UpdateLayeredWindow 上第一帧，再 ShowLayerWindow。
        /// 顺序反了的话合成器可能记下空表面，后面怎么画都不出来。
        /// </summary>
        internal static IntPtr CreateLayerChild(string cls, string title, int w, int h, out string err)
        {
            err = "";
            if (!EnsureClass()) { err = "窗口类注册失败"; return IntPtr.Zero; }

            // 找不到 DefView 时退回「Progman 最上层」，否则挂 Progman 最底层是看不见的
            if (mountMode == 0 && defView == IntPtr.Zero) mountMode = 1;

            // ---- mountMode 5 / 6 / 7：顶层窗口 + 自己选 Z 序插法 ----
            //
            // 24H2 的 Progman 是 WS_EX_NOREDIRECTIONBITMAP：它自己一个像素都不画。
            // 挂在它下面的子窗口（0/1/2/3 档）因此全都没有可合成表面 ——
            // 12:08 实测：148/150/150 帧跑满 30fps、每次 ULW 都提交成功，屏幕上一个像素都没有。
            // 反过来利用这个特性：做成**顶层**分层窗口，画面就能被合成（12:12 实测 88% 屏幕像素变化）。
            //   5 = 插到 Progman 之后：实机落在 Progman **之上**，把桌面图标整个盖住。
            //   6 = HWND_BOTTOM：彻底沉到桌面之下，完全不合成（屏幕恢复原壁纸）。
            //   7 = 未文档化的 Z 序 band：SetWindowBand(hwnd, NULL, ZBID_DESKTOP)
            //       —— 直接把窗口放进「桌面」这一层，理论上正好在图标之下、壁纸之上。
            if (mountMode >= 5 && mountMode <= 7)
            {
                IntPtr h5 = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                                           cls, title, WS_POPUP, 0, 0, w, h,
                                           IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (h5 == IntPtr.Zero)
                {
                    err = "建窗失败 err=" + Marshal.GetLastWin32Error();
                    return IntPtr.Zero;
                }
                ourWindow = h5;
                Log.Note("  [挂载] 顶层窗口模式（mount=" + mountMode + "）扩展=0x"
                         + ((uint)GetWindowLong(h5, GWL_EXSTYLE)).ToString("x8"));
                return h5;
            }

            IntPtr target = progman;
            string who = "Progman";
            if (mountMode == 0 && defView != IntPtr.Zero) { target = defView; who = "SHELLDLL_DefView"; }
            else if (mountMode == 3 && worker != IntPtr.Zero) { target = worker; who = "WorkerW"; }
            if (target == IntPtr.Zero)
            {
                err = "桌面窗口没找到（Progman/DefView 都为空）";
                return IntPtr.Zero;
            }

            IntPtr hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT,
                                         cls, title, WS_POPUP, 0, 0, w, h,
                                         IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                err = "建窗失败 err=" + Marshal.GetLastWin32Error();
                return IntPtr.Zero;
            }
            ourWindow = hwnd;

            SetParent(hwnd, target);

            // SetParent 不会自动把 WS_POPUP 换成 WS_CHILD（文档明写），必须先补样式，
            // GetParent 才会返回真正的父窗口而不是 owner。这里先不带 WS_VISIBLE。
            SetWindowLong(hwnd, GWL_STYLE, (int)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN));

            bool mounted = (GetParent(hwnd) == target) || IsChildOf(target, hwnd);
            if (!mounted && target != progman)
            {
                // 退一步挂 Progman，至少别留一个自由漂着的弹窗
                Log.Note("  !! 挂 " + who + " 失败，退回 Progman");
                SetParent(hwnd, progman);
                SetWindowLong(hwnd, GWL_STYLE, (int)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN));
                who = "Progman";
                target = progman;
                mountMode = 1;
                mounted = (GetParent(hwnd) == progman) || IsChildOf(progman, hwnd);
            }
            if (!mounted)
            {
                err = "SetParent 没挂上 err=" + Marshal.GetLastWin32Error();
                DestroyWindow(hwnd);
                ourWindow = IntPtr.Zero;
                return IntPtr.Zero;
            }

            if (mountMode == 8)
            {
                bool lok = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                Log.Note("  [挂载] SetLayeredWindowAttributes(alpha=255) "
                         + (lok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error())
                         + "；之后必须用 GDI 直画窗口，绝不能再调 UpdateLayeredWindow");
            }

            if ((GetWindowLong(hwnd, GWL_STYLE) & (int)WS_CHILD) == 0)
            {
                err = "WS_CHILD 没生效（窗口样式被系统拒绝）";
                DestroyWindow(hwnd);
                ourWindow = IntPtr.Zero;
                return IntPtr.Zero;
            }

            // 位置先摆好，但仍然不显示
            SetWindowPos(hwnd, ZTarget(), 0, 0, w, h, SWP_NOACTIVATE | SWP_FRAMECHANGED);

            Log.Note("  [挂载] 父窗口=" + who + "(0x" + target.ToString("x") + ") Z 序=" + ZDesc()
                     + " 样式=0x" + ((uint)GetWindowLong(hwnd, GWL_STYLE)).ToString("x8")
                     + " 扩展=0x" + ((uint)GetWindowLong(hwnd, GWL_EXSTYLE)).ToString("x8"));
            return hwnd;
        }

        /// <summary>窗口的 Z 序插入点（取决于 mountMode）</summary>
        static IntPtr ZTarget()
        {
            switch (mountMode)
            {
                case 1: return IntPtr.Zero;              // HWND_TOP
                case 2: return defView;                  // 插在 DefView 之后 = 它的下面
                case 3: return IntPtr.Zero;              // WorkerW 最上层
                case 4: return IntPtr.Zero;              // 先置顶（唯一被实证能上屏的位置），随后再让 DefView 排到前面
                case 5: return progman;                  // 顶层窗口，插到 Progman 之后
                case 6: return new IntPtr(1);            // 顶层窗口，HWND_BOTTOM
                case 7: return progman;                  // 顶层窗口 + ZBID_DESKTOP band
                case 8: return defView;                  // DefView 之下、WorkerW 之上（微软官方口径）
                default: return new IntPtr(1);           // HWND_BOTTOM = DefView 最底层
            }
        }

        static string ZDesc()
        {
            switch (mountMode)
            {
                case 1: return "Progman 最上层（图标之上）";
                case 2: return "Progman 内 · DefView 之下";
                case 3: return "WorkerW 最上层";
                case 4: return "先置顶再让图标层上来（壁纸位 + 图标可见）";
                case 5: return "顶层窗口 · Z 序插到 Progman 之后（壁纸位 + 图标可见）";
                case 6: return "顶层窗口 · HWND_BOTTOM（试沉到桌面之下）";
                case 7: return "顶层窗口 · ZBID_DESKTOP band（壁纸位 + 图标可见）";
                case 8: return "Progman 分层子窗口 · DefView 之下（微软 24H2 官方口径）";
                default: return "DefView 最底层（图标之下）";
            }
        }

        /// <summary>
        /// 上完第一帧再显示——顺序反了 DWM 可能记住空表面。
        /// mountMode=4 时额外做一步：把 SHELLDLL_DefView（图标层）提到本窗口之上。
        /// 这样窗口走的是「唯一被实证能上屏」的置顶位置，最终 Z 序却仍是壁纸位。
        /// </summary>
        internal static void ShowLayerWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;

            // mountMode 5/6/7：顶层窗口，自己决定插在哪。
            // hWndInsertAfter 的语义是「排在这个窗口之后（=Z 序更低）」，
            // 5 档传 progman，6 档传 HWND_BOTTOM，7 档走 band。
            if (mountMode >= 5 && mountMode <= 7)
            {
                ShowWindow(hwnd, 4 /* SW_SHOWNA */);
                IntPtr insertAfter = (mountMode == 6) ? new IntPtr(1) /* HWND_BOTTOM */ : progman;
                bool ok = SetWindowPos(hwnd, insertAfter, 0, 0, scrW, scrH,
                                       SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

                if (mountMode == 7)
                {
                    // ZBID_DESKTOP = 1：桌面自己就在这个 band 里。
                    // 未文档化导出（user32.dll 里confirmed 存在），所以调用后必须回读 GetWindowBand 确认。
                    bool bandOk = SetWindowBand(hwnd, IntPtr.Zero, ZBID_DESKTOP);
                    uint band = 0xFFFFFFFF;
                    bool readOk = GetWindowBand(hwnd, out band);
                    Log.Note("  [层级] SetWindowBand(ZBID_DESKTOP) = " + (bandOk ? "成功" : "失败 err=" + Marshal.GetLastWin32Error())
                             + "；回读 GetWindowBand=" + (readOk ? band.ToString() : "失败")
                             + "（期望 1；调用后仍要重新插一次 Z 序，band 变化会把窗口顶到最前）");
                    // band 切换后窗口会被顶到该 band 的最前，所以再插一次到 Progman 之后
                    SetWindowPos(hwnd, progman, 0, 0, scrW, scrH,
                                 SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
                }

                IntPtr above = GetWindow(hwnd, 3 /* GW_HWNDPREV：Z 序更高 */);
                IntPtr below = GetWindow(hwnd, 2 /* GW_HWNDNEXT：Z 序更低 */);
                Log.Note("  [层级] " + (mountMode == 6 ? "HWND_BOTTOM" : (mountMode == 7 ? "band 后再插 Progman" : "插到 Progman 之后"))
                         + " " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error())
                         + "；上方(Z序更高)=" + DescribeWindow(above)
                         + " 下方(Z序更低)=" + DescribeWindow(below)
                         + "（Progman=0x" + progman.ToString("x") + "）");
                return;
            }

            ShowWindow(hwnd, 4 /* SW_SHOWNA：显示但不激活 */);
            SetWindowPos(hwnd, ZTarget(), 0, 0, scrW, scrH,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);

            if (mountMode == 4 && defView != IntPtr.Zero)
            {
                // 先在置顶位置停 1.2 秒：这段时间画面一定能看到（置顶是唯一被实证能上屏的位置），
                // 用户能确认「视频链路是通的」；然后再让图标层上来，看视频会不会被盖掉。
                Log.Note("  [层级] 先保持置顶 1.2 秒，随后把图标层提到视频之上");
                Pump(1200);

                bool ok = SetWindowPos(defView, hwnd, 0, 0, 0, 0,
                                       SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                Log.Note("  [层级] 把 DefView 提到视频之上 " + (ok ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
                DumpProgmanOrder("[层级] 调整后");
            }
        }

        /// <summary>播放器主循环：跑在自己的线程上，靠 WM_APP+1/2/3 控制</summary>
        internal static void MessageLoop()
        {
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        internal static void Cleanup()
        {
            if (ourWindow != IntPtr.Zero)
            {
                DestroyWindow(ourWindow);
                ourWindow = IntPtr.Zero;
            }
        }

        /// <summary>
        /// 启动失败时直接弹窗说明原因。以前这些错误只写进日志，
        /// 用户在桌面这边看到的就是「双击了 build.bat，什么都没发生」。
        /// </summary>
        internal static void Fatal(string msg)
        {
            Log.Note("!!! " + msg);
            try { MessageBoxW(IntPtr.Zero, msg, "动态壁纸启动失败", 0x00000010 /* MB_ICONERROR */); }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        /// <summary>
        /// 只把当前队列里的消息分发掉，不等待。
        /// 视频播放在主线程上跑（单线程，避免 COM 跨套间），帧循环里靠它来
        /// 及时处理暂停/继续/停止指令。
        /// </summary>
        internal static void PumpOnce()
        {
            MSG msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        internal static void Pump(int ms)        {
            long end = (long)Environment.TickCount + ms;
            MSG msg;
            while ((long)Environment.TickCount < end)
            {
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                Thread.Sleep(10);
            }
        }

        // ---------------- 收尾与保护 ----------------

        /// <summary>把上一轮/上一次进程残留的播放器窗口关掉（它们会压住图标层）</summary>
        internal static void CloseLeftovers()
        {
            if (KeepOld)
            {
                Log.Note("[2] 交接模式：不清残留窗口（旧播放器还在撑画面，由管理器稍后收掉）");
                return;
            }

            int closed = 0;
            closed += CloseChildren(progman, "Progman");
            // 默认挂载方式会把窗口塞进 DefView，残留窗口也得在这一层找
            if (defView != IntPtr.Zero) closed += CloseChildren(defView, "DefView");
            if (worker != IntPtr.Zero) closed += CloseChildren(worker, "WorkerW");

            // 顶层也可能残留（还没插入桌面就被杀的情况）
            for (int i = 0; i < 8; i++)
            {
                IntPtr top = FindWindow(ClsProbe, null);
                if (top == IntPtr.Zero) top = FindWindow(ClsVideo, null);
                if (top == IntPtr.Zero) break;
                PostMessage(top, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed++;
                Log.Note("[2] 关掉残留顶层窗口 0x" + top.ToString("x"));
                Thread.Sleep(120);
            }

            Log.Note(closed == 0 ? "[2] 没有残留播放器窗口" : "[2] 清掉残留窗口 " + closed + " 个");
            if (closed > 0) Thread.Sleep(300);
        }

        static int CloseChildren(IntPtr parent, string parentName)
        {
            if (parent == IntPtr.Zero) return 0;
            int closed = 0;
            IntPtr child = IntPtr.Zero;
            for (int i = 0; i < 32; i++)
            {
                child = FindWindowEx(parent, child, null, null);
                if (child == IntPtr.Zero) break;
                string cls = ClassOf(child);
                if (cls == ClsProbe || cls == ClsVideo)
                {
                    PostMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    closed++;
                    Log.Note("[2] 关掉 " + parentName + " 下的残留窗口 0x" + child.ToString("x"));
                }
            }
            return closed;
        }

        /// <summary>让 Explorer 重画桌面：命令 + 主动失效，防止图标被残留窗口盖住后不恢复</summary>
        internal static void RefreshDesktop()
        {
            try
            {
                if (progman == IntPtr.Zero) progman = FindWindow("Progman", null);
                if (progman != IntPtr.Zero)
                {
                    PostMessage(progman, WM_COMMAND, new IntPtr(CMD_REFRESH_DESKTOP), IntPtr.Zero);
                    Log.Note("[5] 已向 Progman 发送刷新命令");
                }
            }
            catch { }

            try
            {
                if (defView == IntPtr.Zero && progman != IntPtr.Zero)
                    defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    RedrawWindow(defView, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN);
                    Log.Note("[5] 已请求重画 DefView（图标层）");
                }
            }
            catch { }
        }

        /// <summary>看门狗：主线程若卡在某个系统调用里，把当前阶段写进日志再退出，绝不留窗口在桌面上</summary>
        static void StartWatchdog(int seconds)
        {
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(seconds * 1000);
                Log.Note("!!! 看门狗触发：" + seconds + " 秒仍未结束，卡在阶段「" + stage + "」");
                try { if (ourWindow != IntPtr.Zero) PostMessage(ourWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
                catch { }
                Thread.Sleep(1200);
                Environment.Exit(2);
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- 桌面窗口树 ----------------

        /// <summary>打印 Progman 子窗口的 Z 序（上→下），用来确认我们插在了 DefView 与 WorkerW 之间</summary>
        internal static void DumpProgmanOrder(string tag)
        {
            if (progman == IntPtr.Zero) return;
            StringBuilder sb = new StringBuilder();
            IntPtr c = IntPtr.Zero;
            for (int i = 0; i < 12; i++)
            {
                c = FindWindowEx(progman, c, null, null);
                if (c == IntPtr.Zero) break;
                sb.Append("[").Append(i).Append("]").Append(ClassOf(c))
                  .Append(" 0x").Append(c.ToString("x")).Append("  ");
            }
            Log.Note("  " + tag + " Progman 子窗口 Z 序（上→下）：" + sb.ToString());

            // 默认挂载方式把窗口塞进 DefView，这一层的 Z 序也要看
            if (defView != IntPtr.Zero)
            {
                StringBuilder sb2 = new StringBuilder();
                IntPtr d = IntPtr.Zero;
                for (int i = 0; i < 12; i++)
                {
                    d = FindWindowEx(defView, d, null, null);
                    if (d == IntPtr.Zero) break;
                    sb2.Append("[").Append(i).Append("]").Append(ClassOf(d))
                       .Append(" 0x").Append(d.ToString("x")).Append("  ");
                }
                Log.Note("  " + tag + " DefView 子窗口 Z 序（上→下）：" + sb2.ToString());
            }
        }

        /// <summary>枚举父窗口的子窗口，确认 hwnd 真的挂上去了（比 GetParent 可靠：
        /// WS_POPUP 位没清时 GetParent 返回的是 owner，不是父窗口）</summary>
        internal static bool IsChildOf(IntPtr parent, IntPtr hwnd)
        {
            if (parent == IntPtr.Zero || hwnd == IntPtr.Zero) return false;
            IntPtr c = IntPtr.Zero;
            for (int i = 0; i < 64; i++)
            {
                c = FindWindowEx(parent, c, null, null);
                if (c == IntPtr.Zero) break;
                if (c == hwnd) return true;
            }
            return false;
        }

        internal static string ClassOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        internal static string DescribeWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(无)";
            return ClassOf(h) + " 0x" + h.ToString("x");
        }

        // ---------------- 屏幕采样 ----------------

        /// <summary>
        /// 屏幕洋红占比。
        /// 早先版本用 60×60=3600 次 GetPixel，每次都要过桌面合成（实测约 7ms），
        /// 合计 25 秒，正好撞上管理器超时，所以看起来像「卡死」。
        /// 这里一次 StretchBlt 把整屏缩到 40×24 再读内存，单次约 1ms。
        /// </summary>
        static double SamplePercent(string tag)
        {
            const int gw = 40;
            const int gh = 24;

            long t0 = Environment.TickCount;
            IntPtr screenDC = GetDC(IntPtr.Zero);
            if (screenDC == IntPtr.Zero) { Log.Note("  [" + tag + "] GetDC(屏幕) 失败"); return 0; }

            IntPtr mem = IntPtr.Zero;
            IntPtr bmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            double result = 0;
            try
            {
                BITMAPINFO bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = gw;
                bmi.bmiHeader.biHeight = -gh;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = 0;

                IntPtr bits;
                bmp = CreateDIBSection(screenDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) { Log.Note("  [" + tag + "] 采样 DIB 创建失败"); return 0; }

                mem = CreateCompatibleDC(screenDC);
                old = SelectObject(mem, bmp);

                bool ok = StretchBlt(mem, 0, 0, gw, gh, screenDC, 0, 0, scrW, scrH, SRCCOPY | CAPTUREBLT);
                if (!ok)
                {
                    // StretchBlt 被拦（少数远程/受保护会话）时退化为 9 点 GetPixel，仍然很便宜
                    Log.Note("  [" + tag + "] StretchBlt 失败 err=" + Marshal.GetLastWin32Error() + "，退化为 9 点采样");
                    int hit9 = 0;
                    for (int y = 1; y <= 3; y++)
                        for (int x = 1; x <= 3; x++)
                        {
                            uint col = GetPixel(screenDC, scrW * x / 4, scrH * y / 4);
                            if (IsMagenta((byte)(col & 0xFF), (byte)((col >> 8) & 0xFF), (byte)((col >> 16) & 0xFF)))
                                hit9++;
                        }
                    result = hit9 * 100.0 / 9;
                }
                else
                {
                    byte[] px = new byte[gw * gh * 4];
                    Marshal.Copy(bits, px, 0, px.Length);
                    int hit = 0;
                    for (int i = 0; i < px.Length; i += 4)
                        if (IsMagenta(px[i], px[i + 1], px[i + 2])) hit++;
                    result = hit * 100.0 / (gw * gh);

                    int mid = ((gh / 2) * gw + gw / 2) * 4;
                    Log.Note("  [" + tag + "] 中心像素 B=" + px[mid] + " G=" + px[mid + 1]
                             + " R=" + px[mid + 2] + " A=" + px[mid + 3]
                             + "　耗时 " + (Environment.TickCount - t0) + "ms");
                }
            }
            catch (Exception ex)
            {
                Log.Note("  [" + tag + "] 采样异常：" + ex.Message);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                ReleaseDC(IntPtr.Zero, screenDC);
            }
            return result;
        }

        /// <summary>给视频模式用的自检入口：看屏幕上有多少洋红，判断桌面层窗口是否真的显示</summary>
        internal static double SampleMagenta()
        {
            return SamplePercent("自检");
        }

        /// <summary>洋红判定带容差：不同色彩管理下未必精确等于 0xFF00FF</summary>
        static bool IsMagenta(byte b, byte g, byte r)
        {
            return b >= 200 && r >= 200 && g <= 80;
        }

        // ---------------- 消息 ----------------

        static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_PAINT)
            {
                PAINTSTRUCT ps;
                IntPtr hdc = BeginPaint(hWnd, out ps);
                if (hdc != IntPtr.Zero)
                {
                    if (!ulwActive) PaintTo(hdc, scrW, scrH);
                    EndPaint(hWnd, ref ps);
                }
                return IntPtr.Zero;
            }

            if (msg == WM_CLOSE)
            {
                Log.Note("收到 WM_CLOSE，销毁自己的窗口");
                stopRequested = true;
                if (ourWindow != IntPtr.Zero)
                {
                    DestroyWindow(ourWindow);
                    ourWindow = IntPtr.Zero;
                }
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            if (msg == WM_APP + 1) { paused = true; Log.Note("暂停播放"); return IntPtr.Zero; }
            if (msg == WM_APP + 2) { paused = false; Log.Note("继续播放"); return IntPtr.Zero; }
            if (msg == WM_APP + 3)
            {
                Log.Note("收到停止指令");
                stopRequested = true;
                if (ourWindow != IntPtr.Zero)
                {
                    DestroyWindow(ourWindow);
                    ourWindow = IntPtr.Zero;
                }
                PostQuitMessage(0);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
