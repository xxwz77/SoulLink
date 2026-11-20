# -*- coding: utf-8 -*-
import sys, os
sys.path.insert(0, r"E:\Documents\deepseek-harness\SoulLink\design\new-icon")
import numpy as np
from PIL import Image
import _concepts as M

for size in (512, 1024, 3072):
    try:
        im = M.c1_beam(size)
        a = np.asarray(im)
        # measure brightness in the ring band region
        print("size", size, "mode", im.mode, "alphaSum", int(a[...,3].sum()),
              "unique", len(np.unique(a.reshape(-1,4), axis=0)))
        im.resize((256,256), Image.LANCZOS).save(r"E:\Documents\deepseek-harness\SoulLink\design\new-icon\_t%d.png" % size)
    except Exception as e:
        import traceback; traceback.print_exc()
