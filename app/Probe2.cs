using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperManager
{
    /// <summary>
    /// 桌面层探测（v4）：每个变体真截图存 PNG，靠看图判定。
    ///
    /// 11:15 那轮（v3）终于拿到了可信数据：
    ///   · 两处会误判的地方已修：① 截图里全屏是「欢迎回来」遮罩（探测前先关掉）；
    ///     ② 测色把 R/B 通道读反了（COLORREF 是 0x00BBGGRR）。
    ///   · 结论：GDI 画的内容挂在 Progman 下（含分层 + SWA），屏幕上 0 像素；
    ///     唯一真的出现在屏幕上的是「顶层分层窗口 + UpdateLayeredWindow」（100% 紫）。
    ///   · 另一个事实：跨进程子窗口建窗时就带不上 WS_EX_LAYERED（事后补也加不上）。
    /// 所以这一轮只回答一个问题：把 ULW 这套呈现搬到桌面层还灵不灵。
    ///   灵 → 视频直接每帧 ULW，收工；
    ///   不灵 → 换 D3D11 交换链 + DirectComposition（微软给 raised desktop 的口径）。
    /// </summary>
    public static class Probe2
    {
        // ---------------- Win32 ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fInc;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] res;
        }

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
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

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
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("kernel32.dll")]
        static extern void SetLastError(uint code);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr h);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr h, IntPtr p);

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr h);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr h, int i);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetWindowLong(IntPtr h, int i, int v);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint f);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst,
            ref SIZE size, IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string c, string n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr h, StringBuilder sb, int m);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int i);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string n);

        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr h, IntPtr r, bool e);

        [DllImport("user32.dll")]
        static extern bool UpdateWindow(IntPtr h);

        [DllImport("user32.dll")]
        static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr region, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr h);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr h, IntPtr dc);

        // 注意：FillRect 在 user32.dll（不是 gdi32），写错会在第一次绘制时抛 EntryPointNotFoundException
        [DllImport("user32.dll")]
        static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr o);

        [DllImport("gdi32.dll")]
        static extern uint GetPixel(IntPtr dc, int x, int y);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage,
            out IntPtr bits, IntPtr sec, uint off);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr dc, IntPtr o);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

        [DllImport("user32.dll")]
        static extern bool PeekMessage(out MSG m, IntPtr h, uint mn, uint mx, uint rm);

        [DllImport("user32.dll")]
        static extern bool TranslateMessage(ref MSG m);

        [DllImport("user32.dll")]
        static extern IntPtr DispatchMessage(ref MSG m);

        // ---------------- 常量 ----------------
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;
        const uint WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20;
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        const int SW_SHOWNA = 8;

        // 抓屏 ROP：SRCCOPY | CAPTUREBLT。CAPTUREBLT 是抓到分层窗口内容的关键
        const int ROP_CAPTURE = unchecked((int)0x40CC0020);

        // 变体呈现方式
        const int M_CHILD_SWA = 1;    // 原生 WS_CHILD（跨进程父）+ LAYERED + SWA(255) + GDI 填充
        const int M_CHILD_PLAIN = 2;  // 原生 WS_CHILD（跨进程父），不带 LAYERED
        const int M_CHILD_ULW = 3;    // 原生 WS_CHILD + LAYERED + UpdateLayeredWindow（32bpp 逐像素）
        const int M_OLD = 4;          // 老姿势：顶层弹窗 → SetParent → 改 WS_CHILD → SWA → GDI
        const int M_TOPZ = 5;         // 同 M_CHILD_SWA，但 Z 序插到最上层（盖住图标，做可见性判据）
        const int M_CTRL_PLAIN = 6;   // 对照：普通顶层窗口
        const int M_CTRL_ULW = 7;     // 对照：分层顶层窗口（验证抓屏抓得到分层内容）
        const int M_OLD_ULW = 8;      // 老姿势 + UpdateLayeredWindow（11:15 那轮唯一活下来的呈现方式）

        static IntPtr progman, defView, worker;
        static int scrW, scrH;
        static string shotDir;
        static int curColor = 0x0000FF;
        static WndProcDelegate proc;
        static IntPtr inst;

        // 汇总：[key] = 结果描述
        static System.Collections.Generic.List<string> summary = new System.Collections.Generic.List<string>();

        public static void Run()
        {
            try { SetProcessDPIAware(); } catch { }

            Log.Note("");
            Log.Note("############ 桌面层探测 v4：ULW 搬进桌面层 ############");

            scrW = GetSystemMetrics(0);
            scrH = GetSystemMetrics(1);
            shotDir = Path.Combine(Path.GetTempPath(), "wp3");
            try
            {
                if (Directory.Exists(shotDir)) Directory.Delete(shotDir, true);
                Directory.CreateDirectory(shotDir);
            }
            catch { }

            progman = FindWindow("Progman", null);
            defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            worker = FindFullWorker(progman);

            Log.Note("[P3] 屏幕 " + scrW + "x" + scrH);
            Log.Note("[P3] Progman=0x" + progman.ToString("x")
                     + " DefView=0x" + defView.ToString("x")
                     + " WorkerW=0x" + worker.ToString("x"));
            DumpRect("[P3] Progman", progman);
            DumpRect("[P3] DefView", defView);
            DumpRect("[P3] WorkerW", worker);
            Log.Note("[P3] Progman 扩展样式=0x" + ((uint)GetWindowLong(progman, GWL_EXSTYLE)).ToString("x8")
                     + "（0x200000 = WS_EX_NOREDIRECTIONBITMAP，说明是 raised desktop）");
            Log.Note("[P3] 截图目录 " + shotDir);

            inst = GetModuleHandle(null);
            proc = new WndProcDelegate(WndProc);
            WNDCLASSEXW wc = new WNDCLASSEXW();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
            wc.style = 0x0002 | 0x0020;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
            wc.hInstance = inst;
            wc.hbrBackground = IntPtr.Zero;
            wc.lpszClassName = "WpProbe3";
            ushort reg = RegisterClassEx(ref wc);
            if (reg == 0)
            {
                Log.Note("[P3] RegisterClassEx 失败 err=" + Marshal.GetLastWin32Error());
                return;
            }

            // ---------- 对照：先证明「抓屏」本身是好的 ----------
            Safe("V0a", "对照·顶层普通窗口（GDI 填充，应铺满全屏）", IntPtr.Zero, M_CTRL_PLAIN, 255, 140, 0, false);
            Safe("V0b", "对照·顶层分层窗口 + ULW（已知能显示）", IntPtr.Zero, M_CTRL_ULW, 150, 0, 200, false);

            // ---------- 正式变体：把「唯一证明能显示」的 ULW 搬到桌面上 ----------
            // 11:15 那轮的结果：GDI 内容挂在 Progman 下，屏幕上一像素都没有；
            // 唯一真的出现在屏幕上的，是顶层分层窗口 + UpdateLayeredWindow。
            // 所以这轮只问一个问题：ULW 这套呈现搬进桌面层还灵不灵？
            Safe("V1", "弹窗+分层 → SetParent(Progman) → ULW · 插在 DefView 之下", progman, M_OLD_ULW, 255, 0, 0, true);
            Safe("V2", "弹窗+分层 → SetParent(WorkerW) → ULW", worker, M_OLD_ULW, 0, 200, 0, true);
            Safe("V3", "弹窗+分层 → SetParent(Progman) → ULW · Z 序置顶（应盖住图标）", progman, M_OLD_ULW, 255, 220, 0, false);
            Safe("V4", "弹窗+分层 → SetParent(Progman) → GDI（11:15 已证不可见，留作对照）", progman, M_OLD, 255, 0, 255, true);

            Log.Note("");
            Log.Note("================ 汇总 ================");
            foreach (string s in summary) Log.Note("  " + s);
            Log.Note("[P3] 截图在 " + shotDir);
            Log.Note("############ 探测 v4 结束 ############");
        }

        static void SelfTest()
        {
            Log.Note("");
            Log.Note("===== 对照：顶层窗口（确认「画得出来、截得到」）=====");
            curColor = ColorRef(255, 160, 0);
            IntPtr h = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "WpProbe3", "self",
                                      WS_POPUP, 0, 0, scrW, scrH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (h == IntPtr.Zero)
            {
                Log.Note("  自检窗口建不出来 err=" + Marshal.GetLastWin32Error());
                return;
            }
            Paint(h);
            ShowWindow(h, SW_SHOWNA);
            SetWindowPos(h, IntPtr.Zero, 0, 0, scrW, scrH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Paint(h);
            Pump(700);
            Log.Note("  采样橙色占屏 " + SampleColor() + "%（应接近 100）");
            Shot("V0-self.png");
            DestroyWindow(h);
            Pump(200);
        }

        static void Safe(string key, string label, IntPtr parent, int mode,
                         int r, int g, int b, bool insertBelowDefView)
        {
            int pct = -1;
            try
            {
                pct = Variant(key, label, parent, mode, r, g, b, insertBelowDefView);
            }
            catch (Exception ex)
            {
                Log.Note("  !! " + key + " 异常：" + ex.GetType().Name + "：" + ex.Message);
            }
            summary.Add(key + " = " + (pct < 0 ? "未测到" : pct + "%") + "　" + label);
        }

        /// <summary>跑一个变体，返回屏幕命中率（-1 = 没测到）</summary>
        static int Variant(string key, string label, IntPtr parent, int mode,
                           int r, int g, int b, bool belowDefView)
        {
            Log.Note("");
            Log.Note("===== " + key + " " + label + " =====");

            bool isControl = (mode == M_CTRL_PLAIN || mode == M_CTRL_ULW);
            if (!isControl && parent == IntPtr.Zero)
            {
                Log.Note("  父窗口为空，跳过");
                return -1;
            }

            curColor = ColorRef(r, g, b);
            bool layered = (mode == M_CHILD_SWA || mode == M_CHILD_ULW || mode == M_OLD
                            || mode == M_TOPZ || mode == M_CTRL_ULW || mode == M_OLD_ULW);
            bool ulw = (mode == M_CHILD_ULW || mode == M_CTRL_ULW || mode == M_OLD_ULW);

            IntPtr hwnd = IntPtr.Zero;
            uint ex = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (layered ? WS_EX_LAYERED : 0u);

            // ---- 建窗 ----
            if (isControl || mode == M_OLD || mode == M_OLD_ULW)
            {
                // 顶层弹窗（对照用；M_OLD / M_OLD_ULW 之后会挂到桌面上）
                hwnd = CreateWindowEx(ex, "WpProbe3", key, WS_POPUP,
                                      0, 0, scrW, scrH, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) { Log.Note("  弹窗建不出来 err=" + Marshal.GetLastWin32Error()); return -1; }
                Log.Note("  弹窗 0x" + hwnd.ToString("x") + " 扩展=0x"
                         + ((uint)GetWindowLong(hwnd, GWL_EXSTYLE)).ToString("x8")
                         + " 带LAYERED=" + layered);

                if (mode == M_OLD || mode == M_OLD_ULW)
                {
                    // ULW 模式不需要 SWA（两者互斥，混用容易把窗口留在「半初始化」状态）
                    if (layered && mode != M_OLD_ULW)
                    {
                        bool ok0 = SetLayeredWindowAttributes(hwnd, 0, 255, 2);
                        Log.Note("  挂载前 SWA " + (ok0 ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
                    }
                    SetLastError(0);
                    IntPtr prev = SetParent(hwnd, parent);
                    int errSp = Marshal.GetLastWin32Error();
                    SetLastError(0);
                    SetWindowLong(hwnd, GWL_STYLE, (int)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN));
                    int errSt = Marshal.GetLastWin32Error();
                    IntPtr nowP = GetParent(hwnd);
                    Log.Note("  SetParent 旧父=0x" + prev.ToString("x") + " err=" + errSp
                             + "　改样式 err=" + errSt
                             + "　现父=0x" + nowP.ToString("x") + "(" + ClassOf(nowP) + ")"
                             + "　样式=0x" + ((uint)GetWindowLong(hwnd, GWL_STYLE)).ToString("x8"));
                }
            }
            else
            {
                // 原生 WS_CHILD：创建时就把跨进程窗口当父窗口
                uint style = WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;   // 先不带 WS_VISIBLE
                SetLastError(0);
                hwnd = CreateWindowEx(ex, "WpProbe3", key, style,
                                      0, 0, scrW, scrH, parent, IntPtr.Zero, inst, IntPtr.Zero);
                int err = Marshal.GetLastWin32Error();
                Log.Note("  原生 WS_CHILD 建窗 hwnd=0x" + hwnd.ToString("x") + " err=" + err
                         + "（父=" + ClassOf(parent) + "，带 LAYERED=" + layered + "）");

                // 带 LAYERED 建不出来时，退一步验证一个关键问题：
                // 「跨进程子窗口到底允不允许带分层样式」——先建普通子窗，再补 LAYERED 看能不能加上
                if (hwnd == IntPtr.Zero && layered)
                {
                    SetLastError(0);
                    hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "WpProbe3", key, style,
                                          0, 0, scrW, scrH, parent, IntPtr.Zero, inst, IntPtr.Zero);
                    Log.Note("  退一步：不带 LAYERED 建 hwnd=0x" + hwnd.ToString("x")
                             + " err=" + Marshal.GetLastWin32Error());
                    if (hwnd != IntPtr.Zero)
                    {
                        SetLastError(0);
                        SetWindowLong(hwnd, GWL_EXSTYLE, (int)ex);
                        int errEx = Marshal.GetLastWin32Error();
                        uint nowEx = (uint)GetWindowLong(hwnd, GWL_EXSTYLE);
                        Log.Note("  事后补 LAYERED：err=" + errEx + " 扩展样式=0x" + nowEx.ToString("x8")
                                 + ((nowEx & WS_EX_LAYERED) != 0 ? "（加上了）" : "（!! 加不上，跨进程子窗不允许 LAYERED）"));
                    }
                }
            }

            if (hwnd == IntPtr.Zero)
            {
                Log.Note("  ✘ 这组建窗失败，跳过");
                return -1;
            }

            // ---- Z 序（此时仍不显示）----
            IntPtr after = IntPtr.Zero;
            // 只有父是 Progman 时，DefView 才是兄弟窗口；挂 WorkerW 时插到它最上层即可
            if (belowDefView && !isControl && parent == progman) after = defView;
            if (mode == M_TOPZ) after = IntPtr.Zero;
            bool pos = SetWindowPos(hwnd, after, 0, 0, scrW, scrH,
                                    SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSIZE | SWP_NOMOVE);
            Log.Note("  SetWindowPos(插到 " + (after == IntPtr.Zero ? "最上层" : ClassOf(after)) + " 之后) "
                     + (pos ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));

            // ---- 上内容 ----
            // 只有窗口真的带上了 LAYERED 位才能用 UpdateLayeredWindow，否则退回 GDI 填充
            bool useUlw = ulw && (((uint)GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_LAYERED) != 0);
            if (useUlw)
            {
                bool okU = UlwFill(hwnd);
                Log.Note("  UpdateLayeredWindow " + (okU ? "成功" : "失败 err=" + Marshal.GetLastWin32Error()));
            }
            else
            {
                Paint(hwnd);
            }
            Pump(100);

            // ---- 最后才翻可见 ----
            ShowWindow(hwnd, SW_SHOWNA);
            SetWindowPos(hwnd, after, 0, 0, scrW, scrH,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOMOVE);
            UpdateWindow(hwnd);
            // 11:15 那轮实测：ULW 在 ShowWindow 之前写进去的内容，首帧是不显示的，
            // 必须显示之后再 ULW 一次（V0b 就是第二次才 100%）。所以这里补一次。
            if (useUlw) UlwFill(hwnd); else Paint(hwnd);
            Log.Note("  已「先画内容 → 后翻 WS_VISIBLE」");

            // ---- 窗口自检：内容到底进没进这个窗口自己的表面 ----
            IntPtr wdc = GetDC(hwnd);
            uint selfPx = 0;
            if (wdc != IntPtr.Zero)
            {
                selfPx = GetPixel(wdc, 20, 20);
                ReleaseDC(hwnd, wdc);
            }
            int cloaked;
            int hr = DwmGetWindowAttribute(hwnd, 14, out cloaked, 4);
            RECT wr;
            GetWindowRect(hwnd, out wr);
            Log.Note("  窗口自查：自身DC像素=0x" + selfPx.ToString("x8")
                     + (selfPx == 0xFFFFFFFFu
                        ? "（CLR_INVALID：分层窗口读不到自己的像素，属正常）"
                        : "（期望 0x" + ((uint)curColor).ToString("x6") + "）")
                     + "　可见=" + IsWindowVisible(hwnd)
                     + "　父=0x" + GetParent(hwnd).ToString("x") + "(" + ClassOf(GetParent(hwnd)) + ")"
                     + "　样式=0x" + ((uint)GetWindowLong(hwnd, GWL_STYLE)).ToString("x8")
                     + "　扩展=0x" + ((uint)GetWindowLong(hwnd, GWL_EXSTYLE)).ToString("x8")
                     + "　cloaked=" + cloaked + " hr=0x" + hr.ToString("x"));
            Log.Note("  矩形 " + wr.L + "," + wr.T + " " + (wr.R - wr.L) + "x" + (wr.B - wr.T));

            Pump(700);
            int pct = SampleColor();
            Log.Note("  首帧屏幕命中 " + pct + "%");
            Shot(key + "-a.png");

            // 补画一次：区分「首次合成丢了内容」和「完全不进合成」
            if (useUlw) UlwFill(hwnd); else Paint(hwnd);
            Pump(300);
            int pct2 = SampleColor();
            Log.Note("  补画后命中 " + pct2 + "%");
            Shot(key + "-b.png");

            DestroyWindow(hwnd);
            RedrawWindow(progman, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0100 | 0x0001);
            Pump(250);

            if (pct2 >= 0) return pct2;
            return pct;
        }

        // ---------------- 绘制 / 抓屏 ----------------
        static int ColorRef(int r, int g, int b) { return r | (g << 8) | (b << 16); }

        static void Paint(IntPtr hwnd)
        {
            InvalidateRect(hwnd, IntPtr.Zero, false);
            UpdateWindow(hwnd);
            IntPtr dc = GetDC(hwnd);
            if (dc == IntPtr.Zero) return;
            try
            {
                RECT rc;
                GetClientRect(hwnd, out rc);
                IntPtr br = CreateSolidBrush(curColor);
                FillRect(dc, ref rc, br);
                DeleteObject(br);
            }
            finally { ReleaseDC(hwnd, dc); }
        }

        /// <summary>用 UpdateLayeredWindow 写一张 32bpp 纯色（alpha=255）进去</summary>
        static bool UlwFill(IntPtr hwnd)
        {
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bi.biWidth = scrW;
            bi.biHeight = -scrH;          // 负数 = 自上而下
            bi.biPlanes = 1;
            bi.biBitCount = 32;
            bi.biCompression = 0;         // BI_RGB

            IntPtr bits;
            IntPtr dib = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;

            // curColor 是 COLORREF(0x00BBGGRR)：低字节是 R；DIB 内存顺序是 B,G,R
            int rr = curColor & 0xFF, gg = (curColor >> 8) & 0xFF, bb = (curColor >> 16) & 0xFF;
            byte[] buf = new byte[scrW * scrH * 4];
            for (int i = 0; i < buf.Length; i += 4)
            {
                buf[i] = (byte)bb; buf[i + 1] = (byte)gg; buf[i + 2] = (byte)rr; buf[i + 3] = 255;
            }
            Marshal.Copy(buf, 0, bits, buf.Length);

            IntPtr mem = CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = SelectObject(mem, dib);

            POINT dst = new POINT(); dst.X = 0; dst.Y = 0;
            POINT src = new POINT(); src.X = 0; src.Y = 0;
            SIZE size = new SIZE(); size.CX = scrW; size.CY = scrH;
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = 0;
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;        // AC_SRC_ALPHA

            bool ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, mem, ref src, 0, ref blend, 2);

            SelectObject(mem, old);
            DeleteDC(mem);
            DeleteObject(dib);
            return ok;
        }

        static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == 0x000F)   // WM_PAINT
            {
                PAINTSTRUCT ps;
                IntPtr dc = BeginPaint(h, out ps);
                RECT rc;
                GetClientRect(h, out rc);
                IntPtr br = CreateSolidBrush(curColor);
                FillRect(dc, ref rc, br);
                DeleteObject(br);
                EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            if (m == 0x0014) return (IntPtr)1;   // WM_ERASEBKGND
            return DefWindowProc(h, m, w, l);
        }

        /// <summary>
        /// 抓整屏。必须用 BitBlt + CAPTUREBLT：
        /// CAPTUREBLT(0x40000000) 才会把分层窗口的内容包含进来。
        /// （之前用 Graphics.CopyFromScreen 传组合枚举会直接抛 ArgumentException，等于没抓）
        /// </summary>
        static Bitmap Grab()
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new Exception("GetDC(NULL) 失败");
            IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, scrW, scrH);
                old = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, scrW, scrH, screen, 0, 0, ROP_CAPTURE))
                    throw new Exception("BitBlt 失败 err=" + Marshal.GetLastWin32Error());
                SelectObject(mem, old);
                old = IntPtr.Zero;
                return Image.FromHbitmap(bmp);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        static void Shot(string name)
        {
            try
            {
                string path = Path.Combine(shotDir, name);
                using (Bitmap bmp = Grab()) bmp.Save(path, ImageFormat.Png);
                Log.Note("  截图 " + path);
            }
            catch (Exception ex) { Log.Note("  截图失败：" + ex.Message); }
        }

        static int SampleColor()
        {
            // curColor 是 COLORREF：低字节 R、其次 G、高字节 B（之前这里写反了，测色全错）
            int tr = curColor & 0xFF, tg = (curColor >> 8) & 0xFF, tb = (curColor >> 16) & 0xFF;
            try
            {
                using (Bitmap full = Grab())
                using (Bitmap small = new Bitmap(64, 36))
                {
                    using (Graphics g = Graphics.FromImage(small))
                        g.DrawImage(full, 0, 0, 64, 36);
                    int hit = 0;
                    for (int y = 0; y < 36; y++)
                        for (int x = 0; x < 64; x++)
                        {
                            Color c = small.GetPixel(x, y);
                            if (Math.Abs(c.B - tb) <= 16 && Math.Abs(c.G - tg) <= 16 && Math.Abs(c.R - tr) <= 16)
                                hit++;
                        }
                    return (int)Math.Round(hit * 100.0 / (64 * 36));
                }
            }
            catch (Exception ex) { Log.Note("  采样失败：" + ex.Message); return -1; }
        }

        // ---------------- 杂项 ----------------
        static void DumpRect(string tag, IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            RECT wr, cr;
            GetWindowRect(h, out wr);
            GetClientRect(h, out cr);
            Log.Note(tag + " 窗口 " + wr.L + "," + wr.T + " " + (wr.R - wr.L) + "x" + (wr.B - wr.T)
                     + "　客户区 " + (cr.R - cr.L) + "x" + (cr.B - cr.T));
        }

        static IntPtr FindFullWorker(IntPtr parent)
        {
            IntPtr c = IntPtr.Zero;
            for (int i = 0; i < 32; i++)
            {
                c = FindWindowEx(parent, c, null, null);
                if (c == IntPtr.Zero) break;
                if (ClassOf(c) != "WorkerW") continue;
                RECT r;
                GetWindowRect(c, out r);
                if (r.R - r.L >= scrW - 8 && r.B - r.T >= scrH - 8) return c;
            }
            return IntPtr.Zero;
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "-";
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static void Pump(int ms)
        {
            int end = Environment.TickCount + ms;
            MSG msg;
            while (Environment.TickCount < end)
            {
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                System.Threading.Thread.Sleep(8);
            }
        }
    }
}
