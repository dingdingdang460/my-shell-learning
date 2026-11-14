# -*- coding: utf-8 -*-
"""
人物图「局部运动」原型 v3 —— 纯算法、零依赖（无模型 / 无 OpenCV）。

演进过程（每一步都是实机/实图上踩出来的）：
  v1 按亮度找主体 → 这张图背景也是亮虚化，整幅被选中（79%）
  v2 改梯度能量找主体 → 动漫插画好使，但**城市/风景**也会命中（13/14 张假阳性）
  v3 三条修正：
     1) 选连通域不再只看「最大」，改成**居中加权**：城市图的团块常常贴在边上，
        人物图的主体基本都在中间。Obama 那张原来选中了左边的国旗（重心 0.90）。
     2) 脸不再靠肤色（这张图整幅粉调，暖色覆盖 94%），改成**人物团块里最光滑的大块**，
        并据此决定模糊锁定椭圆的大小 —— 脸多大，锁多大。
     3) 摆动范围改成**以脸为中心的环形气泡**：脸内位移 0，往外到 2.6 个脸半径到满幅，
        再往外衰减。这样即使团块把背景吃进来一点，远处也不会大摆。
     4) 找不到脸就**明确拒绝**，退回场景动效，绝不硬上（剪影/风景图会走到这里）。

这套输出直接对应 C# 的 app/PlayerPerson.cs。
"""
import os
import sys
import math
from collections import deque

import numpy as np
from PIL import Image, ImageFilter

AWR, AHR = 320, 180
OUT_W, OUT_H = 960, 540
OUTDIR = r"D:\Code\AI-CODE\workbuddy_test\wallpaper\tools\out\person\v3"


# ---------------------------------------------------------------- 图像与算子

def load_rgb(path, w, h):
    img = Image.open(path).convert("RGB")
    iw, ih = img.size
    s = max(w / iw, h / ih)
    nw, nh = max(w, int(math.ceil(iw * s))), max(h, int(math.ceil(ih * s)))
    img = img.resize((nw, nh), Image.LANCZOS)
    x0, y0 = (nw - w) // 2, (nh - h) // 2
    return img.crop((x0, y0, x0 + w, y0 + h))


def blur(a, r):
    if r <= 0:
        return a
    im = Image.fromarray(np.clip(a * 255, 0, 255).astype(np.uint8)).filter(ImageFilter.BoxBlur(r))
    return np.asarray(im, np.float32) / 255.0


def dilate(m, r):
    if r <= 0:
        return m
    im = Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.MaxFilter(2 * r + 1))
    return np.asarray(im) > 127


