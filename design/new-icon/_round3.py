# -*- coding: utf-8 -*-
"""SoulLink icon - round 3: refine for small-size legibility."""
import os, sys, math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
from _concepts import (CYAN, ICE, PURPLE, MAGENTA, MINT, WHITE,
                       shape_mask, fill_gradient_mask, soft_glow_from_mask, _norm)
from _hero import make_plate, ring_mask, grad, glow

OUT = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon"
BASE = 1024
C = BASE * 3

def arrow(size, x0, tip, cy, shaft_w, head_w, head_len):
    m = shape_mask(size, lambda d: d.rounded_rectangle(
        [x0, cy-shaft_w/2, tip, cy+shaft_w/2], radius=shaft_w/2, fill=255))
    h = shape_mask(size, lambda d: d.polygon(
        [(tip, cy-head_w/2), (tip+head_len, cy), (tip, cy+head_w/2)], fill=255))
    out = Image.new("L", (size, size), 0)
    out.paste(m, (0,0), m); out.paste(h, (0,0), h)
    return out

def ring_on_top(size, base_img, ring_g, cx, cy, R, W, arrow_mask):
    """redraw only the ring pixels that overlap the arrow => arrow passes behind ring."""
    ys, xs = np.mgrid[0:size, 0:size]
    rr = np.sqrt((xs-cx)**2 + (ys-cy)**2)
    band = (np.abs(rr-R) < W*0.55)
    am = np.asarray(arrow_mask) > 0
    A = np.asarray(ring_g).copy()
    A[..., 3] = np.where(band & am, A[..., 3], 0)
    base_img.alpha_composite(Image.fromarray(A, "RGBA"))

# ---------- A: arrow passes BEHIND ring (depth: delivery into session)
def vA(size):
    img = make_plate(size); cx = cy = size*0.5
    R, W = size*0.225, size*0.076
    ringm = ring_mask(size, cx, cy, R, W)
    ring_g = grad(size, ringm, (cx-R, cy-R), (cx+R, cy+R),
                  [(0.0, CYAN), (0.45, ICE), (0.75, PURPLE), (1.0, MAGENTA)])
    am = arrow(size, cx-size*0.290, cx+size*0.070, cy, size*0.090, size*0.235, size*0.115)
    a_g = grad(size, am, (cx-size*0.29, cy), (cx+size*0.185, cy),
               [(0.0, MINT), (0.35, CYAN), (1.0, WHITE)])
    img.alpha_composite(glow(size, ringm, CYAN, 0.055, 0.60))
    img.alpha_composite(glow(size, am, CYAN, 0.05, 0.45))
    img.alpha_composite(a_g)
    img.alpha_composite(ring_g)
    ring_on_top(size, img, ring_g, cx, cy, R, W, am)
    return img

# ---------- B: arrow fully INSIDE ring, smaller
def vB(size):
    img = make_plate(size); cx = cy = size*0.5
    R, W = size*0.258, size*0.072
    ringm = ring_mask(size, cx, cy, R, W)
    ring_g = grad(size, ringm, (cx-R, cy-R), (cx+R, cy+R),
                  [(0.0, CYAN), (0.45, ICE), (0.75, PURPLE), (1.0, MAGENTA)])
    am = arrow(size, cx-size*0.135, cx+size*0.020, cy, size*0.084, size*0.205, size*0.100)
    a_g = grad(size, am, (cx-size*0.135, cy), (cx+size*0.12, cy),
               [(0.0, MINT), (0.5, ICE), (1.0, WHITE)])
    img.alpha_composite(glow(size, ringm, CYAN, 0.055, 0.60))
    img.alpha_composite(glow(size, am, ICE, 0.05, 0.45))
    img.alpha_composite(ring_g); img.alpha_composite(a_g)
    return img

