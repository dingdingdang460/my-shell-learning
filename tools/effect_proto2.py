# -*- coding: utf-8 -*-
"""
静态图「场景动效」原型 v2 —— 云 / 海 / 雨 / 灯 / 人物

思路不是「给整张图贴一个类别标签」，而是按**区域**判断：
  地平线在哪 → 上面是天空（云可以飘）、下面是水面（浪可以涌）
  有没有雨丝、有没有夜景灯光、有没有大块柔和主体（人物）
每块区域各自叠自己的运动，互不干扰。这样分类错一个也不会整张图崩。

这些运算只用「逐行整数位移 + 亮度调制 + 遮罩混合」，
可以原样搬到 C# 里用 unsafe 指针逐行 memmove，1080p 跑 30fps 没问题。
"""
import os
import sys
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H = 960, 540
FPS = 24

FONT_CANDIDATES = [r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\simhei.ttf"]


def font(size):
    for p in FONT_CANDIDATES:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def load_cover(path, w=W, h=H):
    im = Image.open(path).convert("RGB")
    sw, sh = im.size
    f = max(w / sw, h / sh)
    nw, nh = int(round(sw * f)), int(round(sh * f))
    im = im.resize((nw, nh), Image.LANCZOS)
    x, y = (nw - w) // 2, (nh - h) // 2
    return np.asarray(im.crop((x, y, x + w, y + h))).astype(np.float32) / 255.0


# --------------------------------------------------------------- 区域分析

def analyze(img):
    """返回「哪些区域能怎么动」，附带可读理由"""
    h, w, _ = img.shape
    g = (0.299 * img[..., 0] + 0.587 * img[..., 1] + 0.114 * img[..., 2])

    # ---- 地平线：行方向的水平边缘能量最大处（上 35%~85% 之间找）
    small = np.asarray(Image.fromarray((g * 255).astype(np.uint8))
                       .resize((320, 180), Image.BILINEAR)).astype(np.float32) / 255.0
    e = np.abs(np.diff(small, axis=0)).mean(axis=1)          # 每行一个能量
    lo, hi = int(len(e) * 0.32), int(len(e) * 0.86)
    band = e[lo:hi]
    horizon = None
    if band.size:
        k = int(np.argmax(band))
        if band[k] > max(1e-6, np.median(e)) * 1.35:
            horizon = (lo + k) / float(len(e))

    mean_lum = float(g.mean())
    bright = float((g > 0.72).mean())

    # ---- 水面：地平线以下偏蓝/青、且横向结构比纵向多
    water = False
    if horizon is not None:
        below = img[int(h * horizon) + 8:, :, :]
        if below.size:
            br, bg_, bb = below[..., 0].mean(), below[..., 1].mean(), below[..., 2].mean()
            bluish = bb > br * 1.02
            smooth_below = float(np.abs(np.diff(below.mean(axis=2), axis=0)).mean())
            water = bluish and smooth_below < 0.06

    # ---- 天空：地平线以上，亮度中等以上
    sky = False
    if horizon is not None:
        above = g[: int(h * horizon), :]
        sky = float(above.mean()) > 0.28

    # ---- 雨丝：近垂直细线 → 水平梯度 ≫ 垂直梯度
    gx = np.abs(np.diff(small, axis=1))
    gy = np.abs(np.diff(small, axis=0))[:, :gx.shape[1]]
    streak = float((gx > 0.055).mean() / ((gy > 0.055).mean() + 1e-6))
    rainy = streak > 1.45 and mean_lum < 0.55

    # ---- 夜景灯光：暗 + 大量小亮点
    night = mean_lum < 0.46 and bright > 0.012

    # ---- 人物/柔和主体：低对比暖色大面积集中在中部
    r, gg, b = img[..., 0], img[..., 1], img[..., 2]
    warm = ((r > gg) & (gg > b) & (r - b > 0.05) & (g > 0.30)).astype(np.float32)
    warm_small = np.asarray(Image.fromarray((warm * 255).astype(np.uint8))
                            .resize((80, 45), Image.BILINEAR)).astype(np.float32) / 255.0
    cy0, cy1 = int(0.15 * 45), int(0.95 * 45)
    cx0, cx1 = int(0.18 * 80), int(0.82 * 80)
    center_warm = float(warm_small[cy0:cy1, cx0:cx1].mean())
    subject_ratio = float(warm_small.mean())
    subject = center_warm > 0.45 and subject_ratio > 0.35 and not water

    tags = []
    if sky:
        tags.append("云层")
    if water:
        tags.append("水面")
    if rainy:
        tags.append("雨丝")
    if night:
        tags.append("灯光")
    if subject:
        tags.append("主体")

    return dict(horizon=horizon, sky=sky, water=water, rainy=rainy,
                night=night, subject=subject, tags=tags,
                mean_lum=mean_lum, bright=bright, streak=streak,
                subject_ratio=subject_ratio)


# --------------------------------------------------------------- 基础算子

def y_shift(img, dy_rows):
    """每行纵向取源行（越界镜像），用来做「浪涌上来」的起伏"""
    h, w, _ = img.shape
    idx = np.clip(np.arange(h) + np.asarray(dy_rows, dtype=np.float32).astype(np.int32), 0, h - 1)
    return img[idx]


def x_shift(img, dx_rows):
    """每行横向平移整数像素。越界用镜像补齐（不用 wrap：
    画面左边缘的椰树/沙滩会被卷到右边缘，一眼假）。C# 里是逐行 memmove + 边缘复制。"""
    h, w, _ = img.shape
    dx = np.asarray(dx_rows, dtype=np.float32).reshape(-1)
    if dx.size == 1:
        dx = np.full(h, float(dx[0]), dtype=np.float32)

    pad = int(np.abs(dx).max()) + 2
    big = np.pad(img, ((0, 0), (pad, pad), (0, 0)), mode="reflect")
    cols = np.arange(w, dtype=np.int32)[None, :] + pad - dx.astype(np.int32)[:, None]
    rows = np.arange(h, dtype=np.int32)[:, None]
    return big[rows, cols]


def feather(y0, y1, soft, h=H):
    y = np.arange(h, dtype=np.float32)
    m = np.clip((y - (y0 - soft)) / soft, 0, 1) * np.clip(((y1 + soft) - y) / soft, 0, 1)
    return m[:, None, None].astype(np.float32)


def flow_noise(t, amp, sx=120.0, sy=210.0, speed=0.25, h=H, w=W):
    yy = np.arange(h, dtype=np.float32)[:, None]
    xx = np.arange(w, dtype=np.float32)[None, :]
    n = (np.sin(xx / sx + t * speed) * np.sin(yy / sy - t * speed * 0.6)
         + 0.45 * np.sin((xx * 0.7 + yy * 1.3) / (sx * 0.5) + t * speed * 1.6))
    return (n * amp).astype(np.float32)


# --------------------------------------------------------------- 各种效果

def fx_cloud(img, t, a):
    """云：整片天空缓慢横移（越靠上越快），叠一层低频形变让云缘在呼吸"""
    h, w, _ = img.shape
    hz = int(h * (a["horizon"] if a["horizon"] else 0.55))
    rows = np.arange(h, dtype=np.float32)

    drift = 6.0 * np.sin(rows * 0.018 + t * 0.42) + 2.2 * t   # 持续向右飘（2.2px/s，比真实视频略快一点）
    warp = flow_noise(t, 1.0, sx=160.0, sy=260.0, speed=0.30)[:, 0]   # 每行形变分量
    dx = drift + warp * 3.0
    warped = x_shift(img, dx)

    m = feather(-999, hz, h * 0.14, h)
    m = np.clip(m, 0, 1)
    return img * (1 - m) + warped * m


def fx_sea(img, t, a):
    """海：长涌 + 碎浪两套横移叠在一起，并让浪线整体「往岸边推」，
    再给浪花/反光加一层流动高光"""
    h, w, _ = img.shape
    hz = int(h * (a["horizon"] if a["horizon"] else 0.6))
    rows = np.arange(h, dtype=np.float32)
    k = np.clip((rows - hz) / max(1.0, h - hz), 0, 1)      # 0=地平线 1=最下方
    depth = k ** 1.35                                      # 近处的浪更大

    dx = ((2.0 + 6.0 * depth) * np.sin(rows * 0.055 - t * 1.05)
          + (1.0 + 3.0 * depth) * np.sin(rows * 0.135 + t * 1.7))
    dy = (0.8 + 2.2 * depth) * np.sin(rows * 0.045 - t * 1.25)

    out = y_shift(x_shift(img, dx), dy)
    m = np.clip(feather(hz, h + 999, h * 0.06, h), 0, 1)
    out = img * (1 - m) + out * m

    # 浪花高光：亮的地方随浪走，一明一暗
    lum = out.max(axis=2, keepdims=True)
    sparkle = np.clip((lum - 0.55) * 2.2, 0, 1)
    glow = 1.0 + 0.13 * np.sin(rows[:, None, None] * 0.085 - t * 1.8) * sparkle
    return np.clip(out * glow, 0, 1)


def fx_rain(img, t, a):
    h, w, _ = img.shape
    layer = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(layer)
    for seed, count, speed, length, alpha in ((11, 150, 640, 30, 0.10), (37, 44, 900, 62, 0.07)):
        for i in range(count):
            rnd = (seed * 9301 + i * 49297) % 233280
            sx = (rnd * 7919) % w
            ph = (rnd * 104729 % 997) / 997.0
            y = (ph * h + t * speed) % (h + length) - length
            ln = length * (0.6 + 0.8 * (((rnd >> 5) % 100) / 100.0))
            d.line([(sx, y), (sx + 3.5, y + ln)], fill=int(alpha * 255), width=1)
    al = np.asarray(layer).astype(np.float32)[..., None] / 255.0
    return np.clip(img * (1 - al * 0.30) + al * 0.60, 0, 1)


def fx_lights(img, t, a):
    """夜景灯光：按空间相位轻微闪烁（像电流不稳）"""
    h, w, _ = img.shape
    lum = img.max(axis=2, keepdims=True)
    bright = np.clip((lum - 0.62) / 0.38, 0, 1)
    xx = np.arange(w, dtype=np.float32)[None, :, None]
    yy = np.arange(h, dtype=np.float32)[:, None, None]
    flick = np.sin(t * 3.1 + xx * 0.021) * 0.6 + np.sin(t * 1.7 - yy * 0.036) * 0.4
    return np.clip(img * (1.0 + 0.10 * flick * bright), 0, 1)


def fx_subject(img, t, a):
    """人物：主体几乎不动，背景缓慢视差 → 立刻有纵深；
    主体下半部（发梢/衣摆）极低频微摆；整体 0.3% 呼吸"""
    h, w, _ = img.shape
    m = subject_mask(img)
    bg = 1 - m

    dx = 3.0 * np.sin(t * 0.30) + 1.4 * np.sin(t * 0.17 + 1.1)
    out = img * m + x_shift(img, np.full(h, dx)) * bg

    k = np.clip((np.arange(h, dtype=np.float32) - h * 0.42) / (h * 0.58), 0, 1)[:, None, None]
    dx2 = (2.4 * np.sin(np.arange(h, dtype=np.float32) * 0.013 + t * 1.05))[:, None]
    sway = x_shift(out, dx2)
    out = out * (1 - m * k) + sway * (m * k)

    s = 1.0 + 0.0035 * math.sin(t * 2 * math.pi / 6.0)
    return zoom_crop(out, s, 0.5, 0.45)


def subject_mask(img):
    h, w, _ = img.shape
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    g_ = 0.299 * r + 0.587 * g + 0.114 * b
    m = ((r > g) & (g > b) & (r - b > 0.05) & (g > 0.30) & (g_ > 0.25)).astype(np.float32)
    sw, sh = max(1, w // 8), max(1, h // 8)
    small = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).resize((sw, sh), Image.BILINEAR)) / 255.0
    k = 4
    pad = np.pad(small, k, mode="edge")
    acc = np.zeros_like(small)
    for dy in range(-k, k + 1):
        for dx in range(-k, k + 1):
            acc += pad[k + dy:k + dy + sh, k + dx:k + dx + sw]
    small = np.clip((acc / ((2 * k + 1) ** 2) - 0.05) * 5.0, 0, 1)
    return (np.asarray(Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR))
            .astype(np.float32) / 255.0)[..., None]


