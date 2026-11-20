# -*- coding: utf-8 -*-
"""SoulLink icon exploration - vector-ish raster renderer (numpy accelerated)."""
import os, math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon"
os.makedirs(OUT, exist_ok=True)

SS = 3                 # supersample factor for shape canvas
BASE = 1024
C = BASE * SS

# ---------------- palette (from Theme/Styles.xaml) ----------------
BG_TOP   = (15, 20, 52)
BG_BOT   = (5, 7, 20)
CYAN     = (79, 227, 255)
ICE      = (140, 214, 255)
BLUE     = (58, 132, 255)
PURPLE   = (139, 107, 255)
MAGENTA  = (255, 95, 210)
MINT     = (65, 240, 192)
WHITE    = (255, 255, 255)

def _norm(c):
    return np.array(c, dtype=np.float32)

def vgrad(w, h, top, bot):
    """vertical RGB gradient as float array"""
    t = np.linspace(0.0, 1.0, h, dtype=np.float32)[:, None, None]
    a = _norm(top); b = _norm(bot)
    arr = a[None, None, :] * (1 - t) + b[None, None, :] * t
    arr = np.repeat(arr, w, axis=1)
    return arr

def lgrad(w, h, p0, p1):
    """normalised linear gradient coordinate field 0..1 from p0->p1 (in px)"""
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    dx, dy = p1[0]-p0[0], p1[1]-p0[1]
    ln = dx*dx + dy*dy
    t = ((xs - p0[0]) * dx + (ys - p0[1]) * dy) / ln
    return np.clip(t, 0.0, 1.0)

def ramp_field(t, stops):
    """stops: [(pos, color), ...] -> RGB float array"""
    t = np.asarray(t, dtype=np.float32)
    cols = [(_norm(c)) for _, c in stops]
    pos = [p for p, _ in stops]
    out = np.zeros(t.shape + (3,), dtype=np.float32)
    for i in range(len(stops)-1):
        lo, hi = pos[i], pos[i+1]
        m = (t >= lo) & (t <= hi)
        local = np.clip((t - lo) / max(hi-lo, 1e-6), 0, 1)
        for k in range(3):
            out[..., k] = np.where(m, cols[i][k]*(1-local) + cols[i+1][k]*local, out[..., k])
    out[t < pos[0]] = cols[0]
    out[t > pos[-1]] = cols[-1]
    return out

def radial_alpha(w, h, cx, cy, r, power=2.0):
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt((xs-cx)**2 + (ys-cy)**2) / max(r, 1e-6)
    return np.clip(1.0 - d, 0.0, 1.0) ** power

def add_glow(rgb, alpha, color, strength=1.0):
    col = _norm(color)
    a = (alpha * strength)[..., None]
    return rgb * (1 - a*0.85) + col[None, None, :] * (a*0.85) * 1.0 + rgb*(1-a*0.15)*(a*0.85)*0 

def composite_over(base_rgb, base_a, layer_rgb, layer_a):
    """standard source-over on premultiplied-ish float"""
    la = layer_a[..., None]
    out_rgb = layer_rgb * la + base_rgb * (1 - la)
    out_a = la[..., 0] + base_a * (1 - la[..., 0])
    return out_rgb, out_a

def glow_layer(size, cx, cy, r, color, peak, power=2.4):
    a = radial_alpha(size, size, cx, cy, r, power) * peak
    rgb = np.ones((size, size, 3), dtype=np.float32) * _norm(color)[None, None, :]
    return rgb, a

# ---------------- plate ----------------
def make_plate(size, radius_ratio=0.2222):
    rgb = vgrad(size, size, BG_TOP, BG_BOT)
    a = np.ones((size, size), dtype=np.float32)

    # aurora glows
    for (cx, cy, r, col, pk) in [
        (size*0.20, size*0.14, size*0.62, CYAN,    0.34),
        (size*0.90, size*0.34, size*0.60, PURPLE,  0.42),
        (size*0.74, size*0.98, size*0.55, MAGENTA, 0.30),
    ]:
        grgb, ga = glow_layer(size, cx, cy, r, col, pk)
        rgb, a = composite_over(rgb, a, grgb, ga)

    # top inner highlight (glass edge)
    hl = np.clip(1.0 - np.mgrid[0:size, 0:size][0].astype(np.float32)/(size*0.42), 0, 1)**2 * 0.10
    rgb = rgb + _norm(WHITE)[None, None, :] * hl[..., None]

    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size-1, size-1],
                                           radius=int(size*radius_ratio), fill=255)
    img.putalpha(mask)
    return img

# ---------------- glyph helpers ----------------
def layer(size):
    return Image.new("RGBA", (size, size), (0, 0, 0, 0))

def shape_mask(size, fn):
    m = Image.new("L", (size, size), 0)
    fn(ImageDraw.Draw(m))
    return m

def fill_gradient_mask(size, mask, p0, p1, stops, opacity=1.0):
    t = lgrad(size, size, p0, p1)
    rgb = ramp_field(t, stops)
    a = (np.asarray(mask, dtype=np.float32)/255.0) * opacity * 255.0
    out = np.dstack([rgb, np.clip(a, 0, 255)[..., None]]).astype(np.uint8)
    return Image.fromarray(out, "RGBA")

