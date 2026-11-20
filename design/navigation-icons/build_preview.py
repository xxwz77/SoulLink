from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
import math
import re

ROOT = Path(__file__).resolve().parent
SCALE = 2
FONT = Path("C:/Windows/Fonts/msyh.ttc")
FONT_BOLD = Path("C:/Windows/Fonts/msyhbd.ttc")
LATIN = Path("C:/Windows/Fonts/segoeui.ttf")

# 24 x 24 grid, 1.7 px strokes, round caps and joins.
ICONS = {
    "launch": [
        "M10.1 14.1 L7 16.2 L7.9 12.9",
        "M14.1 10.1 L16.3 7 L13 7.9",
        "M8.2 16 C6.7 15.8 6.3 14.7 7 13.5 L9 10.4 C11.6 5.5 15.1 2.7 20.8 3.2 C21.3 8.9 18.5 12.4 13.6 15 L10.5 17 C9.3 17.7 8.2 17.3 8.2 16 Z",
        "M6.7 17.4 C5.2 18.2 4.5 20 4 21 C5.2 20.6 6.8 20.1 7.8 18.7",
    ],
    "console": [
        "M3.5 8.5 H20.5", "M3.5 15.5 H20.5",
        "M8 5.5 V11.5", "M16 12.5 V18.5",
    ],
    "datacenter": [
        "M4 6.2 C4 4.9 7.6 4 12 4 C16.4 4 20 4.9 20 6.2 C20 7.5 16.4 8.4 12 8.4 C7.6 8.4 4 7.5 4 6.2 Z",
        "M4 6.2 V12 C4 13.3 7.6 14.2 12 14.2 C16.4 14.2 20 13.3 20 12 V6.2",
        "M4 12 V17.8 C4 19.1 7.6 20 12 20 C16.4 20 20 19.1 20 17.8 V12",
    ],
    "update": [
        "M20 11.6 V17.5 C20 18.9 16.4 20 12 20 C7.6 20 4 18.9 4 17.5 V11.6",
        "M12 3.5 V15", "M8.2 11.3 L12 15.1 L15.8 11.3",
    ],
    "routeupdate": [
        "M6.8 18.2 H10.8 C16.8 18.2 17 8.1 12.1 8.1 H6.2",
        "M9.1 5.2 L6.2 8.1 L9.1 11",
    ],
    "remote": [
        "M3.5 5 H20.5 V16 H3.5 Z", "M8.5 20 H15.5", "M12 16 V20",
        "M9 11.7 L10.8 13.5 L9 15.3", "M12.8 15.3 H15.5",
    ],
    "lobby": [
        "M3.5 13.5 V18.5 H20.5 V13.5", "M12 18.5 V21",
        "M5.5 10 A6.5 6.5 0 0 1 18.5 10",
        "M8.5 10 A3.5 3.5 0 0 1 15.5 10",
    ],
    "process": [
        "M8 3.5 H4 V20 H20 V16", "M8 8 H12", "M8 12 H12", "M8 16 H11",
        "M14 4 H20 V10 H14 Z", "M16 6.3 L18 8 L16 9.7",
    ],
    "cli": [
        "M5 8 L9 12 L5 16", "M12 16.5 H19",
    ],
}
DOTS = {
    "launch": [(15.7, 8.1, 1.65)],
    "datacenter": [(7, 11.9, 1.05), (7, 17.8, 1.05)],
    "routeupdate": [(5.2, 18.2, 2), (18.4, 5.7, 2)],
    "remote": [],
    "lobby": [(12, 12, 1.65)],
    "console": [], "update": [], "process": [], "cli": [],
}

