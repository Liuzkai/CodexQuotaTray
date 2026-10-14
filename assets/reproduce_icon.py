#!/usr/bin/env python3
"""Recreate the application ICO from an inert, declarative geometry specification.

Requires Python 3 and Pillow. No repository executables or downloaded code are
invoked. Paths are relative to this file; outputs are written beside this file.
See ICON-PROVENANCE.txt for the original design, license, and rasterization caveat.
"""

from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import struct
import zlib

from PIL import Image, ImageChops, ImageDraw, __version__ as pillow_version


ROOT = Path(__file__).resolve().parent
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def rounded_box_mask(size, bounds, radius, expansion=0):
    x, y, width, height = bounds
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (x - expansion, y - expansion,
         x + width + expansion, y + height + expansion),
        radius=radius + expansion, fill=255,
    )
    return mask


def render_icon(spec, output_size):
    render_size = max(spec["minimum_render_size"], output_size * spec["render_scale"])
    render_scale = render_size / spec["canvas_size"]
    aa = spec["pillow_antialias_scale"]
    draw_size = render_size * aa
    scale = render_scale * aa
    card = spec["card"]
    bounds = [value * scale for value in card["bounds"]]
    radius = card["corner_radius"] * scale
    card_mask = rounded_box_mask(draw_size, bounds, radius)

    # Project onto the source gradient's clockwise 55-degree direction.
    # A square has identical fixed/scalable-angle gradient directions.
    x, y, width, height = bounds
    angle = math.radians(card["gradient_angle_degrees"])
    cx, cy = math.cos(angle), math.sin(angle)
    length = width * cx + height * cy
    gradient = Image.linear_gradient("L").transform(
        (draw_size, draw_size), Image.Transform.AFFINE,
        (0, 0, 128, 255 * cx / length, 255 * cy / length,
         -255 * (x * cx + y * cy) / length),
        Image.Resampling.BICUBIC,
    )
    start, end = card["gradient_start_rgb"], card["gradient_end_rgb"]
    channels = [gradient.point([
        round(start[channel] + (end[channel] - start[channel]) * n / 255)
        for n in range(256)
    ]) for channel in range(3)]
    canvas = Image.merge("RGBA", (*channels, card_mask))

    # The border is centered on the original rounded-rectangle path.
    border_width = max(card["minimum_border_width_at_render_size"],
                       card["border_width"] * render_scale) * aa
    outer = rounded_box_mask(draw_size, bounds, radius, border_width / 2)
    inner = rounded_box_mask(draw_size, bounds, radius, -border_width / 2)
    canvas.paste(tuple(card["border_rgb"]) + (255,), (0, 0),
                 ImageChops.subtract(outer, inner))

    drawing = ImageDraw.Draw(canvas)
    for arc in spec["arcs"]:
        if output_size < arc.get("minimum_output_size", 0):
            continue
        x, y, width, height = [value * scale for value in arc["bounds"]]
        stroke = max(arc["minimum_width_at_render_size"],
                     arc["width"] * render_scale) * aa
        first = math.radians(arc["start_degrees"])
        sweep = math.radians(arc["sweep_degrees"])
        # Dense points preserve fractional geometry; final bicubic reduction
        # antialiases the strokes. Clockwise angles match the original renderer.
        count = max(32, math.ceil(abs(sweep) * max(width, height)))
        points, outer_edge, inner_edge = [], [], []
        for i in range(count + 1):
            angle = first + sweep * i / count
            cosine, sine = math.cos(angle), math.sin(angle)
            px = x + width / 2 + width / 2 * cosine
            py = y + height / 2 + height / 2 * sine
            nx, ny = cosine / (width / 2), sine / (height / 2)
            normal_length = math.hypot(nx, ny)
            dx, dy = nx / normal_length * stroke / 2, ny / normal_length * stroke / 2
            points.append((px, py))
            outer_edge.append((px + dx, py + dy))
            inner_edge.append((px - dx, py - dy))
        color = tuple(arc["rgb"]) + (255,)
        # A continuous stroked outline avoids ImageDraw.line's quantized joints
        # when adjacent samples are closer than a raster pixel.
        drawing.polygon(outer_edge + inner_edge[::-1], fill=color)
        if arc["round_caps"]:
            for px, py in (points[0], points[-1]):
                drawing.ellipse((px - stroke / 2, py - stroke / 2,
                                 px + stroke / 2, py + stroke / 2), fill=color)

    # Extra supersampling substitutes for GDI+ SmoothingMode.AntiAlias.
    # The final reduction uses bicubic, as did the original C# source.
    intermediate = canvas.resize((render_size, render_size), Image.Resampling.BICUBIC)
    return intermediate.resize((output_size, output_size), Image.Resampling.BICUBIC)


def png_bytes(image):
    output = BytesIO()
    image.save(output, format="PNG", optimize=False, compress_level=9)
    return output.getvalue()


