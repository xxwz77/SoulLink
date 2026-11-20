from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter
import math
import random
import build_preview as base

ROOT = Path(__file__).resolve().parent
S = 2
W, H = 1680, 1160
RNG = random.Random(2046)


def C(value):
    return base.color(value)


def rect(draw, xy, radius=0, fill=None, outline=None, width=1):
    p = tuple(round(v*S) for v in xy)
    if radius:
        draw.rounded_rectangle(p, radius=round(radius*S), fill=C(fill) if fill else None,
                               outline=C(outline) if outline else None, width=max(1, round(width*S)))
    else:
        draw.rectangle(p, fill=C(fill) if fill else None, outline=C(outline) if outline else None)


def txt(draw, xy, value, size, fill, bold=False, latin=False, anchor=None):
    base.text(draw, xy, value, size, fill, bold, latin, anchor)


def line(draw, xy, fill, width=1):
    draw.line(tuple(round(v*S) for v in xy), fill=C(fill), width=max(1, round(width*S)))


def point(draw, x, y, radius, fill):
    base.dot(draw, x, y, radius, fill)


def glow(canvas, x, y, radius, tint, opacity=0.45):
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    ld.ellipse(tuple(round(v*S) for v in (x-radius, y-radius, x+radius, y+radius)), fill=C(tint))
    layer = layer.filter(ImageFilter.GaussianBlur(radius*S*0.65))
    if opacity < 1:
        alpha = layer.getchannel("A").point(lambda v: round(v*opacity))
        layer.putalpha(alpha)
    canvas.alpha_composite(layer)


def orbit(draw, cx, cy, rx, ry, angle, tint, width=1):
    a = math.radians(angle)
    pts = []
    for i in range(181):
        t = math.tau*i/180
        x, y = rx*math.cos(t), ry*math.sin(t)
        pts.append(((cx+x*math.cos(a)-y*math.sin(a))*S, (cy+x*math.sin(a)+y*math.cos(a))*S))
    draw.line(pts, fill=C(tint), width=max(1, round(width*S)))


def atmosphere(size, dark):
    im = Image.new("RGBA", (round(size[0]*S), round(size[1]*S)), C("#08091B" if dark else "#F8F2FD"))
    if dark:
        glow(im, size[0]*.75, size[1]*.19, size[0]*.28, "#563BAA", .4)
        glow(im, size[0]*.18, size[1]*.88, size[0]*.24, "#174A76", .35)
        glow(im, size[0]*.95, size[1]*.9, size[0]*.24, "#663B79", .18)
    else:
        glow(im, size[0]*.8, size[1]*.1, size[0]*.35, "#CBB8EE", .5)
        glow(im, size[0]*.16, size[1]*.78, size[0]*.3, "#B4DDF0", .4)
        glow(im, size[0]*.92, size[1]*.85, size[0]*.28, "#F0C9E2", .3)
    d = ImageDraw.Draw(im)
    for _ in range(round(size[0]*size[1]/9000)):
        x, y = RNG.uniform(0, size[0]), RNG.uniform(0, size[1])
        point(d, x, y, RNG.choice([.35, .5, .7, .9]), "#8EA3CA" if dark else "#B4A5C9")
    return im


def logo(draw, x, y, size=34):
    rect(draw, (x, y, x+size, y+size), size/2, "#283657", "#576C99")
    point(draw, x+size/2, y+size/2, size*.34, "#657CE0")
    point(draw, x+size/2, y+size/2, size*.23, "#70DCEC")
    point(draw, x+size/2, y+size/2, size*.1, "#E5FAFF")


def gradient_tile(canvas, xy, radius, dark, active=False):
    x1, y1, x2, y2 = xy
    w, h = round((x2-x1)*S), round((y2-y1)*S)
    tile = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    td = ImageDraw.Draw(tile)
    for x in range(w):
        t = x/max(1, w-1)
        a, b = ((124, 92, 255), (79, 227, 255)) if dark else ((157, 122, 204), (79, 179, 204))
        alpha = 56 if dark else 58
        rgb = tuple(round(c+(e-c)*t) for c, e in zip(a, b))
        td.line((x, 0, x, h), fill=rgb+(alpha,))
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w-1, h-1), radius=round(radius*S), fill=255)
    canvas.alpha_composite(Image.composite(tile, Image.new("RGBA", tile.size), mask), (round(x1*S), round(y1*S)))