LABELS = [
    ("launch", "启动台", "火箭 · 快速启动"),
    ("console", "中控台", "滑杆 · 集中控制"),
    ("datacenter", "数据中心", "数据库 · 数据管理"),
    ("update", "更新 BGI", "下载入库 · 程序更新"),
    ("routeupdate", "路线更新", "路径箭头 · 路线同步"),
    ("remote", "远程管理", "连接屏幕 · 远程访问"),
    ("lobby", "联机大厅", "广播节点 · 联机服务"),
    ("process", "会话进程", "活动窗口 · 进程状态"),
    ("cli", "命令行", "命令提示符 · 指令执行"),
]

# Arcs are converted to cubic Beziers so the same geometry feeds SVG and the PNG proof.
def arcs_to_cubics(path):
    tokens = re.findall(r"[MLHVCSQAZmlhvcsqaz]|-?\d*\.?\d+", path)
    out = []
    i = 0
    cmd = None
    current = (0.0, 0.0)
    start = current
    counts = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "A": 7, "Z": 0}
    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i].upper()
            i += 1
        n = counts[cmd]
        values = [float(v) for v in tokens[i:i+n]]
        i += n
        if cmd != "A":
            out.append(cmd + (" " + " ".join(f"{v:g}" for v in values) if values else ""))
            if cmd in ("M", "L"):
                current = (values[0], values[1])
                if cmd == "M":
                    start = current
            elif cmd == "H":
                current = (values[0], current[1])
            elif cmd == "V":
                current = (current[0], values[0])
            elif cmd in ("C", "S", "Q"):
                current = (values[-2], values[-1])
            elif cmd == "Z":
                current = start
            continue
        rx, ry, rotation, large, sweep, x2, y2 = values
        x1, y1 = current
        if rx == 0 or ry == 0:
            out.append(f"L {x2:g} {y2:g}")
            current = (x2, y2)
            continue
        phi = math.radians(rotation)
        dx, dy = (x1-x2)/2, (y1-y2)/2
        x1p = math.cos(phi)*dx + math.sin(phi)*dy
        y1p = -math.sin(phi)*dx + math.cos(phi)*dy
        rx, ry = abs(rx), abs(ry)
        lam = x1p*x1p/(rx*rx) + y1p*y1p/(ry*ry)
        if lam > 1:
            factor = math.sqrt(lam)
            rx, ry = rx*factor, ry*factor
        num = rx*rx*ry*ry - rx*rx*y1p*y1p - ry*ry*x1p*x1p
        den = rx*rx*y1p*y1p + ry*ry*x1p*x1p
        coef = math.sqrt(max(0, num/den)) * (-1 if large == sweep else 1)
        cxp = coef * rx*y1p/ry
        cyp = coef * -ry*x1p/rx
        cx = math.cos(phi)*cxp - math.sin(phi)*cyp + (x1+x2)/2
        cy = math.sin(phi)*cxp + math.cos(phi)*cyp + (y1+y2)/2
        def angle(ux, uy, vx, vy):
            dot = ux*vx + uy*vy
            length = math.hypot(ux, uy)*math.hypot(vx, vy)
            a = math.acos(max(-1, min(1, dot/length)))
            return -a if ux*vy-uy*vx < 0 else a
        theta1 = angle(1, 0, (x1p-cxp)/rx, (y1p-cyp)/ry)
        delta = angle((x1p-cxp)/rx, (y1p-cyp)/ry, (-x1p-cxp)/rx, (-y1p-cyp)/ry)
        if not sweep and delta > 0:
            delta -= 2*math.pi
        elif sweep and delta < 0:
            delta += 2*math.pi
        segments = max(1, math.ceil(abs(delta)/(math.pi/2)))
        delta_seg = delta/segments
        alpha = 4/3*math.tan(delta_seg/4)
        for seg in range(segments):
            t1 = theta1 + seg*delta_seg
            t2 = t1 + delta_seg
            def point(t):
                return (cx + rx*math.cos(phi)*math.cos(t) - ry*math.sin(phi)*math.sin(t),
                        cy + rx*math.sin(phi)*math.cos(t) + ry*math.cos(phi)*math.sin(t))
            def derivative(t):
                return (-rx*math.cos(phi)*math.sin(t) - ry*math.sin(phi)*math.cos(t),
                        -rx*math.sin(phi)*math.sin(t) + ry*math.cos(phi)*math.cos(t))
            p1 = point(t1)
            p4 = point(t2)
            d1 = derivative(t1)
            d2 = derivative(t2)
            p2 = (p1[0]+alpha*d1[0], p1[1]+alpha*d1[1])
            p3 = (p4[0]-alpha*d2[0], p4[1]-alpha*d2[1])
            out.append("C " + " ".join(f"{v:g}" for p in (p2, p3, p4) for v in p))
        current = (x2, y2)
    return " ".join(out)

