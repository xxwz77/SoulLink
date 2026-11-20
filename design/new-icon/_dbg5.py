# -*- coding: utf-8 -*-
import sys, traceback
sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import numpy as np
from PIL import Image
import _concepts as M

size = 512
src = M.c1_beam.__code__
print("c1_beam names:", src.co_names[:20])

fns = [n for n in dir(M) if n.startswith("c")]
print("concept fns:", fns)

img = M.make_plate(size)
print("plate ok")

cx = cy = size*0.5
glyph = M.layer(size)
print("glyph init alpha max:", np.asarray(glyph)[...,3].max())

try:
    for (r, w, op) in [(size*0.330, size*0.020, 0.55), (size*0.250, size*0.018, 0.80)]:
        m = M.shape_mask(size, lambda d, r=r, w=w: d.ellipse(
            [cx-r-w/2, cy-r-w/2, cx+r+w/2, cy+r+w/2], outline=255, width=int(w)))
        print("ring mask r=", r, "nonzero", int((np.asarray(m)>0).sum()))
        layer_img = M.fill_gradient_mask(size, m, (cx-r, cy-r), (cx+r, cy+r),
                                         [(0.0, M.CYAN), (1.0, M.PURPLE)], op)
        print("  gradient alpha max", np.asarray(layer_img)[...,3].max())
        glyph.alpha_composite(layer_img)
        print("  glyph alpha max now", np.asarray(glyph)[...,3].max())
except Exception:
    traceback.print_exc()

print("glyph final alpha nonzero:", int((np.asarray(glyph)[...,3]>0).sum()))
