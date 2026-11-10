# The tool's icon: a white magnifying glass with an upward send arrow in its lens (searches, sent), on a techelet tile,
# the open search site's accent (#1f5bc4). The window's title bar and header show it too.
# python make-icon.py  ->  app.ico, app.png and app-256.png next to this script
import os
from PIL import Image, ImageDraw

here = os.path.dirname(os.path.abspath(__file__))
S = 1024
WHITE = (255, 255, 255, 255)

tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((24, 24, S - 24, S - 24), radius=220, fill=255)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / S
    c = tuple(int(a * (1 - t) + b * t) for a, b in zip((46, 112, 214), (24, 78, 172)))
    gd.line((0, y, S, y), fill=c + (255,))
tile.paste(grad, (0, 0), mask)

glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(glyph)

# the lens: a white ring
cx, cy, r, ring = 440, 430, 250, 74
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=WHITE)
d.ellipse((cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring), fill=(0, 0, 0, 0))

# the handle, from the ring down to the right, with round ends
k = 0.7071
x0, y0 = cx + (r - 10) * k, cy + (r - 10) * k
x1, y1 = 812, 802
w = 120
d.line((x0, y0, x1, y1), fill=WHITE, width=w)
for x, y in ((x0, y0), (x1, y1)):
    d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=WHITE)

# the send arrow in the lens
d.line((cx, cy + 98, cx, cy - 40), fill=WHITE, width=58)
d.polygon([(cx, cy - 128), (cx - 86, cy - 22), (cx + 86, cy - 22)], fill=WHITE)

tile.alpha_composite(glyph)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [tile.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[-1].save(os.path.join(here, "app.ico"), sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
tile.resize((64, 64), Image.LANCZOS).save(os.path.join(here, "app.png"))
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "app-256.png"))
print("ok")