def svg_icon(name):
    paths = [arcs_to_cubics(p) for p in ICONS[name]]
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none">',
             '<g stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">']
    parts += [f'  <path d="{p}"/>' for p in paths]
    parts.append('</g>')
    parts += [f'  <circle cx="{x}" cy="{y}" r="{r}" fill="currentColor"/>' for x, y, r in DOTS[name]]
    parts.append('</svg>')
    return "\n".join(parts) + "\n"

def geometry(path):
    tokens = re.findall(r"[MLHVCQZ]|-?\d*\.?\d+", path)
    subpaths = []
    points = []
    current = (0.0, 0.0)
    start = current
    i = 0
    cmd = None
    counts = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "Q": 4, "Z": 0}
    while i < len(tokens):
        if tokens[i].isalpha():
            cmd = tokens[i]
            i += 1
        n = counts[cmd]
        values = [float(v) for v in tokens[i:i+n]]
        i += n
        if cmd == "Z":
            points.append(start)
            subpaths.append(points)
            points = []
            current = start
            continue
        if cmd == "M":
            if points:
                subpaths.append(points)
            current = (values[0], values[1])
            start = current
            points = [current]
        elif cmd == "L":
            current = (values[0], values[1])
            points.append(current)
        elif cmd == "H":
            current = (values[0], current[1])
            points.append(current)
        elif cmd == "V":
            current = (current[0], values[0])
            points.append(current)
        elif cmd in ("C", "Q"):
            p0 = current
            if cmd == "C":
                p1, p2, p3 = (values[0:2], values[2:4], values[4:6])
                for step in range(1, 25):
                    t = step/24
                    u = 1-t
                    points.append((u**3*p0[0]+3*u*u*t*p1[0]+3*u*t*t*p2[0]+t**3*p3[0],
                                   u**3*p0[1]+3*u*u*t*p1[1]+3*u*t*t*p2[1]+t**3*p3[1]))
                current = p3
            else:
                p1, p2 = (values[0:2], values[2:4])
                for step in range(1, 17):
                    t = step/16
                    u = 1-t
                    points.append((u*u*p0[0]+2*u*t*p1[0]+t*t*p2[0], u*u*p0[1]+2*u*t*p1[1]+t*t*p2[1]))
                current = p2
    if points:
        subpaths.append(points)
    return subpaths

PATHS = {name: [geometry(arcs_to_cubics(p)) for p in paths] for name, paths in ICONS.items()}

def color(value):
    value = value.lstrip("#")
    if len(value) == 6:
        return tuple(int(value[i:i+2], 16) for i in (0, 2, 4)) + (255,)
    if len(value) == 8:
        return tuple(int(value[i:i+2], 16) for i in (0, 2, 4, 6))
    raise ValueError(f"Unsupported color: {value}")

def box(draw, rect, radius, fill, outline=None, width=1):
    rect = tuple(round(v*SCALE) for v in rect)
    draw.rounded_rectangle(rect, radius=round(radius*SCALE), fill=color(fill),
                           outline=color(outline) if outline else None, width=round(width*SCALE))

