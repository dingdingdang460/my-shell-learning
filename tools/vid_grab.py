# -*- coding: utf-8 -*-
"""把一段视频挂到桌面层播放，从**播放器窗口**抓几帧，用来核对云端生成的片子内容对不对。

用法：python tools/vid_grab.py "<视频>" [输出目录] [帧间隔秒]
产出：f0.png / f1.png / f2.png + 相邻帧差异，说明画面确实在动。
"""
import os
import subprocess
import sys
import time

import ctypes

# 必须先声明 DPI 感知：否则 GetSystemMetrics 返回的是 1536x864（125% 缩放后的逻辑尺寸），
# fx_e2e.grab_window 会按这个尺寸去 BitBlt —— 只截到窗口左上角 80%，看起来就像「视频被放大裁切」。
try:
    ctypes.windll.user32.SetProcessDPIAware()
except Exception:
    pass

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fx_e2e


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    exe = os.path.join(root, "app", "bin", "WallpaperPlayer.exe")
    vid = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "out", "vid")
    gap = float(sys.argv[3]) if len(sys.argv) > 3 else 1.4
    if not os.path.exists(exe):
        print("缺少播放器：" + exe)
        return 1
    if not os.path.exists(out):
        os.makedirs(out)

    p = subprocess.Popen([exe, "--video", vid, "--fps", "30",
                          "--mount=8", "--class", "WpPlayerWnd"],
                         cwd=os.path.join(root, "app", "bin"))
    try:
        time.sleep(4.5)
        frames = []
        for i in range(3):
            im = fx_e2e.grab_window()
            if im is None:
                print("第 %d 帧没抓到（窗口还没起？）" % i)
                break
            path = os.path.join(out, "f%d.png" % i)
            im.save(path)
            frames.append(im)
            print("抓帧 %s  %dx%d" % (path, im.size[0], im.size[1]))
            time.sleep(gap)

        if len(frames) >= 2:
            import numpy as np
            for i in range(len(frames) - 1):
                a = np.asarray(frames[i].convert("RGB"), np.int16)
                b = np.asarray(frames[i + 1].convert("RGB"), np.int16)
                d = np.abs(a - b).max(axis=2)
                print("帧 %d→%d 变化>6 的像素 %.2f%%  平均差 %.2f"
                      % (i, i + 1, 100 * (d > 6).mean(), d.mean()))
    finally:
        p.terminate()
        try:
            p.wait(5)
        except Exception:
            p.kill()
    return 0


if __name__ == "__main__":
    sys.exit(main())
