from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
BRAND = ROOT / "Branding"
SOURCE = BRAND / "GLook-Mark-Generated-Master.png"
ASSETS = ROOT / "Assets"

NAVY = (10, 37, 86, 255)
WHITE = (255, 255, 255, 255)
TRANSPARENT = (0, 0, 0, 0)


def resample() -> int:
    return Image.Resampling.LANCZOS


def load_trimmed_mark() -> Image.Image:
    image = Image.open(SOURCE).convert("RGBA")
    alpha = image.getchannel("A")
    bounds = alpha.getbbox()
    if bounds is None:
        raise RuntimeError("The generated mark has no visible pixels.")
    return image.crop(bounds)


def square_mark(mark: Image.Image, size: int, padding_fraction: float = 0.09) -> Image.Image:
    canvas = Image.new("RGBA", (size, size), TRANSPARENT)
    padding = round(size * padding_fraction)
    available = size - (padding * 2)
    scale = min(available / mark.width, available / mark.height)
    resized = mark.resize(
        (max(1, round(mark.width * scale)), max(1, round(mark.height * scale))),
        resample(),
    )
    x = (size - resized.width) // 2
    y = (size - resized.height) // 2
    canvas.alpha_composite(resized, (x, y))
    return canvas


def rounded_background(size: tuple[int, int], color: tuple[int, int, int, int]) -> Image.Image:
    image = Image.new("RGBA", size, TRANSPARENT)
    draw = ImageDraw.Draw(image)
    radius = round(min(size) * 0.11)
    draw.rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=color)
    return image


def wordmark(mark: Image.Image, *, dark: bool) -> Image.Image:
    width, height = 2400, 800
    background = TRANSPARENT
    canvas = Image.new("RGBA", (width, height), background)

    icon = square_mark(mark, 640, 0.06)
    canvas.alpha_composite(icon, (70, 80))

    font_path = Path(r"C:\Windows\Fonts\segoeuib.ttf")
    font = ImageFont.truetype(str(font_path), 330)
    text_color = WHITE if dark else NAVY
    draw = ImageDraw.Draw(canvas)
    label = "GLook"
    bounds = draw.textbbox((0, 0), label, font=font)
    text_height = bounds[3] - bounds[1]
    y = (height - text_height) // 2 - bounds[1]
    draw.text((735, y), label, font=font, fill=text_color)
    return canvas


def save_png(image: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=True)


def contain(image: Image.Image, size: tuple[int, int], padding: float = 0.08) -> Image.Image:
    canvas = Image.new("RGBA", size, TRANSPARENT)
    usable_width = round(size[0] * (1 - 2 * padding))
    usable_height = round(size[1] * (1 - 2 * padding))
    scale = min(usable_width / image.width, usable_height / image.height)
    resized = image.resize(
        (max(1, round(image.width * scale)), max(1, round(image.height * scale))),
        resample(),
    )
    canvas.alpha_composite(
        resized,
        ((size[0] - resized.width) // 2, (size[1] - resized.height) // 2),
    )
    return canvas


def main() -> None:
    mark = load_trimmed_mark()
    BRAND.mkdir(parents=True, exist_ok=True)

    # The checked-in generated master is the stable, portable source for every export.
    mark_2048 = square_mark(mark, 2048)
    save_png(mark_2048, BRAND / "GLook-Mark-Transparent-2048.png")

    light_tile = rounded_background((2048, 2048), WHITE)
    light_tile.alpha_composite(square_mark(mark, 2048, 0.12))
    save_png(light_tile, BRAND / "GLook-Mark-Light-2048.png")

    dark_tile = rounded_background((2048, 2048), NAVY)
    dark_tile.alpha_composite(square_mark(mark, 2048, 0.12))
    save_png(dark_tile, BRAND / "GLook-Mark-Dark-2048.png")

    light_wordmark = wordmark(mark, dark=False)
    dark_wordmark = wordmark(mark, dark=True)
    save_png(light_wordmark, BRAND / "GLook-Logo-Transparent-DarkText-2400x800.png")
    save_png(dark_wordmark, BRAND / "GLook-Logo-Transparent-LightText-2400x800.png")

    # A multi-resolution icon for the unpackaged executable and shortcuts.
    ico_master = square_mark(mark, 256, 0.08)
    ico_path = BRAND / "GLook.ico"
    ico_master.save(
        ico_path,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    ico_master.save(
        ASSETS / "GLook.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )

    # Windows packaging assets at the exact scale-200 dimensions expected by the manifest.
    square_assets = {
        "Square150x150Logo.scale-200.png": 300,
        "Square44x44Logo.scale-200.png": 88,
        "Square44x44Logo.targetsize-24_altform-unplated.png": 24,
        "StoreLogo.png": 50,
    }
    for filename, size in square_assets.items():
        save_png(square_mark(mark, size, 0.08), ASSETS / filename)

    save_png(
        contain(light_wordmark, (620, 300), 0.08),
        ASSETS / "Wide310x150Logo.scale-200.png",
    )
    save_png(
        contain(light_wordmark, (1240, 600), 0.18),
        ASSETS / "SplashScreen.scale-200.png",
    )

    print(f"Created branding exports in {BRAND}")
    print(f"Updated Windows package assets in {ASSETS}")


if __name__ == "__main__":
    main()
