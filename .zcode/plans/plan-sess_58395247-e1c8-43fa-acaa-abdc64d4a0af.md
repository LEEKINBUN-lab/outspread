# Helm 肢解吸收为画板可选插件 · 实施计划

## 上半部分 · 给你看的（简短）

### Helm 现在承担什么——装修监理的一生

把 Helm 想成一位装修监理，一套房从毛坯到入住他都管：

1. **开工前·图纸定稿**：施工图上写清每道工序、先后顺序、每项怎么算合格。监理审图（plan check），审过才准锁定；锁后改图必须写明原因并留档（plan amend --reason，审计可查）。
2. **派工时·第一道闸**：「贴厨房砖」依赖「水电改造」——水电没完，开工申请直接被退回（claim 拒绝，退出码 2）；确有特批通道（--force），但只在这道闸。
3. **收工时·第二道闸**：工人喊"贴完了"不算完——必须交一份写明"怎么验的"凭据（evidence），缺凭据拒收并告诉你补什么命令。**这道闸没有后门**（complete 不许 --force）。
4. **你随时扫一眼**：哪道在干、哪道堵、哪道完，一眼见底——墙上的展示只是账本的投影，真账本只有一本。
5. **底座**：全工地一把锁（跨进程互斥+原子写，两路人马同时记账写不坏）；一套退出码 0–6，agent 不用猜错在哪类。

### 迁移哪些、怎么迁移——监理搬进独立小屋

- **搬（原样）**：规则本（状态机+DAG+两道闸+plan 锁，约 6700 行纯机制代码，天生零 UI 依赖）、对讲机（CLI 20 命令+MCP 8 工具）、图纸语法（helm-plan 块）。
- **改（三处小手术）**：①账本从"项目根 `.agent_board`"改成"跟着某块画板走"（`<板>.gate.json`，全机一把锁改成每板一把）；②任务和画布挂钩——节点记卡号，卡上出一枚只读状态牌；③装上你 9-26 定的硬规矩：**没写验收标准的工序，想开工直接拒**。
- **不搬**：监理自己那面旧展示墙（Helm 三栏 WinUI 窗口）——你的画板就是那面墙；安装脚本、快捷方式、.workbuddy 拷贝全清，原仓整体进回收站（可捞回）。
- **装到哪**：`D:\MyProject\outspread-plugins\helm-gate\`，画板仓零耦合。画板只加一样东西：卡上的只读状态牌——文件在才显示，不在就跟没装过一样。agent 用 `helm-gate` 命令或它自带 MCP 调闸门。

---

## 下半部分 · 实施（agent 对照执行）

### 0. 前提与成本自报
- 预计 60–90 次工具调用、跨 1–2 会话、六个阶段。测试基建从零起一座（插件仓 xunit）——相称性：闸门是 agent 长期打的核心链路，误放行=破坏凭据文化，值得。
- 每轮开工 `board_inbox` 对账；收尾落板凭据。@n134/@n135 decision 由用户自己点，agent 不代勾。
- 本机治理：清理阶段动工前**先读 `D:\Documents\Agent安装治理规则.md`** 并照办（这是用户全局规则）。

### 1. 建插件仓 + 原样迁移
```
D:\MyProject\outspread-plugins\helm-gate\
  helm-gate.sln
  src\HelmGate.Core\      ← D:\MyProject\Helm\src\Shared 全部 9 文件原样拷入
  src\HelmGate.Cli\       ← D:\MyProject\Helm\src\Helm.Cli 的 9 个文件（不含 Projects/Discover）
  tests\HelmGate.Tests\   ← 新建 xunit
  tools\gate-call.py      ← 仿 borad\tools\board-call.py，EXE 指向 HelmGate.Cli 产物
  docs\README.md、plan-syntax.md（从 Helm docs\plan-syntax.md 搬）
