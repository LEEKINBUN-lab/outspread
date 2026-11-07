"""拍浮牌窗口：找到 Outspread.Board 进程里尺寸最小的可见窗（浮牌），PrintWindow 存 PNG。"""

import ctypes
import struct
import subprocess
import sys
import time
import zlib

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32


class RECT(ctypes.Structure):
    _fields_ = [
        ("l", ctypes.c_long),
        ("t", ctypes.c_long),
        ("r", ctypes.c_long),
        ("b", ctypes.c_long),
    ]


def board_pid():
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Outspread.Board.exe", "/FO", "CSV"],
        capture_output=True,
    ).stdout.decode("gbk", errors="replace")
    for line in out.splitlines()[1:]:
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1:
            return int(parts[1])
    return None


def main(out_path):
    pid = board_pid()
    print("pid", pid)
    hwnds = []
    WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def cb(h, _):
        p = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(h):
            rc = RECT()
            user32.GetWindowRect(h, ctypes.byref(rc))
            hwnds.append((h, rc.r - rc.l, rc.b - rc.t))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), None)
    print("windows:", hwnds)
    small = sorted([w for w in hwnds if w[1] < 800], key=lambda w: w[1])
    if not small:
        print("no floater-sized window")
        return 1
    hwnd, w, h = small[0]
    print("capturing", hwnd, w, h)
    time.sleep(0.5)

    hdc = user32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    ok = user32.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT
    print("PrintWindow ok:", ok)

    # BITMAPINFO 40 字节头 + 自底向上 BGRX
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

    def chunk(typ, data):
        return (
            struct.pack(">I", len(data))
            + typ
            + data
            + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF)
        )

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(b"".join(b"\x00" + r for r in rows)))
        + chunk(b"IEND", b"")
    )
    open(out_path, "wb").write(png)
    print("saved", out_path)
    return 0


sys.exit(main(sys.argv[1]))
