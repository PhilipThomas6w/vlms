"""Varangians rank badges: the generator for the proposed collection and the Phase B alternatives.

Every badge is hand-authored vector geometry (no raster, no fonts, no external references): a shared frame
(merrowed rim, gold keylines, one ornament band per movement, the field) and one central symbol per rank.
See ../README.md for the design rules, palette and symbolism.

Run from anywhere:  python build_badges.py
Writes ../proposed/*.svg, ../exploration/*.svg and the review pages (../contact-sheet.html,
../exploration/exploration-sheet.html). The double-headed eagle (Akolouthos) is read from the app's own
public-domain asset, src/Vlms.Web/wwwroot/img/eagle.svg (docs/design/asset-register.md).
"""
import base64
import math
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", "..", ".."))
OUT = os.path.normpath(os.path.join(HERE, ".."))

# ---------------------------------------------------------------- palette (README section 4)
INK = "#1F1828"        # outline stitch: one dark thread for every badge
GOLD = "#EDB637"       # gold thread
GOLD_SH = "#B47C17"    # gold in shadow
GOLD_HI = "#FFE7A3"    # satin sheen on gold
IVORY = "#F6EBD2"
PEARL = "#FFF8EA"
PEARL_GOLD = "#F4E2B0"  # border pearls: warmer, so the ring reads as jewelled, not dotted
SILVER = "#D9DFE6"
SILVER_SH = "#8D99A8"
CRIMSON = "#A51C2E"
CRIMSON_SH = "#6F1120"
FLAME = "#F08A24"
FLAME_HI = "#FFD25A"

# Field colour, field shade (rim and band ground) and ornament band per rank.
RANKS = [
    # order, code, name, field, shade, band
    (1, "recruit", "Recruit", "#A51C2E", "#5E0F1B", "cable"),
    (2, "guardsman", "Guardsman", "#1E7B4F", "#0E3F28", "cable"),
    (3, "shield-bearer", "Shield-bearer", "#2346A8", "#122459", "cable"),
    (4, "sword-bearer", "Sword-bearer", "#C8431F", "#6A200C", "cable"),
    (5, "herald", "Herald", "#137B93", "#0B4652", "notched"),  # darkened from #178AA0 for 4.9:1 with white text (Phase E)
    (6, "torch-bearer", "Torch-bearer", "#1C1E4C", "#0B0C24", "pearls"),
    (7, "standard-bearer", "Standard-bearer", "#DE9A1E", "#7A4C06", "pearls"),
    (8, "cross-bearer", "Cross-bearer", "#F3E6C6", "#7A1424", "pearls"),
    (9, "christ-bearer", "Christ-bearer", "#7A1736", "#3C0A1A", "jewels"),
    (10, "akolouthos", "Akolouthos", "#4A2A74", "#221238", "jewels"),
]

C = 256          # centre of the 512 x 512 viewBox
R_FIELD = 203    # the field the symbol sits on
SW = 11          # outline stitch width (OAJ: bold, confident ink line)
K = 1.14         # symbol scale about the centre (symbols are drawn on a 190-radius field)


def f(v):
    return f"{v:.1f}".rstrip("0").rstrip(".")


def pts(points):
    return " ".join(f"{f(x)},{f(y)}" for x, y in points)


def rot(p, a, o=(C, C)):
    x, y = p[0] - o[0], p[1] - o[1]
    ca, sa = math.cos(a), math.sin(a)
    return (o[0] + x * ca - y * sa, o[1] + x * sa + y * ca)


class Pen:
    """Draws each symbol twice: once as a flat offset shadow (raised embroidery), once in colour."""

    def __init__(self, shadow=False):
        self.shadow = shadow
        self.parts = []

    def col(self, c):
        return INK if self.shadow else c

    def shape(self, tag, attrs, fill, stroke=True, sw=SW, extra=""):
        a = " ".join(f'{k}="{v}"' for k, v in attrs.items())
        s = f' stroke="{INK}" stroke-width="{sw}" stroke-linejoin="round" stroke-linecap="round"' if stroke else ""
        self.parts.append(f'<{tag} {a} fill="{self.col(fill)}"{s}{extra}/>')

    def path(self, d, fill, stroke=True, sw=SW, extra=""):
        self.shape("path", {"d": d}, fill, stroke, sw, extra)

    def poly(self, p, fill, stroke=True, sw=SW):
        self.shape("polygon", {"points": pts(p)}, fill, stroke, sw)

    def circle(self, x, y, r, fill, stroke=True, sw=SW):
        self.shape("circle", {"cx": f(x), "cy": f(y), "r": f(r)}, fill, stroke, sw)

    def line(self, d, color, width, cap="round", outline=SW):
        """A stroked line (ink outline under a coloured core), e.g. a staff or a letter stroke."""
        self.parts.append(f'<path d="{d}" fill="none" stroke="{INK}" stroke-width="{f(width + outline)}" '
                          f'stroke-linecap="{cap}" stroke-linejoin="round"/>')
        if not self.shadow and color != INK:
            self.parts.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="{f(width)}" '
                              f'stroke-linecap="{cap}" stroke-linejoin="round"/>')

    def sheen(self, d, color=GOLD_HI, width=5, opacity=0.85):
        """Satin highlight: a thin light stitch on the lit (upper-left) side. Skipped in the shadow pass."""
        if not self.shadow:
            self.parts.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="{f(width)}" '
                              f'stroke-linecap="round" stroke-opacity="{opacity}"/>')

    def raw(self, s):
        if not self.shadow:
            self.parts.append(s)

    def svg(self):
        return "".join(self.parts)


# ---------------------------------------------------------------- lettering (vector strokes, no font)
GLYPHS = {
    "I": [[(0.5, 0), (0.5, 1)]],
    "C": "arc",
    "X": [[(0.05, 0), (0.95, 1)], [(0.95, 0), (0.05, 1)]],
    "N": [[(0.08, 1), (0.08, 0), (0.92, 1), (0.92, 0)]],
    "K": [[(0.1, 0), (0.1, 1)], [(0.9, 0), (0.12, 0.55)], [(0.38, 0.38), (0.95, 1)]],
    "A": [[(0.02, 1), (0.5, 0), (0.98, 1)], [(0.24, 0.62), (0.76, 0.62)]],
}
GLYPH_W = {"I": 0.35, "C": 0.8, "X": 0.85, "N": 0.85, "K": 0.85, "A": 0.9}


def lettering(pen, text, cx, y, h, color, width, titlo=False):
    """Monoline capitals centred on cx, top at y, height h. Greek abbreviations take a titlo bar."""
    gap = 0.18 * h
    total = sum(GLYPH_W[ch] * h for ch in text) + gap * (len(text) - 1)
    x = cx - total / 2
    # ink letters are a single stroke; light letters get a fine ink edge so they hold on any ground
    ol = 0 if color == INK else 5

    def stroke(d, wd):
        if pen.shadow:
            return  # lettering casts no shadow: it is stitched flat
        pen.line(d, color, wd, outline=ol)

    for ch in text:
        w = GLYPH_W[ch] * h
        if GLYPHS[ch] == "arc":
            r = h / 2
            d = (f"M{f(x + w)} {f(y + 0.16 * h)} A{f(r * 0.95)} {f(r)} 0 1 0 {f(x + w)} {f(y + 0.84 * h)}")
            stroke(d, width)
        else:
            for st in GLYPHS[ch]:
                d = "M" + " L".join(f"{f(x + px * w)} {f(y + py * h)}" for px, py in st)
                stroke(d, width)
        x += w + gap
    if titlo:
        stroke(f"M{f(cx - total / 2 + 2)} {f(y - 0.28 * h)} L{f(cx + total / 2 - 2)} {f(y - 0.28 * h)}", width * 0.8)


