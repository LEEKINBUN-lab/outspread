# 多端同步的读法与写法

给 agent 和**第二个实现（安卓端）**看的契约。这里每一条都对应代码里的行为和一条测试名，不是愿望。

对应文件：`src/Outspread.Core/RemoteFiles.cs`、`Merge3.cs`、`SyncLedger.cs`、`SyncEngine.cs`、`AssetSync.cs`。
测试：`tests/Outspread.Core.Tests/{Merge3Tests.cs, SyncEngineTests.cs, AssetSyncTests.cs, FakeRemote.cs}`（同步这三份里 13＋25＋16 条声明用例；`dotnet test` 整套跑起来 **427 枚全绿**，`Theory` 展开后比声明数多。全部不联网、不花钱）。

## 为什么必须有这份东西

JSON Canvas 1.0 规范里**没有任何版本、时间戳、revision 字段**（规范 `jsoncanvas.org/spec/1.0`；求加 `version` 的 issue #8/#41/#57 至今 open 或被关闭）。所以两份 `.canvas` 放在一起，**文件自身无法回答"谁新"**。Obsidian Sync 官方对白板类文件的态度就是 last-modified-wins（只给 Markdown 做 diff 合并）。

本项目的答法：把"谁新"这件事交给**每个设备自己保存的祖先**（ancestor）＋**远端文件的一次性令牌**（token）。`.canvas` 本体一个字段都不加，仍然是纯 1.0，Obsidian 和 agent 照旧能读。

## 仓库里放什么

| 路径 | 是什么 | 规矩 |
|---|---|---|
| `boards/<名字>.canvas` | 板子本体 | **纯 JSON Canvas 1.0**，同步元数据一律不许写进来 |
| `boards/.sync/<名字>.manifest.json` | 这块板的账本条目：`rev`、`contentSha`、`by`、`clock`、**`droppedAssets`**（不再引用的图，旧在前，上限 64） | 一块板一份，别的板不许替它回答时钟 |
| `boards/.sync/renames.json` | 改名标记：`{from, to, by, at}` 一列，旧在前，上限 64 | 一块板改了名要**跨端跟着改**，不是"多了一块板"。规矩全在下面「改名怎么跨端跟上」那一节；**两端解析必须逐字一致**，凭据是 `golden/renames/vectors.json`（两端各自回放同一批用例） |
| `boards/assets/img_<12位hash>.*` | 图片 | **搬运与删除都已做**（见「图片怎么跟着走」）。只搬被节点 `file` 引用的；文件名是内容寻址，所以同一张图重传是同一个路径 ⇒ 不产生副本，**但推送走的是"创建"语义：撞名即拒发、绝不覆盖**。删除走这份账本里的 `droppedAssets`，**永不对活着的引用开火** |

令牌由远端主机给（GitHub Contents API 给的是 git blob `sha`，Gitee v5 也叫 `sha`）。**引擎只比较它、绝不解析它**，所以换主机不用改合并逻辑。测试里的假令牌是内容 sha256，真实主机是不透明串，两者引擎都必须当黑箱。

## 每台设备自己存什么（不进仓库）

`%LOCALAPPDATA%\Outspread\sync\{板名小写}\state.json` ＋ `base-<12位hash>.canvas`。
**分文件夹的键是板名，不是设备名**（`SyncLedger.LedgerFolder` → `Path.GetFileNameWithoutExtension(...).ToLowerInvariant()`）：一台机器上两块板各有各的祖先，测试 `Two_boards_can_share_one_folder_without_naming_each_other` 钉的就是这件事。

`base-*.canvas` 是**祖先的全文**，不是哈希。少了它三方合并就退化成"谁后写谁整份赢"。

`state.json` 的字段（`SyncLedger.cs` 里 `SyncState`）：`schema` / `deviceId` / `clock` / `docToken` / `manifestToken` / `rev` / `contentSha` / `baseFile` / `publishedSha` / `lastError` / `lastErrorKind` / **`pendingDeletions`**。

**`pendingDeletions` 是"这一台自己没删成的图"**：仓里那份账本已经记着这次删除（别的设备读得到），但"远端不动我就读不到账本"的那一发空转轮次需要另一处地方接着这件事。所以它**只在本机的账本里**，不进仓、不进契约的比对。测试 `A_deletion_the_far_side_refused_is_asked_again_by_a_round_with_nothing_else_to_do`。

**`deviceId` 一旦生成就不能改**：几何字段打架时它参与决胜（`Merge3.cs:424` `Wins`），改了等于把历史合并结果变成不可复现。

**本机改名怎么跨端跟上：见下面「改名怎么跨端跟上」一节。**（10-22 那一版写的是"不动远端、新名当另一块板"，2026-10-23 他否掉了这条，改成协议级跟随；旧表述作废，不再维护。）

**关掉一块板只删本机（2026-10-22 第五轮）**：关标签删本机 `.canvas`、`.view.json` 和这块板的历史快照，命名板也一样；**不发远端删除请求**。同步账本保留（它是同名板下一次同步时的祖先，能把这次删除推过去），远端仓库的板和 manifest 原样留着。⇒ 若之后建立同名板并同步：远端没改过则删除胜出、远端被清空；远端这期间改过则改动胜出（会复活）。没有跨端墓碑，这一版不删远端。

**所有同步用的 JSON 一律 LF**：`Ledger.Encode` 过一个 `JsonText.NormalizeNewLines` 才成字节，`state.json`／`remote.json`／仓里的 `<名>.manifest.json`／`outspread.sync.json` 都走它。原因不是好看——`.NET` 的缩进写器给的是平台行尾，而 `manifest.json` 是**进仓、两端都读**的文件，手机端读 LF：同一份内容在两台上算出两个摘要就是错的。测试 `The_ledger_entry_a_phone_reads_is_line_ending_free`；真机复验：`remote.json`/`state.json` 重写后 CR 计数 0。`base-*.canvas` 的**文件名哈希与落盘字节同源**（`KeepBase` 先 `Encode` 再写，名字声称是这份的哈希，就得真是这份的哈希）。

## 改名怎么跨端跟上（2026-10-23，B 档）

一块板改了名，同伴设备**不许**把它读成"远端把这块板删了"——今天两端都会这么读，而且后果比"少一块板"更糟：仓里那份不见了、又没有标记可查时，合并会把"另一端删了每一格"执行到本地，**这块板被清空**（安卓 `SyncEngine.kt:858-872`；桌面同一条由测试 `A_vanished_board_with_no_marker_still_takes_the_old_road_and_it_empties_the_board` 钉着，它今天答 `Pulled` ＋ 本地零格）。改名是低频动作，但低频不等于可以丢内容，所以这一条进契约。

仓里那份 `boards/.sync/renames.json`：

```json
{ "schema": 1, "entries": [ { "from": "周会", "to": "项目周报", "by": "ENMUSUBI", "at": "2026-10-23T21:40:00Z" } ] }
```

