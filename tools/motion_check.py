# -*- coding: utf-8 -*-
"""
确认「图片动效」真的在动：连抓两帧，比较有多少像素变了。
不依赖 PIL —— 直接 ctypes 调 GDI，把屏幕位图 BitBlt 进一块 DIB，再读内存。
用法：python tools/motion_check.py [间隔秒]
"""
import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD),
        ("biWidth", wt.LONG),
        ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD),
        ("biBitCount", wt.WORD),
        ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD),
        ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG),
        ("biClrUsed", wt.DWORD),
        ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def grab(w, h):
    screen = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(screen)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0
    bits = ctypes.c_void_p()
    hbmp = gdi32.CreateDIBSection(screen, ctypes.byref(bmi), DIB_RGB_COLORS,
                                  ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(mem, hbmp)
    gdi32.BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT)
    buf = ctypes.string_at(bits, w * h * 4)
    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, screen)
    return buf


def main():
    gap = float(sys.argv[1]) if len(sys.argv) > 1 else 2.5
    w = user32.GetSystemMetrics(0)
    h = user32.GetSystemMetrics(1)
    print("屏幕 %dx%d，间隔 %.1fs" % (w, h, gap))

    a = grab(w, h)
    time.sleep(gap)
    b = grab(w, h)

    changed = 0
    total = w * h
    # 每 4 个像素采一个，够判断了
    for i in range(0, total, 4):
        o = i * 4
        if (abs(a[o] - b[o]) > 6 or abs(a[o + 1] - b[o + 1]) > 6
                or abs(a[o + 2] - b[o + 2]) > 6):
            changed += 1

    sampled = total // 4
    pct = changed * 100.0 / max(1, sampled)
    print("变化像素 %.2f%%（%d/%d）" % (pct, changed, sampled))
    print("结论：" + ("画面在动" if pct > 1.0 else "画面几乎没动（壁纸可能是静态的，或被窗口挡满）"))
    return 0 if pct > 1.0 else 1


if __name__ == "__main__":
    sys.exit(main())
