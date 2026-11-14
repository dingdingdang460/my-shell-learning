"""生成 resources/app.ico（多尺寸）+ resources/app-256.png，纯 Pillow 手绘，可重复跑。

设计思路：任务栏 / 托盘里经常只有 16px，细节全糊，所以只用三个元素——
一张叠放的「壁纸卡」、一颗太阳、一道山脊。放大不空，缩小不糊。
"""

import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ICO = os.path.join(ROOT, "resources", "app.ico")
OUT_PNG = os.path.join(ROOT, "resources", "app-256.png")

S = 1024  # 先大后缩，边缘更干净


def rounded(size, radius, fill):
    img = Image.new("RGBA", (size[0], size[1]), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=fill)
    return img


def vertical_gradient(size, top, bottom):
    img = Image.new("RGBA", size, (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        d.line([(0, y), (size[0], y)], fill=c + (255,))
    return img


def build():
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 外框：圆角方块，深色底 + 一点亮边
    outer = rounded((S, S), int(S * 0.22), (24, 26, 33, 255))
    canvas.alpha_composite(outer)

    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ed = ImageDraw.Draw(edge)
    ed.rounded_rectangle([3, 3, S - 4, S - 4], radius=int(S * 0.22), outline=(58, 64, 82, 255), width=6)
    canvas.alpha_composite(edge)

    # 后面那张「壁纸」：暗蓝，露出右上角，表示「一叠」
    back = rounded((int(S * 0.62), int(S * 0.42)), int(S * 0.05), (43, 54, 82, 255))
    canvas.alpha_composite(back, (int(S * 0.28), int(S * 0.20)))

    # 前面那张：蓝到青的渐变，里面放太阳和山
    card_w, card_h = int(S * 0.66), int(S * 0.46)
    card = vertical_gradient((card_w, card_h), (58, 128, 214), (26, 158, 122))
    mask = rounded((card_w, card_h), int(S * 0.05), (255, 255, 255, 255))
    card.putalpha(mask.split()[3])

    cd = ImageDraw.Draw(card)
    # 太阳
    sun_r = int(S * 0.045)
    sx, sy = int(card_w * 0.74), int(card_h * 0.27)
    cd.ellipse([sx - sun_r, sy - sun_r, sx + sun_r, sy + sun_r], fill=(250, 199, 117, 255))
    # 山脊：两层，前面深一点，拉出层次
    cd.polygon([(0, card_h), (int(card_w * 0.30), int(card_h * 0.46)),
                (int(card_w * 0.48), int(card_h * 0.72)), (card_w, card_h)],
               fill=(16, 26, 44, 235))
    cd.polygon([(int(card_w * 0.40), card_h), (int(card_w * 0.66), int(card_h * 0.58)),
                (card_w, int(card_h * 0.88)), (card_w, card_h)],
               fill=(10, 18, 32, 255))

    canvas.alpha_composite(card, (int(S * 0.17), int(S * 0.38)))

    return canvas


def main():
    big = build()
    big.resize((256, 256), Image.LANCZOS).save(OUT_PNG)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    # 必须把大图交给 Pillow 让它自己缩：给一张 16px 的图配 sizes 列表，
    # 它只会存 16px 那一档（第一次就是这么踩的，ico 只有 680 字节）
    big.save(OUT_ICO, format="ICO", sizes=[(s, s) for s in sizes])
    print("wrote", OUT_ICO)
    print("wrote", OUT_PNG)


if __name__ == "__main__":
    main()