def make_ico(spec):
    frames = [png_bytes(render_icon(spec, size)) for size in spec["sizes"]]
    offset = 6 + len(frames) * 16
    directory = []
    for size, frame in zip(spec["sizes"], frames):
        directory.append(struct.pack("<BBBBHHII", size % 256, size % 256,
                                     0, 0, 1, 32, len(frame), offset))
        offset += len(frame)
    result = struct.pack("<HHH", 0, 1, len(frames))
    return result + b"".join(directory) + b"".join(frames), frames


def validate_png(payload, size):
    assert payload.startswith(PNG_SIGNATURE)
    cursor = 8
    chunks = []
    while cursor < len(payload):
        length = struct.unpack_from(">I", payload, cursor)[0]
        kind = payload[cursor + 4:cursor + 8]
        data = payload[cursor + 8:cursor + 8 + length]
        assert len(data) == length
        crc = struct.unpack_from(">I", payload, cursor + 8 + length)[0]
        assert zlib.crc32(kind + data) & 0xffffffff == crc
        chunks.append(kind.decode("ascii"))
        if kind == b"IHDR":
            assert struct.unpack(">IIBBBBB", data) == (size, size, 8, 6, 0, 0, 0)
        cursor += 12 + length
        if kind == b"IEND":
            assert length == 0
            break
    assert cursor == len(payload)
    assert chunks[0] == "IHDR" and chunks[-1] == "IEND" and "IDAT" in chunks
    with Image.open(BytesIO(payload)) as image:
        image.load()
        assert image.size == (size, size) and image.mode == "RGBA"
        alpha = image.getchannel("A")
        assert alpha.getextrema() == (0, 255)
        assert all(image.getpixel(point)[3] == 0 for point in
                   [(0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)])
        assert image.getpixel((size // 2, size // 2))[3] == 255
        assert any(0 < value < 255 for value in alpha.get_flattened_data())
    return chunks


def validate_ico(blob, sizes):
    assert struct.unpack_from("<HHH", blob) == (0, 1, len(sizes))
    next_offset = 6 + 16 * len(sizes)
    entries = []
    for index, size in enumerate(sizes):
        entry = struct.unpack_from("<BBBBHHII", blob, 6 + 16 * index)
        width, height, colors, reserved, planes, depth, length, offset = entry
        assert (width or 256, height or 256) == (size, size)
        assert (colors, reserved, planes, depth) == (0, 0, 1, 32)
        assert offset == next_offset and offset + length <= len(blob)
        payload = blob[offset:offset + length]
        chunks = validate_png(payload, size)
        entries.append({"size": size, "bit_depth": 32, "bytes": length,
                        "offset": offset, "alpha_extrema": [0, 255],
                        "transparent_corners": True, "png_chunks": chunks,
                        "sha256": sha256(payload).hexdigest()})
        next_offset += length
    assert next_offset == len(blob)
    with Image.open(BytesIO(blob)) as ico:
        assert ico.format == "ICO"
        assert ico.ico.sizes() == {(size, size) for size in sizes}
        for size in sizes:
            frame = ico.ico.getimage((size, size))
            assert frame.mode == "RGBA" and frame.size == (size, size)
    return entries


def main():
    spec = json.loads((ROOT / "icon-spec.json").read_text())
    ico, frames = make_ico(spec)
    validation = validate_ico(ico, spec["sizes"])
    (ROOT / "CodexQuotaTray.ico").write_bytes(ico)
    (ROOT / "CodexQuotaTray-preview.png").write_bytes(frames[-1])

    # Native-size light/dark preview confirms the transparent edges and the
    # per-size removal of the blue inner accent below 32 px.
    sheet = Image.new("RGB", (900, 410), (236, 239, 242))
    drawing = ImageDraw.Draw(sheet)
    drawing.rectangle((0, 205, 899, 409), fill=(32, 35, 39))
    for row, text_color in ((0, (40, 40, 40)), (205, (238, 238, 238))):
        x = 18
        for size, payload in zip(spec["sizes"], frames):
            with Image.open(BytesIO(payload)) as icon:
                if size == 256:
                    continue
                sheet.paste(icon, (x, row + 38 + (128 - size) // 2), icon)
            drawing.text((x, row + 178), str(size), fill=text_color)
            x += max(size, 40) + 18
    sheet.save(ROOT / "CodexQuotaTray-sizes.png")
    report = {
        "source": spec["source"], "pillow_version": pillow_version,
        "ico_sha256": sha256(ico).hexdigest(), "ico_bytes": len(ico),
        "preview_sha256": sha256(frames[-1]).hexdigest(),
        "checks": "All ICO directory entries, PNG chunk CRCs, RGBA headers, dimensions, alpha, transparent corners, and Pillow frame decoding passed.",
        "frames": validation,
    }
    (ROOT / "validation.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
