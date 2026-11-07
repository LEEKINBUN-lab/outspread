"""沙箱管道自连探针：确认这个 Bash 沙箱里能不能 CreateFile 打开命名管道。"""

import ctypes
import os

k32 = ctypes.windll.kernel32
PIPE_ACCESS_DUPLEX = 3
PIPE_TYPE_BYTE = 0
PIPE_WAIT = 0
GENERIC_READ_WRITE = 0xC0000000
OPEN_EXISTING = 3

NAME = r"\\.\pipe\outspread-sandbox-probe-1"

server = k32.CreateNamedPipeW(
    NAME, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, None
)
print("server handle ok:", server not in (-1, 0xFFFFFFFFFFFFFFFF))
print("in listdir:", "outspread-sandbox-probe-1" in os.listdir("//./pipe/"))

client = k32.CreateFileW(
    NAME, GENERIC_READ_WRITE, 3, None, OPEN_EXISTING, 0, None
)
ok = client not in (-1, 0xFFFFFFFFFFFFFFFF)
print("client open:", ok, "gle:", k32.GetLastError())
if ok:
    k32.CloseHandle(client)
k32.CloseHandle(server)
