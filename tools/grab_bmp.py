# -*- coding: utf-8 -*-
"""抓一张全屏 BMP（不依赖 PIL），用来肉眼确认桌面层到底显示了什么。
用法：python tools/grab_bmp.py [输出路径]"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

SRCCOPY = 0x00CC0020
CAPTUREBLT = 0x40000000


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
        ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
        ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
        ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "grab.bmp"
    try:
        user32.SetProcessDPIAware()
    except Exception:
        pass
    w = user32.GetSystemMetrics(0)
    h = user32.GetSystemMetrics(1)

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
    hbmp = gdi32.CreateDIBSection(screen, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    old = gdi32.SelectObject(mem, hbmp)
    gdi32.BitBlt(mem, 0, 0, w, h, screen, 0, 0, SRCCOPY | CAPTUREBLT)
    raw = ctypes.string_at(bits, w * h * 4)
    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, screen)

    # 转成 24bpp 自下而上 BMP（BMP 要求行从下往上）
    row = w * 3
    pad = (4 - row % 4) % 4
    body = bytearray()
    for y in range(h - 1, -1, -1):
        base = y * w * 4
        line = bytearray(row)
        for x in range(w):
            o = base + x * 4
            line[x * 3] = raw[o]
            line[x * 3 + 1] = raw[o + 1]
            line[x * 3 + 2] = raw[o + 2]
        body += line + b"\x00" * pad

    size = 54 + len(body)
    hdr = b"BM" + struct.pack("<IHHI", size, 0, 0, 54)
    hdr += struct.pack("<IiiHHIIiiII", 40, w, h, 1, 24, 0, len(body), 2835, 2835, 0, 0)
    with open(out, "wb") as f:
        f.write(hdr)
        f.write(body)
    print("%s  %dx%d" % (out, w, h))
    return 0


if __name__ == "__main__":
    sys.exit(main())
