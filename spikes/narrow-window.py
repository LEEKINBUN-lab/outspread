"""把铺陈主窗挪窄（复现标签压钮）：按窗口标题找，不查进程。"""

import ctypes
import time

user32 = ctypes.windll.user32
WNDENUMPROC = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

found = []


def cb(h, _):
    if user32.IsWindowVisible(h):
        buf = ctypes.create_unicode_buffer(300)
        user32.GetWindowTextW(h, buf, 300)
        title = buf.value
        if "selftest" in title or "板名" in title:
            found.append((h, title))


user32.EnumWindows(WNDENUMPROC(cb), None)
print("windows:", found)
if found:
    user32.MoveWindow(found[0][0], 100, 100, 900, 620, True)
    time.sleep(1.5)
    buf = ctypes.create_unicode_buffer(300)
    user32.GetWindowTextW(found[0][0], buf, 300)
    print("narrowed:", buf.value)
else:
    print("no window matched")