def icon(canvas, name, x, y, size, tint, glow_color=None, gradient=False, dark=True):
    if glow_color:
        halo = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        base.icon(ImageDraw.Draw(halo), name, x, y, size, glow_color, 1.85)
        canvas.alpha_composite(halo.filter(ImageFilter.GaussianBlur(4*S)))
    if not gradient:
        base.icon(ImageDraw.Draw(canvas), name, x, y, size, tint, 1.65)
        return
    tile = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    base.icon(ImageDraw.Draw(tile), name, x, y, size, "#FFFFFF", 1.65)
    x1, y1 = round((x-size/2)*S), round((y-size/2)*S)
    sw = max(1, round(size*S))
    grad = Image.new("RGBA", (sw, sw), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    start, end = ((197, 173, 255), (116, 238, 255)) if dark else ((105, 65, 159), (0, 122, 158))
    for xx in range(sw):
        t = xx/max(1, sw-1)
        rgb = tuple(round(a+(b-a)*t) for a, b in zip(start, end))
        gd.line((xx, 0, xx, sw), fill=rgb+(255,))
    mask = tile.getchannel("A").crop((x1, y1, x1+sw, y1+sw))
    canvas.paste(grad, (x1, y1), mask)


def glass_panel(canvas, xy, dark, radius=20, nav=False):
    x1, y1, x2, y2 = xy
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    rect(ImageDraw.Draw(shadow), (x1+2, y1+6, x2+2, y2+7), radius, "#07051C80" if dark else "#68508132")
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(9*S)))
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    rect(d, xy, radius, "#12132DEB" if dark and nav else "#191B38BD" if dark else "#FFFBFFEF" if nav else "#FFFFFFBC",
         "#67558A99" if dark else "#C9B9E1")
    line(d, (x1+radius, y1+1, x2-radius, y1+1), "#A99BC74D" if dark else "#FFFFFF", 1)
    canvas.alpha_composite(layer)


def nav_menu(canvas, x, y, w, h, dark):
    # Compose translucent highlights in their own layer so they remain subtle.
    menu = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    glass_panel(menu, (x, y, x+w, y+h), dark, 18, True)
    d = ImageDraw.Draw(menu)
    title = "#F2F0FF" if dark else "#35274F"
    secondary = "#BFC5E3" if dark else "#4B3A66"
    muted = "#A8A5CA" if dark else "#78638E"
    border = "#FFFFFF1F" if dark else "#9B7AB52E"
    rect(d, (x+12, y+13, x+44, y+45), 9, "#FFFFFF10" if dark else "#FFFFFFA0", border)
    line(d, (x+31, y+22, x+25, y+29), muted, 1.5)
    line(d, (x+25, y+29, x+31, y+36), muted, 1.5)
    txt(d, (x+57, y+18), "控制台", 14, title, bold=True)
    txt(d, (x+w-19, y+22), "SOULLINK", 8.5, muted, latin=True, anchor="ra")
    line(d, (x+16, y+62, x+w-16, y+62), border)
    start, step, rowh = y+74, 41, 36
    for i, (name, label, _) in enumerate(base.LABELS):
        top = start+i*step
        if i == 0:
            gradient_tile(menu, (x+9, top, x+w-9, top+rowh), 11, dark, True)
            rect(d, (x+9, top, x+w-9, top+rowh), 11, None, "#76D5F066" if dark else "#9875C766")
            rect(d, (x+9, top+10, x+11.5, top+28), 1.2, "#7EE7FA" if dark else "#7B58C6")
            point(d, x+w-28, top+19, 2.3, "#8DE6F8" if dark else "#7D62B8")
        elif i == 5:
            rect(d, (x+9, top, x+w-9, top+rowh), 11, "#FFFFFF0B" if dark else "#9675B913")
        icon(menu, name, x+34, top+18, 21.5, muted, None, gradient=i == 0, dark=dark)
        txt(d, (x+61, top+8), label, 12.5, "#EDF1FF" if dark and i == 0 else "#654986" if i == 0 else secondary, bold=i == 0)
    divy = start+9*step+7
    line(d, (x+16, divy, x+w-16, divy), border)
    box_y = divy+15
    rect(d, (x+15, box_y, x+22, box_y+7), 2, None, muted, 1)
    rect(d, (x+18, box_y+3, x+29, box_y+10), 2, "#796AA8" if dark else "#D4C4E9")
    point(d, x+25, box_y+6.5, 2.4, "#E8E1FA")
    txt(d, (x+37, box_y-4), "鼠标悬停展开", 10.5, secondary)
    rect(d, (x+12, box_y+22, x+w-12, box_y+68), 12, "#FFFFFF08" if dark else "#FFFFFF7A", border)
    txt(d, (x+25, box_y+29), "当前会话", 8.5, muted)
    txt(d, (x+25, box_y+46), "Session 1 · lummy", 10, secondary, latin=True)
    base_y = y+h-29
    point(d, x+30, base_y, 7.5, muted)
    point(d, x+33, base_y-3, 6.5, "#14142E" if dark else "#FFFCFF")
    for k in range(8):
        a = k*math.tau/8
        line(d, (x+w-33+6*math.cos(a), base_y+6*math.sin(a), x+w-33+8.5*math.cos(a), base_y+8.5*math.sin(a)), muted, 1.5)
    point(d, x+w-33, base_y, 5.5, "#181831" if dark else "#FFFCFF")
    point(d, x+w-33, base_y, 3, muted)
    canvas.alpha_composite(menu)


