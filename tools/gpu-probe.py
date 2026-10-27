#!/usr/bin/env python3
"""滚动/缩放/拖一格那几秒到底谁在吃 GPU 与 CPU：按几种手势各跑几发**有界**探针，同时采两本账。

用法：
    python tools/gpu-probe.py [--seconds 10] [--repeats 3] [--src <副本源>]

它只碰 %TEMP%\\outspread-gpu\\probe.canvas（每次从 --src 重拷一份），绝不动 boards\\ 里他真在用的那几块。
探针自己有上限（界面侧 Clamp 到 30 秒），跑完自己结束进程。

为什么要两本账：界面里那一圈秒表只能量"我们这一发花了多少毫秒"（平移每帧 0.13ms），
量不到"合成器为了画出这一帧花了多少 GPU"，也量不到"整个进程（含合成线程、GC）占了多少 CPU"。
那两样正是他报的"10%"。这台机器没有管理员权限，所以走的是：
  · GPU —— PDH 的 \\GPU Engine(pid_..._engtype_3D)\\ 按进程实例（typeperf 读，免管理员）
  · CPU —— GetProcessTimes 的用户＋内核时间增量（同样免管理员）
"""

import argparse
import csv
import ctypes
import ctypes.wintypes
import io
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import threading
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def run(cmd, timeout=120):
    return subprocess.run(
        cmd, capture_output=True, text=True, encoding="mbcs", errors="replace", timeout=timeout
    )


def gpu_instances(pid):
    """GPU Engine 的实例名里带 pid，先按 pid 捞出 3D 那两档。"""
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


def luid_of(instance_name):
    return next((part for part in instance_name.split("_") if part.startswith("0x")), "?")


def sample(paths, seconds, interval=1):
    """采 --seconds 秒，返回该进程 3D 引擎通道在同一块适配器上的合计均值/峰值（%）。

    四个坑都是这台机器上真撞过的：
      · `-si` 只吃整数秒（给 0.5 会报"无效语法"，然后整发采不到东西）；
      · 每通道第一个采样点还含着首帧布局与首次点阵重画，不是这一档手势的价钱；
      · 实例中途消失时 PDH 会吐**负数**——它曾把一行的均值拉成 -0.48%；
      · 两块适配器（luid 不同）的利用率相加是假数，所以分组后只报最忙那一组。
    """
    if not paths:
        return None

    count = max(2, int(seconds / interval))
    proc = run(["typeperf", *paths, "-si", str(interval), "-sc", str(count)], timeout=seconds + 60)
    rows = list(csv.reader(io.StringIO(proc.stdout)))

    if len(rows) < 3:
        return None

    per = [[] for _ in paths]

    for row in rows[2:]:
        if len(row) < 1 + len(paths):
            continue

        for index in range(len(paths)):
            try:
                value = float(row[1 + index])
            except ValueError:
                continue

            if value >= 0:
                per[index].append(value)

    # 每通道丢掉第一个点（见上）。
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


def cpu_fraction(pid, seconds):
    """进程那本 CPU 时间账：(用户＋内核) 增量 ÷ 墙钟，报"占满一核＝100%"那一档。

    口径写死在这里，是为了能和任务管理器那一列对得上（再除以逻辑核数就是它）。
    必须与 GPU 并发采：先采完 GPU 再量 CPU，量到的是一个已经退出的进程——整列 0.00 就是这么来的。
    """
    kernel32 = ctypes.windll.kernel32

    # 不设签名，64 位上返回的 HANDLE 会被截成 32 位：OpenProcess 看着成功、
    # GetProcessTimes 其实全失败，量出来是一列 0.00（第一发就是这么红的）。
    kernel32.OpenProcess.restype = ctypes.wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [
        ctypes.wintypes.DWORD,
        ctypes.wintypes.BOOL,
        ctypes.wintypes.DWORD,
    ]
    kernel32.GetProcessTimes.restype = ctypes.wintypes.BOOL

    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION

    if not handle:
        return float("nan")

    def snapshot():
        creation = ctypes.c_ulonglong()
        exit_ = ctypes.c_ulonglong()
        kernel = ctypes.c_ulonglong()
        user = ctypes.c_ulonglong()

        if not kernel32.GetProcessTimes(
                handle,
                ctypes.byref(creation), ctypes.byref(exit_),
                ctypes.byref(kernel), ctypes.byref(user)):
            return None

        return kernel.value + user.value

    first = snapshot()
    time.sleep(seconds)
    second = snapshot()
    kernel32.CloseHandle(handle)

    if first is None or second is None:
        return float("nan")

    return ((second - first) / 1e7) / seconds * 100  # FILETIME 的 tick 是 100ns