def text(draw, xy, value, size, fill, bold=False, latin=False, anchor=None):
    path = LATIN if latin else (FONT_BOLD if bold else FONT)
    font = ImageFont.truetype(str(path), round(size*SCALE))
    draw.text((round(xy[0]*SCALE), round(xy[1]*SCALE)), value, font=font, fill=color(fill), anchor=anchor)

def icon(draw, name, cx, cy, size, stroke, width=1.7):
    scale = size/24*SCALE
    for subpaths in PATHS[name]:
        for points in subpaths:
            pts = [(round((cx-size/2+x)*SCALE), round((cy-size/2+y)*SCALE)) for x, y in points]
            w = max(1, round(width*scale))
            draw.line(pts, fill=color(stroke), width=w, joint="curve")
            r = w/2
            for x, y in (pts[0], pts[-1]):
                draw.ellipse((round(x-r), round(y-r), round(x+r), round(y+r)), fill=color(stroke))
    for x, y, r in DOTS[name]:
        px, py, rr = (cx-size/2+x)*SCALE, (cy-size/2+y)*SCALE, r*scale
        draw.ellipse((round(px-rr), round(py-rr), round(px+rr), round(py+rr)), fill=color(stroke))

def dot(draw, x, y, r, fill):
    draw.ellipse(tuple(round(v*SCALE) for v in (x-r, y-r, x+r, y+r)), fill=color(fill))

def line(draw, xy, fill, width=1):
    draw.line(tuple(round(v*SCALE) for v in xy), fill=color(fill), width=round(width*SCALE))

def menu_panel(draw, x, y, theme):
    dark = theme == "dark"
    p = {
        "surface": "#151C2BF5" if dark else "#FFFFFFF2",
        "border": "#334052" if dark else "#DCE4EE",
        "title": "#F0F4FA" if dark else "#243247",
        "text": "#D8E1ED" if dark else "#334155",
        "muted": "#91A0B7" if dark else "#65758B",
        "faint": "#77869F" if dark else "#8A99AC",
        "icon": "#A2B0C5" if dark else "#5C6E85",
        "divider": "#2B3547" if dark else "#E6EBF1",
        "selected": "#1E414C" if dark else "#DDF4F5",
        "selected_border": "#438D9B" if dark else "#9ACDD4",
        "selected_text": "#91E7F2" if dark else "#16738A",
        "selected_icon": "#73D8EA" if dark else "#147F99",
        "accent": "#64C9DB" if dark else "#278FA6",
    }
    box(draw, (x, y, x+440, y+774), 24, p["surface"], p["border"])
    text(draw, (x+28, y+26), "暗色 · Dark" if dark else "亮色 · Light", 15, p["title"], bold=True)
    text(draw, (x+412, y+28), "菜单预览", 11, p["faint"], anchor="ra")
    line(draw, (x+24, y+68, x+416, y+68), p["divider"])
    # Collapse control and section heading.
    box(draw, (x+24, y+90, x+60, y+126), 10, "#FFFFFF08" if dark else "#F3F6FA", p["divider"])
    for offset in (0, 6, 12):
        line(draw, (x+34, y+102+offset, x+50, y+102+offset), p["icon"], 1.6)
    text(draw, (x+76, y+97), "控制台", 15, p["title"], bold=True)
    text(draw, (x+412, y+102), "WORKSPACE", 9, p["faint"], latin=True, anchor="ra")
    start = y+153
    for index, (name, label, _) in enumerate(LABELS):
        top = start+index*48
        if index == 0:
            box(draw, (x+20, top, x+420, top+42), 12, p["selected"], p["selected_border"])
            box(draw, (x+20, top+11, x+23, top+31), 1.5, p["selected_icon"])
        icon(draw, name, x+47, top+21, 23, p["selected_icon"] if index == 0 else p["icon"])
        text(draw, (x+76, top+10), label, 13, p["selected_text"] if index == 0 else p["text"], bold=index == 0)
        if index == 0:
            dot(draw, x+397, top+21, 2.5, p["selected_icon"])
    line(draw, (x+24, y+601, x+416, y+601), p["divider"])
    text(draw, (x+26, y+622), "当前会话", 10, p["faint"])
    dot(draw, x+32, y+658, 4, "#43C6A4")
    text(draw, (x+46, y+648), "Session 1  ·  lummy", 11, p["muted"], latin=True)
    # Bottom theme/settings affordances use the same icon language.
    icon(draw, "console", x+45, y+718, 19, p["icon"])
    text(draw, (x+70, y+708), "主题与设置", 11, p["muted"])
    box(draw, (x+335, y+699, x+412, y+737), 19, "#253449" if dark else "#E9EEF5")
    dot(draw, x+353, y+718, 10, "#7B8BA1" if dark else "#FFFFFF")
    text(draw, (x+412, y+756), "20 px 收起态见右侧", 10, p["faint"], anchor="ra")