def session_card(canvas, x, y, w, h, label, dark, selected=False, status="会话在线"):
    glass_panel(canvas, (x, y, x+w, y+h), dark, 16)
    d = ImageDraw.Draw(canvas)
    primary = "#E6E8FC" if dark else "#4A365E"
    muted = "#A7A6C7" if dark else "#877298"
    point(d, x+25, y+h/2, 4, "#62DBCA")
    txt(d, (x+43, y+13), label, 13, primary, bold=True, latin=True)
    txt(d, (x+43, y+35), status, 9.5, "#8D98BD" if dark else "#897596")
    txt(d, (x+w-18, y+23), "#1" if selected else "#2", 10, muted, latin=True, anchor="ra")


def content_scene(canvas, x, y, w, h, dark):
    d = ImageDraw.Draw(canvas)
    primary = "#E4E6FA" if dark else "#513C68"
    muted = "#9393B8" if dark else "#8A769F"
    txt(d, (x+26, y+25), "运行情况 · 下方为快捷动作", 12, primary, bold=True)
    txt(d, (x+26, y+49), "统一管理当前账号的运行情况", 9.5, muted)
    for i in range(2):
        top = y+82+i*70
        rect(d, (x+25, top, x+w-25, top+54), 12, "#FFFFFF0C" if dark else "#FFFFFF82", "#FFFFFF16" if dark else "#AC94C626")
        txt(d, (x+43, top+10), "本机 · 自动运行" if i == 0 else "快捷操作", 10.5, muted)
    txt(d, (x+27, y+224), "快捷动作", 10.5, muted)
    txt(d, (x+w-28, y+224), "+ 新建", 10, muted, anchor="ra")
    rect(d, (x+24, y+250, x+w-24, y+308), 12, "#8D6CDD16" if dark else "#CAB4E326", "#63D3E29A" if dark else "#AC8AC477")
    txt(d, (x+43, y+260), "一条龙启动", 11.5, primary, bold=True)
    txt(d, (x+43, y+282), "BetterGI.exe", 9, muted, latin=True)
    rect(d, (x+w-107, y+267, x+w-67, y+289), 11, "#387875" if dark else "#D7F1EE")
    txt(d, (x+w-87, y+270), "打开", 8.5, "#ABEDE7" if dark else "#3F827D", anchor="ma")
    rect(d, (x+w-61, y+267, x+w-37, y+289), 11, "#FFFFFF10" if dark else "#FFFFFF90")
    txt(d, (x+w-49, y+270), "执行", 8, muted, anchor="ma")
    # Secondary actions are intentionally subdued, as in the existing workspace.
    for i, label in enumerate(["打开 BetterGI", "自动化"]):
        top = y+323+i*65
        rect(d, (x+24, top, x+w-24, top+51), 12, "#FFFFFF09" if dark else "#FFFFFF6E", "#FFFFFF12" if dark else "#AC94C622")
        txt(d, (x+43, top+9), label, 10.5, primary)
        txt(d, (x+43, top+28), "BetterGI.exe" if i == 0 else "运行一次", 8.5, muted)


