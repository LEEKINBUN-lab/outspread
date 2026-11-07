"""裁 PNG 一块区域并放大重采样，存出来给肉眼看。"""

import struct
import sys
import zlib


def decode(path):
    d = open(path, "rb").read()
    pos = 8
    w = h = bd = ct = None
    idat = b""
    while pos + 8 <= len(d):
        ln = struct.unpack(">I", d[pos : pos + 4])[0]
        typ = d[pos + 4 : pos + 8]
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", d[pos + 8 : pos + 18])
        elif typ == b"IDAT":
            idat += d[pos + 8 : pos + 8 + ln]
        elif typ == b"IEND":
            break
        pos += 12 + ln
    raw = zlib.decompress(idat)
    return w, h, bd, ct, raw


def rows_of(w, h, bd, ct, raw):
    ch = {2: 3, 6: 4}[ct]
    stride = w * ch
    prev = bytearray(stride)
    pos = 0
    rows = []
    for y in range(h):
        ft = raw[pos]
        pos += 1
        line = bytearray(raw[pos : pos + stride])
        pos += stride
        if ft == 1:
            for i in range(ch, stride):
                line[i] = (line[i] + line[i - ch]) & 0xFF
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:
            for i in range(stride):
                left = line[i - ch] if i >= ch else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif ft == 4:
            for i in range(stride):
                a = line[i - ch] if i >= ch else 0
                b = prev[i]
                c = prev[i - ch] if i >= ch else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        rows.append(bytes(line))
        prev = line
    return ch, rows


def encode(path, w, h, ch, rows):
    def chunk(typ, data):
        c = struct.pack(">I", len(data)) + typ + data
        return c + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    raw = b"".join(b"\x00" + r[: w * 3] for r in rows)
    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(raw))
        + chunk(b"IEND", b"")
    )
    open(path, "wb").write(png)


def main(src, dst, x0, y0, x1, y1, scale):
    w, h, bd, ct, raw = decode(src)
    ch, rows = rows_of(w, h, bd, ct, raw)
    x0, y0, x1, y1 = max(0, x0), max(0, y0), min(w, x1), min(h, y1)
    cw, chh = x1 - x0, y1 - y0
    ow, oh = cw * scale, chh * scale
    out = []
    for oy in range(oh):
        sy = y0 + oy // scale
        line = bytearray()
        srow = rows[sy]
        for ox in range(ow):
            sx = x0 + ox // scale
            px = srow[sx * ch : sx * ch + 3]
            line += px
        out.append(bytes(line))
    encode(dst, ow, oh, 3, out)
    print(f"crop {cw}x{chh} -> {ow}x{oh} at ({x0},{y0})")


main(sys.argv[1], sys.argv[2], *(int(v) for v in sys.argv[3:8]))
