# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import numpy as np
from PIL import Image
import _concepts as M

out = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon"
for size in (512, 1024):
    im = M.c1_beam(size)
    im.save(out + r"\_d%d.png" % size)
    a = np.asarray(im).astype(int)
    # sample a horizontal line through center
    row = a[size//2, :, :3].mean(axis=1)
    print("size", size, "centerRow max", row.max(), "at x", int(row.argmax()),
          "centerPixel", a[size//2, size//2, :3])
    col = a[:, size//2, :3].mean(axis=1)
    print("   centerCol max", col.max(), "at y", int(col.argmax()))
