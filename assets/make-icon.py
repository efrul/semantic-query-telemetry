# The tool's icon: the olive branch of Zayit's logo on a cream tile, with a gold badge of an upward send arrow and tray
# (sending search telemetry & feedback). Zayit Move and Zayit Export have the same tile with their own badges.
# python make-icon.py [LOGO.png]  ->  app.ico, app.png and app-256.png next to this script
import sys, os
from PIL import Image, ImageDraw

here = os.path.dirname(os.path.abspath(__file__))
if len(sys.argv) > 1 and os.path.isfile(sys.argv[1]):
    logo = Image.open(sys.argv[1]).convert("RGBA")
    branch = logo.crop((915, 0, logo.width, logo.height))
    branch = branch.crop(branch.getbbox())
else:
    branch = Image.open(os.path.join(here, "branch.png")).convert("RGBA")
    branch = branch.crop(branch.getbbox())

S = 1024
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle((24, 24, S - 24, S - 24), radius=220, fill=255)
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / S
    c = tuple(int(a * (1 - t) + b * t) for a, b in zip((252, 248, 236), (240, 226, 190)))
    gd.line((0, y, S, y), fill=c + (255,))
tile.paste(grad, (0, 0), mask)
ImageDraw.Draw(tile).rounded_rectangle((24, 24, S - 24, S - 24), radius=220, outline=(190, 145, 23, 255), width=14)

b = branch.copy()
h = 760
b = b.resize((int(b.width * h / b.height), h), Image.LANCZOS)
tile.alpha_composite(b, ((S - b.width) // 2 + 60, (S - h) // 2 - 40))

# the badge: a gold circle with an open tray at the bottom and an upward arrow sending out of it
cx, cy, r = 265, 760, 205
badge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(badge)
d.ellipse((cx - r - 14, cy - r - 14, cx + r + 14, cy + r + 14), fill=(255, 255, 255, 255))
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(190, 145, 23, 255))

# tray at bottom of badge
tw, th = 210, 80
tx0, ty0 = cx - tw // 2, cy + 25
d.line([(tx0, ty0), (tx0, ty0 + th), (tx0 + tw, ty0 + th), (tx0 + tw, ty0)], fill="white", width=26, joint="curve")

# upward arrow shaft and head
d.line((cx, cy + 52, cx, cy - 82), fill="white", width=32)
d.polygon([(cx, cy - 138), (cx - 68, cy - 54), (cx + 68, cy - 54)], fill="white")
tile.alpha_composite(badge)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [tile.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[-1].save(os.path.join(here, "app.ico"), sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
tile.resize((64, 64), Image.LANCZOS).save(os.path.join(here, "app.png"))
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "app-256.png"))
print("ok")