`from`/`to` 存的是**板名（文件名去掉 `.canvas`）**，不是路径——路径由 `SyncPaths.ForBoard` 从名字派生，存两遍就是第二个真相。`entries` 旧在前、上限 64（与 `droppedAssets` 同一档同一口味）。

**十条规矩（两份实现必须逐字一致）**

1. **只在"我这块板的远端文件不见了"那一格才查这份标记。** 空转轮次不多问一个文件——与"图片墓碑没残留时不为它多问一次 tree"同一条。
2. **跟的是链，不是单跳**：从我的板名出发反复找 `from == 当前名` 的条目，最多 8 步；成环、或走到尽头那个名的远端文件仍不在仓里 ⇒ **不跟**，退回已知的删除语义。宁可保守，也不把一块板改到一个不存在的名字上。
3. **同一个 `from` 有多条（两端同时改名）**：取 `at` 最大的一条，平手取 `by` 的 Ordinal 大者——**两端必须算出同一条**，所以这条比较写进向量集，不留在注释里。`at` 与 `by` 都平手（同一台设备反悔重发）取**列在后面的那条**：同一份字节、同一个顺序读，两端才会同答（实现里那把比较是 `>=` 而不是 `>`，就是为了这一格）。
4. **跟的人先搬自己的盘，再走正常一轮**：`.canvas`、`.canvas.view.json`、账本文件夹 `<旧名小写>` → `<新名小写>` 一起搬；`state.json` 里 `docToken`/`manifestToken` 清成 null（那两个令牌属于旧路径，留着会让下一发空转误判"还是我上次写的"），`base-*` 祖先、`publishedSha`、`rev` 原样带过去。于是合并**仍然有祖先**，不会退化成"首轮取并集"（那正是 10-22 版"多了一块板"的成因）。
5. **改名的人按 ①→④ 发**：① 新名 `.canvas`；② 新名 manifest（把旧名那份的 `droppedAssets` 原样带过去，墓碑不许在改名路上丢）；③ 追加这条标记；④ 删旧名 `.canvas` 与旧名 manifest（各带 sha 条件，条件不过就不删）。**③ 必须在 ④ 之前**：同伴只要看到旧文件没了，就必须能同时看到标记；顺序倒了就把"改名"变成一次"删除"，而删除在契约里是会被执行的（`A_deletion_the_other_end_left_alone_deletes`）。
   **④ 的落地闸已经过了（2026-10-24 00:09 真机读数）**：落地次序是"两端先发标记（不删）→ 两端都会跟（还不删）→ 才开 ④"，前两步都已进仓跑绿，最后一步两端都接上了。④ 的凭据是**改名当时**旧路径那两个令牌（存在本机账本的 `pendingRenameDocToken`/`pendingRenameManifestToken`，第 8 条同一格理由：不进仓）——删的时候拿它们做条件，**条件不过就不删**：旧名字被人占走 ⇒ 冲突 ⇒ 那份留着、待发照留、下一轮再补。读不懂标记时不覆盖（第 3 条）；③ 没落定之前不做 ④（顺序反了就把"改名"变成一次会被执行的"删除"）。

6. **①②没成功就不做③④**：本机改名**已经**成功了（文件都搬了），远端那一发失败不许回滚本机，也不许留半截标记 ⇒ 回执说"远端还没跟上（原因）"，重试的凭据存在本机账本的 `pendingRename`（见第 8 条），下一轮开头先补完再干别的。
7. **从未上过仓的板子不留标记**：这块板没有 `publishedSha`（从没成功发过）⇒ 仓里根本没有旧名那份，标记是纯噪音。
8. **`pendingRename` 只在本机账本里**（`{from,to}` 或 null），不进仓、不进契约比对——与 `pendingDeletions` 同一格理由：仓里的账本要靠"远端还活着"才读得到，而这两件事恰恰在远端不动时才需要接着做。
9. **标记不许用来复活一块已经关掉的板**：第 4 条只在"这块板正开着、且它的远端文件不见了"时执行。本地没有这块板的设备，这份文件对它是空气（它本来也不同步这块板）。
10. **LF 照旧**：`renames.json` 是进仓、两端都读的文件 ⇒ 走 `JsonText.NormalizeNewLines`（同 `The_ledger_entry_a_phone_reads_is_line_ending_free` 那条的适用面）。
11. **"仓里这个路径没有文件"永远不等于"远端把每一格都删了"**（2026-10-23 真机排练之后补的，两端同一条）。以前两端都是拿空的远端去合并：本地每一格都被判成"另一端删了"，于是**清空本地再推上去**——改名以后账本带着祖先来到新路径，第一发就踩中（排练乙 1 格 → 0 格）。现在的判据：远端这个路径没有文件 ⇒ 远端对这条路**没意见** ⇒ 按"首轮没有祖先"那一条走，本地有的格子算本地新增，这一发是 `Pushed`。**推论**：远端整块板的删除从此只能由显式的删除请求产生（④ 那种），不来自"文件不见了"；同伴要清空一块板，得自己写那份板的内容，不能靠删文件。锁：桌面 `A_renamed_board_uploads_what_it_holds_instead_of_taking_the_empty_far_down`、`A_vanished_board_with_no_marker_uploads_what_this_device_holds`（把 `farMissing` 那道闸摘掉，这两条当场红）。

**已知代价，写明白**：第 3 条决胜输掉的那次改名，会在仓里留下一块没人认领的 `boards/<那个名>.canvas`（它当时已经发上去了）。它不挡任何事，能靠"打开盘上的板子"捞回来；不做自动清理，因为"删一份别人正在用的板子"比留一份孤儿危险。

**凭据**：`golden/renames/vectors.json` 两端各自回放（链、成环、同 `from` 两条、`at` 平手比 `by`、目标不在仓、上限挤出）；两端各自的引擎用例覆盖发布顺序与"标记没写进去不挡本机改名"；最后才是两设备真机排练。

