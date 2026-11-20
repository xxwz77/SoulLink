# -*- coding: utf-8 -*-
"""SoulLink icon - hero refinement: arrow interlocking with session ring."""
import os, sys, math
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
from _concepts import (CYAN, ICE, PURPLE, MAGENTA, MINT, WHITE,
                       shape_mask, fill_gradient_mask, soft_glow_from_mask, layer,
                       _norm)

OUT = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon"
SS = 3
BASE = 1024
C = BASE * SS

BG_TOP = (16, 21, 54)
BG_BOT = (5, 7, 19)

def vgrad(w, h, top, bot):
    t = np.linspace(0.0, 1.0, h, dtype=np.float32)[:, None, None]
    a, b = _norm(top), _norm(bot)
    arr = a[None, None, :]*(1-t) + b[None, None, :]*t
    return np.repeat(arr, w, axis=1)

def radial_alpha(w, h, cx, cy, r, power=2.4):
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    d = np.sqrt((xs-cx)**2 + (ys-cy)**2)/max(r, 1e-6)
    return np.clip(1.0-d, 0.0, 1.0)**power

def make_plate(size, aurora=1.0, edge=1.0, radius_ratio=0.2235):
    rgb = vgrad(size, size, BG_TOP, BG_BOT)
    for (cx, cy, r, col, pk) in [
        (size*0.18, size*0.10, size*0.66, CYAN,    0.30*aurora),
        (size*0.92, size*0.30, size*0.62, PURPLE,  0.36*aurora),
        (size*0.76, size*1.00, size*0.58, MAGENTA, 0.26*aurora),
    ]:
        a = (radial_alpha(size, size, cx, cy, r, 2.6)*pk)[..., None]
        rgb = rgb*(1-a) + _norm(col)[None, None, :]*a
    # subtle top sheen
    yy = np.mgrid[0:size, 0:size][0].astype(np.float32)/size
    sheen = np.clip(1.0 - yy/0.40, 0, 1)**2 * 0.09
    rgb = rgb + _norm(WHITE)[None, None, :]*sheen[..., None]
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size-1, size-1],
                                           radius=int(size*radius_ratio), fill=255)
    img.putalpha(mask)
    return img

def ring_mask(size, cx, cy, r, w):
    return shape_mask(size, lambda d: d.ellipse([cx-r-w/2, cy-r-w/2, cx+r+w/2, cy+r+w/2],
                                                outline=255, width=int(w)))

def arrow_parts(size, x0, tip_x, cy, shaft_w, head_w, head_len):
    shaft = shape_mask(size, lambda d: d.rounded_rectangle(
        [x0, cy-shaft_w/2, tip_x, cy+shaft_w/2], radius=shaft_w/2, fill=255))
    headw = shape_mask(size, lambda d: d.polygon(
        [(tip_x, cy-head_w/2), (tip_x+head_len, cy), (tip_x, cy+head_w/2)], fill=255))
    return shaft, headw

def grad(size, mask, p0, p1, stops, opacity=1.0):
    return fill_gradient_mask(size, mask, p0, p1, stops, opacity)

def glow(size, mask, color, blur=0.05, gain=0.85):
    return soft_glow_from_mask(mask, color, blur, gain, size)

def compose(size, mode="interlock"):
    """mode: 'interlock' (head overlaps ring) | 'through' (pierces ring)"""
    img = make_plate(size)
    cx, cy = size*0.5, size*0.5
    R = size*0.212
    W = size*0.068

    ringm = ring_mask(size, cx, cy, R, W)
    ring_g = grad(size, ringm, (cx-R, cy-R), (cx+R, cy+R),
                  [(0.0, CYAN), (0.42, ICE), (0.72, PURPLE), (1.0, MAGENTA)])

    ax0 = cx - size*0.300
    atip = cx + size*0.028
    ahl = size*0.118
    ahw = size*0.215
    asw = size*0.082
    sh, hd = arrow_parts(size, ax0, atip, cy, asw, ahw, ahl)
    arrow_g = grad(size, sh, (ax0, cy), (atip+ahl, cy),
                   [(0.0, MINT), (0.34, CYAN), (0.78, ICE), (1.0, WHITE)])
    head_g = grad(size, hd, (atip, cy-ahw/2), (atip+ahl, cy+ahw/2),
                  [(0.0, ICE), (1.0, WHITE)])

    # glows first
    img.alpha_composite(glow(size, ringm, CYAN, 0.055, 0.62))
    img.alpha_composite(glow(size, sh, MINT, 0.05, 0.50))
    img.alpha_composite(glow(size, hd, ICE, 0.05, 0.45))

    if mode == "interlock":
        # ring behind, arrow in front, but the ring's right stroke is redrawn
        # over the arrow shaft only where it crosses -> true weave
        img.alpha_composite(ring_g)
        img.alpha_composite(arrow_g)
        img.alpha_composite(head_g)
        # reassert a thin arc of ring over the arrow on the left crossing
        ys, xs = np.mgrid[0:size, 0:size]
        rr = np.sqrt((xs-cx)**2 + (ys-cy)**2)
        band = (np.abs(rr-R) < W*0.5) & (xs < cx) & (np.abs(ys-cy) < W*0.9)
        A = np.asarray(ring_g).copy()
        A[..., 3] = np.where(band, A[..., 3], 0)
        img.alpha_composite(Image.fromarray(A, "RGBA"))
    else:
        img.alpha_composite(arrow_g)
        img.alpha_composite(ring_g)
        img.alpha_composite(head_g)
    return img

if __name__ == "__main__":
    imgs = {}
    for mode in ("interlock", "through"):
        imgs[mode] = compose(C, mode).resize((BASE, BASE), Image.LANCZOS)
        imgs[mode].save(os.path.join(OUT, "_hero-%s.png" % mode))

    # evaluation sheet: dark bg, light bg, small sizes
    cell = 300; pad = 20
    cols = 4
    sheet = Image.new("RGB", (cell*cols + pad*(cols+1), 2*(cell+150) + 40), (18, 19, 24))
    d = ImageDraw.Draw(sheet)
    try:
        f  = ImageFont.truetype("C:/Windows/Fonts/msyhbd.ttc", 20)
        fl = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 15)
    except Exception:
        f = fl = ImageFont.load_default()

    for row, (name, im) in enumerate(imgs.items()):
        y = row*(cell+150) + 40
        dark = Image.new("RGB", (cell, cell), (32, 34, 40))
        light = Image.new("RGB", (cell, cell), (240, 242, 248))
        big = im.resize((cell, cell), Image.LANCZOS)
        dark.paste(big, (0, 0), big)
        light.paste(big, (0, 0), big)
        sheet.paste(dark,  (pad, y))
        sheet.paste(light, (pad*2+cell, y))
        # windows taskbar strip (48px icon on dark)
        bar = Image.new("RGB", (cell*2+pad, 72), (28, 30, 36))
        for j, sz in enumerate([16, 24, 32, 48]):
            sm = im.resize((sz, sz), Image.LANCZOS)
            bar.paste(sm, (12 + j*80, 12), sm)
        sheet.paste(bar, (pad*3+cell*2, y))
        d.text((pad, y-28), name, font=f, fill=(235, 240, 255))
        d.text((pad, y+cell+6), "dark bg", font=fl, fill=(160, 170, 195))
        d.text((pad*2+cell, y+cell+6), "light bg", font=fl, fill=(160, 170, 195))
        d.text((pad*3+cell*2, y+cell+6), "16 / 24 / 32 / 48 px", font=fl, fill=(160, 170, 195))
    sheet.save(os.path.join(OUT, "hero-compare.png"))
    print("OK", sheet.size)
