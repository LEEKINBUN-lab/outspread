# 铺陈

一块无限画板：随手把文字和图片贴上去，自由挪动和缩放。板子存成 **JSON Canvas 1.0** 的 `.canvas` 文本文件，人和 agent 同时改同一份文件。

## 跑起来

```
dotnet build src\Outspread.Board\Outspread.Board.csproj -c Debug -p:Platform=x64
run-board.cmd            （或 src\Outspread.Board\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Outspread.Board.exe）
```

要 `Microsoft.WindowsAppSDK 2.5.1`（NuGet 缓存里已有）和 **Windows App Runtime 2.5.1**（已装则免安装直接跑；缺了会弹"缺少组件"）。工程是无包（unpackaged）配置：不打 MSIX，双击 exe 就能开。

一次全验（构建 + 353 项核心测试 + 界面自检；自检跑在 %TEMP% 的副本上，绝不动 boards 里的板子）：

```
check.cmd
```

## 手上的动作

| 动作 | 结果 |
|---|---|
| 拖动空白处 | 框选（Shift／Ctrl＋拖＝加选） |
| 右键拖动 · 空格＋拖动 | 平移（松手带惯性＝待做，见复刻契约 S4） |
| 右键（没拖动的一按） | 颜色 6 档／无色、叠放、删掉、回到 100% |
| 滚轮 | 以指针为不动点缩放（5%–800%） |
| 点一格 / Ctrl＋点 | 选中 / 加选减选 |
| 拖一格 | 移动（8 像素内自动吸附边与中线，画参考线） |
| 端角那 8 个方块 | 缩放；多选时缩放整组 |
| 双击空白 / 双击一格 | 新建并进入编辑 / 改这格文字 |
| Ctrl＋V | 贴上剪贴板里的文字、图或文件 |
| 从资源管理器拖入 | 图片进 `assets/`，别的按链接放 |
| Delete | 删掉选中的（可撤销） |
| 选中 2 格以上按 Alt＋C | 连成一条链（按叠放次序串）；再按一次拆掉。箭头画在纸底下，末端按切线收口 |
| 鼠标移到卡片左下角那排牌子上 | 长出 ×，点一下＝把这条日期／这个标签从正文里拿掉（可撤销）。牌子分两种：**日期＝一个状态点＋文字，没有盒子**；**标签＝无描边胶囊**（淡底一档）。为什么不是方框，调研与来源在 **[docs/research-chrome-and-properties.md](docs/research-chrome-and-properties.md)** §二·6 |
| Ctrl＋Z / Ctrl＋Y | 撤销 / 重做（包括"并入外部改动"这一步） |
| Ctrl＋0 · 右下角 `− 100% +` | 缩放；点中间那个比例＝回 100% |
| 右键菜单里的"筛选：日期与标签…" | 打开筛选面板（原来那枚漏斗按钮按他的意思删了）。筛上了以后板顶才出现一条窄横幅（「只看 #周会 · 15 格中的 1 格 · 点这清除」），点横幅＝清除；平时板子上不常驻任何东西 |
| 按住 Tab | 键位清单，松手就收起（点它空白处也收起） |
| Esc | 先收眼前那层浮层，再清选中 |

编辑文字时：`Ctrl＋Enter` 或点到别处提交，`Esc` 放弃。

给一格标日期或标签：**正文里单独占一行**写 `@d2026-10-20 @t周会`（只有这一种写法；`#` 已经让给 markdown）。卡片上这行字不出现，换成左缘一条色条和底下一行读数（`逾期 10-20 · #周会`）。为什么不是字段、行内为什么不算，都写在 **[docs/canvas-format.md](docs/canvas-format.md)**。

## agent 怎么读这块板

格式与约定在 **[docs/canvas-format.md](docs/canvas-format.md)**。命令行：

```
outspread list / show ID / add / patch ID / rm ID / validate / shot
outspread sync link / status / once / forget
dotnet build src\Outspread.Cli\outspread.csproj -c Debug
src\Outspread.Cli\bin\Debug\net10.0\outspread.exe list
```

板子按 `--board` > `TANKAI_BOARD` > 向上找 `boards/scratch.canvas` > 本目录第一个 `.canvas` 定位；都找不到时落到 `%LOCALAPPDATA%\Outspread\boards\scratch.canvas`。