**落地状态（2026-10-24）**：
- 契约、共享向量（两个仓各一份同字节、各钉同一枚 sha256）、两端解析器与各自的回放测试：已进仓跑绿。
- **桌面**：第 4 条（搬账本、清两令牌、留待发）与第 5 条的 ①②③④ 都接上了，跟随那半边也接上了——引擎拦下"远端那份不见了"那一发（`SyncOutcome.FollowedRename`，本地一字节不动），宿主把文件、sidecar、账本、标签一起搬，搬完立刻按新名字再走一轮。读数：核心 8 条（`SyncRenameTests`）＋界面自检一条（`selftest 跟随改名`：搬对／本机同名板不搬／正在改名不搬）。
- **安卓**：跟随也接上了（2026-10-23 第二轮）。引擎那半边早就在报 `FollowedRename`；这一轮补的是**会话层那半边**（`ui/BoardScreen.kt` 的 `followRemoteRename`）：先压住旧屏的 onDispose 写盘 → `BoardRegistry.rename` 把 `.canvas`、`.canvas.view.json` 与账本一起搬 → 整屏按新名字重建 → 下一次轮询（最长 8 秒）走"正常一轮"。三道不搬的闸与桌面同一档：正在写字／手指还按着／板子列表在屏上。读数：`AutoPace.isClean` 认 `FollowedRename`（否则跟完一次改名白白退避到 16→120 秒）；app 层一条把"报信→搬盘→再一轮"整条拼起来跑（`BoardSyncTest.a_board_the_peer_renamed_is_moved_and_uploaded_under_the_new_name`）；第 11 条在安卓侧补了两条引擎用例（`A_renamed_board_uploads_what_it_holds_instead_of_taking_the_empty_far_down`、`A_vanished_board_with_no_marker_uploads_what_this_device_holds`——把 `farMissing` 那道闸摘掉，这两条当场红，摘闸变异实测过）。
- **两设备真机排练（2026-10-23 23:33，第一次跑通"同伴跟改名"）**：桌面把 `rehearsal-c` 改名成 `rehearsal-d` → 仓里长出 ①②③（新名那份＋manifest＋标记 `by=enmusubi-02c2dd`）→ 手工补 ④（删掉旧名那份，因为 ④ 在代码里还关着闸）→ 手机 8 秒那一发读到 `这块板被改了名，本地未动`，日志 `跟随改名: rehearsal-c → rehearsal-d`，屏上那枚板名胶囊变成 `rehearsal-d`、两格还在（截图 `rehearsal-d`＋"同伴改名之前加的"）→ 下一轮手机按新名字走完，仓里两格的祖先没丢。**读数**：手机 `files/boards/` 只剩 `rehearsal-d.canvas`（旧名那份与它的 sidecar、账本目录都没了），`files/Outspread/sync/` 里只有 `rehearsal-d`。排练件与标记已从仓里撤走（回到 4 条路径），两端本地账本与手机上的排练板都已删净。
- **④ 已开（2026-10-24，他点头）**：两端改名那一台在标记落进仓之后，按**改名当时**的旧令牌删掉 `boards/<旧名>.canvas` 与 `boards/.sync/<旧名>.manifest.json`；条件不过就不删、待发留着下一轮只补删（不重写第二份标记）。读数：桌面 4 条（`A_rename_reaps_the_old_copy_once_the_marker_is_in`、`A_reap_the_far_side_refused_asks_again_without_writing_the_marker_twice`、`A_reap_never_deletes_a_file_the_far_side_moved_under_us`、`Following_a_rename_moves_the_ledger_without_owing_a_marker`）＋安卓同名三条。三发摘闸变异各自实测过：不做 ④ ⇒ 三条红；删的时候现读令牌 ⇒ "别人占走旧名字"那条红；不认"已经在仓里" ⇒ 补删那一发多写一遍标记（`writeCalls`）红。
- **跟的人已经闭嘴**：跟着别人标记搬的那一台 `publishMarker=false`——账本照搬、祖先照带、两个令牌照清，但**不欠仓里一条标记**，也不去删同伴那份。真机复验（2026-10-24 00:09）：桌面 `rehearsal-e → rehearsal-f`，日志三行 `改名标记已发到远端` → `仓里旧名那份清了：boards/rehearsal-e.canvas` → `…rehearsal-e.manifest.json`；手机 00:09:41 `跟随改名: rehearsal-e → rehearsal-f`、00:09:43 按新名字走完，**仓里那条标记始终只有一条、`by` 还是改名那台**。
- **排练操作的一条坑（两次都踩了）**：撤排练件时必须**先把还在同步的那一端停掉再删仓**。反过来做，删除会被对方的轮询当成"远端没了、我本地有"⇒ 当场原样推回来（第 11 条的正常行为），我因此在仓里多删了两遍。

## 一轮同步干什么（`SyncEngine.cs`）

1. **一发 tree 请求**拿回全部 path→token。
2. 若 `docToken`、`manifestToken` 都还是我上次写的，且本地文件哈希 == `publishedSha` ⇒ `NothingToDo`，**一个文件都没读**（`SyncEngine.RoundAsync` 的空转返回；测试 `Nothing_moving_costs_one_request_and_not_one_file_read`：断言 `ReadCalls == 0`）。
3. 否则取远端 `.canvas`，与本地、祖先三份做 `Merge3`。
4. **上传的是整份合并结果，不是补丁**：`PUT boards/<名>.canvas` 带 `merged.ToJson()` 的全部字节（`SyncEngine.PushAsync`）。`DocDiff.Between(远端, 合并结果)` 只用来判断"这一轮有没有东西要发"（`delta.IsEmpty`），不当作线上格式。
   安卓定档里那句"`BoardSyncTransport` 必须交 `DocPatch` 而非整份文档"约束的是**收端怎么写自己的盘**：收到远端状态后必须 `DocDiff.Between(祖先, 收到)` 再 `PatchApplier.Apply` 到本地副本，**不许拿远端那份直接覆盖本地文件**。桌面端就是这么实现的——写完之前比对，比对不过就不写（`SyncEngine.PublishIfUnchanged`，在 `PushAsync` / `PullOnlyAsync` 末尾各调一次），所以"整份文件"是载体、"补丁"才是落地方式。**下一个实现者不要以为线上要传 delta。**
5. `PUT` 带 `expectToken`。被挡回 ⇒ 重读远端、用**同一个祖先**重新合并、再发。上限 `SyncEngine.MaxRounds = 3`。
6. 三轮都挤不进 ⇒ `GaveUpConflict`：**本地一个字不改**，把本想上传的那份整份写到板子旁边的 `<板子文件名>.canvas.conflict-<设备名>-<yyyyMMdd>-<HHmm>-r<N>`（例如 `scratch.canvas.conflict-ENMUSUBI-20261012-1531-r2`，`SyncEngine.cs` 的 `ConflictCopyPath`）。**故意不叫 `.canvas` 结尾**：`BoardLocator` 有一条"取目录里 Ordinal 最小的 `*.canvas`"的兜底，冲突副本要是被当成板子打开，一次同步失败就变成了一块新板子。人工动作只有三个：重试 / 以本地为准 / 以远端为准。

## 合并裁决（`Merge3.cs`）——两份实现必须逐字一致

远端那份是**载体**（`merged` 从远端深克隆起步），本地改动以字段级补丁并进去。这样上线的差异最小，也保证两端从同样三个输入得到同样的板子。

