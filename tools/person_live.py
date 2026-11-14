# -*- coding: utf-8 -*-
"""
真机验收：把「人物动效」挂到桌面上，最小化挡着桌面的窗口，连抓两帧比像素。

为什么必须一条命令跑完：命令行启动的 GUI 进程会在命令结束时被回收，
所以「启动 → 等待 → 最小化 → 抓屏 → 收尾」都要写在同一次运行里。

用法：
    python tools/person_live.py <图片> [动效] [等待秒数]
       动效 0=场景 1=缩放 2=人物（默认 2）
"""
import os
import subprocess
import sys
import time

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import diag

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(ROOT, "app", "bin", "WallpaperPlayer.exe")
OUT = os.path.join(ROOT, "tools", "out", "person", "live")


def main():
    img = sys.argv[1] if len(sys.argv) > 1 else r"D:\Wallpaper\images\biliart-17403571-4.jpg"
    effect = int(sys.argv[2]) if len(sys.argv) > 2 else 2
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 7.0
    os.makedirs(OUT, exist_ok=True)

    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(0.8)

    args = [EXE, "--image", img, "--fps", "30", "--effect", str(effect),
            "--class", "WpPlayerWnd", "--mount=8"]
    print("启动：", " ".join(args))
    p = subprocess.Popen(args)
    try:
        time.sleep(wait)
        saved = diag.hide_foreground()
        time.sleep(1.6)

        a = diag.grab()
        a.save(os.path.join(OUT, "a.png"))
        time.sleep(2.4)
        b = diag.grab()
        b.save(os.path.join(OUT, "b.png"))
        diag.restore(saved)

        A = np.asarray(a.convert("RGB"), np.float32)
        B = np.asarray(b.convert("RGB"), np.float32)
        d = np.abs(A - B).max(2)
        print("屏幕 %dx%d  两帧差异：平均 %.2f，像素占比(>8) %.2f%%"
              % (a.size[0], a.size[1], d.mean(), 100 * (d > 8).mean()))

        Image.fromarray(np.clip(d * 4, 0, 255).astype(np.uint8)).save(os.path.join(OUT, "diff.png"))
        a.convert("RGB").resize((960, int(960 * a.size[1] / a.size[0])), Image.LANCZOS).save(
            os.path.join(OUT, "a-small.png"))

        # 分区统计：确认动的是人物、不是整个屏幕
        W, H = a.size
        for name, (y0, y1, x0, x1) in {
            "脸部": (0.42, 0.75, 0.34, 0.56),
            "左侧长发": (0.55, 0.95, 0.16, 0.36),
            "右侧袖子": (0.25, 0.75, 0.62, 0.92),
            "左上角": (0.02, 0.20, 0.02, 0.12),
            "右上角": (0.02, 0.20, 0.88, 0.98),
        }.items():
            sub = d[int(y0 * H):int(y1 * H), int(x0 * W):int(x1 * W)]
            print("  %-6s 平均变化 %5.2f  像素>8 占比 %5.1f%%" % (name, sub.mean(), 100 * (sub > 8).mean()))
    finally:
        subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return 0


if __name__ == "__main__":
    sys.exit(main())