# ---------- C: thick donut with arrow knocked out (negative space)
def vC(size):
    img = make_plate(size); cx = cy = size*0.5
    R, W = size*0.255, size*0.115
    ringm = ring_mask(size, cx, cy, R, W)
    ring_g = grad(size, ringm, (cx-R, cy-R), (cx+R, cy+R),
                  [(0.0, CYAN), (0.45, ICE), (0.8, PURPLE), (1.0, MAGENTA)])
    am = arrow(size, cx-size*0.42, cx+size*0.02, cy, size*0.088, size*0.230, size*0.112)
    # knock out alpha where arrow overlaps ring
    A = np.asarray(ring_g).copy()
    A[..., 3] = np.where(np.asarray(am) > 0, 0, A[..., 3])
    ring_g = Image.fromarray(A, "RGBA")
    img.alpha_composite(glow(size, ringm, CYAN, 0.06, 0.55))
    img.alpha_composite(ring_g)
    # add arrow as white on top, but only outside ring
    ys, xs = np.mgrid[0:size, 0:size]
    rr = np.sqrt((xs-cx)**2 + (ys-cy)**2)
    outside = ~((np.abs(rr-R) < W*0.5))
    a_g = grad(size, am, (cx-size*0.42, cy), (cx+size*0.14, cy),
               [(0.0, MINT), (0.4, CYAN), (1.0, WHITE)])
    A2 = np.asarray(a_g).copy()
    A2[..., 3] = np.where(outside, A2[..., 3], 0)
    img.alpha_composite(Image.fromarray(A2, "RGBA"))
    return img

# ---------- D: two-session: big ring + arrow entering from left, thick & simple
def vD(size):
    img = make_plate(size); cx = cy = size*0.5
    R, W = size*0.235, size*0.085
    ringm = ring_mask(size, cx, cy, R, W)
    ring_g = grad(size, ringm, (cx-R, cy-R), (cx+R, cy+R),
                  [(0.0, CYAN), (1.0, MAGENTA)])
    am = arrow(size, cx-size*0.330, cx+size*0.045, cy, size*0.092, size*0.225, size*0.110)
    a_g = grad(size, am, (cx-size*0.33, cy), (cx+size*0.155, cy),
               [(0.0, WHITE), (1.0, WHITE)])
    img.alpha_composite(glow(size, ringm, CYAN, 0.055, 0.62))
    img.alpha_composite(ring_g); img.alpha_composite(a_g)
    return img

CONCEPTS = {"A-behind": vA, "B-inside": vB, "C-cutout": vC, "D-simple": vD}

if __name__ == "__main__":
    imgs = {k: fn(C).resize((BASE, BASE), Image.LANCZOS) for k, fn in CONCEPTS.items()}
    for k, v in imgs.items():
        v.save(os.path.join(OUT, "_r3-%s.png" % k))

    # TWO-ROW sheet: big (256) then real small sizes on dark + light
    cell, pad = 256, 18
    n = len(imgs)
    H = (cell + 120)*2 + 30
    Wd = n*(cell+pad) + pad
    sheet = Image.new("RGB", (Wd, H), (18, 19, 24))
    d = ImageDraw.Draw(sheet)
    try:
        f = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 18)
    except Exception:
        f = ImageFont.load_default()
    for i, (name, im) in enumerate(imgs.items()):
        x = pad + i*(cell+pad)
        d.text((x, 6), name, font=f, fill=(230, 236, 255))
        big = im.resize((cell, cell), Image.LANCZOS)
        sheet.paste(big, (x, 30), big)
        # real small sizes, dark strip
        y = cell + 46
        for j, sz in enumerate([16, 20, 24, 32, 48, 64]):
            sm = im.resize((sz, sz), Image.LANCZOS)
            sheet.paste(sm, (x + 6 + j*40, y), sm)
        # same on light
        light = Image.new("RGB", (cell, 74), (243, 245, 250))
        for j, sz in enumerate([16, 20, 24, 32, 48, 64]):
            sm = im.resize((sz, sz), Image.LANCZOS)
            light.paste(sm, (6 + j*40, 0), sm)
        sheet.paste(light, (x, y + 92))
    sheet.save(os.path.join(OUT, "round3.png"))
    print("OK", sheet.size)
