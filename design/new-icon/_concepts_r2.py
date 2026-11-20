# -*- coding: utf-8 -*-
"""SoulLink icon - round 2: concepts focused on 'deliver into another session'."""
import os, math, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import _concepts as M
from _concepts import (CYAN, ICE, PURPLE, MAGENTA, MINT, WHITE, BLUE,
                       shape_mask, fill_gradient_mask, soft_glow_from_mask, layer,
                       make_plate, _norm)

OUT = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon"
SS = 3
BASE = 1024
C = BASE * SS

def ring_mask(size, cx, cy, r, w):
    return shape_mask(size, lambda d: d.ellipse([cx-r-w/2, cy-r-w/2, cx+r+w/2, cy+r+w/2],
                                                outline=255, width=int(w)))

def arc_mask(size, cx, cy, r, w, start, end):
    return shape_mask(size, lambda d: d.arc([cx-r-w/2, cy-r-w/2, cx+r+w/2, cy+r+w/2],
                                            start=start, end=end, fill=255, width=int(w)))

def arrow_mask(size, x0, x1, cy, shaft_w, head_w, head_len):
    def fn(d):
        d.rounded_rectangle([x0, cy-shaft_w/2, x1, cy+shaft_w/2], radius=shaft_w/2, fill=255)
        d.polygon([(x1, cy-head_w/2), (x1+head_len, cy), (x1, cy+head_w/2)], fill=255)
    return shape_mask(size, fn)

def grad(size, mask, p0, p1, stops, opacity=1.0):
    return fill_gradient_mask(size, mask, p0, p1, stops, opacity)

def glow(size, mask, color, blur=0.05, gain=0.85):
    return soft_glow_from_mask(mask, color, blur, gain, size)

# ---------------------------------------------------------------- 1. arrow into ring
def v2_in(size):
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    R, W = size*0.268, size*0.052
    m = ring_mask(size, cx, cy, R, W)
    g1 = grad(size, m, (cx-R, cy-R), (cx+R, cy+R), [(0.0, CYAN), (0.5, ICE), (1.0, PURPLE)])
    am = arrow_mask(size, cx-size*0.300, cx+size*0.060, cy, size*0.070, size*0.190, size*0.105)
    g2 = grad(size, am, (cx-size*0.30, cy), (cx+size*0.17, cy), [(0.0, MINT), (1.0, WHITE)])
    img.alpha_composite(glow(size, m, CYAN, 0.055, 0.75))
    img.alpha_composite(glow(size, am, MINT, 0.045, 0.55))
    img.alpha_composite(g1); img.alpha_composite(g2)
    return img

# ---------------------------------------------------------------- 2. arrow through ring
def v2_through(size):
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    R, W = size*0.238, size*0.058
    m = ring_mask(size, cx, cy, R, W)
    g1 = grad(size, m, (cx-R, cy-R), (cx+R, cy+R), [(0.0, CYAN), (1.0, PURPLE)])
    # arrow crossing entirely, tip past the ring on the right
    x0 = cx - size*0.355
    x1 = cx + size*0.055
    am = arrow_mask(size, x0, x1, cy, size*0.072, size*0.198, size*0.110)
    g2 = grad(size, am, (x0, cy), (x1+size*0.11, cy), [(0.0, MINT), (0.55, ICE), (1.0, WHITE)])
    img.alpha_composite(glow(size, m, CYAN, 0.055, 0.7))
    img.alpha_composite(glow(size, am, CYAN, 0.05, 0.7))
    img.alpha_composite(g1); img.alpha_composite(g2)
    return img

# ---------------------------------------------------------------- 3. tunnel / portal depth
def v2_tunnel(size):
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    rings = [(0.300, 0.046, 0.55), (0.212, 0.040, 0.80), (0.126, 0.034, 1.0)]
    stopsets = [(CYAN, PURPLE), (CYAN, MAGENTA), (ICE, MAGENTA)]
    for (r, w, op), st in zip(rings, stopsets):
        r *= size; w *= size
        m = ring_mask(size, cx, cy, r, w)
        img.alpha_composite(glow(size, m, st[0], 0.05, 0.45*op))
        img.alpha_composite(grad(size, m, (cx-r, cy-r), (cx+r, cy+r),
                                 [(0.0, st[0]), (1.0, st[1])], op))
    # luminous core
    m = shape_mask(size, lambda d: d.ellipse([cx-size*0.062, cy-size*0.062,
                                              cx+size*0.062, cy+size*0.062], fill=255))
    img.alpha_composite(glow(size, m, WHITE, 0.06, 0.9))
    img.alpha_composite(grad(size, m, (cx, cy-size*0.062), (cx, cy+size*0.062),
                             [(0.0, WHITE), (1.0, ICE)]))
    return img

