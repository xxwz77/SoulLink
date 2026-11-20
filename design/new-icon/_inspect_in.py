
import os
from PIL import Image

src = r"E:\Documents\deepseek-harness\SoulLink\Assets\soullink.ico"
out = r"E:\Documents\deepseek-harness\SoulLink\design\new-icon\_inspect"
os.makedirs(out, exist_ok=True)

im = Image.open(src)
print("ico sizes:", sorted(im.ico.sizes()) if hasattr(im, "ico") else "n/a")
print("mode:", im.mode, "size:", im.size)

# extract each frame
sizes = sorted(im.ico.sizes())
for s in sizes:
    try:
        fr = im.ico.getimage(s).convert("RGBA")
        if s[0] >= 256:
            fr.save(os.path.join(out, "current-256.png"))
    except Exception as e:
        print("err", s, e)

big = im.ico.getimage((256,256)).convert("RGBA") if (256,256) in sizes else im.convert("RGBA")
big.save(os.path.join(out, "current-max.png"))
print("saved", os.listdir(out))
