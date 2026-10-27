#!/usr/bin/env python3
"""滚动那几秒到底谁在吃 GPU：把界面按几种相机各跑一发有界的平移探针，同时采 GPU Engine 利用率。

用法：
    python tools/gpu-probe.py [--exe <Outspread.Board.exe>] [--board <临时副本.canvas>] [--seconds 6]

它只碰 --board 指的那块**副本**（默认 %TEMP%\\outspread-gpu\\probe.canvas，每次重新拷），
绝不动 boards\\ 里他真在用的那几块。探针自己有上限（--seconds，最长 30 秒），跑完关窗。

为什么需要它：界面里那一圈秒表只能量"我们这一发花了多少毫秒"（平移每帧 0.05ms），
量不到"合成器为了把这一帧画出来花了多少 GPU"。那正是他报的"滚动时还占 10%"。
GPU Engine 的按进程计数器是唯一能在这台机器上（无管理员）拿到那一半的通道。
"""

import argparse
import csv
import io
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def run(cmd, timeout=120):
    return subprocess.run(
        cmd, capture_output=True, text=True, encoding="mbcs", errors="replace", timeout=timeout
    )


def gpu_instances(pid):
    """这台机器上 GPU Engine 的实例名里带 pid，先按 pid 捞出 3D 那两档。"""
    out = run(["typeperf", "-qx"]).stdout
    want = []

    for line in out.splitlines():
        line = line.strip()

        if not line.startswith("\\GPU Engine(") or not line.endswith("\\Utilization Percentage"):
            continue

        name = line[len("\\GPU Engine("):-len("\\Utilization Percentage")]

        if name.startswith(f"pid_{pid}_") and ("engtype_3D" in name or "engtype_High Priority 3D" in name):
            want.append(line)

    return want


def sample(paths, seconds, interval=1):
    """采 --seconds 秒，返回该进程 3D 引擎通道的均值/峰值（%）。

    `-si` 只吃整数秒（给 0.5 会报"无效语法"，然后整发采不到东西），所以窗口要长一点才有几个点。
    """
    if not paths:
        return None

    count = max(2, int(seconds / interval))
    proc = run(["typeperf", *paths, "-si", str(interval), "-sc", str(count)], timeout=seconds + 40)
    rows = list(csv.reader(io.StringIO(proc.stdout)))

    if len(rows) < 3:
        return None

    per = [[] for _ in paths]

    for row in rows[2:]:
        if len(row) < 1 + len(paths):
            continue

        for index in range(len(paths)):
            try:
                per[index].append(float(row[1 + index]))
            except ValueError:
                pass

    # 每通道丢掉第一个点：那一秒里还含着首帧布局与首次点阵重画，不是平移的价钱。
    per = [vals[1:] if len(vals) > 3 else vals for vals in per]

    lanes = [
        {
            "name": path[len("\\GPU Engine("):-len("\\Utilization Percentage")],
            "avg": statistics.fmean(vals),
            "max": max(vals),
            "samples": len(vals),
        }
        for path, vals in zip(paths, per)
        if vals and (statistics.fmean(vals) > 0 or max(vals) > 0)
    ]

    if not lanes:
        return None

    # 这台机器可能有两块适配器（实例名里的 luid 不同）。跨适配器把利用率相加是假数，
    # 所以按 luid 分组，报最忙的那一组，并把组名留在读数旁边。
    groups = {}

    for lane in lanes:
        groups.setdefault(luid_of(lane["name"]), []).append(lane)

    winner = max(groups.values(), key=lambda group: sum(lane["avg"] for lane in group))

    return {
        "lanes": winner,
        "luid": luid_of(winner[0]["name"]),
        "avg": sum(lane["avg"] for lane in winner),
        "max": sum(lane["max"] for lane in winner),
        "samples": min(lane["samples"] for lane in winner),
    }


def luid_of(instance_name):
    return next((part for part in instance_name.split("_") if part.startswith("0x")), "?")


