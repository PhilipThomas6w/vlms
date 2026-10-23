# Source for wwwroot/img/art/skyline.svg and skyline-mask.svg. Run: python docs/design/art-src/skyline.py src/Vlms.Web/wwwroot/img/art
# Generates the Constantinople skyline illustration (colour and one-colour silhouette).
# Original work for Varangians: flat, geometric, after Byzantine mosaic townscapes.
import math

W, H = 1200, 240
STONE, BRICK, LEAD, OCHRE, OCHRE_D = "#EADFC6", "#B5562F", "#6E86A6", "#D9906A", "#C27654"
DEEP, LAPIS, GOLD, PEARL, RED = "#14254F", "#1F45A3", "#E2B33C", "#F7F3E8", "#C2341F"
GREEN, PORPHYRY, WOOD = "#1B6B4C", "#7A2E52", "#7A3B1F"

out = []          # (shape, fill, in_silhouette)
def add(el, fill, sil=True): out.append((el, fill, sil))
def rect(x, y, w, h, fill, sil=True, rx=0): add(f'<rect x="{x}" y="{y}" width="{w}" height="{h}"' + (f' rx="{rx}"' if rx else '') + ' fill="{F}"/>', fill, sil)
def path(d, fill, sil=True): add(f'<path d="{d}" fill="{{F}}"/>', fill, sil)
def dome(cx, base, rx, ry, fill): path(f"M{cx-rx} {base} A{rx} {ry} 0 0 1 {cx+rx} {base} Z", fill)
def cross(cx, top, s=1.0):
    rect(cx - 1.2*s, top, 2.4*s, 14*s, GOLD)
    rect(cx - 5*s, top + 3.5*s, 10*s, 2.4*s, GOLD)
def arched_windows(x0, y, n, w, h, gap, fill=DEEP):
    for i in range(n):
        x = x0 + i*(w+gap)
        path(f"M{x} {y+h} V{y+w/2} A{w/2} {w/2} 0 0 1 {x+w} {y+w/2} V{y+h} Z", fill, False)
def merlons(x0, x1, y, w=7, gap=6, h=6, fill=STONE):
    x = x0
    while x + w <= x1:
        rect(x, y - h, w, h, fill); x += w + gap
def banded(x, y, w, h, base=STONE, band=BRICK, every=14, bh=3):
    rect(x, y, w, h, base)
    yy = y + every - bh
    while yy < y + h - 2:
        rect(x, yy, w, bh, band, False); yy += every
def cypress(cx, base, h):
    path(f"M{cx} {base-h} C{cx+9} {base-h*0.6} {cx+8} {base-h*0.15} {cx} {base} C{cx-8} {base-h*0.15} {cx-9} {base-h*0.6} {cx} {base-h} Z", GREEN)

# Lighthouse (the Pharos by the Great Palace), with its beacon.
banded(28, 78, 34, 122)
merlons(28, 62, 78, w=6, gap=4)
path("M45 50 C52 60 50 68 45 72 C40 68 38 60 45 50 Z", GOLD)
path("M45 58 C48 63 47 67 45 69 C43 67 42 63 45 58 Z", RED, False)

# Great Palace: a two-storey arcade above the sea walls.
banded(80, 128, 250, 72)
rect(80, 122, 250, 8, BRICK)
arched_windows(92, 160, 10, 16, 32, 7)
arched_windows(96, 134, 11, 10, 18, 11, PEARL)
cypress(345, 200, 70)

# Hagia Sophia: buttresses, semi-domes, the windowed drum and the great lead dome with its cross.
rect(388, 120, 304, 80, OCHRE)
rect(374, 92, 26, 108, OCHRE_D); rect(680, 92, 26, 108, OCHRE_D)
rect(374, 88, 26, 6, BRICK); rect(680, 88, 26, 6, BRICK)
dome(452, 122, 62, 36, LEAD)
dome(628, 122, 62, 36, LEAD)
rect(452, 94, 176, 28, OCHRE)
arched_windows(462, 100, 12, 8, 18, 6.6)
dome(540, 96, 96, 62, LEAD)
for i in range(1, 8):                      # ribs of the dome
    a = math.pi * i / 8
    x = 540 - 96*math.cos(a); y = 96 - 62*math.sin(a)
    add(f'<line x1="540" y1="34" x2="{x:.1f}" y2="{y:.1f}" stroke="{{F}}" stroke-width="1.6"/>', "#5A7092", False)
