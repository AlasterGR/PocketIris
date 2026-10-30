r"""Draws the app icon: a white regular 8-pointed star {8/3} inside a circle through its tips, the 8 spaces between the rays in the
rainbow's colours (red, orange, yellow, green, blue, indigo, violet, and rose to close the wheel), on a night-blue background.
Writes the adaptive icon layers and the legacy icon for every screen density into app/Resources. Run: python tools/make-icon.py"""
import math, os
from PIL import Image, ImageDraw

#  The app's resource folder, found from where this script lives (tools/ is next to app/).
R = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "app", "Resources") + os.sep
NIGHT = (24, 26, 38, 255)
WHITE = (255, 255, 255, 255)
COLOURS = [(229, 57, 53), (251, 140, 0), (253, 216, 53), (67, 160, 71), (30, 136, 229), (57, 73, 171), (142, 36, 170), (216, 27, 96)]
S = 8  # drawn 8 times larger, then reduced, for smooth edges
#  For the regular {8/3} star the notches sit at cos(3*pi/8) / cos(pi/8) of the tip radius.
INNER = math.cos(3 * math.pi / 8) / math.cos(math.pi / 8)
TURN = -math.pi / 2  # one tip straight up


def star(size, scale):
    """The emblem on a transparent square: coloured slices between the tips, the white star, the white circle through the tips."""
    n = size * S
    im = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    c, r = n / 2, n * scale
    #  One coloured slice of the circle between each pair of neighbouring tips.
    for k in range(8):
        a0 = math.degrees(TURN + k * math.pi / 4)
        d.pieslice([c - r, c - r, c + r, c + r], a0, a0 + 45, fill=COLOURS[k] + (255,))
    pts = [(c + (r if k % 2 == 0 else r * INNER) * math.cos(TURN + k * math.pi / 8), c + (r if k % 2 == 0 else r * INNER) * math.sin(TURN + k * math.pi / 8)) for k in range(16)]
    d.polygon(pts, fill=WHITE)
    d.ellipse([c - r, c - r, c + r, c + r], outline=WHITE, width=max(2, int(n * 0.012)))
    return im.resize((size, size), Image.LANCZOS)


#  Legacy icon size and adaptive-layer size per density (the adaptive canvas is 108 dp; its safe circle is 66 dp, so the emblem stays within 0.30).
dens = {"mdpi": (48, 108), "hdpi": (72, 162), "xhdpi": (96, 216), "xxhdpi": (144, 324), "xxxhdpi": (192, 432)}
for k, (leg, ad) in dens.items():
    p = R + f"mipmap-{k}/"
    star(ad, 0.28).save(p + "appicon_foreground.png")
    Image.new("RGBA", (ad, ad), NIGHT).save(p + "appicon_background.png")
    #  The legacy icon: the emblem on a rounded night-blue square.
    mask = Image.new("L", (leg * S, leg * S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, leg * S - 1, leg * S - 1], radius=leg * S * 0.22, fill=255)
    mask = mask.resize((leg, leg), Image.LANCZOS)
    base = Image.new("RGBA", (leg, leg), NIGHT)
    base.alpha_composite(star(leg, 0.38))
    full = Image.new("RGBA", (leg, leg), (0, 0, 0, 0))
    full.paste(base, (0, 0), mask)
    full.save(p + "appicon.png")
print("ok")
