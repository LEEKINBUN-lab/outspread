import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/* 图标怎么出来的：形状和材质全在 logo-variants.html 里（那才是能改的地方），
   本脚本只干两件事 —— 驱动无头浏览器按尺寸各出一张 PNG，再把 PNG 装进 .ico。
   为什么不在这里画：材质用的是 SVG 滤镜（两层投影 / 内阴影 / 高斯模糊 / 颗粒），
   纯 JS 光栅化做不了；浏览器就是这台机器上唯一的渲染器。
   为什么不拿 256 缩放：16/24 档上三条细墨条会并成一团灰，所以那两档在页面里换了两条加粗的。 */

const SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const PAGE = new URL("../logo-variants.html", import.meta.url).href;
const WORK = "D:/Temp/outspread-check/ico-build";
const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), "../../src/Outspread.Board/Assets");
const ICO = path.join(OUT, "outspread.ico");
const PNG256 = path.join(OUT, "outspread-256.png");

const EDGE = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(p => existsSync(p));
if (!EDGE) { console.error("FAIL 找不到 Edge：出图通道就是无头浏览器，没它画不出来"); process.exit(1); }

mkdirSync(WORK, { recursive: true });
mkdirSync(OUT, { recursive: true });

/* PNG 头就能判形状：IHDR 里是宽高，colorType 6 = 带 alpha */
function pngInfo(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) return { err: "不是 PNG" };
  const ihdr = buf.subarray(16, 24);
  return { w: ihdr.readUInt32BE(0), h: ihdr.readUInt32BE(4), depth: buf[24], colorType: buf[25] };
}
const images = [];
for (const s of SIZES) {
  const out = path.join(WORK, `${s}.png`);
  execFileSync(EDGE, [
    "--headless", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
    `--user-data-dir=${WORK}/profile`, "--no-first-run",
    "--default-background-color=00000000",
    `--window-size=${s},${s}`, `--screenshot=${out}`, `${PAGE}?sec=ico&s=${s}`,
  ], { stdio: "pipe", timeout: 60000 });

  if (!existsSync(out)) { console.error(`FAIL ${s}px：Edge 没落盘`); process.exit(1); }
  const buf = readFileSync(out);
  const info = pngInfo(buf);
  if (info.err) { console.error(`FAIL ${s}px：${info.err}`); process.exit(1); }
  // 允许 1px 的边界差：窗口尺寸含边框取整时会出现
  if (Math.abs(info.w - s) > 1 || Math.abs(info.h - s) > 1) {
    console.error(`FAIL ${s}px：出图是 ${info.w}×${info.h}`); process.exit(1);
  }
  if (info.colorType !== 6) { console.error(`FAIL ${s}px：colorType=${info.colorType}，透明底没生效（四角不会是空的）`); process.exit(1); }
  if (buf.length < 300) { console.error(`FAIL ${s}px：只有 ${buf.length} 字节，多半是空白页`); process.exit(1); }
  images.push({ s, buf });
  console.log(`  ${String(s).padStart(3)}px  ${String(buf.length).padStart(6)}B  ${info.w}×${info.h} colorType=${info.colorType}`);
}

/* 装进 .ico：PNG-in-ICO（Vista 之后支持），每档一条目录项 */
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(images.length, 4);

const ENTRY = 16;
let offset = header.length + ENTRY * images.length;
const entries = images.map(({ s, buf }, i) => {
  const e = Buffer.alloc(ENTRY);
  e.writeUInt8(s >= 256 ? 0 : s, 0);
  e.writeUInt8(s >= 256 ? 0 : s, 1);
  e.writeUInt16LE(1, 4);
  e.writeUInt16LE(32, 6);
  e.writeUInt32LE(buf.length, 8);
  e.writeUInt32LE(offset, 12);
  offset += buf.length;
  return e;
});
writeFileSync(ICO, Buffer.concat([header, ...entries, ...images.map(i => i.buf)]));
writeFileSync(PNG256, images.find(i => i.s === 256).buf);

/* 写回读：验的不是"我算了多少字节"，而是这枚文件本身能不能被按 .ico 读回来 */
const back = readFileSync(ICO);
const n = back.readUInt16LE(4);
if (n !== images.length) { console.error(`FAIL 目录条数 ${n}，应为 ${images.length}`); process.exit(1); }
let cursor = 6 + 16 * n;
const table = [];
for (let i = 0; i < n; i++) {
  const e = 6 + 16 * i;
  const edge = back.readUInt8(e) || 256;                 // 0 存的是 256
  const size = back.readUInt32LE(e + 8);
  const off = back.readUInt32LE(e + 12);
  if (off !== cursor) { console.error(`FAIL 第 ${i} 项偏移 ${off} 与上一项末尾 ${cursor} 不接`); process.exit(1); }
  if (back.readUInt32BE(off) !== 0x89504e47) { console.error(`FAIL 第 ${i} 项开头不是 PNG`); process.exit(1); }
  const ihdr = back.subarray(off + 16, off + 24);
  const w = ihdr.readUInt32BE(0);
  if (w !== edge) { console.error(`FAIL 第 ${i} 项目录写 ${edge}px、图本身 ${w}px`); process.exit(1); }
  table.push(`${edge}×${ihdr.readUInt32BE(4)}`);
  cursor = off + size;
}
if (cursor !== back.length) { console.error(`FAIL 末尾多出 ${back.length - cursor} 字节`); process.exit(1); }
const p256 = readFileSync(PNG256).length;
console.log(`OK ${ICO}  ${back.length}B  ${n} 档 [${table.join(" ")}]`);
console.log(`OK ${PNG256}  ${p256}B`);