def state_row(draw, x, y, label, state):
    text(draw, (x, y-2), label, 12, "#8291A7")
    bx = x+104
    if state == "selected":
        box(draw, (bx, y-12, bx+260, y+38), 11, "#1E414C", "#438D9B")
    elif state == "hover":
        box(draw, (bx, y-12, bx+260, y+38), 11, "#202C3D", "#34475C")
    icon(draw, "routeupdate", bx+28, y+13, 20, "#73D8EA" if state == "selected" else "#A2B0C5")
    text(draw, (bx+53, y+3), "路线更新", 12, "#91E7F2" if state == "selected" else "#D8E1ED", bold=state == "selected")
    if state == "selected":
        box(draw, (bx, y+3, bx+3, y+23), 1.5, "#73D8EA")
    if state == "hover":
        # A restrained pointer illustrates hover without turning the icon into a button.
        pts = [(bx+227, y+2), (bx+227, y+24), (bx+233, y+18), (bx+240, y+29), (bx+245, y+26), (bx+238, y+15), (bx+247, y+14)]
        draw.polygon([(round(a*SCALE), round(b*SCALE)) for a, b in pts], fill=color("#F3F7FC"))

img = Image.new("RGBA", (1600*SCALE, 1160*SCALE), color("#0B101B"))
d = ImageDraw.Draw(img)
# Keep the proof sheet quiet: no decoration behind the specification samples.
for x, y, r in [(1070, 62, 1.4), (1480, 167, 1.2), (998, 140, 1), (1550, 74, 1.1), (1190, 178, 1)]:
    dot(d, x, y, r, "#6A9AAF")
box(d, (56, 49, 92, 85), 18, "#203D52", "#3A6E86")
dot(d, 74, 67, 10, "#5DCAE0")
dot(d, 74, 67, 4, "#DDFBFF")
text(d, (108, 45), "SoulLink", 21, "#F2F6FC", bold=True, latin=True)
text(d, (108, 77), "导航系统 / 图标重设计", 12, "#8C9CB2")
box(d, (1336, 47, 1544, 85), 19, "#151F2E", "#30445B")
dot(d, 1358, 66, 3, "#6FD5DF")
text(d, (1373, 56), "概念样板  /  01", 12, "#B9C8D9")
text(d, (56, 125), "一眼识别，明暗皆清晰。", 34, "#F2F6FC", bold=True)
text(d, (58, 181), "从抽象字形到功能轮廓  ·  保留菜单结构，只提升识别效率", 14, "#93A5BC")
text(d, (1544, 146), "24 px 网格   /   1.7 px 描边   /   圆角线性", 12, "#8CA3BA", anchor="ra")
menu_panel(d, 56, 232, "dark")
menu_panel(d, 524, 232, "light")

