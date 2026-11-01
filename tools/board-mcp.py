#!/usr/bin/env python3
"""outspread-board 的用户级连接器入口（挂在 ~/.zcode/cli/config.json → mcp.servers）。

为什么经这一手而不直指 exe：exe 缺了或比源码旧时先 build——不这样，10-30 夜
「旧 exe 静默丢署名」那一幕会在每个新会话重演。build 失败（比如另一个连接器
正锁着 bin）就退回跑现有 exe：宁可旧一点也不把连接器弄死。

stdout 一概不碰：它是 MCP 的 JSON-RPC 通道，build 的输出全部改道 stderr。
"""

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "Outspread.Mcp" / "Outspread.Mcp.csproj"
EXE = ROOT / "src" / "Outspread.Mcp" / "bin" / "Debug" / "net10.0" / "outspread-mcp.exe"


def sources_newer_than_exe() -> bool:
    if not EXE.exists():
        return True

    exe_time = EXE.stat().st_mtime

    for folder in ("Outspread.Mcp", "Outspread.Core"):
        for pattern in ("*.cs", "*.csproj"):
            for source in (ROOT / "src" / folder).rglob(pattern):
                if source.stat().st_mtime > exe_time:
                    return True

    return False


def main() -> int:
    if sources_newer_than_exe():
        build = subprocess.run(
            ["dotnet", "build", str(PROJECT), "-c", "Debug"],
            stdout=sys.stderr,
            stderr=sys.stderr,
        )

        if build.returncode != 0 and not EXE.exists():
            print("outspread-board: build 失败且没有现成 exe，连接器起不来", file=sys.stderr)
            return 1

    return subprocess.call([str(EXE)])


if __name__ == "__main__":
    sys.exit(main())
