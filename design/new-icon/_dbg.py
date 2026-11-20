# -*- coding: utf-8 -*-
import numpy as np
from PIL import Image, ImageDraw

def shape_mask(size, fn):
    m = Image.new("L", (size, size), 0)
    fn(ImageDraw.Draw(m))
    return m

def _norm(c): return np.array(c, dtype=np.float32)

def lgrad(w, h, p0, p1):
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    dx, dy = p1[0]-p0[0], p1[1]-p0[1]
    ln = dx*dx + dy*dy
    t = ((xs - p0[0]) * dx + (ys - p0[1]) * dy) / ln
    return np.clip(t, 0.0, 1.0)

def ramp_field(t, stops):
    t = np.asarray(t, dtype=np.float32)
    cols = [_norm(c) for _, c in stops]
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

def fill_gradient_mask(size, mask, p0, p1, stops, opacity=1.0):
    t = lgrad(size, size, p0, p1)
    rgb = ramp_field(t, stops)
    a = (np.asarray(mask, dtype=np.float32)/255.0) * opacity
    out = np.dstack([rgb, a[..., None]]).astype(np.uint8)
    return Image.fromarray(out, "RGBA")

s = 512
m = shape_mask(s, lambda d: d.ellipse([100,100,400,400], outline=255, width=40))
print("mask mode", m.mode, "sum", np.asarray(m).sum())

g = fill_gradient_mask(s, m, (100,100),(400,400), [(0.0,(79,227,255)),(1.0,(139,107,255))], 1.0)
ga = np.asarray(g)
print("glyph", g.mode, g.size, ga.shape, "alphaSum", ga[...,3].sum(), "rgbMax", ga[...,:3].max())

base = Image.new("RGBA", (s,s), (10,10,10,255))
base.alpha_composite(g)
print("after composite alpha sum", np.asarray(base)[...,3].sum())
