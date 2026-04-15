#!/usr/bin/env python
"""
Extracts the NKS WDC logo icon from the Gemini-generated composite image,
removes the dark navy background, and writes sized PNG + ICO variants for
use as the Electron app icon, the browser favicon, and the frontend
in-app logo. Idempotent — safe to re-run.

Input : ~/Downloads/Gemini_Generated_Image_2aw6f2aw6f2aw6f2.png
Output: src/frontend/build/icon.ico           (Electron packager)
        src/frontend/build/icon.png           (raw 512x512)
        src/frontend/public/favicon.ico       (browser tab)
        src/frontend/src/assets/logo-icon.png (in-app square logo)
        src/frontend/src/assets/logo-wordmark.png (title bar)
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

try:
    from PIL import Image, ImageChops
except ImportError:
    sys.stderr.write("Pillow is required. Install with: pip install Pillow\n")
    sys.exit(1)


REPO_ROOT = Path(__file__).resolve().parents[1]
SRC = Path.home() / "Downloads" / "Gemini_Generated_Image_2aw6f2aw6f2aw6f2.png"


def load_source() -> Image.Image:
    if not SRC.exists():
        sys.stderr.write(f"Source image not found: {SRC}\n")
        sys.exit(1)
    return Image.open(SRC).convert("RGBA")


def remove_dark_background(img: Image.Image, tolerance: int = 48) -> Image.Image:
    """
    Makes near-black/navy pixels transparent. The Gemini composite uses a
    very dark navy (~#0B0F1A) as background and otherwise-bright fills
    for the icon; a luminance-threshold filter gives a clean cutout
    without the halo that chroma-keying would leave around anti-aliased
    edges.
    """
    pixels = img.load()
    w, h = img.size
    for y in range(h):
        for x in range(w):
            r, g, b, a = pixels[x, y]
            # Dark background: low luminance + near-uniform channels.
            lum = (r * 299 + g * 587 + b * 114) / 1000
            if lum < tolerance and max(r, g, b) - min(r, g, b) < 24:
                pixels[x, y] = (r, g, b, 0)
    return img


def autocrop(img: Image.Image) -> Image.Image:
    """Crops to the non-transparent bounding box."""
    bbox = img.getbbox()
    return img.crop(bbox) if bbox else img


def crop_top_icon(source: Image.Image) -> Image.Image:
    """
    The composite has the stand-alone icon in the top ~45% of the image,
    roughly centred. We crop that region, run background removal, then
    auto-trim to the actual icon bounds.
    """
    w, h = source.size
    # Empirical bounds tuned for this specific 600x330-ish layout: the
    # icon is roughly centred horizontally and lives in rows ~5 to 180.
    left = int(w * 0.33)
    right = int(w * 0.67)
    top = int(h * 0.02)
    bottom = int(h * 0.55)
    region = source.crop((left, top, right, bottom)).copy()
    region = remove_dark_background(region)
    return autocrop(region)


def crop_bottom_wordmark(source: Image.Image) -> Image.Image:
    """
    The 'NKS WDC / local dev, instantly' wordmark with its small logo
    is in the bottom half, left-aligned.
    """
    w, h = source.size
    region = source.crop((0, int(h * 0.55), int(w * 0.95), h)).copy()
    region = remove_dark_background(region)
    return autocrop(region)


def square(img: Image.Image) -> Image.Image:
    """Pads to a square canvas so scaling stays round."""
    w, h = img.size
    size = max(w, h)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(img, ((size - w) // 2, (size - h) // 2), img)
    return canvas


def ensure_dir(p: Path) -> None:
    p.parent.mkdir(parents=True, exist_ok=True)


def main() -> int:
    src = load_source()

    icon = square(crop_top_icon(src))
    wordmark = crop_bottom_wordmark(src)

    # Core 512×512 raw PNG (Electron + in-app asset)
    icon512 = icon.resize((512, 512), Image.LANCZOS)
    icon256 = icon.resize((256, 256), Image.LANCZOS)
    icon128 = icon.resize((128, 128), Image.LANCZOS)
    icon64 = icon.resize((64, 64), Image.LANCZOS)
    icon48 = icon.resize((48, 48), Image.LANCZOS)
    icon32 = icon.resize((32, 32), Image.LANCZOS)
    icon16 = icon.resize((16, 16), Image.LANCZOS)

    # Electron packager wants a multi-resolution .ico AND a PNG source.
    build_dir = REPO_ROOT / "src" / "frontend" / "build"
    build_dir.mkdir(parents=True, exist_ok=True)
    icon512.save(build_dir / "icon.png", "PNG")
    # Pillow's ICO writer accepts a `sizes` kwarg for multi-res ico.
    icon512.save(
        build_dir / "icon.ico",
        "ICO",
        sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # Browser favicon next to index.html
    public_dir = REPO_ROOT / "src" / "frontend" / "public"
    public_dir.mkdir(parents=True, exist_ok=True)
    icon256.save(
        public_dir / "favicon.ico",
        "ICO",
        sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    icon512.save(public_dir / "logo.png", "PNG")

    # In-app assets (imported as URL by Vite)
    assets_dir = REPO_ROOT / "src" / "frontend" / "src" / "assets"
    assets_dir.mkdir(parents=True, exist_ok=True)
    icon256.save(assets_dir / "logo-icon.png", "PNG")
    wordmark_w, wordmark_h = wordmark.size
    if wordmark_w > 1024:
        wordmark = wordmark.resize((1024, int(wordmark_h * 1024 / wordmark_w)), Image.LANCZOS)
    wordmark.save(assets_dir / "logo-wordmark.png", "PNG")

    print(f"icon.ico      : {build_dir / 'icon.ico'}")
    print(f"icon.png      : {build_dir / 'icon.png'}")
    print(f"favicon.ico   : {public_dir / 'favicon.ico'}")
    print(f"logo-icon.png : {assets_dir / 'logo-icon.png'} ({icon256.size})")
    print(f"logo-wordmark : {assets_dir / 'logo-wordmark.png'} ({wordmark.size})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