| 情形 | 裁决 | 代码 | 测试 |
|---|---|---|---|
| 只有一端碰过 | 直接并，不记账 | `Merge3.cs:245` 前后 | `One_field_changed_by_only_one_end_arrives_without_a_verdict` |
| **内容字段**（`text`/`url`/`file`/`color`/`label`，以及一切非几何字段）双改 | 远端值留在原格子；本地值另起一张便签，落在原格子正下方 `y + height + 40`（`ConflictDropOffset`，`Merge3.cs:78`），**不隐形取胜** | `Merge3.cs:319` | `Two_ends_writing_different_words_to_one_note_keep_both_sets_of_words` |
| **几何字段**（`x`/`y`/`width`/`height`/`zIndex`，`PositionFields`，`Merge3.cs:72`）双改 | `clock` 大者赢；平手比 `deviceId` 的 Ordinal 大者 | `Merge3.cs:282`、`:424` | `Two_ends_moving_one_note_settle_on_the_higher_clock`、`Equal_device_names_still_reach_the_same_answer` |
| 一端删、另一端改 | **改动胜出**（删除不执行），记 `Revived` | `Merge3.cs:123`、`:245` | `An_edit_beats_a_deletion_the_other_end_never_saw` |
| 另一端没碰的删除 | 执行删除，不记账 | — | `A_deletion_the_other_end_left_alone_deletes` |
| 同 `id` 双加、内容全等 | 合成一张，记 `Deduplicated` | `Merge3.cs:172` | `Two_ends_adding_the_same_note_by_accident_stay_one_note` |
| 同 `id` 双加、内容不同 | **远端保号**，本地这份换发 `u`+12hex；只有"本轮我们自己带进来的节点"里的 `group` 引用跟着改号，**远端节点的引用一动不动**（它指的正是保号那张） | `Merge3.cs:189` | `Two_ends_minting_the_same_id_for_different_notes_rename_ours`、`A_group_renamed_here_moves_our_children_and_leaves_theirs_pointing_at_the_note_that_kept_the_id` |
| 两端都重排 z-order | 取远端顺序，记 `OrderFromRemote` | `Merge3.cs:343` | `Two_ends_restacking_the_board_keep_the_fars_order_and_say_so` |
| **`edges` 及任何顶层键**两端不一致 | **补丁模型只看 `nodes`，看不见 `edges`**（`DocPatch.cs:202-247` 全程只遍历 `document.Nodes`）⇒ 取远端，但必须记 `CarriedFromRemote` 并让 `HasConflicts` 为真，**不许静默** | `Merge3.cs:381` | `Lines_the_two_ends_disagree_about_are_reported_because_node_ids_cannot_merge_them` |

## 首轮没有祖先时

`state.baseFile` 为空 ⇒ 祖先取**空文档**（`SyncEngine.RoundAsync` 里的 `ancestor ?? CanvasDocument.CreateNew()`）。于是首轮是**并集**，任何一端的节点都不会被当成"另一端删掉的"。

这是硬规矩：首轮若拿远端当祖先，本地缺的每个远端节点都会被 `DocDiff` 报成本地的删除，第一次同步就清空对面。

**代价说清楚**：首轮分不出"删除"，第二轮起才有删除语义。**节点**的删除至今没有墓碑（`boards/.sync/` 里那份只记图片），所以"手机在第一次同步之前删掉的一块便签"这格仍然答不了。图片的删除从这一版起有墓碑，见「图片怎么跟着走」。

### 新设备带着一份出厂种子板来配对（安卓 `40c3c86` 定的规矩）

首轮是并集这条规则有个它自己没料到的用法：**一台新设备出厂自带一块演示板**，而仓里那块板是这个人真正在写的内容。两边用的是同一批演示 `id`（`u0a1b2c3d4e5f`、`u111111111111`…），内容却不同 ⇒ 并集 = 7 条 `Renumbered` + 7 对叠在一起的卡片，而且这份结果会被当成 r3 推上仓。**合并没算错，错在"新设备带一份演示板"这件事没有规则。**

定档：**没有祖先 + 远端非空 + 本地逐字节仍是出厂种子 ⇒ 远端为正本，取下来而不是并上去。**

- 引擎**不自己判断种子**。它多一个注入判据 `isFactorySeed(text): Boolean`，**默认 `{ false }`**：没有出厂板的端（桌面就是）行为一字不变。这条规则不许变成"引擎猜这块板像是模板"。
- 命中时走已有的 `pullOnly` 那条路，因此"比对后才发布 + 记下祖先 + 取令牌"全套纪律照旧，报 `Pulled` 与一句人话，**零条裁决**。
- 什么算"出厂种子"**由每个端自己回答，引擎只问不发**。安卓那侧重读 APK 资产比摘要（安装与首次配对可能隔很久，不许记启动时的内存值）；桌面 `CreateNew()` 建的新板是空的，返回 `false` 就是正确行为，**不要在桌面代码里加种子判定**。
- 它治不了历史：规则生效之前已经推上去的那份重叠，只能人工清（删掉多出来的那几张再同步）。

**顺带一条 id 纪律**：手写的板子（出厂种子、探针件、fixture）里那些写死的 `id` 是**任何两份手写板都会撞**的东西——`a5a5a5a5a5a5a` 这一枚就在安卓种子板（一张 file 卡）和桌面原来那块 `scratch.canvas`（`assets/probe-chart.png` 那张探针图）里各用了一次，撞出来的结果就是一条 `Renumbered`。新写的手写板请带用途前缀（`useed…` / `uprobe…`）。桌面那一枚 10-23 随 `scratch.canvas` 一起删了；现在用前缀的手写板是 `boards/快速入门.canvas`（`u5eed…`）与 `tests/fixtures/gate.canvas`。

## 安卓端必须照抄/必须注意的

1. `Global\` 具名 Mutex **只覆盖同一台设备**（安卓那边已换成进程内锁 + `FileChannel` 建议锁）。跨端冲突的答案是 token 的 CAS，不是锁。
2. 收到远端状态 ⇒ `DocDiff.Between(祖先, 远端)` ⇒ `PatchApplier.Apply` 到本地副本。**不许整份覆盖本地文件。**
3. `PatchApplier` 对"祖先里没这个 id"是**静默跳过**（`DocPatch.cs:107-118` `MissingNode`）。"改 vs 删"必须在合并层预先判掉，不能指望它回报。
4. `expected.*` 由生成器产出、人手不写：合并裁决的用例将来要从这份表里生成，两端跑同一份向量。
   **两把尺不许互当对方**：那份语料是**跨端裁判**（方向是单向的——桌面发、手机复现，它证"手机没自己发明序列化"，证不了"桌面不会改坏自己的发射器"）；桌面侧另有**四枚单端回归钉**（`CanvasDocumentTests.The_bytes_this_emitter_writes_are_frozen`：中文与 `<&"'` 与 `U+2028` 那份 256 B、含组那份 375 B、含边那份 470 B、`40.0` 那份 178 B，全部 LF、末尾带一个 `\n`），它的作用是让"发射器被改坏"在**桌面这一侧当场变红**，而不是以"手机跟不上"的形状在对面报回来、归因推给对面。已验：把编码器从 `UnsafeRelaxedJsonEscaping` 换成默认档，那枚钉子红。
   三条量出来的发射器事实，两端都要遵守：**出 LF**（不是平台行尾）、**数字原样保留**（收到的 `40.0` 就发回 `40.0`，不许归一成 `40`——那样两端永远对不上字节，板子每跑一趟都"像被改过"）、**`ToJson()` 末尾一个 `\n`**。