# Right-hand proof: actual-size rail and interaction states.
box(d, (1024, 232, 1544, 572), 24, "#121A28", "#2A384C")
text(d, (1052, 257), "收起后，也能快速辨认", 18, "#EEF4FB", bold=True)
text(d, (1053, 292), "9 种不同轮廓，不依赖文字与记忆位置", 12, "#91A2B9")
box(d, (1054, 330, 1106, 552), 13, "#192334", "#314157")
for index, (name, _, _) in enumerate(LABELS):
    cy = 349+index*24
    if index == 0:
        box(d, (1058, cy-11, 1102, cy+11), 7, "#245060", "#3C7683")
    icon(d, name, 1080, cy, 20, "#79DCEB" if index == 0 else "#9DACC2", 1.7)
text(d, (1130, 340), "实际尺寸 · 20 px", 12, "#B5C4D7")
text(d, (1130, 379), "每个入口都有独立轮廓；", 12, "#91A2B9")
text(d, (1130, 405), "中文标签与悬停提示保留。", 12, "#91A2B9")
line(d, (1130, 444, 1512, 444), "#2A384C")
text(d, (1130, 467), "默认：次级文字色", 11, "#91A2B9")
text(d, (1130, 493), "选中：青色 + 底色 + 侧标", 11, "#91A2B9")
text(d, (1130, 519), "不依赖单一颜色传达状态。", 11, "#91A2B9")

box(d, (1024, 596, 1544, 804), 24, "#121A28", "#2A384C")
text(d, (1052, 619), "三种状态，层级更明确", 18, "#EEF4FB", bold=True)
state_row(d, 1052, 668, "默认", "default")
state_row(d, 1052, 718, "悬停", "hover")
state_row(d, 1052, 768, "选中", "selected")

box(d, (1024, 828, 1544, 1006), 24, "#121A28", "#2A384C")
text(d, (1052, 851), "一套形状，两种主题", 18, "#EEF4FB", bold=True)
for index, (title, sub) in enumerate([
    ("轮廓承载语义", "相似字形改为功能具象轮廓"),
    ("细节保持克制", "小尺寸不堆叠装饰与渐变"),
    ("主题只换颜色", "同一 SVG 使用 currentColor"),
]):
    yy = 894+index*36
    box(d, (1053, yy, 1075, yy+22), 7, "#1D3442")
    text(d, (1064, yy+3), str(index+1), 10, "#78D8E7", bold=True, latin=True, anchor="ma")
    text(d, (1090, yy-1), title, 12, "#D9E3EF", bold=True)
    text(d, (1224, yy+1), sub, 10, "#8396AF")

line(d, (56, 1050, 1544, 1050), "#253246")
text(d, (56, 1074), "设计说明", 12, "#B3C2D4", bold=True)
text(d, (137, 1074), "图标不是按钮功能的替代品：展开时保留中文标签，收起时沿用悬停提示。", 12, "#8FA2B9")
text(d, (1544, 1108), "SoulLink  ·  Navigation icon concept  /  Preview only", 10, "#627991", latin=True, anchor="ra")

img = img.resize((1600, 1160), Image.Resampling.LANCZOS).convert("RGB")
img.save(ROOT / "navigation-icon-preview.png", optimize=True)

icons_dir = ROOT / "icons"
icons_dir.mkdir(exist_ok=True)
for name in ICONS:
    (icons_dir / f"{name}.svg").write_text(svg_icon(name), encoding="utf-8")

sprite = ['<svg xmlns="http://www.w3.org/2000/svg">', '<defs>']
for name in ICONS:
    body = svg_icon(name).split("\n", 1)[1].rsplit("</svg>", 1)[0]
    sprite.append(f'<symbol id="nav-{name}" viewBox="0 0 24 24">{body}</symbol>')
sprite += ['</defs>', '</svg>', '']
(ROOT / "navigation-icons.svg").write_text("\n".join(sprite), encoding="utf-8")
print(f"Created {ROOT / 'navigation-icon-preview.png'}")
print(f"Created {len(ICONS)} SVG icons and a reusable SVG sprite.")
