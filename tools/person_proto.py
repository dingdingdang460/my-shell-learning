# -*- coding: utf-8 -*-
"""
人物图「局部运动」原型 v2 —— 纯算法、零依赖（无模型 / 无 OpenCV）。

v1 的失败：这张图的背景也是「亮虚化」，按亮度找主体把整张图都选中了（subject=79%）。
v2 换判据 —— 用**局部梯度能量**分人/背景：
    头发 8.5 / 帽子 8.2 / 袖子 5.7  vs  虚化背景 1.3 ~ 4.4
  流程：梯度种子 → 膨胀闭轮廓 → 取最大连通域（人） → 从边界 flood 填内部空洞
        （大面积光滑的头发/衣服内部没有细节，但被轮廓围住，属「洞」）
        → 高斯模糊抹平边界 → 乘「离脸的距离渐开」权重（脸锁死，发梢/衣摆摆得最狠）

渲染：逐像素反向采样（dst 取 src 的 x+dx, y+dy），位移连续 → 不会出现空洞或撕裂缝。
"""
import os
import sys
import math
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

AWR, AHR = 320, 180
OUT_W, OUT_H = 960, 540
SRC = r"D:\Wallpaper\images\biliart-17403571-4.jpg"
OUTDIR = r"D:\Code\AI-CODE\workbuddy_test\wallpaper\tools\out\person"


def load_rgb(path, w, h):
    img = Image.open(path).convert("RGB")
    iw, ih = img.size
    s = max(w / iw, h / ih)
    nw, nh = max(w, int(math.ceil(iw * s))), max(h, int(math.ceil(ih * s)))
    img = img.resize((nw, nh), Image.LANCZOS)
    x0, y0 = (nw - w) // 2, (nh - h) // 2
    return img.crop((x0, y0, x0 + w, y0 + h))


def blur(a, r):
    im = Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8))
    im = im.filter(ImageFilter.BoxBlur(r))
    return np.asarray(im, np.float32) / 255.0


def dilate(mask, r):
    """膨胀：PIL 的 MaxFilter（方形核），r 像素"""
    if r <= 0:
        return mask
    im = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(2 * r + 1))
    return np.asarray(im) > 127


def erode(mask, r):
    if r <= 0:
        return mask
    im = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(2 * r + 1))
    return np.asarray(im) > 127


def ccl_largest(mask):
    h, w = mask.shape
    labels = np.zeros((h, w), np.int32)
    best, best_n, cur = 0, 0, 0
    for y in range(h):
        for x in range(w):
            if not mask[y, x] or labels[y, x]:
                continue
            cur += 1
            q = deque([(y, x)])
            labels[y, x] = cur
            n = 0
            while q:
                cy, cx = q.popleft()
                n += 1
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = cy + dy, cx + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not labels[ny, nx]:
                        labels[ny, nx] = cur
                        q.append((ny, nx))
            if n > best_n:
                best_n, best = n, cur
    return labels == best