def erode(m, r):
    if r <= 0:
        return m
    im = Image.fromarray((m * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(2 * r + 1))
    return np.asarray(im) > 127


def components(mask, minpx=120):
    """返回 (size, mask, cx, cy, x0, x1, y0, y1)"""
    h, w = mask.shape
    lab = np.zeros((h, w), np.int32)
    out = []
    cur = 0
    for y in range(h):
        for x in range(w):
            if not mask[y, x] or lab[y, x]:
                continue
            cur += 1
            q = deque([(y, x)])
            lab[y, x] = cur
            n = 0
            x0 = x1 = x
            y0 = y1 = y
            while q:
                cy, cx = q.popleft()
                n += 1
                x0 = min(x0, cx); x1 = max(x1, cx)
                y0 = min(y0, cy); y1 = max(y1, cy)
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = cy + dy, cx + dx
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not lab[ny, nx]:
                        lab[ny, nx] = cur
                        q.append((ny, nx))
            if n >= minpx:
                out.append((n, lab == cur, (x0 + x1) / 2 / w, (y0 + y1) / 2 / h, x0, x1, y0, y1))
    out.sort(key=lambda c: -c[0])
    return out


def pick_center(mask, min_area=0.03, max_area=0.80):
    """居中加权挑主体：面积大、重心靠中间、别贴着画面下沿"""
    best = None
    for size, m, cx, cy, x0, x1, y0, y1 in components(mask):
        area = size / (AWR * AHR)
        if area < min_area or area > max_area:
            continue
        score = area * (1.0 - 0.85 * abs(cx - 0.50)) * (1.0 - 0.55 * max(0.0, cy - 0.78))
        if best is None or score > best[0]:
            best = (score, m, cx, cy, x0, x1, y0, y1, area)
    return best


def fill_small_holes(mask, max_ratio=0.02):
    h, w = mask.shape
    outside = np.zeros((h, w), bool)
    q = deque()
    for x in range(w):
        for y in (0, h - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True; q.append((y, x))
    for y in range(h):
        for x in (0, w - 1):
            if not mask[y, x] and not outside[y, x]:
                outside[y, x] = True; q.append((y, x))
    while q:
        cy, cx = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = cy + dy, cx + dx
            if 0 <= ny < h and 0 <= nx < w and not mask[ny, nx] and not outside[ny, nx]:
                outside[ny, nx] = True; q.append((ny, nx))
    holes = ~outside & ~mask
    limit = int(h * w * max_ratio)
    out = mask.copy()
    seen = np.zeros((h, w), bool)
    for y in range(h):
        for x in range(w):
            if not holes[y, x] or seen[y, x]:
                continue
            q = deque([(y, x)]); seen[y, x] = True; cells = []
            over = False
            while q:
                cy, cx = q.popleft()
                cells.append((cy, cx))
                if len(cells) > limit:
                    over = True; break
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = cy + dy, cx + dx
                    if 0 <= ny < h and 0 <= nx < w and holes[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True; q.append((ny, nx))
            if not over:
                for cy, cx in cells:
                    out[cy, cx] = True
    return out


# ---------------------------------------------------------------- 分析

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
    eb = blur(np.clip(e / 32.0, 0, 1), 1) * 32.0

    med = float(np.median(eb))
    thr = max(3.2, med * 2.4)
    info = {"edge_med": round(med, 2), "thr": round(thr, 2)}

    m = dilate(eb > thr, 4)
    b1 = pick_center(m)
    if b1 is None:
        info["verdict"] = "没找到前景团块"
        return None, info
    m = b1[1]
    m = fill_small_holes(m)
    m = erode(m, 6)
    b2 = pick_center(m)
    if b2 is None:
        info["verdict"] = "腐蚀后团块消失"
        return None, info
    m = b2[1]
    cov = float(m.mean())
    info["mask"] = round(cov, 3)

    # 脸：团块上半部里最光滑的大块
    ys = np.where(m.any(axis=1))[0]
    ylo, yhi = int(ys.min()), int(ys.max())
    upper = m.copy()
    upper[yhi - int((yhi - ylo) * 0.35):, :] = False
    smooth = upper & (eb < med * 0.9)
    fc = components(smooth, minpx=60)
    if not fc:
        info["verdict"] = "团块里找不到脸（纹理太碎）"
        return None, info
    face = fc[0]
    fsize, fm, _, _, fx0, fx1, fy0, fy1 = face
    if fsize < AWR * AHR * 0.004:
        info["verdict"] = "脸太小（%.2f%%）" % (100 * fsize / (AWR * AHR))
        return None, info
    ax = (fx0 + fx1) / 2 / AWR
    ay = (fy0 + fy1) / 2 / AHR
    rw = (fx1 - fx0 + 1) / AWR * 0.75
    rh = (fy1 - fy0 + 1) / AHR * 0.75
    rw = max(rw, 0.05); rh = max(rh, 0.06)
    info.update({"face": (round(ax, 3), round(ay, 3)), "face_w": round(rw, 3), "face_h": round(rh, 3)})

    # 权重：脸内 0，往外 2.6 个脸半径到满幅，3.6 之后衰减，6 之外归零
    xx = (np.arange(AWR, dtype=np.float32) / (AWR - 1))[None, :].repeat(AHR, 0)
    yy = (np.arange(AHR, dtype=np.float32) / (AHR - 1))[:, None].repeat(AWR, 1)
    d = np.sqrt(((xx - ax) / rw) ** 2 + ((yy - ay) / rh) ** 2)

    ramp = np.clip((d - 1.0) / 1.6, 0, 1)
    ramp = ramp * ramp * (3 - 2 * ramp)
    att = np.clip((d - 3.6) / 2.4, 0, 1)
    att = 1.0 - att * att * (3 - 2 * att)

    # 团块可信 → 再乘上团块（把气泡里的背景挖掉）；
    # 团块本身就是「脸」（照片常见：只有脸有细节）→ 只用气泡
    use_mask = cov >= 0.15
    info["use_mask"] = bool(use_mask)
    w = ramp * att
    if use_mask:
        w = w * m
    w = blur(w, 3)
    info["cover"] = round(float((w > 0.3).mean()), 3)
    info["verdict"] = "OK"
    return w, info


def render(small, w, info, t, ax, ay, amp=0.009):
    ah, aw = small.shape[:2]
    xx = np.arange(aw, dtype=np.float32)[None, :]
    yy = np.arange(ah, dtype=np.float32)[:, None]
    nx, ny = xx / (aw - 1), yy / (ah - 1)
    dx = (0.95 * np.sin(t * 1.25 - nx * 5.4 - ny * 1.4)
          + 0.42 * np.sin(t * 0.61 - ny * 3.1 + 1.3)) * (aw * amp)
    below = np.clip((ny - ay) / max(0.05, 1 - ay), 0, 1)
    dy = (0.55 * np.sin(t * 0.83 - ny * 4.4)) * (ah * amp * 0.4) * below
    sx = np.clip(np.round(np.broadcast_to(xx, (ah, aw)) + dx * w), 0, aw - 1).astype(np.int32)
    sy = np.clip(np.round(np.broadcast_to(yy, (ah, aw)) + dy * w), 0, ah - 1).astype(np.int32)
    return small[sy, sx]


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    samples = [
        (r"D:\Wallpaper\images\biliart-17403571-4.jpg", "anime"),
        (os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", "person", "samples", "obama.jpg"), "photo"),
        (r"D:\Wallpaper\images\edgewalker.jpg", "city"),
        (r"D:\Wallpaper\images\urban-calm-cat.jpg", "room"),
    ]
    for path, tag in samples:
        w, info = analyze(path)
        print("%-6s %s" % (tag, info))
        if w is None:
            continue
        small = np.asarray(load_rgb(path, AWR, AHR), np.float32)
        ov = small.astype(np.uint8).copy()
        ww = w.astype(np.float32)
        ov[:, :, 0] = np.clip(ov[:, :, 0] * (1 - ww * 0.55) + 255 * ww * 0.55, 0, 255)
        ov[:, :, 1] = (ov[:, :, 1] * (1 - ww * 0.55)).astype(np.uint8)
        ov[:, :, 2] = (ov[:, :, 2] * (1 - ww * 0.55)).astype(np.uint8)
        Image.fromarray(ov).resize((OUT_W, OUT_H), Image.LANCZOS).save(
            os.path.join(OUTDIR, "%s-mask.png" % tag))
        ax, ay = info["face"]
        for i, t in enumerate((0.0, 2.0, 4.0)):
            f = render(small, w, info, t, ax, ay)
            Image.fromarray(np.clip(f, 0, 255).astype(np.uint8)).resize(
                (OUT_W, OUT_H), Image.LANCZOS).save(os.path.join(OUTDIR, "%s-t%d.png" % (tag, i)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
