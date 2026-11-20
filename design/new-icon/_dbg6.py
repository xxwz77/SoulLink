# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import numpy as np
from PIL import Image
import _concepts as M

size = 512
cx = cy = size*0.5
r, w, op = size*0.330, size*0.020, 0.55

m = M.shape_mask(size, lambda d, rr=r, ww=w: d.ellipse(
    [cx-rr-ww/2, cy-rr-ww/2, cx+rr+ww/2, cy+rr+ww/2], outline=255, width=int(ww)))

t = M.lgrad(size, size, (cx-r, cy-r), (cx+r, cy+r))
print("t shape", t.shape, "min/max", t.min(), t.max())
rgb = M.ramp_field(t, [(0.0, M.CYAN), (1.0, M.PURPLE)])
print("rgb shape", rgb.shape, rgb.dtype, "max", rgb.max())
a = (np.asarray(m, dtype=np.float32)/255.0) * op
print("a shape", a.shape, "max", a.max(), "nonzero", int((a>0).sum()))
outarr = np.dstack([rgb, a[..., None]]).astype(np.uint8)
print("out shape", outarr.shape, "alpha max", outarr[...,3].max())
im = Image.fromarray(outarr, "RGBA")
print("im mode", im.mode, "arr alpha max", np.asarray(im)[...,3].max())