cross(540, 18, 1.1)
arched_windows(410, 150, 5, 12, 30, 9); arched_windows(586, 150, 5, 12, 30, 9)
rect(520, 160, 40, 40, DEEP, False)
path("M520 160 A20 20 0 0 1 560 160 Z", DEEP, False)

# Porphyry column of Constantine, crowned with a cross.
rect(733, 196, 26, 6, STONE)
rect(738, 76, 16, 120, PORPHYRY)
for y in (96, 118, 140, 162, 184): rect(737, y, 18, 3, GOLD, False)
rect(733, 70, 26, 7, GOLD)
cross(746, 50, 0.9)

# Church of the Holy Apostles: five domes on drums.
banded(778, 140, 200, 60)
for cx, base, r, top in ((812, 140, 22, 112), (944, 140, 22, 112), (878, 126, 30, 90)):
    rect(cx - r + 4, base - 14, 2*r - 8, 16, STONE)
    arched_windows(cx - r + 8, base - 11, max(3, r // 6), 5, 10, 4)
    dome(cx, base - 14, r - 2, r * 0.75, BRICK)
    cross(cx, top - 26, 0.8)
arched_windows(792, 160, 7, 11, 26, 15)
cypress(990, 200, 62)

# A small domed church and a bell tower to the east.
banded(1012, 150, 90, 50)
rect(1040, 136, 34, 16, STONE)
dome(1057, 138, 18, 15, LEAD)
cross(1057, 98, 0.75)
banded(1112, 110, 26, 90)
path("M1108 112 L1125 88 L1142 112 Z", BRICK)
cypress(1158, 200, 54)

# Sea walls across the front: stone with brick courses, merlons and towers.
banded(0, 182, W, 26, every=10, bh=3)
merlons(2, W, 182)
for tx in (14, 352, 716, 1000, 1166):
    banded(tx, 162, 30, 46, every=10, bh=3)
    merlons(tx, tx + 30, 162, w=6, gap=6)

# The sea: lapis, with gold and pearl waves after Byzantine wave borders.
rect(0, 208, W, 32, LAPIS, False)
for row, (y, col) in enumerate(((216, GOLD), (226, PEARL), (235, GOLD))):
    d = "".join(f"M{x + (row % 2) * 10} {y} q5 -5 10 0 " for x in range(0, W, 20))
    add(f'<path d="{d}" fill="none" stroke="{{F}}" stroke-width="1.6"/>', col, False)

# A ship under a lateen sail, a cross on the sail.
path("M1046 214 L1150 214 L1136 230 L1062 230 Z", WOOD, False)
rect(1096, 150, 3, 64, WOOD, False)
path("M1062 206 L1099 152 L1128 206 Z", PEARL, False)
add('<path d="M1095 172 V194 M1086 182 H1104" stroke="{F}" stroke-width="3"/>', RED, False)

def render(colour):
    body = []
    for el, fill, sil in out:
        if colour:
            body.append(el.replace("{F}", fill))
        elif sil:
            body.append(el.replace("{F}", "#000"))
    note = ("Constantinople by the sea: lighthouse, Great Palace, Hagia Sophia, the column of Constantine, the Holy Apostles, "
            "the sea walls and a ship. Original illustration for Varangians, after Byzantine mosaic townscapes.")
    if not colour:
        note = "One-colour silhouette of the Constantinople skyline (used as a CSS mask). Original work for Varangians."
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}">\n  <!-- {note} -->\n  ' + "\n  ".join(body) + "\n</svg>\n"

import sys
target = sys.argv[1]
open(target + "/skyline.svg", "w", encoding="utf-8").write(render(True))
open(target + "/skyline-mask.svg", "w", encoding="utf-8").write(render(False))
print("ok", len(out))