```
- 命名空间 `Helm.Core`/`Helm.Cli` **不改**（保真移植，减少无谓 diff）；csproj/Assembly 改名 HelmGate.Core / HelmGate.Cli（exe=helm-gate）。Core 与 Cli 改为 ProjectReference（不再用 Compile Include 链接）。
- 不迁：`Commands.Projects.cs`、`Commands.Discover.cs`（Helm 自有项目登记/扫描，与插件无关）；Helm.Board 整个 UI 工程；.githooks；integrations\；install 脚本。
- `git init` 新仓，首提交=迁移基线（原样、未改），第二提交起再做适配——两步分开，回滚与审查都容易。

### 2. 机制适配（按文件列）
1. **BoardLocator.cs → GateLocator**：状态文件 = `<板完整路径>.gate.json`（与 `.view.json`/`.inbox.json` 同族）；plan 文件 = `<板完整路径>.gate.plan.md`。解析顺序：`--board` 参数 → 环境变量 `OUTSPREAD_BOARD`（指 .canvas 文件）→ cwd 向上找 boards\ 下唯一 .canvas（多块则报用法错要求显式）→ `%LOCALAPPDATA%\Outspread\last-board.txt` 参照。删除 HELM_ROOT 与 .agent_board/.git 向上搜索逻辑。`init` 校验目标 .canvas 存在，**不再往项目根写 AGENTS.md**。
2. **StateStore.cs**：ctor 接板路径；互斥体名 `Global\Outspread.Gate.` + SHA256(大写全路径) 前 16 hex（每板一把，其余锁/原子写/重试逻辑原样保留）；`AgentsFilePath` 删除。
3. **BoardState.cs + schema\state.schema.json**：node 加可选字段 `card`（画板卡 id，`^[ua][0-9a-f]{12}$`）；**修复已知契约裂缝**——`intent`/`acceptance` 补进 schema（模型有、schema 没有、additionalProperties:false，一旦写入即被拒）。schemaVersion 保持 2（附加可选字段，两侧校验器都在本仓内）。
4. **Commands.cs**：`Claim` 升级——`UnmetDependencies` 之外，`Acceptance` 为空 → 拒（exit 2，提示 `helm-gate update <id> --acceptance …`），`--force` 保留；其余闸（complete 证据+依赖、Update 冻结禁改标题、Remove 计划节点禁删）原样。
5. **新增 attach/detach**：`helm-gate attach <nodeId> --card <cardId>` 写 `node.card`（只做格式校验，不读 .canvas——卡不存在时投影端自然不画牌，零耦合）；`detach` 清空。写入走 StateStore.Mutate。
6. **Commands.Mcp.cs**：8 工具改名 `gate_next/gate_show/gate_claim/gate_update/gate_complete/gate_block/gate_list/gate_plan_show`；serverInfo=`helm-gate 0.1.0`；协议与"拒绝=isError:true 工具结果"的语义原样。
7. **Program.cs**：命令表去掉 projects/discover，加 attach/detach；help 文案改 helm-gate；退出码体系原样。
8. StateSerializer/PlanParser/PlanHasher/StateValidator/NodeIds/Errors/ParsedArgs：原样（除命名空间所在工程变化）。

### 3. 插件仓测试（Helm 零测试，全部新写——风险对应：闸门误放行）
- 闸门行为：claim 拒（依赖未完/缺 acceptance）、claim --force 放行、complete 拒（无 evidence/依赖未完）且无 --force、next 只出零未满足依赖节点、Update 冻结节点禁改标题、Remove 计划节点禁删。
- 环检测：DFS 报完整环路径。plan：check 报错带行号、lock 继承执行状态、hash 漂移检出、amend 缺 --reason 被拒。
- Store：per-board 互斥体两进程并发不交错、原子写不留半文件、revision 单调。
- 序列化：round-trip 含 card/intent/acceptance、LF 强制。attach/detach 格式校验。
- MCP 冒烟：gate-call.py 走 initialize→tools/list（8 枚）→gate_claim 拒绝路径返回 isError:true。
- 跑法：`dotnet test tests\HelmGate.Tests`。

### 4. 画板显示挂接（D:\MyProject\borad，只改显示，最小批）
1. `src\Outspread.Core\CanvasStore.cs:48` 旁加 `GatePath(boardPath) => boardPath + ".gate.json"`。
2. 新 `src\Outspread.Core\GateProjection.cs`：只读容错载入（同 BoardReader 的 FileShare+重试模式），产出 `GateChip(CardId, Status, Progress, Blocked, Title)` 列表；坏文件/缺文件→null+日志，**绝不炸**；未知字段忽略（与全仓"未知字段原样保留"哲学一致）。
3. `NodeVisual.cs`（PaintChips 约 L2265 一带）：有投影时在现有牌子排前插一枚**只读状态牌**（planned 灰/doing 蓝/blocked 橙/done 绿/dropped 灰删除线，形状与标签牌同族、与完成牌绿勾红叉区分）；点击→现有详情浮层加一行 evidence/blockedBy（只读，无切换）。`node.card` 为空的卡不画。
4. FileWatcher（MainWindow 约 L2898）：对 `*.gate.json` 加过滤分支，0.4s 去抖同款 → 只重读投影重画受影响卡的牌子，**不并入 .canvas 合并链路**。
5. `.gitignore` 加 `*.canvas.gate.json`（机器本地账，与 inbox 同类；gate 状态不进同步契约、不进 .canvas——严格第三方读取器风险为零）。
6. 画板仓回归红线：无 gate 文件的板零变化；`check.cmd` 六步全绿后才 commit（本仓既定纪律）。
7. 文档：borad README 加"可选插件"一段（三行：是什么、怎么装、状态不进格式）。

### 5. 安装与注册
- `helm-gate install`：写 `%LOCALAPPDATA%\Outspread\plugins.json`（`{schema:1, plugins:[{id:"helm-gate", exe:"<绝对路径>", installedAt}]}`，仿 SyncConfig.Load/Save 模式）；`uninstall` 反操作。
- `outspread plugin list` 子命令（读 plugins.json，仿 SyncCommand.cs:34 的子 switch，约 60 行）——这是"安装到画板"的注册面；画板窗口本身不读它（显示只认 gate 文件在场）。

### 6. 残留清理（回收站语义）
- **先读 `D:\Documents\Agent安装治理规则.md`**，若其规矩与本清单冲突以它为准并汇报。
- 确认无 Helm 进程在跑（tasklist 查 Helm.Board/Helm.Cli）。
- 移入回收站（PowerShell `Microsoft.VisualBasic.FileIO.FileSystem::DeleteDirectory(...,SendToRecycleBin)`，不用永久删）：`D:\MyProject\Helm\` 整仓（46 提交无远端，回收站=可捞回的保险）；`%APPDATA%\Helm\`；开始菜单 `Helm.lnk`；`~\.workbuddy\binaries\helm\` 与 `~\.workbuddy\skills\helm\`。
- 保留不动：`D:\test\123\.agent_board`（若在）＝用户数据，gate 将来可接管。
- borad 仓 `Outspread.Board.csproj:27` 提到 Helm README 的注释改为自述。

### 7. 端到端验证（真实链路）
1. `%TEMP%\gate-e2e\` 建试板（outspread new）+ 三张卡；`helm-gate init --board …`；attach 三卡。
2. 写 helm-plan 块 → `plan check` → `plan lock`；改图缺 --reason 被拒。
3. `claim T1` 被拒（缺 acceptance）→ 补 acceptance → 过；`claim T2`（依赖 T1）被拒；`complete T1` 无 evidence 被拒 → `update --evidence` → 过。
4. 开画板窗口看试板：状态牌随 gate 文件变化自动刷新（FileWatcher）；`outspread shot` 截图存证（牌子像素观感=**视觉债**，复现通道：本步骤命令，不冒充回归锁）。
5. `tools\gate-call.py` 全套 MCP round-trip。
6. `check.cmd`（borad）全绿证明画板无回归；`dotnet test`（插件仓）全绿。
7. 界面覆盖清单随汇报附上：状态牌五态/点击详情/无 gate 板零变化/坏文件容错/helm-gate 命令帮助/MCP 8 工具/install-list——逐项标已覆盖或未覆盖+理由。

### 8. 收尾
- borad 仓小批提交（显示挂接、gitignore、README、csproj 注释各一批，闸门绿才 commit）；插件仓按阶段提交。
- 更新记忆：闸门机制已从 Helm 迁至插件（防下次找错仓）。
- 落板凭据：`board_read` 回读未结数写入汇报；dsh-chamber 侧 = 下一轮（本计划不含）。

### 明确不做（MVP 边界）
chamber 一切（下轮）；类型化边/卡状态机进 .canvas 格式（地图④的语义层，另立项）；gate 状态跨设备同步；安卓端显示；Helm 三栏 UI 复活；projects/Discover 扫描。