"""生成应用图标。纯标准库：手写 PNG 编码 + ICO 容器，不依赖 PIL。

图形：深蓝圆角方块 + 白色播放三角 + 三条渐短的白色列表线。
"""

import math
import os
import struct
import zlib

OUT_DIR = r"C:\Users\Administrator\NPlayerServer\icon"
ICO_PATH = r"C:\Users\Administrator\NPlayerServer\app.ico"

BG = (24, 95, 165)
WHITE = (255, 255, 255)
DIM_ALPHA = 150 / 255.0

TRI = ((0.26, 0.31), (0.26, 0.69), (0.51, 0.50))
LINES = (
    (0.59, 0.375, 0.81, 0.375),
    (0.59, 0.500, 0.73, 0.500),
    (0.59, 0.625, 0.67, 0.625),
)
LINE_W = 0.032
CORNER = 0.23


def in_round_rect(u, v, r):
    cx = min(max(u, r), 1.0 - r)
    cy = min(max(v, r), 1.0 - r)
    dx = u - cx
    dy = v - cy
    return dx * dx + dy * dy <= r * r


def _cross(ax, ay, bx, by, cx, cy):
    return (ax - cx) * (by - cy) - (bx - cx) * (ay - cy)


def in_triangle(px, py):
    (ax, ay), (bx, by), (cx, cy) = TRI
    d1 = _cross(px, py, ax, ay, bx, by)
    d2 = _cross(px, py, bx, by, cx, cy)
    d3 = _cross(px, py, cx, cy, ax, ay)
    neg = d1 < 0 or d2 < 0 or d3 < 0
    pos = d1 > 0 or d2 > 0 or d3 > 0
    return not (neg and pos)


def dist_to_segment(px, py, x1, y1, x2, y2):
    dx = x2 - x1
    dy = y2 - y1
    if dx == 0 and dy == 0:
        return math.hypot(px - x1, py - y1)
    t = ((px - x1) * dx + (py - y1) * dy) / (dx * dx + dy * dy)
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (x1 + t * dx), py - (y1 + t * dy))


def line_hit(u, v, with_lines):
    if not with_lines:
        return False
    half = LINE_W / 2
    for x1, y1, x2, y2 in LINES:
        if dist_to_segment(u, v, x1, y1, x2, y2) <= half:
            return True
    return False


def render(size, with_lines=True, ss=4):
    """渲染 RGBA 图，ss x ss 超采样做抗锯齿。"""
    total = size * ss
    acc = [[[0, 0, 0, 0] for _ in range(size)] for _ in range(size)]

    for j in range(total):
        v = (j + 0.5) / total
        row = j // ss
        for i in range(total):
            if not in_round_rect((i + 0.5) / total, v, CORNER):
                continue
            u = (i + 0.5) / total
            col = i // ss
            if in_triangle(u, v):
                r, g, b, a = 255, 255, 255, 255
            elif line_hit(u, v, with_lines):
                r = int(255 * DIM_ALPHA + BG[0] * (1 - DIM_ALPHA))
                g = int(255 * DIM_ALPHA + BG[1] * (1 - DIM_ALPHA))
                b = int(255 * DIM_ALPHA + BG[2] * (1 - DIM_ALPHA))
                a = 255
            else:
                r, g, b, a = BG[0], BG[1], BG[2], 255

            cell = acc[row][col]
            cell[0] += r
            cell[1] += g
            cell[2] += b
            cell[3] += a

    n = ss * ss
    out = []
    for row in acc:
        line = []
        for cell in row:
            line.append((cell[0] // n, cell[1] // n, cell[2] // n, cell[3] // n))
        out.append(line)
    return out


def resample(src, dst_size):
    """盒式滤波缩放。"""
    src_size = len(src)
    if src_size == dst_size:
        return src

    out = []
    ratio = src_size / dst_size
    for y in range(dst_size):
        y0 = int(y * ratio)
        y1 = max(y0 + 1, int((y + 1) * ratio))
        line = []
        for x in range(dst_size):
            x0 = int(x * ratio)
            x1 = max(x0 + 1, int((x + 1) * ratio))
            sr = sg = sb = sa = 0
            count = 0
            for yy in range(y0, min(y1, src_size)):
                row = src[yy]
                for xx in range(x0, min(x1, src_size)):
                    px = row[xx]
                    sr += px[0]
                    sg += px[1]
                    sb += px[2]
                    sa += px[3]
                    count += 1
            count = count or 1
            line.append((sr // count, sg // count, sb // count, sa // count))
        out.append(line)
    return out


def png_bytes(pixels):
    size = len(pixels)
    raw = bytearray()
    for row in pixels:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    compressed = zlib.compress(bytes(raw), 9)

    def chunk(kind, data):
        return (struct.pack(">I", len(data)) + kind + data +
                struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", ihdr) +
            chunk(b"IDAT", compressed) +
            chunk(b"IEND", b""))


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    base = render(256, with_lines=True, ss=4)
    small = render(16, with_lines=False, ss=8)

    sizes = [256, 128, 64, 48, 32, 24, 16]
    images = []
    for size in sizes:
        if size == 16:
            pixels = small
        elif size == 256:
            pixels = base
        else:
            pixels = resample(base, size)

        data = png_bytes(pixels)
        images.append((size, data))
        with open(os.path.join(OUT_DIR, f"icon_{size}.png"), "wb") as f:
            f.write(data)
        print(f"  {size}x{size}  {len(data)} bytes")

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    blob = b""
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blob += data

    with open(ICO_PATH, "wb") as f:
        f.write(header + entries + blob)

    print(f"\nICO: {ICO_PATH}  {os.path.getsize(ICO_PATH)} bytes")


if __name__ == "__main__":
    main()