def fill_holes(mask, max_ratio=0.02):
    """从四边 flood 出「外部」，剩下的空位是被轮廓围住的洞（光滑的头发/衣服内部）。
    只填面积小于 max_ratio 的洞 —— 被手臂/身体围住的**背景**也是「洞」，
    整块填掉会让半个画面的背景跟着人物一起动（v2 踩过：62% 画面都在动）。"""
    h, w = mask.shape
    outside = np.zeros((h, w), bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True
                q.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True
                q.append((y, x))
    while q:
        cy, cx = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = cy + dy, cx + dx
            if 0 <= ny < h and 0 <= nx < w and not mask[ny, nx] and not outside[ny, nx]:
                outside[ny, nx] = True
                q.append((ny, nx))

    holes = ~outside & ~mask
    labels = np.zeros((h, w), np.int32)
    limit = int(h * w * max_ratio)
    cur = 0
    out = mask.copy()
    for y in range(h):
        for x in range(w):
            if not holes[y, x] or labels[y, x]:
                continue
            cur += 1
            q = deque([(y, x)])
            labels[y, x] = cur
            cells = []
            while q:
                cy, cx = q.popleft()
                cells.append((cy, cx))
                if len(cells) > limit:
                    break
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = cy + dy, cx + dx
                    if 0 <= ny < h and 0 <= nx < w and holes[ny, nx] and not labels[ny, nx]:
                        labels[ny, nx] = cur
                        q.append((ny, nx))
            if len(cells) <= limit:
                for cy, cx in cells:
                    out[cy, cx] = True
    return out


def analyze(path):
    small = np.asarray(load_rgb(path, AWR, AHR), np.float32)
    r, g, b = small[:, :, 0], small[:, :, 1], small[:, :, 2]
    gray = (r * 299 + g * 587 + b * 114) / 1000.0

    gx = np.abs(np.diff(gray, axis=1))
    gy = np.abs(np.diff(gray, axis=0))
    e = np.zeros_like(gray)
    e[:, :-1] += gx
    e[:-1, :] += gy
    e *= 0.5
    # 轻微模糊掉单像素噪声，免得胶片颗粒被当成「细节」
    eb = blur(np.clip(e / 32.0, 0, 1), 1) * 32.0

    med = float(np.median(eb))
    thr = max(3.2, med * 2.4)
    seed = eb > thr
    info = {"edge_med": med, "edge_thr": thr, "seed": float(seed.mean())}

    mask = dilate(seed, 4)
    mask = ccl_largest(mask)
    info["mask0"] = float(mask.mean())
    mask = fill_holes(mask)                 # 只填空洞（发丝缝隙 / 衣服内部）
    info["mask1"] = float(mask.mean())
    mask = erode(mask, 6)                   # 把膨胀时吃进来的背景光晕收回去
    mask = ccl_largest(mask)
    info["mask2"] = float(mask.mean())
    w = blur(mask.astype(np.float32), 3)
    w = np.clip((w - 0.30) / 0.55, 0, 1)

    # 脸部锚点：遮罩内偏上、暖色（肤色）像素重心
    warm = (r > g) & (g > b) & ((r - b) > 12)
    ys, xs = np.where(mask)
    if len(ys) == 0:
        return None, info, small
    top = ys < np.percentile(ys, 55)
    skin = np.zeros_like(mask)
    skin[ys[top], xs[top]] = warm[ys[top], xs[top]]
    if skin.sum() > 60:
        sy, sx = np.where(skin)
    else:
        sy, sx = ys[top], xs[top]
    ax, ay = sx.mean() / AWR, sy.mean() / AHR
    info["anchor"] = (round(ax, 3), round(ay, 3))

    xx = (np.arange(AWR, dtype=np.float32) / AWR)[None, :].repeat(AHR, 0)
    yy = (np.arange(AHR, dtype=np.float32) / AHR)[:, None].repeat(AWR, 1)
    dxn = xx - ax
    dyn = (yy - ay) * 1.78
    dist = np.sqrt(dxn * dxn + dyn * dyn)

    lock = 0.105
    ramp = np.clip((dist - lock) / 0.19, 0, 1)
    ramp = ramp * ramp * (3 - 2 * ramp)

    # 第二道保险：离头部越远，幅度衰减越多。
    # 遮罩万一吃进了背景（人物站在复杂街景里很常见），远处的背景也不会大摆。
    att = np.clip((dist - 0.30) / 0.45, 0, 1)
    att = 1.0 - 0.55 * (att * att * (3 - 2 * att))

    w = np.clip(w * ramp * att, 0, 1)
    info["wmax"] = float(w.max())
    info["wcover"] = float((w > 0.3).mean())
    return w, info, small


def render(small, w, info, t):
    ah, aw = small.shape[:2]
    ax, ay = info["anchor"]
    xx = np.arange(aw, dtype=np.float32)[None, :]
    yy = np.arange(ah, dtype=np.float32)[:, None]
    nx, ny = xx / aw, yy / ah

    # 主波横向：相位随 x/y 变化 → 风从画面扫过，发梢不同步
    dx = (0.95 * np.sin(t * 1.25 - nx * 5.4 - ny * 1.4)
          + 0.42 * np.sin(t * 0.61 + nx * 2.2 - ny * 3.1 + 1.3)) * (aw * 0.013)
    # 纵向弱波：衣摆上下涌，越靠下越明显
    below = np.clip((ny - ay) / max(0.05, 1 - ay), 0, 1)
    dy = (0.55 * np.sin(t * 0.83 - nx * 2.9 - ny * 4.4)) * (ah * 0.006) * below

    dxf = np.broadcast_to(dx * w, (ah, aw))
    dyf = np.broadcast_to(dy * w, (ah, aw))

    sx = np.clip(np.round(np.broadcast_to(xx, (ah, aw)) + dxf), 0, aw - 1).astype(np.int32)
    sy = np.clip(np.round(np.broadcast_to(yy, (ah, aw)) + dyf), 0, ah - 1).astype(np.int32)
    return small[sy, sx]


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    w, info, small = analyze(SRC)
    print("分析：", {k: (round(v, 3) if isinstance(v, float) else v) for k, v in info.items()})
    if w is None:
        print("没识别出主体"); return 1

    img = Image.fromarray(np.clip(w * 255, 0, 255).astype(np.uint8))
    img.resize((OUT_W, OUT_H), Image.NEAREST).save(os.path.join(OUTDIR, "weights.png"))

    base = np.asarray(small, np.float32)
    ov = base.copy()
    ov[:, :, 0] = np.clip(ov[:, :, 0] * (1 - w * 0.45) + 255 * w * 0.45, 0, 255)
    ov[:, :, 1] *= (1 - w * 0.45)
    ov[:, :, 2] *= (1 - w * 0.45)
    Image.fromarray(ov.astype(np.uint8)).resize((OUT_W, OUT_H), Image.LANCZOS).save(
        os.path.join(OUTDIR, "overlay.png"))

    frames = []
    for i, t in enumerate((0.0, 0.7, 1.4, 2.1, 2.8, 3.5)):
        f = render(base, w, info, t)
        im = Image.fromarray(np.clip(f, 0, 255).astype(np.uint8)).resize((OUT_W, OUT_H), Image.LANCZOS)
        im.save(os.path.join(OUTDIR, "t%02d.png" % i)); frames.append(im)
        d = np.abs(f - base).max(axis=2)
        Image.fromarray(np.clip(d * 4, 0, 255).astype(np.uint8)).resize(
            (OUT_W, OUT_H), Image.NEAREST).save(os.path.join(OUTDIR, "d%02d.png" % i))
        print("t=%.1fs  变化>6 的像素 %.1f%%  最大位移区占比 %.1f%%"
              % (t, 100.0 * (d > 6).mean(), 100.0 * (w > 0.5).mean()))

    frames[0].save(os.path.join(OUTDIR, "preview.gif"), save_all=True,
                   append_images=frames[1:], duration=140, loop=0)
    return 0


if __name__ == "__main__":
    sys.exit(main())
