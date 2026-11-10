# The tool's icon, in Zayit's and Pardes's style: a gold frame round a black tile with a lighter centre, as Zayit's,
# and in it a gold magnifying glass with an upward send arrow in its lens (searches, sent), gold in a vertical gradient
# as Pardes's letters. The window's title bar and header show it too.
# python make-icon.py  ->  app.ico, app.png and app-256.png next to this script
import os
from PIL import Image, ImageDraw, ImageFilter

here = os.path.dirname(os.path.abspath(__file__))
S = 1024
GOLD_TOP, GOLD_BOT = (246, 220, 140), (186, 137, 46)


def radial(center, edge):
    g = Image.new("RGBA", (S, S))
    px = g.load()
    c = S / 2
    for y in range(S):
        for x in range(S):
            f = min(1.0, (((x - c) ** 2 + (y - c * 0.92) ** 2) ** 0.5) / (c * 1.15))
            px[x, y] = tuple(int(a * (1 - f) + b * f) for a, b in zip(center, edge)) + (255,)
    return g


def vgrad(top, bot):
    g = Image.new("RGBA", (S, S))
    d = ImageDraw.Draw(g)
    for y in range(S):
        f = y / S
        d.line((0, y, S, y), fill=tuple(int(a * (1 - f) + b * f) for a, b in zip(top, bot)) + (255,))
    return g


def rounded(box, radius):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m


# The frame and the tile
tile = Image.new("RGBA", (S, S), (0, 0, 0, 0))
tile.paste(vgrad((253, 236, 166), (214, 170, 76)), (0, 0), rounded((0, 0, S - 1, S - 1), 230))
tile.paste(radial((30, 30, 30), (4, 4, 4)), (0, 0), rounded((62, 62, S - 63, S - 63), 175))

# The glyph: the lens, its handle with round ends, the send arrow
glyph = Image.new("L", (S, S), 0)
d = ImageDraw.Draw(glyph)
cx, cy, r, ring = 450, 440, 220, 66
end, w = (770, 760), 106
k = 0.7071
x0, y0 = cx + (r - 10) * k, cy + (r - 10) * k
d.line((x0, y0, end[0], end[1]), fill=255, width=w)
for x, y in ((x0, y0), end):
    d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=255)
d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)
d.ellipse((cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring), fill=0)
a = r / 250
d.line((cx, cy + 100 * a, cx, cy - 40 * a), fill=255, width=int(60 * a))
d.polygon([(cx, cy - 132 * a), (cx - 90 * a, cy - 22 * a), (cx + 90 * a, cy - 22 * a)], fill=255)

# A soft shadow under it, then the gold
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
shadow.putalpha(glyph.filter(ImageFilter.GaussianBlur(14)).point(lambda v: v * 110 // 255))
tile.alpha_composite(shadow.transform(shadow.size, Image.AFFINE, (1, 0, 0, 0, 1, -10)))
gold = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gold.paste(vgrad(GOLD_TOP, GOLD_BOT), (0, 0), glyph)
tile.alpha_composite(gold)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
imgs = [tile.resize((s, s), Image.LANCZOS) for s in sizes]
imgs[-1].save(os.path.join(here, "app.ico"), sizes=[(s, s) for s in sizes], append_images=imgs[:-1])
tile.resize((64, 64), Image.LANCZOS).save(os.path.join(here, "app.png"))
tile.resize((256, 256), Image.LANCZOS).save(os.path.join(here, "app-256.png"))
print("ok")
