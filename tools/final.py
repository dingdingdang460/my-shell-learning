# -*- coding: utf-8 -*-
"""
最终验收：管理器带 --dynvideo 启动 → 等视频上屏 → 最小化挡着桌面的窗口 →
抓屏存 tools/out/final_fixed.png → 打印顶部与中心像素 → 收尾。

一条命令里跑完是必须的：命令结束后它启动的进程会被回收。

用法：python tools/final.py [等待秒数]
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import diag  # 复用抓屏 / 最小化工具

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MGR = os.path.join(ROOT, "app", "bin", "WallpaperManager.exe")
OUT = os.path.join(ROOT, "tools", "out")


def main():
    wait = float(sys.argv[1]) if len(sys.argv) > 1 else 14.0
    subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)

    p = subprocess.Popen([MGR, "--dynvideo"])
    try:
        time.sleep(wait)
        saved = diag.hide_foreground()
        time.sleep(2.5)
        im = diag.grab()
        path = os.path.join(OUT, "final_fixed.png")
        im.save(path)
        w, h = im.size
        print("已保存", path, w, "x", h)
        print("顶部 y=0/8/15/20 =", [im.getpixel((w // 2, y)) for y in (0, 8, 15, 20)])
        print("中心 =", im.getpixel((w // 2, h // 2)))
        print("左上图标区 =", im.getpixel((40, 40)), im.getpixel((75, 40)))
        diag.restore(saved)
    finally:
        if os.environ.get("KEEP") != "1":
            subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


if __name__ == "__main__":
    main()