def soft_glow_from_mask(mask, color, blur_frac, gain, size):
    blur = mask.filter(ImageFilter.GaussianBlur(size*blur_frac))
    a = np.clip(np.asarray(blur, dtype=np.float32)/255.0 * gain * 255.0, 0, 255)
    rgb = np.ones((size, size, 3), dtype=np.float32) * _norm(color)[None, None, :]
    return Image.fromarray(np.dstack([rgb, a[..., None]]).astype(np.uint8), "RGBA")

# =====================================================================
# CONCEPTS
# =====================================================================

def c1_beam(size):
    """Concentric session target pierced by a vertical delivery beam."""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5

    glyph = layer(size)
    g = ImageDraw.Draw(glyph)
    # outer rings
    for (r, w, op) in [(size*0.330, size*0.020, 0.55), (size*0.250, size*0.018, 0.80)]:
        m = shape_mask(size, lambda d, r=r, w=w: d.ellipse(
            [cx-r-w/2, cy-r-w/2, cx+r+w/2, cy+r+w/2], outline=255, width=int(w)))
        glyph.alpha_composite(fill_gradient_mask(size, m, (cx-r, cy-r), (cx+r, cy+r),
                                                 [(0.0, CYAN), (1.0, PURPLE)], op))
    # main bold ring
    R, W = size*0.170, size*0.042
    m = shape_mask(size, lambda d: d.ellipse([cx-R-W/2, cy-R-W/2, cx+R+W/2, cy+R+W/2], outline=255, width=int(W)))
    glyph.alpha_composite(fill_gradient_mask(size, m, (cx-R, cy-R), (cx+R, cy+R),
                                             [(0.0, CYAN), (0.55, ICE), (1.0, MAGENTA)], 1.0))
    # vertical beam
    bw, ty, by = size*0.050, size*0.130, size*0.870
    m = shape_mask(size, lambda d: d.rounded_rectangle([cx-bw/2, ty, cx+bw/2, by], radius=bw/2, fill=255))
    glyph.alpha_composite(fill_gradient_mask(size, m, (cx, ty), (cx, by),
                                             [(0.0, MINT), (0.5, WHITE), (1.0, MAGENTA)], 1.0))
    # glow
    img.alpha_composite(soft_glow_from_mask(m, CYAN, 0.05, 0.85, size))
    img.alpha_composite(glyph)
    return img

def c2_chain(size):
    """Two interlocking link rings = the 'Link'."""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    R, W = size*0.148, size*0.046
    off = size*0.118

    def ring_at(x, stops, alpha=1.0):
        m = shape_mask(size, lambda d: d.ellipse([x-R-W/2, cy-R-W/2, x+R+W/2, cy+R+W/2],
                                                 outline=255, width=int(W)))
        return m, fill_gradient_mask(size, m, (x-R, cy-R), (x+R, cy+R), stops, alpha)

    m1, l1 = ring_at(cx-off, [(0.0, MINT), (1.0, CYAN)])
    m2, l2 = ring_at(cx+off, [(0.0, PURPLE), (1.0, MAGENTA)])

    # interlock: split left ring so right ring passes through
    a1 = np.asarray(l1).copy()
    ys, xs = np.mgrid[0:size, 0:size]
    band = (xs > cx-off) & (np.abs(ys-cy) < W*0.62)
    a1[..., 3] = np.where(band, 0, a1[..., 3])
    l1 = Image.fromarray(a1, "RGBA")

    glow = layer(size)
    glow.alpha_composite(soft_glow_from_mask(m1, CYAN, 0.045, 0.75, size))
    glow.alpha_composite(soft_glow_from_mask(m2, MAGENTA, 0.045, 0.75, size))
    img.alpha_composite(glow)
    img.alpha_composite(l1)
    img.alpha_composite(l2)
    return img

def c3_portal(size):
    """Rounded portal frame with a spark breaking through = silent injection."""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    glyph = layer(size)
    W = size*0.215; H = size*0.235
    lw = int(size*0.052)
    rad = size*0.105
    m = shape_mask(size, lambda d: d.rounded_rectangle([cx-W, cy-H, cx+W, cy+H], radius=rad,
                                                       outline=255, width=lw))
    glyph.alpha_composite(fill_gradient_mask(size, m, (cx-W, cy-H), (cx+W, cy+H),
                                             [(0.0, CYAN), (0.5, PURPLE), (1.0, MAGENTA)], 1.0))
    img.alpha_composite(soft_glow_from_mask(m, PURPLE, 0.05, 0.9, size))
    img.alpha_composite(glyph)

    g = ImageDraw.Draw(img)
    # spark / arrow entering
    r = size*0.083
    g.ellipse([cx-r, cy-r, cx+r, cy+r], fill=WHITE)
    r2 = size*0.056
    g.ellipse([cx-r2, cy-r2, cx+r2, cy+r2], fill=CYAN + (255,))
    return img

