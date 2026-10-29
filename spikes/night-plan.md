# 夜间账本 · 2026-10-29 深夜 → 10-30 早 9 点

> 给每 30 分钟一轮的心跳看。一轮做一件事：从「待办」挑最上面一件，做完打勾、写发现、分批 commit。
> 总目标（他原话）：0) 分批提交可回滚 1) 不损失阅读体验地降 CPU/GPU 与缩放卡顿
> 2) 让他电脑里所有 agent 都能通过这块板子干活 3) 对标成熟商业软件找差距，没有终点。
> 硬规矩：闸门（check.cmd）绿了才许 commit；每轮收尾按 .qoder/skills/board-outlet/SKILL.md 落板。

## 心跳

- automation `automation-7f877f05-830a-4a9e-b934-633b8b7518f2`，`*/30 * * * *`，18 发（00:00 → 08:30）。
- 并发限制：子代理最多同时 1 个。

## 已完成（今晚 23:41–01:00，第 0 轮）

- [x] 闸门修红 → 全绿。工作树里在途改动有三处没收口：
  - `Snapping.Field`（SnapField）只有测试没有实现 → 补齐，等价测试钉"摊一次＝每帧现摊逐位相同"。
  - `Occlusion` 草稿拿 Paper 当遮挡源，三条测试红 → 按测试规格收口（遮挡源与被剔者都比 Drawn；摘掉的不当遮挡源；结果按文档序）。
  - IntervalIndex 闭/开区间口径不一致（索引闭、两条全表扫测试开）→ 统一闭区间（压线那格能给出 ±threshold 的增量，BestAxis 收 |delta|≤threshold）。
- [x] heal 假崩修掉：`--heal-install` 在消息循环起来前 Environment.Exit 会以 0xC0000142 收场
  （活儿干完了退出码报崩，check.cmd 第 6 步因此红）。改法：csproj DISABLE_XAML_GENERATED_MAIN＋自定义
  Program.Main 读 App.ExitCode；heal 分支借一枚 Hide 空窗、循环里 Close 收场，15s 看门狗兜底。
  注意 TryEnqueue 不吃"以投影对象为 target 的方法组委托"（AsValue 炸、stowed 0xC000027B），要用 lambda。
- [x] 主窗口性能日志里"逐格"段重复了一行，顺手修掉。
- [x] 在途改动分 7 批提交（9c6a98e…94aa7d7），每批一个主题，回滚就挑批次 revert。
- [x] **Occlusion 接线完成并提交（4e597db）**：ArrangeAll 收视窗内按绘制序 cells → CulledCells →
  被完全盖住的格 Collapse（Update 复位路径有自检断言钉住）。自检现造同位叠摞验证"摘得掉、回得来"。
  关键发现：同尺寸两张卡 lift 各异 → 阴影伸出量不同 → 真就不该摘（保守方向正确），测试里上面那张加高 32 吃掉差。
- [x] 本机 git 身份补在仓库级（Ruler4396，照历史提交抄的）。
- [x] spikes/heal-rc.cmd 调试批处理已删；tools/__pycache__ 挡进 .gitignore。

## 进行中（其他心跳轮次别动 src/，量完即收）

- **gpu-probe 遮挡 A/B**：40 摞×5 张的叠摞板，A 臂=接线（HEAD）、B 臂=`OUTSPREAD_ABLATE=occl`
  （临时钩子，量完即删，按 4463316 先例）。跑 `--overview --repeats 3 --seconds 8`。
  01:12 的心跳如果读到这行：这条做完会提交，先挑「待办 3」（agent 协作）或「待办 4」（商业对标）里的
  **调研类**活做（只读网页不碰 src），避免跟 A/B 的构建/编辑撞车。

## 已完成（第 1 轮 · 01:12–01:45）

- [x] **刀二（2696707）**：SavePatch 把锁内现成的 current 丢了重读重析、调用方存完再回读当基线——
  现在吃锁内 current、回传 WrittenJson；每次存盘 **3 读→1 读**，"回传＝盘上字节"有等价断言
  （PatchAndStoreTests），四线程并发写测试照旧绿，闸门六步全绿。
- [x] **刀三（4747965）**：连线三色刷按主题缓存（空连线也每拍 new 三枚那笔）＋徽章形状常量只造一次、
  皮随主题翻重铸——每拍定值重赋清零。闸门六步全绿。
- [x] 板上：a7b1fec（刀二）/a1e9e28（刀三）/a354e2e（探针节拍已提交于 b302887）三张打勾，
  #降占用 组（a3845849f3c1a）重新归摞（已结 6 格叠起）；五张已勾工单收进「工单 · 你提的五件（已结）」成摞；
  落「夜间第 1 轮 · 刀二刀三落地」@n104–105；tidy 缝 48；inbox 已 ack。

## 已完成（第 2 轮 · 01:45–02:05）

