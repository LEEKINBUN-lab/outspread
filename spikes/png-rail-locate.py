"""在 shot PNG 里找彩虹色轮的像素簇，定位操作条，再推第 4 枚（删）的槽位坐标。"""

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
    return w, h, bd, ct, zlib.decompress(idat)


def rows_of(w, h, bd, ct, raw):
    ch = {2: 3, 6: 4}[ct]
    stride = w * ch
    prev = bytearray(stride)
    pos = 0
    rows = []
    for _ in range(h):
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


def main(path):
    w, h, bd, ct, raw = decode(path)
    ch, rows = rows_of(w, h, bd, ct, raw)
    sat = []
    for y, line in enumerate(rows):
        for x in range(w):
            r, g, b = line[x * ch], line[x * ch + 1], line[x * ch + 2]
            if max(r, g, b) - min(r, g, b) > 70:
                sat.append((x, y))
    if not sat:
        print("没找到高饱和簇")
        return
    xs = [p[0] for p in sat]
    ys = [p[1] for p in sat]
    cx, cy = sum(xs) / len(xs), sum(ys) / len(ys)
    diameter = max(max(xs) - min(xs), max(ys) - min(ys))
    scale = diameter / 30.0  # 色轮彩色区约 30 DIP
    pitch = round(scale * 48)
    print(f"色轮簇中心 ({cx:0.0f},{cy:0.0f}) 直径 {diameter}px → DPI scale ≈ {scale:0.2f}，槽距 {pitch}px")
    print(f"删除槽位中心估算 ≈ ({cx:0.0f}, {cy + 2 * pitch:0.0f})")


main(sys.argv[1])
