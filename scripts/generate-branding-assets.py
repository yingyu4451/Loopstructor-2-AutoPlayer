from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw


CANVAS = 1024
SUPERSAMPLE = 4
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

COAL = "#12100D"
SOOT = "#211A13"
OUTLINE = "#090705"
BRONZE_DARK = "#3A2517"
BRONZE = "#70492B"
BRASS = "#B27A3D"
GOLD = "#F0B23D"
GREEN = "#78D13E"
BLUE = "#328CC5"
RED = "#D74B31"
INK = "#F1EBDD"


def scaled(value: float) -> int:
    return round(value * SUPERSAMPLE)


def point(cx: float, cy: float, radius: float, angle: float) -> tuple[int, int]:
    return scaled(cx + math.cos(angle) * radius), scaled(cy + math.sin(angle) * radius)


def gear_points(cx: float, cy: float, root_radius: float, tip_radius: float, teeth: int = 16):
    points: list[tuple[int, int]] = []
    tooth_step = math.tau / teeth
    offsets = (-0.48, -0.25, 0.25, 0.48)
    radii = (root_radius, tip_radius, tip_radius, root_radius)
    for tooth in range(teeth):
        center_angle = -math.pi / 2 + tooth * tooth_step
        for offset, radius in zip(offsets, radii):
            points.append(point(cx, cy, radius, center_angle + offset * tooth_step))
    return points


def ellipse_box(cx: float, cy: float, radius: float):
    return tuple(scaled(v) for v in (cx - radius, cy - radius, cx + radius, cy + radius))


def rounded_line(draw: ImageDraw.ImageDraw, points, fill: str, width: float):
    draw.line([(scaled(x), scaled(y)) for x, y in points], fill=fill, width=scaled(width), joint="curve")
    radius = width / 2
    for x, y in (points[0], points[-1]):
        draw.ellipse(ellipse_box(x, y, radius), fill=fill)


def draw_gear_badge(draw: ImageDraw.ImageDraw) -> None:
    center = (512, 512)
    shadow = gear_points(520, 524, 388, 465)
    draw.polygon(shadow, fill="#050403B8")

    gear = gear_points(*center, 386, 458)
    draw.polygon(gear, fill=BRONZE_DARK)
    draw.line(gear + [gear[0]], fill=OUTLINE, width=scaled(28), joint="curve")

    draw.ellipse(ellipse_box(*center, 390), fill=BRONZE, outline=OUTLINE, width=scaled(24))
    draw.ellipse(ellipse_box(*center, 342), fill=BRASS, outline=GOLD, width=scaled(10))
    draw.ellipse(ellipse_box(*center, 314), fill=BRONZE_DARK, outline=OUTLINE, width=scaled(22))
    draw.ellipse(ellipse_box(*center, 278), fill=COAL, outline=BRASS, width=scaled(10))
    draw.ellipse(ellipse_box(*center, 252), fill=SOOT, outline="#28170D", width=scaled(8))

    for index in range(8):
        angle = -math.pi / 2 + index * math.pi / 4
        x = 512 + math.cos(angle) * 354
        y = 512 + math.sin(angle) * 354
        draw.ellipse(ellipse_box(x, y, 27), fill=OUTLINE, outline=GOLD, width=scaled(6))
        draw.ellipse(ellipse_box(x - 3, y - 4, 10), fill=BRASS)

    draw.arc(ellipse_box(*center, 369), 204, 333, fill="#E6B75A", width=scaled(8))
    draw.arc(ellipse_box(*center, 369), 24, 152, fill="#402616", width=scaled(9))


