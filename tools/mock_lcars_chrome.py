r"""
mock_lcars_chrome.py - preview of TssBase.Chrome (TextPanels.cs): Ground Truth's header with the slim LCARS
accent, drawn with the same rectangles and circles, in the same canvas units, as the C#.

    python tools\mock_lcars_chrome.py   -> docs\images\lcars_chrome_mock.png

The lettering comes from the LLM pack's SEFont (Keen's white_shadow bitmap = the "Debug" font GT draws with), so
the mock reads like the game. The panel content is only a stand-in to judge the accent against.
"""
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r"G:\space engineers\TD Large Logic Model\tools")
import make_console_art as A                                           # noqa: E402
from PIL import Image, ImageDraw                                        # noqa: E402

S = A.S
BG = (0, 0, 0)
FG = (179, 237, 255)            # SE's default script foreground
PAD = 22.0

# must match TssBase.Chrome
EX, EY, SW, BH, RO, RI = 6.0, 55.0, 9.0, 5.0, 12.0, 6.0
ELBOW_RUN = 64.0
SIDE_END = 128.0
LC = 0.75
PEACH, LAV, PERI = (255, 168, 110), (172, 150, 222), (112, 142, 232)


def mul(c, k):
    return tuple(min(255, int(v * k)) for v in c)


def rect(d, x0, y0, x1, y1, c):
    d.rectangle([A.P(x0), A.P(y0), A.P(x1), A.P(y1)], fill=c)


def circ(d, cx, cy, r, c):
    d.ellipse([A.P(cx - r), A.P(cy - r), A.P(cx + r), A.P(cy + r)], fill=c)


def chrome(d, w, title, source):
    ink = mul(FG, 0.9)
    A.se(d, (PAD, 16 + 25), title, 0.85 * 27, FG)
    A.se(d, (w - PAD, 19 + 22), source, 0.7 * 27, FG, anchor="rs")
    # the elbow
    circ(d, EX + RO, EY + RO, RO, ink)
    rect(d, EX + SW, EY + BH, EX + 2 * RO, EY + 2 * RO, BG)
    rect(d, EX, EY + RO, EX + SW, SIDE_END, ink)
    rect(d, EX + RO, EY, EX + ELBOW_RUN, EY + BH, ink)
    rect(d, EX + SW, EY + BH, EX + SW + RI, EY + BH + RI, ink)
    circ(d, EX + SW + RI, EY + BH + RI, RI, BG)
    # sidebar blocks
    rect(d, EX, SIDE_END + 6, EX + SW, SIDE_END + 42, mul(PEACH, LC))
    rect(d, EX, SIDE_END + 48, EX + SW, SIDE_END + 62, mul(LAV, LC))
    # rule and right-end segments
    segs = ((40.0, PEACH), (14.0, LAV), (26.0, PERI))
    total = sum(s for s, _ in segs) + 4 * (len(segs) - 1)
    right = w - PAD
    start = right - total
    if start - 6 > EX + ELBOW_RUN + 20:
        rect(d, EX + ELBOW_RUN, 57, start - 6, 59, ink)
        x = start
        for i, (sw, c) in enumerate(segs):
            c = mul(c, LC)
            last = i == len(segs) - 1
            rect(d, x, EY, x + sw - (BH / 2 if last else 0), EY + BH, c)
            if last:
                circ(d, x + sw - BH / 2, EY + BH / 2, BH / 2, c)
            x += sw + 4
    else:
        rect(d, EX + ELBOW_RUN, 57, right, 59, ink)


def standin(d, w):
    # radiation-app-like content, to judge the accent against
    A.TRACK = (40, 46, 56)
    A.arc_gauge(d, 132, 212, 84, 0.42, (90, 220, 130), segs=30, width=16)
    A.se(d, (132, 214), "0.12", 40, (90, 220, 130), anchor="mm")
    A.se(d, (132, 320), "mSv/h", 16, mul(FG, 0.55), anchor="mm")
    A.se(d, (260, 150), "EXPOSURE", 14, mul(FG, 0.55))
    A.se(d, (260, 186), "NOMINAL", 30, (90, 220, 130))
    for k in range(3):
        A.se(d, (PAD, 400 + k * 30), ("SOL", "PLA", "COS")[k], 14, mul(FG, 0.55))
        rect(d, PAD + 50, 390 + k * 30, w - PAD, 400 + k * 30, (40, 46, 56))
        rect(d, PAD + 50, 390 + k * 30, PAD + 50 + (w - PAD * 2 - 50) * (0.7, 0.3, 0.1)[k], 400 + k * 30,
             ((255, 205, 90), (120, 225, 225), (190, 130, 255))[k])


def panel(aspect, title, source):
    w = 512 * aspect
    im = Image.new("RGB", (A.P(w), A.P(512)), BG)
    d = ImageDraw.Draw(im)
    chrome(d, w, title, source)
    standin(d, w)
    return im.resize((int(w), 512), Image.LANCZOS)


def main():
    a = panel(1.0, "RADIATION", "Radiation Monitor")
    b = panel(1.6, "HABITAT", "Habitat - Crew Deck")
    out = Image.new("RGB", (a.width + b.width + 24, 512), (60, 60, 60))
    out.paste(a, (0, 0))
    out.paste(b, (a.width + 24, 0))
    path = os.path.join(ROOT, "docs", "images", "lcars_chrome_mock.png")
    out.save(path)
    print(path)


if __name__ == "__main__":
    main()
