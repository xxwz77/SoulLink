from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter
import math
import build_preview as base

ROOT = Path(__file__).resolve().parent
S = 2
W, H = 1800, 1210

PALETTE = {
    True: {
        "bg": "#0A0B1A", "nav": "#111126F5", "edge": "#72618A75",
        "title": "#F1EFFF", "text": "#D4D0EA", "muted": "#AAA3C7",
        "line": "#FFFFFF21", "tile": "#FFFFFF0B",
        "icon": "#B7B1D1", "active_icon": "#F1F0FF", "active_text": "#F1F0FF",
    },
    False: {
        "bg": "#F5EFFB", "nav": "#FFFCFFF4", "edge": "#AD94C270",
        "title": "#39294F", "text": "#55416B", "muted": "#7F6B94",
        "line": "#9E7EB72E", "tile": "#9D79B90D",
        "icon": "#77638F", "active_icon": "#69478F", "active_text": "#563576",
    },
}


def C(value):
    return base.color(value)


def translucent(draw, value):
    return value is not None and len(value.lstrip("#")) == 8 and C(value)[3] < 255


def box(draw, xy, radius=0, fill=None, outline=None, width=1, _composite=False):
    if not _composite and (translucent(draw, fill) or translucent(draw, outline)):
        # ImageDraw writes RGBA channels directly on an RGBA surface. Composite first
        # so a translucent glass tint does not accidentally become an opaque white bar.
        layer = Image.new("RGBA", draw._image.size, (0, 0, 0, 0))
        box(ImageDraw.Draw(layer), xy, radius,
            fill if translucent(draw, fill) else None,
            outline if translucent(draw, outline) else None, width, _composite=True)
        draw._image.alpha_composite(layer)
        fill = fill if not translucent(draw, fill) else None
        outline = outline if not translucent(draw, outline) else None
        if fill is None and outline is None:
            return
    xy = tuple(round(v*S) for v in xy)
    if radius:
        draw.rounded_rectangle(xy, radius=round(radius*S), fill=C(fill) if fill else None,
                               outline=C(outline) if outline else None, width=max(1, round(width*S)))
    else:
        draw.rectangle(xy, fill=C(fill) if fill else None, outline=C(outline) if outline else None)


def text(draw, xy, value, size, color, bold=False, latin=False, anchor=None):
    base.text(draw, xy, value, size, color, bold, latin, anchor)


def line(draw, xy, color, width=1):
    draw.line(tuple(round(v*S) for v in xy), fill=C(color), width=max(1, round(width*S)))


def dot(draw, x, y, radius, color):
    base.dot(draw, x, y, radius, color)


def composite_round(canvas, xy, radius, color, outline=None):
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    box(d, xy, radius, color, outline)
    canvas.alpha_composite(layer)


def gradient_row(canvas, xy, radius, dark, variant):
    x1, y1, x2, y2 = xy
    w, h = round((x2-x1)*S), round((y2-y1)*S)
    tile = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    td = ImageDraw.Draw(tile)
    if variant == "A":
        a, b, alpha = ((124, 92, 255), (79, 227, 255), 64) if dark else ((157, 122, 204), (79, 179, 204), 52)
    else:
        a, b, alpha = ((126, 92, 194), (93, 159, 205), 47) if dark else ((162, 131, 202), (111, 174, 194), 38)
    for xx in range(w):
        t = xx/max(1, w-1)
        rgb = tuple(round(c+(e-c)*t) for c, e in zip(a, b))
        td.line((xx, 0, xx, h), fill=rgb+(alpha,))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w-1, h-1), radius=round(radius*S), fill=255)
    canvas.alpha_composite(Image.composite(tile, Image.new("RGBA", tile.size), mask), (round(x1*S), round(y1*S)))


def gradient_icon(canvas, name, x, y, size, dark):
    tile = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    base.icon(ImageDraw.Draw(tile), name, x, y, size, "#FFFFFF", 1.65)
    x1, y1 = round((x-size/2)*S), round((y-size/2)*S)
    side = round(size*S)
    grad = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    start, end = ((202, 175, 255), (117, 236, 255)) if dark else ((105, 65, 159), (0, 122, 158))
    for xx in range(side):
        t = xx/max(1, side-1)
        rgb = tuple(round(a+(b-a)*t) for a, b in zip(start, end))
        gd.line((xx, 0, xx, side), fill=rgb+(255,))
    canvas.paste(grad, (x1, y1), tile.getchannel("A").crop((x1, y1, x1+side, y1+side)))


