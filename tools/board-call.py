#!/usr/bin/env python3
"""走 stdio 调一次 outspread-mcp 的工具，把结果原样打到 stdout。

用法：
    python tools/board-call.py tools/list
    python tools/board-call.py board_inbox "{}"
    python tools/board-call.py board_post '{"title":"…","cards":[…]}'

为什么存在：MCP 连接器是长驻旧进程，行为可能落后于仓里的代码；每轮出口流程
（.qoder/skills/board-outlet/SKILL.md）都指明用**新鲜构建**的 exe 走 stdio。
这个脚本就是那条路的最后一厘米：起新进程、握手、调一发、收场，不留常驻活。

协议：MCP stdio 是**按行分隔**的 JSON-RPC（不是 LSP 那种 Content-Length 头）。
先 initialize、再发 initialized 通知，然后才是 tools/call。
"""

import json
import subprocess
import sys
from pathlib import Path

EXE = Path(__file__).resolve().parents[1] / "src/Outspread.Mcp/bin/Debug/net10.0/outspread-mcp.exe"


def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2

    tool = sys.argv[1]
    arguments = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}

    proc = subprocess.Popen(
        [str(EXE)],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        text=True,
        encoding="utf-8",
    )

    next_id = 0

    def request(method, params=None):
        nonlocal next_id

        payload = {"jsonrpc": "2.0", "id": next_id, "method": method}

        if params is not None:
            payload["params"] = params

        proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        proc.stdin.flush()
        next_id += 1

        while True:
            line = proc.stdout.readline()

            if not line:
                raise RuntimeError(f"{method}: 进程收场了，没等到回应")

            reply = json.loads(line)

            if reply.get("id") == next_id - 1:
                return reply

    request("initialize", {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "board-call", "version": "1.0"},
    })
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
    proc.stdin.flush()

    if tool == "tools/list":
        reply = request("tools/list")
    else:
        reply = request("tools/call", {"name": tool, "arguments": arguments})

    proc.stdin.close()
    proc.wait(timeout=10)

    result = reply.get("result", reply)

    print(json.dumps(result, ensure_ascii=False, indent=1))

    return 0 if "error" not in reply else 1


if __name__ == "__main__":
    sys.exit(main())
