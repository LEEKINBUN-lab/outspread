# 参与「铺陈」画板

先读 [README](README.md) 的「跑起来」和 [docs/](docs/)。这个仓库最重要的约定是：**板子文件是用户的数据，不是测试夹具**；任何自动测试都必须在临时副本上跑。

## 本地检查

```powershell
# 全量门禁：核心测试 + 两个构建 + 界面自检（自检会在 %TEMP% 的副本上跑）
.\check.cmd
```

只跑核心测试：

```powershell
dotnet test tests/Outspread.Core.Tests/Outspread.Core.Tests.csproj -c Debug
```

界面自检需要先构建，并把**临时副本**作为参数传进去：

```powershell
$scratch = Join-Path $env:TEMP 'outspread-selftest'
New-Item -ItemType Directory -Force $scratch | Out-Null
Copy-Item tests\fixtures\gate.canvas "$scratch\selftest.canvas" -Force
src\Outspread.Board\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Outspread.Board.exe --selftest "$scratch\selftest.canvas"
```

## 提交前

- `check.cmd` 必须过；界面自检不能靠跳过或放宽断言变绿。
- 新增交互要同时给出可自动验证的读数；不能自动验证的（例如真正的鼠标双击手感）在 PR 说明里写清复现步骤。
- 不要提交 `%LOCALAPPDATA%\Outspread\`、`boards/*.canvas` 的个人数据、`bin/`、`obj/` 或同步令牌。
- 中文文案先按 [README](README.md) 的语气写；涉及用户可见的错误要把原因和下一步说清楚。

## 改板子格式

JSON Canvas 1.0 的字段、打补丁存盘、外部改动合并都放在 `src/Outspread.Core/`。改解析或补丁语义前先读 `docs/canvas-format.md` 和 `docs/note-lifecycle.md`，并给读写两侧各留一条测试。

## 问题与建议

用仓库的 Issue 模板；附上复现步骤、Windows 版本、`board.log` 里的相关行（先去掉令牌和个人路径）。
