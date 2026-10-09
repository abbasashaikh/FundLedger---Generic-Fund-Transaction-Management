"""Generates the PWA icons in public/icons from the brand tokens (Design Brief §3).
Run: python scripts/generate-icons.py   (requires Pillow)
Placeholder mark until a designed logo exists: a rupee glyph on the primary teal."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

PRIMARY = (15, 118, 110)      # --color-primary #0F766E
WHITE = (255, 255, 255)
OUT = Path(__file__).resolve().parent.parent / "public" / "icons"
FONTS = [r"C:\Windows\Fonts\NirmalaB.ttf", r"C:\Windows\Fonts\Nirmala.ttc", r"C:\Windows\Fonts\arialbd.ttf",
         "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"]


def font(size: int) -> ImageFont.FreeTypeFont:
    for path in FONTS:
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    raise SystemExit("No font with a rupee glyph found")


def icon(size: int, maskable: bool) -> Image.Image:
    img = Image.new("RGBA", (size, size), PRIMARY if maskable else (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    if not maskable:
        draw.rounded_rectangle((0, 0, size - 1, size - 1), radius=size // 5, fill=PRIMARY)
    # Maskable icons keep content inside the central 80% safe zone.
    glyph_size = int(size * (0.46 if maskable else 0.6))
    f = font(glyph_size)
    box = draw.textbbox((0, 0), "₹", font=f)
    w, h = box[2] - box[0], box[3] - box[1]
    draw.text(((size - w) / 2 - box[0], (size - h) / 2 - box[1]), "₹", font=f, fill=WHITE)
    return img


OUT.mkdir(parents=True, exist_ok=True)
for size in (192, 512):
    icon(size, maskable=False).save(OUT / f"icon-{size}.png")
icon(512, maskable=True).save(OUT / "icon-maskable-512.png")
icon(180, maskable=True).convert("RGB").save(OUT / "apple-touch-icon.png")
print("icons written to", OUT)
