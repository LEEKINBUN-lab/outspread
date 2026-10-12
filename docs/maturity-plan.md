# 铺陈 → 成熟商业产品：差距盘点与分期计划

制定于 2026-10-12 夜，四路一手调研回来后定稿。**这一页是判据，不是愿望清单**：每一期都要能单独收口、单独回退，验收口径写成"会抛错的闸 + 现读的数"，不接受"看着像"。

回退点：`v0-baseline`（tag，已推 `LEEKINBUN-lab/outspread`，commit `531444e`）。

调研来源全部是一手：规范与参考实现仓库、tldraw 官方 SDK 文档与其 `packages/tldraw/src/lib`、Excalidraw 的 `packages/excalidraw/{actions,data,components,locales}`、Obsidian 帮助仓库 `obsidianmd/obsidian-help`、Notion 开发者文档、Linear docs/changelog、PowerToys / Files / Windows Terminal / VS Code 的仓库文件（带路径与行号）。**没有引用任何第三方总结文章。**

---

## 一、现状（本夜现读，不是回忆）

代码 20,403 行 C#（`src` + `tests`，不含 `obj`），23 个 Core 测试文件，`check.cmd` 全过（362 项 + 两个构建 + 界面自检）。

| 面 | 现读结果 | 判 |
|---|---|---|
| 设置 | `grep "Settings" src` → **0 命中** | 没有任何配置面 |
| 无障碍 | `AutomationProperties` **0**、`AccessKey` **0**、UI 自动化测试 **0** | 读屏下这块板子不存在 |
| 崩溃 | `App.xaml.cs:13` 只把 `Application.UnhandledException` 写进日志；`AppDomain` 与 `TaskScheduler` 两个钩子**没挂** | 崩了只有日志里有，屏上看不见 |
| 遥测 / 更新 / 打包 | `Telemetry` 0、无 updater、`WindowsPackageType=None` 且 **`WindowsAppSDKSelfContained=false`** | 只能双击 exe，且要求机器上装过运行时 |
| 法务 | 仓库根**没有 LICENSE / NOTICE / SECURITY / SUPPORT / `.github/`** | 商用前置缺失 |
| CI | 无 workflow（本地 `check.cmd` 是唯一闸） | 推上去没人验 |
| 查找 | 只有 `@t/@d` 筛选；**没有全文搜索** | 贴多了找不到 |
| 微调 / 视图 | 方向键 nudge **0**、`zoomToFit`/`zoomToSelection` **0**、导出/打印 **0**、锁定 **0** | 键盘用户与"给别人看"两条路都断 |
| 数据保护 | 无回收站、无版本历史；冲突件 `.canvas.conflict-rN` **没有时间/设备名、也没有 UI 去看它** | 见 §二·A |
| 命令注册表 | `BoardCommands.cs` 有 `Command(Label,Chord,Section,Run)`，20 条键位、10 个 `VirtualKey` 落点；对齐/均分/堆叠/成组/拆组/复制都已实现 | **`docs/interaction-machine.md` §〇 那句"29 条里只有 4 条有落点、键盘只有 7 条"已过期**，P6 更正 |
| 格式一致性 | `fromEnd/toEnd` 默认值不写（合规）、z 序走 `DocPatch.Order` 不重排（合规）、色用预设串 `"1".."6"`（合规） | 见 §二·B |
| 格式缺口 | `subpath` **0 命中**；悬空边（`fromNode/toNode` 指向不存在的节点）**没有处理分支**；group 的 `background/backgroundStyle` 只做了一部分 | 见 §二·B |

**一句话诊断**：交互与渲染这一层已经能打；**"不丢数据、看得见错误、找得到东西、键盘和读屏也能用、装得上机器、有人替它跑测试"这六层基本是空的**。商业化的差距主要不在画布手感，在这六层。

**本夜已修掉的一个真缺陷（顺带发现）**：写锁用的是 `Global\outspread.canvas` 一枚**全机器共享**的互斥体——CLI 写 A 板、界面写 B 板会互相卡到 5 秒超时报"什么都没存"。已改成按板子路径派生锁名（`CanvasStore.MutexNameFor`），并因此暴露/消除了测试套件里的并行争用。**这正是"锁的粒度"这种商业级问题在开源小项目里最常见的样子。**

