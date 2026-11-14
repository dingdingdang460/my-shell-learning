# -*- coding: utf-8 -*-
"""
gen-motion.py —— 壁纸「动起来」的出片引擎（本地、零额度）v3

v3 相对 v2 的改动，起因是主人反馈「动态像打了马赛克、一点都不自然」：
v2 每帧把整幅原图按亚像素位移场重采样，实测 0.25~0.5px 位移会损失
14~19% 的高频能量，而位移随时间在 0↔1px 之间摆动，星点/云絮/草叶
就一帧清一帧糊地闪。v3 改成：

    ① 高频锁定  只对低通图做形变，把「低频变化量」加回原图。
                 结构细节逐像素来自原图，永不重采样 → 不再糊、不再闪。
    ② 内容演化  多尺度行波 + 原地脉动，让云/雾/水面的明暗自己流动、
                 翻卷、生成消散；不是把像素从别处搬过来。
    ③ 连续场    位移场做小网格平滑，区域拼接处不留折线；遮罩只乘在
                 变化量上，不乘进位移场。
    ④ 柔化粒子  雨丝过 0.6px 高斯，从「划痕」变成半透明水丝。
    ⑤ 编码      crf 14 / slow / tune animation，压掉 8x8 宏块。

按区域叠的运动仍在：天空飘移、水面涌动、雨丝下落、夜景灯起伏、
主体视差、兜底微动。三条硬约束不变：

  1. 首尾同帧。所有时间函数取 sin(2πkt/T)，k 为整数，t=T 时回到起点；
     雨丝用 (y + speed*t) mod H，且让 speed*T 是周期的整数倍。
  2. 不能有整体平移感。位移场是「按行 + 按区域」的，不是全图同向。
  3. 不能有伪影。锐利结构（鸟居、栏杆、电线）由软区域权重挡住，
     不会被云拖着走、像贴纸被撕开。

注意边界：这是程序化动效，能做出「画面活着」，但**生成不了新语义内容**
（头发真正飘起、人物动作、云的形态改变）。那种效果要走 VideoGen 一类
图生视频模型，见 docs/PROGRESS.md 第十三节之前的「云端出片实战」。

用法：
    python gen-motion.py --image a.jpg --out a.mp4 [--seconds 3] [--fps 30]
    python gen-motion.py --image a.jpg --probe out_prefix   # 只吐几张关键帧 PNG
"""

import argparse
import math
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageFilter, ImageDraw


# ---------------------------------------------------------------- 基础

def load_cover(path, w, h):
    """按 cover 语义缩放并居中裁切，返回 float32 [0,1] RGB"""
    im = Image.open(path)
    im = im.convert("RGB")
    sw, sh = im.size
    k = max(w / float(sw), h / float(sh))
    nw, nh = int(round(sw * k)), int(round(sh * k))
    im = im.resize((nw, nh), Image.LANCZOS)
    x, y = (nw - w) // 2, (nh - h) // 2
    return np.asarray(im.crop((x, y, x + w, y + h))).astype(np.float32) / 255.0


def blur(arr, radius):
    """对 2D float 数组做快速近似高斯（PIL 的 GaussianBlur 够快也够软）"""
    if radius <= 0:
        return arr.astype(np.float32)
    a = np.clip(arr * 255.0, 0, 255).astype(np.uint8)
    im = Image.fromarray(a).filter(ImageFilter.GaussianBlur(radius))
    return np.asarray(im).astype(np.float32) / 255.0


def x_shift(img, dx_rows):
    """
    按行做横向位移。dx_rows 可以是标量，也可以是长度 = h 的数组。
    越界用镜像补齐（reflect）。不用 wrap：画面左边的东西卷到右边一眼假。
    """
    h, w, _ = img.shape
    dx = np.asarray(dx_rows, dtype=np.float32).reshape(-1)
    if dx.size == 1:
        dx = np.full(h, float(dx[0]), dtype=np.float32)

    pad = int(np.abs(dx).max()) + 2
    big = np.pad(img, ((0, 0), (pad, pad), (0, 0)), mode="reflect")
    cols = np.arange(w, dtype=np.int32)[None, :] + pad - dx.astype(np.int32)[:, None]
    rows = np.arange(h, dtype=np.int32)[:, None]
    return big[rows, cols]


_GRID = {}


def _grid(h, w):
    """坐标网格只建一次：每帧重建 mgrid 在 1080p 上要多花几十毫秒"""
    key = (h, w)
    g = _GRID.get(key)
    if g is None:
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        g = (yy, xx)
        _GRID[key] = g
    return g


_WARP_BUF = {}
_WARP_SRC = {}


def _warp_buf(h, w):
    """四个采样结果复用常驻缓冲；用 uint8 存，gather 的内存流量只有 float32 的 1/4。"""
    key = (h, w)
    b = _WARP_BUF.get(key)
    if b is None:
        shape = (h * w, 3)
        b = tuple(np.empty(shape, dtype=np.uint8) for _ in range(4))
        _WARP_BUF[key] = b
    return b


def _warp_src(img):
    """
    每帧 warp 的源都是同一张原图（render 从原图起算，不是链式叠帧），
    所以量化成 uint8 只做一次。插值结果仍按 float32 加权，最后除以 255
    回到 [0,1]，量化误差 ≤0.5/255，成片是 8bit，看不出差别。
    """
    key = id(img)
    c = _WARP_SRC.get(key)
    if c is None or c[0] is not img:
        u8 = (np.clip(img, 0, 1) * 255.0 + 0.5).astype(np.uint8)
        c = (img, u8.reshape(-1, 3))
        _WARP_SRC.clear()          # 只留当前这张，换图不累积
        _WARP_SRC[key] = c
    return c[1]