# ---------------------------------------------------------------- shared shapes
def byz_cross(cx, cy, up, side, down, w0, w1, depth=14):
    """A Byzantine cross with flared arms and concave ends (after Gould's crosses, OAJ small ornaments).
    Returns (polygon, terminal points) for arms of lengths up/side/down; w0 = half-width at the centre,
    w1 = half-width at the ends."""
    poly, ends = [], []
    arms = [(-math.pi / 2, up), (0, side), (math.pi / 2, down), (math.pi, side)]
    for ang, L in arms:
        # local arm pointing along +x
        local = [(w0, -w0), (L, -w1)]
        for i in range(1, 8):
            t = i / 8
            local.append((L - depth * math.sin(math.pi * t), -w1 + 2 * w1 * t))
        local += [(L, w1)]
        for (x, y) in local:
            poly.append(rot((cx + x, cy + y), ang, (cx, cy)))
        ends.append(rot((cx + L, cy - w1), ang, (cx, cy)))
        ends.append(rot((cx + L, cy + w1), ang, (cx, cy)))
    return poly, ends


def scaled(poly, k, o):
    return [(o[0] + (x - o[0]) * k, o[1] + (y - o[1]) * k) for x, y in poly]


# ---------------------------------------------------------------- the frame
def frame(field, shade, band, texture=True):
    s = []
    # merrowed rim: a dark band with satin overstitching
    s.append(f'<circle cx="256" cy="256" r="254" fill="{INK}"/>')
    s.append(f'<circle cx="256" cy="256" r="244" fill="none" stroke="{shade}" stroke-width="16"/>')
    s.append(f'<circle cx="256" cy="256" r="244" fill="none" stroke="{GOLD_SH}" stroke-width="14" '
             f'stroke-dasharray="3 4.2" stroke-opacity="0.6"/>')
    s.append(f'<circle cx="256" cy="256" r="234" fill="{shade}" stroke="{GOLD}" stroke-width="5"/>')
    s.append(band_svg(band, shade))
    s.append(f'<circle cx="256" cy="256" r="{R_FIELD + 3}" fill="{field}" stroke="{GOLD}" stroke-width="6"/>')
    s.append(f'<circle cx="256" cy="256" r="{R_FIELD - 5}" fill="none" stroke="{INK}" stroke-width="2.5" stroke-opacity="0.35"/>')
    s.append(ground(shade, light=luminance(field) > 0.5))
    if texture:
        # satin fill: faint stitches in one direction only (never crossed: no grid)
        lines = "".join(f'M{x} 40 l-180 440' for x in range(60, 700, 14))
        s.append(f'<clipPath id="field"><circle cx="256" cy="256" r="{R_FIELD}"/></clipPath>')
        s.append(f'<path d="{lines}" clip-path="url(#field)" stroke="#FFFFFF" stroke-width="2" stroke-opacity="0.06" fill="none"/>')
    return "".join(s)


def luminance(hexcol):
    r, g, b = (int(hexcol[i:i + 2], 16) / 255 for i in (1, 3, 5))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def ground(shade, light=False):
    """The quiet ground ornament shared by every badge: two interlaced squares (an eight-pointed star),
    after Lukovich's medallion (OAJ small ornaments), tone on tone behind the symbol."""
    r = 196
    op = 0.2 if light else 0.5
    out = []
    for a0 in (math.pi / 4, 0):
        sq = [(C + r * math.cos(a0 + i * math.pi / 2), C + r * math.sin(a0 + i * math.pi / 2)) for i in range(4)]
        out.append(f'<polygon points="{pts(sq)}" fill="none" stroke="{shade}" stroke-width="9" stroke-opacity="{op}" stroke-linejoin="round"/>')
    for i in range(8):
        a = i * math.pi / 4 + math.pi / 8
        out.append(f'<circle cx="{f(C + 158 * math.cos(a))}" cy="{f(C + 158 * math.sin(a))}" r="7" fill="{shade}" fill-opacity="{op}"/>')
    return "".join(out)


def band_svg(band, shade):
    rc = 220  # band centre radius
    out = []
    if band == "cable":
        # Anglo-Saxon two-strand twist: two strands that cross 28 times round the ring.
        n = 28
        for phase in (0, math.pi):
            p = []
            for i in range(0, 721):
                t = 2 * math.pi * i / 720
                r = rc + 7 * math.sin(n * t + phase)
                p.append((C + r * math.cos(t), C + r * math.sin(t)))
            d = "M" + " L".join(f"{f(x)} {f(y)}" for x, y in p) + "Z"
            out.append(f'<path d="{d}" fill="none" stroke="{INK}" stroke-width="9"/>')
            out.append(f'<path d="{d}" fill="none" stroke="{GOLD}" stroke-width="4.5"/>')
        # pellets in the eyes of the twist (as in manuscript knotwork)
        for i in range(n):
            t = 2 * math.pi * (i + 0.5) / n
            out.append(f'<circle cx="{f(C + rc * math.cos(t))}" cy="{f(C + rc * math.sin(t))}" r="2.2" fill="{GOLD_HI}" fill-opacity="0.8"/>')
    elif band == "notched":
        # O'Rourke's notched border, bent into a ring: gold teeth pointing inwards.
        n = 36
        for i in range(n):
            a0 = 2 * math.pi * i / n
            a1 = 2 * math.pi * (i + 1) / n
            am = (a0 + a1) / 2
            p = [(C + 230 * math.cos(a0), C + 230 * math.sin(a0)), (C + 230 * math.cos(a1), C + 230 * math.sin(a1)),
                 (C + 210 * math.cos(am), C + 210 * math.sin(am))]
            out.append(f'<polygon points="{pts(p)}" fill="{GOLD}" stroke="{INK}" stroke-width="3" stroke-linejoin="round"/>')
    elif band == "pearls":
        # Byzantine pearled border, as on processional crosses and Gospel covers.
        n = 40
        for i in range(n):
            t = 2 * math.pi * i / n
            out.append(f'<circle cx="{f(C + rc * math.cos(t))}" cy="{f(C + rc * math.sin(t))}" r="7" fill="{PEARL_GOLD}" stroke="{INK}" stroke-width="3"/>')
            out.append(f'<circle cx="{f(C + rc * math.cos(t) - 2.2)}" cy="{f(C + rc * math.sin(t) - 2.2)}" r="2" fill="#FFFFFF" fill-opacity="0.9"/>')
    elif band == "jewels":
        # Pearls alternating with gold-set jewels: the jewelled border of the sanctuary.
        n = 18
        for i in range(2 * n):
            t = 2 * math.pi * i / (2 * n)
            x, y = C + rc * math.cos(t), C + rc * math.sin(t)
            if i % 2 == 0:
                d = 10
                p = [rot((x, y - d), t + math.pi / 2, (x, y)), rot((x + d * 0.8, y), t + math.pi / 2, (x, y)),
                     rot((x, y + d), t + math.pi / 2, (x, y)), rot((x - d * 0.8, y), t + math.pi / 2, (x, y))]
                out.append(f'<polygon points="{pts(p)}" fill="{GOLD}" stroke="{INK}" stroke-width="3.5" stroke-linejoin="round"/>')
                out.append(f'<circle cx="{f(x)}" cy="{f(y)}" r="3.2" fill="{CRIMSON}"/>')
            else:
                out.append(f'<circle cx="{f(x)}" cy="{f(y)}" r="6" fill="{PEARL_GOLD}" stroke="{INK}" stroke-width="2.5"/>')
    return "".join(out)


