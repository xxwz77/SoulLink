# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import numpy as np
from PIL import Image, ImageDraw
import _concepts as M

size = 512
img = M.make_plate(size)
print("plate mode", img.mode, img.size)

cx = cy = size/2
R, W = size*0.170, size*0.042
m = M.shape_mask(size, lambda d: d.ellipse([cx-R-W/2, cy-R-W/2, cx+R+W/2, cy+R+W/2], outline=255, width=int(W)))
arr = np.asarray(m)
print("mask nonzero:", int((arr>0).sum()))

g = M.fill_gradient_mask(size, m, (cx-R, cy-R), (cx+R, cy+R), [(0.0, M.CYAN), (1.0, M.MAGENTA)], 1.0)
ga = np.asarray(g)
print("grad alpha nonzero:", int((ga[...,3]>0).sum()), "rgb mean where alpha>0:", ga[ga[...,3]>0][:, :3].mean(axis=0))

before = np.asarray(img).copy()
img.alpha_composite(g)
after = np.asarray(img)
diff = np.abs(after.astype(int) - before.astype(int)).sum()
print("pixel diff after composite:", int(diff))

# try paste instead
img2 = M.make_plate(size)
img2.paste(g, (0,0), g)
print("paste ok")
