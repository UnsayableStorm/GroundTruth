"""Ground Truth - Workshop thumbnail in the Threshold Dynamics house layout (Long Slopes Kit / Docking Connector Camera).

    python tools/make_thumb.py

Renders the SHIPPED models (reference-library render_mwm_views.py): the five Mk1 instruments on the left, the four Mk2 variants on the right. Writes thumb.jpg into the mod root (this folder IS the mod folder);
deploy-local.ps1 ships it, and the game uploads it on publish (MyWorkshop looks for thumb.png, then thumb.jpg).
Renders are cached in G:\\space engineers\\workshop_images\\GroundTruth\\_build\\thumb - never inside the mod.
Replaces the 2026-08 screenshot-based version (see git history).
"""
import os
import re
import sys

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, r"G:\space engineers\reference-library\tools")
import td_art  # noqa: E402

MOD = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = r"G:\space engineers\workshop_images\GroundTruth\_build\thumb"
VANILLA = r"F:\SteamLibrary\steamapps\common\SpaceEngineers\Content"
VIEW, UP = [0.62, 0.48, -0.75], [0, 1, 0]

MK1 = [("GT_RadiationMonitor", "RADIATION"), ("GT_WeatherStation", "WEATHER"), ("GT_HabitatMonitor", "HABITAT"),
       ("GT_BioScanner", "BIO SCANNER"), ("GT_RotatingRadarDish", "ANTENNA")]
MK2 = [("GT_RadiationMonitorAlt", "RADIATION MK2"), ("GT_WeatherStationAlt", "WEATHER MK2"),
       ("GT_HabitatMonitorAlt", "HABITAT MK2"), ("GT_BioScannerAlt", "BIO SCANNER MK2")]

W, H = 1536, 1024
FONTS = r"C:\Windows\Fonts"
F_TITLE, F_MONO, F_MONO_R = (os.path.join(FONTS, f) for f in ("bahnschrift.ttf", "consolab.ttf", "consola.ttf"))
INK, GRID, RULE, TEXT, DIM = (19, 22, 25), (24, 28, 33), (58, 66, 74), (237, 241, 244), (138, 148, 158)
RAMP = [(200, 84, 30), (232, 120, 31), (255, 158, 44)]


def models():
    t = open(os.path.join(MOD, "Data", "CubeBlocks_GroundTruth.sbc"), encoding="utf-8-sig").read()
    out = {}
    for d in re.findall(r"<Definition\b.*?</Definition>", t, re.S):
        sub = re.search(r"<SubtypeId>(.*?)</SubtypeId>", d).group(1)
        rel = re.search(r"<Model>(.*?)</Model>", d).group(1)
        own = os.path.join(MOD, rel)
        out[sub] = own if os.path.isfile(own) else os.path.join(VANILLA, rel)     # the Habitat Monitor is Keen's panel
    return out


def crop(png):
    im = Image.open(png).convert("RGBA")
    return im.crop(im.getbbox())


def main():
    m = models()
    jobs = []
    for sub, _ in MK1 + MK2:
        jobs.append(td_art.job(MOD, m[sub], os.path.join(CACHE, sub + ".png"), VIEW, UP, 1200))
    td_art.render(jobs, os.path.join(CACHE, "render.log"))

    im = Image.new("RGB", (W, H), INK)
    d = ImageDraw.Draw(im)
    for x in range(0, W, 64):
        d.line([(x, 0), (x, H)], fill=GRID)
    for y in range(0, H, 64):
        d.line([(0, y), (W, y)], fill=GRID)
    f_eye = ImageFont.truetype(F_MONO, 30)
    d.text((84, 64), "THRESHOLD DYNAMICS", font=f_eye, fill=RAMP[1])
    dept = "SURVEY AND ASSESSMENT"
    d.text((W - 84 - d.textlength(dept, font=f_eye), 64), dept, font=f_eye, fill=DIM)
    d.text((84, 108), "GROUND TRUTH", font=ImageFont.truetype(F_TITLE, 132), fill=TEXT)
    d.text((88, 238), "ENVIRONMENTAL INSTRUMENTS", font=ImageFont.truetype(F_TITLE, 40), fill=DIM)
    top = 300
    d.line([(84, top), (W - 84, top)], fill=RULE, width=3)

    # hero: the five Mk1 instruments, each fitted to the same box - three over two. NOT one shared scale: the point is the
    # range of instruments, and at true scale the Habitat Monitor (a small wall panel) shrinks to a speck.
    RX = 968
    f_lab = ImageFont.truetype(F_MONO, 22)
    crops = [crop(os.path.join(CACHE, s + ".png")) for s, _ in MK1]
    BOXW, BOXH = 230, 236
    crops = [c.resize((max(1, round(c.width * min(BOXW / c.width, BOXH / c.height))),
                       max(1, round(c.height * min(BOXW / c.width, BOXH / c.height)))), Image.LANCZOS) for c in crops]
    rows = [(MK1[:3], crops[:3], top + 300), (MK1[3:], crops[3:], H - 150)]
    for items, cs, floor in rows:
        slot = (RX - 84) / 3
        x0 = 84 + (RX - 84 - slot * len(cs)) / 2
        for i, ((sub, label), c) in enumerate(zip(items, cs)):
            cx = x0 + slot * i + slot / 2
            im.paste(c, (int(cx - c.width / 2), floor - c.height - 10), c)
            d.text((cx - d.textlength(label, font=f_lab) / 2, floor), label, font=f_lab, fill=TEXT)

    # right: the Mk2 variants
    d.line([(RX, top), (RX, H - 118)], fill=RULE, width=2)
    f_small = ImageFont.truetype(F_MONO_R, 20)
    d.text((RX + 40, top + 26), "MK2 VARIANTS", font=f_small, fill=DIM)
    CELL_W, CELL_H, BOX = 232, 268, 180
    for i, (sub, label) in enumerate(MK2):
        cx0, cy0 = RX + 44 + (i % 2) * CELL_W, top + 74 + (i // 2) * CELL_H
        c = crop(os.path.join(CACHE, sub + ".png"))
        s = min(BOX / c.width, BOX / c.height)
        c = c.resize((max(1, round(c.width * s)), max(1, round(c.height * s))), Image.LANCZOS)
        im.paste(c, (int(cx0 + (BOX - c.width) / 2), int(cy0 + (BOX - c.height) / 2)), c)
        d.text((cx0, cy0 + BOX + 12), label, font=f_small, fill=TEXT)

    d.line([(84, H - 96), (W - 84, H - 96)], fill=RULE, width=3)
    d.text((84, H - 74), "18 BLOCKS  /  LARGE + SMALL GRID  /  RADIATION, WEATHER, PRESSURE, LIFE",
           font=ImageFont.truetype(F_MONO, 25), fill=RAMP[1])
    out = os.path.join(MOD, "thumb.jpg")
    im.save(out, "JPEG", quality=93, subsampling=0)
    print("wrote", out, os.path.getsize(out) // 1024, "KB")


if __name__ == "__main__":
    main()