# ---------------------------------------------------------------- symbols
def sym_star(p):
    """Recruit A: the eight-pointed star that led the Magi, after Gould's stars (OAJ)."""
    outer, inner = 150, 46
    poly = []
    for i in range(16):
        a = -math.pi / 2 + i * math.pi / 8
        r = outer if i % 2 == 0 else inner
        poly.append((C + r * math.cos(a), C + r * math.sin(a)))
    p.poly(poly, GOLD)
    p.circle(C, C, 26, PEARL)
    for i in range(8):
        a = -math.pi / 2 + math.pi / 8 + i * math.pi / 4
        p.circle(C + 118 * math.cos(a), C + 118 * math.sin(a), 10, PEARL, sw=6)
    p.sheen(f"M{C} {C - 140} L{C - 14} {C - 46}")
    p.sheen(f"M{C - 136} {C} L{C - 44} {C - 12}")


def sym_budded(p):
    """Recruit B: a budded cross rising from a sprouting seed."""
    # leaves at the foot
    p.path("M256 400 C214 398 168 372 150 330 C196 330 236 352 256 392 Z", "#4E9A3A")
    p.path("M256 400 C298 398 344 372 362 330 C316 330 276 352 256 392 Z", "#4E9A3A")
    p.line("M256 398 L256 150", GOLD, 26, cap="butt")
    p.line("M166 220 L346 220", GOLD, 26, cap="butt")
    for x, y in [(256, 124), (140, 220), (372, 220)]:
        for dx, dy in [(0, -16), (-16, 8), (16, 8)]:
            p.circle(x + dx, y + dy, 16, GOLD)
        p.circle(x, y, 8, CRIMSON, sw=5)
    p.sheen("M249 392 L249 160", width=5)


def sym_logo_cross(p):
    """Recruit C: the group's own mark: Greek cross with IC XC NI KA."""
    poly, _ = byz_cross(C, C, 150, 150, 150, 20, 24, depth=0)
    p.poly(poly, GOLD)
    p.sheen("M246 116 L246 232")
    lettering(p, "IC", 182, 158, 44, PEARL, 9, titlo=True)
    lettering(p, "XC", 330, 158, 44, PEARL, 9, titlo=True)
    lettering(p, "NI", 182, 310, 44, PEARL, 9)
    lettering(p, "KA", 330, 310, 44, PEARL, 9)


def sym_helmet(p):
    """Guardsman: the helmet of salvation (Ephesians 6:17), an eleventh-century conical guard's helmet."""
    # Drawn as a spangenhelm (riveted plates, nasal, mail), wider than tall, so it never reads as a mitre.
    # mail curtain round the face, in curved rows (no grid)
    p.path("M138 310 L138 386 Q256 436 374 386 L374 310 Z", SILVER_SH)
    for k, y in enumerate((344, 368, 392)):
        p.raw(f'<path d="M150 {y - 14} Q256 {y + 30 + 6 * k} 362 {y - 14}" fill="none" stroke="{SILVER}" stroke-width="4" stroke-opacity="0.75"/>')
    # the face opening, in shadow, either side of the nasal
    p.path("M196 318 Q256 310 316 318 L306 370 Q256 398 206 370 Z", "#2A2233", sw=7)
    # dome of plates
    p.path("M140 306 C140 216 194 150 256 116 C318 150 372 216 372 306 Z", SILVER)
    p.sheen("M168 286 C168 226 202 176 246 140", "#FFFFFF", 7, 0.9)
    for d in ("M256 120 L256 172", "M206 168 C186 214 178 256 178 296", "M306 168 C326 214 334 256 334 296"):
        p.line(d, GOLD, 11)
    for x, y in ((190, 214), (183, 256), (322, 214), (329, 256)):
        p.circle(x, y, 5, PEARL, sw=3)
    # the Cross on the front plate: the helmet of salvation
    cross, _ = byz_cross(256, 222, 36, 30, 40, 6, 11, depth=4)
    p.poly(cross, GOLD, sw=7)
    # brow band with rivets, and the nasal
    p.path("M132 292 Q256 276 380 292 L380 324 Q256 308 132 324 Z", GOLD)
    for i in range(9):
        x = 152 + i * 26
        p.circle(x, 308 - 7 * math.sin(math.pi * i / 8), 5.5, PEARL, sw=3.5)
    p.path("M243 304 L269 304 L266 384 Q256 396 246 384 Z", GOLD)


def sym_kite_shield(p):
    """Shield-bearer A: the shield of faith (Ephesians 6:16), the kite shield of the Guard's day."""
    outer = "M150 136 Q256 106 362 136 C368 252 330 332 256 418 C182 332 144 252 150 136 Z"
    p.path(outer, GOLD)
    p.path("M170 152 Q256 128 342 152 C346 250 314 320 256 392 C198 320 166 250 170 152 Z", CRIMSON, sw=6)
    cross, ends = byz_cross(256, 236, 84, 76, 140, 9, 20, depth=8)
    cross = [(x, min(y, 382 - abs(x - 256) * 1.2)) for x, y in cross]
    p.poly(cross, GOLD, sw=7)
    for i in (0, 1, 2, 3, 6, 7):  # Gould's knobbed terminals, except at the foot
        p.circle(ends[i][0], ends[i][1], 7.5, GOLD, sw=5)
    p.circle(256, 236, 18, PEARL, sw=6)
    p.sheen("M168 150 Q210 132 252 128")
    p.sheen("M164 170 C164 240 190 300 226 352", GOLD_HI, 5, 0.6)


def sym_round_shield(p):
    """Shield-bearer B: an Anglo-Saxon round shield with boss and cruciform strapwork."""
    p.circle(C, C, 146, GOLD)
    p.circle(C, C, 128, CRIMSON, sw=6)
    for a in range(4):
        ang = a * math.pi / 2
        x2, y2 = C + 124 * math.cos(ang), C + 124 * math.sin(ang)
        p.line(f"M{C} {C} L{f(x2)} {f(y2)}", GOLD, 22, cap="butt")
    for i in range(16):
        t = 2 * math.pi * i / 16 + math.pi / 16
        p.circle(C + 137 * math.cos(t), C + 137 * math.sin(t), 5, PEARL, sw=3)
    p.circle(C, C, 40, SILVER)
    p.circle(C - 10, C - 10, 10, "#FFFFFF", stroke=False)