5. `deviceId` 形状：`<机器名清洗后>-<6位hex>`，持久化后不许重铸。
6. **图片删除的三条硬规矩**：墓碑**永不对活着的引用开火**（重新引用即作废）；**回收只在板子被接受之后**，且删前重读一次 tree；删前**先问同目录其它板（远端＋本地）还引用着它吗**，读不懂就当引用着。这三条不是实现细节，是"删错一张图不可逆"的唯一屏障。
7. `isFactorySeed` 是**注入的判据，默认 `{ false }`**：没有出厂板的端必须一字不变地走并集。不许把"这块板像是模板"做成引擎里的猜测。

**这些词两端共用，别各自另造**（`AssetReport` 的六个桶，手机 UI 直接按这六个说话）：`uploaded` 这轮传上去的、`downloaded` 这轮取回来的、`refused` **这端有但没传**（带原因：过大 / 撞名不覆盖 / 平台拒）、`absent` 板子引用着而**两头都没有**（⇒ 那一格会空着）、**`deleted` 这轮清掉的（远端那份、本地那份，或两份）**、**`held` 墓碑点名叫删、这一发没删，带原因**。另有一枚**与 `held` 并列的 `retry`**（不是 `held` 里的一个格子）：它是"这一发没删成、下一发该再问一次"的那几个，**按构造 `retry ⊆ held` 的路径集合**（每一枚 `held` 只在被记进去的同一处决定里顺手记 `retry`）。只有它进 `state.pendingDeletions`，也只有它值得冒一条提示（"另一块板还在引用它"是**在用**，不是待办，每两分钟说一次就是噪音）。报告在**每一发**上都可能带，包括空转那发和"板子没发上去"的那发——图动了而板子被拒，是要说出来的两件事。

## 图片怎么跟着走（`AssetSync.cs`）

| 规则 | 为什么 |
|---|---|
| **只搬被节点 `file` 引用的**，孤儿图不动 | `AssetStore` 的既定态度是"孤儿只报告、不删除"（agent 可能正贴到一半）。把没人引用的图上云是花别人的配额搬没人在看的东西 |
| **删除靠这块板账本里的 `droppedAssets`**（`AssetSync.Tombstones` 派生：祖先引用着、合并结果没有 ⇒ 这一发删掉了它） | `.canvas` 只能说"我用哪些"，永远说不出"我不再用了"。所以删除必须写在**能进仓、两端都读**的地方 |
| **墓碑永不对活着的引用开火**：路径被任何一份板子重新引用 ⇒ 那条墓碑作废（`Tombstones` 里就地剔掉） | 图片名是内容哈希，"把同一张图加回来"落回**同一个路径**。墓碑要是还咬人，它就会删掉板子上正显示着的那张图。测试 `A_picture_that_came_back_into_use_moots_its_own_tombstone`、`A_picture_brought_back_by_a_racing_edit_is_not_eaten_by_its_own_tombstone`（后者是"一端删卡、另一端正改那张卡"⇒ `Revived` 把图和号一起带回来） |
| **回收只在板子被接受之后**（`Pushed`/`Pulled` 两档出口），且删之前**重读一次 tree** | 被挡回的那一发绝不能已经把图删了——远端那块板可能还显示着它。重读是为了：这一发跑的过程中别人新推的一块板，可能正是还在引用这张图的那块 |
| **删之前先问"还有别的板引用它吗"**：远端同目录的每份 `*.canvas` 都读一遍，本地同目录的板子也读一遍 | 一个文件夹可以放好几块板、共用一个 `assets/`。"我这块板不用了"≠"没人用了"。**读不懂的兄弟板算"还在引用"**：宁可留一张孤儿，不能删掉别人的图 |
| **没残留时不为墓碑多问一次远端**：候选先按"tree 里还在不在／本地还在不在"筛，全都不在了就一步都不发。**反过来，确实还有得删的那一发（含空转那一发）会多一次 tree**——那一发正在干活，不是白问 | 墓碑会留在账本里直到被更新的删除挤出去（上限 64）。若照单全收，"删过一张图"会让之后**每一轮**都多花一个请求；而"远端拒删"那一发留下的残留又必须再问一次才删得掉。两头各一条测试：`A_tombstone_with_nothing_left_to_delete_costs_no_extra_request`（两台设备交替，第三发才走到这一格，`TreeCalls == 1`）、`A_deletion_the_far_side_refused_is_asked_again_by_a_round_with_nothing_else_to_do`（补刀那一发 `TreeCalls == 2`：一轮一发 ＋ 回收前一发） |
| **>1 MiB 的单张图拒发并点名** | Contents API 官方分级里 ≤1MB 才是全功能；超了宁可写明"这张没搬，因为 X 字节"，也不要一次半途上传看着像同步成功 |
| 板子引用着、**两头都没有** ⇒ 点名 `Absent` | 那一格注定空着，这句话必须有人说 |
| 落盘走"写临时名＋改名" | 和 `.canvas` 同一把规矩；进程被杀留下的半截 PNG 不该顶着内容哈希的名字存在 |
| 路径过 `AssetStore` 那道墙 | 文档里写 `../../…` 或绝对路径一律不进引用集合——同步层不许比渲染层更宽松 |

**实测（2026-10-12，你的凭据，真仓）**：本地没图而远端有 ⇒ 第一轮 `Pulled` 把 `boards/assets/probe-chart.png` 取回，字节落地；板子与图都齐之后连续空转 ⇒ **每轮 1 个请求**（只一发 tree，资产层不花配额）；冷启那轮 5 个。⇒ 8 秒轮询 = 450 发/小时，**但空转那一发带 `If-None-Match`、304 不计配额**（2026-10-23 实测：完整 GET 让 `remaining` 4980→4979，同一发条件请求返回 304 后仍是 4979）⇒ 稳态占 **0%**，只有远端真的变了的那轮才计数。

**到达方式的定档（2026-10-23，他问"能不能不一直轮询"之后拍定）**：就用**事件写＋条件轮询读**，不上推送。

- **不是"写完让 GitHub 广播"**：GitHub 的推送只有 webhook，落点必须是**我们自建的公网 HTTPS 服务**（客户端不是合法落点，建 webhook 还要仓库 admin）。手机在家外时还得在那个服务上再挂一条长连接。换来的是 6 秒→3 秒，代价是一个要养证书、处理 GitHub 重试与超时常数、并多一个公网攻击面的常驻服务。
- **不是 Actions→ntfy**：ntfy.sh 免费档 **250 条/天**、发布桶 60 再每 5 秒补 1、消息 ≤4 KB（上游文档现读）。我们的存盘节奏是"每次改动一次 commit"，正常使用就会撞穿那道顶；Actions 的排队冷启还会把延迟抖到十几秒（**未实测**）。
- **轮询为什么可以接受**：空转那发是 304、**不扣配额**；手机每天停留两小时 ≈900 发 ≈0.9 MB。真正被削掉的只剩"900 次定时唤醒"这一项，为它引入第三方不成比例。
- **明确没做的能力（不是遗漏）**：手机**熄屏/后台**被告知。现在只在板子处于前台时问，退后台整条时钟停。要做只有一条路——厂商推送（建 Firebase 项目＋APK 加消息 SDK＋WorkManager 或常驻通知），这条**等他点头**，不进本轮。
- **底线不许撤**：任何推送通道都会丢消息（进程被杀、切 Wi-Fi、hub 没起）。所以将来若加"局域网唤醒"或 FCM，**条件轮询必须留着当对账兜底**，不是替换。