def zoom_crop(img, scale, cx, cy):
    h, w, _ = img.shape
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    nw, nh = int(round(w * scale)), int(round(h * scale))
    im = im.resize((nw, nh), Image.BILINEAR)
    x = int(round((nw - w) * cx))
    y = int(round((nh - h) * cy))
    x = max(0, min(x, nw - w))
    y = max(0, min(y, nh - h))
    return np.asarray(im.crop((x, y, x + w, y + h))).astype(np.float32) / 255.0


def ken_burns(img, t, period=26.0, amp=0.07):
    ph = (t % period) / period
    s = 1.0 + amp * (0.5 - 0.5 * math.cos(ph * 2 * math.pi))
    return zoom_crop(img, s, 0.5 + 0.05 * math.sin(ph * 2 * math.pi),
                     0.5 + 0.035 * math.cos(ph * 2 * math.pi))


def apply_effects(img, t, a, base_zoom=True):
    out = img
    if a["sky"]:
        out = fx_cloud(out, t, a)
    if a["water"]:
        out = fx_sea(out, t, a)
    if a["rainy"]:
        out = fx_rain(out, t, a)
    if a["night"]:
        out = fx_lights(out, t, a)
    if a["subject"]:
        out = fx_subject(out, t, a)
    elif base_zoom:
        out = zoom_crop(out, 1.0 + 0.012 * (0.5 - 0.5 * math.cos(t * 2 * math.pi / 24.0)), 0.5, 0.5)
    return np.clip(out, 0, 1)