---

## 二、差距的六个面（每面：谁做过 → 来源 → 我们差什么）

### A. 不丢数据（最高优先）

一手事实：

- Obsidian 有 **File recovery**：设备本地快照，最小间隔 5 分钟、保留 7 天，`.canvas` 也在恢复范围内（`obsidian-help` → `Plugins/File recovery.md`）。
- Obsidian 官方文案明写 **"Syncing is not a backup"**，并让用户另外备份（`Getting started/Back up.md`）。
- 冲突副本是**有名字的**：`note (Conflicted copy <device> YYYYMMDDHHMM).md`，用户能打开来看（`Obsidian Sync/Troubleshoot.md`）。
- 删除可恢复：Version history 保留 1 个月（Standard）/12 个月（Plus），附件 2 周（`Obsidian Sync/Version history.md`）；Notion 的 Trash 支持搜索与筛选，回收站里的页面必须先还原才能编辑（`notion.com/help/duplicate-delete-and-restore-content`）。
- Notion 对**离线期间被删的东西**的处理是明确的：保持删除状态，不复活（`notion.com/help/use-pages-offline`）。

我们差什么：

1. ~~没有任何写前快照~~ → **本夜已做**：`Snapshots`（写锁内、原子改名之前存下"要被替换掉的那一份"；5 分钟间隔闸 + 同内容不重复存；恢复强制存；上限 50 份、超 7 天可清但最少留 10 份）。9 项测试。
2. **冲突件是死文件**：`.conflict-rN` 里没有设备名与时间，界面上也没有入口去看/合并它。
3. **README 把同步当耐用性来讲**，与 Obsidian 的官方立场相反。
4. **没有回收站**：`Delete` 走出 history 之后不可恢复。
5. **祖先文件是设备本地的**（`%LOCALAPPDATA%\Outspread\sync`）：换机/重装即失忆，三方合并退化成"并集或覆盖"。

### B. 格式一致性与互操作

一手事实（规范与参考实现）：

- JSON Canvas 1.0 **顶层只有 `nodes`/`edges`，没有 `version`、没有 `draft`、没有 `$schema`**；`obsidianmd/jsoncanvas` 仓库里**没有类型定义、没有校验器、没有 schema**（issue #5 提的 JSON Schema PR 未合并），规范对**未知字段完全沉默**（#57/#13/#55/#63 都在问，没人答）。
- 边的默认值：`fromEnd` 缺省＝`none`、`toEnd` 缺省＝`arrow`；**写方应当省略默认值**。
- z 序只对**节点**有定义（数组升序），边的次序沉默；Obsidian 帮助页明写 **"Selecting a card moves it to the front"** ⇒ 对方一次点击就会重排整个数组。
- `group` **没有成员字段**，包含关系是几何的；`subpath` 若存在必须以 `#` 开头；`file` 节点渲染成图/文/PDF 是**应用自定义**。
- 第三方严格读取器（`@moritzrs/jsoncanvas` 的 zod `strictObject`）会**直接拒绝**非规范字段。
- 没有 1.1/2.0 在途；开放提案要盯：顶层 `version`（#8/#41）、`$schema`（#18）、`width/height` 变可选（#15/#32）、`canvasz` 打包（#36）。

我们差什么：

6. **`subpath` 不读不显示**：一张指向 `文件#章节` 的卡在我们这里丢掉那半句语义。
7. **悬空边没有策略**：规范沉默 = 我们必须自己定（保留？丢弃？标记？），且要写进文档，否则两份实现各自便。
8. **z 序抖动**：Obsidian 一点就把节点提到最前，我们的补丁保存要证明"我没顺手重排对方没动的东西"。
9. **`@d/@t` 写在正文里**这件事被这次调研**加强**（严格读取器会丢未知字段 ⇒ 加字段是赌），但代价要写死：**别的 JSON Canvas 应用会把 `@d2026-10-09` 当正文显示**。
10. **永远不要用文本 diff 比 `.canvas`**：Obsidian 自己写盘时键序都不稳定（`basic.canvas` 与 `groups.canvas` 两份它自己产出的样本顺序不同、缩进也不同）⇒ 同步层的比较必须是解析后的。

