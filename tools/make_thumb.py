import os
from PIL import Image, ImageDraw, ImageFont, ImageEnhance

SRC = os.path.join(os.environ['USERPROFILE'], r'Pictures\Screenshots\Screenshot 2026-08-15 102246.png')
OUTDIR = r'G:\space engineers\Ground Truth\tools'

W, H = 1536, 1024


def base():
    im = Image.open(SRC).convert('RGB')
    src_w, src_h = im.size
    ratio = W / H

    # Crop to 3:2 from the full width, biased DOWN slightly: the cluster sits low and
    # the upper half is empty space, which is atmosphere but not information.
    crop_w = src_w
    crop_h = int(crop_w / ratio)
    if crop_h > src_h:
        crop_h = src_h
        crop_w = int(crop_h * ratio)
    left = int((src_w - crop_w) * 0.85)
    top = int((src_h - crop_h) * 0.62)
    im = im.crop((left, top, left + crop_w, top + crop_h)).resize((W, H), Image.LANCZOS)

    # Gentle lift only - this shot is already well exposed.
    im = ImageEnhance.Contrast(im).enhance(1.06)
    return im


def font(size):
    for candidate in ('bahnschrift.ttf', 'segoeuib.ttf', 'arialbd.ttf'):
        try:
            return ImageFont.truetype(candidate, size)
        except Exception:
            continue
    return ImageFont.load_default()


AMBER = (255, 176, 46)
WHITE = (240, 246, 252)
DIM = (158, 172, 188)


def shade_corner(im, strength=200):
    """Darken the lower-left wedge only, so the cluster on the right stays clean."""
    mask = Image.new('L', (W, H), 0)
    d = ImageDraw.Draw(mask)
    for y in range(H):
        ty = max(0.0, (y - H * 0.46) / (H * 0.54))
        for_x = int(W * (0.72 - 0.10 * ty))
        d.line([(0, y), (for_x, y)], fill=int(strength * (ty ** 1.2)))
    mask = mask.filter(__import__('PIL.ImageFilter', fromlist=['ImageFilter']).GaussianBlur(90))
    dark = Image.new('RGB', (W, H), (3, 6, 11))
    return Image.composite(dark, im, mask)


def variant_low_left():
    im = shade_corner(base())
    d = ImageDraw.Draw(im)
    x, y = 70, H - 250
    d.text((x, y), 'GROUND TRUTH', font=font(96), fill=WHITE)
    y += 108
    d.line([(x + 4, y + 4), (x + 4, y + 48)], fill=AMBER, width=6)
    d.text((x + 26, y), 'ENVIRONMENTAL INSTRUMENTS', font=font(42), fill=AMBER)
    y += 66
    d.text((x + 26, y), 'radiation  ·  weather  ·  pressure  ·  life', font=font(33), fill=DIM)
    return im


def variant_quiet():
    """Name only. Steam prints the title under the thumbnail anyway."""
    im = shade_corner(base(), 170)
    d = ImageDraw.Draw(im)
    x, y = 78, H - 210
    d.text((x, y), 'GROUND TRUTH', font=font(120), fill=WHITE)
    d.line([(x + 4, y + 148), (x + 430, y + 148)], fill=AMBER, width=6)
    return im


def variant_no_text():
    return base()


for name, fn in (('thumb_A_low_left', variant_low_left),
                 ('thumb_B_quiet', variant_quiet),
                 ('thumb_C_clean', variant_no_text)):
    p = os.path.join(OUTDIR, name + '.jpg')
    fn().save(p, 'JPEG', quality=93)
    print('%-22s %.0f KB' % (name, os.path.getsize(p) / 1024))
