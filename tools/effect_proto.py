# -*- coding: utf-8 -*-
"""
静态图「场景动效」原型：先判类型，再按类型叠运动。
输出左右对比片：
  左 = 现在的效果（只有缩放平移）
  右 = 新效果（场景识别 + 元素级运动）

这套数学是给 app/PlayerEffect.cs 打样的：只用「逐行整数位移 + 亮度调制 +
遮罩混合」，都能在 C# 里用 unsafe 指针按行 memmove 实现，1080p 也能跑 30fps。
"""
import os
import sys
import math
import numpy as np
from PIL import Image, ImageDraw, ImageFont

PANEL_W, PANEL_H = 960, 540
FPS = 24
SECONDS = 8

FONT_CANDIDATES = [
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\simhei.ttf",
]


def font(size):
    for p in FONT_CANDIDATES:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def load_cover(path, w, h):
    """等比放大裁切到 w x h（和 App 里 UniformToFill 一致）"""
    im = Image.open(path).convert("RGB")
    sw, sh = im.size
    f = max(w / sw, h / sh)
    nw, nh = int(round(sw * f)), int(round(sh * f))
    im = im.resize((nw, nh), Image.LANCZOS)
    x = (nw - w) // 2
    y = (nh - h) // 2
    im = im.crop((x, y, x + w, y + h))
    return np.asarray(im).astype(np.float32) / 255.0


# ---------------------------------------------------------------- 场景识别

def classify(img):
    """只用统计量判类型：亮度、肤色占比、天空平滑度、水平/垂直边缘比。
    结果要能解释，所以每项都给分数，不搞黑盒。"""
    h, w, _ = img.shape
    small = np.asarray(Image.fromarray((img * 255).astype(np.uint8))
                       .resize((320, 180), Image.BILINEAR)).astype(np.float32) / 255.0
    r, g, b = small[..., 0], small[..., 1], small[..., 2]
    lum = 0.299 * r + 0.587 * g + 0.114 * b

    mean_lum = float(lum.mean())
    bright = float((lum > 0.72).mean())
    dark = float((lum < 0.25).mean())

    # 肤色：R 明显高于 G/B，且不太饱和
    skin = ((r > 0.45) & (g > 0.22) & (b > 0.15) &
            (r - b > 0.06) & (r - g > 0.04) & (lum > 0.28) & (lum < 0.96))
    skin_ratio = float(skin.mean())
    cy0, cy1 = int(skin.shape[0] * 0.15), int(skin.shape[0] * 0.9)
    cx0, cx1 = int(skin.shape[1] * 0.2), int(skin.shape[1] * 0.8)
    center_skin = float(skin[cy0:cy1, cx0:cx1].mean())

    # 天空：上半部又亮又平滑
    top = lum[: int(lum.shape[0] * 0.38), :]
    gx = np.abs(np.diff(top, axis=1)).mean()
    gy = np.abs(np.diff(top, axis=0)).mean() if top.shape[0] > 2 else 0
    sky_smooth = float(1.0 / (1.0 + 40.0 * (gx + gy)))
    top_bright = float(top.mean())

    # 雨：近垂直细线 → 水平梯度 ≫ 垂直梯度
    hs = []
    vs = []
    for c in range(3):
        gxs = np.abs(np.diff(small[..., c], axis=1))
        gys = np.abs(np.diff(small[..., c], axis=0))[:, :gxs.shape[1]]
        hs.append(float((gxs > 0.08).mean()))
        vs.append(float((gys > 0.08).mean()))
    streak = float(np.mean(hs) / (np.mean(vs) + 1e-6))

    if skin_ratio > 0.030 and center_skin > skin_ratio * 1.6:
        kind = "portrait"
        why = "肤色占比 %.1f%%（画面中部 %.1f%%）→ 人物" % (skin_ratio * 100, center_skin * 100)
    elif mean_lum < 0.46 and bright > 0.012:
        kind = "nightcity"
        why = "平均亮度 %.2f + 亮点占比 %.1f%% → 夜景城市" % (mean_lum, bright * 100)
    elif top_bright > 0.5 and sky_smooth > 0.4:
        kind = "sky"
        why = "上半部亮度 %.2f、平滑度 %.2f → 天空风景" % (top_bright, sky_smooth)
    else:
        kind = "generic"
        why = "没有明显特征（亮度 %.2f）→ 走通用缓动" % mean_lum

    rainy = streak > 1.35 and mean_lum < 0.55
    return dict(kind=kind, why=why, rainy=rainy,
                skin_ratio=skin_ratio, mean_lum=mean_lum,
                bright=bright, streak=streak)