**混版窗口里旧二进制会抹掉这一格（2026-10-14 实测）**：`droppedAssets` 是**加性字段**，旧端读不懂就**整份重写没了**——`SyncManifest` 没有 `JsonIgnore(WhenWritingNull)` 之类的省略条件（`SyncLedger.cs` 里 `DroppedAssets { get; set; } = []`，空表也写格），所以旧二进制写出的账本**根本没这一格**。现场读数：仓里 `boards/.sync/scratch.manifest.json` 是 `rev:7`、`at:2026-10-14T02:40:31Z`，**无 `droppedAssets`**；写它的是桌面上那个实例，它加载的 `Outspread.Core.dll` 时间戳 **10-13 19:16**，而碑层提交在 **10-14 10:28:37**。两个后果：

1. 新构建第一次写这份账本会多出一格 `"droppedAssets": []` —— **一次性的字节跳动，不是两端的尺不一致**，别当回归查。
2. **跑跨端删除那一发之前，先把旧实例关掉、用新构建重开。** 否则旧实例下一次写账本就把刚记下的墓碑抹掉，那一发白跑。**损害是有界的**：远端那张图已经由删它的那台清掉了，抹掉只让"还没应用墓碑的那一端留一份本地没人引用的文件"，而只有被引用的图才会上去，所以它不会重新出现在仓里。

**两端各自的读法**：读不到这一格时，判"这块板没清过图"，**不要**判"形状读不懂"（那是 `RemoteUnreadable` 那条停用同步的路径，代价完全不同）。

## 配对载荷的形状（`PairCommand.cs`，`outspread sync pair`）

```json
{ "v": "1", "host": "github", "owner": "…", "repo": "…", "branch": "main", "token": "…" }
```

实测整份 **192 字节**（令牌 93 字符）⇒ 画成二维码是可行的尺寸。纪律：只在被点名时出现在屏幕上，**不落盘、不进日志、不进异常文本**（`download_url` 那条教训），要看第二次得重跑。载荷里**故意不带板子清单**——清单从手机本来就要发的那发 tree 里当场读。

**渲染已接（2026-10-12）**：CLI 加了一个第三方包 `QRCoder` 1.8.0（MIT，从包内 `LICENSE.txt` 读到原文；登记在 `NOTICE.md`），它**只负责把文字变成模块网格**；网格→终端方块字的映射是第一方的 `QrTerminal`（在 Core，零依赖、有 9 条测试）。

两条只能实测的事实：

- **`QRCodeData.ModuleMatrix` 不含静区**：192 字节载荷 ECC Q 出来是 **73×73**，而 73 = 4×14+17 正好是版本 14 的边长。文档没写这一条，所以静区由 `QrTerminal` 自己补 4 圈（ISO 18004 的数），加完是 **81 列 × 33 行**。
- **终端画出来的这张图确实扫得出来**：验证方法不是人眼看，而是在**仓库外**把 `QrTerminal` 的方块字反向光栅化成 RGBA（一格字 = 4×8 像素，`█/▀/▄/空格` 四种映射），再交给独立解码器 jsQR 解 ⇒ 解回的文字与载荷**逐字节相同**（带静区与不带静区两版都过；带静区那版才是我们会用的）。

还有一条物理限制：码宽 81 列，**终端窗口不够宽会把它折行，折了就不是码**。所以 `pair` 先量 `Console.WindowWidth`，不够宽就明说"拉宽再跑"，绝不画半张；输出被重定向时宽度无从测，按"够宽"处理而不是让探测本身把命令弄崩。



| 项 | 状态 |
|---|---|
| GitHub 适配器（`GitHubContents.cs`） | **已做并已真机验证**（2026-10-12，跑在 `LEEKINBUN-lab/tankan-boards` 上） |
| CLI `outspread sync link / status --json / once / forget` | **已做**：`SyncCommand.cs` 新文件，`Program.cs` 只加了两处（分发一行 ＋ 帮助文本） |
| 界面接线（`SyncHost.cs`） | **已做**：触发点＝存盘后、外来改动并入后、回前台、每 8 秒轮询（空转发条件请求，304 复用上一次的树；失败按 16→32→64→120 秒退避，一发清的答案就回到 8 秒；`SyncHost.RememberPace`）；提示走现有的 `Say()`，**沿用他们那条判断——常态不是消息**，所以传成功时空转什么都不说，只有没传上去/挤不进/读不懂才浮一句 |
| Gitee 备胎适配器 | **没做**。限额官方无数字、`sha` 过期的错误码官方没写 ⇒ 等 GitHub 被手机侧证伪再做 |
| 手机蜂窝可达性 | 他说能连上 GitHub。**但没拿到是哪一条边**：`github.com` 与 `api.github.com` 是两条（本机桌面宽带实测就是 api 通、主站超时、raw 被重置），同步只用 api 域 ⇒ 判据应是手机上打开 `https://api.github.com/rate_limit` 出不出 JSON |
| `assets/` 图片同步与删除墓碑 | **搬运与删除都已做，且真仓打过**（2026-10-14，见「真机读数」那张表：远端 404 ＋ 本地空 ＋ 清单里记着 `droppedAssets` ＋ 空转那发不多问）。**"另一台收到墓碑后清自己那份"仍只有夹具读数**——两块同名临时板会撞同一本账，真机上打不了 |
| 私有 sidecar `*.canvas.view.json` | 已在 `.gitignore`，**永远不进同步** |
| 令牌的存放 | **已接**：他本人的 fine-grained PAT 存在 Windows 凭据管理器 `outspread.sync.github`，读写删都在真仓验过。环境变量优先认 `OUTSPREAD_SYNC_TOKEN`，旧名 `TANKAN_SYNC_TOKEN` 仍作兜底 |
| 改名留下的用户态 | **已迁移**：凭据 target（`tankan.sync.github` → 新名，**认过来但旧的没删**）与账本目录（`%LOCALAPPDATA%\TanKai\sync` → `Outspread\sync`，整目录搬走）都在 `status` / 日志里明说发生了什么；仓根说明文件也跟着改成 `outspread.sync.json`，`link` 会把远端旧名那份删掉 |
| 配对 `outspread sync pair` | **载荷已做**（192 字节，含令牌，只出现一次、不落盘不进日志）；**画成二维码没做**，卡在编码器那一步（见上） |

## 真机读数（2026-10-11，实测对象 = `LEEKINBUN-lab/tankan-boards` 私有库）

不是文档抄来的，是这台机器一发一发打出来的：