def hamburger(draw, x, y, dark):
    p = PALETTE[dark]
    for i in range(3):
        line(draw, (x-7, y-6+i*6, x+7, y-6+i*6), p["icon"], 1.6)


def moon(draw, x, y, dark, nav=False):
    p = PALETTE[dark]
    dot(draw, x, y, 7, p["muted"])
    dot(draw, x+3, y-3, 6, "#131329" if dark and nav else "#0C0D1E" if dark else "#FFFCFF" if nav else "#F5EFFB")


def gear(draw, x, y, color):
    for i in range(8):
        a = i*math.tau/8
        line(draw, (x+6*math.cos(a), y+6*math.sin(a), x+8*math.cos(a), y+8*math.sin(a)), color, 1.5)
    dot(draw, x, y, 5.5, "#131329")
    dot(draw, x, y, 3, color)


def collapsed_rail(canvas, x, y, dark, variant):
    # This is a visual sample only: no change to the existing collapse behavior.
    p = PALETTE[dark]
    composite_round(canvas, (x, y, x+58, y+430), 14, p["nav"], p["edge"])
    d = ImageDraw.Draw(canvas)
    hamburger(d, x+29, y+25, dark)
    line(d, (x+10, y+49, x+48, y+49), p["line"])
    for i, (name, _, _) in enumerate(base.LABELS):
        cy = y+72+i*38
        if i == 0:
            if variant == "A":
                gradient_row(canvas, (x+7, cy-16, x+51, cy+16), 9, dark, variant)
            elif variant == "B":
                composite_round(canvas, (x+7, cy-16, x+51, cy+16), 9, p["tile"], "#79CFE566" if dark else "#8665AB66")
            elif variant == "C":
                d.polygon([(round((x+7)*S), round((cy-16)*S)), (round((x+44)*S), round((cy-16)*S)),
                           (round((x+51)*S), round((cy-9)*S)), (round((x+51)*S), round((cy+9)*S)),
                           (round((x+44)*S), round((cy+16)*S)), (round((x+7)*S), round((cy+16)*S))], fill=C("#7C5CFF4D" if dark else "#9670C32E"))
            else:
                composite_round(canvas, (x+7, cy-16, x+51, cy+16), 6, p["tile"])
        if variant == "A" and i == 0:
            gradient_icon(canvas, name, x+29, cy, 20, dark)
        else:
            base.icon(d, name, x+29, cy, 20, p["active_icon"] if i == 0 else p["icon"], 1.65)
    moon(d, x+29, y+405, dark, True)