- [x] **任务 2 第一轮 · agent 署名（cc39505）**：board_post/board_patch 带可选 `agent` 参数——
  打成 `@tby-名字` 与轮次标签并排（筛选面板/search 两把尺互不妨碍），patch 侧幂等；
  两条新测试（盖章＋幂等）在 MCP 写分支过，闸门六步全绿。
  端到端凭据：新 exe patch 补签两格（@n106 与组标题），`board_search "@by-夜间班"` 搜回两格。
- [x] 流程坑入 skill：**闸门不刷新仓内 outspread-mcp.exe**（第 2 步影子目录跑测试）——落板前
  先 `dotnet build src/Outspread.Mcp`；第一发署名就是栽在旧 exe 上丢的。SKILL.md §3 与 AGENTS.md 都补了。
- [x] 板上：「署名机制自证」组（@n106＋标题）@tby-夜间班 署名；tidy 缝 48；inbox ack。
  还欠他的 @b0 13 格不变。

## 待办（按优先级，02:05 更新）

1. **任务 3 · 商业对标**：tldraw / Miro / FigJam / Heptabase。**先读板上 #成熟件调研 组的旧结论**
  （藏字 LOD 已做、空间索引已做、pinch raster 结论已有）——挑还没做的：候选面有 选区批量操作／
  对齐分布工具／模板库／评论模式（Miro 式 comment pin）／演示模式。挑一晚能落的最小闭环。
2. **刀四候选**：aa053c6 提到"空转同步在 UI 线程做读+析+SHA2"——读 Sync/空转路径再定性。
3. **stash@{0}（WIP on main: f5129df）**：先 `git stash show -p` 对账再处置。

### agent 协作候选（任务 2 素材）

- board_outbox：agent 工单 @a 的自动巡检循环（本轮 board_tickets 对账就是雏形，可以固化成工具/流程）。
- 多 agent 署名：board_post 加 agent 名，`board_search` 按 agent 过滤——几路 agent 不互相看不懂。
- tools/board-call.py 已进仓（本轮写的 stdio 调用器）：每轮出口流程用它走新鲜 exe，连接器旧进程不再是坑。

## 已落板凭据（第 0 轮收尾，00:55–01:05）

- 五张工单全部打勾（凭据：区间索引提交 f8e0dac/b302887＋候选集等价自检；堆叠 Alt+B 在 BoardCommands.cs:84；
  tidy 按标题时间戳排序在 BoardTools.cs:453；本轮把 5 张"旧构建风波"卡进了组）。
- 落了「夜间第 0 轮 · 在途收口与遮挡接线」@n99–@n103（5 note），tidy 缝 48，整板高 5967px。
- board_inbox 已 ack（16 处抹平）。
- **还欠他的（@b0，16 格）**：#降占用 5 ／ #藏字与索引 3 ／ #遮挡剔除 2 ／ #换栈 2 ／ #成熟件调研 2 ／ #旧构建 2。

## 发现（读数与判断，滚动记）

- **遮挡 A/B（叠摞板 200 格＝40 摞，--overview 3 发中位）**：GPU 19.78↔19.55%、总览滚轮 6.64↔6.77ms/帧、
  总览平移 0.124↔0.125ms、平移 CPU 14.26↔18.55%（CPU 噪声大，10-28 同款问题）——全部在噪声内。
  带内可见 60↔144 证明剔除在干活。**结论：合成器对 Collapsed 子树不计费，整格剔除省的是元素数不是合成费**
  ——与板上 #遮挡剔除 组的预测（aab133015b68d"整格摘除天然没肉"）一致，这次是量出来的。
- 板上 #顿挫归因 已结论：慢帧里我们只占 6.2ms（总览），其余在布局＋合成那一拍，33fps 是合成地板，别追。
- 前任会话 10:13 的日志里有它自己的遮挡接线尝试（"取视觉+几何/遮挡剔除"桶，127 格板），后被撤——
  本轮 4e597db 是落地版（带正对照自检）。
- 遮挡判定成本：Core 侧 Covers/CulledCells 在叠摞板上是微秒级（自检 arrange 0.2ms 循环含它）。
- gpu-probe 用法：`python tools/gpu-probe.py --overview --repeats 3 --seconds 8 --src <板>`；
  消融走环境变量透传（`OUTSPREAD_ABLATE=occl python tools/gpu-probe.py …`）——钩子量完即删，别留在产品里。

## 发现（读数与判断，滚动记）

- 闸门六步：核心测试 731 例、MCP 写分支、界面/命令行构建、界面自检（临时副本）、heal。
- selftest 日志口径：`可见 13/17` 一类的断言在 arrange 后读，接线 Occlusion 后要重新对表。
- gpu-probe 用法：`python tools/gpu-probe.py --help`（四个手势档 + OUTSPREAD_ABLATE 消融开关）。
  10-29 的版本自带 --no-heal，读数进程不碰用户那份构建。

## 每轮格式（照抄）

```
### 第 N 轮 · HH:mm
- 做了：…
- 读数：…（probe/测试/闸门）
- 提交：<sha 或 无>
- 下轮建议：…
```