| 打的是什么 | 平台实际回什么 | 适配器怎么处理 |
|---|---|---|
| `PUT contents/<path>` 带**过期 sha** | **HTTP 409**，`message: "README.md does not match <sha>"`，**体里不给当前 sha** | `Conflict` 且 `Token=null` ⇒ 引擎必须重读再合并（`SyncEngine` 的 remeasure 分支） |
| `PUT` 更新**却不带 sha** | **HTTP 422**，`"sha" wasn't supplied` | 也归 `Conflict`：平台本身就拒绝盲目覆盖 |
| 请求体不是合法 JSON | **HTTP 400** `Problems parsing JSON` | 这一发抓出了我自己的缺陷（见下） |
| `GET contents/<不存在的路径>` | **HTTP 404**，形状与"仓不存在/我没权限"**完全相同** | 该调用**不许**用来判"远端不见了"；只有 tree 那发可以 |
| `GET git/trees/{branch}?recursive=1` | 每个 blob 给 `path`/`sha`/`size`，`size` 在场 ⇒ 体积上限可省一问 | `size > 1MB` 直接不试 |
| `contents.sha` 与同路径 tree 里的 blob `sha` | **同一个数**（`4531b523…` 两处一致） | 一发 tree 就能发出全部 per-file 令牌 |
| `GET contents` 的 `content` | base64，**每 60 字符一个换行** | `Convert.FromBase64String` 本身忽略空白，无需手工剥 |
| `Accept: application/vnd.github.raw` | 走 `api.github.com` 拿到的字节与本地 **sha256 全等** | >1MB 文件的唯一读法（`download_url` 指向 raw 域，本机被 SNI 重置） |
| 新文件的令牌位置 | 响应的 **`.content.sha`**；**`.commit.content.sha` 是 null** | 读前者；我原先按后者写，会拿到 null |

⚠ **私有库的 `download_url` 里内嵌一个可写 token**（`…README.md?token=A2H7…`，实测就在响应里）。所以 `DescribeAsync`/`SafeMessageAsync` **只取 `message` 与 `status` 两个字段**，绝不把整份响应体写进日志、状态行或异常消息。

**空转一轮的实发请求数**（用 `DelegatingHandler` 数的）：给了分支名 **1 个**；没给分支名 **2 个**（多一发查 `default_branch`）。⇒ 接线时**适配器要长期复用同一个实例并把分支名定下来**，不要每次轮询 `Create` 一个新的。按 1 发/轮：8 秒轮询＝450 请求/小时，但空转那发是 304、**不计入 5000/小时**（`GitHubContents` 的 `_treeEtag`／`_tree`）；只有远端真变了的那一轮才花配额。

**两台设备真机排练**（夹具，非真板）跑通的序列：远端清空 → PC 首传 `Pushed r1` → 手机空板首拉 `Pulled`（本地拿到 PC 的两块便签）→ 手机加一块上传 `Pushed r2` → PC 在互不知情的情况下也加一块上传 `Pushed r3`（两端各自拿到 4 块）→ 再拉一次 `Pulled` → 空转 `NothingToDo rounds=0`。删除分支也真机走过（`DELETE` 两发 `Written`）。

**这一轮真机抓出的我自己的两个缺陷**（夹具测试没能发现的，因为它们绕过了真实响应形状）：

1. 手拼 JSON 的请求体无效 —— `JsonEncodedText.Encode` 只转义**不加引号**，发出去是 `{message:…}`。改用 `JsonSerializer.Serialize`。
2. 文档写成功后我把 `manifestToken` 清了空，等于对一个**已存在的**账本文件发"创建"，被平台拒 ⇒ 出现过"板子上去了、账本没上去"的半程状态。**规矩是"本轮已读到的账本令牌不许清"**：它是另一个文件的令牌，不会因为我们写这块板而失效。今天代码里只剩两种合法置空——(a) 账本这一发确实被拒（`SyncEngine.PushAsync` 里 `state.ManifestToken = ... : null`），下一轮从 tree 重取；(b) 补记前先重读 tree 拿新令牌再试一次。二者都不会拿旧令牌硬重发。
3. （映射缺陷）形状被拒（`Rejected`）曾被报成"远端限流" ⇒ 新增 `SyncOutcome.RemoteRejected`，状态行"远端不收这份：形状或大小"；限流与拒收是两种等待，测试 `A_refusal_for_size_is_not_reported_as_a_limit` 看着这条。

**令牌这一格差点没人会发现**（2026-10-12，用他本人存进凭据管理器的那把才暴露）：`cmdkey` 把 blob 存成 **UTF-16**，按 UTF-8 解就是"每个字符中间夹一个 NUL"，组 `Authorization` 头时在框架深处抛 `FormatException`，离真正的错处十万八千里。`SyncCredentials.DecodeStored` 因此**按字节形状判编码**（偶数长度且奇数位全 0 ⇒ UTF-16LE），两条来源（凭据／`--token-stdin`）都再过一次 `Normalize` 去掉随粘贴带来的换行。测试：`A_credential_written_by_cmdkey_is_still_one_token`、`A_token_with_the_newline_that_came_with_it_is_still_one_token`。

他这把 fine-grained PAT 已真机验过读＋写＋删（`outspread sync once` 走的是凭据，不是环境变量）。

**图片删除的真机那一发（2026-10-14 11:34–11:37，探针板在 `%TEMP%`，用户当场点头授权"**会在你仓里建两个文件再删掉**"）**：

| 打了什么 | 平台上实际回什么 | 我这侧看到什么 |
|---|---|---|
| 一块带图卡的临时板 `sync once` | `已上传 r1 · 远端 3a8e12` | 报告一行 `图已上传 assets/img_reap_probe.png` |
| `GET contents/boards/assets/img_reap_probe.png`（**独立见证，不走我方代码**） | `blob_sha=22a565bf6e0c… size=70` | 远端真有那张 |
| 删掉图卡再 `sync once` | `已上传 r2 · 远端 b1f6a8 · 裁决=0` | `图已清掉 assets/img_reap_probe.png：板子不再引用它，仓里和本地那份都拿掉了`；本地 `assets/` 空 |
| 再 `GET` 同一路径 | **HTTP 404** | 远端那份真没了（不是"我们以为没了"） |
| `GET contents/boards/.sync/probe-reap.manifest.json` | `rev:2, clock:2, by:enmusubi-736350, droppedAssets:["assets/img_reap_probe.png"]` | 墓碑在**仓里**，字段序 `at` 之后，LF |
| 一个字不改再 `sync once` | `NothingToDo · rounds=0`，**没提图** | 账本里那条墓碑还在，但没残留 ⇒ 不多问一发（夹具里 `TreeCalls == 1` 那条的同形） |

探针收干净了：`boards/probe-reap.canvas` 与它的账本都 `DELETE`（commit `3155ee74` / `a2335df2`），事后 tree 里只剩 `boards/scratch.canvas`、`boards/assets/probe-chart.png` 与他自己的那块板，本地 `sync/` 下只剩 `remote.json` 与 `scratch/`。

