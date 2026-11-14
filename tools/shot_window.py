# -*- coding: utf-8 -*-
"""按窗口标题抓一张图，不需要窗口在前台。

为什么要 PrintWindow：这台机器上编辑器会反复抢前台，SetForegroundWindow 之后再截屏
经常截到编辑器自己。PrintWindow(hwnd, hdc, 2/=PW_RENDERFULLCONTENT) 让窗口自己往
内存 DC 里画一份，被挡住也没关系。

用法：
    py -3 tools/shot_window.py <窗口标题> [输出png] [索引]
输出：默认 tools/out/window.png
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys

from PIL import Image

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 2
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def find_windows(title):
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(h, l):
        if user32.IsWindowVisible(h):
            n = user32.GetWindowTextLengthW(h)
            if n:
                b = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(h, b, n + 1)
                if b.value == title:
                    r = wt.RECT()
                    user32.GetWindowRect(h, ctypes.byref(r))
                    out.append((h, r.left, r.top, r.right - r.left, r.bottom - r.top))
        return True

    user32.EnumWindows(cb, 0)
    out.sort(key=lambda x: x[3] * x[4], reverse=True)
    return out


def grab(h, w, hgt, path):
    hdc = user32.GetWindowDC(h)
    mdc = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, hgt)
    old = gdi32.SelectObject(mdc, bmp)
    ok = user32.PrintWindow(h, mdc, PW_RENDERFULLCONTENT)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -hgt          # 负数 = 自上而下
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0

    buf = ctypes.create_string_buffer(w * hgt * 4)
    got = gdi32.GetDIBits(mdc, bmp, 0, hgt, buf, ctypes.byref(bi), DIB_RGB_COLORS)

    img = Image.frombuffer("RGBA", (w, hgt), buf, "raw", "BGRA", 0, 1).convert("RGB")
    img.save(path)

    gdi32.SelectObject(mdc, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(h, hdc)
    return ok, got


def main():
    title = sys.argv[1] if len(sys.argv) > 1 else "设置"
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join("tools", "out", "window.png")
    idx = int(sys.argv[3]) if len(sys.argv) > 3 else 0

    ws = find_windows(title)
    print("windows:", ws)
    if not ws:
        print("没找到标题为", title, "的窗口")
        return 1
    h, x, y, w, hgt = ws[min(idx, len(ws) - 1)]
    os.makedirs(os.path.dirname(out), exist_ok=True)
    ok, got = grab(h, w, hgt, out)
    print("PrintWindow=%s GetDIBits=%s size=%dx%d -> %s" % (ok, got, w, hgt, out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