def draw_manager_mark(draw: ImageDraw.ImageDraw) -> None:
    center = (512, 512)
    orbit_box = ellipse_box(*center, 200)
    draw.arc(orbit_box, 205, 346, fill=BLUE, width=scaled(36))
    draw.arc(orbit_box, 22, 165, fill=GREEN, width=scaled(36))

    route = [(350, 580), (430, 420), (586, 374), (683, 522), (616, 654)]
    rounded_line(draw, route, "#0A0806", 42)
    rounded_line(draw, route, BLUE, 18)
    for index, (x, y) in enumerate(route):
        color = GREEN if index in (0, len(route) - 1) else BLUE
        draw.ellipse(ellipse_box(x, y, 28), fill=color, outline=OUTLINE, width=scaled(10))

    triangle = [(456, 372), (456, 652), (686, 512)]
    triangle_scaled = [(scaled(x), scaled(y)) for x, y in triangle]
    draw.polygon(triangle_scaled, fill=GREEN)
    draw.line(triangle_scaled + [triangle_scaled[0]], fill=OUTLINE, width=scaled(28), joint="curve")
    inner = [(492, 430), (492, 594), (628, 512)]
    draw.polygon([(scaled(x), scaled(y)) for x, y in inner], fill="#D9F4C6")


def draw_cheat_mark(draw: ImageDraw.ImageDraw) -> None:
    diamond = [(512, 290), (734, 512), (512, 734), (290, 512)]
    diamond_scaled = [(scaled(x), scaled(y)) for x, y in diamond]
    draw.polygon(diamond_scaled, fill=RED)
    draw.line(diamond_scaled + [diamond_scaled[0]], fill=OUTLINE, width=scaled(34), joint="curve")

    inner = [(512, 338), (686, 512), (512, 686), (338, 512)]
    inner_scaled = [(scaled(x), scaled(y)) for x, y in inner]
    draw.polygon(inner_scaled, fill="#6E2119")
    draw.line(inner_scaled + [inner_scaled[0]], fill=GOLD, width=scaled(12), joint="curve")

    rounded_line(draw, [(390, 640), (610, 420)], OUTLINE, 88)
    rounded_line(draw, [(390, 640), (610, 420)], GOLD, 50)
    draw.ellipse(ellipse_box(377, 653, 55), fill=GOLD, outline=OUTLINE, width=scaled(18))
    draw.ellipse(ellipse_box(377, 653, 18), fill=COAL)

    draw.ellipse(ellipse_box(625, 405, 78), fill=GOLD, outline=OUTLINE, width=scaled(18))
    cut = [(625, 405), (694, 360), (720, 438), (648, 462)]
    draw.polygon([(scaled(x), scaled(y)) for x, y in cut], fill=SOOT)

    lightning = [(560, 332), (462, 512), (535, 512), (468, 694), (654, 470), (575, 470), (660, 332)]
    lightning_scaled = [(scaled(x), scaled(y)) for x, y in lightning]
    draw.polygon(lightning_scaled, fill=INK)
    draw.line(lightning_scaled + [lightning_scaled[0]], fill=OUTLINE, width=scaled(18), joint="curve")
    highlight = [(574, 365), (505, 488), (568, 488), (527, 591), (615, 482), (551, 482), (618, 365)]
    draw.polygon([(scaled(x), scaled(y)) for x, y in highlight], fill=GOLD)


def render(kind: str) -> Image.Image:
    image = Image.new("RGBA", (CANVAS * SUPERSAMPLE, CANVAS * SUPERSAMPLE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw_gear_badge(draw)
    if kind == "manager":
        draw_manager_mark(draw)
    elif kind == "cheat":
        draw_cheat_mark(draw)
    else:
        raise ValueError(f"Unknown logo kind: {kind}")
    return image.resize((CANVAS, CANVAS), Image.Resampling.LANCZOS)


def save_assets(output_directory: Path, kind: str) -> None:
    master = render(kind)
    master_path = output_directory / f"{kind}-logo-1024.png"
    ui_path = output_directory / f"{kind}-logo-256.png"
    icon_path = output_directory / f"{kind}.ico"

    master.save(master_path, optimize=True)
    master.resize((256, 256), Image.Resampling.LANCZOS).save(ui_path, optimize=True)
    master.save(icon_path, format="ICO", sizes=[(size, size) for size in ICO_SIZES])


def main() -> None:
    repository_root = Path(__file__).resolve().parent.parent
    output_directory = repository_root / "assets" / "branding"
    output_directory.mkdir(parents=True, exist_ok=True)
    for kind in ("manager", "cheat"):
        save_assets(output_directory, kind)


if __name__ == "__main__":
    main()
