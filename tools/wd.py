# -*- coding: utf-8 -*-
"""
看门狗验证：管理器开着动态壁纸时，把播放器进程干掉，
管理器的看门狗（4 秒一轮）应该自己把视频重新拉起来。

用法：python tools/wd.py
"""
import os
import subprocess
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MGR = os.path.join(ROOT, "app", "bin", "WallpaperManager.exe")
LOG = os.path.join(os.environ.get("TEMP", "."), "wallpaper-debug.log")


def count(name):
    out = subprocess.run(["tasklist", "/fi", "IMAGENAME eq " + name],
                         capture_output=True).stdout.decode("gbk", "ignore")
    return out.count(name)


def log_tail(n=8):
    try:
        with open(LOG, "rb") as f:
            data = f.read().decode("utf-8", "ignore")
        return "\n".join(data.strip().splitlines()[-n:])
    except Exception as e:
        return "读日志失败 " + str(e)


def main():
    subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)

    subprocess.Popen([MGR, "--dynvideo"])
    print("管理器和播放器启动中，等 12 秒…")
    time.sleep(12)
    print("  播放器进程数 =", count("WallpaperPlayer.exe"))

    print("强制结束播放器（模拟它掉线）…")
    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)
    print("  杀掉后进程数 =", count("WallpaperPlayer.exe"))

    print("等 10 秒，看看门狗有没有自己拉起来…")
    time.sleep(10)
    n = count("WallpaperPlayer.exe")
    print("  10 秒后进程数 =", n, "→", "已自动恢复" if n else "没恢复")

    print("=== 日志尾部 ===")
    print(log_tail(10))

    if os.environ.get("KEEP") != "1":
        subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        print("（已关闭管理器；加 KEEP=1 可保留）")


if __name__ == "__main__":
    main()