def load_board(src):
    doc = json.loads(Path(src).read_text(encoding="utf-8"))
    cards = [n for n in doc.get("nodes", []) if n.get("type") == "text"]
    xs = [c.get("x", 0) for c in cards]
    ys = [c.get("y", 0) for c in cards]

    return {
        "cards": len(cards),
        "dense": (statistics.fmean(xs), statistics.fmean(ys)),
        "empty": (max(xs) + 3000, min(ys) if ys else 0),
    }


def write_sidecar(board, zoom, world):
    """把相机摆到 (world) 居中、缩放 zoom ——探针跑的就是这一帧的相机。"""
    width, height = 1320, 802  # 板面的布局尺寸（现读自日志 atmosphere 那一行）
    sidecar = Path(str(board) + ".view.json")
    existing = {}

    if sidecar.exists():
        existing = json.loads(sidecar.read_text(encoding="utf-8"))

    existing["zoom"] = zoom
    existing["panX"] = (width / 2) - (world[0] * zoom)
    existing["panY"] = (height / 2) - (world[1] * zoom)
    sidecar.write_text(json.dumps(existing, ensure_ascii=False), encoding="utf-8")


def one_run(exe, board, log, zoom, world, gesture, seconds, flags=(), settle=2.2):
    """跑一档：摆好相机 → 起界面 → 并发采 GPU 与进程 CPU → 读回探针那一行。"""
    write_sidecar(board, zoom, world)

    before = log.stat().st_size if log.exists() else 0

    # 探针跑 seconds+4：采样窗（含起手的等待）要整个落在它还活着的时候里。
    extra = [*flags] if gesture == "idle" else [*flags, f"--{gesture}-probe", str(int((seconds + 4) * 1000))]

    # 静置那一档要等得更久：起来头几秒里还有自愈（往稳定目录拷 42MB）、快捷方式对齐、
    # JIT 与首帧重建。拿 2.2 秒就去采，量到的是"开机"不是"静置"——21% 就是这么冒出来的。
    proc = subprocess.Popen([exe, "--board", str(board), *extra])
    time.sleep(14.0 if gesture == "idle" else settle)

    cpu = {}

    def meter_cpu():
        cpu["value"] = cpu_fraction(proc.pid, seconds)

    meter = threading.Thread(target=meter_cpu)
    meter.start()

    read = sample(gpu_instances(proc.pid), seconds)
    meter.join()

    if gesture == "idle":
        proc.terminate()

    proc.wait(timeout=seconds + 90)

    with log.open("r", encoding="utf-8", errors="replace") as handle:
        handle.seek(before)
        tail = handle.read()

    line = next((row for row in tail.splitlines() if " 帧 · 每帧均" in row), "")
    control = next((row for row in tail.splitlines() if "drag 正对照" in row), "")
    frames = re.search(r"(?:pan|zoom|drag) \d+ms: (\d+) 帧", line)
    per_frame = re.search(r"每帧均 ([0-9.]+)ms", line)
    visible = re.search(r"可见 (\d+) 枚", line)
    elements = re.search(r"板面元素 (\d+) 个", line)

    # 拖一格那一档的自检：格子到底被拖走了几像素。没动就等于"这一发什么都没做"，
    # 那种读数会低得好看（0.056ms/帧），不写进表里就会被我当成结论读。
    moved = re.findall(r"从 \((-?[\d.]+)，(-?[\d.]+)\) 走到了 \((-?[\d.]+)，(-?[\d.]+)\)", control)
    drag_px = abs(float(moved[0][2]) - float(moved[0][0])) + abs(float(moved[0][3]) - float(moved[0][1])) if moved else 0.0

    return {
        "frames": int(frames.group(1)) if frames else 0,
        "per_frame": float(per_frame.group(1)) if per_frame else None,
        "visible": int(visible.group(1)) if visible else 0,
        "elements": int(elements.group(1)) if elements else 0,
        "drag_px": drag_px,
        "gpu": read["avg"] if read else None,
        "cpu": cpu.get("value", float("nan")),
    }