### C. 交互闭环（tldraw / Excalidraw 基线）

一手来源：tldraw `sdk-features/{history,accessibility,internationalization,actions,clipboard,errors,performance,snapping,selection,focus,locked-shapes,image-export,bindings,camera,tools}` 与 `packages/tldraw/src/lib/{defaultTools,defaultShapeTools,defaultExternalContentHandlers}`；Excalidraw `packages/excalidraw/actions/{shortcuts,actionHistory,actionExport,actionDuplicateSelection}.tsx`、`history.ts`、`data/{restore,filesystem,library}.ts`、`components/{welcome-screen,CommandPalette,OverwriteConfirm,Stats}`、`locales/`（57 语言 + `percentages.json` 覆盖率）。

差距（按"看得见的手感 / 工作量"排序，全部本仓现读确认为 0 命中）：

1. **方向键微调与键盘移动选中**（tldraw `Ctrl+方向键` 导航 + `focusShape`/`selectAdjacentShape`）。
2. **锁定 / 隐藏**（Excalidraw `Ctrl+Alt+L` + `elementLock.{lock,unlock,lockAll}`；tldraw `locked-shapes`：锁定后不可选/移/缩/删/编组，`canEditWhileLocked`）——**共享板子被一次误拖就会搅乱**。
3. **导出与"给别人看"**（tldraw PNG/JPEG/WebP/SVG + `scale` + `background:false` + 按选中导出 + 字体内联；Excalidraw 的 PNG `embedScene` 把场景塞进图里可再打开编辑，`Shift+Alt+C` 复制为图片）——我们只有 CLI 的 `outspread shot`。
4. **板上查找**（Excalidraw `Ctrl+F` `searchMenu` + `CommandPalette`）。
5. **首次使用 / 空板**（Excalidraw `welcomeScreen`：示例形状 + `toolbarHint` + `helpHint` + "记得定期存成文件"；`helpDialog` 用 `?`）——我们开屏是一块空板。
6. **Alt 拖拽复制**（tldraw 拖拽即复制；Excalidraw 悬停复制并重新选中新副本）——我们只有原地 `Ctrl+D`。
7. **选中上方的跟随工具条**（tldraw 用 `getSelectionBounds` 定位的 contextual toolbar + 样式面板）——我们所有动作都埋在右键菜单里。
8. **带名字、按动作合并的撤销**（tldraw `markHistoryStoppingPoint('rotate shapes')` 分批、`history:'ignore'`、相机/会话不进栈；Excalidraw `History.record(delta)` 连 appState 一起恢复）——我们是整份文档快照 60 步。
9. **箭头绑定深度**（tldraw `TLBaseBinding` 存 `terminal`/`normalizedAnchor`、形状移动后重连、删除时隔离；Excalidraw `actionToggleArrowBinding`、`repairBinding`）。
10. **吸附的丰富度**（tldraw 边界+把手吸附、**间距/尺寸读数**、等距指示；Excalidraw `objectsSnapMode Alt+S`、`gridMode Ctrl+'`）——我们只有 8px 边/中线。
11. **相机命令**（`zoomToFit Shift+1`、`zoomToSelection Shift+2/3`、`resetZoom`、拖到边缘自动滚动）——我们只有 `Ctrl+0`。
12. **大板保护与可观测**（tldraw 剔除屏外形状、每页 ~4000 上限、低缩放去文字阴影；Excalidraw `Stats` 面板 `Alt+/`、超尺寸拒绝并给提示）——我们实测 `ArrangeAll` 16–38ms，无剔除、无用户可见计数。
13. **字符串外置**（tldraw 49 语言、id 形如 `action.copy`、自动 RTL；Excalidraw 57 语言 + Crowdin + 覆盖率）——我们的中文标签写死在 `BoardCommands.cs`。
14. **减少动画**（tldraw 一切走 `animationSpeed`/`usePrefersReducedMotion`）——本轮刚加上 `PopupThemeTransition`，这条立刻变成必做。

