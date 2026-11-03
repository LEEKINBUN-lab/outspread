using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The filter: what the board reads out of everyone's text — overdue, due today, unreadable dates,
/// and the tags in use — and the lens those readings apply.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a mark nobody can see is indistinguishable from a mark that was not
/// understood. Before it, writing a due date into a note changed the text and nothing else, so the
/// user had no way to learn whether the board had registered the date at all (invariant 3 of
/// docs/note-lifecycle.md).
/// </para>
/// <para>
/// It used to sit permanently in the bottom-left corner as a row of outlined chips with counts. That
/// was removed: the card's own edge bar already says "this one has a date", so the second place it
/// was said was a data summary standing in for the interface, always on screen, whether or not
/// anyone was asking. Now the readings live behind one funnel next to the zoom buttons, and the only
/// thing that appears unasked is the banner — and that appears only while a lens is actually on,
/// which is the state a board cannot show by itself.
/// </para>
/// <para>
/// The rows filter, they do not hide: a filtered-out note goes to 22% opacity and stays exactly where
/// it was, because moving notes to satisfy a lens would make the document look edited and would put
/// a step in the undo stack that the user never asked for.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>
    /// A note made by a double-tap, until it earns text. Kept with the other view-only state:
    /// nothing about it belongs in the document.
    /// </summary>
    private string? _provisionalId;

    private bool _filterPanelOpen;

    /// <summary>横幅那一行：建窗时搭好的菜单行，之后只换字。</summary>
    private PanelKit.SheetRow _bannerRow = null!;

    /// <summary>
    /// The property lines the editor is not showing: what was on the note's mark lines when editing
    /// began, plus anything absorbed while typing. The buffer the user sees is the body alone, and the
    /// text written back to the file is both — which is what makes "回车以后它从文本栏消失、变成左下角
    /// 那枚牌子" true without the mark leaving the document.
    /// </summary>
    private string _editorProps = string.Empty;

    /// <summary>
    /// Enter on a line that is nothing but mark tokens absorbs that line into
    /// <see cref="_editorProps"/> and takes it out of the buffer — so the token disappears where it
    /// was typed and shows up as a chip. Any other Enter is a normal newline and is left alone.
    /// </summary>
    /// <summary>
    /// 编辑框里的回车。Ctrl＋Enter＝收笔；标记行＝收进隐藏块（收下后仍留在编辑态，方便接着补日期或序号）。
    /// 返回 true＝这一发被吃掉（不插换行）。
    /// </summary>
    private bool HandleEditorEnter()
    {
        if (_editingId is null)
        {
            return false;
        }

        // 改表格里那一格：回车＝收下这一格（单行输入框，接着换行没有意义）。
        if (_editingCell is not null)
        {
            CloseEditor(commit: true);

            return true;
        }

        if (KeyDownIs(Windows.System.VirtualKey.Control))
        {
            CloseEditor(commit: true);

            return true;
        }

        if (AbsorbPropertyLine())
        {
            return true;
        }

        // 换行照插（不吃这一发，交给输入框自己），但排版要等 TextChanged 落定之后再量一次。
        DispatcherQueue.TryEnqueue(GrowEditorToFit);

        return false;
    }

    private bool AbsorbPropertyLine()
    {
        if (_editor is not { } editor)
        {
            return false;
        }

        var text = editor.Text;
        var caret = Math.Min(editor.SelectionStart, text.Length);
        var start = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, caret - 1)) + 1;
        var end = start;

        while (end < text.Length && text[end] != '\n' && text[end] != '\r')
        {
            end++;
        }

        var line = text[start..end].Trim();

        if (!Tags.LooksLikePropertyLine(line))
        {
            return false;
        }

        _editorProps = _editorProps.Length == 0 ? line : _editorProps + "\n" + line;

        // 连着它后面那个换行一起吃掉，否则原地留一个空行。
        var stop = end < text.Length && text[end] == '\r' ? end + 1 : end;

        if (stop < text.Length && text[stop] == '\n')
        {
            stop++;
        }

        editor.Text = text[..start] + text[stop..];
        editor.Select(start, 0);

        PaintEditorChips();

        Say($"已收纳标记行：{line}");

        return true;
    }

    private void OnFilterClicked(object sender, RoutedEventArgs e)
    {
        SetFilterPanel(!_filterPanelOpen);
    }

    private void SetFilterPanel(bool open)
    {
        _filterPanelOpen = open;
        FilterPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (open)
        {
            WriteFilterRows();
        }
    }

    private void CloseFilterPanel()
    {
        if (_filterPanelOpen)
        {
            SetFilterPanel(false);
        }
    }

    /// <summary>
    /// A press on the panel's own chrome — between the rows — means "this layer is finished with":
    /// it closes and does not reach the board underneath, so it neither deselects nor starts a pan.
    /// A press on a row never comes here; the button marks it handled first, which is why clicking a
    /// row leaves the panel open for the next choice.
    /// </summary>
    private void OnFilterPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseFilterPanel();
    }

    /// <summary>
    /// Refreshes the panel's rows and the banner. Called from the same rebuild the cards are painted
    /// in, so the counts cannot be a snapshot from whenever the board was opened.
    /// </summary>
    private void RebuildFilterPanel()
    {
        // The panel's brushes come from the theme pass; a rebuild can run before ArrangeAll does it.
        ChromeStyling.Ensure(NoteStyling.IsDark);
        ChromeStyling.ApplyFloatingChrome(FilterPanel);
        ChromeStyling.ApplyFloatingChrome(FilterBanner);

        if (_filterPanelOpen)
        {
            WriteFilterRows();
        }

        UpdateFilterBanner();
    }

    /// <summary>
    /// The panel's rows, written from the readings of this frame. The surface itself is the right-click
    /// menu's control — see <see cref="PanelKit"/> — so "筛选" and "日期" are disabled rows, not a second
    /// type of line, and the counts sit in the menu's own hint column.
    /// </summary>
    private void WriteFilterRows()
    {
        var rows = new List<PanelKit.SheetRow> { PanelKit.Line("日期") };

        var notes = _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();
        var states = notes.Select(node => NoteMarks.StateOf(node.Text, _view.Today)).ToList();

        var dated = states.Count(state => state != NoteMarks.DueState.None);

        if (dated == 0)
        {
            rows.Add(PanelKit.Line("板上还没有日期"));
        }
        else
        {
            foreach (var state in new[] { NoteMarks.DueState.Late, NoteMarks.DueState.Today, NoteMarks.DueState.Upcoming, NoteMarks.DueState.Malformed })
            {
                var count = states.Count(reading => reading == state);

                // 0 的档不占一行——面板不是统计报表；但正被这一档筛着时必须留着，否则"清除"这一格消失了。
                if (count > 0 || _view.DueFilter == state)
                {
                    var wanted = state;

                    rows.Add(PanelKit.Row(
                        $"{(NoteIsFiltered(state) ? "✓ " : string.Empty)}{DueLabel(wanted)}",
                        count.ToString(),
                        () => SetDueFilter(wanted)));
                }
            }
        }

        // 完成那一档（@b1）：两行，不是三行——"没写 @b"就是还没做完，所以它归进「还没做完」那一档，
        // 而不是单开一行"无标记"。三档会把人教成"要先写 @b0 才看得见"，那是语法泄漏。
        var doneCount = notes.Count(node => Tags.Read(node.Text).Done == true);
        var openCount = notes.Count - doneCount;

        rows.Add(PanelKit.Line("完成"));

        if (doneCount == 0 && _view.DoneFilter is null)
        {
            rows.Add(PanelKit.Line("板上还没有完成标记（写 @b1 或点牌子切换）"));
        }
        else
        {
            rows.Add(PanelKit.Row(
                $"{(_view.DoneFilter == true ? "✓ " : string.Empty)}已完成",
                doneCount.ToString(),
                () => SetDoneFilter(true)));

            rows.Add(PanelKit.Row(
                $"{(_view.DoneFilter == false ? "✓ " : string.Empty)}还没做完",
                openCount.ToString(),
                () => SetDoneFilter(false)));
        }

        var tally = NoteMarks.Tally(notes.Select(node => node.Text).ToList());

        rows.Add(PanelKit.Line("标签"));

        if (tally.Count == 0)
        {
            rows.Add(PanelKit.Line("板上还没有标签"));
        }

        foreach (var (tag, count) in tally)
        {
            var wanted = tag;
            var on = string.Equals(_view.TagFilter, wanted, StringComparison.Ordinal);

            rows.Add(PanelKit.Row($"{(on ? "✓ " : string.Empty)}{wanted}", count.ToString(), () => SetTagFilter(wanted)));
        }

        if (_view.Filtering)
        {
            // 面板里筛上的，就得在面板里能解开。原来只有板顶那条横幅能清，
            // 而横幅在面板打开时被面板自己压住了一半。
            rows.Add(PanelKit.Row("清除筛选", "全部", ClearFilters));
        }

        FilterScroll.Content = PanelKit.Host(rows.ToArray());
    }

    /// <summary>
    /// The due states in the words the board uses everywhere else — the enum names are English and
    /// would read as a bug report on a Chinese surface.
    /// </summary>
    private static string DueLabel(NoteMarks.DueState state) => state switch
    {
        NoteMarks.DueState.Late => "逾期",
        NoteMarks.DueState.Today => "今天",
        NoteMarks.DueState.Upcoming => "以后",
        NoteMarks.DueState.Malformed => "日期读不出",
        _ => "无日期",
    };

    private bool NoteIsFiltered(NoteMarks.DueState state) => _view.DueFilter == state;

    /// <summary>
    /// The narrow banner that only exists while a lens is on: what is being hidden, how much of the
    /// board survives it, and one click back to everything.
    /// </summary>
    private void UpdateFilterBanner()
    {
        if (!_view.Filtering)
        {
            FilterBanner.Visibility = Visibility.Collapsed;

            return;
        }

        var notes = _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();

        var shown = notes.Count(node =>
        {
            var marks = Tags.Read(node.Text, _view.Today);

            return NoteMarks.Shows(NoteMarks.StateOf(marks, _view.Today), marks.Tags, marks.Done, _view.TagFilter, _view.DueFilter, _view.DoneFilter);
        });

        // 内插里不放条件式：{ } 是属性模式的记号，编译器会先把它当成插补的结束符。
        string what = "没有筛选";

        if (_view.TagFilter is { Length: > 0 } tag)
        {
            what = $"只看 {tag}";
        }
        else if (_view.DueFilter is { } state)
        {
            what = $"只看{DueLabel(state)}的便签";
        }
        else if (_view.DoneFilter is { } done)
        {
            what = done ? "只看已完成的便签" : "只看还没做完的便签";
        }

        // 折进组里的那几格必须有一句，否则筛选看起来像删了便签。框上那行标签会被卡片盖住
        // （框贴着成员收，没给标题留边），所以这句话由这条一直在屏幕上的横幅来说。
        var folded = FoldedAwayCount;

        _bannerRow.Text = $"{what} · {notes.Count} 个便签中的 {shown} 个"
            + (folded > 0 ? $" · {folded} 个便签折叠进了组" : string.Empty)
            + " · 点这清除";
        FilterBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A chip on a card was clicked: take that span out of the note's text. This is a document edit
    /// (one undoable step, saved), not a view state — the mark was written into the text, so removing
    /// it has to go back through the same door.
    /// </summary>
    private void RemoveMarkFrom(string nodeId, string token)
    {
        if (NodeOf(nodeId) is not { } node || node.Kind != CanvasNodeKind.Text)
        {
            return;
        }

        var without = Tags.Without(node.Text, token);

        if (string.Equals(without, node.Text, StringComparison.Ordinal))
        {
            // 删不掉却说删了，比不删更糟。
            Say($"正文中未找到“{token}”，未作修改");
            return;
        }

        BeginGesture("去掉一枚标记");

        node.Text = without;
        FitNoteHeight(node);

        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"已移除标记 {token} · 可按 Ctrl＋Z 撤销");
    }

    /// <summary>
    /// 把一枚标记从隐藏属性块挪进编辑器正文（单独一行）并选中它，等用户改写。
    /// 日期/标签/序号都只是 token，改法一致；提交时解析器会把它重新认成标记。
    /// </summary>
    private void BeginMarkEdit(string token)
    {
        if (_editor is null || string.IsNullOrEmpty(token))
        {
            return;
        }

        var lines = _editorProps
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!lines.Any(line => line.Contains(token, StringComparison.Ordinal)))
        {
            // 已经在正文里（或者标记已经不在了）：编辑器开着，让用户自己改。
            _editor.Focus(FocusState.Programmatic);

            return;
        }

        _editorProps = string.Join('\n', lines
            .Select(line => line.Replace(token, string.Empty, StringComparison.Ordinal).Trim())
            .Where(line => line.Length > 0));

        var body = _editor.Text;
        var lead = body.Length > 0 && !body.EndsWith('\n') ? "\n" : string.Empty;

        _editor.Text = body + lead + token;
        _editor.Focus(FocusState.Programmatic);
        _editor.SelectionStart = body.Length + lead.Length;
        _editor.SelectionLength = token.Length;

        PaintEditorChips();
    }

    /// <summary>
    /// The chips inside the editor. He asked for this specifically: pressing Enter on a tag must make
    /// it leave the text field <b>then</b>, not after the edit finishes — so the strip that will end up
    /// on the card is shown in the editor too, and deleting one there edits the same pending block.
    /// </summary>
    private StackPanel? _editorChips;

    private void PaintEditorChips()
    {
        if (_editorChips is null || _editor is null)
        {
            return;
        }

        var marks = Tags.Read(_editorProps, _view.Today);
        var size = NoteType.ChipFont(_view.Zoom);

        _editorChips.Children.Clear();

        // × 那块盖字的底要按**这张卡自己的面色**算，否则在彩色卡上它会透出底下那几个字的影子。
        var editingSurface = NoteStyling.SurfaceFor(_document.Nodes.FirstOrDefault(node => node.Id == _editingId)?.Color);

        foreach (var chip in NoteMarks.Chips(marks, _view.Today))
        {
            var source = chip.Source;

            _editorChips.Children.Add(ChromeStyling.Chip(
                chip,
                size,
                NoteStyling.InkFor(null, NoteStyling.SurfaceFor(null)),
                editingSurface,
                () =>
                {
                    _editorProps = Tags.Without(_editorProps, source);

                    PaintEditorChips();
                },
                () => BeginMarkEdit(source),
                null,
                "点一下＝改这枚标记；点 × 去掉"));
        }

        _editorChips.Visibility = _editorChips.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 贴在编辑器左下角：与卡片上那排牌子的位置是同一个地方，看着才是同一件事。
        // 顶边用卡片那条牌子带（ChipBand）自己的数：编辑框的正文也让出同一截，两边才咬得上；
        // 低缩放下牌子会缩到 16 像素以下，旧写法用 max(16, …) 会把它往上顶进正文里。
        var pad = Math.Clamp(NoteTypography.For(_view.FontSizeFor(_editingId ?? string.Empty)).Padding * _view.Zoom, 0, 60);

        Canvas.SetLeft(_editorChips, Canvas.GetLeft(_editor) + pad);
        Canvas.SetTop(_editorChips, Canvas.GetTop(_editor) + _editor.Height - pad - NoteType.ChipBand(_view.Zoom));
    }

    /// <summary>
    /// 编辑态里的回车：收下标记行（整行只有 token）要吃掉这一发、留在编辑态。
    /// 旧的"点牌→只改这枚标记→回车收笔"那一趟随"点牌＝编辑"一起退了（他 11-03 拍：点牌＝聚焦）。
    /// </summary>
    private void SelfTestMarkEditEnter(List<string> problems)
    {
        var target = BoardNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text)
            .Select(node => (Node: node, Chips: NoteMarks.Chips(Tags.Read(node.Text, _view.Today), _view.Today)))
            .FirstOrDefault(pair => pair.Chips.Count > 0);

        if (target.Node is null)
        {
            Diagnostics.Write("selftest 标记回车: 板上没有带标记的便签，这一发不适用");

            return;
        }

        var before = _document.ToJson();

        BeginEdit(target.Node.Id);
        _editor!.Text = "@t自检";
        _editor.SelectionStart = _editor.Text.Length;

        if (!HandleEditorEnter())
        {
            problems.Add("自检标记回车: 普通编辑里收下标记行失败");
        }
        else if (_editor is null)
        {
            problems.Add("自检标记回车: 收下标记行后也被迫收笔了");
        }

        CloseEditor(commit: false);

        if (_document.ToJson() != before)
        {
            problems.Add("自检标记回车: 取消普通编辑却改了文档");
        }
        else
        {
            Diagnostics.Write("selftest 标记回车: 收下标记行仍留在编辑态、取消不写文档");
        }
    }

    private void ClearFilters()
    {
        _view.TagFilter = null;
        _view.DueFilter = null;
        _view.DoneFilter = null;

        Rebuild();
    }

    /// <summary>
    /// 牌子的新三扇门（他 11-03 拍的 A）：点牌身＝聚焦这一类（投影，文档零改动）、
    /// 再点同一枚＝退出、双击＝编辑便签；× 仍是删那枚 token。
    /// </summary>
    private void SelfTestMarkChipEdits(List<string> problems)
    {
        var target = BoardNotes()
            .Where(node => node.Kind == CanvasNodeKind.Text)
            .Select(node => (Node: node, Chips: NoteMarks.Chips(Tags.Read(node.Text, _view.Today), _view.Today)
                .Where(chip => chip.Done is null && ChipLensKind(chip) is not null)
                .ToList()))
            .FirstOrDefault(pair => pair.Chips.Count > 0);

        if (target.Node is null)
        {
            Diagnostics.Write("selftest 标记牌: 板上没有可聚焦的标记牌，这一发不适用");

            return;
        }

        var before = _document.ToJson();
        var mark = target.Chips[0];
        var kind = ChipLensKind(mark)!;

        ChipLensFrom(target.Node.Id, mark);

        if (LensKind != kind || !LensPlan.Ids.Contains(target.Node.Id))
        {
            problems.Add($"自检标记牌: 点牌身没聚焦到「{kind}」（现在是 {LensKind ?? "无"}）");
        }
        else if (_editor is not null)
        {
            problems.Add("自检标记牌: 点牌身不该再开编辑器（编辑挪到双击了）");
        }
        else if (_document.ToJson() != before)
        {
            problems.Add("自检标记牌: 聚焦改了文档（它只能是投影）");
        }

        // 再点同一枚＝退出这一档。
        ChipLensFrom(target.Node.Id, mark);

        if (LensKind is not null)
        {
            problems.Add("自检标记牌: 再点同一枚没有退出聚焦");
        }

        // 双击＝编辑便签。
        ChipEditFrom(target.Node.Id, mark);

        if (_editor is null)
        {
            problems.Add("自检标记牌: 双击牌身没打开编辑器");
        }
        else
        {
            CloseEditor(commit: false);
        }

        if (_document.ToJson() == before)
        {
            Diagnostics.Write($"selftest 标记牌: 点牌聚焦「{kind}」、再点退出、双击进编辑；一圈下来文档零改动");
        }
        else
        {
            problems.Add("自检标记牌: 一圈点下来改了文档");
        }
    }

    /// <summary>
    /// @b 那枚牌的新门规（他 11-03 拍 A 时把"点牌＝切换"一并归进镜头）：点＝聚焦完成态、
    /// 双击＝进编辑器改 token、×＝删段。这一发钉：聚焦不碰文档、退出干净、× 仍一步可撤销地拿掉整段。
    /// </summary>
    private void SelfTestDoneChip(List<string> problems)
    {
        var saved = _document.ToJson();
        var savedCamera = (_view.PanX, _view.PanY, _view.Zoom);
        var node = _document.Add(CanvasNodeKind.Text, 43200, 40000, 260, 200);

        node.Text = "@b1\n自检完成标记";

        // 相机要把这一格摆进可见区：落在视野外的格子根本不走 Update，"牌子画没画"就读不到东西。
        // 缩放留在 100%：卡片小得装不下一行正文加一排牌子时，规矩是"先保正文"、牌子整排收起，
        // 那一发量的就不是这块牌子了。
        _view.Zoom = 1;
        _view.PanX = 60 - 43200;
        _view.PanY = 60 - 40000;

        Rebuild();
        ArrangeAll();
        BoardArea.UpdateLayout();

        try
        {
            var chips = NoteMarks.Chips(Tags.Read(node.Text), _view.Today);

            if (!_realized.TryGetValue(node.Id, out var card))
            {
                problems.Add($"自检完成标记: 这一格没进可见区（相机 {savedCamera.Item1:0}→{_view.PanX:0}，zoom {_view.Zoom:0.##}），量不到牌子");
            }
            else if (!card.PaintsReadout)
            {
                problems.Add(
                    $"自检完成标记: 牌子那一排没画出来（Core 侧 {chips.Count} 枚、正文「{node.Text}」、框 {node.Width:0}×{node.Height:0}）");
            }
            else if (chips.FirstOrDefault(chip => chip.Label == "已完成") is var found && found.Label != "已完成")
            {
                problems.Add($"自检完成标记: 牌子上读不到「已完成」（牌子 {chips.Count} 枚、正文「{node.Text}」）");
            }
            else
            {
                var mark = chips.First(chip => chip.Label == "已完成");
                var before = _document.ToJson();

                ChipLensFrom(node.Id, mark);

                if (LensKind != "done:1" || !LensPlan.Ids.Contains(node.Id))
                {
                    problems.Add($"自检完成标记: 点已完成牌没聚焦 done:1（现在是 {LensKind ?? "无"}）");
                }

                if (_document.ToJson() != before)
                {
                    problems.Add("自检完成标记: 点牌改了文档——聚焦只能是投影，切换 @b 走编辑器");
                }

                ChipLensFrom(node.Id, mark);

                if (LensKind is not null)
                {
                    problems.Add("自检完成标记: 再点同一枚没有退出聚焦");
                }

                ChipEditFrom(node.Id, mark);

                if (_editor is null)
                {
                    problems.Add("自检完成标记: 双击牌身没打开编辑器");
                }
                else
                {
                    CloseEditor(commit: false);
                }

                RemoveMarkFrom(node.Id, mark.Source);

                if (Tags.Read(node.Text).Done is not null || node.Text.Contains("@b", StringComparison.Ordinal))
                {
                    problems.Add($"自检完成标记: 点 × 没把这段拿掉（正文「{node.Text}」）");
                }
                else
                {
                    Diagnostics.Write("selftest 完成标记: 点牌聚焦 done:1、再点退出、双击进编辑、× 拿掉整段；四步文档各归各");
                }
            }
        }
        finally
        {
            _document.Remove(node.Id);
            (_view.PanX, _view.PanY, _view.Zoom) = savedCamera;
            Rebuild();

            if (_document.ToJson() != saved)
            {
                problems.Add("自检完成标记: 这一发没把板子原样还回去");
            }
        }
    }

    private void SetTagFilter(string? tag)
    {
        _view.TagFilter = string.Equals(_view.TagFilter, tag, StringComparison.Ordinal) ? null : tag;

        Rebuild();

        // 这两句走 Say() 而不是直接写字：回执那一枚只在 Say() 里浮出来，
        // 上一版直接改文字＝写进一块看不见的板子里。
        Say(_view.TagFilter is { } active
            ? $"仅显示标签为 {active} 的便签（再次点击取消）"
            : "已取消标签筛选");
    }

    private void SetDueFilter(NoteMarks.DueState state)
    {
        _view.DueFilter = _view.DueFilter == state ? null : state;

        Rebuild();

        Say(_view.DueFilter is { } active
            ? $"仅显示{DueLabel(active)}的便签（再次点击取消）"
            : "已取消到期筛选");
    }

    /// <summary>
    /// 完成那一档：再点同一行＝取消。<c>false</c> 那一档含"没写 @b"的便签（见 WriteFilterRows 里的理由），
    /// 所以它读起来是"还没做完"，不是"标了 @b0"。
    /// </summary>
    private void SetDoneFilter(bool done)
    {
        _view.DoneFilter = _view.DoneFilter == done ? null : done;

        Rebuild();

        var words = _view.DoneFilter switch
        {
            true => "仅显示已完成的便签（再次点击取消）",
            false => "仅显示还没做完的便签（再次点击取消）",
            _ => "已取消完成状态筛选",
        };

        Say(words);
    }
}
