# -*- coding: utf-8 -*-
"""端到端验证：真的把图片通过桌面层播放器铺上去，抓两帧比对，再收尾。

用法：python tools/fx_e2e.py "<图片>" [effect] [fps] [间隔秒]
输出：fx-e2e-a.png / fx-e2e-b.png + 变化率 + 本次新增日志
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000


class BIH(ctypes.Structure):
    _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]


class BI(ctypes.Structure):
    _fields_ = [("bmiHeader", BIH), ("bmiColors", wt.DWORD * 3)]


def grab():
    user32.SetProcessDPIAware()
    w = user32.GetSystemMetrics(0)
    h = user32.GetSystemMetrics(1)
    screen = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(screen)
    bmi = BI()
    bmi.bmiHeader.biSize = ctypes.sizeof(BIH)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(screen, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(mem, hbmp)
    gdi32.BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT)
    raw = ctypes.string_at(bits, w * h * 4)
    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, screen)
    return Image.frombuffer("RGB", (w, h), raw, "raw", "BGRX", 0, 1).copy()


def grab_window(cls="WpPlayerWnd", title="WallpaperPlayerVideo"):
    """直接从播放器窗口的 DC 取内容：不受别的程序挡屏影响，
    而且能证明「画进窗口的内容」本身在变（窗口是 GDI 直画的）。"""
    pm = user32.FindWindowW("Progman", None)
    h = user32.FindWindowW(cls, None)
    if not h and pm:
        h = user32.FindWindowExW(pm, None, cls, None)
    if not h and pm:
        h = user32.FindWindowExW(pm, None, None, title)
    if not h:
        return None
    w = user32.GetSystemMetrics(0)
    hh = user32.GetSystemMetrics(1)
    dc = user32.GetDC(h)
    if not dc:
        return None
    mem = gdi32.CreateCompatibleDC(dc)
    bmi = BI()
    bmi.bmiHeader.biSize = ctypes.sizeof(BIH)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -hh
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(mem, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(mem, hbmp)
    gdi32.BitBlt(mem, 0, 0, w, hh, dc, 0, 0, SRCCOPY)
    raw = ctypes.string_at(bits, w * hh * 4)
    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(h, dc)
    return Image.frombuffer("RGB", (w, hh), raw, "raw", "BGRX", 0, 1).copy()


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    exe = os.path.join(root, "app", "bin", "WallpaperPlayer.exe")
    if not os.path.exists(exe):
        print("缺少播放器：" + exe)
        return 1

    img = sys.argv[1]
    effect = sys.argv[2] if len(sys.argv) > 2 else "0"
    fps = sys.argv[3] if len(sys.argv) > 3 else "30"
    gap = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0

    log = os.path.join(os.environ.get("TEMP", "."), "wallpaper-debug.log")
    start_line = 0
    if os.path.exists(log):
        with open(log, "rb") as f:
            start_line = len(f.read().splitlines())

    p = subprocess.Popen([exe, "--image", img, "--fps", fps, "--effect", effect,
                          "--mount=8", "--class", "WpPlayerWnd"],
                         cwd=os.path.join(root, "app", "bin"))
    try:
        time.sleep(6.0)
        a = grab()
        a.save(os.path.join(root, "fx-e2e-a.png"))
        wa = grab_window()
        if wa is not None:
            wa.save(os.path.join(root, "fx-win-a.png"))

        time.sleep(gap)
        b = grab()
        b.save(os.path.join(root, "fx-e2e-b.png"))
        wb = grab_window()
        if wb is not None:
            wb.save(os.path.join(root, "fx-win-b.png"))

        from PIL import ImageChops
        import numpy as np

        def report(a, b, tag):
            if a is None or b is None:
                print(tag + "：没抓到")
                return
            d = np.abs(np.asarray(a, dtype=np.int16) - np.asarray(b, dtype=np.int16)).mean(axis=2)
            print(tag + " %dx%d  平均差=%.4f  变化>2%%的像素=%.1f%%" %
                  (a.size[0], a.size[1], d.mean() / 255.0, (d > 5).mean() * 100))

        report(a, b, "全屏")
        report(wa, wb, "播放器窗口")
    finally:
        try:
            p.terminate()
            p.wait(timeout=6)
        except Exception:
            pass
        subprocess.call("taskkill /f /im WallpaperPlayer.exe", shell=True,
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    print("--- 新增日志 ---")
    if os.path.exists(log):
        with open(log, "rb") as f:
            lines = f.read().splitlines()[start_line:]
        for ln in lines[-25:]:
            try:
                print(ln.decode("utf-8", "replace"))
            except Exception:
                pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
