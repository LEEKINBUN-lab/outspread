"""STAY 模式下轮询抓浮牌：一出现（topmost 工具小窗）立刻 PrintWindow。"""

import ctypes
import struct
import subprocess
import sys
import time
import zlib

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)


class RECT(ctypes.Structure):
    _fields_ = [
        ("l", ctypes.c_long),
        ("t", ctypes.c_long),
        ("r", ctypes.c_long),
        ("b", ctypes.c_long),
    ]


def board_pids():
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Outspread.Board.exe", "/FO", "CSV"],
        capture_output=True,
    ).stdout.decode("gbk", errors="replace")
    pids = []
    for line in out.splitlines()[1:]:
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1:
            pids.append(int(parts[1]))
    return pids


def find_floater(pids):
    hits = []

    def cb(h, _):
        p = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value in pids and user32.IsWindowVisible(h):
            rc = RECT()
            user32.GetWindowRect(h, ctypes.byref(rc))
            w, hgt = rc.r - rc.l, rc.b - rc.t
            title = ctypes.create_unicode_buffer(120)
            user32.GetWindowTextW(h, title, 120)
            hits.append((h, w, hgt, title.value))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), None)
    cand = [w for w in hits if w[2] < 800 and not w[3]]
    return cand[0] if cand else None


def shoot(hwnd, w, h, path):
    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    user32.PrintWindow(hwnd, mem, 2)
    bmi = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, bmi, 0)
    rows = []
    for y in range(h):
        row = bytearray()
        base = (h - 1 - y) * w * 4
        for x in range(w):
            b, g, r, _ = buf[base + x * 4 : base + x * 4 + 4]
            row += bytes((r, g, b))
        rows.append(bytes(row))

    def chunk(t, d):
        return (
            struct.pack(">I", len(d))
            + t
            + d
            + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
        )

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(b"".join(b"\x00" + r for r in rows)))
        + chunk(b"IEND", b"")
    )
    open(path, "wb").write(png)


def main(path):
    deadline = time.time() + 120
    while time.time() < deadline:
        pids = board_pids()
        if pids:
            hit = find_floater(pids)
            if hit:
                hwnd, w, h, title = hit
                print("浮窗现形:", hwnd, w, h)
                time.sleep(0.6)
                shoot(hwnd, w, h, path)
                print("saved", path)
                return 0
        time.sleep(0.15)
    print("30 秒内没等到浮牌")
    return 1


sys.exit(main(sys.argv[1]))
