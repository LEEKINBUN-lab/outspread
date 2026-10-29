# spikes/SurfaceSpike — 一枚读数用的 spike，**当前编不过，且不该被修好**

它要回答的问题：能不能在现有栈（WinUI 3 桌面）里把整块板面画成**一枚合成表面**，
用来比"每格一枚 XAML 视觉"那一本账值多少 GPU／CPU／帧间隔。

## go/no-go 的实测结果（2026-10-29）

| 试到哪一步 | 读到的东西 |
| --- | --- |
| 只加 `Win2D.uwp` 1.28.3 | `NETSDK1218`：本项目对完整 Windows SDK 投影（含 `Windows.UI.Xaml.*`）有传递依赖，但未指定 `UseUwp`。**报在 targets 层，在我自己的代码之前** |
| 加 `<UseUwp>true</UseUwp>` | `Microsoft.WinUI.targets:98`：两套 XAML 投影一次只能正确工作一种，硬开得 `EnableUnsafeMixedMicrosoftWindowsUIXamlProjections` |
| 再开那个 unsafe 开关 | 引用层过了，但 **WinUI 的 XAML 代码生成整条不再跑**：`obj/.../win-x64/` 里只有 `GlobalUsings.g.cs`，没有 `App.g.cs`/`MainWindow.g.cs` ⇒ `InitializeComponent` 与入口 `Main` 双双不存在（CS5001／CS0103） |

结论：**Win2D 这条路在 WinUI 3 桌面里断**，不是参数没调对。剩下的两条都不便宜：

- **A 手写 interop**：WinUI 第一方的 `Compositor.CreateCompositionDrawingSurface` ＋ 第三方 Direct2D COM 绑定（Vortice 之类），几百行设备/表面绑定；
- **B 换栈**：Uno Platform 或 Avalonia，它们的 Skia 渲染是官方支持的那条单表面路。

## 为什么留着

`MainWindow.xaml.cs` 里那套东西是有用的骨架，走 A 的时候直接接上：
读夹具、世界单位排版＋一枚缩放变换（与产品的 `_paintZoom` 同构）、
阴影带现读 `NoteSurface.BandsFor(id, 1.0, w, h)`、纸色现读 `NoteSurface.PaperAt`、
`CanvasTextLayout` 按格缓存、屏外不画、节拍挂 `CompositionTarget.Rendering`、
收场自报"帧间隔中位／超 25ms 帧数／最坏五帧／常驻／正对照"。

`NoteStyling.cs` 是从产品**链**进来的（不是复制的那一份），两臂读同一组色号与字体栈。

## 闸门

这枚工程不在 `check.cmd` 里（那里面按项目点名：Core.Tests、Mcp.Tests、`src/Outspread.Board`、`src/Outspread.Cli`），
也不在 `run-board.cmd` 里。它不进产品，别把它加进任何闸门。
