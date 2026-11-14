$ErrorActionPreference = 'Stop'

$code = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public class DeskProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rc; public int f1, f2; [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] res; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct WNDCLASSEXW { public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    public delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern ushort RegisterClassEx(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetParent(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte a, uint f);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int m);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool e);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int c);
    [DllImport("gdi32.dll")] public static extern int FillRect(IntPtr dc, ref RECT r, IntPtr b);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG m, IntPtr h, uint mn, uint mx, uint rm);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000, WS_POPUP = 0x80000000;
    public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20;

    static WndProcDelegate proc;
    static int color = 0xFF00FF;

    static IntPtr WP(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == 0x000F)
        {
            PAINTSTRUCT ps; IntPtr dc = BeginPaint(h, out ps);
            RECT rc; GetClientRect(h, out rc);
            IntPtr br = CreateSolidBrush(color); FillRect(dc, ref rc, br); DeleteObject(br);
            EndPaint(h, ref ps);
            return IntPtr.Zero;
        }
        if (m == 0x0014) return (IntPtr)1;
        return DefWindowProc(h, m, w, l);
    }

    public static void Pump(int ms)
    {
        int end = Environment.TickCount + ms;
        MSG msg;
        while (Environment.TickCount < end)
        {
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            Thread.Sleep(8);
        }
    }

    public static string Cls(IntPtr h)
    {
        if (h == IntPtr.Zero) return "-";
        StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString();
    }

    public static void Shot(string path, int w, int h)
    {
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // returns log lines
    public static string Run(int mode, string shotDir)
    {
        StringBuilder log = new StringBuilder();
        try { SetProcessDPIAware(); } catch { }

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        IntPtr prog = FindWindow("Progman", null);
        IntPtr dv = FindWindowEx(prog, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr wk = IntPtr.Zero;
        IntPtr c = IntPtr.Zero;
        for (int i = 0; i < 32; i++)
        {
            c = FindWindowEx(prog, c, null, null);
            if (c == IntPtr.Zero) break;
            if (Cls(c) != "WorkerW") continue;
            RECT wr; GetWindowRect(c, out wr);
            if (wr.R - wr.L >= sw - 8 && wr.B - wr.T >= sh - 8) { wk = c; break; }
        }
        log.AppendLine("screen " + sw + "x" + sh + " prog=0x" + prog.ToString("x") + " defview=0x" + dv.ToString("x") + " worker=0x" + wk.ToString("x"));

        RECT pr, pc; GetWindowRect(prog, out pr); GetClientRect(prog, out pc);
        log.AppendLine("prog rect " + pr.L + "," + pr.T + " " + (pr.R - pr.L) + "x" + (pr.B - pr.T) + " client " + (pc.R - pc.L) + "x" + (pc.B - pc.T));
        if (wk != IntPtr.Zero) { GetWindowRect(wk, out pr); GetClientRect(wk, out pc); log.AppendLine("worker rect " + pr.L + "," + pr.T + " " + (pr.R - pr.L) + "x" + (pr.B - pr.T) + " client " + (pc.R - pc.L) + "x" + (pc.B - pc.T)); }
        if (dv != IntPtr.Zero) { GetWindowRect(dv, out pr); GetClientRect(dv, out pc); log.AppendLine("defview rect " + pr.L + "," + pr.T + " " + (pr.R - pr.L) + "x" + (pr.B - pr.T) + " client " + (pc.R - pc.L) + "x" + (pc.B - pc.T)); }

        IntPtr inst = GetModuleHandle(null);
        proc = new WndProcDelegate(WP);
        WNDCLASSEXW wc = new WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
        wc.style = 0x22;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
        wc.hInstance = inst;
        wc.lpszClassName = "DeskProbeCls" + Environment.TickCount;
        ushort reg = RegisterClassEx(ref wc);
        log.AppendLine("RegisterClassEx=" + reg + " err=" + Marshal.GetLastWin32Error());

        IntPtr parent = prog;
        if (mode == 2) parent = wk;
        if (mode == 3) parent = dv;
        if (parent == IntPtr.Zero) { log.AppendLine("no parent"); return log.ToString(); }

        color = 0x0000FF;   // red
        uint style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        uint ex = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        IntPtr h = CreateWindowEx(ex, wc.lpszClassName, "p", style, 0, 0, sw, sh, parent, IntPtr.Zero, inst, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        log.AppendLine("CreateWindowEx(mode=" + mode + ") hwnd=0x" + h.ToString("x") + " err=" + err);
        if (h == IntPtr.Zero) return log.ToString();

        bool swa = SetLayeredWindowAttributes(h, 0, 255, 2);
        log.AppendLine("SWA=" + swa + " err=" + Marshal.GetLastWin32Error());
        ShowWindow(h, 5);
        IntPtr after = (parent == prog) ? dv : IntPtr.Zero;
        bool sp = SetWindowPos(h, after, 0, 0, sw, sh, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        log.AppendLine("SetWindowPos=" + sp + " err=" + Marshal.GetLastWin32Error());

        RECT wr2; GetWindowRect(h, out wr2);
        int cl;
        int hr = DwmGetWindowAttribute(h, 14, out cl, 4);
        log.AppendLine("win rect " + wr2.L + "," + wr2.T + " " + (wr2.R - wr2.L) + "x" + (wr2.B - wr2.T) + " visible=" + IsWindowVisible(h) + " parent=0x" + GetParent(h).ToString("x") + "(" + Cls(GetParent(h)) + ") style=0x" + ((uint)GetWindowLong(h, GWL_STYLE)).ToString("x8") + " cloaked=" + cl + " hr=0x" + hr.ToString("x"));

        InvalidateRect(h, IntPtr.Zero, false); UpdateWindow(h);
        IntPtr dc = GetDC(h);
        if (dc != IntPtr.Zero) { RECT rc; GetClientRect(h, out rc); IntPtr br = CreateSolidBrush(color); FillRect(dc, ref rc, br); DeleteObject(br); ReleaseDC(h, dc); }
        Pump(800);

        string f = System.IO.Path.Combine(shotDir, "mode" + mode + ".png");
        Shot(f, sw, sh);
        log.AppendLine("shot " + f);

        DestroyWindow(h);
        Pump(150);
        return log.ToString();
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies System.Drawing, System.Windows.Forms

$dir = Join-Path $env:TEMP "wp2ps"
if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$out = @()
foreach ($m in 1,2,3) {
  try { $out += [DeskProbe]::Run($m, $dir) } catch { $out += ("mode $m EX: " + $_.Exception.Message) }
  $out += "-----"
}
$out -join "`n"
Write-Output ("SHOTDIR=" + $dir)
