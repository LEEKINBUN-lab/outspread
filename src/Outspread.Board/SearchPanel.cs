using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// 查找那一层：一格输入＋一份命中列表，点一行就把相机和选中放到那一格上。
/// </summary>
/// <remarks>
/// <para>
/// 这块板子的前提是"随手贴"，所以它最容易翻车的地方不是贴不进去，是贴进去三段话之后
/// 没有任何办法把它们找回来。在这层之前，板上唯一的检索是 `@t`/`@d` 两枚标记——
/// 那要求你贴的时候就想好了它将来会被怎么找。
/// </para>
/// <para>
/// 命中行为什么不带"把命中的那几个字加粗/染色"：这一套浮层只有两种线（正文墨、禁用墨），
/// 这正是他第三次打回"从字体、排版到动画都完全不一样"之后钉死的。为强调开第三种线，
/// 就是把刚收回去的多样性又请回来一次。所以左列是原文摘要，右列只有那一格是什么。
/// </para>
/// <para>
/// 这层占键盘的时候板子的键位一律不进（不变量 1），Esc 是纯放弃（不变量 5）：
/// 跳转只动相机与选中，不动文档，所以没有任何东西需要撤销。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>一屏够读的行数。再多就不是"找东西"而是"把板子列一遍"了。</summary>
    private const int SearchRowsShown = 12;

    private bool _searchOpen;

    private IReadOnlyList<BoardSearch.Hit> _searchHits = [];

    private int _searchCursor = -1;

    private TextBox _searchBox = null!;

    /// <summary>建窗时搭一次：以后只有文字和行数在变，结构不变，就不会长出第二、第三种组件。</summary>
    private void BuildSearchSurface()
    {
        _searchBox = PanelKit.Input("查找 · 空格＝都要命中，Enter 下一处，Esc 收起");

        SearchBody.Children.Add(_searchBox);

        _searchBox.TextChanged += (_, _) => RunSearch();
        _searchBox.KeyDown += OnSearchBoxKeyDown;
    }

    private void OpenSearch()
    {
        SetRestorePanel(false);
        CloseChordPage();
        SetSearchPanel(true);

        // 焦点必须真的落进那一格：这层的意义是"开始打字就在找"，
        // 而板子持焦点时打进去的每一个字都会变成板子的键位。
        _searchBox.Focus(FocusState.Programmatic);

        RunSearch();
    }

    private void SetSearchPanel(bool open)
    {
        if (open)
        {
            ChromeStyling.Ensure(NoteStyling.IsDark);
            ChromeStyling.ApplyFloatingChrome(SearchPanel);
        }

        SearchPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        _searchOpen = open;
    }

    private void CloseSearchPanel()
    {
        if (!_searchOpen)
        {
            return;
        }

        SetSearchPanel(false);

        // 收起之后把焦点还给板子，否则下一个键会打在已经不存在的输入框上。
        BoardArea.Focus(FocusState.Programmatic);
    }

    /// <summary>面板自己的皮上按一发＝收起，并且不许穿到板子上去选中后面那格。</summary>
    private void OnSearchPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseSearchPanel();
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                CloseSearchPanel();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                JumpToHit(_searchCursor + (IsShiftDown() ? -1 : 1));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F3:
                JumpToHit(_searchCursor + 1);
                e.Handled = true;
                break;
        }
    }

    private static bool IsShiftDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void RunSearch()
    {
        var query = _searchBox.Text;

        // 44 字窗口：命中左右各留一段上下文，整行又不会长到把命中裁出可视宽度。
        _searchHits = BoardSearch.Find(_document, query, window: 44);
        _searchCursor = -1;

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchScroll.Content = null;

            return;
        }

        if (_searchHits.Count == 0)
        {
            SearchScroll.Content = PanelKit.Host(PanelKit.Line("板上没有任何便签包含这些文字"));

            return;
        }

        var rows = new List<FrameworkElement>();

        for (var i = 0; i < Math.Min(SearchRowsShown, _searchHits.Count); i++)
        {
            var hit = _searchHits[i];
            var index = i;

            rows.Add(
                PanelKit.SearchRow(
                    hit.Snippet,
                    hit.MatchStart,
                    hit.MatchLength,
                    KindWord(hit.Kind),
                    () => JumpToHit(index)));
        }

        if (_searchHits.Count > SearchRowsShown)
        {
            rows.Add(PanelKit.Line($"还有 {_searchHits.Count - SearchRowsShown} 处（再按 Enter 继续跳）"));
        }

        SearchScroll.Content = PanelKit.Host(rows.ToArray());
    }

    /// <summary>跳到第 index 处命中（越界就绕回）：相机居中、只选那一格。</summary>
    private void JumpToHit(int index)
    {
        if (_searchHits.Count == 0)
        {
            Say("当前没有可跳转的匹配项");

            return;
        }

        _searchCursor = ((index % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;

        var hit = _searchHits[_searchCursor];

        var node = _document.Nodes.FirstOrDefault(candidate => candidate.Id == hit.NodeId);

        if (node is null)
        {
            // 列表是上一发按键建的，板子在这之间可能被外面改过——旧 id 不猜，直接说。
            Say("目标便签已不在板面上，请重新查找");
            RunSearch();

            return;
        }

        CenterOn(node);

        ClearSelection();
        _selection.Add(node.Id);

        Rebuild();
        ProjectSelection();
        ShakeArrival(node);
        SaveSoon();

        Say($"第 {_searchCursor + 1}/{_searchHits.Count} 项");
    }

    /// <summary>
    /// 把相机挪到那一格正中间，缩放不动——"看到它在哪"不等于"把它放大"。
    /// 查找面板开着时"正中"要让开它：原来按整块板面居中，目标正好藏在面板背后，
    /// 点了行屏幕上一点变化都看不到（他 10-24 报的"搜索不好用"就是这个）。
    /// </summary>
    private void CenterOn(CanvasNode node)
    {
        var top = 0d;

        if (_searchOpen)
        {
            top = SearchPanel.Margin.Top + Math.Max(0, SearchPanel.ActualHeight) + 12;
        }

        var band = Math.Max(80, BoardArea.ActualHeight - top);

        _view.PanX = BoardArea.ActualWidth / 2 - (node.X + node.Width / 2) * _view.Zoom;
        _view.PanY = (top + (band / 2)) - (node.Y + node.Height / 2) * _view.Zoom;

        ArrangeAll();
    }

    /// <summary>
    /// 跳过去了：让那一格自己抖一下（他 10-24 在两种做法里选了 A 档）。
    /// 卡片重铸过一次，所以要在 Rebuild 之后拿新的那枚 visual。
    /// </summary>
    private void ShakeArrival(CanvasNode node)
    {
        if (_realized.TryGetValue(node.Id, out var visual))
        {
            visual.ShakeArrival();
        }
    }

    /// <summary>右列那一个词：命中来自哪一型格子。<see cref="CanvasNode.NameOfKind"/> 给的是
    /// 格式里的令牌（text/file/link），那是校验文案要用的，不是给人读的那一列。</summary>
    private static string KindWord(CanvasNodeKind kind) => kind switch
    {
        CanvasNodeKind.File => "文件",
        CanvasNodeKind.Link => "链接",
        CanvasNodeKind.Group => "分组",
        _ => "正文",
    };

    /// <summary>
    /// 自检用的那半句话：从第一格有正文的格子里取第一个不是标记行的行的前几个字。
    /// 取自板子本身，所以换一块板子它仍然在问同一个问题——"搜板上有的东西搜得到吗"。
    /// </summary>
    private string ProbeTerm()
    {
        foreach (var node in BoardNotes())
        {
            // 文件格与链接格的 Text 是 null（规范里那两型没有正文），所以先收下再看。
            if (node.Text is not { Length: > 0 } text)
            {
                continue;
            }

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();

                // @d/@t 那两行是标记，不是"用户在找的那种内容"；标题的井号会跟着进查询串。
                if (trimmed.Length >= 4 && !trimmed.StartsWith('@') && !trimmed.StartsWith('#'))
                {
                    return trimmed[..4];
                }
            }
        }

        return string.Empty;
    }

    // -------------------------------------------------------------------------
    // 相机：看到全部 / 看到选中
    // -------------------------------------------------------------------------

    /// <summary>
    /// 相机只认"真占地方、真人看得见"的格子：0 宽 0 高、坐标不是有限数的是幽灵；组框是成员
    /// 派生出来的，空组根本画不出来，却会把包围盒拽到空处——它们是"回到 100% 一片空白"
    /// 的真凶（10-25 他第二次报：测试板上有两个空组在 x≈435、便签却从 1684 起）。
    /// </summary>
    private static bool IsPlaceable(CanvasNode node)
        => node.Kind != CanvasNodeKind.Group
            && double.IsFinite(node.X) && double.IsFinite(node.Y)
            && double.IsFinite(node.Width) && double.IsFinite(node.Height)
            && node.Width > 0.5 && node.Height > 0.5;

    private void ZoomToFit() => Frame(_document.Nodes.Where(IsPlaceable).ToList());

    private void ZoomToSelection()
    {
        var members = _document.Nodes.Where(node => _selection.Contains(node.Id) && IsPlaceable(node)).ToList();

        if (members.Count == 0)
        {
            // 不合法的命令要明着拒绝（不变量 4）：说清它要什么、现在有什么。
            Say("“看到选中”需要先选中便签，当前选中 0 个便签");

            return;
        }

        Frame(members);
    }

    /// <summary>
    /// 把这些框收进视野，缩放取"两个轴都装得下"的那一档，并且只缩不放超过 100%——
    /// 这两条命令要的是看清全局，不是把一格放大到糊。
    /// </summary>
    private void Frame(IReadOnlyList<CanvasNode> members)
    {
        members = members.Where(IsPlaceable).ToList();

        if (members.Count == 0)
        {
            Say("板面为空，已回到 100%");
            ResetView();

            return;
        }

        var left = members.Min(node => node.X);
        var top = members.Min(node => node.Y);

        var width = Math.Max(members.Max(node => node.X + node.Width) - left, 1);
        var height = Math.Max(members.Max(node => node.Y + node.Height) - top, 1);

        var pad = Chrome.Radius * 2;

        var zoom = Math.Min(
            (BoardArea.ActualWidth - pad * 2) / width,
            (BoardArea.ActualHeight - pad * 2) / height);

        _view.Zoom = Math.Clamp(Math.Min(zoom, 1), MinZoom, MaxZoom);

        _view.PanX = BoardArea.ActualWidth / 2 - (left + width / 2) * _view.Zoom;
        _view.PanY = BoardArea.ActualHeight / 2 - (top + height / 2) * _view.Zoom;

        ArrangeAll();
        ProjectSelection();
        SaveSoon();
    }


    /// <summary>
    /// 只差几像素的手动尺寸按误触修正；断头线（一端便签已删）在打开/删除时真正清掉。
    /// </summary>
    /// <summary>
    /// 内容确实放不下时：底部渐隐要亮，而且卡里要真的多画半行，否则渐隐压在空白上，
    /// 屏幕上还是"看不出有没有显示完"（他 10-25 报的第二条）。
    /// </summary>
    private void SelfTestOverflowCue(List<string> problems)
    {
        var saved = (_view.PanX, _view.PanY, _view.Zoom);
        var note = _document.Add(CanvasNodeKind.Text, 40000, 40000, 260, 90);
        note.Text = "快速\n所有\n再看";
        _view.MarkManualSize(note.Id);

        try
        {
            _view.Zoom = 1.4;
            _view.PanX = 24 - (note.X * 1.4);
            _view.PanY = 16 - (note.Y * 1.4);

            // 先按 90 高放一次（已有便签的加载状态），再只改高度、不改文字——复现"已有便签要重新编辑才出现渐隐"。
            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            note.Height = 68;
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(note.Id, out var card))
            {
                problems.Add("自检溢出处: 140% 下没有画出那枚便签");

                return;
            }

            var state = card.WindowState();
            var fade = FindDescendant<Microsoft.UI.Xaml.Shapes.Rectangle>(card, "OverflowFadeBelow");

            if (!state.HasMore)
            {
                problems.Add("自检溢出处: 68 高的三行便签没被判定为还有内容");
            }

            if (fade?.Visibility != Visibility.Visible)
            {
                problems.Add("自检溢出处: 内容放不下时底部渐隐没有亮");
            }

            if (card.RenderedMaxLines <= state.Visible)
            {
                problems.Add(
                    $"自检溢出处: 渐隐亮着但只画了 {card.RenderedMaxLines} 行（可见 {state.Visible}）——渐隐压在空白上");
            }
            else if (card.OverflowFadeBelowShape is not (>= 0.95, >= 0.45))
            {
                var shape = card.OverflowFadeBelowShape;

                problems.Add(
                    $"自检溢出处: 渐隐带高 {shape.HeightInLines:0.###} 行、抬进最后一行 {shape.OverLastLineInLines:0.###} 行——"
                    + "没吃进最后一行的字，看着像没生效");
            }
            else
            {
                var shape = card.OverflowFadeBelowShape;

                Diagnostics.Write(
                    $"selftest 溢出处: 68 高便签可见 {state.Visible} 行、多画 {card.RenderedMaxLines - state.Visible} 行半行，"
                    + $"底部渐隐高 {shape.HeightInLines:0.###} 行、抬进最后一行 {shape.OverLastLineInLines:0.###} 行");
            }
        }
        finally
        {
            _view.UnmarkManualSize(note.Id);
            _document.Remove(note.Id);
            _view.PanX = saved.PanX;
            _view.PanY = saved.PanY;
            _view.Zoom = saved.Zoom;

            Rebuild();
            ArrangeAll();
        }
    }

    /// <summary>
    /// 只差几像素的手动尺寸按误触修正；断头线（一端便签已删）在打开/删除时真正清掉。
    /// </summary>
    private void SelfTestRepairs(List<string> problems)
    {
        var note = _document.Add(CanvasNodeKind.Text, 40000, 40000, 260, 60);
        note.Text = "快速\n所有";

        var need = MeasureTextHeight(note.Text, note.Width, _view.FontSizeFor(note.Id));
        note.Height = need - 5;
        _view.MarkManualSize(note.Id);

        var repaired = RepairTinyManualShrinks();

        if (repaired < 1 || Math.Abs(note.Height - need) > 0.001 || _view.IsManualSize(note.Id))
        {
            problems.Add(
                $"自检修复: 只差 5px 的手动尺寸没被修正（高 {note.Height:0.#}，应 {need:0.#}，手动 {_view.IsManualSize(note.Id)}）");
        }

        _view.UnmarkManualSize(note.Id);
        _document.Remove(note.Id);

        var left = _document.Add(CanvasNodeKind.Text, 40100, 40000, 200, 60);
        var right = _document.Add(CanvasNodeKind.Text, 40400, 40000, 200, 60);
        var edge = _document.AddEdge(left.Id, right.Id);
        _document.Remove(right.Id);

        var removed = PurgeOrphanEdges();

        if (removed < 1 || _document.Edges.Any(item => item.Id == edge.Id))
        {
            problems.Add("自检修复: 删掉一端便签后，指向它的连线没被清掉");
        }

        _document.Remove(left.Id);
        _document.RemoveEdge(edge.Id);

        if (problems.Count == 0)
        {
            Diagnostics.Write("selftest 修复: 5px 误触尺寸回到自动高度；断头线在删除后清掉");
        }
    }

    /// <summary>
    /// 两行便签在 160% 这样的小数缩放下也必须显示两行：所有尺寸都乘过 zoom，floor 之前若不留
    /// epsilon，42/21 会算成 1.9999999999999998，第二行整行消失、渐隐却亮着（10-25 他报的）。
    /// </summary>
    private void SelfTestTwoLineNoteAtFractionalZoom(List<string> problems)
    {
        var saved = (_view.PanX, _view.PanY, _view.Zoom);
        var note = _document.Add(CanvasNodeKind.Text, 40000, 40000, 260, 76);
        note.Text = "快速\n所有";

        try
        {
            _view.Zoom = 1.6;
            _view.PanX = 24 - (note.X * 1.6);
            _view.PanY = 16 - (note.Y * 1.6);

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(note.Id, out var card))
            {
                problems.Add("自检两行缩放: 160% 下没有画出那枚便签");
            }
            else
            {
                var state = card.WindowState();
                var fade = FindDescendant<Microsoft.UI.Xaml.Shapes.Rectangle>(card, "OverflowFadeBelow");

                if (state.Visible < 2 || state.Total < 2)
                {
                    problems.Add(
                        $"自检两行缩放: 160% 下两行便签只显示 {state.Visible}/{state.Total} 行（浮点取整把第二行吃掉）");
                }

                if (fade?.Visibility == Visibility.Visible)
                {
                    problems.Add("自检两行缩放: 放得下的便签在 160% 下仍压着底部渐隐");
                }
                else if (state.Visible >= 2)
                {
                    Diagnostics.Write($"selftest 两行缩放: 160% 下 76 高便签显示 {state.Visible}/{state.Total} 行、底部渐隐收着");
                }
            }
        }
        finally
        {
            _document.Remove(note.Id);
            _view.PanX = saved.PanX;
            _view.PanY = saved.PanY;
            _view.Zoom = saved.Zoom;

            Rebuild();
            ArrangeAll();
        }
    }

    /// <summary>
    /// 相机三问：0 尺寸的幽灵格与空组拉不拉远"看到全部"；fit 有没有把内容摆在正中；
    /// "回到 100%"以后屏幕上有没有内容、左上角是不是贴着内容。
    /// </summary>
    private void SelfTestCameraFraming(List<string> problems)
    {
        var content = _document.Nodes.Where(IsPlaceable).ToList();

        if (content.Count == 0)
        {
            problems.Add("自检相机: 板上没有可画的格子，这一发不适用");

            return;
        }

        var saved = (_view.PanX, _view.PanY, _view.Zoom);
        var ghost = _document.Add(CanvasNodeKind.Text, -50000, -50000, 0.1, 0.1);

        // 空组画不出来，却曾经把包围盒拽到空处（他 10-25 那块"测试"板上就是两个空组）。
        var ghostGroup = _document.Add(CanvasNodeKind.Group, -60000, -60000, 900, 700);

        try
        {
            ZoomToFit();
            var withGhost = _view.Zoom;

            var fitLeft = content.Min(node => _view.ToScreenX(node.X));
            var fitTop = content.Min(node => _view.ToScreenY(node.Y));
            var fitRight = content.Max(node => _view.ToScreenX(node.Right));
            var fitBottom = content.Max(node => _view.ToScreenY(node.Bottom));

            _document.Remove(ghost.Id);
            _document.Remove(ghostGroup.Id);
            ZoomToFit();
            var without = _view.Zoom;

            if (Math.Abs(withGhost - without) > 0.001)
            {
                problems.Add($"自检相机: 一个 0 尺寸幽灵格/空组把『看到全部』从 {without:0.###} 拉到了 {withGhost:0.###}");
            }

            if (withGhost < 0.15)
            {
                problems.Add($"自检相机: 『看到全部』只剩 {withGhost:0.###} 的缩放，屏幕基本是空的");
            }

            var leftMargin = fitLeft;
            var rightMargin = BoardArea.ActualWidth - fitRight;
            var topMargin = fitTop;
            var bottomMargin = BoardArea.ActualHeight - fitBottom;

            if (Math.Abs(leftMargin - rightMargin) > 1.5 || Math.Abs(topMargin - bottomMargin) > 1.5)
            {
                problems.Add(
                    $"自检相机: 『看到全部』没有把内容摆在正中（左右留白 {leftMargin:0.#}/{rightMargin:0.#}，" +
                    $"上下 {topMargin:0.#}/{bottomMargin:0.#}）——空组把包围盒拽歪了");
            }

            if (fitLeft < -0.5 || fitTop < -0.5
                || fitRight > BoardArea.ActualWidth + 0.5 || fitBottom > BoardArea.ActualHeight + 0.5)
            {
                problems.Add($"自检相机: 『看到全部』以后仍有内容在屏幕外（{fitLeft:0.#},{fitTop:0.#}–{fitRight:0.#},{fitBottom:0.#}）");
            }

            _view.PanX = 40000;
            _view.PanY = 40000;
            _view.Zoom = 0.29;

            // 走按钮那一条真路径：比例钮以前只改缩放，视野还停在空白处（他 10-25 第二次报的）。
            OnZoomActualSize(this, new RoutedEventArgs());

            var resetLeft = content.Min(node => _view.ToScreenX(node.X));
            var resetTop = content.Min(node => _view.ToScreenY(node.Y));

            if (Math.Abs(resetLeft - 24) > 1 || Math.Abs(resetTop - 16) > 1)
            {
                problems.Add(
                    $"自检相机: 回到 100% 没对准内容左上角（读到 {resetLeft:0.#},{resetTop:0.#}，应为 24,16）" +
                    "——空组/幽灵格还在算进包围盒");
            }

            var visible = content.Any(node =>
            {
                var left = _view.ToScreenX(node.X);
                var top = _view.ToScreenY(node.Y);
                var width = node.Width * _view.Zoom;
                var height = node.Height * _view.Zoom;

                return left < BoardArea.ActualWidth && left + width > 0
                    && top < BoardArea.ActualHeight && top + height > 0;
            });

            if (!visible)
            {
                problems.Add("自检相机: 回到 100% 以后屏幕上没有任何内容（起点是写死的坐标）");
            }
            else
            {
                Diagnostics.Write(
                    $"selftest 相机: 幽灵格与空组不改 fit（{withGhost:0.###} 对 {without:0.###}）、" +
                    $"fit 居中（左右 {leftMargin:0.#}/{rightMargin:0.#}、上下 {topMargin:0.#}/{bottomMargin:0.#}）、" +
                    $"回到 100% 内容左上角在 ({resetLeft:0.#},{resetTop:0.#})");
            }
        }
        finally
        {
            if (_document.Find(ghost.Id) is not null)
            {
                _document.Remove(ghost.Id);
            }

            if (_document.Find(ghostGroup.Id) is not null)
            {
                _document.Remove(ghostGroup.Id);
            }

            _view.PanX = saved.PanX;
            _view.PanY = saved.PanY;
            _view.Zoom = saved.Zoom;

            ArrangeAll();
            ProjectSelection();
        }
    }
}