def warp(img, dx, dy):
    """
    二维亚像素形变：每个像素按它自己的 (dx,dy) 去取样。

    这是「不像整体平移」的关键 —— 位移场是空间变化的（有的地方多、有的地方少、
    方向还不同），画面就会像在蠕动/流动，而不是被整体拖走。

    1080p 一帧要取 200 多万个点，中间数组的分配比算术本身还贵：
    floor / clip / 乘加全部就地完成，四个角点采样写进常驻缓冲。
    """
    h, w, _ = img.shape
    yy, xx = _grid(h, w)

    sx = xx + dx
    x0 = np.floor(sx)
    fx = (sx - x0).reshape(-1, 1)
    sy = yy + dy
    y0 = np.floor(sy)
    fy = (sy - y0).reshape(-1, 1)

    # 就地裁剪 + 就地算扁平索引，省掉 clip / astype / 乘加产生的三份全幅浮点数组
    np.clip(x0, 0, w - 2, out=x0)
    np.clip(y0, 0, h - 2, out=y0)
    np.multiply(y0, w, out=y0)
    y0 += x0
    idx = y0.astype(np.int32).ravel()

    flat = _warp_src(img)
    a, b, c, d = _warp_buf(h, w)
    np.take(flat, idx, axis=0, out=a)
    np.take(flat, idx + 1, axis=0, out=b)
    np.take(flat, idx + w, axis=0, out=c)
    np.take(flat, idx + w + 1, axis=0, out=d)

    # 权重只有 (N,1)，广播到三通道；结果单独分配，不与复用缓冲产生别名
    shape = (h * w, 3)
    res = np.empty(shape, dtype=np.float32)
    tmp = np.empty(shape, dtype=np.float32)
    np.multiply(a, (1 - fx) * (1 - fy), out=res)
    np.multiply(b, fx * (1 - fy), out=tmp)
    res += tmp
    np.multiply(c, (1 - fx) * fy, out=tmp)
    res += tmp
    np.multiply(d, fx * fy, out=tmp)
    res += tmp
    np.multiply(res, 1.0 / 255.0, out=res)
    return res.reshape(h, w, 3)


def y_shift(img, dy_rows):
    h, w, _ = img.shape
    idx = np.clip(np.arange(h) + np.asarray(dy_rows, dtype=np.float32).astype(np.int32), 0, h - 1)
    return img[idx]


def feather(y0, y1, soft, h):
    """上下方向的羽化权重，避免「天空和地面」之间出现一条硬分界"""
    y = np.arange(h, dtype=np.float32)
    m = np.clip((y - (y0 - soft)) / max(1e-3, soft), 0, 1) * \
        np.clip(((y1 + soft) - y) / max(1e-3, soft), 0, 1)
    return m[:, None].astype(np.float32)


_UP = {}


def _up_coeff(gh, gw, h, w):
    """双线性上采样的坐标系数只跟尺寸有关，按尺寸缓存，不每帧重算。"""
    key = (gh, gw, h, w)
    c = _UP.get(key)
    if c is None:
        # 与 PIL BILINEAR 用同一套中心对齐坐标，替换后画面不会整体偏半格
        xx = np.clip((np.arange(w, dtype=np.float32) + 0.5) * (gw / float(w)) - 0.5,
                     0, gw - 1)
        x0 = np.floor(xx).astype(np.int32)
        x1 = np.minimum(x0 + 1, gw - 1)
        fx = (xx - x0).astype(np.float32)[None, :]
        yy = np.clip((np.arange(h, dtype=np.float32) + 0.5) * (gh / float(h)) - 0.5,
                     0, gh - 1)
        y0 = np.floor(yy).astype(np.int32)
        y1 = np.minimum(y0 + 1, gh - 1)
        fy = (yy - y0).astype(np.float32)[:, None]
        c = (x0, x1, fx, y0, y1, fy)
        _UP[key] = c
    return c


def upsample(n, h, w):
    """
    低频小场 → 全幅双线性放大，纯 numpy、系数可复用。

    PIL 对 mode="F" 的 resize 在 1080p 上单次就要几十毫秒，而每帧要放
    大 6 张场（四个位移场 + 光影 + 灯光），是仅次于 warp 的开销。
    这里先在低分辨率行上做 x 插值，再做 y 插值，只碰两次全幅数据。
    """
    gh, gw = n.shape
    if gh == h and gw == w:
        return n
    x0, x1, fx, y0, y1, fy = _up_coeff(gh, gw, h, w)
    a = n[:, x0] * (1.0 - fx) + n[:, x1] * fx
    return (a[y0] * (1.0 - fy) + a[y1] * fy).astype(np.float32)