界面开着时，外面改了文件会在 0.4 秒内并进来（只并入对方改的那些格，不覆盖你正在拖的那格），并且这一步可以撤销。`outspread shot` 向**运行中的界面**要一张 PNG（`--mode view|fit|node`，可带 `--select 节点,节点` 把选中态画进去、`--panel filter|cheat` 把那两张浮层打开来拍，拍完都恢复原样）；没有开着的界面就明确报错，不会自己另画一份。

## 手机 ↔ 电脑同步

板子的**跨端副本**放一个私有仓库（现在：`LEEKINBUN-lab/tankan-boards`），只用平台的"按路径读写单文件"HTTPS API 搬——**不装 git、不装同步软件、不买服务器**。`.canvas` 本体仍是纯 JSON Canvas 1.0，同步元数据一律在外面：远端多一个 `boards/.sync/<名字>.manifest.json`，每台设备自己在 `%LOCALAPPDATA%\Outspread\sync\` 存**上次大家一致的祖先全文**。

为什么要祖先：格式的规范里**没有任何版本／时间戳字段**，两份文件放在一起没法从内容判"谁新"。所以"谁新"靠远端文件的一次性令牌（写的时候带上你读到的那个，被人抢先就失败重和），"怎么合"按 `id` 逐格逐字段判。

```
outspread sync link --remote owner/仓库名 --token-stdin   （令牌进 Windows 凭据管理器，不进仓、不进文件）
outspread sync status [--json]     读数：rev / 远端令牌 / 待发几块 / 上次为什么没成 / 冲突副本
outspread sync once                拉—合—推一轮（界面的触发点：存盘后、回前台、每 120 秒）
outspread sync pair                把手机要的配对载荷画成二维码，只出现一次；窗口太窄会明说而不是画半张
```

图片**只搬被某一格引用的**：`assets/` 里没人指的孤儿图不上云（那是 `AssetStore` 对孤儿的一贯态度——报告，不删）；单张超过 1 MiB 的会被点名拒发，其余照走；板子引用着而两头都没有的图，会被说成"那一格会空着"。空转一轮实测**只发 1 个请求**（一发树，稳态不读任何文件），120 秒轮询占免费限额 5000/小时的 0.60%。

三条要记住的行为：**首轮两端取并集**（没祖先时删除无法判定，所以谁都不清空）；**内容双改不隐形取胜**（远端那半留在原格，本地那半另起一张落在原格下方，并在日志里留一条判决）；**挤不进去就一个字节都不动**（三轮都被挡 ⇒ 本地保持原样，本想上传的那份整份写在旁边的 `<板子名>.canvas.conflict-<设备名>-<yyyyMMdd>-<HHmm>-r<N>`）。

契约、真机读数（含平台返回码的确切形状）和"哪些格子没测到"在 **[docs/sync-contract.md](docs/sync-contract.md)**——安卓端要照那一页复刻，裁决规则两份实现必须逐字一致。

## 日志

失败原因默认写到 `%LOCALAPPDATA%\Outspread\board.log`（`TANKAI_DIAGNOSTICS=<路径>` 改位置，`=0` 关掉）。屏上不显示任何自证性文字。

## 还没做

样式与手感的**完整差距清单、分阶段顺序和每阶段的验收口径**在 **[docs/port-parity.md](docs/port-parity.md)**——那一页是判据，没打勾的不算完成。
功能与操作（快捷键、命令、创建→编辑→落盘这一圈）按**状态机**组织，判据在 **[docs/interaction-machine.md](docs/interaction-machine.md)**：谁持键盘、谁能改文档、不合法的命令怎么被明着拒绝。
一格便签自己的属性怎么活（内容／颜色／标记／归属／关系／稳定性六条轴，加"第一次用它的人卡在哪"）在 **[docs/note-lifecycle.md](docs/note-lifecycle.md)**。
浮层样式、markdown、标签与日期放在哪、左下角那排要不要常驻——**先调研后判决**，在 **[docs/research-chrome-and-properties.md](docs/research-chrome-and-properties.md)**（含一手来源与"没查到什么"）。

其余：旋转（格式里没有）、多板切换与文件关联、便签字号调节。同步这边还没做的：`assets/` 里的图片随板子走（现在只搬 `.canvas`，图留在各自机器上）、跨端**删除**的墓碑（首轮之后能判"另一端删了"，但设备离线三天期间的删除仍需墓碑才可靠）、第二台设备上的手机端实现（按 docs/sync-contract.md 复刻）。