def sym_chirho_shield(p):
    """Shield-bearer C: a kite shield bearing the Chi-Rho."""
    p.path("M150 136 Q256 106 362 136 C368 252 330 332 256 418 C182 332 144 252 150 136 Z", GOLD)
    p.path("M170 152 Q256 128 342 152 C346 250 314 320 256 392 C198 320 166 250 170 152 Z", "#1A2E7A", sw=6)
    chi_rho(p, 256, 164, 356, 32, 62, 16)


def chi_rho(p, cx, top, bottom, r, hx, w):
    """The Chi-Rho (☧): the Rho's stem runs top to bottom with its closed bowl at the top right; the Chi
    crosses the stem below the bowl."""
    xc = top + 2 * r + (bottom - top - 2 * r) * 0.42
    p.line(f"M{f(cx - hx)} {f(xc - hx * 0.8)} L{f(cx + hx)} {f(xc + hx * 0.8)} M{f(cx + hx)} {f(xc - hx * 0.8)} L{f(cx - hx)} {f(xc + hx * 0.8)}", GOLD, w)
    p.line(f"M{f(cx)} {f(bottom)} L{f(cx)} {f(top)} A{f(r)} {f(r)} 0 0 1 {f(cx)} {f(top + 2 * r)}", GOLD, w)


def sym_sword(p):
    """Sword-bearer: the sword of the Spirit, which is the word of God (Ephesians 6:17): an upright
    sword over the open Gospel book."""
    # open book (pages ivory, crimson cover edge)
    p.path("M128 254 Q192 236 256 262 Q320 236 384 254 L384 330 Q320 312 256 338 Q192 312 128 330 Z", CRIMSON)
    p.path("M138 240 Q196 222 256 248 Q316 222 374 240 L374 314 Q316 298 256 322 Q196 298 138 314 Z", IVORY, sw=7)
    for k in range(3):
        y = 258 + k * 16
        p.raw(f'<path d="M156 {y} Q200 {y - 12} 238 {y + 4} M274 {y + 4} Q312 {y - 12} 356 {y}" fill="none" stroke="{INK}" stroke-width="3" stroke-opacity="0.45"/>')
    # blade
    p.poly([(256, 96), (276, 128), (273, 324), (239, 324), (236, 128)], SILVER)
    p.raw(f'<path d="M256 132 L256 318" stroke="{SILVER_SH}" stroke-width="5"/>')
    p.sheen("M246 134 L244 316", "#FFFFFF", 4, 0.9)
    # crossguard, grip, pommel
    p.path("M180 324 L332 324 Q346 334 332 344 L180 344 Q166 334 180 324 Z", GOLD)
    p.path("M246 344 L266 344 L266 384 L246 384 Z", CRIMSON_SH, sw=7)
    p.circle(256, 394, 16, GOLD)
    p.circle(180, 334, 9, PEARL, sw=5)
    p.circle(332, 334, 9, PEARL, sw=5)


def sym_trumpet(p):
    """Herald: the straight trumpet of proclamation, with a pennant of the Cross and sound going out."""
    a = math.radians(-30)
    o = (246, 250)

    def P(x, y):
        return rot((o[0] + x, o[1] + y), a, o)

    # rays of sound
    for k, da in enumerate((-0.55, 0, 0.55)):
        b = a + da
        x0, y0 = P(122, 0)
        x1, y1 = x0 + 18 * math.cos(b), y0 + 18 * math.sin(b)
        x2, y2 = x0 + 40 * math.cos(b), y0 + 40 * math.sin(b)
        p.line(f"M{f(x1)} {f(y1)} L{f(x2)} {f(y2)}", GOLD, 10)
    # pennant hanging from the tube
    ax, ay = P(-104, 8)
    bx, by = P(16, 8)
    # swallow-tailed pennant, gold-edged, with a flared Byzantine cross (not a plain white cross on red,
    # which would read as the Swiss flag or the Red Cross)
    mxp = (ax + bx) / 2
    pen = [(ax, ay), (bx, by), (bx, by + 120), (mxp, by + 92), (ax, ay + 120)]
    p.poly(pen, CRIMSON, sw=8)
    p.raw(f'<polygon points="{pts(scaled(pen, 0.84, (mxp, (ay + by) / 2 + 52)))}" fill="none" stroke="{GOLD}" stroke-width="4" stroke-linejoin="round"/>')
    my = (ay + by) / 2 + 46
    cross, _ = byz_cross(mxp, my, 30, 28, 34, 6, 13, depth=5)
    p.poly(cross, GOLD, sw=6)
    # tube, knops, bell, mouthpiece
    tube = [P(-134, -9), P(70, -9), P(70, 9), P(-134, 9)]
    p.poly(tube, GOLD, sw=8)
    p.poly([P(60, -10), P(116, -40), P(122, 0), P(116, 40), P(60, 10)], GOLD, sw=8)
    p.path("M{0} {1} Q{2} {3} {4} {5}".format(*[f(v) for v in (*P(116, -40), *P(130, 0), *P(116, 40))]), GOLD_SH, sw=6)
    for x in (-60, 16):
        cx, cy = P(x, 0)
        p.circle(cx, cy, 15, GOLD, sw=7)
    mx0, my0 = P(-142, 0)
    p.circle(mx0, my0, 13, GOLD, sw=7)
    s0, s1 = P(-124, -4), P(60, -4)
    p.sheen(f"M{f(s0[0])} {f(s0[1])} L{f(s1[0])} {f(s1[1])}", GOLD_HI, 4)


