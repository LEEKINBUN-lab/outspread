#!/usr/bin/env python3
"""悬案探针：两枚同步令牌的 blob 编码对比（cmdkey 写 UTF-16，adopt 复制写 UTF-8）。"""
import ctypes
from ctypes import wintypes

advapi = ctypes.windll.advapi32


class CRED(ctypes.Structure):
    _fields_ = [
        ("Flags", wintypes.DWORD), ("Type", wintypes.DWORD),
        ("TargetName", wintypes.LPWSTR), ("Comment", wintypes.LPWSTR),
        ("LastWritten", wintypes.FILETIME), ("CredentialBlobSize", wintypes.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)), ("Persist", wintypes.DWORD),
        ("AttributeCount", wintypes.DWORD), ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wintypes.LPWSTR), ("UserName", wintypes.LPWSTR),
    ]


def probe(target):
    cred = ctypes.POINTER(CRED)()
    if not advapi.CredReadW(target, 1, 0, ctypes.byref(cred)):
        return f"{target}: 读不到（Win32 err {ctypes.GetLastError()}）"
    c = cred.contents
    blob = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize)
    utf8 = blob.decode("utf-8", "replace")
    utf16 = None
    if len(blob) >= 4 and len(blob) % 2 == 0:
        try:
            utf16 = blob.decode("utf-16-le")
        except Exception as ex:
            utf16 = f"解码异常 {ex}"
    looks16 = len(blob) >= 4 and len(blob) % 2 == 0
    return (f"{target}: {len(blob)} 字节 | 前12字节 {blob[:12].hex()} | "
            f"utf8前10={utf8[:10]!r} | looksUtf16={looks16} | "
            f"utf16前10={(utf16[:10] + chr(8230)) if isinstance(utf16, str) else '（未尝试）'} | "
            f"Persist={c.Persist} | nul={blob.count(0)}")


print(probe("outspread.sync.github"))
print(probe("tankan.sync.github"))