def skin_mask(img):
    """给人物用：肤色区域 + 外扩，得到「主体」遮罩（羽化后当混合权重）"""
    h, w, _ = img.shape
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    m = ((r > 0.45) & (g > 0.22) & (b > 0.15) &
         (r - b > 0.06) & (r - g > 0.04) & (lum > 0.28)).astype(np.float32)

    small = np.asarray(Image.fromarray((m * 255).astype(np.uint8))
                       .resize((max(1, w // 8), max(1, h // 8)), Image.BILINEAR)) / 255.0
    # 膨胀：简单盒式模糊再阈值
    k = 5
    pad = np.pad(small, k, mode="edge")
    acc = np.zeros_like(small)
    for dy in range(-k, k + 1):
        for dx in range(-k, k + 1):
            acc += pad[k + dy:k + dy + small.shape[0], k + dx:k + dx + small.shape[1]]
    small = acc / ((2 * k + 1) ** 2)
    small = np.clip((small - 0.06) * 6.0, 0, 1)
    big = np.asarray(Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR)) / 255.0
    return big


# ---------------------------------------------------------------- 基础算子

def rows_shift(img, dx_rows):
    """每行横向平移整数像素（列方向 wrap），C# 里就是逐行 memmove"""
    h, w, _ = img.shape
    dx = np.asarray(dx_rows, dtype=np.float32).reshape(-1)
    if dx.size == 1:
        dx = np.full(h, float(dx[0]), dtype=np.float32)
    cols = np.mod(np.arange(w, dtype=np.int32)[None, :] - dx.astype(np.int32)[:, None], w)
    rows = np.arange(h, dtype=np.int32)[:, None]
    return img[rows, cols]


def feather(h, y0, y1, soft):
    """纵向羽化权重：y0..y1 内为 1，两侧 soft 像素平滑过渡"""
    y = np.arange(h, dtype=np.float32)
    wgt = np.clip((y - (y0 - soft)) / soft, 0, 1) * np.clip(((y1 + soft) - y) / soft, 0, 1)
    return wgt[:, None, None]


def smooth_noise(h, w, t, amp, scale=90.0, speed=0.35):
    """用几组正弦拼一个缓慢流动的低频场（不引入随机，跨帧连续）"""
    yy = np.arange(h, dtype=np.float32)[:, None]
    xx = np.arange(w, dtype=np.float32)[None, :]
    n = (np.sin(xx / scale + t * speed) * np.sin(yy / (scale * 1.7) - t * speed * 0.7)
         + 0.5 * np.sin((xx + yy) / (scale * 0.6) + t * speed * 1.3))
    return (n * amp).astype(np.float32)


# ---------------------------------------------------------------- 各类效果

def fx_sky(img, t, info):
    """天空：云层缓慢横移 + 雾气流淌（只在天空区域，羽化过渡）"""
    h, w, _ = img.shape
    horizon = int(h * info.get("horizon", 0.55))
    rows = np.arange(h, dtype=np.float32)
    dx = (7.0 * np.sin(rows * 0.021 + t * 0.55)
          + 4.0 * np.sin(rows * 0.047 - t * 0.9)) * (1.0 - rows / max(1, h) * 0.6)
    warped = rows_shift(img, dx)
    m = feather(h, 0, horizon, h * 0.12)
    out = img * (1 - m) + warped * m

    fog = smooth_noise(h, w, t, 0.035, scale=140.0, speed=0.28)[..., None]
    return np.clip(out + fog * m, 0, 1)


def fx_water(img, t, info):
    """水面 / 湿地反光：波纹越靠下越大，同时亮部像被水搅动一样轻微起伏"""
    h, w, _ = img.shape
    y0 = int(h * info.get("water_top", 0.62))
    rows = np.arange(h, dtype=np.float32)
    k = np.clip((rows - y0) / max(1, h - y0), 0, 1)
    dx = (4.5 * np.sin(rows * 0.16 + t * 2.6) + 2.0 * np.sin(rows * 0.33 - t * 3.4)) * k
    warped = rows_shift(img, dx)
    m = feather(h, y0, h, h * 0.08)
    out = img * (1 - m) + warped * m

    lum = out.max(axis=2, keepdims=True)
    shimmer = 1.0 + 0.10 * np.sin(rows[:, None, None] * 0.22 + t * 3.1) * np.clip(lum - 0.55, 0, 1) * 2.0
    return np.clip(out * shimmer, 0, 1)


def fx_rain(img, t, info):
    """雨丝：两层（远细近粗），斜向下落，落在亮部时更明显"""
    h, w, _ = img.shape
    layer = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(layer)

    def streak(seed, count, speed, length, alpha_base):
        for i in range(count):
            rnd = (seed * 9301 + i * 49297) % 233280
            sx = (rnd * 7919) % w
            phase = (rnd * 104729 % 1000) / 1000.0
            y = (phase * h + t * speed) % (h + length) - length
            ln = length * (0.6 + 0.8 * (((rnd >> 3) % 100) / 100.0))
            slant = 3.5
            d.line([(sx, y), (sx + slant, y + ln)], fill=int(alpha_base * 255), width=1)

    streak(11, 130, 520, 34, 0.10)
    streak(37, 46, 760, 64, 0.07)
    a = np.asarray(layer).astype(np.float32)[..., None] / 255.0
    return np.clip(img * (1 - a * 0.35) + a * 0.55, 0, 1)


def fx_neon(img, t, info):
    """霓虹/灯光：亮部按空间相位轻微闪烁，整体亮度呼吸（模拟电流不稳）"""
    lum = img.max(axis=2, keepdims=True)
    bright = np.clip((lum - 0.62) / 0.38, 0, 1)
    h, w, _ = img.shape
    xx = np.arange(w, dtype=np.float32)[None, :, None]
    yy = np.arange(h, dtype=np.float32)[:, None, None]
    flick = (np.sin(t * 3.1 + xx * 0.021) * 0.6 + np.sin(t * 1.7 - yy * 0.036) * 0.4)
    gain = 1.0 + 0.16 * flick * bright
    return np.clip(img * gain, 0, 1)


def fx_portrait(img, t, info):
    """人物：背景视差漂移（主体不动）→ 立刻有纵深；主体下半部（发梢/衣服）
    跟着频率极低的横向微摆；整体 0.3% 呼吸缩放"""
    h, w, _ = img.shape
    m = info["subject"]                      # 1 = 主体
    bg = 1.0 - m

    # 背景：慢速横移 + 轻微纵向漂移（视差），幅度小但持续
    rows = np.arange(h, dtype=np.float32)
    dx = 3.2 * np.sin(t * 0.33) + 1.6 * np.sin(rows * 0.01 + t * 0.21)
    shifted = rows_shift(img, np.full(h, 0.0) + dx.mean())
    out = img * m + shifted * bg

    # 发梢/衣摆：只作用在主体下部，随高度错相
    k = np.clip((np.arange(h, dtype=np.float32) - h * 0.45) / (h * 0.55), 0, 1)[:, None, None]
    dx2 = (2.6 * np.sin(np.arange(h, dtype=np.float32) * 0.017 + t * 1.15))[:, None]
    sw = rows_shift(img, dx2)
    out = out * (1 - m * k) + sw * (m * k)

    # 呼吸
    s = 1.0 + 0.0035 * math.sin(t * 2 * math.pi / 6.0)
    out = zoom_crop(out, s, 0.5, 0.42)
    return np.clip(out, 0, 1)


def zoom_crop(img, scale, cx, cy):
    """以 (cx,cy) 为中心放大 scale 倍并裁回原尺寸（浮点用双线性，一次即可）"""
    h, w, _ = img.shape
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    nw, nh = int(round(w * scale)), int(round(h * scale))
    im = im.resize((nw, nh), Image.BILINEAR)
    x = int(round((nw - w) * cx))
    y = int(round((nh - h) * cy))
    im = im.crop((max(0, min(x, nw - w)), max(0, min(y, nh - h)),
                  max(0, min(x, nw - w)) + w, max(0, min(y, nh - h)) + h))
    return np.asarray(im).astype(np.float32) / 255.0


def ken_burns(img, t, period=26.0, amp=0.07):
    """现在的效果：缓慢缩放 + 一点漂移（对照用）"""
    ph = (t % period) / period
    s = 1.0 + amp * (0.5 - 0.5 * math.cos(ph * 2 * math.pi))
    cx = 0.5 + 0.06 * math.sin(ph * 2 * math.pi)
    cy = 0.5 + 0.04 * math.cos(ph * 2 * math.pi)
    return zoom_crop(img, s, cx, cy)


def apply_effects(img, t, cls, info):
    kind = cls["kind"]
    out = img
    if kind == "nightcity":
        out = fx_sky(out, t, info)
        out = fx_neon(out, t, info)
        if cls["rainy"]:
            out = fx_rain(out, t, info)
        out = fx_water(out, t, info)
    elif kind == "sky":
        out = fx_sky(out, t, info)
        out = fx_water(out, t, info)
    elif kind == "portrait":
        out = fx_portrait(out, t, info)
    else:
        out = ken_burns(out, t)
    return out


# ---------------------------------------------------------------- 合成

LABEL_FONT = None


def label_panel(arr, text, sub):
    im = Image.fromarray((np.clip(arr, 0, 1) * 255).astype(np.uint8))
    d = ImageDraw.Draw(im, "RGBA")
    f1 = font(30)
    f2 = font(20)
    tw = d.textlength(text, font=f1)
    d.rectangle([18, 18, 18 + tw + 28, 62], fill=(0, 0, 0, 130))
    d.text((32, 26), text, font=f1, fill=(255, 255, 255, 240))
    if sub:
        tw2 = d.textlength(sub, font=f2)
        d.rectangle([18, 66, 18 + tw2 + 28, 102], fill=(0, 0, 0, 110))
        d.text((32, 73), sub, font=f2, fill=(230, 230, 230, 235))
    return np.asarray(im)


def render(src, out_path, cls, info):
    left = load_cover(src, PANEL_W, PANEL_H)
    right = left.copy()
    n = FPS * SECONDS
    frames = []
    for i in range(n):
        t = i / float(FPS)
        a = label_panel(ken_burns(left, t), "现在：只有缩放平移", "")
        b = label_panel(apply_effects(right, t, cls, info), "新：%s 场景动效" % KIND_CN[cls["kind"]], cls["why"])
        canvas = np.concatenate([a, b], axis=1)
        frames.append(canvas)

    import imageio
    writer = imageio.get_writer(out_path, fps=FPS, codec="libx264", quality=8,
                                macro_block_size=1)
    for f in frames:
        writer.append_data(f)
    writer.close()
    print("wrote", out_path, os.path.getsize(out_path) // 1024, "KB")


KIND_CN = {"nightcity": "雨夜城市", "sky": "天空风景", "portrait": "人物", "generic": "通用"}


def main():
    jobs = [
        (r"D:\Wallpaper\images\rainy-cyberpunk-streets.jpg", "demo-rain-city.mp4", dict(horizon=0.42, water_top=0.60)),
        (r"D:\Wallpaper\images\biliart-17403571-4.jpg", "demo-portrait.mp4", None),
    ]
    out_dir = os.path.dirname(os.path.abspath(__file__))
    for src, name, extra in jobs:
        img = load_cover(src, PANEL_W, PANEL_H)
        cls = classify(img)
        info = dict(extra or {})
        if cls["kind"] == "portrait":
            info["subject"] = skin_mask(img)
        print("%-38s → %-9s %s  雨=%s streak=%.2f" %
              (os.path.basename(src), cls["kind"], cls["why"], cls["rainy"], cls["streak"]))
        render(src, os.path.join(out_dir, "..", name), cls, info)


if __name__ == "__main__":
    main()