def flame(p, cx, top, bottom, halo=True):
    if halo and not p.shadow:
        # a nimbus of light: two soft discs of gold thread behind the flame
        cy = (top + bottom) / 2 + 8
        p.raw(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="62" fill="{GOLD}" fill-opacity="0.28" stroke="{GOLD}" stroke-width="4"/>')
        p.raw(f'<circle cx="{f(cx)}" cy="{f(cy)}" r="44" fill="{GOLD_HI}" fill-opacity="0.30"/>')
    h = bottom - top
    w = h * 0.36
    p.path(f"M{cx} {top} C{cx + w * 0.4} {top + h * 0.3} {cx + w} {top + h * 0.5} {cx + w * 0.8} {top + h * 0.78} "
           f"C{cx + w * 0.6} {bottom} {cx - w * 0.6} {bottom} {cx - w * 0.8} {top + h * 0.78} "
           f"C{cx - w} {top + h * 0.5} {cx - w * 0.4} {top + h * 0.3} {cx} {top} Z", FLAME)
    p.path(f"M{cx} {top + h * 0.3} C{cx + w * 0.5} {top + h * 0.55} {cx + w * 0.5} {bottom - h * 0.08} {cx} {bottom - h * 0.06} "
           f"C{cx - w * 0.5} {bottom - h * 0.08} {cx - w * 0.5} {top + h * 0.55} {cx} {top + h * 0.3} Z", FLAME_HI, stroke=False)
    p.path(f"M{cx} {top + h * 0.55} C{cx + w * 0.22} {top + h * 0.7} {cx + w * 0.2} {bottom - h * 0.12} {cx} {bottom - h * 0.12} "
           f"C{cx - w * 0.2} {bottom - h * 0.12} {cx - w * 0.22} {top + h * 0.7} {cx} {top + h * 0.55} Z", "#FFFFFF", stroke=False)


def sym_candle(p):
    """Torch-bearer A: the server's processional candle, its flame giving light."""
    flame(p, 256, 94, 184)
    p.line("M256 182 L256 192", INK, 2)
    p.path("M228 190 L284 190 L286 288 L226 288 Z", IVORY)
    p.path("M228 190 Q240 204 250 192 L250 214 Q256 222 262 214 L262 192 Q272 204 284 190", IVORY, sw=6)
    p.sheen("M238 212 L237 280", "#FFFFFF", 6)
    p.path("M190 288 Q256 304 322 288 L310 306 Q256 318 202 306 Z", GOLD)
    p.path("M244 310 L268 310 L272 376 L240 376 Z", GOLD, sw=8)
    p.circle(256, 330, 15, GOLD, sw=7)
    p.circle(256, 356, 11, GOLD, sw=7)
    p.path("M200 404 Q212 378 240 374 L272 374 Q300 378 312 404 Z", GOLD)
    p.sheen("M200 292 Q232 300 256 300", GOLD_HI, 4)


def sym_torch(p):
    """Torch-bearer B: a hand-held torch, raised."""
    a = math.radians(28)
    base = rot((256, 420), a, (256, 256))
    top = rot((256, 210), a, (256, 256))
    p.line(f"M{f(base[0])} {f(base[1])} L{f(top[0])} {f(top[1])}", "#8A5524", 26)
    cup = [rot(q, a, (256, 256)) for q in [(214, 176), (298, 176), (282, 222), (230, 222)]]
    p.poly(cup, GOLD, sw=8)
    fx, fy = rot((256, 178), a, (256, 256))
    flame(p, fx - 4, fy - 112, fy + 8, halo=False)


def sym_lamp(p):
    """Torch-bearer C: the hanging oil lamp (lampada) kept burning before the icons."""
    p.line("M256 64 L256 140", GOLD, 5)
    p.line("M256 140 L190 230 M256 140 L322 230", GOLD, 5)
    p.path("M170 230 L342 230 Q336 330 256 348 Q176 330 170 230 Z", CRIMSON)
    p.path("M172 230 L340 230 L336 252 L176 252 Z", GOLD, sw=7)
    for i in range(5):
        p.circle(196 + i * 30, 288, 7, PEARL, sw=4)
    p.path("M240 346 L272 346 L264 384 L248 384 Z", GOLD, sw=7)
    p.circle(256, 396, 10, GOLD, sw=6)
    flame(p, 256, 150, 226, halo=False)


def sym_labarum(p):
    """Standard-bearer: the labarum, the standard of the Cross with the Chi-Rho carried before the army
    of Constantine and in the Church's processions."""
    p.line("M256 126 L256 420", GOLD_SH, 14, cap="butt")
    cross, _ = byz_cross(256, 110, 18, 16, 18, 4, 8, depth=3)
    p.poly(cross, GOLD, sw=6)
    p.line("M154 142 L358 142", GOLD, 12)
    p.circle(148, 142, 10, PEARL, sw=5)
    p.circle(364, 142, 10, PEARL, sw=5)
    p.path("M164 150 L348 150 L348 304 L164 304 Z", CRIMSON)
    p.raw(f'<path d="M176 162 L336 162 L336 292 L176 292 Z" fill="none" stroke="{GOLD}" stroke-width="5"/>')
    for i in range(7):
        x = 172 + i * 28
        p.line(f"M{x} 306 L{x} 326", GOLD, 6)
        p.circle(x, 332, 7, GOLD, sw=4)
    chi_rho(p, 256, 174, 284, 22, 40, 13)


def sym_processional_cross(p, letters=True):
    """Cross-bearer A: the processional cross on its staff, its arms flared and pearled (after Gould),
    with IC XC NI KA: Jesus Christ conquers."""
    p.line("M256 290 L256 422", GOLD_SH, 18, cap="butt")
    p.circle(256, 318, 17, GOLD, sw=7)
    p.circle(256, 374, 12, GOLD, sw=6)
    cross, ends = byz_cross(256, 200, 100, 110, 96, 15, 32, depth=12)
    p.poly(cross, CRIMSON)
    inner, _ = byz_cross(256, 200, 84, 94, 80, 7, 18, depth=8)
    p.poly(inner, GOLD, sw=4)
    for (x, y) in ends:
        p.circle(x, y, 10, PEARL, sw=5)
    p.circle(256, 200, 16, PEARL, sw=5)
    if letters:
        lettering(p, "IC", 188, 128, 30, INK, 7, titlo=True)
        lettering(p, "XC", 324, 128, 30, INK, 7, titlo=True)
        lettering(p, "NI", 188, 246, 30, INK, 7)
        lettering(p, "KA", 324, 246, 30, INK, 7)


def sym_golgotha(p):
    """Cross-bearer B: the three-bar cross on Golgotha with the spear and the reed (after Gould)."""
    p.path("M150 404 Q256 340 362 404 Z", "#6B4A2E")
    p.line("M180 120 L240 390", GOLD_SH, 8)
    p.line("M332 120 L272 390", GOLD_SH, 8)
    p.circle(180, 116, 12, "#7E8F55", sw=6)
    p.poly([(332, 96), (342, 130), (322, 130)], SILVER, sw=6)
    p.line("M256 100 L256 392", CRIMSON, 26, cap="butt")
    p.line("M226 132 L286 132", CRIMSON, 20, cap="butt")
    p.line("M174 184 L338 184", CRIMSON, 26, cap="butt")
    p.line("M222 312 L290 290", CRIMSON, 20, cap="butt")
    p.sheen("M249 108 L249 384", "#E9707D", 5)


def sym_flower_cross(p):
    """Cross-bearer C: the flowering cross with rosette terminals (after O'Rourke's Byzantine flower cross)."""
    p.line("M256 100 L256 412", CRIMSON, 34, cap="butt")
    p.line("M120 236 L392 236", CRIMSON, 34, cap="butt")
    p.line("M256 100 L256 412", GOLD, 12, cap="butt")
    p.line("M120 236 L392 236", GOLD, 12, cap="butt")
    for x, y in [(256, 96), (116, 236), (396, 236), (256, 416), (256, 236)]:
        for i in range(8):
            ang = i * math.pi / 4
            p.circle(x + 15 * math.cos(ang), y + 15 * math.sin(ang), 11, GOLD, sw=5)
        p.circle(x, y, 10, CRIMSON, sw=5)


def sym_chalice(p):
    """Christ-bearer: the chalice of Holy Communion, marked with the Cross and IC XC. Receiving Christ,
    the communicant carries Him into the world."""
    lettering(p, "IC", 182, 300, 38, PEARL, 8, titlo=True)
    lettering(p, "XC", 330, 300, 38, PEARL, 8, titlo=True)
    p.path("M170 170 L342 170 Q340 266 256 280 Q172 266 170 170 Z", GOLD)
    p.path("M168 162 L344 162 L344 182 L168 182 Z", GOLD, sw=8)
    for i in range(7):
        p.circle(186 + i * 23.3, 172, 5.5, PEARL, sw=3)
    cross, _ = byz_cross(256, 226, 30, 28, 32, 5, 11, depth=4)
    p.poly(cross, CRIMSON, sw=5)
    p.path("M244 278 L268 278 L272 352 L240 352 Z", GOLD, sw=8)
    p.circle(256, 312, 19, GOLD, sw=7)
    p.circle(256, 312, 7, CRIMSON, stroke=False)
    p.path("M182 410 Q196 362 240 350 L272 350 Q316 362 330 410 Z", GOLD)
    p.sheen("M186 190 Q190 248 236 268")
    p.sheen("M200 400 Q212 372 240 362", GOLD_HI, 4)


EAGLE_D = None


def sym_eagle(p, scale=0.86, dy=4):
    """Akolouthos: the double-headed eagle, emblem of the Orthodox Church (public-domain vector already
    in the app), for the commander of the Guard who stood closest to the Emperor."""
    global EAGLE_D
    if EAGLE_D is None:
        s = open(os.path.join(ROOT, "src", "Vlms.Web", "wwwroot", "img", "eagle.svg"), encoding="utf8").read()
        EAGLE_D = re.search(r' d="([^"]+)"', s).group(1)
    w, h = 390 * scale, 330 * scale
    tx, ty = C - w / 2, C - h / 2 + dy
    tr = f'transform="translate({f(tx)} {f(ty)}) scale({scale})"'
    if p.shadow:
        p.parts.append(f'<path d="{EAGLE_D}" {tr} fill="{INK}" fill-rule="evenodd" stroke="{INK}" stroke-width="{f(9 / scale)}" stroke-linejoin="round"/>')
    else:
        p.parts.append(f'<path d="{EAGLE_D}" {tr} fill="{INK}" fill-rule="evenodd" stroke="{INK}" stroke-width="{f(7 / scale)}" stroke-linejoin="round"/>')
        p.parts.append(f'<path d="{EAGLE_D}" {tr} fill="{GOLD}" fill-rule="evenodd"/>')


def badge(field, shade, band, symbol, title, desc):
    shadow, colour = Pen(shadow=True), Pen()
    symbol(shadow)
    symbol(colour)
    k = f"translate({f(C * (1 - K))} {f(C * (1 - K))}) scale({K})"
    body = (f'<g transform="translate(5 8)" opacity="0.42"><g transform="{k}">{shadow.svg()}</g></g>'
            f'<g transform="{k}">{colour.svg()}</g>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512" role="img">'
            f'<title>{title}</title><desc>{desc}</desc>'
            f'{frame(field, shade, band)}{body}</svg>')


PROPOSED = {
    1: (sym_star, "The eight-pointed star that led the Magi to Christ: the first light on the way, and the beginning of a journey made together."),
    2: (sym_helmet, "The helmet of salvation (Ephesians 6:17): a guard's conical helmet with the Cross on its brow, for vigilance and service."),
    3: (sym_kite_shield, "The shield of faith (Ephesians 6:16): the kite shield of the Guard's own day, bearing the Cross."),
    4: (sym_sword, "The sword of the Spirit, which is the word of God (Ephesians 6:17): an upright sword over the open Gospel."),
    5: (sym_trumpet, "The herald's trumpet with the pennant of the Cross: the Faith proclaimed and carried outwards."),
    6: (sym_candle, "The server's processional candle: the light of Christ that enlightens all."),
    7: (sym_labarum, "The labarum, the standard of the Cross bearing the Chi-Rho, carried before the army of Constantine and in the Church's processions."),
    8: (sym_processional_cross, "The processional cross with IC XC NI KA, Jesus Christ conquers: the Cross at the centre of the procession and of the faith."),
    9: (sym_chalice, "The chalice of Holy Communion marked with the Cross and IC XC: receiving Christ, the communicant carries Him into the world."),
    10: (sym_eagle, "The double-headed eagle, emblem of the Orthodox Church, for the commander of the Guard who stood closest to the Emperor."),
}

EXPLORATION = [
    # file, order (for colours), override field/shade/band or None, symbol, label
    ("recruit-a-star", 1, None, sym_star, "Recruit A: the star of the Magi"),
    ("recruit-b-budded-cross", 1, ("#1E6B3E", "#0D3A20", "cable"), sym_budded, "Recruit B: the budded cross from the seed"),
    ("recruit-c-group-cross", 1, None, sym_logo_cross, "Recruit C: the group's own cross, IC XC NI KA"),
    ("shield-bearer-a-kite", 3, None, sym_kite_shield, "Shield-bearer A: kite shield with the Cross"),
    ("shield-bearer-b-round", 3, None, sym_round_shield, "Shield-bearer B: Anglo-Saxon round shield"),
    ("shield-bearer-c-chi-rho", 3, ("#C9A12A", "#5E4A0A", "cable"), sym_chirho_shield, "Shield-bearer C: kite shield with the Chi-Rho"),
    ("torch-bearer-a-candle", 6, None, sym_candle, "Torch-bearer A: processional candle"),
    ("torch-bearer-b-torch", 6, None, sym_torch, "Torch-bearer B: hand-held torch"),
    ("torch-bearer-c-lampada", 6, None, sym_lamp, "Torch-bearer C: hanging oil lamp"),
    ("cross-bearer-a-processional", 8, None, sym_processional_cross, "Cross-bearer A: processional cross, IC XC NI KA"),
    ("cross-bearer-b-golgotha", 8, None, sym_golgotha, "Cross-bearer B: Golgotha cross, spear and reed"),
    ("cross-bearer-c-flowering", 8, None, sym_flower_cross, "Cross-bearer C: flowering cross with rosettes"),
    ("akolouthos-a-purple", 10, None, sym_eagle, "Akolouthos A: gold eagle on imperial purple"),
    ("akolouthos-b-lapis", 10, ("#14254F", "#0A1430", "jewels"), sym_eagle, "Akolouthos B: gold eagle on lapis (as agreed 23/10/2026)"),
    ("akolouthos-c-crimson", 10, ("#9E1B2B", "#4F0C15", "jewels"), sym_eagle, "Akolouthos C: gold eagle on imperial crimson"),
]


def dedupe_images(html):
    """Embed each badge once: <img data-b="n"> plus one inline map, so the page stays self-contained
    without repeating the same SVG dozens of times."""
    keys = {}

    def sub(m):
        k = keys.setdefault(m.group(1), str(len(keys)))
        return f'data-b="{k}"'

    html = re.sub(r'src="(data:image/svg\+xml;base64,[A-Za-z0-9+/=]+)"', sub, html)
    table = ",".join(f'"{v}":"{k}"' for k, v in keys.items())
    script = ("<script>const B={" + table + "};"
              "document.querySelectorAll('img[data-b]').forEach(i=>i.src=B[i.dataset.b]);</script>")
    return html.replace("</body>", script + "</body>")


def write(path, text):
    if path.endswith(".html"):
        text = dedupe_images(text)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf8", newline="\n") as fh:
        fh.write(text)


def data_uri(svg):
    return "data:image/svg+xml;base64," + base64.b64encode(svg.encode("utf8")).decode("ascii")


def font_face():
    fonts = os.path.join(ROOT, "src", "Vlms.Web", "wwwroot", "fonts")
    css = []
    for fam, fn, wt in (("Alegreya", "alegreya-latin-wght-normal.woff2", "400 900"),
                        ("Alegreya", "alegreya-latin-wght-italic.woff2", "400 900"),
                        ("Source Sans 3", "source-sans-3-latin-wght-normal.woff2", "200 900")):
        b = base64.b64encode(open(os.path.join(fonts, fn), "rb").read()).decode("ascii")
        style = "italic" if "italic" in fn else "normal"
        css.append(f'@font-face{{font-family:"{fam}";src:url(data:font/woff2;base64,{b}) format("woff2");font-weight:{wt};font-style:{style}}}')
    return "".join(css)


def main():
    by_order = {r[0]: r for r in RANKS}
    proposed = {}
    for order, code, name, field, shade, band in RANKS:
        sym, note = PROPOSED[order]
        svg = badge(field, shade, band, sym, f"{name} badge", note)
        write(os.path.join(OUT, "proposed", f"{order:02d}-{code}.svg"), svg)
        # the production copy, uploaded through /administration/rank-badges (seed/rank-badges/README.md)
        write(os.path.join(ROOT, "seed", "rank-badges", f"{order:02d}-{code}.svg"), svg)
        proposed[order] = svg
    explored = []
    for fname, order, override, sym, label in EXPLORATION:
        _, _, _, field, shade, band = by_order[order]
        if override:
            field, shade, band = override
        svg = badge(field, shade, band, sym, label, label)
        write(os.path.join(OUT, "exploration", f"{fname}.svg"), svg)
        explored.append((fname, label, svg))
    write(os.path.join(OUT, "exploration", "exploration-sheet.html"), exploration_sheet(explored))
    write(os.path.join(OUT, "contact-sheet.html"), contact_sheet(proposed))
    print("written", len(proposed), "proposed and", len(explored), "exploration badges")


# ---------------------------------------------------------------- review pages
APP_TOKENS = {  # current app colours (src/Vlms.Web/wwwroot/app.css, @layer tokens): field, on, tint, accent
    1: ("#E2B33C", "#172033", "#FDF3D1", "#B3261E"), 2: ("#B3261E", "#FFFFFF", "#FBE4E1", "#E2B33C"),
    3: ("#1E2F8F", "#FFFFFF", "#E3E7F8", "#E2B33C"), 4: ("#1C7A3E", "#FFFFFF", "#DFF3E6", "#D9DEE5"),
    5: ("#1A6FA8", "#FFFFFF", "#DDEFFA", "#D63A2F"), 6: ("#2A211C", "#FFFFFF", "#FDE6D8", "#E8590C"),
    7: ("#4D7C0F", "#FFFFFF", "#ECF7D9", "#D63A2F"), 8: ("#233B8C", "#FFFFFF", "#FFF3C4", "#F2C744"),
    9: ("#9B2C2C", "#FFFFFF", "#F6E9D6", "#D9B98C"), 10: ("#14254F", "#FFFFFF", "#F7EBC8", "#E2B33C"),
}

MEDALLION_CSS = """
.medallion{--size:4rem;position:relative;flex:none;width:var(--size);height:var(--size);border-radius:50%;display:grid;place-items:center;
 background:var(--rank-field);color:var(--rank-on);box-shadow:0 0 0 3px var(--rank-accent),0 0 0 5px var(--surface),0 0 0 6px color-mix(in srgb,var(--rank-field) 40%,transparent)}
.medallion::after{content:"";position:absolute;inset:calc(var(--size)*0.07);border-radius:50%;border:2px dashed color-mix(in srgb,var(--rank-on) 65%,transparent);pointer-events:none}
.medallion img{width:76%;height:76%;object-fit:contain;border-radius:50%;position:relative;z-index:1}
.medallion img.unearned{filter:grayscale(1);opacity:.45}
.medallion img.in-progress{opacity:.6}
.medallion--lg{--size:7.5rem}
.medallion--sm{--size:2.75rem;box-shadow:0 0 0 2px var(--rank-accent)}
.medallion--sm::after{border-width:1.5px}
.medallion--full{box-shadow:none;background:none}.medallion--full::after{display:none}.medallion--full img{width:100%;height:100%}
.medallion--ahead{background:var(--surface);color:var(--rank-field);box-shadow:0 0 0 2px var(--rank-field)}
"""


def rank_vars(order, proposed_tokens=False):
    field, on, tint, accent = APP_TOKENS[order]
    if proposed_tokens:
        r = RANKS[order - 1]
        field, accent = r[3], "#EDB637"
        on = "#172033" if order in (7, 8) else "#FFFFFF"
    return f"--rank-field:{field};--rank-on:{on};--rank-tint:{tint};--rank-accent:{accent}"


def contact_sheet(proposed):
    notes = {o: PROPOSED[o][1] for o in PROPOSED}
    cards = []
    for order, code, name, field, shade, band in RANKS:
        uri = data_uri(proposed[order])
        v_now = rank_vars(order, True)  # the proposed ring colours (Phase E would set these tokens)
        v_old = rank_vars(order)
        cards.append(f"""
<article class="card">
  <div class="hero"><img src="{uri}" alt="{name} badge" width="200" height="200"></div>
  <div class="info">
    <p class="num">Rank {order} of 10</p>
    <h2>{name}</h2>
    <p class="note">{notes[order]}</p>
    <div class="sizes">
      <figure><span class="medallion medallion--sm" style="{v_now}"><img src="{uri}" alt=""></span><figcaption>sm, 33 px<br>procession</figcaption></figure>
      <figure><span class="medallion" style="{v_now}"><img src="{uri}" alt=""></span><figcaption>md, 49 px<br>rank header</figcaption></figure>
      <figure><span class="medallion" style="{v_now}"><img class="in-progress" src="{uri}" alt=""></span><figcaption>in progress<br>(faded)</figcaption></figure>
      <figure><span class="medallion medallion--sm" style="{v_now}"><img class="unearned" src="{uri}" alt=""></span><figcaption>not yet earned<br>(grey)</figcaption></figure>
      <figure><span class="medallion medallion--sm" style="{v_old}"><img src="{uri}" alt=""></span><figcaption>today's<br>ring colour</figcaption></figure>
      <figure><span class="medallion medallion--full" style="{v_now}"><img src="{uri}" alt=""></span><figcaption>option: badge<br>fills, 64 px</figcaption></figure>
      <figure><span class="medallion medallion--lg" style="{v_now}"><img src="{uri}" alt=""></span><figcaption>lg, 91 px<br>(not used today)</figcaption></figure>
    </div>
  </div>
</article>""")
    # procession strip, as on the student page sidebar (rank tint behind the current station)
    stations = []
    for order, code, name, *_ in RANKS:
        uri = data_uri(proposed[order])
        state = "past" if order < 4 else ("current" if order == 4 else "ahead")
        img_cls = "" if state == "past" else ("in-progress" if state == "current" else "unearned")
        med_cls = "medallion medallion--sm" + (" medallion--ahead" if state == "ahead" else "")
        meta = {"past": "Earned", "current": "Your rank now: 3 of 8 lessons", "ahead": "8 lessons"}[state] if order != 10 else "After Christ-bearer: leadership and a wider programme"
        stations.append(f'<li class="st is-{state}" style="{rank_vars(order, True)}"><span class="{med_cls}" style="{rank_vars(order, True)}">'
                        f'<img class="{img_cls}" src="{uri}" alt=""></span><span><b>{name}</b><small>{meta}</small></span></li>')
    row_paper = "".join(f'<span class="medallion" style="{rank_vars(o, True)}"><img src="{data_uri(proposed[o])}" alt=""></span>' for o in range(1, 11))
    row_today = "".join(f'<span class="medallion" style="{rank_vars(o)}"><img src="{data_uri(proposed[o])}" alt=""></span>' for o in range(1, 11))
    row_full = "".join(f'<span class="medallion medallion--full medallion--sm" style="{rank_vars(o, True)}"><img src="{data_uri(proposed[o])}" alt=""></span>' for o in range(1, 11))
    row_grey = "".join(f'<span class="medallion medallion--sm" style="{rank_vars(o, True)}"><img class="unearned" src="{data_uri(proposed[o])}" alt=""></span>' for o in range(1, 11))
    row_white = "".join(f'<span class="medallion medallion--sm" style="{rank_vars(o, True)}"><img src="{data_uri(proposed[o])}" alt=""></span>' for o in range(1, 11))
    row_lapis = "".join(f'<img src="{data_uri(proposed[o])}" alt="" width="72" height="72">' for o in range(1, 11))
    row_bare = "".join(f'<figure><img src="{data_uri(proposed[o])}" alt="" width="128" height="128"><figcaption>{RANKS[o - 1][2]}</figcaption></figure>' for o in range(1, 11))
    return f"""<!doctype html>
<html lang="en-GB"><head><meta charset="utf-8"><title>Varangians rank badges: proposed collection (for approval)</title>
<style>{font_face()}
:root{{--ink:#172033;--ink-soft:#4A5568;--paper:#F7F8FA;--surface:#FFFFFF;--line:#D5DBE4;--lapis-900:#14254F;--gold-700:#8A6410;
 --font-display:"Alegreya",Georgia,serif;--font-text:"Source Sans 3","Segoe UI",sans-serif}}
*{{box-sizing:border-box}} body{{margin:0;background:var(--paper);color:var(--ink);font:16px/1.5 var(--font-text)}}
main{{max-width:76rem;margin:0 auto;padding:2rem 1.5rem 4rem}}
h1,h2{{font-family:var(--font-display);color:var(--lapis-900);margin:0}} h1{{font-size:2.4rem}}
.lead{{color:var(--ink-soft);max-width:60rem}}
section{{margin-top:2rem}} section>h2{{font-size:1.5rem;margin-bottom:.75rem}}
.strip{{display:flex;flex-wrap:wrap;gap:1.25rem;align-items:center;padding:1.25rem;border:1px solid var(--line);border-radius:10px}}
.strip.paper{{background:var(--paper)}} .strip.white{{background:var(--surface)}} .strip.lapis{{background:var(--lapis-900);border-color:var(--lapis-900)}}
.bare{{display:grid;grid-template-columns:repeat(5,1fr);gap:1rem;background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:1.25rem}}
.bare figure{{margin:0;text-align:center}} .bare figcaption{{font-weight:700}}
.grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.25rem}}
.card{{display:flex;gap:1.25rem;background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:1.25rem}}
.hero img{{display:block;width:200px;height:200px}}
.num{{margin:0;color:var(--gold-700);font-weight:700;font-size:.875rem}} .card h2{{font-size:1.6rem}}
.note{{margin:.25rem 0 .75rem;font-size:.95rem}}
.sizes{{display:flex;flex-wrap:wrap;gap:.9rem;align-items:flex-end}} .sizes figure{{margin:0;display:flex;flex-direction:column;align-items:center;gap:.5rem}}
.sizes figcaption{{font-size:.72rem;color:var(--ink-soft);text-align:center;line-height:1.2}}
.way{{max-width:22rem;background:var(--surface);border:1px solid var(--line);border-radius:10px;padding:1rem;list-style:none;margin:0}}
.st{{display:flex;align-items:center;gap:.75rem;padding:.5rem}} .st small{{display:block;color:var(--ink-soft)}}
.st.is-current{{background:var(--rank-tint);box-shadow:inset 4px 0 0 var(--rank-field);border-radius:6px}}
.st.is-ahead b{{color:var(--ink-soft)}}
.two{{display:flex;gap:2rem;flex-wrap:wrap;align-items:flex-start}}
{MEDALLION_CSS}
</style></head>
<body><main>
<h1>Rank badges: the proposed collection</h1>
<p class="lead">For approval before anything in the application changes. Ten badges, Recruit to Akolouthos, drawn as vector art.
Each one is shown large, then inside the real rank medallion at the sizes the application uses (the badge fills 76% of the medallion and is clipped to a circle),
faded and greyed as the application shows ranks in progress and not yet earned, and on the page colours of the student interface:
paper <code>#F7F8FA</code>, white panels, the rank tint behind the current station, and the lapis bar <code>#14254F</code>.
The medallion rings use the proposed rank colours (one row shows today's for comparison).</p>

<section><h2>The whole collection at a glance</h2><div class="bare">{row_bare}</div></section>
<section><h2>In the medallion at 49 px on paper</h2><div class="strip paper">{row_paper}</div></section>
<section><h2>In the medallion at 33 px on a white panel</h2><div class="strip white">{row_white}</div></section>
<section><h2>Not yet earned, at 33 px (the application greys and fades them)</h2><div class="strip white">{row_grey}</div></section>
<section><h2>Option for Phase E: the badge fills the whole medallion (44 px), no second ring</h2><div class="strip white">{row_full}</div></section>
<section><h2>For comparison: today's medallion ring colours, 49 px</h2><div class="strip paper">{row_today}</div></section>
<section><h2>Bare, at 72 px, on the lapis bar</h2><div class="strip lapis">{row_lapis}</div></section>
<section class="two"><div><h2>Through the ranks, as on the student page</h2><ol class="way">{"".join(stations)}</ol></div>
</section>
<section><h2>Rank by rank</h2><div class="grid">{"".join(cards)}</div></section>
</main></body></html>"""


def exploration_sheet(explored):
    groups = {}
    for fname, label, svg in explored:
        groups.setdefault(fname.split("-a-")[0].split("-b-")[0].split("-c-")[0], []).append((label, svg))
    rows = []
    for g, items in groups.items():
        cells = "".join(f'<figure><img src="{data_uri(s)}" width="230" height="230" alt=""><div class="small">'
                        f'<img src="{data_uri(s)}" width="49" height="49" alt=""><img src="{data_uri(s)}" width="33" height="33" alt=""></div>'
                        f'<figcaption>{l}</figcaption></figure>' for l, s in items)
        rows.append(f'<div class="row">{cells}</div>')
    return f"""<!doctype html><html lang="en-GB"><head><meta charset="utf-8"><title>Rank badges: Phase B alternatives</title>
<style>body{{margin:0;background:#F7F8FA;font:15px/1.4 "Segoe UI",sans-serif;color:#172033;padding:16px}}
.row{{display:flex;gap:16px;margin-bottom:16px;flex-wrap:wrap}} figure{{margin:0;background:#fff;border:1px solid #D5DBE4;border-radius:10px;padding:12px;width:270px}}
.small{{display:flex;gap:10px;align-items:center;margin:8px 0}} figcaption{{font-weight:600}}</style></head>
<body>{"".join(rows)}</body></html>"""


if __name__ == "__main__":
    main()