# --------------------------------------------------------------- 输出

def label(arr, text, sub=""):
    im = Image.fromarray((np.clip(arr, 0, 1) * 255).astype(np.uint8))
    d = ImageDraw.Draw(im, "RGBA")
    f1, f2 = font(28), font(18)
    tw = d.textlength(text, font=f1)
    d.rectangle([16, 16, 16 + tw + 26, 58], fill=(0, 0, 0, 140))
    d.text((29, 23), text, font=f1, fill=(255, 255, 255, 245))
    if sub:
        tw2 = d.textlength(sub, font=f2)
        d.rectangle([16, 62, 16 + tw2 + 26, 96], fill=(0, 0, 0, 120))
        d.text((29, 68), sub, font=f2, fill=(232, 232, 232, 240))
    return np.asarray(im)


def read_video_cover(path, w=W, h=H):
    """把视频按时间轴铺成「第几秒一帧」，方便和静态图同时间对齐"""
    import imageio
    rd = imageio.get_reader(path, "ffmpeg")
    frames = []
    for f in rd:
        frames.append(f)
        if len(frames) >= FPS * 9:
            break
    try:
        rd.close()
    except Exception:
        pass
    out = []
    for f in frames:
        im = Image.fromarray(f)
        sw, sh = im.size
        k = max(w / sw, h / sh)
        im = im.resize((int(sw * k), int(sh * k)), Image.LANCZOS)
        x, y = (im.size[0] - w) // 2, (im.size[1] - h) // 2
        out.append(np.asarray(im.crop((x, y, x + w, y + h))).astype(np.float32) / 255.0)
    return out


