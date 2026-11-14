# -*- coding: utf-8 -*-
"""
一次性验证脚本：启动播放器 -> 等它出画面 -> 抓屏 -> 和启动前的桌面做差异统计 -> 收尾。

为什么要自己抓屏：本机这台机器上，Progman 在 24H2 是 NOREDIRECTIONBITMAP，
能不能上屏只能看「合成后的最终画面」，日志里的 ULW 返回值证明不了任何事。
ImageGrab 抓的是 DWM 合成结果，正好能当判据。

用法：
    python tools/shot.py <mountMode> [额外等待秒数]
输出：
    tools/out/base_<n>.png / tools/out/after_<n>.png
    以及一段统计：变化像素占比、中心/角落颜色
"""
import os
import subprocess
import sys
import time

from PIL import ImageChops, ImageGrab

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "app", "bin", "WallpaperPlayer.exe")
VIDEO = r"D:\Wallpaper\videos\dystopian-city-port.mp4"
OUT = os.path.join(ROOT, "tools", "out")


def grab():
    return ImageGrab.grab().convert("RGB")


def changed_ratio(a, b, thresh=20):
    d = ImageChops.difference(a, b).convert("L")
    hist = d.histogram()
    total = float(sum(hist)) or 1.0
    return sum(hist[thresh:]) / total


def px(img, fx, fy):
    w, h = img.size
    return img.getpixel((int(w * fx), int(h * fy)))


def main():
    mount = sys.argv[1] if len(sys.argv) > 1 else "5"
    extra = float(sys.argv[2]) if len(sys.argv) > 2 else 0.0
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1.0)

    base = grab()
    base.save(os.path.join(OUT, "base_%s.png" % mount))

    p = subprocess.Popen([EXE, "--video", VIDEO, "--fps", "30",
                          "--class", "WpPlayerWnd", "--mount=%s" % mount])
    try:
        time.sleep(7.0 + extra)
        after = grab()
        after.save(os.path.join(OUT, "after_%s.png" % mount))

        r = changed_ratio(base, after)
        print("mount=%s  变化像素占比 %.1f%%" % (mount, r * 100))
        print("  中心(0.5,0.5)  前 %s  后 %s" % (px(base, .5, .5), px(after, .5, .5)))
        print("  左上(0.05,0.05) 前 %s  后 %s" % (px(base, .05, .05), px(after, .05, .05)))
        print("  左下(0.05,0.95) 前 %s  后 %s" % (px(base, .05, .95), px(after, .05, .95)))
        print("  右上(0.95,0.05) 前 %s  后 %s" % (px(base, .95, .05), px(after, .95, .05)))
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()


if __name__ == "__main__":
    main()