def flow_field(h, w, t, T, amp, sx=180.0, sy=260.0, k=1):
    """
    低频形变场：两个不同尺度的正弦相交，云缘会慢慢起伏而不是刚体平移。
    t 用 sin(2πk t/T) 封口，t=T 时回到 0。
    """
    # 在 1/12 的网格上算，再双线性放大：位移场只有几个像素的起伏，低分辨率完全够
    gh = max(4, h // 12)
    gw = max(4, w // 12)
    yy = (np.arange(gh, dtype=np.float32) * (h / float(gh)))[:, None]
    xx = (np.arange(gw, dtype=np.float32) * (w / float(gw)))[None, :]
    ph = 2.0 * math.pi * k * t / T
    # 所有相位都必须是 ph 的整数倍：ph = 2πk·t/T，t=T 时 ph=2πk 才回到起点。
    # 原来这里写的是 -ph*0.6（fx_subject 里还有 ph*1.6），t=T 回不到 0，
    # 循环点会留下一个台阶 —— 实测成片首尾只有 36.5dB。别再改回小数倍。
    n = (np.sin(xx / sx + ph) * np.sin(yy / sy - ph)
         + 0.45 * np.sin((xx * 0.7 + yy * 1.3) / (sx * 0.5) + ph * 2.0))
    return upsample((n * amp).astype(np.float32), h, w)


def edge_mask(img, soft=18.0):
    """
    「可以动」的权重：平坦处 =1，锐利边缘 =0。
    云会动，但鸟居/电线的边缘不动 —— 不然整根柱子被云拖着平移。
    """
    g = (0.299 * img[..., 0] + 0.587 * img[..., 1] + 0.114 * img[..., 2])
    gx = np.abs(np.diff(g, axis=1, prepend=g[:, :1]))
    gy = np.abs(np.diff(g, axis=0, prepend=g[:1, :]))
    e = np.clip((gx + gy) * 4.0, 0, 1)
    e = blur(e, max(1.0, soft * 0.35))
    return np.clip(1.0 - e * 2.2, 0, 1).astype(np.float32)


# ---------------------------------------------------------------- 画面分析

# 雨景语义线索。库里的 id 本来就是语义化 slug（sleepy-rainy-evening、
# rainy-cyberpunk-streets），比任何像素阈值都可靠。
# 只用「雨」这一类词：storm / mist / 雾 不算，暴风海崖、云海起雾都不该凭空下雨。
RAIN_WORDS = ("rain", "drizzle", "雨")


def rain_hint(name):
    """确定性纯函数：文件名/工单 id 是否明示雨景。"""
    n = os.path.basename(name or "").lower()
    return any(word in n for word in RAIN_WORDS)


def analyze(img, hint=""):
    h, w, _ = img.shape
    g = (0.299 * img[..., 0] + 0.587 * img[..., 1] + 0.114 * img[..., 2])

    # 缩略图找地平线：行方向水平边缘能量最大处
    sw, sh = 320, max(2, int(320.0 * h / w))
    small = np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((sw, sh), Image.BILINEAR)
                       ).astype(np.float32) / 255.0
    e = np.abs(np.diff(small, axis=0)).mean(axis=1)
    lo, hi = int(len(e) * 0.30), int(len(e) * 0.90)
    band = e[lo:hi]
    horizon = None
    if band.size:
        k = int(np.argmax(band))
        if band[k] > max(1e-6, float(np.median(e))) * 1.30:
            horizon = (lo + k) / float(len(e))

    mean_lum = float(g.mean())
    bright = float((g > 0.72).mean())

    # 水面：地平线以下要同时满足三条，缺一条就判成「不是水」——
    # 判错的代价比不判大得多：把城市天际线当成水面去做涌动，整片楼会游起来。
    #   a) 偏蓝/青（暖色灯光多的夜景会在这里被挡掉）
    #   b) 左右方向的梯度明显小于上下方向（水面是横着连续、竖着分层）
    #   c) 没有成片的锐利暗块（建筑立面）
    water = False
    water_dbg = "-"
    if horizon is not None and horizon < 0.94:
        below = img[int(h * horizon) + 6:, :, :]
        if below.size > 1000:
            br, bg_, bb = below[..., 0].mean(), below[..., 1].mean(), below[..., 2].mean()
            lum_b = (0.299 * below[..., 0] + 0.587 * below[..., 1] + 0.114 * below[..., 2])
            gx_b = float(np.abs(np.diff(lum_b, axis=1)).mean())
            gy_b = float(np.abs(np.diff(lum_b, axis=0)).mean())
            smooth_h = gx_b < gy_b * 0.95
            # 建筑立面 / 窗格 / 栏杆会留下大量强竖直边缘；水面几乎没有。
            # 这一条专门用来挡「夜景城市」被当成海的情况。
            gx_map = np.abs(np.diff(lum_b, axis=1))
            vert = float((gx_map > 0.07).mean())
            bright_b = float((lum_b > 0.72).mean())
            sb = np.asarray(Image.fromarray(np.clip(lum_b * 255, 0, 255).astype(np.uint8))
                            .resize((160, 90), Image.BILINEAR)).astype(np.float32) / 255.0
            row_sim = float(np.abs(np.diff(sb, axis=0)).mean())
            water = (bb > br * 1.04) and smooth_h and vert < 0.05 \
                and bright_b < 0.05 and float(lum_b.mean()) > 0.10
            water_dbg = "blue=%.2f gx/gy=%.2f vert=%.3f bright=%.3f row=%.4f" % (
                bb / max(1e-6, br), gx_b / max(1e-6, gy_b), vert, bright_b, row_sim)

    # 天空：地平线以上，亮度中等以上
    sky = False
    if horizon is not None and horizon > 0.10:
        above = g[: int(h * horizon), :]
        sky = float(above.mean()) > 0.26

    # 雨丝：细长的竖向条纹 → 水平梯度远大于垂直梯度。
    #
    # 不能只靠放宽 streak 阈值：全库实测，动漫线稿（1.23 / 1.13）、
    # 浮空城（1.13）都比真雨景（1.09~1.10）还高，一放宽就有 5 张非雨图
    # 凭空下雨。所以走两条通道：
    #   a) 纯视觉强证据：streak > 1.35（老规则，保留）
    #   b) 语义线索 + 视觉佐证：id 里写明雨，且画面确有细密竖向纹理、整体偏暗
    # 语义线索只降低「需要多强的视觉证据」，不单独决定下雨，避免 id 与画面不符时乱加雨丝。
    gx = np.abs(np.diff(small, axis=1))
    gy = np.abs(np.diff(small, axis=0))[:, :gx.shape[1]]
    streak = float((gx > 0.05).mean() / ((gy > 0.05).mean() + 1e-6))
    named_rain = rain_hint(hint)
    rainy = ((streak > 1.35 and mean_lum < 0.58)
             or (named_rain and streak > 0.80 and mean_lum < 0.62))

    # 夜景：暗 + 成片的小亮点（城市灯）。只看「暗」不够，
    # 黎明的云海也暗，但它没有密集亮点，不该让雾气闪。
    night = mean_lum < 0.55 and bright > 0.003 and streak < 1.60

    # 主体：暖色、集中在中部、而且不是水面
    r, gg, b = img[..., 0], img[..., 1], img[..., 2]
    warm = ((r > gg) & (gg > b) & (r - b > 0.06) & (g > 0.30)).astype(np.float32)
    ws, hs = 80, max(2, int(80.0 * h / w))
    warm_small = np.asarray(Image.fromarray((warm * 255).astype(np.uint8)).resize((ws, hs), Image.BILINEAR)
                            ).astype(np.float32) / 255.0
    cy0, cy1 = int(0.15 * hs), int(0.95 * hs)
    cx0, cx1 = int(0.18 * ws), int(0.82 * ws)
    center_warm = float(warm_small[cy0:cy1, cx0:cx1].mean())
    subject = center_warm > 0.42 and not water

    return dict(horizon=horizon, sky=sky, water=water, rainy=rainy,
                night=night, subject=subject, mean_lum=mean_lum, bright=bright,
                streak=streak, center_warm=center_warm, water_dbg=water_dbg,
                named_rain=named_rain)


def subject_mask(img):
    """人物/焦点遮罩：暖色 + 中部集中，再模糊成大块。置信度低时返回 None。"""
    h, w, _ = img.shape
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    g_ = 0.299 * r + 0.587 * g + 0.114 * b
    m = ((r > g) & (g > b) & (r - b > 0.05) & (g_ > 0.22)).astype(np.float32)

    sw, sh = max(8, w // 10), max(8, h // 10)
    small = np.asarray(Image.fromarray((m * 255).astype(np.uint8)).resize((sw, sh), Image.BILINEAR)
                       ).astype(np.float32) / 255.0
    # 形态学闭运算（膨胀+腐蚀），把零碎的暖色块连成一片
    def dil(a, r_):
        p = np.pad(a, r_, mode="edge")
        acc = np.zeros_like(a)
        for dy in range(-r_, r_ + 1):
            for dx in range(-r_, r_ + 1):
                acc = np.maximum(acc, p[r_ + dy:r_ + dy + a.shape[0], r_ + dx:r_ + dx + a.shape[1]])
        return acc

    def ero(a, r_):
        p = np.pad(a, r_, mode="edge")
        acc = np.ones_like(a)
        for dy in range(-r_, r_ + 1):
            for dx in range(-r_, r_ + 1):
                acc = np.minimum(acc, p[r_ + dy:r_ + dy + a.shape[0], r_ + dx:r_ + dx + a.shape[1]])
        return acc

    small = ero(dil(small, 2), 2)
    cover = float((small > 0.5).mean())
    # 太小说明没找到人，太大说明整张图都是暖色（夕照、沙漠）—— 两种都不能拿来做遮罩
    if cover < 0.02 or cover > 0.45:
        return None, cover

    big = np.asarray(Image.fromarray((small * 255).astype(np.uint8)).resize((w, h), Image.BILINEAR)
                     ).astype(np.float32) / 255.0
    big = blur(big, max(2.0, min(w, h) * 0.012))
    return big[..., None], cover


def _dilate2(a, r):
    """二维膨胀（取邻域最大）：用来把「细节区」往外扩一圈"""
    if r <= 0:
        return a
    p = np.pad(a, r, mode="edge")
    out = np.zeros_like(a)
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            out = np.maximum(out, p[r + dy:r + dy + a.shape[0], r + dx:r + dx + a.shape[1]])
    return out


def smooth_mask(region, radius=None, strength=5.0):
    """
    「这块地方能不能位移」的权重：局部平坦 =1，细节多 =0。

    这是整套效果里最关键的一道保险。与其去猜「这是海还是城市」（猜错一次
    整栋楼就游起来了），不如按像素看「这里平不平」：平坦的地方动，锐利的地方
    不动，中间用羽化过渡。云海、海面、雾气、暗部都很平 → 会动；
    建筑、栏杆、草叶、人脸细节 → 不动。
    """
    h, w = region.shape[:2]
    if radius is None:
        radius = max(2.0, min(w, h) * 0.008)

    lum = 0.299 * region[..., 0] + 0.587 * region[..., 1] + 0.114 * region[..., 2]
    sm = blur(lum, radius * 1.6)
    detail = np.clip(np.abs(lum - sm) * strength, 0.0, 1.0)
    # 细节要连同周围一圈一起排除：只挡细节本身的话，插值仍会把边缘拉出重影
    detail = _dilate2(detail, 2)
    m = 1.0 - detail
    m = blur(m, max(1.0, radius * 0.6))   # 轻微羽化，避免「动/不动」之间出现硬棱
    return m.astype(np.float32)


def build_masks(img, info):
    """所有静态遮罩只算一次（每帧重算既慢又没必要）"""
    h, w, _ = img.shape
    masks = {"sky": None, "sea": None, "breathe": None, "soft": soft_region(img)}

    # 中部呼吸层：不靠「暖色找主体」（蓝调/暗光人像的遮罩覆盖率只有 1~2%，判不出人）。
    # 换成「中部椭圆 × 局部平坦度」：衣服、被褥、雾气这类平坦处轻轻起伏，
    # 人脸、发丝、电线这些细节由 smooth_mask 挡住。它只是一张静态权重图，
    # 每帧乘一个标量正弦即可，不额外增加一次 warp。
    yy, xx = _grid(h, w)
    band = np.exp(-(((xx / w - 0.5) ** 2) / 0.18
                    + ((yy / h - 0.60) ** 2) / 0.22))
    masks["breathe"] = (band * smooth_mask(img, strength=6.0) * 0.85).astype(np.float32)

    if info["sky"] and info["horizon"]:
        hz = max(8, int(h * info["horizon"]))
        sky = img[:hz]
        em = edge_mask(sky, soft=max(6.0, min(w, h) * 0.02))
        m = blur(em, max(3.0, min(w, h) * 0.02)) * smooth_mask(sky)
        masks["sky"] = (m * feather(-9999, hz, h * 0.10, hz))[..., None].astype(np.float32)

    if info["water"]:
        hz = max(0, min(int(h * (info["horizon"] or 0.60)), h - 4))
        # 只动「地平线附近」这一条带：那是远景，模糊、容错高。
        # 近景（草叶、栏杆、被褥）一律不动 —— 那些地方一动就是双影。
        sea = img[hz: hz + max(8, int(h * 0.22))]
        rh = sea.shape[0]
        m = smooth_mask(sea, strength=12.0) * feather(0, rh, max(4.0, rh * 0.14), rh)
        masks["sea"] = (hz, m[..., None].astype(np.float32))

    return masks


# ------------------------------------------------- 高频锁定 / 内容演化（v3）

_LOW = {}


def lowpass(img, radius=2.0):
    """只给位移场用的低通版本。高频细节永远从原图取，绝不参与重采样。"""
    im = Image.fromarray((np.clip(img, 0, 1) * 255.0 + 0.5).astype(np.uint8))
    im = im.filter(ImageFilter.GaussianBlur(radius))
    return np.asarray(im).astype(np.float32) / 255.0


def _low_cached(img, radius=2.0):
    """低通图每帧都一样，缓存住，warp 的 uint8 源也就能一直命中。"""
    key = id(img)
    v = _LOW.get(key)
    if v is None or v[0] is not img:
        v = (img, lowpass(img, radius))
        _LOW.clear()
        _LOW[key] = v
    return v[1]


def blur_field(a, radius=10.0):
    """
    位移场专用的大幅平滑：缩到小网格再双线性放大，保持空间连续（C0）。

    不能用 blur()：它把数组量化到 uint8，位移场有负值、幅度也远超 1，
    一 clip 就全毁。区域拼接处的折线，靠这一步抹平。
    """
    h, w = a.shape
    step = max(2.0, float(radius))
    gh, gw = max(4, int(h / step)), max(4, int(w / step))
    small = np.asarray(
        Image.fromarray(np.ascontiguousarray(a, dtype=np.float32), mode="F")
        .resize((gw, gh), Image.BILINEAR), dtype=np.float32)
    return upsample(small, h, w)


def soft_region(img):
    """
    区域级「能不能动」权重：平坦处 1、锐利结构 0，再做一次大幅平滑。

    这张权重只乘在「低频变化量」和「亮度演化」上 —— 不乘进位移场。
    上一版把逐像素遮罩乘进位移场，再对整幅原图做亚像素重采样，
    实测 0.25~0.5px 位移会吃掉 14~19% 的高频能量，位移随时间在
    0↔1px 之间摆动，星点/云絮/草叶就一帧清一帧糊地闪 —— 那就是
    主人看到的「像打了马赛克」。v3 从根上不再重采样原图。
    """
    h, w = img.shape[:2]
    m = smooth_mask(img, strength=5.0)
    m = blur(m, max(8.0, min(w, h) * 0.014))
    return np.clip(m * 1.25, 0, 1).astype(np.float32)


def evolution_field(h, w, t, T):
    """
    内容演化场：多个尺度的行波 + 原地脉动叠加。

    行波 sin(kx·x + ky·y - k·ph) 让明暗图案沿各自方向缓慢流动（像云在飘、
    雾在走），脉动项 cos(ph)·sin(...) 让密度在原地生成/消散。两者都不搬动
    任何像素 —— 画面是「长出来」在动，而不是「被拖着」在动。

    所有时间项都是 ph=2πt/T 的整数倍，t=T 严格回到起点。
    """
    ph = 2.0 * math.pi * t / T
    gh, gw = max(8, h // 4), max(8, w // 4)
    yy = (np.arange(gh, dtype=np.float32) * (h / float(gh)))[:, None]
    xx = (np.arange(gw, dtype=np.float32) * (w / float(gw)))[None, :]
    # (kx, ky, 时间谐波 k, 相位, 权重)；k=0 表示原地脉动
    waves = (
        (0.0090, 0.0030, 1, 0.0, 0.30),
        (0.0210, -0.0060, 1, 1.7, 0.24),
        (0.0430, 0.0110, 2, 0.6, 0.18),
        (0.0750, -0.0200, 2, 2.4, 0.12),
        (0.0120, 0.0140, 0, 0.9, 0.22),
        (0.0340, -0.0300, 0, 2.1, 0.14),
    )
    f = np.zeros((gh, gw), dtype=np.float32)
    for kx, ky, k, p0, wt in waves:
        s = kx * xx + ky * yy + p0
        f += (wt * (np.cos(ph) * np.sin(s) if k == 0 else np.sin(s - k * ph))).astype(np.float32)
    return upsample(f, h, w)


def content_evolution(img, soft, t, T, gain=0.10):
    """亮部变化明显、暗部几乎不动：云/雾/水在翻卷，山体与建筑保持沉稳。"""
    e = evolution_field(img.shape[0], img.shape[1], t, T)
    return img * (1.0 + gain * e[..., None] * soft[..., None])


# ---------------------------------------------------------------- 各区域动效

def cloud_field(hz, w, t, T, a, k=1):
    """
    天空的位移场（只算场、不采样）：分层横移 + 低频形变，近地平线动得更多。

    原来这里会 warp 一次天空、把结果按遮罩混回去；现在只交出 (dx,dy)，
    由 render 把「遮罩」直接乘进位移场，全帧只 warp 一次。
    遮罩为 0 的锐利结构位移就是 0，warp 取回原像素，效果与混合等价。
    """
    amp = 20.0
    rows = np.arange(hz, dtype=np.float32)
    depth = 0.35 + 0.65 * np.clip(rows / max(1.0, hz), 0, 1)   # 0=远 1=近
    ph = 2.0 * math.pi * k * t / T
    drift = (amp * depth * math.sin(ph))[:, None]
    dx = drift + flow_field(hz, w, t, T, 5.0, sx=140.0, sy=210.0, k=k) * depth[:, None]
    dy = flow_field(hz, w, t, T, 2.4, sx=200.0, sy=170.0, k=2) * depth[:, None]
    return dx, dy


def sea_field(rh, w, t, T):
    """水面/云海的位移场：涌动 + 起伏，越靠下越大；最底 12% 渐隐保护近景。"""
    rows = np.arange(rh, dtype=np.float32)
    depth = np.clip(rows / max(1.0, rh), 0, 1) ** 1.3
    ph = 2.0 * math.pi * t / T

    dx = (1.1 + 2.4 * depth) * np.sin(rows * 0.052 - ph * 2.0) \
        + (0.6 + 1.3 * depth) * np.sin(rows * 0.128 + ph * 3.0)
    dy = (0.4 + 1.0 * depth) * np.sin(rows * 0.040 - ph * 2.0)

    # 画面最底下通常是近处的草叶/栏杆/岸线，锐到极点的近景一横移就是双影。
    falloff = np.clip((rh - rows) / max(1.0, rh * 0.12), 0.0, 1.0)
    dx = dx * falloff
    dy = dy * falloff

    dx2 = flow_field(rh, w, t, T, 1.1, sx=120.0, sy=95.0, k=2) * (0.45 + depth)[:, None]
    dy2 = flow_field(rh, w, t, T, 0.8, sx=160.0, sy=130.0, k=3) * (0.45 + depth)[:, None]
    return dx[:, None] + dx2, dy[:, None] + dy2


def fx_sea_glow(out, hz, rh, t, T):
    """浪花反光：亮点随浪走，一明一暗（幅度压小，避免闪烁感）。这是亮度调制，不进位场。"""
    ph = 2.0 * math.pi * t / T
    band = out[hz: hz + rh]
    lum = band.max(axis=2, keepdims=True)
    sparkle = np.clip((lum - 0.55) * 2.0, 0, 1)
    rows = np.arange(rh, dtype=np.float32)
    glow = 1.0 + 0.06 * np.sin(rows[:, None, None] * 0.080 - ph * 3.0) * sparkle
    out[hz: hz + rh] = np.clip(band * glow, 0, 1)
    return out

def fx_rain(img, t, T, a):
    """雨丝：两层不同速度/密度，落出画面后从顶部回来。速度按 T 取整保证无缝。"""
    h, w, _ = img.shape
    layer = Image.new("L", (w, h), 0)
    from PIL import ImageDraw
    d = ImageDraw.Draw(layer)

    # 覆盖率是「像雨」和「像划痕」的分界：旧版只有 0.28%，放大看是几根孤立
    # 亮线。这里提到约 1%，同时把单条亮度压低 —— 密而淡才是细雨，稀而亮是刮痕。
    layers = (
        (11, max(240, w // 4), 3, 30, 0.090),  # (种子, 数量, 每周期落下几屏, 长度, 亮度)
        (37, max(64, w // 14), 5, 62, 0.065),
    )
    for seed, count, cycles, length, alpha in layers:
        speed = cycles * (h + length) / float(T)      # 一周正好落下 cycles 屏 → 首尾同帧
        for i in range(count):
            rnd = (seed * 9301 + i * 49297) % 233280
            sx = (rnd * 7919) % w
            ph = (rnd * 104729 % 997) / 997.0
            y = (ph * h + t * speed) % (h + length) - length
            ln = length * (0.6 + 0.8 * (((rnd >> 5) % 100) / 100.0))
            d.line([(sx, y), (sx + 3.0, y + ln)], fill=int(alpha * 255), width=1)

    # 1px 硬线放大看像划痕/噪点；0.6px 高斯把它化成半透明水丝
    layer = layer.filter(ImageFilter.GaussianBlur(0.6))
    al = np.asarray(layer).astype(np.float32)[..., None] / 255.0
    return np.clip(img * (1.0 - al * 0.28) + al * 0.62, 0, 1)


def fx_lights(img, t, T, a, k=2):
    """夜景灯光：按空间相位轻微起伏，像电流不稳；只作用在亮点上"""
    h, w, _ = img.shape
    lum = img.max(axis=2, keepdims=True)
    bright = np.clip((lum - 0.60) / 0.40, 0, 1)

    gh, gw = max(4, h // 16), max(4, w // 16)
    xx = np.arange(gw, dtype=np.float32)[None, :] * 16.0
    yy = np.arange(gh, dtype=np.float32)[:, None] * 16.0
    ph = 2.0 * math.pi * k * t / T
    flick = np.sin(ph + xx * 0.021) * 0.6 + np.sin(ph * 2.0 - yy * 0.036) * 0.4
    flick = flick.astype(np.float32)
    flick = upsample(flick, h, w)
    return np.clip(img * (1.0 + 0.075 * flick[..., None] * bright), 0, 1)


def fx_subject(img, t, T, a, mask):
    """
    人物：主体不动，背景极轻视差（有纵深），主体下半部（发梢/衣摆）低频微摆。
    遮罩不可靠时返回 None，交给上层走别的效果 —— 宁可不动，也不能把人脸拉扯。
    """
    if mask is None:
        return None
    h, w, _ = img.shape
    ph = 2.0 * math.pi * t / T

    dx = 2.2 * math.sin(ph) + 0.9 * math.sin(ph * 2.0 + 1.1)
    bg = x_shift(img, np.full(h, dx, dtype=np.float32))
    out = img * mask + bg * (1.0 - mask)

    # 主体下半部：越靠下摆幅越大（头发/衣摆）
    krow = np.clip((np.arange(h, dtype=np.float32) - h * 0.45) / max(1.0, h * 0.55), 0, 1)
    dx2 = (1.8 * np.sin(np.arange(h, dtype=np.float32) * 0.012 + ph * 2.0)).astype(np.float32)
    sway = x_shift(out, dx2)
    weight = mask * krow[:, None, None] * 0.85
    out = out * (1.0 - weight) + sway * weight
    return np.clip(out, 0, 1)


def ambient_field(h, w, t, T):
    """
    通用微动的位移场：整幅 0.6~1 像素低频位移。

    幅度小到不产生伪影（比一个像素还小），但每个像素每帧都在动一点点 ——
    静态图之所以「死」，就是因为它每个像素都永远不变。这层是视频感的底座。
    """
    dx = (flow_field(h, w, t, T, 0.75, sx=280.0, sy=340.0, k=1)
          + flow_field(h, w, t, T, 0.35, sx=95.0, sy=120.0, k=3))
    dy = flow_field(h, w, t, T, 0.60, sx=310.0, sy=250.0, k=2)
    return dx, dy


def fx_light(img, t, T, amp=0.032):
    """
    光影流动：一条很柔的亮带斜着慢慢扫过，配一层同周期的呼吸。
    任何画面都成立，不会撕裂、也不会像整片平移 ——
    「这张图没有明显能动的东西」时，靠它让画面不死。
    """
    h, w, _ = img.shape
    gh, gw = max(4, h // 16), max(4, w // 16)
    yy = (np.arange(gh, dtype=np.float32)[:, None] / max(1.0, gh - 1))
    xx = (np.arange(gw, dtype=np.float32)[None, :] / max(1.0, gw - 1))
    ph = 2.0 * math.pi * t / T

    band = np.exp(-((xx * 0.85 + yy * 0.55 - 0.55 - 0.30 * math.sin(ph)) ** 2) / 0.055)
    band = (band * (0.6 + 0.4 * math.sin(ph + 0.9)) * amp).astype(np.float32)
    band = upsample(band, h, w)
    g = band[..., None]

    # 用 screen 而不是直接乘：暗部被提起来，亮部不会过曝
    return np.clip(img + (1.0 - img) * g, 0, 1)


def fx_breathe(img, t, T):
    """
    兜底：0.35% 的呼吸（推近再回来）。几乎看不出来，但画面不是死的。
    用固定尺寸 + crop 实现，不留黑边。
    """
    h, w, _ = img.shape
    s = 1.0 + 0.0035 * (0.5 - 0.5 * math.cos(2.0 * math.pi * t / T))
    if abs(s - 1.0) < 1e-6:
        return img
    nw, nh = int(round(w * s)), int(round(h * s))
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8))
    im = im.resize((nw, nh), Image.BILINEAR)
    x, y = (nw - w) // 2, (nh - h) // 2
    return np.asarray(im.crop((x, y, x + w, y + h))).astype(np.float32) / 255.0


def render(img, t, T, info, masks, subj):
    """
    一帧 = 连续位移场 + 一次「只对低通图」的 warp + 内容演化 + 粒子/光影。

    v3 的核心约束：**原图的高频细节一个像素都不重采样**。
    旧版把整幅原图按亚像素位移场 warp，实测 0.25~0.5px 位移会吃掉
    14~19% 的高频能量，而位移随时间在 0↔1px 之间摆动，星点/云絮/草叶
    就一帧清一帧糊地闪 —— 播放时看起来就是碎块/马赛克。现在只把
    「低通图被位移后的变化量」加回原图：结构细节始终来自原图，动的是
    云块面、雾的浓淡、水的反光这些本来就该动的东西。

    第二条：用多尺度行波让软区域的明暗自己流动/翻卷（内容演化），
    而不是把像素从别处搬过来 —— 这是「像活的」和「像被拖着」的分界。

    所有时间项仍取 ph=2πt/T 的整数倍，t=T 严格回到起点。
    """
    h, w, _ = img.shape
    ph = 2.0 * math.pi * t / T
    dx = np.zeros((h, w), dtype=np.float32)
    dy = np.zeros((h, w), dtype=np.float32)
    sea_band = None

    if info["sky"] and masks["sky"] is not None:
        hz = masks["sky"].shape[0]
        if hz >= 8:
            cx, cy = cloud_field(hz, w, t, T, info)
            # 行权重连续：区域拼接处不留折线
            prof = np.clip((np.arange(hz, dtype=np.float32) / max(1.0, hz)) * 1.4, 0, 1)
            dx[:hz] += cx * prof[:, None]
            dy[:hz] += cy * prof[:, None]

    if info["water"] and masks["sea"] is not None:
        hz, m = masks["sea"]
        rh = m.shape[0]
        if rh >= 6 and hz + rh <= h:
            sx, sy = sea_field(rh, w, t, T)
            prof = np.clip(1.0 - np.arange(rh, dtype=np.float32) / max(1.0, rh), 0, 1)
            dx[hz:hz + rh] += sx * prof[:, None]
            dy[hz:hz + rh] += sy * prof[:, None]
            sea_band = (hz, rh)

    # 找不到云/水/雨的室内、人像，靠全幅低频微动才不是一张死图
    if not (info["sky"] or info["water"] or info["rainy"]):
        ax, ay = ambient_field(h, w, t, T)
        dx += ax
        dy += ay

    # 中部呼吸：连续椭圆权重，只落在平坦区；sin(ph) 保证首尾同帧
    bm = masks.get("breathe")
    if bm is not None:
        dy += (1.6 * math.sin(ph)) * bm

    # 场本身必须连续：小网格平滑一次，抹掉区域拼接处的折线
    dx = blur_field(dx, 10.0)
    dy = blur_field(dy, 10.0)

    # ---- 高频锁定：只形变低通图，把低频变化量加回原图
    soft = masks.get("soft")
    if soft is None:
        soft = soft_region(img)
    low = _low_cached(img, 2.0)
    delta = warp(low, dx, dy) - low
    out = img + delta * soft[..., None] * 1.25

    # ---- 内容演化：明暗/密度在软区域里流动、翻卷（不搬像素）
    out = content_evolution(out, soft, t, T, gain=0.10)

    if sea_band is not None:
        out = fx_sea_glow(out, sea_band[0], sea_band[1], t, T)
    if info["rainy"]:
        out = fx_rain(out, t, T, info)
    if info["night"]:
        out = fx_lights(out, t, T, info)
    if info["subject"] and subj is not None:
        s = fx_subject(out, t, T, info, subj)
        if s is not None:
            out = s

    # 光影永远加一层：幅度小到不会喧宾夺主，但没有它，安静的画面就是一张死图
    out = fx_light(out, t, T)
    return np.clip(out, 0, 1)


# ---------------------------------------------------------------- 出口

def encode(frames, out_path, w, h, fps, ffmpeg):
    tmp = out_path + ".part.mp4"
    cmd = [ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
           "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "%dx%d" % (w, h),
           "-r", str(fps), "-i", "-",
           # 动画调校 + 更高质量：复杂云海/发丝在低码率下会结出 8x8 宏块，
           # 播放时和「马赛克」是同一种观感。宁可文件大一点，也不留块效应。
           "-an", "-c:v", "libx264", "-crf", "14", "-preset", "slow", "-tune", "animation",
           # 固定 GOP：多段并行渲染后要能无损拼起来，段首必须是关键帧
           "-g", str(max(1, fps)), "-keyint_min", str(max(1, fps)),
           "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-r", str(fps), tmp]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    for f in frames:
        p.stdin.write((np.clip(f, 0, 1) * 255.0 + 0.5).astype(np.uint8).tobytes())
    p.stdin.close()
    code = p.wait()
    if code != 0:
        if os.path.exists(tmp):
            os.remove(tmp)
        raise RuntimeError("ffmpeg 编码失败，退出码 %d" % code)
    if os.path.exists(out_path):
        os.remove(out_path)
    os.rename(tmp, out_path)


def _render_segment(job):
    """子进程：渲染 [i0,i1) 这段帧并编码成一个临时 mp4"""
    img_path, tmp_out, w, h, fps, ffmpeg, T, n, i0, i1, hint = job
    img = load_cover(img_path, w, h)
    info = analyze(img, hint)
    masks = build_masks(img, info)
    subj = None
    if info["subject"]:
        subj, _ = subject_mask(img)

    def gen():
        for i in range(i0, i1):
            yield render(img, T * i / float(n - 1), T, info, masks, subj)

    encode(gen(), tmp_out, w, h, fps, ffmpeg)
    return tmp_out


def concat_parts(parts, out_path, ffmpeg):
    """把各段按顺序拼起来（-c copy，不重新编码，画质不变）"""
    lst = out_path + ".parts.txt"
    with open(lst, "w", encoding="utf-8") as f:
        for p in parts:
            f.write("file '%s'\r\n" % os.path.abspath(p).replace("\\", "/").replace("'", "'\\''"))
    tmp = out_path + ".cat.mp4"
    cmd = [ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
           "-f", "concat", "-safe", "0", "-i", lst, "-c", "copy",
           "-movflags", "+faststart", tmp]
    r = subprocess.run(cmd, capture_output=True) if hasattr(subprocess, "run") else None
    if r is None or r.returncode != 0:
        raise RuntimeError("拼接失败：%s" % (r.stderr.decode("utf-8", "replace") if r else "?"))
    if os.path.exists(out_path):
        os.remove(out_path)
    os.rename(tmp, out_path)
    for p in parts:
        try:
            os.remove(p)
        except OSError:
            pass
    try:
        os.remove(lst)
    except OSError:
        pass


def selftest_analyze(lib):
    """
    判定自检：把「哪些图会下雨」变成可重复的断言。

    全库里纯视觉强证据（streak > 1.35）本来一张都没命中，所以 rainy 应当
    严格等于 rain_hint(文件名)。哪天阈值一放宽把动漫线稿判成雨，这里立刻红，
    不用等出片后肉眼发现画面凭空下雨。
    """
    import glob
    import json

    cases = (
        ("sleepy-rainy-evening.jpg", True),
        ("rainy-cyberpunk-streets.jpg", True),
        ("城市夜雨.jpg", True),
        ("drizzle-window.png", True),
        ("coastal-cliffs-storm.jpg", False),
        ("biliart-17403571-4.jpg", False),
        ("floating-city-dreams.jpg", False),
        ("", False),
    )
    ok = True
    for name, want in cases:
        got = rain_hint(name)
        if got != want:
            ok = False
        print("  %-4s rain_hint(%-32s) = %-5s 期望 %s"
              % ("OK" if got == want else "FAIL", repr(name), got, want))

    if not lib:
        lib = "D:\\Wallpaper"
        cfg = os.path.join(os.environ.get("APPDATA", ""), "wallpaper", "config.json")
        try:
            with open(cfg, encoding="utf-8") as f:
                lib = json.load(f).get("libRoot") or lib
        except Exception:
            pass

    files = []
    for ext in ("*.jpg", "*.jpeg", "*.png"):
        files.extend(glob.glob(os.path.join(lib, "images", ext)))
    files = sorted(files)
    print("库：%s（%d 张）" % (lib, len(files)))
    if not files:
        print("没有找到图片，无法核对判定")
        return 1

    hits = []
    for p in files:
        name = os.path.basename(p)
        a = analyze(load_cover(p, 1280, 720), p)
        want = rain_hint(p)
        if a["rainy"] != want:
            ok = False
        if a["rainy"]:
            hits.append(name)
        print("  %-4s %-34s streak=%.3f lum=%.3f 雨=%s"
              % ("OK" if a["rainy"] == want else "FAIL", name,
                 a["streak"], a["mean_lum"], a["rainy"]))
    print("判成雨的图：%s" % (", ".join(hits) if hits else "（无）"))
    print("判定自检：%s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--image", default="")
    ap.add_argument("--out", default="")
    ap.add_argument("--probe", default="")
    ap.add_argument("--seconds", type=float, default=3.0)
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--strength", type=float, default=1.0)
    # 1600 是「画质 × 出片时间」的折中档：1080p 屏上放大 1.2 倍几乎看不出软化，
    # 而 1080p 渲染有 1.44 倍像素量、实测慢 40%。要原尺寸画质就显式传 1920。
    ap.add_argument("--max-edge", type=int, default=1600,
                    help="输出长边上限，默认 1600；1920 = 原尺寸高质量档")
    ap.add_argument("--ffmpeg", default="")
    ap.add_argument("--jobs", type=int, default=0, help="并行进程数，0=自动")
    ap.add_argument("--analyzetest", action="store_true", help="核对画面判定，不出片")
    ap.add_argument("--lib", default="", help="库根目录，仅 --analyzetest 使用")
    args = ap.parse_args()

    if args.analyzetest:
        sys.exit(selftest_analyze(args.lib))
    if not args.image:
        ap.error("需要 --image，或用 --analyzetest 跑判定自检")
    if not args.ffmpeg:
        ap.error("需要 --ffmpeg（一般由 gen-motion.ps1 传入）")

    src = Image.open(args.image)
    sw, sh = src.size
    k = min(1.0, args.max_edge / float(max(sw, sh)))
    w = int(round(sw * k)) // 2 * 2
    h = int(round(sh * k)) // 2 * 2
    if w < 64 or h < 64:
        raise SystemExit("图片太小：%dx%d" % (w, h))

    img = load_cover(args.image, w, h)
    info = analyze(img, args.image)
    masks = build_masks(img, info)
    subj, cover = (None, 0.0)
    if info["subject"]:
        subj, cover = subject_mask(img)

    n = max(6, int(round(args.seconds * args.fps)))
    T = args.seconds
    rain_src = ("语义+纹理" if info["named_rain"] and info["rainy"]
                else ("纹理" if info["rainy"] else "无"))
    print("分析：地平线=%s 天空=%s 水面=%s 雨=%s(%s) 夜景=%s 主体=%s 遮罩=%.0f%%"
          % (("-" if info["horizon"] is None else "%.2f" % info["horizon"]),
             info["sky"], info["water"], info["rainy"], rain_src, info["night"],
             ("有" if subj is not None else "无"), cover * 100), flush=True)

    if args.probe:
        for idx, frac in ((0, 0.0), (1, 0.25), (2, 0.5), (3, 0.75)):
            t = T * frac
            fr = render(img, t, T, info, masks, subj)
            Image.fromarray((np.clip(fr, 0, 1) * 255).astype(np.uint8)).save(
                "%s-%d.png" % (args.probe, idx))
        diff = np.abs(render(img, T * 0.25, T, info, masks, subj) - img).mean() * 255
        print("位移最大帧与首帧平均差异：%.2f/255" % diff, flush=True)
        return

    if not args.out:
        raise SystemExit("要么给 --out，要么给 --probe")

    import time
    t_start = time.time()

    # 一帧要 0.7~1 秒（亚像素形变是内存随机访问，快不起来），90 帧就是一分多钟。
    # 切成几段丢给多个进程各渲各的，最后无损拼起来 —— 4 核机器上大约 20 秒。
    workers = args.jobs
    if workers <= 0:
        workers = min(4, max(1, (os.cpu_count() or 2)))
    if n < 24 or workers <= 1:
        def gen():
            for i in range(n):
                yield render(img, T * i / float(n - 1 if n > 1 else 1), T, info, masks, subj)
        encode(gen(), args.out, w, h, args.fps, args.ffmpeg)
    else:
        seg = (n + workers - 1) // workers
        jobs = []
        for k in range(workers):
            i0 = k * seg
            i1 = min(n, i0 + seg)
            if i0 >= i1:
                break
            jobs.append((args.image, args.out + ".p%d.mp4" % k, w, h, args.fps,
                         args.ffmpeg, T, n, i0, i1, args.image))
        import multiprocessing as mp
        ctx = mp.get_context("spawn")
        with ctx.Pool(len(jobs)) as pool:
            parts = pool.map(_render_segment, jobs)
        concat_parts(parts, args.out, args.ffmpeg)

    print("出片：%s（%dx%d %d帧，%.0f 秒）"
          % (args.out, w, h, n, time.time() - t_start), flush=True)


if __name__ == "__main__":
    main()