def median(values, fallback=float("nan")):
    filled = [value for value in values if value is not None and value == value]

    return statistics.median(filled) if filled else fallback


def make_board(path, near, far=0, cell=(360, 88), gap=24, columns=3):
    """合成一块板：`near` 格铺在密区，`far` 格甩到 60000 之外（同尺寸同内容，只是看不见）。

    这是为了把"便签一多"拆成两个轴：板上**总共**多少格（遍历/对账那条路），
    和视野里**压着**多少格（每帧参与合成的那批）。只报"实格 73"分不开这两件事。
    """
    nodes = []

    for index in range(near + far):
        x = (index % columns) * (cell[0] + gap)
        y = (index // columns) * (cell[1] + gap)

        if index >= near:
            x += 60000

        nodes.append({
            "id": f"s{index:012d}",
            "type": "text",
            "x": x,
            "y": y,
            "width": cell[0],
            "height": cell[1],
            "text": f"### 格子 {index}\n\n@t形状\n2026-10-27 21:00",
        })

    Path(path).write_text(json.dumps({"nodes": nodes, "edges": []}, ensure_ascii=False), encoding="utf-8")

    rows = -(-near // columns)

    return ((columns - 1) * (cell[0] + gap) / 2, (rows - 1) * (cell[1] + gap) / 2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument(
        "--exe",
        default=str(REPO / "src/Outspread.Board/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Outspread.Board.exe"))
    ap.add_argument("--src", default=str(REPO / "boards/Agent 出口.canvas"))
    ap.add_argument("--seconds", type=float, default=10.0)
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--shapes", action="store_true",
                    help="跑「格数」矩阵：把「便签一多」拆成 板上总量 / 视野里可见 两个轴，不跑默认那六档")
    args = ap.parse_args()

    root = Path(os.environ.get("TEMP", "/tmp")) / "outspread-gpu"
    root.mkdir(parents=True, exist_ok=True)
    log = Path(os.environ.get("LOCALAPPDATA", "")) / "Outspread/board.log"

    if args.shapes:
        shapes = [
            ("60 总·拖一格", 60, 0, 1.0, "drag"),
            ("60 总·平移", 60, 0, 1.0, "pan"),
            ("600 总·拖一格", 60, 540, 1.0, "drag"),
            ("600 总·平移", 60, 540, 1.0, "pan"),
            ("600 满视野·拖一格", 600, 0, 0.25, "drag"),
            ("600 满视野·平移", 600, 0, 0.25, "pan"),
        ]

        print(f"形状矩阵（每档 {args.repeats} 发取中位）：把「便签一多」拆成 板上总量 / 视野里可见 两个轴")
        print(f"{'档':<18}{'帧数':>7}{'每帧ms':>9}{'GPU中位%':>10}{'CPU占一核%':>11}{'可见':>6}{'拖距px':>9}{'元素':>8}  GPU 各发")

        for label, near, far, zoom, gesture in shapes:
            board = root / f"shape-{near}-{far}.canvas"

            runs = [
                # 每一发都重新生成板子：拖完那一格会被存进盘，下一发就从"已经被拖走的位置"接着拖
                # ——600 格那一档量出 0.088ms/帧（比 60 格还快十倍）就是这么来的。
                one_run(args.exe, board, log, zoom,
                        make_board(board, near, far), gesture, args.seconds, settle=8.0)
                for _ in range(args.repeats)
            ]
            gpus = sorted(run["gpu"] for run in runs if run["gpu"] is not None)

            print(
                f"{label:<18}{median(run['frames'] for run in runs):>7.0f}"
                f"{median(run['per_frame'] for run in runs):>9}"
                f"{median(gpus):>10.2f}"
                f"{median(run['cpu'] for run in runs):>11.2f}"
                f"{median(run['visible'] for run in runs):>6.0f}"
                f"{median(run['drag_px'] for run in runs):>9.0f}"
                f"{median(run['elements'] for run in runs):>8.0f}"
                f"  [{', '.join(f'{value:.2f}' for value in gpus)}]"
            )

        return 0

    board = root / "probe.canvas"

    shutil.copyfile(args.src, board)
    shutil.copyfile(str(args.src) + ".view.json", str(board) + ".view.json")

    geo = load_board(board)
    log = Path(os.environ.get("LOCALAPPDATA", "")) / "Outspread/board.log"

    # (标签, 缩放, 相机中心, 手势, 额外开关)——idle 那一档是**零点**：什么都不做，只让窗口开着。
    configs = [
        ("静置 100%", 1.0, geo["dense"], "idle", ()),
        ("平移 100%", 1.0, geo["dense"], "pan", ()),
        ("空区平移", 1.0, geo["empty"], "pan", ()),
        ("滚轮 100%", 1.0, geo["dense"], "zoom", ()),
        ("拖一格 100%", 1.0, geo["dense"], "drag", ()),
        ("平移 250%", 2.5, geo["dense"], "pan", ()),
    ]

    # 量过并且已撤回的一档：`--cached-cards`（UIElement.CacheMode＝框架自带的栅格化烘焙）。
    # 10-27 三发中位：平移那档 GPU 4.05→5.32%、CPU 61→54%（散布之内），滚轮 6.54→6.19%——
    # 没有一处赢，官方那句"缓存＋每帧动画＝反而每帧重栅"在我们这层被实测证实。开关已删。

    print(f"板：{board}（{geo['cards']} 格，副本；boards\\ 里那块没动）")
    print(f"每档跑 {args.repeats} 遍取中位：这台机器的 GPU 计数器单发能差三倍，一发不算读数。")
    print(f"{'档':<12}{'帧数':>7}{'每帧ms':>9}{'GPU中位%':>10}{'CPU占一核%':>11}{'元素':>7}  GPU 各发")

    # 零点对照：一枚什么都不做的进程，CPU 那一列必须读到≈0；读不到 0 就是尺的问题，不是产品的问题。
    sleeper = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    time.sleep(1.0)
    zero = cpu_fraction(sleeper.pid, 3.0)
    sleeper.terminate()
    sleeper.wait(timeout=10)
    print(f"{'对照·睡着':<12}{'—':>7}{'—':>9}{'—':>10}{zero:>11.2f}{'—':>7}")

    for label, zoom, world, gesture, flags in configs:
        runs = []

        for _ in range(args.repeats):
            # 每一发都从原始那份重拷：拖一格会把格子写进盘，下一发就从"已经被拖走的位置"接着拖。
            shutil.copyfile(args.src, board)
            shutil.copyfile(str(args.src) + ".view.json", str(board) + ".view.json")

            runs.append(one_run(args.exe, board, log, zoom, world, gesture, args.seconds, flags))
        gpus = sorted(run["gpu"] for run in runs if run["gpu"] is not None)

        print(
            f"{label:<12}{median(run['frames'] for run in runs):>7.0f}"
            f"{median(run['per_frame'] for run in runs):>9}"
            f"{median(gpus):>10.2f}"
            f"{median(run['cpu'] for run in runs):>11.2f}"
            f"{median(run['elements'] for run in runs):>7.0f}"
            f"  [{', '.join(f'{value:.2f}' for value in gpus)}]"
        )

    print(
        "\n口径：GPU＝该进程在同一块适配器（同 luid）上所有 3D 引擎通道的合计，"
        "每通道丢掉第一个采样点、丢掉负数采样点；"
        "CPU＝(用户＋内核)时间增量÷墙钟，占满一核＝100%（除以逻辑核数才是任务管理器那一列），与 GPU 并发采。"
        "\n静置档是零点；空区平移是对照：同样的板底、点阵、暗角，0 格便签。"
    )


if __name__ == "__main__":
    sys.exit(main() or 0)