def c4_arrow(size):
    """Ring session + arrow delivering into it."""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    R, W = size*0.205, size*0.050
    glyph = layer(size)
    m = shape_mask(size, lambda d: d.ellipse([cx-R-W/2, cy-R-W/2, cx+R+W/2, cy+R+W/2],
                                             outline=255, width=int(W)))
    glyph.alpha_composite(fill_gradient_mask(size, m, (cx-R, cy-R), (cx+R, cy+R),
                                             [(0.0, CYAN), (1.0, PURPLE)], 1.0))
    img.alpha_composite(soft_glow_from_mask(m, CYAN, 0.05, 0.85, size))
    img.alpha_composite(glyph)

    # arrow: shaft + head, pointing right into the ring centre
    aw = size*0.060
    g = ImageDraw.Draw(img, "RGBA")
    g.rounded_rectangle([cx-size*0.145, cy-aw/2, cx+size*0.005, cy+aw/2], radius=aw/2, fill=WHITE)
    g.polygon([(cx+size*0.010, cy-size*0.105), (cx+size*0.150, cy), (cx+size*0.010, cy+size*0.105)],
              fill=WHITE)
    return img

def c5_nodes(size):
    """Two session nodes joined by a luminous bridge."""
    img = make_plate(size)
    cy = size*0.5
    lx, rx = size*0.285, size*0.715
    R, W = size*0.140, size*0.040
    glyph = layer(size)
    # bridge
    m = shape_mask(size, lambda d: d.rounded_rectangle(
        [lx, cy-size*0.026, rx, cy+size*0.026], radius=size*0.026, fill=255))
    glyph.alpha_composite(fill_gradient_mask(size, m, (lx, cy), (rx, cy),
                                             [(0.0, CYAN), (1.0, MAGENTA)], 1.0))
    for (x, st) in ((lx, [(0.0, MINT), (1.0, CYAN)]), (rx, [(0.0, PURPLE), (1.0, MAGENTA)])):
        m2 = shape_mask(size, lambda d, x=x: d.ellipse([x-R-W/2, cy-R-W/2, x+R+W/2, cy+R+W/2],
                                                      outline=255, width=int(W)))
        glyph.alpha_composite(fill_gradient_mask(size, m2, (x-R, cy-R), (x+R, cy+R), st, 1.0))
        img.alpha_composite(soft_glow_from_mask(m2, st[-1][1], 0.05, 0.7, size))
    img.alpha_composite(glyph)
    return img

def c6_orbit(size):
    """Open orbit + core + satellite = deploy outward."""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    glyph = layer(size)
    w = int(size*0.046)
    m1 = shape_mask(size, lambda d: d.arc([cx-size*0.285, cy-size*0.285, cx+size*0.285, cy+size*0.285],
                                          start=130, end=400, fill=255, width=w))
    glyph.alpha_composite(fill_gradient_mask(size, m1, (cx-size*0.285, cy-size*0.285),
                                             (cx+size*0.285, cy+size*0.285),
                                             [(0.0, CYAN), (1.0, PURPLE)], 1.0))
    img.alpha_composite(soft_glow_from_mask(m1, CYAN, 0.05, 0.8, size))
    img.alpha_composite(glyph)
    g = ImageDraw.Draw(img, "RGBA")
    r = size*0.095
    g.ellipse([cx-r, cy-r, cx+r, cy+r], fill=WHITE)
    ang = math.radians(130); rr = size*0.285
    sx, sy = cx + rr*math.cos(ang), cy + rr*math.sin(ang)
    sr = size*0.062
    g.ellipse([sx-sr, sy-sr, sx+sr, sy+sr], fill=MAGENTA + (255,))
    return img

CONCEPTS = {
    "1-beam":   c1_beam,
    "2-chain":  c2_chain,
    "3-portal": c3_portal,
    "4-arrow":  c4_arrow,
    "5-nodes":  c5_nodes,
    "6-orbit":  c6_orbit,
}

def render_all(size=C, scale_to=BASE):
    out = {}
    for name, fn in CONCEPTS.items():
        im = fn(size)
        out[name] = im.resize((scale_to, scale_to), Image.LANCZOS)
    return out

if __name__ == "__main__":
    imgs = render_all()
    for k, v in imgs.items():
        v.save(os.path.join(OUT, "_c-%s.png" % k))
    # contact sheet
    n = len(imgs)
    cell = 320
    pad = 16
    sheet = Image.new("RGB", (cell*n + pad*(n+1), cell + 130), (14, 15, 20))
    d = ImageDraw.Draw(sheet)
    try:
        f = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 20)
    except Exception:
        f = ImageFont.load_default()
    for i, (name, im) in enumerate(imgs.items()):
        x = pad + i*(cell+pad)
        big = im.resize((cell, cell), Image.LANCZOS)
        sheet.paste(big, (x, 34), big)
        d.text((x+2, 8), name, font=f, fill=(230, 236, 255))
        for j, sz in enumerate([16, 24, 32, 48, 64]):
            sm = im.resize((sz, sz), Image.LANCZOS)
            sheet.paste(sm, (x + 4 + j*64, cell + 44), sm)
    sheet.save(os.path.join(OUT, "concepts.png"))
    print("OK", sheet.size)