**临时目录护栏是唯一必经点**：`SyncHost` 的"这块板不-sync"判决必须放在 `RoundAsync()` 里，不能只放 `Start()` —— 一开始只挡了 `Start()`，一次门禁跑照样给 `%TEMP%` 那份自检副本铸了个 `deviceId`（存盘后的 `NotifyChanged()` 绕过它发起了网络轮）。修后复验：带真凭据跑 `check.cmd`，远端树逐字节不变、本地不再出现 `sync/selftest/` 账本。

**门禁自身的缺陷已修**：`check.cmd` 里三行中文 `rem`（2026-10-11 18:53 那一版新增）—— 文件是 UTF-8 而 cmd 按 936 代码页解析批处理，多字节序列会吃掉行尾 CR 把下一行并进注释，被剩下那一半**当成命令执行**，于是"全过"里混着一行 `不是内部或外部命令`。今天被吃掉的是注释；换个字节对齐就可能吃掉第 49 行的自检本身而门禁照喊全过。已改成 ASCII 并留下"这几行不许写中文"的原因。



## 明确没测到的格子

- **界面开着时的同步往返**：`check.cmd` 四步全过（含 09:13:34 那次 `SELFTEST PASS`），但**自检里没有一条断言看着同步**——它跑的是没存令牌时的"一声不响"分支。"引擎写盘 → `FileWatcher` 认出自家那一发（`writtenHash`）→ 不重复合并"这一格**只有带着真令牌把界面开着跑一遍才算测到**，现在没有证据。
- **临时目录护栏只证了"不写网络"**：护栏加在 `SyncHost.RoundAsync()` 这个唯一必经点上（一开始只挡在 `Start()`，被一次门禁跑打回原形——它照样给 `%TEMP%` 那份副本铸了个 `deviceId`）。修后复验：远端树逐字节不变、本地不再出现 `sync/selftest/` 账本。
- **空仓（零提交）时 `git/trees` 到底回什么码**：没测到（排练时仓里已有 README 提交）。代码按"409 ⇒ 当作空远端、可以首次创建"处理，这一格没有实测支持。
- Gitee 备胎适配器**一行没写**：它的 `sha` 是否等价 CAS 官方无文字，本机也没测；GitHub 已真机跑通，备胎要等主通道被手机侧证伪才值得做。
- `assets/` **搬运与删除都已在真仓打过**（2026-10-14，一发一发数的，见「真机读数」最后那段：上传 → 独立见证远端有 → 删卡再同步 → 独立见证远端 404 ＋ 本地那份也没了 ＋ 清单里记着 `droppedAssets` → 再空转一发不多问）。**节点的跨端删除仍无墓碑**：`droppedAssets` 只管图片，首轮并集那格对便签仍然无解。
- **第二台设备收到墓碑后清自己本地那份**：这一格**只有夹具读数**（`The_other_end_reads_the_tombstone_and_loses_its_copy_without_one_verdict`）。真仓那一发没法用两块临时板打——`SyncLayout.LedgerFolder` 按**板名**分文件夹，两块同名板会撞同一本账，所以"另一台"在真机上要么等手机端回来，要么得改板名（那就不再是同一块板）。
- **一张图被两块板共用时删掉其中一块的引用**：`Held` 那一格（"另一块板还在引用它"）在真仓里长什么样、`AssetStore` 的孤儿报告会不会跟它打架，没测。

## 配对：谁告诉手机"同步给谁"

**仓本身就是绑定**。手机要知道的只有两样，它们的安全等级完全不同：

| 内容 | 走哪条路 | 是不是秘密 |
|---|---|---|
| `host` / `owner` / `repo` / `branch` | 二维码 | **不是**，拍到也只是知道有这么个仓 |
| 令牌 | 二维码（他要求：手机上不许粘贴，也不许建 OAuth app） | **是** ⇒ 所以安全只能压在令牌自己的形状上 |
| 有哪些板子 | **不发**。手机从它本来就要发的那一发 `git/trees?recursive=1` 里读 `boards/*.canvas` | 不是 |
| 格式版本 | 仓根 `outspread.sync.json`（一次写入，见下） | 不是 |

令牌要守三条，少一条这个设计就不成立：**仓库范围只勾这一个仓**、**权限只有 `Contents: read/write`**、**建它时给一个有限的有效期**（建议 90 天——但那是**人在 GitHub 上选的值，不是代码里的常量**：载荷没有那一位，我们也不查 token 接口，所以屏幕上、日志里、这份文档里都不许把它写成这把令牌的属性；今天写成"90 天后自己失效"，明天有人建了 30 天或永不过期的那把，这句话就是假的）。这样二维码被人拍走的最坏结果是"别人能改你的便签，直到你在 GitHub 上撤掉它，或者它自己到期"。

`二维码只出现一次`要有真机制，不能靠自觉：`outspread sync pair` 与界面里那层都**只在被点名时**把码画出来，**不写日志、不落文件、不进异常文本**（我们已经吃过一次教训：私有库的 `download_url` 里内嵌着可写令牌，所以适配器从第一版起就规定"响应体绝不进任何文案"，见上面「真机读数」）。想再看一次必须重新点一次/按重新生成。

**入口在界面里，不在终端里**：右键菜单 →「配对手机…」开一层（`PairingPanel.cs`），码按 `ModuleMatrix` **画成图形**（`QrImage.cs`，按行做游程合并的矩形，任意 DPI 都锐利），带「重新生成」「关掉」两枚动作；界面上**不显示载荷文字**——`--json` 留在 CLI，那档是给脚本和取证用的。CLI 那一发保留为"没界面也能配"的后门，不是主路。编码器（`QRCoder`，MIT）因此挂在 **CLI 与 Board 两处**，`Outspread.Core` **仍然零第三方包**（安卓那台生成 golden 语料的工具直接吃 Core.dll，不能给它添依赖）；网格→图形在 Board、网格→方块字在 Core（`QrTerminal`，有测试），两边共用同一份 `PairingPayload.Build` ⇒ **CLI 与界面画出来的是同一串字节**。

### `outspread.sync.json`（仓根，非机密，已实现：`outspread sync link` 写它）

```jsonc
{ "schema": 1, "format": "jsoncanvas-1.0", "writtenBy": "enmusubi-3acd0c", "at": "2026-10-12T01:22:00Z" }
```

它只回答"这个仓归谁管、按哪版格式说话"，**故意不列板子清单**：清单从 tree 读是当场事实，写进这个文件就会过期，而过期的清单比没有清单更坏。

## 顺手修掉的门禁缺陷（不是同步的代码）


`check.cmd` 里那三行中文 `rem`（2026-10-11 18:53 那一版新增）在这个文件上是**UTF-8 存的、cmd 按 936 代码页读**：多字节序列会把行尾的 CR 吃掉、把下一行并进注释行，被并剩下的那一半**当成命令执行**，于是"全过"里混着一行 `… 不是内部或外部命令`。今天被吃掉的是注释；换个字节对齐就可能吃掉第 49 行的自检本身，而门禁照样喊"全过"。已把那几行改成 ASCII（文件里其它 `rem` 本来就是英文），并留了一段说明为什么这几行不许写中文。