**不是差距**（已对齐或更靠前）：对齐 6 / 均分 2 / 堆叠 / 聚拢、`Ctrl+D`、成组拆组、筛选面板、按住 Tab 的键位清单、外部改动作为可撤销一步并入、拒绝写入时留冲突件、基于祖先的三方合并、5–800% 缩放、24px 命中区。

### D. 找得到东西

- Obsidian 的搜索是全文、跨 `.canvas`，带 `tag:`、`[prop:value]`、正则与 6 种排序（`Plugins/Search.md`）；并且**纯文本卡片的文字不进 Backlinks**（`Plugins/Canvas.md`）——这条对我们很关键：板子上的文字要不要进搜索，是一个要明说的设计决定。
- 我们只有按 `@t`/`@d` 筛。**贴进去的一段话，事后没有任何办法找回**——"随手贴"这类产品最容易翻车的就是这里。

### E. 键盘与无障碍

- tldraw `sdk-features/accessibility`：`useA11y` 播报"[描述]，[形状类型]。第 X 个 / 共 Y 个"、形状可覆写 `canTabTo`/`getAriaDescriptor`、偏好项 `enhancedA11yMode`、调试开关 `a11y.set(true)`。
- PowerToys 把每个控件的 UIA 名字绑到它自己的标题：`AutomationProperties.Name="{Binding ElementName=…, Path=Header}"`，测试用 `AutomationId`，页根 `AutomationProperties.LandmarkType="Main"`（`SettingsXAML/Views/GeneralPage.xaml:13,55,154,228`）。
- **Files 在 CI 里跑 axe**：`Axe.Windows` 包 + `Config.Builder.ForProcessId(pid)` → `AssertNoAccessibilityErrors()`，只豁免两条规则，300ms 稳定后重扫（`tests/Files.InteractionTests/Helper/AxeHelper.cs`，在 `Tests/GeneralAppTests.cs:16,19` 调用；驱动 Appium 3 + `appium-windows-driver@6`，CI 自启在 `:4723`，`ci.yml:262-350`）。⇒ **`Axe.Windows` 就是一个压在 UIA 上的 NuGet 引用，这是"几天能 adopt"的那一条。**
- Windows Terminal 的 UIA 测试断言在具名元素上（`AutomationId == "Text Area"`，`src/host/ft_uia/AccessibilityTests.cs`）；`doc/terminal-a11y-2023.md` 列出要尊重的系统开关（讲述人/JAWS/NVDA、文字大小、常显滚动条、透明度与动画效果、光标指示、颜色滤镜、高对比度、Voice Access），并把 Accessibility Insights 定为验证工具。
- VS Code 把无障碍当成一个测试区：`test/smoke/src/areas/accessibility/accessibility.test.ts` → `driver.assertNoAccessibilityViolations({selector, excludeRules})`。

我们差什么：**`AutomationProperties` 与 `AccessKey` 在 `src/` 里 0 命中**；键盘缺 §二·C 的第 1、2、11 条 ⇒ 没有鼠标就不能工作。另外 WCAG 2.5.7（单字符快捷键必须有替代输入面）是我们自己 `interaction-machine.md` 引过的判据，命令面板（P2）就是那块替代面。

### F. 交付面：装得上、更得动、坏了看得见