def load_board(src):
    doc = json.loads(Path(src).read_text(encoding="utf-8"))
    cards = [n for n in doc.get("nodes", []) if n.get("type") == "text"]
    xs = [c.get("x", 0) for c in cards]
    ys = [c.get("y", 0) for c in cards]

    return {
        "cards": len(cards),
        "dense": (statistics.fmean(xs), statistics.fmean(ys)),
        "empty": (max(xs) + 3000, min(ys) if ys else 0),
        "span": (min(xs), min(ys), max(xs), max(ys)),
    }


def write_sidecar(board, zoom, world):
    """把相机摆到 (world) 居中、缩放 zoom ——探针跑的就是这一帧的相机。"""
    width, height = 1320, 802  # 窗口板面的布局尺寸（现读自 atmosphere 那一行）
    sidecar = Path(str(board) + ".view.json")
    existing = {}

    if sidecar.exists():
        existing = json.loads(sidecar.read_text(encoding="utf-8"))

    existing["zoom"] = zoom
    existing["panX"] = (width / 2) - (world[0] * zoom)
    existing["panY"] = (height / 2) - (world[1] * zoom)
    sidecar.write_text(json.dumps(existing, ensure_ascii=False), encoding="utf-8")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=str(REPO / "src/Outspread.Board/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Outspread.Board.exe"))
    ap.add_argument("--src", default=str(REPO / "boards/Agent 出口.canvas"))
    ap.add_argument("--seconds", type=float, default=10.0)
    args = ap.parse_args()

    root = Path(os.environ.get("TEMP", "/tmp")) / "outspread-gpu"
    root.mkdir(parents=True, exist_ok=True)
    board = root / "probe.canvas"

    shutil.copyfile(args.src, board)
    shutil.copyfile(str(args.src) + ".view.json", str(board) + ".view.json")

    geo = load_board(board)
    log = Path(os.environ.get("LOCALAPPDATA", "")) / "Outspread/board.log"

    configs = [
        ("密区 100%", 1.0, geo["dense"]),
        ("空区 100%", 1.0, geo["empty"]),
        ("密区 40%", 0.4, geo["dense"]),
        ("密区 250%", 2.5, geo["dense"]),
    ]

    print(f"板：{board}（{geo['cards']} 格，副本；boards\\ 里那块没动）")
    print(f"{'相机':<12}{'帧数':>6}{'每帧ms':>9}{'GPU均值%':>11}{'GPU峰值%':>11}{'元素':>7}")

    for label, zoom, world in configs:
        write_sidecar(board, zoom, world)

        before = log.stat().st_size if log.exists() else 0

        proc = subprocess.Popen([args.exe, "--board", str(board), "--pan-probe", str(int(args.seconds * 1000))])
        time.sleep(2.2)  # 等它把窗口与板子立起来，探针那几秒才开始跑

        read = sample(gpu_instances(proc.pid), args.seconds)
        proc.wait(timeout=args.seconds + 90)

        with log.open("r", encoding="utf-8", errors="replace") as handle:
            handle.seek(before)
            tail = handle.read()

        line = next((l for l in tail.splitlines() if " 帧 · 每帧均" in l), "")
        frames = re.search(r"pan \d+ms: (\d+) 帧", line)
        per_frame = re.search(r"每帧均 ([0-9.]+)ms", line)
        elements = re.search(r"板面元素 (\d+) 个", line)

        print(
            f"{label:<12}{(frames.group(1) if frames else '?'):>6}"
            f"{(per_frame.group(1) if per_frame else '?'):>9}"
            f"{(read['avg'] if read else float('nan')):>11.2f}"
            f"{(read['max'] if read else float('nan')):>11.2f}"
            f"{(elements.group(1) if elements else '?'):>7}"
        )

    print(
        "\n说明：GPU 那两列是该进程在同一块适配器（同 luid）上所有 3D 引擎通道的合计，"
        "每通道丢掉第一个采样点（那一点还含着首帧布局与首次点阵重画）。"
        "空区那一发是**对照**：同样的板底、点阵、暗角，0 格便签——两行之差就是便签自己那一层的价钱。"
    )


if __name__ == "__main__":
    sys.exit(main() or 0)