def app_mockup(canvas, x, y, w, h, dark):
    bg = atmosphere((w, h), dark)
    mask = Image.new("L", bg.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, bg.width-1, bg.height-1), radius=22*S, fill=255)
    canvas.paste(bg, (x*S, y*S), mask)
    d = ImageDraw.Draw(canvas)
    rect(d, (x, y, x+w, y+h), 22, None, "#8D79B76E" if dark else "#B6A0C980")
    orbit(d, x+w*.81, y+h*.67, w*.36, h*.14, -28, "#9A7AC326" if dark else "#9870B62A", 1)
    orbit(d, x+w*.81, y+h*.67, w*.30, h*.10, -28, "#68CBE42B" if dark else "#6BABC73D", 1)
    logo(d, x+24, y+22, 30)
    txt(d, (x+66, y+20), "SoulLink", 17, "#F0F0FF" if dark else "#49345E", bold=True, latin=True)
    txt(d, (x+67, y+43), "v2.17.44", 8.5, "#9694B6" if dark else "#887299", latin=True)
    rect(d, (x+w-193, y+22, x+w-112, y+49), 13, "#FFFFFF0C" if dark else "#FFFFFF80", "#FFFFFF21" if dark else "#AC8BC242")
    point(d, x+w-179, y+35.5, 3, "#6EE2C7")
    txt(d, (x+w-169, y+27), "引擎就绪", 9, "#B5BDD7" if dark else "#746184")
    for i, glyph in enumerate(["≡", "—", "□", "×"]):
        txt(d, (x+w-88+i*23, y+25), glyph, 13, "#A4A5C6" if dark else "#756382", latin=True)
    nav_menu(canvas, x+20, y+72, 240, h-94, dark)
    cx, cy, cw, ch = x+280, y+72, w-450, h-94
    glass_panel(canvas, (cx, cy, cx+cw, cy+ch), dark, 20)
    content_scene(canvas, cx, cy, cw, ch, dark)
    rx, rw = cx+cw+16, 154
    glass_panel(canvas, (rx, cy, rx+rw, cy+ch), dark, 20)
    txt(d, (rx+17, cy+20), "在线会话", 15, "#EBE9FC" if dark else "#49365D", bold=True)
    txt(d, (rx+17, cy+44), "自动从 query user 读取", 8, "#9391B7" if dark else "#8D779E")
    session_card(canvas, rx+12, cy+78, rw-24, 65, "lummy", dark, True, "当前会话 · 通道未投送")
    session_card(canvas, rx+12, cy+153, rw-24, 65, "A1", dark)
    session_card(canvas, rx+12, cy+228, rw-24, 65, "A4", dark)
    glow(canvas, rx+rw*.48, cy+382, 55, "#7366BB", .35 if dark else .22)
    d = ImageDraw.Draw(canvas)
    orbit(d, rx+rw*.5, cy+385, 76, 22, -25, "#C399D162" if dark else "#B287B881", 1)
    point(d, rx+rw*.46, cy+385, 28, "#7265AB88" if dark else "#C9B9E0AA")
    orbit(d, rx+rw*.46, cy+385, 20, 29, 25, "#D1A9D95C" if dark else "#C59BD399", 1)
    txt(d, (rx+18, cy+ch-116), "BGI 实时日志", 11, "#72DAE9" if dark else "#477D9D", bold=True)
    rect(d, (rx+12, cy+ch-93, rx+rw-12, cy+ch-20), 12, "#080B2255" if dark else "#FFFFFF6E", "#FFFFFF12" if dark else "#AA92BC33")
    txt(d, (rx+24, cy+ch-77), "共 3 个会话在线", 8.5, "#A1A3C5" if dark else "#7E6B91")
    txt(d, (rx+24, cy+ch-58), "点击查看运行的程序", 8, "#8587AD" if dark else "#96819F")


