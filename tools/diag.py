# -*- coding: utf-8 -*-
"""
诊断脚本（一次性）：解决两个问题
  1) 屏幕顶部那条 16px 纯绿是什么 —— 打印播放器窗口的窗口矩形/客户区矩形，
     以及抓屏后顶部若干行的像素，判断绿条是在窗口之内还是之外。
  2) 换一张静态壁纸后动态壁纸为什么会消失 —— 设置一次壁纸，
     再查询播放器窗口是否还活着 / 还在 Progman 下面。

用法：
    python tools/diag.py [mountMode]
"""
import ctypes
import os
import subprocess
import sys
import time

from PIL import ImageGrab

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "app", "bin", "WallpaperPlayer.exe")
VIDEO = r"D:\Wallpaper\videos\dystopian-city-port.mp4"
OUT = os.path.join(ROOT, "tools", "out")

u = ctypes.WinDLL("user32", use_last_error=True)
SHELL = ctypes.WinDLL("shell32", use_last_error=True)

PROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]


def rect_of(h):
    r = RECT()
    u.GetWindowRect(h, ctypes.byref(r))
    return (r.l, r.t, r.r - r.l, r.b - r.t)


def client_origin(h):
    """客户区左上角在屏幕坐标系里的位置"""
    r = RECT()
    u.GetClientRect(h, ctypes.byref(r))
    pt = ctypes.wintypes.POINT(0, 0)
    u.ClientToScreen(h, ctypes.byref(pt))
    return (pt.x, pt.y, r.r - r.l, r.b - r.t)


def class_of(h):
    b = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(h, b, 256)
    return b.value


def find_player():
    pm = u.FindWindowW("Progman", None)
    if not pm:
        return 0
    # 直接子窗口里找
    h = u.FindWindowExW(pm, None, "WallpaperPlayerVideo", None)
    if h:
        return h
    h = u.FindWindowExW(pm, None, "WpPlayerWnd", None)
    if h:
        return h
    h = u.FindWindowW("WpPlayerWnd", None)
    if h:
        return h
    return u.FindWindowW("WallpaperPlayerVideo", None)


def z_order_of(h):
    """返回 (上层邻居, 下层邻居) 的描述"""
    above = u.GetWindow(h, 3)
    below = u.GetWindow(h, 2)
    def d(x):
        return "%s(%#x)" % (class_of(x), x) if x else "None"
    return d(above), d(below)


def grab():
    return ImageGrab.grab().convert("RGB")


def hide_foreground():
    """把挡着桌面的普通窗口最小化，返回可恢复列表"""
    pm = u.FindWindowW("Progman", None)
    tray = u.FindWindowW("Shell_TrayWnd", None)
    skip = {pm, tray, find_player()}
    saved = []

    def cb(h, l):
        if not u.IsWindowVisible(h):
            return True
        if h in skip:
            return True
        ex = u.GetWindowLongW(h, -20)
        st = u.GetWindowLongW(h, -16)
        # 只处理有标题的顶层窗口，跳过 tool window / 我们自己的分层窗
        if ex & 0x00000080:      # WS_EX_TOOLWINDOW
            return True
        if ex & 0x00080000:      # WS_EX_LAYERED
            return True
        if not (st & 0x10000000):  # WS_VISIBLE
            return True
        if u.GetWindowTextLengthW(h) == 0:
            return True
        u.ShowWindow(h, 6)       # SW_MINIMIZE
        saved.append(h)
        return True

    u.EnumWindows(PROC(cb), 0)
    return saved


def restore(saved):
    for h in saved:
        u.ShowWindow(h, 9)       # SW_RESTORE
        u.ShowWindow(h, 5)       # SW_SHOW


def main():
    mount = sys.argv[1] if len(sys.argv) > 1 else "8"
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)

    saved = hide_foreground()
    time.sleep(0.8)

    base = grab()
    base.save(os.path.join(OUT, "diag_base.png"))
    print("== 基线（无播放器）顶部像素 ==")
    w, hh = base.size
    for y in (0, 1, 8, 15, 16, 20):
        print("   y=%-3d %s" % (y, base.getpixel((w // 2, y))))

    p = subprocess.Popen([EXE, "--video", VIDEO, "--fps", "30",
                          "--class", "WpPlayerWnd", "--mount=%s" % mount],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(6.0)
        after = grab()
        after.save(os.path.join(OUT, "diag_after.png"))
        print("== 播放中顶部像素 ==")
        for y in (0, 1, 8, 15, 16, 20, 40):
            print("   y=%-3d %s" % (y, after.getpixel((w // 2, y))))

        ph = find_player()
        print("== 播放器窗口 ==")
        if ph:
            print("   hwnd=%#x class=%s" % (ph, class_of(ph)))
            print("   window rect (x,y,w,h) =", rect_of(ph))
            print("   client origin  (x,y,w,h) =", client_origin(ph))
            print("   parent = %s(%#x)" % (class_of(u.GetParent(ph)), u.GetParent(ph)))
            a, b = z_order_of(ph)
            print("   Z 上方 =", a, " Z 下方 =", b)
            print("   alive =", bool(u.IsWindow(ph)), "visible =", bool(u.IsWindowVisible(ph)))
        else:
            print("   !! 没找到播放器窗口")

        # --- 第二步：换一张静态壁纸，看动态壁纸是否还活着 ---
        print("== 设置静态壁纸后 ==")
        wall = r"C:\Windows\Web\Wallpaper\Windows\img0.jpg"
        SPI_SETDESKWALLPAPER = 0x0014
        SPIF_UPDATEINIFILE = 0x01
        SPIF_SENDCHANGE = 0x02
        ok = u.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, wall,
                                     SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)
        print("   SPI_SETDESKWALLPAPER ->", bool(ok))
        time.sleep(3.0)
        ph2 = find_player()
        print("   壁纸更换后播放器窗口 = %#x（原来 %#x）" % (ph2 or 0, ph or 0))
        if ph2:
            print("   window rect =", rect_of(ph2))
            a2, b2 = z_order_of(ph2)
            print("   Z 上方 =", a2, " Z 下方 =", b2)
            print("   visible =", bool(u.IsWindowVisible(ph2)))
        shot = grab()
        shot.save(os.path.join(OUT, "diag_afterwall.png"))
        print("   中心像素 =", shot.getpixel((w // 2, hh // 2)))
        print("   process alive =", p.poll() is None)
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()
        restore(saved)


if __name__ == "__main__":
    main()
