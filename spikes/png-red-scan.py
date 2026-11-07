"""扫 PNG 里的红色像素（垃圾桶那抹 D32F2F）：量它画在哪、画了几个。"""

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


def pixels(w, h, bd, ct, raw):
    """只处理 ct=2(RGB)/6(RGBA)、bd=8 的常见档；返回 [(x,y,r,g,b),...]（过滤全透明）。"""
    ch = {2: 3, 6: 4}[ct]
    stride = w * ch
    out = []
    prev = bytearray(stride)
    pos = 0
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
        out.append(bytes(line))
        prev = line
    return ch, out


def main(path):
    w, h, bd, ct, raw = decode(path)
    ch, rows = pixels(w, h, bd, ct, raw)
    reds = []
    for y, line in enumerate(rows):
        for x in range(w):
            r, g, b = line[x * ch], line[x * ch + 1], line[x * ch + 2]
            alpha = line[x * ch + 3] if ch == 4 else 255
            if alpha > 100 and r > 150 and g < 120 and b < 120:
                reds.append((x, y))
    print(f"{path}: {w}x{h} ct={ct} 红像素 {len(reds)}")
    if reds:
        xs = [p[0] for p in reds]
        ys = [p[1] for p in reds]
        print(f"  红的范围 x[{min(xs)}..{max(xs)}] y[{min(ys)}..{max(ys)}]，样本 {reds[:8]}")


for p in sys.argv[1:]:
    main(p)
