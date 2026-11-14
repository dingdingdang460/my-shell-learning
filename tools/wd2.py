# -*- coding: utf-8 -*-
"""
看门狗细节验证：按 PID 杀掉播放器，然后每 3 秒观察一次，看管理器有没有把它拉回来。
用法：python tools/wd2.py
"""
import os
import subprocess
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
        if len(parts) > 1 and parts[0].lower().startswith(name.lower().split(".")[0]):
            res.append(parts[1])
    return res


def tail(n=4):
    try:
        with open(LOG, "rb") as f:
            return [l for l in f.read().decode("utf-8", "ignore").strip().splitlines()[-n:]]
    except Exception:
        return []


def main():
    subprocess.call(["taskkill", "/f", "/im", "WallpaperManager.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.call(["taskkill", "/f", "/im", "WallpaperPlayer.exe"],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    time.sleep(1)

    subprocess.Popen([MGR, "--dynvideo"])
    time.sleep(12)
    before = pids("WallpaperPlayer.exe")
    print("启动后播放器 PID =", before)
    if not before:
        print("!! 播放器没起来，先看日志：")
        print("\n".join(tail(6)))
        return

    for pid in before:
        print("kill PID", pid)
        subprocess.call(["taskkill", "/f", "/pid", pid],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    for i in range(6):
        time.sleep(3)
        now = pids("WallpaperPlayer.exe")
        print("  +%2ds  播放器 PID = %s" % ((i + 1) * 3, now))
        if now and now != before:
            print("  → 已由看门狗重新拉起：", now)
            break

    print("=== 日志尾 ===")
    print("\n".join(tail(8)))
    print("（管理器保留运行，方便继续观察；需要关掉请自己结束 WallpaperManager.exe）")


if __name__ == "__main__":
    main()