def menu(canvas, x, y, w, h, dark, variant):
    p = PALETTE[dark]
    # A near-opaque nav surface, matching the existing menu's role over page content.
    composite_round(canvas, (x, y, x+w, y+h), 17, p["nav"], p["edge"])
    d = ImageDraw.Draw(canvas)
    line(d, (x+18, y+1, x+w-18, y+1), "#C9B7E940" if dark else "#FFFFFF")
    box(d, (x+14, y+14, x+46, y+46), 9, p["tile"], p["line"])
    hamburger(d, x+30, y+30, dark)
    text(d, (x+61, y+18), "控制台", 15, p["title"], bold=True)
    text(d, (x+w-19, y+22), "MENU", 8.5, p["muted"], latin=True, anchor="ra")
    line(d, (x+16, y+65, x+w-16, y+65), p["line"])
    start, step, rowh = y+87, 45, 38
    for i, (name, label, _) in enumerate(base.LABELS):
        top = start+i*step
        selected, hovered = i == 0, i == 5
        if hovered:
            box(d, (x+9, top, x+w-9, top+rowh), 10, p["tile"])
        if selected:
            if variant == "A":
                gradient_row(canvas, (x+9, top, x+w-9, top+rowh), 10, dark, variant)
                box(d, (x+9, top, x+w-9, top+rowh), 10, None, "#85D4EC55" if dark else "#9774B84D")
                box(d, (x+9, top+10, x+11.5, top+28), 1, "#7CE2F7" if dark else "#8760B2")
                dot(d, x+w-28, top+19, 2.4, "#89E5F8" if dark else "#8662AD")
            elif variant == "B":
                box(d, (x+9, top, x+w-9, top+rowh), 10, "#84CBE417" if dark else "#B5DDEB2E",
                    "#83D9ECA0" if dark else "#A387BC99", 1.2)
            elif variant == "C":
                # Faceted selection echoes the nebula theme without touching the window chrome.
                points = [(x+9, top), (x+w-19, top), (x+w-9, top+10), (x+w-9, top+rowh-10),
                          (x+w-19, top+rowh), (x+9, top+rowh)]
                d.polygon([(round(a*S), round(b*S)) for a, b in points], fill=C("#7C5CFF4D" if dark else "#A684C729"))
                box(d, (x+9, top, x+12, top+rowh), 0, "#AA87F2" if dark else "#8252AC")
            else:
                box(d, (x+9, top, x+w-9, top+rowh), 7, "#FFFFFF12" if dark else "#8866A916")
                dot(d, x+20, top+19, 2.5, "#B8B1D1" if dark else "#826799")
        if selected and variant == "A":
            gradient_icon(canvas, name, x+43, top+19, 21.5, dark)
        else:
            base.icon(d, name, x+43, top+19, 21.5,
                      p["active_icon"] if selected else "#C8C4E1" if dark and hovered else p["icon"], 1.65)
        text(d, (x+74, top+9), label, 13, p["active_text"] if selected else p["text"], bold=selected)
        if selected and variant == "B":
            line(d, (x+w-36, top+19, x+w-25, top+19), "#87D7E8" if dark else "#8162A5", 1.5)
    divy = start+9*step+10
    line(d, (x+16, divy, x+w-16, divy), p["line"])
    # The original footer controls are shown in the same order and positions.
    box_y = divy+18
    box(d, (x+20, box_y, x+27, box_y+7), 2, None, p["muted"], 1)
    box(d, (x+23, box_y+3, x+34, box_y+10), 2, "#8071A2" if dark else "#D6C4E8")
    dot(d, x+30, box_y+6.5, 2.2, "#F0E9FF")
    text(d, (x+43, box_y-4), "鼠标悬停展开", 10.5, p["text"])
    composite_round(canvas, (x+14, box_y+24, x+w-14, box_y+88), 12, p["tile"], p["line"])
    d = ImageDraw.Draw(canvas)
    text(d, (x+27, box_y+32), "当前会话", 9, p["muted"])
    text(d, (x+27, box_y+51), "Session 1", 10.5, p["text"], latin=True)
    text(d, (x+27, box_y+68), "lummy", 9, p["muted"], latin=True)
    moon(d, x+31, y+h-26, dark, True)
    # Gear uses a local theme-aware center color rather than inheriting a window style.
    gx, gy = x+w-35, y+h-26
    # A simple six-sided settings mark keeps the preview focused on the menu icons.
    points = [(gx+8*math.cos(k*math.tau/6), gy+8*math.sin(k*math.tau/6)) for k in range(7)]
    d.line([(round(a*S), round(b*S)) for a, b in points], fill=C(p["muted"]), width=round(1.5*S), joint="curve")
    dot(d, gx, gy, 3, "#15152D" if dark else "#FFFCFF")
    dot(d, gx, gy, 1.8, p["muted"])


im = Image.new("RGBA", (W*S, H*S), C("#090A17"))
d = ImageDraw.Draw(im)
# Page backdrop is intentionally quiet: only the four sidebar variants are being compared.
text(d, (52, 39), "SoulLink", 22, "#F0EDFF", bold=True, latin=True)
text(d, (52, 74), "可展开左侧菜单 / 样式选型", 12, "#A39ABE")
box(d, (1455, 41, 1748, 78), 18, "#FFFFFF0B", "#FFFFFF20")
dot(d, 1477, 59.5, 3, "#84D5E4")
text(d, (1491, 50), "4 套方向 · 明暗双主题", 11, "#C0B7D6")
text(d, (52, 121), "只改菜单，不动其他。", 34, "#F2EFFF", bold=True)
text(d, (54, 178), "菜单结构、文字、顺序、展开交互与全局主题保持原样。", 14, "#AAA0C3")
text(d, (1748, 138), "SIDEBAR STYLE STUDY  /  03", 10.5, "#8D80A7", latin=True, anchor="ra")