im = Image.new("RGBA", (W*S, H*S), C("#090A18"))
glow(im, 1320, 110, 390, "#483780", .36)
glow(im, 120, 1080, 310, "#193B60", .26)
d = ImageDraw.Draw(im)
logo(d, 50, 44, 34)
txt(d, (99, 41), "SoulLink", 21, "#F0F0FF", bold=True, latin=True)
txt(d, (99, 72), "图标重设计 / 主题融合方案", 11.5, "#A39CBD")
rect(d, (1381, 45, 1628, 82), 18, "#FFFFFF0B", "#FFFFFF1D")
point(d, 1402, 63, 3, "#7DDEEB")
txt(d, (1415, 53), "月莓星轨 × 云端棱镜", 11, "#C2BCD8")
txt(d, (50, 120), "让图标，成为星空的一部分。", 33, "#F3F1FF", bold=True)
txt(d, (52, 175), "延续紫蓝星云、磨砂玻璃与轨道光效，以清晰轮廓承载菜单语义。", 14, "#AAA5C5")
txt(d, (1628, 137), "THEME-INTEGRATED  /  02", 11, "#9B90B8", latin=True, anchor="ra")

app_mockup(im, 50, 226, 700, 670, True)
app_mockup(im, 778, 226, 700, 670, False)
d = ImageDraw.Draw(im)
for x, title, sub, dark in [(72, "月莓星轨 · 暗色", "紫蓝渐变选中 + 青色微光", True), (800, "云端棱镜 · 亮色", "乳白磨砂 + 紫罗兰选中", False)]:
    point(d, x+3, 920, 3, "#8E74DE" if dark else "#52B8D1")
    txt(d, (x+16, 908), title, 13, "#D7D0ED", bold=True)
    txt(d, (x+16, 933), sub, 10.5, "#8F8AAE")

glass_panel(im, (50, 978, 1628, 1120), True, 22)
d = ImageDraw.Draw(im)
txt(d, (78, 995), "图标保留清晰，主题负责氛围", 16, "#EEEAFE", bold=True)
txt(d, (78, 1025), "光效仅落在选中项，其余保持单色。", 10.5, "#A49FC0")
txt(d, (78, 1054), "轨道点缀 · 玻璃容器 · 紫青渐变", 10.5, "#827DA0")
for i, (name, title, sub) in enumerate([
    ("launch", "选中 · 品牌渐变", "紫罗兰 → 冰晶青"),
    ("routeupdate", "默认 · 清晰单色", "保留轮廓识别效率"),
    ("console", "悬停 · 玻璃提亮", "不以强发光干扰阅读"),
]):
    xx = 580+i*340
    if i == 0:
        gradient_tile(im, (xx, 999, xx+74, 1073), 18, True, True)
        rect(d, (xx, 999, xx+74, 1073), 18, None, "#83DCF077")
    elif i == 2:
        rect(d, (xx, 999, xx+74, 1073), 18, "#FFFFFF0C", "#FFFFFF1C")
    icon(im, name, xx+37, 1036, 38, "#A5A3C7", "#4FE3FF77" if i == 0 else None, gradient=i == 0)
    txt(d, (xx+94, 1009), title, 12, "#DCD6EF", bold=True)
    txt(d, (xx+94, 1035), sub, 10, "#9791B4")
    txt(d, (xx+94, 1056), "20 px 下保持同一轮廓", 9.5, "#7D7699")

im = im.resize((W, H), Image.Resampling.LANCZOS).convert("RGB")
im.save(ROOT / "theme-integrated-icon-preview.png", optimize=True)
print(ROOT / "theme-integrated-icon-preview.png")
