using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 键盘快捷键那一页：一行一条命令，右端是它现在的键位；点一行就进"按下新的组合键…"，
/// 再按一发带修饰键的组合就换上去，Esc 或点别处退出。
/// </summary>
/// <remarks>
/// <para>
/// 这一页是键位表的**第二个面**，不是第二份表。命令名、默认键位仍只来自
/// <c>BoardCommands.Commands()</c>；改过的那些存在 <see cref="ChordBook"/> 里，派发、Tab 清单、
/// 右键菜单和这一页都过同一个 <see cref="ChordOf"/>——所以"菜单上写着 Alt+Left、按下去没反应"
/// 这种两边各算一次的分叉在这里不可能出现。
/// </para>
/// <para>
/// 为什么要有这一页：Alt+L 在这台机器上被系统抢走那一次（10-27），改一个键位要去改一行 C# 再重编，
/// 而他手上的板子正开着。键位是使用习惯，不是产品常量。
/// </para>
/// <para>
/// 捕获在 <c>Root.PreviewKeyDown</c> 那一层拦（见 <see cref="TryTakeChordCapture"/>）：那一层先于
/// 焦点控件与平台的 Tab 遍历，所以"等按键"时每一个键都归这一行，不会顺手把别的命令跑掉。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>这一页开着没有（算进"板子现在不收键"的那一份，和其余浮层一样）。</summary>
    private bool _chordPageOpen;

    /// <summary>正在等哪一条命令的新键位；null＝不在等。</summary>
    private string? _chordCaptureLabel;

    /// <summary>这一页最上面那一句（拒绝的理由、刚落定的改键）。下一次动作清掉。</summary>
    private string? _chordPageNote;

    private ChordBook? _chordBooks;

    /// <summary>键位表读回来时的问题（读不懂的行、读不到文件），开页时说到界面上。</summary>
    private string? _chordTableProblem;

    /// <summary>
    /// 改键存哪儿。<b>自检那一发只许碰 %TEMP%</b>：它和板子副本待在同一个临时目录里，
    /// 绝不去写他 <c>%LOCALAPPDATA%\Outspread\chords.txt</c>——那份可能正是他自己改过的键位。
    /// </summary>
    internal string ChordPrefsPath
    {
        get
        {
            if (!App.SelfTest)
            {
                return ChordBook.PathIn(BoardSession.DefaultDirectory());
            }

            var directory = _boardPath is { Length: > 0 } path ? Path.GetDirectoryName(path) : null;

            return ChordBook.PathIn(directory ?? Path.Combine(Path.GetTempPath(), "outspread-selftest"));
        }
    }

    /// <summary>改过的那一份。第一次问的时候才读盘：没人开这一页就不去碰文件。</summary>
    private ChordBook ChordTable
    {
        get
        {
            if (_chordBooks is null)
            {
                var (book, problem) = ChordBook.Load(ChordPrefsPath);

                _chordBooks = book;
                _chordTableProblem = problem;

                if (problem is not null)
                {
                    Diagnostics.Write($"键位表：{problem}");
                }
            }

            return _chordBooks;
        }
    }

    /// <summary>这一条命令现在该按哪一串：改过的优先，否则表里的默认值。四个面共用这一条。</summary>
    private string ChordOf(Command command) => ChordTable.Resolve(command.Label, command.Chord);

    /// <summary>这一条命令现在该显示的键帽串（方向键写成箭头）。</summary>
    private string ChordsText(Command command) => Command.Caps(ChordOf(command));

    /// <summary>
    /// 一发按键的串，修饰键顺序固定为 Ctrl、Shift、Alt——和 <see cref="ChordBook"/> 规范化之后
    /// 落盘的那一串同序。三个地方（两层派发、改键捕获）都走这一条，谁也不许自己拼。
    /// </summary>
    internal static string ChordFrom(Windows.System.VirtualKey key, bool control, bool shift, bool alt)
        => $"{(control ? "Ctrl+" : string.Empty)}{(shift ? "Shift+" : string.Empty)}{(alt ? "Alt+" : string.Empty)}{key}";

    /// <summary>
    /// 键位派发的唯一一条：两层键盘路径（Preview 与 KeyDown）和自检都走这里，于是"改过的键位
    /// 派发得到"这件事只有一处可以出错。命中就执行并返回 true。
    /// </summary>
    internal bool TryRunCommandForChord(string chord)
    {
        foreach (var command in Commands())
        {
            if (!command.Dispatched || !string.Equals(ChordOf(command), chord, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            command.Run();

            return true;
        }

        return false;
    }

    /// <summary>这一页能改的那些：键盘真要执行的、且键位本就是一发"修饰键 + 主键"的组合。</summary>
    private List<Command> RebindableCommands()
        => Commands().Where(command => command.Dispatched && ChordBook.IsBindable(ChordOf(command))).ToList();

    // ---------------------------------------------------------------------
    // 改键
    // ---------------------------------------------------------------------

    /// <summary>
    /// 把一条命令改到 <paramref name="chord"/>。成功返回 null；否则返回拒绝的理由——冲突时点名
    /// 占着这个键位的那条命令，写盘失败时带上原因。
    /// </summary>
    private string? TryRebind(string label, string chord)
    {
        if (!ChordBook.TryCanonical(chord, out var canonical))
        {
            return $"「{Command.Caps(chord)}」不是一串键位：要 Ctrl、Alt 或 Shift 加一个主键";
        }

        foreach (var command in Commands())
        {
            if (command.Label == label)
            {
                continue;
            }

            var taken = ChordOf(command);

            if (taken.Length > 0 && string.Equals(taken, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return $"「{Command.Caps(canonical)}」已经是「{command.Label}」的键位，换一个";
            }
        }

        var had = ChordTable.Overrides.TryGetValue(label, out var previous);
        var before = had ? previous! : null;

        if (!ChordTable.Set(label, canonical))
        {
            return $"「{Command.Caps(canonical)}」不是一串键位：要 Ctrl、Alt 或 Shift 加一个主键";
        }

        if (ChordTable.Save(ChordPrefsPath) is { } problem)
        {
            // 盘上没写成，内存里也就不许留着：界面上说的必须是下次开窗还在的那一套。
            if (before is null)
            {
                ChordTable.Remove(label);
            }
            else
            {
                ChordTable.Set(label, before);
            }

            return $"键位表写不进（{problem}），这次没改";
        }

        return null;
    }

    /// <summary>全部恢复默认：内存与盘一起回表里的值。写不成要说出来，否则下次开窗键位又回来。</summary>
    private void ResetChords()
    {
        ChordTable.Reset();

        var problem = ChordTable.Save(ChordPrefsPath);

        _chordCaptureLabel = null;
        _chordPageNote = problem is null
            ? "已全部恢复成表里的默认键位"
            : $"键位表写不进（{problem}）：这次开着的窗口是默认键位，下次开窗还会读回旧的那份";

        RebuildChordPage();
        Say(problem is null ? "键位已全部恢复默认" : "键位表写不进，见键盘快捷键页上的说明");
    }

    // ---------------------------------------------------------------------
    // 这一页
    // ---------------------------------------------------------------------

    private void OpenChordPage()
    {
        SetSearchPanel(false);
        SetRestorePanel(false);
        SetFilterPanel(false);
        CloseColorPopover();

        ChromeStyling.Ensure(NoteStyling.IsDark);
        ChromeStyling.ApplyFloatingChrome(ChordPanel);

        _chordCaptureLabel = null;

        // 先把表读出来，再取"读盘留下的那句话"——反过来取永远是空的（那一步才第一次读盘）。
        _ = ChordTable.Any;
        _chordPageNote = ChordTableProblemSummary();

        RebuildChordPage();

        ChordPanel.Visibility = Visibility.Visible;
        _chordPageOpen = true;

        // 键盘得有个落点：捕获挂在 Root.PreviewKeyDown 上，而它只在焦点还在这个窗口里时收得到键。
        BoardArea.Focus(FocusState.Programmatic);
    }

    /// <summary>读盘时留下的问题只在这一页上说一次，不每次重建都念一遍。</summary>
    private string? ChordTableProblemSummary()
    {
        var problem = _chordTableProblem;

        _chordTableProblem = null;

        return problem;
    }

    internal void CloseChordPage()
    {
        if (!_chordPageOpen)
        {
            return;
        }

        ChordPanel.Visibility = Visibility.Collapsed;

        _chordPageOpen = false;
        _chordCaptureLabel = null;
        _chordPageNote = null;
    }

    private void OnChordPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;

        CloseChordPage();
    }

    private void RebuildChordPage()
    {
        var dark = NoteStyling.IsDark;
        var rows = new List<FrameworkElement>();

        if (_chordPageNote is { Length: > 0 } note)
        {
            // 拒绝的理由（谁占着这个键位、或者盘写不进）比这一页上任何一行都重要：用正文墨说。
            rows.Add(PanelKit.Row(note));
        }

        rows.Add(_chordCaptureLabel is null
            ? PanelKit.Line("点一行右端的键位，再按新的组合键 · Esc 取消")
            : PanelKit.Row($"正在等「{_chordCaptureLabel}」的新键位：Ctrl、Alt 或 Shift 加一个主键，Esc 取消"));

        foreach (var command in RebindableCommands())
        {
            var label = command.Label;
            var capturing = string.Equals(label, _chordCaptureLabel, StringComparison.Ordinal);

            var row = capturing
                ? PanelKit.Row(label, "按新的组合键…", () => BeginChordCapture(label))

                // 没在等的那一行照键位清单的样子画：右端是键帽，不是一段灰字。
                : PanelKit.Row(label, ChordsText(command), () => BeginChordCapture(label), keys: true);

            if (capturing)
            {
                // 等按键的那一行衬一层强调色。行本身只有"悬停"这一种状态，这里借的是同一枚强调色：
                // "键盘现在归这一行"必须一眼看得出，而它又不是悬停（手已经离开了）。
                // 衬的是行内容那一格而不是 SheetRow 自己：ContentControl 的默认模板不画 Background
                // （10-27 拍图量的：那一层 0.14 的强调色在 PNG 上一个像素都没出现），Grid 画。
                if (row.Content is Panel content)
                {
                    content.Background = new SolidColorBrush(NoteStyling.WithAlpha(SelectionChrome.Ring(dark), 0.14));
                }
            }

            rows.Add(row);
        }

        if (ChordTable.Any)
        {
            rows.Add(PanelKit.Row("全部恢复默认键位", null, ResetChords));
        }

        ChordScroll.Content = PanelKit.Host(rows.ToArray());
    }

    private void BeginChordCapture(string label)
    {
        _chordCaptureLabel = label;
        _chordPageNote = null;

        RebuildChordPage();

        // 点行这一发之后焦点仍得落在板面上：捕获在 Root.PreviewKeyDown，焦点跑到别的控件上，
        // 那一发键就不会经过这里（第一版就是这样，点完行再按键什么都没发生）。
        BoardArea.Focus(FocusState.Programmatic);
    }

    /// <summary>等按键那一层对一个键的判断。挪成纯函数，自检才量得到这张表（键盘事件本身造不出来）。</summary>
    private enum CaptureAnswer
    {
        /// <summary>只按了修饰键：他还在按组合的路上，等主键。</summary>
        Wait,

        /// <summary>Esc：取消这一发改键。</summary>
        Cancel,

        /// <summary>不成一串键位（不带修饰键、或两个主键）：说清原因，留在等按键。</summary>
        Reject,

        /// <summary>收下。</summary>
        Bind,
    }

    /// <summary>
    /// "等按键"那一层的收键口。返回 true＝这一发归改键页，别的层不许再看见它。
    /// 只按修饰键不算一发；Esc 取消；不带修饰键的、以及两个主键的都不收。
    /// </summary>
    internal bool TryTakeChordCapture(KeyRoutedEventArgs e)
    {
        if (_chordCaptureLabel is not { } label)
        {
            return false;
        }

        var (answer, chord) = AnswerCapture(
            e.Key,
            KeyDownIs(Windows.System.VirtualKey.Control),
            KeyDownIs(Windows.System.VirtualKey.Shift),
            KeyDownIs(Windows.System.VirtualKey.Menu));

        switch (answer)
        {
            case CaptureAnswer.Cancel:
                _chordCaptureLabel = null;
                _chordPageNote = null;
                RebuildChordPage();

                break;

            case CaptureAnswer.Reject:
                _chordPageNote = $"「{Command.Caps(chord)}」不带修饰键：键位要 Ctrl、Alt 或 Shift 加一个主键";
                RebuildChordPage();

                break;

            case CaptureAnswer.Bind:
                AcceptChord(label, chord);

                break;
        }

        return true;
    }

    /// <summary>这一发键在"等按键"里算什么：Esc 取消、只按修饰键等着、其余按语法判收或不收。</summary>
    private static (CaptureAnswer Answer, string Chord) AnswerCapture(
        Windows.System.VirtualKey key,
        bool control,
        bool shift,
        bool alt)
    {
        switch (key)
        {
            case Windows.System.VirtualKey.Escape:
                return (CaptureAnswer.Cancel, string.Empty);

            // 他按 Ctrl、再按 Alt 的中间那两下不是"一发组合键"：等主键，不判错。
            case Windows.System.VirtualKey.Control:
            case Windows.System.VirtualKey.Shift:
            case Windows.System.VirtualKey.Menu:
            case Windows.System.VirtualKey.LeftWindows:
            case Windows.System.VirtualKey.RightWindows:
                return (CaptureAnswer.Wait, string.Empty);
        }

        var chord = ChordFrom(key, control, shift, alt);

        return (ChordBook.IsBindable(chord) ? CaptureAnswer.Bind : CaptureAnswer.Reject, chord);
    }

    /// <summary>
    /// 落一发组合键。被拒时留在"等按键"这一档——他刚按错一个组合，下一步就是再按一个，
    /// 而不是重新在一列里找到那一行。
    /// </summary>
    private void AcceptChord(string label, string chord)
    {
        if (TryRebind(label, chord) is { } refusal)
        {
            _chordPageNote = refusal;
            RebuildChordPage();

            return;
        }

        _chordCaptureLabel = null;
        _chordPageNote = $"「{label}」现在是 {Command.Caps(chord)}";

        RebuildChordPage();
        Say(_chordPageNote);
    }

    // ---------------------------------------------------------------------
    // 自检
    // ---------------------------------------------------------------------

    /// <summary>
    /// 改键这一路的自检：改一条 → 读回来是新值 → 派发用新值 → 恢复默认回旧值；冲突拒收且不改盘。
    /// </summary>
    /// <remarks>
    /// 只跑在 %TEMP% 那一份上。它会把临时表清空再从零改——所以"改键没有落盘"这类错在这里是真的红，
    /// 而不是被上一次的残留衬托成绿。派发那一发走的是键盘那两层调的同一个
    /// <see cref="TryRunCommandForChord"/>，不是另写一遍"应该命中哪一条"的算术。
    /// </remarks>
    private void SelfTestChords(List<string> problems)
    {
        var path = ChordPrefsPath;

        if (!path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"自检改键: 键位表落在 {path} —— 自检只许碰 %TEMP% 那一份，绝不许动他的真表");

            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"自检改键: 清不掉 {path}（{ex.Message}），这一发量到的是上一发留下的值");
        }

        _chordBooks = null;
        _chordTableProblem = null;

        string Now(string label)
            => ChordOf(Commands().First(command => command.Label == label));

        string PageCaps(string label)
        {
            var row = MenuRows(ChordPanel).FirstOrDefault(candidate => candidate.Text == label);

            return row is null ? "(没有这一行)" : CapsTextOf(row);
        }

        var start = Now("左对齐");

        if (start != "Alt+Left")
        {
            problems.Add($"自检改键: 起步的「左对齐」读到 {start}，%TEMP% 里那张空表不该有任何改键");
        }

        // 1) 页要开得出来、列的是能改的那些命令。
        var entry = BuildSettingsMenu().Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(item => item.Text == "键盘快捷键…");

        if (entry is null)
        {
            problems.Add("自检改键: ⋮ 菜单里没有「键盘快捷键…」——这一页只有这一个入口");
        }
        else if (entry.Icon is null)
        {
            problems.Add("自检改键: ⋮ 里那一行没有图标（路径没认下来），它会从那一列里凹进去");
        }

        OpenChordPage();

        var pageRows = MenuRows(ChordPanel);

        if (!_chordPageOpen || ChordPanel.Visibility != Visibility.Visible)
        {
            problems.Add("自检改键: 键盘快捷键页没打开");
        }

        if (pageRows.Count < 5 || !pageRows.Any(row => row.Text == "左对齐"))
        {
            problems.Add($"自检改键: 页上只列了 {pageRows.Count} 行，且{(pageRows.Any(row => row.Text == "左对齐") ? "有" : "没有")}「左对齐」");
        }

        // 2) 点一行 → 进"等按键"这一档，并且看得出来在等。
        BeginChordCapture("左对齐");

        var capturingRow = MenuRows(ChordPanel).FirstOrDefault(row => row.Text == "左对齐");
        var captureHint = capturingRow is null ? "(没有这一行)" : CapsTextOf(capturingRow);

        if (_chordCaptureLabel != "左对齐")
        {
            problems.Add("自检改键: 点了「左对齐」却没进等按键的状态");
        }

        if (!captureHint.Contains("按新的组合键", StringComparison.Ordinal))
        {
            problems.Add($"自检改键: 等按键那一行右端写着『{captureHint}』——他看不出键盘现在归哪一行");
        }

        if ((capturingRow?.Content as Panel)?.Background is not SolidColorBrush captureFill || captureFill.Color.A == 0)
        {
            problems.Add("自检改键: 等按键的那一行没有衬底，屏幕上看不出它在等你按");
        }

        // 3) 按一发组合键（和键盘那层拼同一串）→ 绑上、捕获关掉、落盘。
        //    "等按键"的判断表本身也要量：键盘事件造不出来，所以那一层被挪成了 AnswerCapture。
        (CaptureAnswer Answer, string Chord)[] answers =
        [
            (CaptureAnswer.Cancel, "Esc"),
            (CaptureAnswer.Wait, "只按 Ctrl"),
            (CaptureAnswer.Wait, "只按 Alt"),
            (CaptureAnswer.Reject, "F9"),
            (CaptureAnswer.Bind, "Alt+F9"),
            (CaptureAnswer.Bind, "Ctrl+Shift+K"),
            (CaptureAnswer.Bind, "Shift+1"),
        ];

        (Windows.System.VirtualKey Key, bool Ctrl, bool Shift, bool Alt)[] presses =
        [
            (Windows.System.VirtualKey.Escape, false, false, true),
            (Windows.System.VirtualKey.Control, true, false, false),
            (Windows.System.VirtualKey.Menu, false, false, true),
            (Windows.System.VirtualKey.F9, false, false, false),
            (Windows.System.VirtualKey.F9, false, false, true),
            (Windows.System.VirtualKey.K, true, true, false),
            (Windows.System.VirtualKey.Number1, false, true, false),
        ];

        for (var i = 0; i < answers.Length; i++)
        {
            var (want, described) = answers[i];
            var (key, ctrl, shift, alt) = presses[i];
            var (got, chord) = AnswerCapture(key, ctrl, shift, alt);

            if (got != want)
            {
                problems.Add($"自检改键: {described} 判成了 {got}，应为 {want}（拼出 {chord}）");
            }
        }

        var captured = ChordFrom(Windows.System.VirtualKey.F9, control: false, shift: false, alt: true);

        if (captured != "Alt+F9")
        {
            problems.Add($"自检改键: 按键拼串拼出了 {captured}，应为 Alt+F9");
        }

        AcceptChord("左对齐", captured);

        if (_chordCaptureLabel is not null)
        {
            problems.Add("自检改键: 绑上了还留在等按键——捕获没关掉");
        }

        if (Now("左对齐") != "Alt+F9")
        {
            problems.Add($"自检改键: 绑完读到的是 {Now("左对齐")}，不是 Alt+F9");
        }

        var (reread, readProblem) = ChordBook.Load(path);
        var rereadValue = reread.Resolve("左对齐", "Alt+Left");

        if (readProblem is not null)
        {
            problems.Add($"自检改键: 读回来报「{readProblem}」");
        }

        if (rereadValue != "Alt+F9")
        {
            problems.Add($"自检改键: 盘上读回来是 {rereadValue}，不是 Alt+F9——改键没落盘");
        }

        // 4) 派发用新值：走键盘那两层调的同一个入口。一格里「左对齐」会明着拒绝，
        //    所以"真的跑到了"的证据是那句回执，而不是一个布尔。
        var probe = _document.Nodes.FirstOrDefault(node => node.Kind != CanvasNodeKind.Group);

        if (probe is null)
        {
            problems.Add("自检改键: 板上没有可选的格子，派发那一发量不了");
        }
        else
        {
            ClearSelection();
            _selection.Add(probe.Id);
            ProjectSelection();

            var undoBefore = _history.UndoCount;
            var oldFired = TryRunCommandForChord("Alt+Left");
            var newFired = TryRunCommandForChord("Alt+F9");
            var receipt = SayTextForSelfTest;

            if (oldFired)
            {
                problems.Add("自检改键: 旧键位 Alt+Left 还在派发——改键没接管键盘");
            }

            if (!newFired)
            {
                problems.Add("自检改键: 新键位 Alt+F9 派发不到「左对齐」");
            }

            if (!receipt.Contains("至少需要 2 件", StringComparison.Ordinal))
            {
                problems.Add($"自检改键: 派发跑到的不是「左对齐」（回执『{receipt}』）");
            }

            if (_history.UndoCount != undoBefore)
            {
                problems.Add("自检改键: 一格上的拒绝留下了撤销步");
            }
        }

        // 5) 另外两个面读的也是这一份：右键菜单的加速键文字、Tab 清单的键帽。
        var menuItem = BuildBoardMenu(MenuWhere.ManyNotes).Items
            .OfType<MenuFlyoutSubItem>()
            .SelectMany(submenu => submenu.Items.OfType<MenuFlyoutItem>())
            .FirstOrDefault(item => item.Text == "左对齐");

        if (menuItem is null)
        {
            problems.Add("自检改键: 右键菜单里找不到「左对齐」");
        }
        else if (menuItem.KeyboardAcceleratorTextOverride != "Alt+F9")
        {
            problems.Add($"自检改键: 右键菜单上还写着 {menuItem.KeyboardAcceleratorTextOverride}，没跟着改键走");
        }

        ShowCheatsheet();

        var sheetRow = MenuRows(CheatPanel).FirstOrDefault(row => row.Text == "左对齐");
        var sheetCaps = sheetRow is null ? "(没有这一行)" : CapsTextOf(sheetRow);

        if (sheetCaps != "Alt+F9")
        {
            problems.Add($"自检改键: Tab 清单上「左对齐」是 {sheetCaps}，没跟着改键走");
        }

        CloseCheatsheet();

        var pageCaps = PageCaps("左对齐");

        if (pageCaps != "Alt+F9")
        {
            problems.Add($"自检改键: 键盘快捷键页上「左对齐」是 {pageCaps}，没跟着改键走");
        }

        // 6) 冲突与语法：拒收，点名占着的那条，盘上一个字都不许动。
        var conflict = TryRebind("左对齐", "Alt+B");

        if (conflict is null)
        {
            problems.Add("自检改键: 抢「堆叠」的 Alt+B 也被收下了");
        }
        else if (!conflict.Contains("堆叠", StringComparison.Ordinal))
        {
            problems.Add($"自检改键: 冲突拒收了，但没说是谁占着（『{conflict}』）");
        }

        var (afterConflict, _) = ChordBook.Load(path);

        if (Now("左对齐") != "Alt+F9" || afterConflict.Resolve("左对齐", "Alt+Left") != "Alt+F9")
        {
            problems.Add("自检改键: 冲突被拒以后键位变了（内存或盘上）");
        }

        if (TryRebind("左对齐", "F9") is null)
        {
            problems.Add("自检改键: 不带修饰键的 F9 也被收下了");
        }

        if (TryRebind("左对齐", "Alt+B+K") is null)
        {
            problems.Add("自检改键: 两个主键（Alt+B+K）也被收下了");
        }

        // 7) 恢复默认：回表里的值，盘上不再留改键，派发也跟着回去。
        ResetChords();

        var (afterReset, resetProblem) = ChordBook.Load(path);
        var backToFired = TryRunCommandForChord("Alt+Left");
        var strayFired = TryRunCommandForChord("Alt+F9");

        if (Now("左对齐") != "Alt+Left")
        {
            problems.Add($"自检改键: 恢复默认以后读到 {Now("左对齐")}，应是 Alt+Left");
        }

        if (resetProblem is null && afterReset.Any)
        {
            problems.Add($"自检改键: 恢复默认以后盘上还留着 {afterReset.Overrides.Count} 条改键");
        }

        if (!backToFired)
        {
            problems.Add("自检改键: 恢复默认以后 Alt+Left 派发不到「左对齐」");
        }

        if (strayFired)
        {
            problems.Add("自检改键: 恢复默认以后 Alt+F9 还连着「左对齐」");
        }

        var resetCaps = PageCaps("左对齐");

        if (resetCaps != "Alt+←")
        {
            problems.Add($"自检改键: 恢复默认以后页上是 {resetCaps}，应回到 Alt+←");
        }

        if (problems.Count == 0)
        {
            Diagnostics.Write(
                $"selftest 改键: 页 {MenuRows(ChordPanel).Count} 行；左对齐 Alt+←→Alt+F9（盘上读回一致、派发走新值、菜单与 Tab 清单同值）；"
                + "抢 Alt+B 被「堆叠」挡住、无修饰键与两个主键被拒；恢复默认回 Alt+← 且盘上不再留改键");
        }

        CloseChordPage();
        ClearSelection();
        ProjectSelection();
    }

    /// <summary>
    /// 一行右端那串键帽的字（把行里的文字拼起来，跳掉命令名本身）。
    /// 走的是内容树而不是可视化树：这些行是建出来的，没排过版之前可视化树里一个子节点都没有。
    /// </summary>
    private static string CapsTextOf(PanelKit.SheetRow row)
    {
        var parts = new List<string>();

        Walk(row);

        return string.Join('+', parts);

        void Walk(DependencyObject node)
        {
            if (node is TextBlock text && text.Text != row.Text)
            {
                parts.Add(text.Text);
            }

            switch (node)
            {
                case Panel panel:
                    foreach (var child in panel.Children)
                    {
                        Walk(child);
                    }

                    break;

                case Border border when border.Child is DependencyObject inner:
                    Walk(inner);

                    break;

                case ContentControl content when content.Content is DependencyObject payload:
                    Walk(payload);

                    break;
            }
        }
    }
}