cards = [
    ("A", "星轨微光", "紫青渐变 · 轻微发光", "最贴近当前主题，识别与氛围兼顾"),
    ("B", "冰晶描边", "细线轮廓 · 通透玻璃", "更清爽，选中边界一目了然"),
    ("C", "月莓切角", "几何切面 · 紫色侧标", "更有品牌个性，仍保留线性图标"),
    ("D", "原生极简", "单色图标 · 轻量底色", "最低视觉干扰，适合长期工作"),
]
for index, (variant, title, tags, desc) in enumerate(cards):
    x = 52+index*428
    y = 230
    dark = True
    # Card backdrops are for comparison only; they are not proposed app changes.
    composite_round(im, (x, y, x+392, y+896), 22, "#0D0F20", "#322B47")
    d = ImageDraw.Draw(im)
    box(d, (x+22, y+20, x+58, y+56), 10, "#FFFFFF0C", "#FFFFFF1C")
    text(d, (x+40, y+26), variant, 15, "#C4B7E3", bold=True, latin=True, anchor="ma")
    text(d, (x+74, y+19), title, 17, "#EEE9FC", bold=True)
    text(d, (x+74, y+48), tags, 10, "#968BAD")
    text(d, (x+366, y+26), "DARK", 9, "#877A9F", latin=True, anchor="ra")
    menu(im, x+20, y+82, 294, 590, dark, variant)
    collapsed_rail(im, x+324, y+83, dark, variant)
    d = ImageDraw.Draw(im)
    line(d, (x+20, y+693, x+372, y+693), "#FFFFFF16")
    text(d, (x+22, y+713), "LIGHT", 9, "#877A9F", latin=True)
    text(d, (x+370, y+711), "云端棱镜", 10, "#968BAD", anchor="ra")
    # Two complete light rows: a quiet default plus the variant's selected state.
    composite_round(im, (x+20, y+743, x+372, y+845), 14, PALETTE[False]["nav"], PALETTE[False]["edge"])
    d = ImageDraw.Draw(im)
    base.icon(d, "console", x+47, y+778, 21, PALETTE[False]["icon"], 1.65)
    text(d, (x+74, y+767), "中控台", 12, PALETTE[False]["text"])
    text(d, (x+356, y+769), "默认", 9.5, PALETTE[False]["muted"], anchor="ra")
    ly = y+800
    box(d, (x+20, ly, x+372, ly+36), 10, "#FFFCFFF4", PALETTE[False]["edge"])
    if variant == "A":
        gradient_row(im, (x+24, ly+3, x+368, ly+33), 8, False, variant)
    elif variant == "B":
        box(d, (x+24, ly+3, x+368, ly+33), 8, "#B5DDEB2E", "#A387BC99")
    elif variant == "C":
        box(d, (x+24, ly+3, x+368, ly+33), 4, "#A684C729")
        box(d, (x+24, ly+3, x+27, ly+33), 0, "#8252AC")
    else:
        box(d, (x+24, ly+3, x+368, ly+33), 6, "#8866A916")
    d = ImageDraw.Draw(im)
    if variant == "A":
        gradient_icon(im, "launch", x+47, ly+18, 20, False)
    else:
        base.icon(d, "launch", x+47, ly+18, 20, PALETTE[False]["active_icon"], 1.65)
    text(d, (x+74, ly+8), "启动台", 12, PALETTE[False]["active_text"], bold=True)
    text(d, (x+356, ly+9), "选中态", 9.5, PALETTE[False]["muted"], anchor="ra")
    text(d, (x+22, y+861), desc, 9.8, "#968BAD")

line(d, (52, 1153, 1748, 1153), "#FFFFFF18")
text(d, (52, 1174), "仅菜单样式提案", 11, "#B3A8CB", bold=True)
text(d, (171, 1174), "不包含任何窗口、交互逻辑或全局主题修改。", 11, "#8E82A7")
text(d, (1748, 1174), "Preview only", 9.5, "#776B90", latin=True, anchor="ra")

im = im.resize((W, H), Image.Resampling.LANCZOS).convert("RGB")
im.save(ROOT / "sidebar-style-options.png", optimize=True)
print(ROOT / "sidebar-style-options.png")