def render_compare(still_path, video_path, out_path, seconds=8):
    still = load_cover(still_path)
    a = analyze(still)
    print("区域分析：", a["tags"], "地平线=", None if a["horizon"] is None else round(a["horizon"], 2))
    print("  亮度=%.2f 亮点=%.1f%% 雨丝比=%.2f 主体占比=%.2f" %
          (a["mean_lum"], a["bright"] * 100, a["streak"], a["subject_ratio"]))

    real = read_video_cover(video_path)
    n = FPS * seconds
    import imageio
    wr = imageio.get_writer(out_path, fps=FPS, codec="libx264", quality=7, macro_block_size=1)
    for i in range(n):
        t = i / float(FPS)
        j = min(len(real) - 1, int(t * 30))
        left = label(real[j], "原视频（真运动）", "azure-horizon.mp4")
        right = label(apply_effects(still, t, a), "同一张图 + 场景动效", "云在飘 · 浪在涌（本地实时算）")
        wr.append_data(np.concatenate([left, right], axis=1))
    wr.close()
    print("wrote", out_path, os.path.getsize(out_path) // 1024, "KB")


def render_still_preview(still_path, out_png, t=2.0):
    still = load_cover(still_path)
    a = analyze(still)
    img = apply_effects(still, t, a)
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(out_png)
    print("preview ->", out_png, a["tags"])


if __name__ == "__main__":
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    still = os.path.join(root, "ref-frame0.png")
    if len(sys.argv) > 1 and sys.argv[1] == "preview":
        render_still_preview(still, os.path.join(root, "fx-preview.png"))
    else:
        render_compare(still, r"D:\Wallpaper\videos\azure-horizon.mp4",
                       os.path.join(root, "demo-sea.mp4"))