# ---------------------------------------------------------------- 4. two interlocked session rings
def v2_interlock(size):
    img = make_plate(size)
    cy = size*0.5
    la, ra = size*0.148, size*0.148
    lx, rx = size*0.348, size*0.652
    W = size*0.056
    ml = ring_mask(size, lx, cy, la, W)
    mr = ring_mask(size, rx, cy, ra, W)
    gl = grad(size, ml, (lx-la, cy-la), (lx+la, cy+la), [(0.0, MINT), (1.0, CYAN)])
    gr = grad(size, mr, (rx-ra, cy-ra), (rx+ra, cy+ra), [(0.0, PURPLE), (1.0, MAGENTA)])
    # erase where the right ring crosses the left ring's band (right over left)
    A = np.asarray(gl).copy()
    ys, xs = np.mgrid[0:size, 0:size]
    cross = (np.abs(xs - lx) < W*0.85) & (np.abs(ys - cy) < la + W)
    # only erase the left ring near the crossing points (top & bottom where right ring passes)
    bandx = (xs > lx) & (xs < lx + W*1.3)
    A[..., 3] = np.where(bandx & (np.abs(ys-cy) > la - W*1.15), 0, A[..., 3])
    gl = Image.fromarray(A, "RGBA")
    img.alpha_composite(glow(size, ml, CYAN, 0.05, 0.6))
    img.alpha_composite(glow(size, mr, MAGENTA, 0.05, 0.6))
    img.alpha_composite(gl); img.alpha_composite(gr)
    return img

# ---------------------------------------------------------------- 5. rounded-square session + arrow in
def v2_window(size):
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    W = size*0.236; H = size*0.246
    lw = int(size*0.058); rad = size*0.098
    m = shape_mask(size, lambda d: d.rounded_rectangle([cx-W, cy-H, cx+W, cy+H],
                                                       radius=rad, outline=255, width=lw))
    g1 = grad(size, m, (cx-W, cy-H), (cx+W, cy+H), [(0.0, CYAN), (0.45, PURPLE), (1.0, MAGENTA)])
    am = arrow_mask(size, cx-size*0.175, cx-size*0.020, cy, size*0.062, size*0.155, size*0.090)
    g2 = grad(size, am, (cx-size*0.175, cy), (cx+size*0.070, cy), [(0.0, MINT), (1.0, WHITE)])
    img.alpha_composite(glow(size, m, PURPLE, 0.05, 0.8))
    img.alpha_composite(glow(size, am, MINT, 0.045, 0.5))
    img.alpha_composite(g1); img.alpha_composite(g2)
    return img

# ---------------------------------------------------------------- 6. S-link monogram
def v2_slink(size):
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    w = int(size*0.085)
    # upper arc (left-opening) + lower arc (right-opening) = S
    R = size*0.175
    top = arc_mask(size, cx, cy-R*0.62, R, w, 30, 320)
    bot = arc_mask(size, cx, cy+R*0.62, R, w, 210, 500)
    gt = grad(size, top, (cx-R, cy-R*2), (cx+R, cy), [(0.0, CYAN), (1.0, ICE)])
    gb = grad(size, bot, (cx-R, cy), (cx+R, cy+R*2), [(0.0, PURPLE), (1.0, MAGENTA)])
    for m, c in ((top, CYAN), (bot, MAGENTA)):
        img.alpha_composite(glow(size, m, c, 0.05, 0.55))
    img.alpha_composite(gt); img.alpha_composite(gb)
    return img

CONCEPTS = {
    "r2-in":       v2_in,
    "r2-through":  v2_through,
    "r2-tunnel":   v2_tunnel,
    "r2-interlock":v2_interlock,
    "r2-window":   v2_window,
    "r2-slink":    v2_slink,
}

if __name__ == "__main__":
    imgs = {k: fn(C).resize((BASE, BASE), Image.LANCZOS) for k, fn in CONCEPTS.items()}
    for k, v in imgs.items():
        v.save(os.path.join(OUT, "_%s.png" % k))
    n = len(imgs); cell = 320; pad = 16
    sheet = Image.new("RGB", (cell*n + pad*(n+1), cell + 140), (14, 15, 20))
    d = ImageDraw.Draw(sheet)
    try:
        f = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 20)
    except Exception:
        f = ImageFont.load_default()
    for i, (name, im) in enumerate(imgs.items()):
        x = pad + i*(cell+pad)
        big = im.resize((cell, cell), Image.LANCZOS); sheet.paste(big, (x, 34), big)
        d.text((x+2, 8), name, font=f, fill=(230, 236, 255))
        for j, sz in enumerate([16, 24, 32, 48, 64]):
            sm = im.resize((sz, sz), Image.LANCZOS)
            sheet.paste(sm, (x + 4 + j*62, cell + 44), sm)
    sheet.save(os.path.join(OUT, "concepts-r2.png"))
    print("OK", sheet.size)
