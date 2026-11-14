# -*- coding: utf-8 -*-
"""
人物动效检测器 —— 通用性自检。

对一组图片跑同一个分析器，输出：种子占比 / 人物遮罩占比 / 锚点位置 / 判定结果。
用途有两个：
  1. 确认「非人物图（城市、风景、纯色）不会被误判成人物」；
  2. 确认「人物图的遮罩比例落在合理区间，且锚点落在人脸上而不是角落」。

用法：
    python tools/person_check.py                  # 默认跑库里所有图片
    python tools/person_check.py <目录或图片...>    # 指定图片/目录
"""
import os
import sys
import glob

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import person_proto as P


def detect(path):
    """返回 (是否有主体, 报告 dict)。判据全部是自适应的，不含任何按图写死的坐标。"""
    small = np.asarray(P.load_rgb(path, P.AWR, P.AHR), np.float32)
    gray = (small[:, :, 0] * 299 + small[:, :, 1] * 587 + small[:, :, 2] * 114) / 1000.0

    gx = np.abs(np.diff(gray, axis=1))
    gy = np.abs(np.diff(gray, axis=0))
    e = np.zeros_like(gray)
    e[:, :-1] += gx
    e[:-1, :] += gy
    e *= 0.5
    eb = P.blur(np.clip(e / 32.0, 0, 1), 1) * 32.0
    med = float(np.median(eb))

    # 归一化尺度：阈值跟着这张图自己的细节水平走（不写死 7.5）
    thr = max(3.2, med * 2.4)
    seed = eb > thr
    m = P.dilate(seed, 4)
    m = P.ccl_largest(m)
    m = P.fill_holes(m)
    m = P.erode(m, 6)
    m = P.ccl_largest(m)

    cov = float(m.mean())
    rep = {"edge_med": round(med, 2), "thr": round(thr, 2),
           "seed": round(float(seed.mean()), 3), "mask": round(cov, 3)}

    if cov < 0.04:
        rep["verdict"] = "无主体（遮罩太小）"
        return False, rep
    if cov > 0.80:
        rep["verdict"] = "无主体（遮罩几乎铺满，判不出前景）"
        return False, rep

    ys, xs = np.where(m)
    cx, cy = xs.mean() / P.AWR, ys.mean() / P.AHR
    # 主体重心必须在画面中间那块（人物不会整体贴边）
    if not (0.12 < cx < 0.88 and 0.10 < cy < 0.92):
        rep["verdict"] = "无主体（重心贴边 %.2f,%.2f）" % (cx, cy)
        return False, rep

    # 主体必须「有细节」：遮罩内的平均梯度要明显高于全图 —— 人/物有轮廓和纹理，
    # 纯色块、渐变天空、模糊背景没有。
    inner = float(eb[m].mean())
    ratio = inner / max(1e-6, med)
    rep["inner_edge"] = round(inner, 2)
    rep["detail_ratio"] = round(ratio, 2)
    if ratio < 1.6:
        rep["verdict"] = "无主体（遮罩内细节不足 %.1fx）" % ratio
        return False, rep

    rep["center"] = (round(cx, 2), round(cy, 2))
    rep["verdict"] = "主体 ✓"
    return True, rep


def main(argv):
    targets = []
    if len(argv) > 1:
        for a in argv[1:]:
            if os.path.isdir(a):
                targets += sorted(glob.glob(os.path.join(a, "*.jpg")) + glob.glob(os.path.join(a, "*.png")))
            else:
                targets.append(a)
    else:
        targets = sorted(glob.glob(os.path.join(r"D:\Wallpaper\images", "*.jpg")))

    hit = 0
    for p in targets:
        try:
            ok, rep = detect(p)
        except Exception as ex:
            print("%-34s  ERROR %s" % (os.path.basename(p), ex))
            continue
        if ok:
            hit += 1
        print("%-34s %-28s %s" % (os.path.basename(p)[:34], rep["verdict"], rep))
    print("\n共 %d 张，判定为主体 %d 张" % (len(targets), hit))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
