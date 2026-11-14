# -*- coding: utf-8 -*-
"""
出片后处理：把云端生成的 mp4 变成「能直接当壁纸」的样子。

主人定下的三条规矩，这个脚本一次做完：
  1. 去掉「AI生成」水印（delogo 用周边像素插值补掉，模糊背景上完全看不出来）
  2. 时长压到 3 秒以内（省额度、循环更快）
  3. 无缝循环：前半段正放 + 后半段倒放（ping-pong）。单条片子只有 L 秒时，
     文件级「首尾同帧」在数学上不可能，正放倒放是唯一能做到 0 跳帧的办法，
     头发/衣摆这种来回摆动的动作倒放看起来就是自然摆动，不会有「倒带」感。

用法：
  python video_fix.py <输入.mp4> <输出.mp4> [--seconds 3] [--no-loop] [--wm x,y,w,h]

--wm 用相对坐标（0~1），默认 0.007,0.008,0.066,0.060 是本服务水印的实测位置，
换服务商时量一次改这里就行。
"""
import argparse
import json
import os
import re
import subprocess
import sys

import imageio_ffmpeg

FF = imageio_ffmpeg.get_ffmpeg_exe()

# 本服务水印「AI生成」的实测相对位置（1344x768 上量出来的）
DEFAULT_WM = (0.007, 0.008, 0.066, 0.060)


def probe(path):
    """返回 (宽, 高, 时长秒, 帧率)"""
    out = subprocess.run(
        [FF, "-hide_banner", "-i", path],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    txt = out.stderr or ""
    w = h = 0
    fps = 24.0
    dur = 0.0

    m = re.search(r"Duration:\s*(\d+):(\d+):([\d.]+)", txt)
    if m:
        dur = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))

    m = re.search(r"Video:.*?,\s*(\d{2,5})x(\d{2,5})", txt)
    if m:
        w, h = int(m.group(1)), int(m.group(2))

    m = re.search(r"([\d.]+)\s*fps", txt)
    if m:
        fps = float(m.group(1))
    return w, h, dur, fps


def build_filter(w, h, seconds, wm, loop):
    x, y, ww, hh = wm
    # delogo 要求 x/y 至少留 1 像素给边框插值，宽高也留点余量
    dx = max(1, int(round(x * w)) - 2)
    dy = max(1, int(round(y * h)) - 2)
    dw = int(round(ww * w)) + 4
    dh = int(round(hh * h)) + 4
    dw -= dw % 2
    dh -= dh % 2

    parts = ["delogo=x=%d:y=%d:w=%d:h=%d" % (dx, dy, dw, dh)]
    parts.append("trim=0:%.3f" % seconds)
    parts.append("setpts=PTS-STARTPTS")
    parts.append("fps=24")
    if loop:
        # 正放 + 倒放：先用 split 复制两路，第二路 reverse 后接在后面
        return ("[0:v]" + ",".join(parts) + "[f];"
                "[f]split[a][b];[b]reverse[r];[a][r]concat=n=2:v=1:a=0[v]"), True
    return ("[0:v]" + ",".join(parts) + "[v]"), False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--seconds", type=float, default=3.0)
    ap.add_argument("--no-loop", action="store_true")
    ap.add_argument("--wm", default="")
    a = ap.parse_args()

    if not os.path.exists(a.src):
        print("找不到输入：" + a.src)
        return 1

    w, h, dur, fps = probe(a.src)
    if not w or not h:
        print("读不出视频规格，放弃")
        return 1
    seconds = min(a.seconds, 3.0, dur)          # 硬上限 3 秒

    wm = DEFAULT_WM
    if a.wm:
        wm = tuple(float(v) for v in a.wm.split(","))

    graph, looped = build_filter(w, h, seconds, wm, not a.no_loop)
    cmd = [FF, "-hide_banner", "-loglevel", "error", "-y", "-i", a.src,
           "-filter_complex", graph, "-map", "[v]", "-an",
           "-c:v", "libx264", "-preset", "medium", "-crf", "20",
           "-pix_fmt", "yuv420p", "-movflags", "+faststart", a.dst]
    r = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print("转码失败：\n" + (r.stderr or "")[-1500:])
        return 1

    w2, h2, dur2, _ = probe(a.dst)
    print(json.dumps({
        "src": a.src, "dst": a.dst,
        "in": "%dx%d %.2fs" % (w, h, dur),
        "out": "%dx%d %.2fs" % (w2, h2, dur2),
        "loop": looped,
        "bytes": os.path.getsize(a.dst),
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
