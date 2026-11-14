# -*- coding: utf-8 -*-
"""
循环播放稳定性测试：管理器开启动态壁纸后连续观察 60 秒，
覆盖至少一次「播完一轮」，确认播放器进程不再崩溃、桌面不再闪回系统壁纸。

用法：python tools/loop_test.py [秒]
"""
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MGR = os.path.join(ROOT, "app", "bin", "WallpaperManager.exe")
LOG = os.path.join(os.environ.get("TEMP", "."), "wallpaper-debug.log")


def pids(name):
    out = subprocess.run(["tasklist", "/fi", "IMAGENAME eq " + name, "/fo", "csv", "/nh"],
                         capture_output=True).stdout.decode("gbk", "ignore")
    res = []
    for line in out.splitlines():
        parts = [x.strip('"') for x in line.split('","')]
        if len(parts) > 1 and parts[0].lower().startswith("wallpaperplayer"):
            res.append(parts[1])
    return res


def main():
    dur = int(sys.argv[1]) if len(sys.argv) > 1 else 60
    subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)

    mark = 0
    try:
        with open(LOG, "rb") as f:
            mark = len(f.read().decode("utf-8", "ignore").splitlines())
    except Exception:
        pass

    subprocess.Popen([MGR, "--dynvideo"])
    print("观察 %d 秒…" % dur)
    seen = set()
    dead = 0
    for i in range(dur // 5):
        time.sleep(5)
        p = pids("WallpaperPlayer.exe")
        print("  +%2ds  PID=%s" % ((i + 1) * 5, p or "无"))
        if not p:
            dead += 1

    try:
        with open(LOG, "rb") as f:
            lines = f.read().decode("utf-8", "ignore").splitlines()
        new = lines[mark:]
        seen = [l for l in new if "播完一轮" in l or "看门狗" in l
                or "立即恢复" in l or "!!!" in l]
    except Exception:
        new = []

    print("\n=== 本次新增的关键日志 ===")
    for l in seen[-12:]:
        print(" ", l)
    print("\n循环次数 =", len([l for l in seen if "播完一轮" in l]),
          " 恢复次数 =", len([l for l in seen if "看门狗" in l or "立即恢复" in l]),
          " 采样到进程消失 =", dead)

    if os.environ.get("KEEP") != "1":
        subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


if __name__ == "__main__":
    main()