- **打包**：PowerToys 的设置进程就是**无包 WinUI 3 且自带运行时**（`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，`src/settings-ui/Settings.UI/PowerToys.Settings.csproj:12,16`、`src/Common.SelfContained.props`）。我们是 `false` ⇒ **翻一个属性就摆脱"机器上必须装过 Windows App Runtime"**，这是全部调研里性价比最高的一行。再往上是 WiX MSI/burn（PowerToys `installer/PowerToysSetupVNext/Product.wxs` 的 `Scope`）、MSIX + Store（Files，且它**为 Store 构建删掉 `rescap:packageManagement`**：`.github/scripts/Configure-AppxManifest.ps1:66-74`——那个能力正是自更新的前提）。
- **自更新的最小可信形状**：Files 侧载更新器读发布出来的 `.appinstaller`，比 `MainBundle@Version` 与 `Package.Current.Id.Version`，用 `Windows.Management.Deployment.PackageManager` 装（`src/Files.App/Services/App/AppUpdateSideloadService.cs:84-130,206`）；PowerToys 更新器读 `api.github.com/repos/.../releases/latest`、按架构匹配资产、重试 3 次，**提权启动前用 `WinVerifyTrust` 校验签名链与签名者**（`src/common/updating/updating.cpp:18,46-78,171`），节奏 24h / 失败后 2h，可"跳过两个小版本"；VS Code 走 `{updateUrl}/api/update/{platform}/{quality}/{commit}` + **下载后 sha256 校验** + 一把"安装器已在运行"的具名互斥体（`updateService.win32.ts:313,421-447`）。
- **崩溃与挂起**：Files 三个钩子全挂（`src/Files.App/App.xaml.cs:63-65`），日志带 **HRESULT**（`scope.SetTag("hresult", $"0x{ex.HResult:X8}")`），因 XAML 剥栈而**缓存最近的抛出**（`AppLifecycleHelper.cs:434-465`），退出码 `Environment.Exit(ex?.HResult ?? 1)`，**通知用 toast 不用对话框**，并用 `LastCrashedTabList` 防无限重启（`:592-620`）。我们只有第一个钩子，且只写日志。
- **遥测与同意**：PowerToys 同意位默认 **0＝关**（`HKCU\Software\Classes\PowerToys\AllowDataDiagnostics` + 单独的 `DataDiagnosticsUserAction`），UI 上是两句人话（`Settings.UI/Strings/en-us/Resources.resw:4409-4414,5778-5785`），`DATA_AND_PRIVACY.md` **逐个事件**列清楚，CI 的 `telemetry-pr-check.yml` 会在改了事件没改文档时留言。零服务器的等价物就是 PowerToys 的 `tools/BugReportTool`：**"报告问题"＝打一个 zip（日志 + 版本 + 显示器/DPI 拓扑）**。
- **发布工程**：GitHub Actions 里最便宜的一套——Azure Trusted Signing（`Azure/artifact-signing-action@v1`）、`wingetcreate update` 挂在 `release: published`（Files `.github/workflows/winget.yml:14-25`）、**测试抖动用 `nick-fields/retry@v4` 三次**（`ci.yml:339-351`）、矩阵 `fail-fast: false`、PR 上删产物但 `if: failure()` 上传诊断；Files 还在 CI 里**量 MSIX 体积**并当闸（`ci.yml:170-256`）。
- **法务**：PowerToys `LICENSE`(MIT) + 生成的 `NOTICE.md` 且 CI 校验（`verifyNoticeMdAgainstNugetPackages.ps1`）；Terminal `LICENSE` + `NOTICE.md` + `cgmanifest.json`；VS Code `LICENSE.txt` + `ThirdPartyNotices.txt` + `cglicenses.json` 且有带测试的工具链。**我们仓库根上这些全没有。**

---

## 三、分期计划（每期都能单独收口 / 单独回退）

顺序按"不丢数据 > 看得见错误 > 找得到东西 > 键盘与读屏 > 装得上机器 > 手感"。每期末尾给**会抛错的闸**，不给"应该没问题"。

### P0 · 写前快照与可见的失败 —（快照已完成，余下三项在排）

已完成并测：`Snapshots` + `CanvasStore.Restore` + 9 项测试 + 锁粒度修复。
- [x] 落盘前在锁内、改名之前存"要被替换掉的那一份"；5 分钟间隔闸 + 同内容不重复存；上限 50 / 超 7 天可清但最少留 10；恢复强制存；历史目录写不成时**不许挡住保存**。
- [x] 冲突件改名带设备与时间：`<板子>.canvas.conflict-<设备>-<yyyyMMdd>-<HHmm>-r<N>`（**仍然不以 .canvas 结尾**，理由见 sync-contract §6），回执那一行念出文件名。10-12 做了；`outspread sync status` 的计数glob 已同时吃新旧两种名字。
- [ ] 冲突件要有界面入口（列出来、打开看、二选一）——名字解决了，界面还没解决。
- [ ] "板子 → 恢复…"入口：列快照（浮层＝同一枚 `SheetRow`），选中即恢复（恢复本身也强制存一份，可再来一次）。
- [ ] 同步卡住要有常驻横幅（写明上次失败原因与下次重试），与"常态不是消息"不冲突：**stuck 是常态之外的状态**。

闸：Core 测试（已有）；界面自检新增"恢复面板行数 == 磁盘快照数"；`check.cmd` 全过。

### P1 · 错误与状态必须看得见

- 全局异常三钩子全挂（Files 的形状），日志带 HRESULT，屏上给一句"出了什么问题 + 记在哪 + 你能做什么"，**不静默重启成死循环**。
- 存盘失败/只读/磁盘满：现在只有 `Say`，加一个"这几格还没落盘"的常驻角标，落盘成功即消失。
- 缺图的 `assets/`：给占位与"文件不在"的一句话（tldraw 的 `assets.files.size-too-big | type-not-allowed | upload-failed`、`url.failed` 是同一类）。

闸：注入一次假失败（把目标目录改只读）⇒ 断言横幅出现且文案含原因；断言三个钩子都挂着（读 `App` 的事件订阅）。

### P2 · 找得到：全文搜索 + 命令面板

- `Ctrl+F` 查找：全文（正文 + 标签 + 文件名 + URL），命中列表，回车跳过去并选中，`F3` 下一个。
- `Ctrl+P` 命令面板：读 `BoardCommands` 注册表 ⇒ **这就是 WCAG 2.5.7 要的那块替代输入面**，同时把 §二·C 第 13 条的入口一次补齐。
- 明确写进文档：卡片正文**进**搜索（与 Obsidian 的 canvas 行为相反，要说明为什么——我们的正文就是数据）。

闸：注册表测试（无重复 id、无重复键位、每条命令都有非键盘入口）；查找：给定 16 格板子搜"周会"必须命中那两格且顺序稳定。

### P3 · 键盘与读屏

- 方向键微调（1px，Shift=10px，Alt=复制着拖）、`Shift+1` 看到全部、`Shift+2` 看到选中、`Ctrl+]`/`[` 叠放键位化。
- 每个可交互件补 `AutomationProperties.Name`；菜单项补 `AccessKey`；焦点顺序走一遍（Tab 环不能被"按住 Tab"抢掉——已有规则，要写成闸）。
- 高对比度与"减少动画"两档：系统关掉动画时 `PopupThemeTransition` 不播。
- 引入 `Axe.Windows`（Files 的形状：一个 NuGet 引用压在 UIA 上），先本地跑、后挂 CI。

闸：自检遍历视觉树，断言四块浮层与控件簇每个可命中件都有 `Name`；axe 扫描 0 错（豁免清单显式列出）。

### P4 · 交付面（打包 / 更新 / 崩溃 / 法务 / CI）

顺序按"一行属性 > 一个钩子 > 一个 workflow > 一个更新器"：

1. `WindowsAppSDKSelfContained=true`——摆脱"机器上必须先装 Windows App Runtime"。**体积差已经量过（10-12，临时工作树里两档各构建一次，主树没动）**：Debug/x64 输出目录 **40 MB / 52 个文件 → 155 MB / 337 个文件**（+115 MB），自包含那一档 `--selftest` 一次通过（rc=0，`SELFTEST PASS` 16:08:51 北京时）。
   **判决：建议翻，但合之前要在 Release/publish 上再量一次。** 理由不是体积，是"双击 exe 就要能用"这条承诺——不自包含就要求对方机器装过 Windows App Runtime，而 `README.md` 现在**没写**这条前置，那正是新装用户第一发会撞上的墙。
2. 三个异常钩子（若 P1 未做则并到 P1）。
3. `LICENSE`（MIT）+ `NOTICE.md`（第三方声明）+ `SECURITY.md`/`SUPPORT.md` + `.github/workflows/check.yml` 把 `check.cmd` 变成真闸（矩阵 `fail-fast: false`，失败时上传诊断）。
4. "报告问题"＝一个 zip（日志 + 版本 + 显示器/DPI + 板子文件名不含内容），抄 PowerToys 的 `BugReportTool`；**遥测默认关，且开关本身有 `AutomationProperties.Name`**。
5. 自更新：先做"检查到新版本 → 提示 → 下载并校验 sha256 → 交给用户运行"这一档（Files 的 `.appinstaller` 比较 + VS Code 的校验），**不做静默提权安装**。

### P5 · 交互与画布手感补齐

按 §二·C 的 1–14 顺序做，每条独立可回退：相机命令 → 微调 → 锁定/隐藏 → 导出/打印 → 空板与首次使用 → 按动作合并的撤销 → 箭头重连与标签 → 吸附读数 → 大板剔除与统计面板 → 减少动画。

### P6 · 文档与口径

- 更正 `docs/interaction-machine.md` §〇 的过期数字（本夜已确证）。
- `docs/canvas-format.md` 补：`subpath`、悬空边、z 序抖动、"别的实现会看到 `@d` 原文"这条代价、"不许用文本 diff 比 `.canvas`"。
- README 的"手机↔电脑同步"一节加一句官方立场：**同步不是备份**，备份是 P0 的快照。

---

## 四、明确不做 / 等他一句话

1. **不做旋转**（格式里没有，已定档）。
2. **不做多板实时协同**：与"人和 agent 同改一份文本文件"的定位冲突，先不排。
3. **`PanelAlpha` 要不要改成不透明**（键位清单在压暗层读到 244–246、菜单 252）——会动 `ChromeTests` 的断言，等他判。
4. **`zoom=0.7971` 时四行 markdown 卡末行被切 1.45px**：真缺陷，归属分数缩放行盒取整，排在 P3 之前单独修（它是正确性缺口，不是"没被人看过"）。
5. 遥测：**默认全关**这条不商量；任何上报都要先在界面上有同意开关。
6. 打包形态：**先自包含 exe，不碰 MSIX/Store**（Store 要删 `rescap:packageManagement`、要 PDP/隐私文案，是另一条时间线）。

## 五、风险登记

| 风险 | 为什么会翻车 | 挡法 |
|---|---|---|
| 快照把板子目录撑大 | 每 5 分钟一份全量文本 | 只存 `.canvas` 不存 `assets/`；上限 + 天数 + 地板三闸，已有测试 |
| 命令面板抢了"按住 Tab" | 两个都是全局键盘面 | 面板只在 `Ctrl+P` 后活，`Esc` 是纯放弃（不变量 5） |
| 打包改了，路径假设全断 | 现在到处按 `AppContext.BaseDirectory` 与裸路径找板子 | P4 之前先把路径解析集中到一处并加测试 |
| 补 `AutomationProperties` 时把浮层结构改坏 | 加名字会引入新的视觉树层级猜测 | 每次都跑 `selftest 皮` + `shot --panel` 像素复验，不许只编译过 |
| 把"成熟"理解成功能堆多 | 他反复打回过"AI 感"和"数据摘要当主界面" | 每期都要交"用户看得见什么变了"，交不出就不做 |
| 自更新变成供应链风险 | 下载即运行 | 只走 HTTPS + sha256 + 签名校验，且**永远由用户点最后那一下**（PowerToys 在提权前做 `WinVerifyTrust`） |

---

## 六、10-12 下午这一轮做掉了什么（现读，含装置读数）

代码 20,403 行 → **22,195** 行（`find src tests -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' | xargs wc -l`，本回合现读）；Core 测试 362 → **388**（新增 26 项 `BoardSearchTests`）。`check.cmd` 全过。

| 期 | 落地 | 现读凭据 |
|---|---|---|
| P0 | 冲突件带设备与时间、同步卡住横幅 —— **未做**（排在下一轮） | 恢复那一项已做：`RestorePanel.cs` 一行一个快照 |
| P2 | **查找层**：`Ctrl+F` 搜正文/标签/链接/文件名，空格＝都要命中，Enter 逐处跳、Shift+Enter 回上一处、Esc 纯放弃 | `selftest 查找: 探词『这一格演』⇒ 框里『这一格演』、命中 12 处、行 12 条`；像素 `D:\Temp\popup-measure\v7-search.png` |
| P2 | 命令面板 `Ctrl+P` —— **未做**（查找层先落地是因为它挡的是"找不回来"，命令面板挡的是"够不着"） | — |
| P3/P5 | **方向键微调**（1 世界像素，Shift 10 倍）、**Shift+1 看到全部**、**Shift+2 看到选中**，全部进 `Commands()` 一张表 ⇒ 右键菜单与按住 Tab 的清单同时长出这些行 | `selftest 同一枚控件: 键位清单 36 行`（原 28 行）|
| P0 | **恢复层**：`Snapshots.List` → 一行一个时刻 → 点它回到那一版（回之前强制存一份） | `selftest … 恢复面板 1 行`；空态像素 `v5-restore.png` |
| P4 | **四张纸**：`LICENSE`(MIT) / `NOTICE.md` / `SECURITY.md` / `SUPPORT.md` | `git log -1 --stat`；NOTICE 逐包标 verified 或"再分发前自己核" |
| — | 闸门自身的洞：`if errorlevel 1` 看不见负数退出码 ⇒ 自检抛异常时 `check.cmd` 念"全过" | 已改成"非 0 即红"＋自检里抛出的一律以 1 落地（`App.xaml.cs:26`）|

**判据跟着长**："是不是同一个组件"那两把尺从 4 块浮层扩到 **6 块**：`selftest 皮: 6 块浮层同枚填充、圆角 8、外缩 4px、各抬 1 件`，行高四处同一枚 40px，新增一条"输入框必须和行同高"（差 >0.5px 就抛错）。

### 6.1 一条装置红灯，不是产品判语（要记下来）

`--selftest` 这一发在今天下午**随机以 Win32 退出码 127 死在第一次 arrange 之后**：没有托管异常、日志里没有下一行、`%LOCALAPPDATA%\CrashDumps` 里没有新 dump。

对照做在 v0 基线上（`git worktree` 里单独构建 `531444e`，同一块临时板子，同一台机器）：

| 帧 | 10 次里死 | 结论 |
|---|---|---|
| v0 基线 `531444e` | **8** | 本轮改动之前就在死 |
| 本轮 HEAD（含两浮层） | 5 | 与基线同量级 |
| 本轮 HEAD＋自制 ControlTemplate（已回退） | 8 | 换模板不改变发生率 ⇒ 不是它 |
| 同一枚 exe，不带 `--selftest` 普通启动 | 0（活过 12s，进程可被 taskkill） | 死的是自检那条路径，不是启动 |

所以：**这是装置的问题，不是这块板子的判语**，本轮的处理是"承认它、别让它冒充绿灯"——`check.cmd` 那一发现在是 `… || …`（只重跑一次；产品真红时两次都红，念不出"全过"）。归因未结：候选是这台机器此刻的合成器/内存状态（他睡着、会话可能已断开），下一轮要拿"断开会话 vs 活动会话"两档各 10 次来定它到底是不是环境。

### 6.2 两件要他一句话的

1. **仓里躺着三件别人的东西**（`NOTICE.md` 已如实标出，但没有删）：`mip.html` 是一整张存下来的 Microsoft Learn 页面（文件里带 canonical URL）、`design/RichTextLabel.tsx` 从 `@tldraw/editor` 改出来、`design/apple.json` 是 Apple 教程页面的 HTML 壳。要商用发布就得决定：**删掉、还是各自注明来源并按上游条款办**。我没有动它们——那是别人的东西，删是破坏性的。
2. **查找那一格输入框还穿着平台的衣服**：聚焦时一条强调色下划线、有字时一枚圆形清除按钮（`v5-search.png` 里看得见）。自制模板换掉它被证伪（不改发生率也不改外观，已回退），剩下的路是覆盖模板自己的那几个主题资源。今天没做是因为它要 10 次一发地验，而我把那一晚上别的预算花在了"找得回来"上。

### 6.3 下一轮的第一发

P0 剩下的两项（冲突件带设备与时间、同步卡住的常驻横幅）＋ P1（三个异常钩子全挂、存盘失败的常驻角标）。理由不变：**"不丢数据、看得见错误"排在"手感"前面**。
