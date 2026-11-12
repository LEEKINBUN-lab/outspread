using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The command table, and the commands that change the document's shape rather than its geometry.
/// </summary>
/// <remarks>
/// <para>
/// One row per command, read by the menu, by the keyboard dispatcher and by the self test — the
/// prototype's <c>CMDS</c> shape. That is not tidiness: his config defers bare-key shortcuts, which
/// leaves the menu as the only non-keyboard way to reach a command (WCAG 2.5.7), so a command that
/// exists in the key handler but not in the menu is a rule violation, and a menu entry with no
/// handler is a lie. Generating both from one table makes both impossible.
/// </para>
/// <para>
/// Chords are modifier combinations only, and every command states its precondition in the same
/// breath as its action — refusing says how many notes were needed and how many were offered.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <param name="Dispatched">
    /// False for the handful of keys the window still handles in its own switch (Esc, Ctrl+A,
    /// Ctrl+V, Ctrl+Z/Y, Delete, Ctrl+0). They live in this table so the cheat sheet cannot lie
    /// about them, but the table must not also run them — that would fire twice. M1b's registry
    /// pass is meant to fold those six into the switch-free path and drop this flag.
    /// </param>
    /// <param name="Listed">
    /// False keeps a working command off the two surfaces that *list* things (the key sheet and the
    /// right-click menu) while the keyboard still dispatches it. His call on 2026-10-12: the
    /// everyday five (全选 / 删除 / 撤销 / 重做 / 查找) do not need a menu route — a list that
    /// carries them reads like a manual, and the sheet is for the chords nobody guesses.
    /// </param>
    /// <param name="Sheet">
    /// False keeps the row off the Tab sheet only. The sheet is the "chords nobody guesses" page:
    /// copy/paste, Esc, arrow nudges, double-click-new and closing a board were named by him on
    /// 10-24 as noise there. The menu and the keyboard still use the same row.
    /// </param>
    /// <param name="Menu">
    /// False keeps the row off the right-click menu only. “从边缘拉一根线” stays on the Tab sheet —
    /// it is the only place the double-click gesture is written down — but the menu already has
    /// plenty of entries and he asked for that one to go (10-25).
    /// </param>
    private readonly record struct Command(string Label, string Chord, string Section, Action Run, bool Dispatched = true, bool Listed = true, bool Sheet = true, bool Menu = true)
    {
        public string MenuText => Chord is { Length: > 0 } chord ? $"{Label}    {chord}" : Label;

        /// <summary>
        /// 键帽上那一串：方向键写成箭头（Alt+Left → Alt+←）。表里的字符串仍用 e.Key 的名字，
        /// 因为派发是拿它和 <c>e.Key.ToString()</c> 比的；这里只换显示。
        /// </summary>
        public string ChordDisplay => Caps(Chord);

        /// <summary>
        /// 任意一串键位的显示形。改键之后显示的**不是**表里那一串，所以这一条必须是静态的：
        /// 键位页、Tab 清单、右键菜单三处都拿"现在这一串"过它，而不是各自去拆 <see cref="Chord"/>。
        /// </summary>
        public static string Caps(string chord)
            => chord is { Length: > 0 }
                ? string.Join('+', chord.Split('+').Select(part => part switch
                {
                    "Left" => "←",
                    "Right" => "→",
                    "Up" => "↑",
                    "Down" => "↓",
                    _ => part,
                }))
                : string.Empty;
    }

    private Command[] Commands() =>
    [
        // 键位清单按"同一类键连在一起"排：Alt＋方向四条相邻，居中两条相邻，均分两条相邻。
        new("左对齐", "Alt+Left", "排布", () => Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left))),
        new("右对齐", "Alt+Right", "排布", () => Arrange("右对齐", items => Aligning.Align(items, Aligning.Side.Right))),
        new("上对齐", "Alt+Up", "排布", () => Arrange("上对齐", items => Aligning.Align(items, Aligning.Side.Top))),
        new("下对齐", "Alt+Down", "排布", () => Arrange("下对齐", items => Aligning.Align(items, Aligning.Side.Bottom))),
        new("水平居中", "Alt+H", "排布", () => Arrange("水平居中", items => Aligning.Align(items, Aligning.Side.HCenter))),
        new("垂直居中", "Alt+V", "排布", () => Arrange("垂直居中", items => Aligning.Align(items, Aligning.Side.VCenter))),
        new("横向均分", "Alt+J", "排布", () => Arrange("横向均分", items => Aligning.Distribute(items, horizontal: true))),
        new("纵向均分", "Alt+K", "排布", () => Arrange("纵向均分", items => Aligning.Distribute(items, horizontal: false))),
        // Alt+L 在这台机器上会和系统抢（他 10-27：一按就响系统提示音），换成 Alt+B（"叠"的首字母位）。
        new("堆叠", "Alt+B", "排布", () => Arrange("堆叠", Aligning.Stack)),
        new("按颜色排列", "Alt+Y", "排列", () => ArrangeByKey("按颜色排列", node => node.Color ?? "无颜色"), Menu: false),
        new("按标签排列", "Alt+T", "排列", () => ArrangeByKey("按标签排列", node => Tags.Of(node.Text).FirstOrDefault() ?? "#无标签", "#无标签"), Menu: false),
        new("按序号排列", "Alt+N", "排列", () => ArrangeBySequence("按序号排列"), Menu: false),
        new("成组", "Ctrl+G", "组织", GroupOrDissolve),
        new("拆组", string.Empty, "组织", UngroupSelection, Sheet: false),
        new("制作副本", "Ctrl+D", "组织", DuplicateSelection),
        new("打开原件", string.Empty, "组织", OpenSelectedEntity, Dispatched: false, Sheet: false),
        new("在文件夹中打开", string.Empty, "组织", RevealSelectedEntity, Dispatched: false, Sheet: false),

        // 连线按叠放次序把选中的串成一条链，全都在就是拆链（原型同一条命令两用）。
        new("连线", "Alt+C", "组织", ConnectSelection),
        // 键位页不再列它（他 10-25 点名），但双击边缘那条手势本身照旧。
        new("从边缘拉一根线", "双击边缘", "组织", StartPullFromSelection, Dispatched: false, Sheet: false, Menu: false),
        new("全部选中", "Ctrl+A", "板子", SelectAll, Dispatched: false, Listed: false),
        new("复制选中", "Ctrl+C", "板子", () => _ = CopySelectionAsync(), Sheet: false),
        new("剪切选中", "Ctrl+X", "板子", () => _ = CutSelectionAsync(), Listed: false, Sheet: false),
        new("粘贴剪贴板", "Ctrl+V", "板子", () => _ = PasteFromClipboardAsync(), Dispatched: false, Sheet: false),
        new("取消选中", "Esc", "板子", () => { ClearSelection(); ProjectSelection(); }, Dispatched: false, Sheet: false),
        new("删掉选中", "Delete", "板子", RemoveSelection, Dispatched: false, Listed: false),
        new("保存", "Ctrl+S", "板子", SaveAndConfirm, Sheet: false, Menu: false),
        new("撤回", "Ctrl+Z", "板子", Undo, Dispatched: false, Listed: false),
        new("重做", "Ctrl+Y", "板子", Redo, Dispatched: false, Listed: false),
        new("新建便签", "双击空白", "板子", NewNoteAtCentre, Dispatched: false, Sheet: false),
        // 演示（10-30 夜）：一枚组一页、页序＝组名时间戳；←/→ 或空格翻页、Esc 退出并还原相机。
        new("演示", "Alt+P", "板子", TogglePresent),

        // 实体卡不能只靠"碰巧粘对形状"才建得出来：粘的是别家软件的剪贴板、或在触屏上，
        // 这一条是唯一走得进去的门。识别与建卡那半和粘贴共用同一发，两条入口不可能认得不一样。
        new("从路径或链接新建卡片", string.Empty, "板子", () => _ = NewCardFromLocationAsync()),

        // 查找与恢复没有第二个入口的要不得：查找是"贴进去的东西找得回来"，恢复是"改坏了还够得着"。
        new("查找", "Ctrl+F", "板子", OpenSearch, Listed: false),
        // 恢复只从标题条右端的 ⋮ 进；键位页不再列它（功能还在）。
        new("恢复…", string.Empty, "板子", OpenRestorePanel, Sheet: false),

        // 一块板一件事：这一组管的是"这扇窗口现在在处理哪件事"，不动板子上的任何一个字。
        new("新板块", "Ctrl+N", "换板子", CreateNewBoard),
        new("关掉这块板", "Ctrl+W", "换板子", CloseCurrentBoard, Sheet: false),
        new("下一块板", "Ctrl+Right", "换板子", () => StepBoard(1)),
        new("上一块板", "Ctrl+Left", "换板子", () => StepBoard(-1)),

        // Shift+1 / Shift+2 同样走那枚 switch（数字键的 e.Key 是 Number1），这里只负责菜单与清单上有它。
        new("看到全部", "Shift+1", "看", ZoomToFit, Dispatched: false),
        new("看到选中", "Shift+2", "看", ZoomToSelection, Dispatched: false),
    ];

    /// <summary>保存按钮与 Ctrl+S 共用的那一发：板子本来就自动保存，这里给他一个明确的落定回执。</summary>
    private void SaveAndConfirm()
    {
        SaveNow();
        Say("保存成功");
    }

    /// <summary>统一大小的写分支：两格真的变成同一框，且每格正文都放得下；撤销逐格回到原框。
    /// 两档各跑一遍——按最小那一档尤其要量"有没有裁字"，它收窄了宽度，正是容易裁的一档。</summary>
    private void SelfTestUniformSize(List<string> problems)
    {
        // 挑正文最长和最短的那两格：Take(2) 那两格在统一宽度下恰好需要同一个高度（都是 160），
        // 于是"各自贴正文"和"最小公框"算出同一个框，变异回旧语义也照样绿——夹具必须真能分岔。
        var candidates = BoardNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text && !EntityCards.IsTable(node))
            .ToList();

        List<CanvasNode> notes;

        if (candidates.Count < 2)
        {
            notes = candidates.Take(2).ToList();
        }
        else
        {
            notes = new List<CanvasNode>
            {
                candidates.OrderByDescending(node => (node.Text ?? string.Empty).Length).First(),
                candidates.OrderBy(node => (node.Text ?? string.Empty).Length).First(),
            }.DistinctBy(node => node.Id).ToList();
        }

        if (notes.Count < 2)
        {
            problems.Add("自检统一大小: 板上不足两格文字便签");

            return;
        }

        // 记忆化（他 10-27 的卡顿工单）：同一个（正文, 宽, 字号）问第二次必须命中缓存，且数值分毫不差。
        var probe = notes[0].Text ?? string.Empty;
        var hitsBefore = MeasureCacheHits;
        var firstRead = MeasureTextHeight(probe, 300, NoteTypography.DefaultSize);
        var secondRead = MeasureTextHeight(probe, 300, NoteTypography.DefaultSize);

        if (Math.Abs(firstRead - secondRead) > 0.001)
        {
            problems.Add($"自检量高缓存: 同一句量两次得到 {firstRead:0.##} 和 {secondRead:0.##}，记忆化改坏了数值");
        }

        if (MeasureCacheHits <= hitsBefore)
        {
            problems.Add("自检量高缓存: 第二问没命中缓存——记忆化没生效，拖动还是会重复算");
        }

        Diagnostics.Write($"selftest 量高缓存: 命中 {MeasureCacheHits} 次，缓存 {MeasureCacheSize} 条，两次读数 {firstRead:0.##}／{secondRead:0.##}");

        // 原框只记一次：每一发跑完都把两发撤销回去，下一发的起点还是这里。
        var before = notes.ToDictionary(node => node.Id, node => (node.Width, node.Height), StringComparer.Ordinal);

        // 只留编号：撤销会把整份文档换成新的对象，攥着上一档的节点对象会改到一个已经不在板上的东西上
        // （今天那条"按最小量到 340"就是这么来的）。
        var ids = notes.Select(node => node.Id).ToList();

        foreach (var (bySmallest, label) in new[] { (false, "按最大"), (true, "按最小") })
        {
            var live = ids.Select(NodeOf).Where(node => node is not null).Select(node => node!).ToList();

            if (live.Count < 2)
            {
                problems.Add($"自检统一大小（{label}）: 撤销以后这两格找不回来了，量不了");

                break;
            }

            // 先弄出一宽一窄：夹具里这几格都是 340 宽，min 和 max 撞成同一个数，两档算出来一模一样，
            // 那一发就成了空操作——History 不记空操作，撤销会-pop 到别人那一步（今天那条红就是这么来的）。
            // 这一步是直接写，不进历史：它不是被测对象，进历史只会让撤销错位。
            live[0].Width = 340;
            live[1].Width = 200;
            Rebuild();

            // Rebuild 会清掉选中：不在这儿重新选上，下面统一的就是整块板而不是这两格
            // （第一版栽过：按最小量到的是全板最窄 260，不是这两格里最窄的 200）。
            _selection.Clear();

            foreach (var node in live)
            {
                _selection.Add(node.Id);
            }

            ProjectSelection();

            var staged = live.ToDictionary(node => node.Id, node => (node.Width, node.Height), StringComparer.Ordinal);
            var widest = staged.Values.Max(size => size.Width);
            var narrowest = staged.Values.Min(size => size.Width);
            var shortest = staged.Values.Min(size => size.Height);

            UniformNoteSizes(bySmallest);

            var after = live.Select(node => NodeOf(node.Id)).Where(node => node is not null).Select(node => node!).ToList();

            if (after.Count != notes.Count
                || after.Select(node => node.Width).Distinct().Count() != 1
                || after.Select(node => node.Height).Distinct().Count() != 1)
            {
                problems.Add($"自检统一大小（{label}）: 两档都该收到同一个框（{string.Join(" / ", after.Select(node => $"{node.Width:0}×{node.Height:0}"))}）");
            }
            else
            {
                var width = after[0].Width;
                var height = after[0].Height;
                var wantWidth = bySmallest ? narrowest : widest;

                if (Math.Abs(width - wantWidth) > 0.5)
                {
                    problems.Add($"自检统一大小（{label}）: 宽统一到了 {width:0}，应取这群里的 {(bySmallest ? "最窄" : "最宽")} {wantWidth:0}");
                }

                // 他 10-26 22:5x 定的：按最小＝宽高各自取组内最小，**允许裁字**（这一档换的是省地方）。
                if (bySmallest && Math.Abs(height - shortest) > 0.5)
                {
                    problems.Add($"自检统一大小（按最小）: 高收到 {height:0}，应取这群里最矮的 {shortest:0}");
                }

                var tallest = after.Max(node => MeasureTextHeight(node.Text ?? string.Empty, node.Width, _view.FontSizeFor(node.Id)));

                // 裁不裁是这一档允许的，但**回执不能瞒**：裁了几格必须和真算出来的数对得上。
                var clipped = after.Count(node => MeasureTextHeight(node.Text ?? string.Empty, node.Width, _view.FontSizeFor(node.Id)) > height + 0.5);
                var receipt = SayTextForSelfTest ?? string.Empty;

                if (bySmallest && clipped == 0 && !receipt.Contains("都还放得下", StringComparison.Ordinal))
                {
                    problems.Add($"自检统一大小（{label}）: 一格没裁，回执却没这么说（读到「{receipt}」）");
                }

                if (bySmallest && clipped > 0 && !receipt.Contains($"其中 {clipped} 格正文放不下", StringComparison.Ordinal))
                {
                    problems.Add($"自检统一大小（{label}）: 裁了 {clipped} 格，回执没报（读到「{receipt}」）");
                }

                if (!bySmallest && clipped > 0)
                {
                    problems.Add($"自检统一大小（按最大）: 这一档承诺谁也不裁，却裁了 {clipped} 格（正文要 {tallest:0}，框只有 {height:0}）");
                }

                Diagnostics.Write($"selftest 统一大小（{label}）: 这群里宽 {narrowest:0}–{widest:0}、矮到 {shortest:0}，"
                    + $"统一成 {width:0}×{height:0}，裁 {clipped} 格（最高那格的正文要 {tallest:0}）");
            }

            // 两发都得从撤销栈上摘干净：栈是整块板共用的，留一枚在后面，
            // 下一位（左对齐那一步）的 Undo 就撤到我这一发上——10-26 那发"第一跑必红、重跑才绿"就是这么来的。
            // "统一大小是一步撤销"这句产品承诺只在按最大那一发验：连撤两发会和别人的步骤错位。
            Undo();

            if (!bySmallest)
            {
                foreach (var (id, size) in staged)
                {
                    if (NodeOf(id) is { } node
                        && (Math.Abs(node.Width - size.Width) > 0.01 || Math.Abs(node.Height - size.Height) > 0.01))
                    {
                        problems.Add($"自检统一大小（{label}）: {id} 撤销以后没回到 {size.Width:0}×{size.Height:0}（读到 {node.Width:0}×{node.Height:0}）");
                    }
                }
            }

            // 回原框用直接写，不靠撤销栈：这一档测的是"宽度取哪一头"，撤销只在上档验一次。
            foreach (var (id, size) in before)
            {
                if (NodeOf(id) is { } node)
                {
                    node.Width = size.Width;
                    node.Height = size.Height;
                }

                _view.UnmarkManualSize(id);
            }

            Rebuild();
        }

        // 等宽那一发是他 10-26 21:4x 撞的：那批卡的宽本来就全一样，只收宽的那一档点出来毫无变化，
        // 他读到的就是"我按了最小，怎么按的最大"。22:5x 定档：宽高都取组内最小、允许裁字——
        // 这一发锁的就是"宽没得收时，高照样得收到组内最矮，并且回执说出裁了几格"。
        {
            var equal = ids.Select(NodeOf).Where(node => node is not null).Select(node => node!).ToList();

            if (equal.Count >= 2)
            {
                equal[0].Width = 200;
                equal[1].Width = 200;

                // 一枚抬到 500 的空框（先挂手动标记，免得布局那一发把它按回去）：
                // 这一档要是在高上取"最高的那格"，500 会传染给另一格——那正是被他否掉的行为。
                _view.MarkManualSize(equal[0].Id);
                equal[0].Height = 500;
                Rebuild();

                _selection.Clear();

                foreach (var node in equal)
                {
                    _selection.Add(node.Id);
                }

                ProjectSelection();

                var staged2 = equal.ToDictionary(node => node.Id, node => (node.Width, node.Height), StringComparer.Ordinal);
                var shortest2 = staged2.Values.Min(size => size.Height);

                UniformNoteSizes(bySmallest: true);

                var same = equal.Select(node => NodeOf(node.Id)).Where(node => node is not null).Select(node => node!).ToList();
                var receipt = SayTextForSelfTest ?? string.Empty;

                if (same.Any(node => Math.Abs(node.Width - 200) > 0.5))
                {
                    problems.Add($"自检统一大小（宽本来就一样）: 不该动的宽动了（读到 {string.Join(" / ", same.Select(node => $"{node.Width:0}"))}）");
                }

                if (same.Any(node => Math.Abs(node.Height - shortest2) > 0.5))
                {
                    problems.Add($"自检统一大小（宽本来就一样）: 高没收到组内最矮的 {shortest2:0}（读到 {string.Join(" / ", same.Select(node => $"{node.Height:0}"))}）——那一格 500 的空框没收掉");
                }

                var clipped2 = same.Count(node => MeasureTextHeight(node.Text ?? string.Empty, node.Width, _view.FontSizeFor(node.Id)) > shortest2 + 0.5);

                if (clipped2 > 0 && !receipt.Contains($"其中 {clipped2} 格正文放不下", StringComparison.Ordinal))
                {
                    problems.Add($"自检统一大小（宽本来就一样）: 裁了 {clipped2} 格，回执没报（读到「{receipt}」）");
                }

                if (!receipt.Contains("原来", StringComparison.Ordinal) || !receipt.Contains("收到最窄最矮", StringComparison.Ordinal))
                {
                    problems.Add($"自检统一大小（宽本来就一样）: 回执没给「原来／现在」两个数，或没说清这一档收到最窄最矮（读到「{receipt}」）");
                }

                Diagnostics.Write($"selftest 统一大小（宽本来就一样）: 收到 {same[0].Width:0}×{shortest2:0}，裁 {clipped2} 格——{receipt}");

                // 这一步自己也要从撤销栈上摘掉：栈是整块板共用的，留一枚在这儿，
                // 后面"左对齐"那一步的 Undo 就会撤到我这一发上（10-26 那发第一跑必红、重跑才绿就是这么来的）。
                if (same.Any(node => staged2.TryGetValue(node.Id, out var size)
                        && (Math.Abs(node.Width - size.Width) > 0.5 || Math.Abs(node.Height - size.Height) > 0.5)))
                {
                    Undo();
                }

                foreach (var (id, size) in before)
                {
                    if (NodeOf(id) is { } node)
                    {
                        node.Width = size.Width;
                        node.Height = size.Height;
                    }

                    _view.UnmarkManualSize(id);
                }

                Rebuild();
            }
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>右键菜单的「统一便签大小」：选中优先；没选中就对所有文字便签。</summary>
    /// <param name="bySmallest">false＝按最大的统一：宽度取最宽的一格，框高取这群里最长那段正文放得下的
    /// 那一格——全组进同一个框，谁也不裁。
    /// true＝按最小的统一：宽、高**各取组内最小**拼成一个框，全组收进去，**允许裁字**——
    /// 他 10-26 22:5x 定的：这一档换的就是"占得少"，被压掉的正文由渐隐收尾，想看全用按最大那一档。
    /// 两维不可能都由"每格自己的最小值"拼出来：宽没有自己的最小值（正文能无限折行），
    /// 高的最小值各格不同，取最小的那个必然裁掉最高的那格——所以这里取的是组内的 min 宽 × min 高。</param>
    private void UniformNoteSizes(bool bySmallest = false)
    {
        var notes = SelectedNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text && !EntityCards.IsTable(node))
            .ToList();

        if (notes.Count == 0)
        {
            notes = BoardNotes()
                .Where(node => node.Kind == CanvasNodeKind.Text && !EntityCards.IsTable(node))
                .ToList();
        }

        if (notes.Count == 0)
        {
            Say("板上没有可统一的文字便签");

            return;
        }

        var narrowest = notes.Min(node => node.Width);
        var widest = notes.Max(node => node.Width);
        var shortest = notes.Min(node => node.Height);
        var beforeTotal = notes.Sum(node => node.Height);

        // 按最小＝宽高各自取组内最小，拼成一个框（他 10-26 22:5x 定的）：**允许裁字**，
        // 这一档换的就是"占得少"，正文被压掉的部分由渐隐收尾，回头按最大那一档还能展开。
        // 不走"各自贴正文"：那样每格尺寸不一，摞起来两边都不齐，也就不叫统一了。
        var width = bySmallest ? narrowest : widest;
        var height = bySmallest
            ? shortest
            : notes.Max(node => MeasureTextHeight(
                node.Text ?? string.Empty,
                width,
                _view.FontSizeFor(node.Id)));

        BeginGesture("统一便签大小");

        foreach (var node in notes)
        {
            node.Width = width;
            node.Height = height;
            _view.MarkManualSize(node.Id);
        }

        EndGesture();

        // 裁掉了几格要报出来：这一档的代价就是"看不全"，他不是不知道，但不能由我替他稀里糊涂地裁。
        var clipped = notes.Count(node => MeasureTextHeight(
            node.Text ?? string.Empty,
            width,
            _view.FontSizeFor(node.Id)) > height + 0.5);
        var stacked = notes.Sum(node => node.Height);

        var ids = notes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);

        Rebuild();
        Reselect(ids);
        SaveSoon();
        ProjectSelection();
        Say(bySmallest
            ? $"已把 {notes.Count} 张便签收到最窄最矮的 {width:0}×{height:0}"
                + $"（宽取组内最窄 {narrowest:0}、高取组内最矮 {shortest:0}；原来合计 {beforeTotal:0}px，现在 {stacked:0}px）"
                + (clipped == 0
                    ? "——每一格正文都还放得下。"
                    : $"——其中 {clipped} 格正文放不下，末尾会被渐隐吃掉；要看得全用「按最大的统一」。")
            : $"已把 {notes.Count} 张便签统一到 {width:0}×{height:0}"
                + $"（按最大：宽取这群里最宽的 {width:0}，高取这格里最长那段正文放得下的 {height:0}）");
    }

    /// <summary>Every node on the board — the same call the keyboard's Ctrl+A makes.</summary>
    private void SelectAll()
    {
        var emptyGroups = EmptyGroupIds();

        foreach (var node in _document.Nodes)
        {
            // 空组已经被藏起来；把它选进来只会在屏幕上留下一个没有内容的实线框。
            if (node.Kind == CanvasNodeKind.Group && emptyGroups.Contains(node.Id))
            {
                continue;
            }

            _selection.Add(node.Id);
        }

        ProjectSelection();
    }

    /// <summary>
    /// Sections in first-appearance order, so the menu reads like this table — and only the rows that
    /// are <b>listed</b>: the two surfaces that enumerate commands read this, the keyboard dispatcher
    /// reads <see cref="Commands"/> whole, so hiding a row from the list cannot switch it off.
    /// </summary>
    private IEnumerable<IGrouping<string, Command>> Sections() => Commands().Where(command => command.Listed).GroupBy(command => command.Section);

    // ---------------------------------------------------------------------
    // The group layer
    // ---------------------------------------------------------------------

    /// <summary>
    /// 最近一次按下的是哪一组的名牌／环带——只是菜单的<b>语境</b>（右键它时"拆组"排前面），
    /// 不再是一层选中状态：10-22 起单击便签就是那一枚，点边才选整组，层这个概念没了。
    /// </summary>
    private string? _groupFocus;

    private List<CanvasNode> SelectedNotes()
        => _document.Nodes.Where(node => _selection.Contains(node.Id) && node.Kind != CanvasNodeKind.Group).ToList();

    private List<CanvasNode> BoardNotes()
        => _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();

    private List<Grouping.Rect> GroupRects()
        => _document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group).Select(RectOf).ToList();

    /// <summary>
    /// 谁在哪个组里：一次算一份，问的人全走这一份。
    /// </summary>
    /// <remarks>
    /// 规矩（10-24 他点的）：组框上写着 <c>members</c> 名单就用名单，<b>有名单的框不再靠几何收人</b>——
    /// 否则"新建一格正好落在框里"照样当场入组，那一格等于没修。没有名单的框（别的工具写的、
    /// 这份改动之前存的）照旧按几何。写名单的只有三处：成组、组钮拖入／拖出、拆组（删框）。
    /// 普通拖动一格什么都不写：那是挪位置，不是改关系。
    /// </remarks>
    private GroupMap Groups()
        => GroupMap.Build(
            BoardNotes().ConvertAll(RectOf),
            [.. _document.Nodes
                .Where(node => node.Kind == CanvasNodeKind.Group)
                .Select(node => (RectOf(node), (IReadOnlyList<string>?)node.GroupMemberIds))]);

    private static Grouping.Rect RectOf(CanvasNode node) => new(node.Id, node.X, node.Y, node.Width, node.Height);

    private static Aligning.Placed PlacedOf(CanvasNode node)
        => new(node.Id, node.X, node.Y, node.Width, node.Height);

    /// <summary>Clear the selection and the layer with it — never one without the other.</summary>
    private void ClearSelection()
    {
        _selection.Clear();
        _groupFocus = null;

        // 线是另一种选中物，但同一时刻只选一样：清格子也清线。
        ClearEdgeSelection();

        // 选择框/手柄/操作条画的是文档位置上的独立一层，不跟着格子走：只清集合不重投影，
        // 那一层就留在原处（他 11-03 报的"进镜头后原本的选择框没清理掉"）。
        ProjectSelection();
    }

    private void GroupSelection()
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            Say("请先选中至少一个便签再创建组");
            return;
        }

        var map = Groups();
        var selected = members.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
        var loose = members.Where(note => map.GroupOf(RectOf(note)) is null).ToList();

        // 嵌套（11-12 他拍）：选中了某个组的**全部**便签，这个组就整组当一件东西，
        // 和选中里的散格一起新建一个外层组。只选中组里一部分时，仍旧走"并进已有的组"。
        var fullGroups = GroupRects()
            .Where(frame => map.HasRoster(frame.Id))
            .Select(frame => (Frame: frame.Id, Notes: map.NoteIdsUnder(frame.Id)))
            .Where(pair => pair.Notes.Count > 0 && pair.Notes.All(selected.Contains))
            .ToList();

        // 都是"整组选中"时只留最外面那几枚：外层已经装着内层，不能把同一个组写进两层。
        var units = fullGroups
            .Where(pair => !fullGroups.Any(other => other.Frame != pair.Frame
                && other.Notes.Count > pair.Notes.Count
                && other.Notes.ToHashSet(StringComparer.Ordinal).IsSupersetOf(pair.Notes)))
            .Select(pair => pair.Frame)
            .ToList();

        var covered = units
            .SelectMany(id => map.NoteIdsUnder(id))
            .ToHashSet(StringComparer.Ordinal);

        if (units.Count > 0)
        {
            if (units.Count == 1 && loose.Count == 0)
            {
                // 只有"再罩一层"的活要干时不做：一个组外面再包一个组没有任何意义。
                Say("所选便签均已属于组 · 按 Ctrl＋G 解散");
                return;
            }

            var memberIds = new List<string>(units);

            memberIds.AddRange(loose.Select(note => note.Id));

            CreateGroup(
                memberIds,
                members.Select(note => note.Id).ToList(),
                covered.Count + loose.Count);

            return;
        }

        if (loose.Count == 0)
        {
            Say("所选便签均已属于组 · 按 Ctrl＋G 解散");
            return;
        }

        // 选中里已经有组里的格子 ⇒ 把没成组的并进那个组，不再罩第二个框
        // （他 10-24：成组和没成组的混选，没成组的入组）。
        if (loose.Count < members.Count && TargetGroup(map, members) is { } target)
        {
            AppendToGroup(target, loose, map);

            return;
        }

        CreateGroup([.. members.Select(member => member.Id)], [.. members.Select(member => member.Id)], members.Count);
    }

    /// <summary>
    /// 嵌套组（11-12 他拍的三条）：多选整组＋散格成组＝长出外层；点外层名牌选里面全部便签、
    /// 点内层名牌只选内层；拆外层时内层原样留下；外层全是容器、画得比里层淡。
    /// </summary>
    private void SelfTestNestedGroups(List<string> problems)
    {
        var originX = Math.Round(_view.ToWorldX(420));
        var originY = Math.Round(_view.ToWorldY(320));

        var first = _document.Add(CanvasNodeKind.Text, originX, originY, 200, 100);
        var second = _document.Add(CanvasNodeKind.Text, originX, originY + 130, 200, 100);
        var loose = _document.Add(CanvasNodeKind.Text, originX + 260, originY, 200, 100);

        var inner = _document.Add(CanvasNodeKind.Group, originX - 10, originY - 10, 220, 250);
        inner.Label = "自检内层";
        inner.SetGroupMembers([first.Id, second.Id]);

        Rebuild();

        ClearSelection();
        _selection.Add(first.Id);
        _selection.Add(second.Id);
        _selection.Add(loose.Id);
        ProjectSelection();

        GroupSelection();

        var outer = _document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Group
                && node.Id != inner.Id
                && node.GroupMemberIds is { } roster
                && roster.Contains(inner.Id, StringComparer.Ordinal)
                && roster.Contains(loose.Id, StringComparer.Ordinal))
            .FirstOrDefault();

        if (outer is null)
        {
            problems.Add("selftest 嵌套组: 整组＋散格按下成组，没有长出外层组（或外层名单里没有内层组的 id）");
            CleanupNestedSelfTest(inner, loose, first, second);

            return;
        }

        var innerStill = NodeOf(inner.Id)?.GroupMemberIds;
        var outerIds = Groups().NoteIdsUnder(outer.Id);
        var innerIds = Groups().NoteIdsUnder(inner.Id);
        var derived = DerivedFrames();

        var encloses = derived.TryGetValue(outer.Id, out var outerBox)
            && NodeOf(inner.Id) is { } innerNow
            && outerBox.X <= innerNow.X + 0.001
            && outerBox.Y <= innerNow.Y + 0.001
            && outerBox.X + outerBox.Width >= innerNow.X + innerNow.Width - 0.001
            && outerBox.Y + outerBox.Height >= innerNow.Y + innerNow.Height - 0.001;

        _realized.TryGetValue(outer.Id, out var outerVisual);
        _realized.TryGetValue(inner.Id, out var innerVisual);

        var outerFainter = outerVisual is not null && innerVisual is not null
            && innerVisual.GroupFrameOpacity() > outerVisual.GroupFrameOpacity();

        if (innerStill is null || !innerStill.Contains(first.Id, StringComparer.Ordinal))
        {
            problems.Add("selftest 嵌套组: 长出外层以后，内层组自己那份名单被改掉了");
        }
        else if (!outerIds.Contains(first.Id) || !outerIds.Contains(second.Id) || !outerIds.Contains(loose.Id))
        {
            problems.Add($"selftest 嵌套组: 点外层名牌该收全部 3 格便签，读到 {outerIds.Count} 格");
        }
        else if (innerIds.Contains(loose.Id) || innerIds.Count != 2)
        {
            problems.Add($"selftest 嵌套组: 点内层名牌该只收内层 2 格，读到 {innerIds.Count} 格");
        }
        else if (!encloses)
        {
            problems.Add("selftest 嵌套组: 外层框没有把内层框罩进去");
        }
        else if (!outerFainter)
        {
            problems.Add("selftest 嵌套组: 外层容器框没有画得比里层淡（或两枚框没画出来，读不到不透明度）");
        }
        else
        {
            Diagnostics.Write(
                $"selftest 嵌套组: 外层名单={outer.GroupMemberIds?.Count ?? 0} 项、点外层收 {outerIds.Count} 格便签、"
                + $"点内层收 {innerIds.Count} 格；外层框罩住内层，不透明度 {outerVisual?.GroupFrameOpacity() ?? 1:0.##} < 内层 {innerVisual?.GroupFrameOpacity() ?? 1:0.##}");
        }

        // 拆外层：只删外层，内层组与它的名单原样留下，散格回到自由身。
        ClearSelection();
        _selection.Add(first.Id);
        _selection.Add(second.Id);
        _selection.Add(loose.Id);
        _groupFocus = outer.Id;
        ProjectSelection();

        UngroupSelection();

        var innerKept = NodeOf(inner.Id)?.GroupMemberIds is { } keptRoster
            && keptRoster.Contains(first.Id, StringComparer.Ordinal)
            && keptRoster.Contains(second.Id, StringComparer.Ordinal);
        var looseFree = Groups().GroupOf(RectOf(NodeOf(loose.Id)!)) is null;

        if (NodeOf(outer.Id) is not null)
        {
            problems.Add("selftest 嵌套组: 拆外层以后外层框还在");
        }
        else if (!innerKept)
        {
            problems.Add("selftest 嵌套组: 拆外层时把内层组也拆了（或改了它的名单）");
        }
        else if (!looseFree)
        {
            problems.Add("selftest 嵌套组: 拆外层以后散格还在某个组里");
        }
        else
        {
            Diagnostics.Write("selftest 嵌套组: 拆外层只删外层，内层组与名单原样、散格回到自由身");
        }

        CleanupNestedSelfTest(inner, loose, first, second);
    }

    private void CleanupNestedSelfTest(params CanvasNode[] nodes)
    {
        ClearSelection();
        _groupFocus = null;

        foreach (var node in nodes)
        {
            if (NodeOf(node.Id) is not null)
            {
                _document.Remove(node.Id);
            }
        }

        Rebuild();
        ProjectSelection();   // 操作条跟着选中一起收：只 Rebuild 会把它留在屏上

        // 收操作条带淡出：自检是同步跑完的，下一发读 Visibility 时那支淡出还没播完，
        // 会被当成"条还亮着"。这里直接落定，下一发看到的就是收好的样子。
        if (_rail is not null && !RailIsShown)
        {
            _rail.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>新建一个组框，成员名单原样写进去：可能是便签，也可能是整块内层组（嵌套）。</summary>
    private void CreateGroup(IReadOnlyList<string> memberIds, IReadOnlyList<string> selectIds, int noteCount)
    {
        BeginGesture("成组");

        var boxes = memberIds
            .Select(id => NodeOf(id))
            .Where(node => node is not null)
            .Select(node => RectOf(node!))
            .ToList();

        var frame = Grouping.Enclose(string.Empty, boxes);
        var made = _document.Add(CanvasNodeKind.Group, frame.X, frame.Y, frame.Width, frame.Height);

        // 新建的组要有一句能看见、能改的描述（10-22 他：新建的组没描述也没法改）。
        // 给个默认名，双击名牌即可改名。
        made.Label = "分组";

        // 成员是一张名单：成组那一拍把它写死，之后只有组钮那一路能改它。
        made.SetGroupMembers([.. memberIds]);

        EndGesture();

        _groupFocus = made.Id;
        Rebuild();
        Reselect(selectIds);
        SaveSoon();
        ProjectSelection();

        Say(noteCount == 1
            ? "已创建组（1 个便签）· 按 Ctrl＋G 解散"
            : $"已创建组（{noteCount} 个便签）· 按 Ctrl＋G 解散");
    }

    /// <summary>
    /// Ctrl＋G 这一发是"成组"还是"解散"：选中的每一格都已经在组里 ⇒ 解散；否则按
    /// <see cref="GroupSelection"/> 办（全散＝新建，混选＝把散格并进已有的组）。
    /// </summary>
    private void GroupOrDissolve()
    {
        var members = SelectedNotes();
        var map = Groups();

        if (members.Count > 0 && members.All(note => map.GroupOf(RectOf(note)) is not null))
        {
            UngroupSelection();

            return;
        }

        GroupSelection();
    }

    /// <summary>混选里该并进哪个组：先认当前聚焦的那个组，再认"选中成员最多的那个"。</summary>
    private Grouping.Rect? TargetGroup(GroupMap map, IReadOnlyList<CanvasNode> members)
    {
        var groups = members
            .Select(note => map.GroupOf(RectOf(note)))
            .Where(group => group is not null)
            .Select(group => group!.Value)
            .ToList();

        if (groups.Count == 0)
        {
            return null;
        }

        if (_groupFocus is { } focus && groups.Any(group => group.Id == focus))
        {
            return groups.First(group => group.Id == focus);
        }

        return groups
            .GroupBy(group => group.Id)
            .OrderByDescending(bucket => bucket.Count())
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
            .First()
            .First();
    }

    /// <summary>把没成组的并进已有那个组：只改名单，框在 EndGesture 里按名单自己长。</summary>
    private void AppendToGroup(Grouping.Rect target, IReadOnlyList<CanvasNode> loose, GroupMap map)
    {
        if (NodeOf(target.Id) is not { Kind: CanvasNodeKind.Group } frame)
        {
            return;
        }

        var ids = (frame.GroupMemberIds ?? map.MemberIds(target.Id)).ToList();

        foreach (var note in loose)
        {
            if (!ids.Contains(note.Id, StringComparer.Ordinal))
            {
                ids.Add(note.Id);
            }
        }

        var keep = _selection.ToList();

        BeginGesture("并进组");
        frame.SetGroupMembers(ids);
        EndGesture();

        _groupFocus = frame.Id;
        Rebuild();
        Reselect(keep);
        SaveSoon();
        ProjectSelection();

        Say("已加入组 · 按 Ctrl＋G 解散");
    }

    /// <summary>重铸会清选中；成组/解散之后要把人还选着——不然"再按一次"就没有对象了。</summary>
    private void Reselect(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (NodeOf(id) is not null)
            {
                _selection.Add(id);
            }
        }
    }

    /// <summary>解散选中这些格子所在的组（菜单那一项；Ctrl＋G 在"全都在组里"时走这里）。</summary>
    /// <remarks>
    /// 嵌套档（11-12 他拍）：按住的是哪个框就拆哪个——拆外层时里面的内层组原样留下。
    /// 没按过框（比如框选）时拆的是"直接装着选中便签的那些组"，并且只拆最外层那些：
    /// 外层一拆，内层组仍然带着自己的便签，正好是"保留内层"那条。
    /// </remarks>
    private void UngroupSelection()
    {
        var groups = GroupRects();
        var hit = new HashSet<string>(StringComparer.Ordinal);

        if (_groupFocus is { } focus && groups.Any(group => group.Id == focus))
        {
            hit.Add(focus);
        }
        else
        {
            foreach (var node in SelectedNotes())
            {
                if (Groups().GroupOf(RectOf(node)) is { } group)
                {
                    hit.Add(group.Id);
                }
            }

            foreach (var id in _selection.Where(selected => groups.Any(group => group.Id == selected)))
            {
                hit.Add(id);
            }

            // 同一发里外层和内层都被点名时只拆外层：内层组连同它的便签留下来。
            if (hit.Count > 1)
            {
                var under = hit.ToDictionary(
                    id => id,
                    id => Groups().NoteIdsUnder(id).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

                hit = hit
                    .Where(id => !hit.Any(other => other != id
                        && under[other].Count > under[id].Count
                        && under[other].IsSupersetOf(under[id])))
                    .ToHashSet(StringComparer.Ordinal);
            }
        }

        if (hit.Count == 0)
        {
            Say("所选便签均不属于任何组");
            return;
        }

        BeginGesture("拆组");

        foreach (var id in hit)
        {
            _document.Remove(id);
        }

        _groupFocus = null;

        var keep = _selection.ToList();

        // 拆掉的框若还是某个外层的成员，那条引用一并摘掉；摘空了的外壳也一起收。
        PurgeEmptyGroups();

        EndGesture();
        Rebuild();
        Reselect(keep);
        SaveSoon();
        ProjectSelection();

        Say("组已解散");
    }

    private void DuplicateSelection()
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            Say("未选中任何便签");
            return;
        }

        BeginGesture($"制作 {members.Count} 个副本");

        foreach (var node in members)
        {
            // 整份节点原样带走：entity/label/table 这些新字段不在 Add 的六个参数里，
            // 照旧手工搬会做出"副本丢格式"的第二种行为。
            var copy = _document.AppendNode(node.Json);

            copy.X = node.X + Aligning.MinGapPx;
            copy.Y = node.Y + Aligning.MinGapPx;

            // 组复制成空组：members 指着原件，照搬会让两个组同时认领同一批成员。
            if (copy.Kind == CanvasNodeKind.Group)
            {
                copy.SetGroupMembers(null);
            }
        }

        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"已制作 {members.Count} 个副本");
    }

    /// <summary>
    /// Re-flow the whole board by a key, not just the selection — the command's promise is "put
    /// like with like", which means nothing over a subset.
    /// </summary>
    /// <summary>
    /// 排列的入口：选中了至少两格就只排选中的那些（就近找空地），没选中就是整块板
    /// （10-25 他："只选中一部分标签，然后点击排列"）。
    /// </summary>
    private void ArrangeByKey(string label, Func<CanvasNode, string> keyOf, string? lastKey = null, Func<CanvasNode, int>? priorityOf = null)
    {
        var selected = SelectedNotes();

        if (selected.Count >= 2)
        {
            ArrangeNotes(label, selected, keyOf, lastKey, priorityOf);

            return;
        }

        ArrangeWholeBoard(label, keyOf, lastKey, priorityOf);
    }

    /// <summary>按序号排：一整块矩阵，@n 小的在前，没有序号的排在最后。</summary>
    private void ArrangeBySequence(string label)
        => ArrangeByKey(label, _ => "序号", priorityOf: node => Tags.Read(node.Text).Sequence ?? int.MaxValue);

    /// <summary>只排选中的这些格子：排成块后就近找一块不压人的空地落下去。</summary>
    private void ArrangeNotes(string label, IReadOnlyList<CanvasNode> notes, Func<CanvasNode, string> keyOf, string? lastKey, Func<CanvasNode, int>? priorityOf = null)
    {
        var items = notes.Select(PlacedOf).ToList();
        var keys = notes.Select(keyOf).ToList();
        var priorities = notes.Select(priorityOf ?? DuePriority).ToList();
        var ids = notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);

        var links = _document.Edges
            .Where(edge => ids.Contains(edge.FromNode) && ids.Contains(edge.ToNode))
            .Select(edge => (edge.FromNode, edge.ToNode))
            .ToList();

        var outcome = Aligning.Arrange(items, keys, priorities, links, lastKey);

        if (!outcome.Ok)
        {
            Say($"“{label}”至少需要 {outcome.Needed} 个便签，当前可选 {outcome.Have} 个便签");

            return;
        }

        var sizes = notes.ToDictionary(note => note.Id, note => (note.Width, note.Height), StringComparer.Ordinal);
        var arranged = outcome.Moves
            .Select(move => new Aligning.Placed(
                move.Id,
                move.X,
                move.Y,
                sizes[move.Id].Width,
                sizes[move.Id].Height))
            .ToList();

        // 障碍＝别的格子（还有组框）：排好的块落在谁身上都算遮挡。
        var obstacles = BoardNotes()
            .Where(note => !ids.Contains(note.Id))
            .Select(note => RectOf(note))
            .Concat(GroupRects())
            .Select(rect => new Aligning.Placed(rect.Id, rect.X, rect.Y, rect.Width, rect.Height))
            .ToList();

        var anchorX = notes.Min(note => note.X);
        var anchorY = notes.Min(note => note.Y);
        var spot = Aligning.PlaceNear(arranged, obstacles, anchorX, anchorY);

        var blockLeft = arranged.Min(item => item.X);
        var blockTop = arranged.Min(item => item.Y);
        var shiftX = spot.X - blockLeft;
        var shiftY = spot.Y - blockTop;

        var moved = 0;

        foreach (var move in outcome.Moves)
        {
            if (NodeOf(move.Id) is not { } node
                || (Math.Abs(node.X - (move.X + shiftX)) < 0.001 && Math.Abs(node.Y - (move.Y + shiftY)) < 0.001))
            {
                continue;
            }

            moved++;
        }

        if (moved == 0)
        {
            Say("已就位");

            return;
        }

        BeginGesture(label);

        foreach (var move in outcome.Moves)
        {
            if (NodeOf(move.Id) is { } node)
            {
                node.X = move.X + shiftX;
                node.Y = move.Y + shiftY;
            }
        }

        EndGesture();
        Rebuild();
        Reselect(ids);
        SaveSoon();
        ProjectSelection();

        Say($"已应用{label}");
    }

    /// <summary>整块板：组算一件、颜色/标签分块、矩阵与小间距都在 Core 里。</summary>
    private void ArrangeWholeBoard(string label, Func<CanvasNode, string> keyOf, string? lastKey, Func<CanvasNode, int>? priorityOf = null)
    {
        var units = Groups().UnitsOf();

        // 一个组跟着它第一格走：排列要的是"同类放一起"，而组是一件东西，不能为了分色把它劈成两半。
        // 所以那一块由组里最先出现的那格决定。
        var keys = units
            .Select(unit => NodeOf(unit.MemberIds[0]) is { } first ? keyOf(first) : string.Empty)
            .ToList();

        // 组里有一格逾期，这个组就按逾期那一档排：时间是"这一件东西有多急"，不是某一格的事。
        var priority = priorityOf ?? DuePriority;

        var priorities = units
            .Select(unit => unit.MemberIds
                .Select(NodeOf)
                .Where(note => note is not null)
                .Select(note => priority(note!))
                .DefaultIfEmpty(DuePriorityOfNone)
                .Min())
            .ToList();

        // 连线按"件"算：组里任何一格连到别件，都算这个组连到那一件。
        var unitOf = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            foreach (var member in unit.MemberIds)
            {
                unitOf[member] = unit.Id;
            }
        }

        var links = _document.Edges
            .Select(edge => (
                From: unitOf.GetValueOrDefault(edge.FromNode),
                To: unitOf.GetValueOrDefault(edge.ToNode)))
            .Where(pair => pair.From is not null && pair.To is not null && pair.From != pair.To)
            .Select(pair => (pair.From!, pair.To!))
            .ToList();

        ApplyUnitMoves(label, units, Aligning.Arrange([.. units.Select(PlacedOf)], keys, priorities, links, lastKey));
    }

    /// <summary>"无日期"那一档的下标；排列的优先级越小越靠前。</summary>
    private const int DuePriorityOfNone = 4;

    /// <summary>到期优先级：逾期 → 今天 → 将来 → 日期读不出 → 无日期。</summary>
    private int DuePriority(CanvasNode note) => NoteMarks.StateOf(Tags.Read(note.Text, _view.Today), _view.Today) switch
    {
        NoteMarks.DueState.Late => 0,
        NoteMarks.DueState.Today => 1,
        NoteMarks.DueState.Upcoming => 2,
        NoteMarks.DueState.Malformed => 3,
        _ => DuePriorityOfNone,
    };

    /// <summary>
    /// Ctrl＋G 是一枚开关：全散＝新建；混选＝把散格并进已有的那个组；全在组里＝解散。
    /// 组完/解散以后选中必须还在——否则"再按一次"没有对象。三发撤销要回到原样。
    /// </summary>
    private void SelfTestGroupToggle(List<string> problems)
    {
        var before = _document.ToJson();
        var loose = BoardNotes().Where(note => Groups().GroupOf(RectOf(note)) is null).Take(3).ToList();

        if (loose.Count < 3)
        {
            problems.Add("自检成组开关: 板上不足三格散格，这一发不适用");

            return;
        }

        var groupsBefore = GroupRects().Count;

        // ① 成组：两格散格 ⇒ 一个新组。用两格而不是一格，是因为"一格成组"凑不出
        // "只选中组里一部分"那一发（那一格就是整组），混选并入就测不到了。
        SelectOnly(loose[0].Id);
        _selection.Add(loose[1].Id);
        ProjectSelection();
        GroupOrDissolve();

        var group = Groups().GroupOf(RectOf(loose[0]))?.Id;

        if (group is null || GroupRects().Count != groupsBefore + 1)
        {
            problems.Add($"自检成组开关: 两格 Ctrl＋G 没建出新组（组数 {GroupRects().Count}，原 {groupsBefore}）");

            Undo();
            Rebuild();
            ClearSelection();
            ProjectSelection();

            return;
        }

        if (!_selection.Contains(loose[0].Id))
        {
            problems.Add("自检成组开关: 成组以后选中被清掉了——再按一次没有对象");
        }

        // 混选：只选中组里**一部分** ＋ 另一格散着 ⇒ 只把散的那格并进去，不罩第二个框。
        // （整组全选中＋散格不再并进老组——那一档 11-12 起是新建外层组，见 SelfTestNestedGroups。）
        SelectOnly(loose[0].Id);
        _selection.Add(loose[2].Id);
        ProjectSelection();
        GroupOrDissolve();

        var roster = NodeOf(group)?.GroupMemberIds ?? [];

        if (GroupRects().Count != groupsBefore + 1)
        {
            problems.Add($"自检成组开关: 混选以后组数是 {GroupRects().Count}，应仍是 {groupsBefore + 1}（不该再造一个框）");
        }

        if (!roster.Contains(loose[2].Id, StringComparer.Ordinal))
        {
            problems.Add("自检成组开关: 混选里没成组的那格没被并进组");
        }

        // 全在组里 ⇒ 再按一次是解散。
        SelectOnly(loose[0].Id);
        _selection.Add(loose[1].Id);
        _selection.Add(loose[2].Id);
        ProjectSelection();
        GroupOrDissolve();

        if (NodeOf(group) is not null)
        {
            problems.Add("自检成组开关: 选中的全在组里时按 Ctrl＋G 没解散");
        }

        if (!_selection.Contains(loose[0].Id) || !_selection.Contains(loose[1].Id))
        {
            problems.Add("自检成组开关: 解散以后选中丢了");
        }

        for (var step = 0; step < 3; step++)
        {
            Undo();
        }

        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检成组开关: 三发撤销以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write("selftest 成组开关: 全散新建、混选并入、全在组里解散、选中一路保住；三发撤销回原样");
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>
    /// 排列这一发的界面接线：排完不许有格子重叠；整发是一步撤销，撤销后文档回到原样。
    /// 矩阵、优先级、连线顺序是 Core 那几条单测的事，这里只钉"界面上真的按新算法排了"。
    /// </summary>
    private void SelfTestArrange(List<string> problems)
    {
        var before = _document.ToJson();
        var notes = BoardNotes();

        if (notes.Count < 4)
        {
            problems.Add("自检排列: 板上不足四格，这一发不适用");

            return;
        }

        ArrangeByKey("按颜色排列", note => note.Color ?? "无颜色");

        var placed = BoardNotes().ConvertAll(RectOf);
        var overlaps = 0;

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var a = placed[i];
                var b = placed[j];

                if (a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height)
                {
                    overlaps++;
                }
            }
        }

        if (overlaps > 0)
        {
            problems.Add($"自检排列: 排完有 {overlaps} 对格子重叠");
        }

        Undo();
        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检排列: 撤销一发以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write($"selftest 排列: {placed.Count} 格无重叠、按颜色排成矩阵（块内 {Aligning.MinGapPx}／块间 {Aligning.BucketGapPx}）、撤销回原样");
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>
    /// 只排选中的那一发（操作条上的圆钮）：排完必须不压任何没选中的格子/组框；
    /// 整发一步撤销，撤销后文档回原样。
    /// </summary>
    private void SelfTestArrangeSelection(List<string> problems)
    {
        var before = _document.ToJson();
        var loose = BoardNotes().Where(note => Groups().GroupOf(RectOf(note)) is null).Take(3).ToList();

        if (loose.Count < 3)
        {
            problems.Add("自检排列选中: 板上不足三格散格，这一发不适用");

            return;
        }

        var ids = loose.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);

        // 没选中的那些必须一个都不动：这一发是"只排选中的"。
        var untouched = BoardNotes()
            .Where(note => !ids.Contains(note.Id))
            .ToDictionary(note => note.Id, note => (note.X, note.Y), StringComparer.Ordinal);

        ClearSelection();

        foreach (var note in loose)
        {
            _selection.Add(note.Id);
        }

        ProjectSelection();

        // 与圆钮菜单里"按标签排列"同一条路。
        var menu = BuildArrangeMenu();
        var menuRows = menu.Items.OfType<MenuFlyoutItem>().Select(item => item.Text).ToList();

        if (!menuRows.SequenceEqual(["按颜色排列", "按标签排列", "按序号排列"]))
        {
            problems.Add($"自检排列选中: 圆钮的菜单不是「颜色 / 标签 / 序号」三行（读到 {string.Join(" / ", menuRows)}）");
        }

        if (!Commands().Any(command => command.Label == "按序号排列" && command.Chord == "Alt+N"))
        {
            problems.Add("按序号排列没有 Alt＋N 这一条");
        }

        ArrangeByKey("按标签排列", note => Tags.Of(note.Text).FirstOrDefault() ?? "#无标签", "#无标签");

        // @n8：排完还是多选（选中框在），左侧那一列钮就必须也在——选中框和操作条是一件事的两半，
        // 不许一半留着、一半收回去了。
        BoardArea.UpdateLayout();

        if (_selection.Count < 2)
        {
            problems.Add($"自检排列选中: 排完选中只剩 {_selection.Count} 枚——这一发要量的就是「多选还留着时操作条在不在」，缺正对照不算过");
        }
        else if (RailButtonCount < 2)
        {
            problems.Add($"自检排列选中: 排完仍选 {_selection.Count} 枚，左侧操作条只有 {RailButtonCount} 枚钮（排列钮与标签钮该在多选时插进来）");
        }
        else if (!RailIsShown)
        {
            problems.Add($"自检排列选中: 排完仍选 {_selection.Count} 枚、操作条有 {RailButtonCount} 枚钮，可它没亮（@n8：选中框在、左侧按钮没了）");
        }
        else
        {
            Diagnostics.Write($"selftest 排列选中: 排完仍选 {_selection.Count} 枚，操作条亮着、{RailButtonCount} 枚钮");
        }

        foreach (var (id, (x, y)) in untouched)
        {
            if (NodeOf(id) is { } still && (Math.Abs(still.X - x) > 0.001 || Math.Abs(still.Y - y) > 0.001))
            {
                problems.Add($"自检排列选中: 没选中的 {id} 也被挪了（{x:0.#},{y:0.#} → {still.X:0.#},{still.Y:0.#}）");
            }
        }

        var obstacles = BoardNotes()
            .Where(note => !ids.Contains(note.Id))
            .Select(note => RectOf(note))
            .Concat(GroupRects())
            .ToList();

        var collisions = 0;

        foreach (var id in ids)
        {
            if (NodeOf(id) is not { } moved)
            {
                continue;
            }

            var box = RectOf(moved);

            collisions += obstacles.Count(obstacle =>
                box.X < obstacle.X + obstacle.Width
                && obstacle.X < box.X + box.Width
                && box.Y < obstacle.Y + obstacle.Height
                && obstacle.Y < box.Y + box.Height);
        }

        if (collisions > 0)
        {
            problems.Add($"自检排列选中: 排完压到了 {collisions} 个没选中的格子/组框");
        }

        // @n8 同款：排完还多选着，那列钮就必须够得着。把相机推到「选区比视口还宽」那一档再投影一次——
        // 老算法在这档会把操作条贴到屏幕外（选中框在、按钮没了），夹回视口里才是他要的绑定。
        var savedCamera = (_view.PanX, _view.PanY, _view.Zoom);

        try
        {
            if (SelectionBox() is not { } wide || wide.Width <= 0)
            {
                problems.Add("自检排列选中: 量不到选区，这一发没测到操作条够不够得着");
            }
            else
            {
                _view.Zoom = Math.Max(1, (BoardArea.ActualWidth / wide.Width) * 2);
                _view.PanX = (BoardArea.ActualWidth / 2) - ((wide.X + (wide.Width / 2)) * _view.Zoom);
                _view.PanY = (BoardArea.ActualHeight / 2) - ((wide.Y + (wide.Height / 2)) * _view.Zoom);

                ProjectSelection();
                BoardArea.UpdateLayout();

                var spans = wide.Width * _view.Zoom;

                if (spans <= BoardArea.ActualWidth)
                {
                    problems.Add($"自检排列选中: 推到 {spans:0} 宽仍没超过视口 {BoardArea.ActualWidth:0}，这一发没量到那一档");
                }
                else if (!RailInsideViewport)
                {
                    problems.Add($"自检排列选中: 选区 {spans:0} 宽比视口 {BoardArea.ActualWidth:0} 还宽，操作条却没落在可视区里（@n8：选中框在、左侧按钮没了）");
                }
                else
                {
                    Diagnostics.Write($"selftest 排列选中: 选区 {spans:0} 宽 > 视口 {BoardArea.ActualWidth:0}，操作条夹在可视区内、{RailButtonCount} 枚钮");
                }
            }
        }
        finally
        {
            (_view.PanX, _view.PanY, _view.Zoom) = savedCamera;

            ProjectSelection();
        }

        Undo();
        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检排列选中: 撤销一发以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write($"selftest 排列选中: 3 格就地排成一块、0 处遮挡、撤销回原样");
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>双击组名走的是改名，不是"空白新建"：名牌那一块必须能被单独认出来。</summary>
    private void SelfTestGroupLabelHit(List<string> problems)
    {
        var before = _document.ToJson();
        var created = false;
        var group = _document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Group);

        if (group is null)
        {
            var pair = BoardNotes().Take(2).ToList();

            if (pair.Count < 2)
            {
                Diagnostics.Write("selftest 组名: 板上一格都不够，这一发跳过");

                return;
            }

            ClearSelection();

            foreach (var note in pair)
            {
                _selection.Add(note.Id);
            }

            ProjectSelection();
            GroupSelection();
            Rebuild();
            created = true;
            group = _document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Group);
        }

        if (group is null || !_realized.TryGetValue(group.Id, out var visual))
        {
            problems.Add("自检组名: 组建出来了却读不到它的视觉件");

            return;
        }

        BoardArea.UpdateLayout();
        var plate = visual.NamePlateBoundsIn(BoardArea);

        if (plate.Width <= 0 || plate.Height <= 0)
        {
            problems.Add("自检组名: 名牌量不到（它没显示，或者还没排版）");
        }
        else
        {
            var centre = new Windows.Foundation.Point(plate.X + (plate.Width / 2), plate.Y + (plate.Height / 2));

            if (GroupLabelAt(centre)?.Id != group.Id)
            {
                problems.Add($"自检组名: 名牌中心 ({centre.X:0.#},{centre.Y:0.#}) 没被认成 {group.Id} 的名牌——双击会当成空白新建");
            }
            else
            {
                Diagnostics.Write($"selftest 组名: 名牌中心能认回 {group.Id}（双击＝改名，不再落成新建）");
            }
        }

        // 改名要在"组还活着"的时候试：造出来的那组等这一发过了再撤。
        var renamed = SelfTestGroupRenameByOutsidePress(problems, group);

        // 收尾：先撤改名那一条手势，再撤造组那一条。
        if (renamed)
        {
            Undo();
            Rebuild();
        }

        if (created)
        {
            Undo();
            Rebuild();

            if (_document.ToJson() != before)
            {
                problems.Add("自检组名: 造组测试收尾时撤销没回到原样");
            }
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>
    /// 输入新名字后点到别处＝定名。只挂 LostFocus 是不够的：这一版按不持焦点的区域不会把
    /// 焦点从 TextBox 拿走（10-25 他报的"输入新名字后点击其他地方不会自动应用"）。
    /// </summary>
    private bool SelfTestGroupRenameByOutsidePress(List<string> problems, CanvasNode group)
    {
        var originalLabel = group.Label;

        // 名字一定要和原值不同，否则 CommitRename 不动手，收尾那一发 Undo 会撤掉别的东西。
        var wanted = originalLabel == "自检组名" ? "自检组名·二" : "自检组名";

        BeginRenameGroup(group);

        if (_groupRenameBox is null)
        {
            problems.Add("自检组名: 双击组名后没出现改名框");

            return false;
        }

        if (GroupRenamePressOutside(_groupRenameBox))
        {
            problems.Add("自检组名: 按在改名框里也被当成点了别处");
            CloseRename();

            return false;
        }

        _groupRenameBox.Text = wanted;

        if (!CommitGroupRenameIfOutside(BoardArea))
        {
            problems.Add("自检组名: 按在板面上没收笔画名");

            return false;
        }

        if (NodeOf(group.Id)?.Label != wanted)
        {
            problems.Add($"自检组名: 点到别处后新名字没应用（读回 {NodeOf(group.Id)?.Label ?? "空"}，原来 {originalLabel ?? "空"}）");

            return false;
        }

        Diagnostics.Write($"selftest 组名: 输入“{wanted}”后点板面＝定名（原 {originalLabel ?? "空"}）");

        return true;
    }
}
