using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// 左下角那两档计数（逾期／即将逾期），以及"点一档把那几格临时聚到中间"的聚焦态。
/// 镜头不止状态条那两档：卡片上的标记牌点一下也进同一套投影（他 11-03 拍的 A），
/// 档位 key 见 <see cref="ChipLensKind"/>——tag:/done:/state: 三类与 late/soon 走同一条进入/退出路。
/// </summary>
/// <remarks>
/// <para>
/// 聚合是<b>投影</b>，不是搬家：文档里的坐标一个都不改，位移走卡片根上那枚
/// <see cref="NodeVisual.FoldShift"/>——和"筛选把没命中的折进组里"是同一枚变换。
/// </para>
/// <para>
/// 它复用「演示」（Alt+P）那条"临时版式＋退出还原"的路，但不碰相机：聚焦时禁掉缩放、
/// 平移与拖动，屏幕坐标就是布局坐标；编辑仍然走同一个编辑器（位置加上这一格的投影位移）。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    internal const string LensLate = "late";
    internal const string LensSoon = "soon";

    /// <summary>正在看的那一档；null＝正常布局。</summary>
    private string? _lensKind;

    /// <summary>这一拍聚起来的格子（文档顺序）。</summary>
    private List<string> _lensIds = [];

    /// <summary>这一拍每格的落点（屏幕坐标）与相对"它本该在的位置"的位移。</summary>
    private Dictionary<string, (double X, double Y)> _lensSlots = new(StringComparer.Ordinal);
    private Dictionary<string, (double DX, double DY)> _lensOffsets = new(StringComparer.Ordinal);

    private int _dueLateCount;
    private int _dueSoonCount;

    /// <summary>镜头里的"翻页"位移（屏幕像素）：聚起来的一坨超过一屏时，滚轮在两端限死地滑槽位。</summary>
    private double _lensScrollY;
    private double _lensMaxScroll;

    /// <summary>镜头缩放的限幅带：总览没有平移，缩过头点不着、放过头一屏只剩两三格（他 11-03 的限幅令）。
    /// 已经在带外时只许往带里走。</summary>
    private const double LensZoomMin = 0.6;
    private const double LensZoomMax = 2.0;

    /// <summary>镜头里让开的常驻浮件（右下按钮组、左上小地图）及各自进镜头前的可见性。</summary>
    private readonly List<(FrameworkElement Element, Visibility Was)> _lensChromeHidden = [];

    private bool? _dueStripDark;
    private string? _dueStripLit;

    // ---------------------------------------------------------------------
    // 计数与状态条
    // ---------------------------------------------------------------------

    /// <summary>这一拍的日期标记读的是表格卡的备注（表格自己的 markdown 不是正文）。</summary>
    private NoteProperties DueMarks(CanvasNode node)
        => Tags.Read(EntityCards.IsTable(node) ? EntityCards.NoteOf(node) : node.Text, _view.Today);

    /// <summary>
    /// 重算左下角那两个数。只在文档变了那一拍（<see cref="Rebuild"/>）调：平移的每一帧都去读
    /// 全板的正文太贵，而这两个数只在格子/日期变了的时候才会变。
    /// </summary>
    private void RefreshDueStrip()
    {
        var late = 0;
        var soon = 0;

        foreach (var node in BoardNotes())
        {
            var marks = DueMarks(node);

            if (NoteMarks.IsOpenLate(marks, _view.Today))
            {
                late++;
            }
            else if (NoteMarks.IsOpenSoon(marks, _view.Today, NoteMarks.SoonDays))
            {
                soon++;
            }
        }

        _dueLateCount = late;
        _dueSoonCount = soon;

        DueLateCount.Text = late.ToString(CultureInfo.InvariantCulture);
        DueSoonCount.Text = soon.ToString(CultureInfo.InvariantCulture);
        DueStrip.Visibility = late + soon > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 正看着的那一档空了（例如刚把最后一格改完）：退回正常布局，别留一个空镜头。
        if (_lensKind is { } watching && LensMatches(watching).Count == 0)
        {
            ExitDueLens();
        }
    }

    /// <summary>状态条的皮与右下角那一组同源：外壳走 Badge 令牌，成员是无边框的按钮。</summary>
    private void StyleDueStrip()
    {
        var dark = NoteStyling.IsDark;

        if (_dueStripDark != dark)
        {
            var line = new SolidColorBrush(NoteStyling.WithAlpha(Badge.LineColour(dark), Badge.LineAlpha(dark)));
            var muted = new SolidColorBrush(NoteStyling.ToColor(Badge.Muted(dark)));
            var ink = new SolidColorBrush(NoteStyling.ToColor(Badge.Ink(dark)));

            DueStrip.BorderThickness = new Thickness(1);
            DueStrip.CornerRadius = new CornerRadius(Chrome.Radius);
            DueStrip.Padding = new Thickness(Badge.ContainerPaddingPx);
            DueStrip.Background = new SolidColorBrush(NoteStyling.ToColor(Badge.PanelColour(dark)));
            DueStrip.BorderBrush = line;

            foreach (var button in new[] { DueLateButton, DueSoonButton })
            {
                button.Padding = new Thickness(Badge.MemberPaddingX, Badge.MemberPaddingY, Badge.MemberPaddingX, Badge.MemberPaddingY);
                button.MinWidth = 0;
                button.MinHeight = Badge.LineHeight + (2 * Badge.MemberPaddingY);
                button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.BorderThickness = new Thickness(0);
                button.CornerRadius = new CornerRadius(Badge.MemberRadiusPx);
                button.FontSize = Badge.FontSize;
                button.Foreground = muted;
            }

            foreach (var label in new[] { DueLateLabel, DueSoonLabel })
            {
                label.FontSize = Badge.FontSize;
                label.LineHeight = Badge.LineHeight;
                label.Foreground = muted;
            }

            foreach (var count in new[] { DueLateCount, DueSoonCount })
            {
                count.FontSize = Badge.FontSize;
                count.LineHeight = Badge.LineHeight;
                count.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                count.Foreground = ink;
            }

            DueLateDot.Fill = new SolidColorBrush(NoteStyling.ToColor(
                NoteMarks.BarFor(NoteMarks.DueState.Late, dark) ?? new Rgb(209, 52, 21)));
            DueSoonDot.Fill = new SolidColorBrush(NoteStyling.ToColor(NoteMarks.SoonColour(dark)));

            DueSep.Fill = line;
            DueSep.Width = Badge.SeparatorWidthPx;
            DueSep.Margin = new Thickness(Badge.SeparatorMarginX, Badge.SeparatorMarginY, Badge.SeparatorMarginX, Badge.SeparatorMarginY);
            DueSep.VerticalAlignment = VerticalAlignment.Stretch;

            _dueStripDark = dark;

            // 主题翻那一拍把成员底色重铺成了透明，正在看的那一档要重上一遍底色。
            _dueStripLit = null;
        }

        // 正在看的那一档压一点底色：两枚钮平时透明，这一档才亮起来。
        if (_dueStripLit != _lensKind)
        {
            _dueStripLit = _lensKind;

            var wash = dark
                ? new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0));

            DueLateButton.Background = _lensKind == LensLate ? wash : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            DueSoonButton.Background = _lensKind == LensSoon ? wash : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private void OnDueLateClick(object sender, RoutedEventArgs e)
    {
        ToggleDueLens(LensLate);
    }

    private void OnDueSoonClick(object sender, RoutedEventArgs e)
    {
        ToggleDueLens(LensSoon);
    }

    // ---------------------------------------------------------------------
    // 聚焦态
    // ---------------------------------------------------------------------

    internal string? LensKind => _lensKind;

    internal (int Late, int Soon) DueCounts => (_dueLateCount, _dueSoonCount);

    /// <summary>自检读回：这一拍聚起来的那几格，以及每格落了多远（屏幕像素）。</summary>
    internal (List<string> Ids, Dictionary<string, (double DX, double DY)> Offsets) LensPlan => (_lensIds, _lensOffsets);

    internal double LensDimOpacity => LensDim.Opacity;

    private void ToggleDueLens(string kind)
    {
        if (_lensKind == kind)
        {
            ExitDueLens();
        }
        else
        {
            EnterDueLens(kind);
        }
    }

    private void EnterDueLens(string kind)
    {
        // 空着一档的那一下点击必须是真 no-op，一个字都不动状态。以前放在关闭面板、
        // 记旧位移之后才判空：上一档的投影还挂在屏上，_lensKind 却已被清成 null，
        // 再点回来时动画从原位起步，屏幕上就闪一下（他 11-03 点出来的缺陷）。
        if (LensMatches(kind).Count == 0)
        {
            return;
        }

        // 换档时"上一次选的那几格"要边走边回家：先把旧位移记下来（ArrangeAll 会把它清掉）。
        var previous = new Dictionary<string, (double DX, double DY)>(_lensOffsets, StringComparer.Ordinal);

        CloseEditor(commit: true);
        ClearSelection();
        ClearEdgeSelection();
        CloseFilterPanel();
        CloseCheatsheet();
        CloseSearchPanel();
        CloseRestorePanel();
        CloseChordPage();
        ClearGroupHint();
        CloseColorPopover();

        _lensKind = kind;

        _lensScrollY = 0;
        _lensMaxScroll = 0;

        // 镜头是临时看板态：右下按钮组与左上小地图会被聚起来的格子压住（他 11-03 报的重叠），
        // 照演示那套"让开再按原样还"。左下状态条不让——它就是换档的入口。
        HideLensChrome();

        // 先排一拍把落点算出来（这一拍卡片的 FoldShift 已经是槽位），再从原位播过去——
        // 同一帧里排完就起动画，屏幕上看不到"先跳一下"。
        ArrangeAll();
        AnimateDim(0.38);

        // 同一拍出发：新的一档从原位弹到槽位，上一档从槽位弹回原位、同时退成灰。
        // 两条弹簧并行，才有"这边回去、那边出来"的衔接感（他 11-02 点出来的那一格）。
        foreach (var (id, offset) in _lensOffsets)
        {
            if (_realized.TryGetValue(id, out var visual))
            {
                visual.FadeTo(0.22, 1);
                visual.SlideSpring(0, 0, offset.DX, offset.DY);
            }
        }

        foreach (var (id, offset) in previous)
        {
            if (_lensOffsets.ContainsKey(id) || !_realized.TryGetValue(id, out var leaving))
            {
                continue;
            }

            leaving.FadeTo(1, 0.22);
            leaving.SlideSpring(offset.DX, offset.DY, 0, 0);
        }

        Say($"{_lensIds.Count} 个{LensWords(kind)} · 点空白处返回"
            + (_lensMaxScroll > 0 ? " · 滚轮翻页 · Ctrl缩放" : " · Ctrl＋滚轮缩放"));
        StyleDueStrip();

    }

    private void ExitDueLens()
    {
        if (_lensKind is null)
        {
            return;
        }

        CloseEditor(commit: true);

        // 先拷一份：下面会把同一个字典清空，引用过去的那一份会跟着一起空。
        var offsets = new Dictionary<string, (double DX, double DY)>(_lensOffsets, StringComparer.Ordinal);

        _lensKind = null;
        _lensIds = [];
        _lensSlots.Clear();
        _lensOffsets.Clear();
        _lensScrollY = 0;
        _lensMaxScroll = 0;

        AnimateDim(0);
        ArrangeAll();

        foreach (var (id, offset) in offsets)
        {
            if (_realized.TryGetValue(id, out var visual))
            {
                // 排版已经把位移收回去了，先从"槽位上"那一格弹回原位，同时亮回正常。
                visual.FadeTo(0.22, 1);
                visual.SlideSpring(offset.DX, offset.DY, 0, 0);
            }
        }

        Say("");
        StyleDueStrip();
        RestoreLensChrome();
    }

    /// <summary>清掉聚焦态但不播动画、不重排（换板/关板时用：文档与画面都要从零开始）。</summary>
    private void ResetDueLens()
    {
        _lensKind = null;
        _lensIds = [];
        _lensSlots.Clear();
        _lensOffsets.Clear();
        _lensScrollY = 0;
        _lensMaxScroll = 0;
        LensDim.Opacity = 0;
        _dueStripLit = null;
        RestoreLensChrome();
    }

    /// <summary>进镜头让开常驻浮件；已经让开的不重复记（换档时会再叫一次）。</summary>
    private void HideLensChrome()
    {
        foreach (var element in new FrameworkElement[] { BadgeHost, _miniMap.Root })
        {
            if (_lensChromeHidden.Exists(entry => ReferenceEquals(entry.Element, element)))
            {
                continue;
            }

            _lensChromeHidden.Add((element, element.Visibility));
            element.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>退出镜头按进镜头前的可见性还——本来就该藏着的，还回去也还是藏着。</summary>
    private void RestoreLensChrome()
    {
        foreach (var (element, was) in _lensChromeHidden)
        {
            element.Visibility = was;
        }

        _lensChromeHidden.Clear();
    }

    private void AnimateDim(double to)
    {
        var from = LensDim.Opacity;

        if (Math.Abs(from - to) < 0.01)
        {
            LensDim.Opacity = to;

            return;
        }

        LensDim.Opacity = to;

        // 压暗跟着同一条弹簧的时间表走（曲线也用那一份采样：一层只会变透明的面不该有回弹）。
        LensDim.Opacity = to;

        var fade = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(Motion.SpringMs),
            EnableDependentAnimation = true,
        };

        var samples = Motion.SpringSamples;

        for (var index = 0; index < samples.Count; index++)
        {
            fade.KeyFrames.Add(new LinearDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(index * Motion.SpringStepMs)),
                Value = from + ((to - from) * samples[index]),
            });
        }

        Storyboard.SetTarget(fade, LensDim);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var board = new Storyboard { FillBehavior = FillBehavior.Stop };
        board.Children.Add(fade);
        board.Begin();
    }

    private List<string> LensMatches(string kind)
        => BoardNotes()
            .Where(node => LensHit(DueMarks(node), kind))
            .Select(node => node.Id)
            .ToList();

    /// <summary>
    /// 一格落不落进这一档镜头。状态条两档沿用"还欠着"的口径（完成的、被逾期抢走的不算）；
    /// 牌子进来的三档读的是字面——牌上写了什么就聚什么，@b0 的"未完成"不含没写 @b 的格子。
    /// </summary>
    private bool LensHit(NoteProperties marks, string kind)
    {
        switch (kind)
        {
            case LensLate:
                return NoteMarks.IsOpenLate(marks, _view.Today);
            case LensSoon:
                return !NoteMarks.IsOpenLate(marks, _view.Today) && NoteMarks.IsOpenSoon(marks, _view.Today, NoteMarks.SoonDays);
            case "done:1":
                return marks.Done == true;
            case "done:0":
                return marks.Done == false;
        }

        if (kind.StartsWith("tag:", StringComparison.Ordinal))
        {
            return marks.Tags.Contains(kind[4..], StringComparer.Ordinal);
        }

        if (kind.StartsWith("state:", StringComparison.Ordinal))
        {
            return NoteMarks.StateOf(marks, _view.Today).ToString() == kind[6..];
        }

        return false;
    }

    /// <summary>牌身那一下点出来的是哪一档；null＝这枚牌不进镜头（闸门牌、序号牌）。</summary>
    internal static string? ChipLensKind(NoteMarks.Mark mark)
    {
        if (mark.Gate != GateStatus.None)
        {
            return null;
        }

        if (mark.Done is { } done)
        {
            return done ? "done:1" : "done:0";
        }

        if (mark.IsDate)
        {
            return "state:" + mark.State;
        }

        if (mark.Label.StartsWith("NO.", StringComparison.Ordinal))
        {
            return null;
        }

        return "tag:" + mark.Label;
    }

    /// <summary>点牌身＝聚焦这一类；再点同一枚（隔过双击窗口）＝退出。空档不回声的规矩照旧。</summary>
    private void ChipLensFrom(string nodeId, NoteMarks.Mark mark)
    {
        if (ChipLensKind(mark) is not { } kind)
        {
            return;
        }

        ToggleDueLens(kind);
    }

    /// <summary>双击牌身＝编辑便签：把第一发点出来的镜头收掉，再开编辑框。</summary>
    private void ChipEditFrom(string nodeId, NoteMarks.Mark mark)
    {
        if (ChipLensKind(mark) is { } kind && _lensKind == kind)
        {
            ExitDueLens();
        }

        BeginEdit(nodeId);
    }

    /// <summary>这一档在回执里怎么说。</summary>
    private static string LensWords(string kind)
    {
        switch (kind)
        {
            case LensLate:
                return "逾期";
            case LensSoon:
                return "即将逾期";
            case "done:1":
                return "已完成";
            case "done:0":
                return "未完成";
        }

        if (kind.StartsWith("tag:", StringComparison.Ordinal))
        {
            return kind[4..];
        }

        if (kind.StartsWith("state:", StringComparison.Ordinal)
            && Enum.TryParse<NoteMarks.DueState>(kind[6..], out var state))
        {
            return DueLabel(state);
        }

        return kind;
    }

    /// <summary>
    /// 这一拍聚焦态的显示表：聚起来的有槽位（位移＝槽位 − 它本该在的位置），其余退成灰。
    /// 槽位每拍重算——聚合时改字会让格子长高，跟着重排才不叠在一起。
    /// </summary>
    private Dictionary<string, NodeVisual.Display> MergeLensDisplays(Dictionary<string, NodeVisual.Display> displays)
    {
        if (_lensKind is null)
        {
            _lensIds = [];
            _lensSlots.Clear();
            _lensOffsets.Clear();

            return displays;
        }

        _lensIds = LensMatches(_lensKind);
        _lensSlots = LayoutLensSlots(_lensIds);
        _lensOffsets.Clear();

        var merged = new Dictionary<string, NodeVisual.Display>(displays, StringComparer.Ordinal);

        foreach (var node in BoardNotes())
        {
            var baseDisplay = displays.TryGetValue(node.Id, out var planned) ? planned : NodeVisual.Display.None;

            if (_lensSlots.TryGetValue(node.Id, out var slot))
            {
                var dx = slot.X - _view.ToScreenX(node.X);
                var dy = slot.Y - _lensScrollY - _view.ToScreenY(node.Y);

                _lensOffsets[node.Id] = (dx, dy);
                merged[node.Id] = baseDisplay with { FoldDX = dx, FoldDY = dy, FoldedAway = false, LensFocus = true };
            }
            else
            {
                merged[node.Id] = baseDisplay with { LensDimmed = true };
            }
        }

        return merged;
    }

    /// <summary>
    /// 槽位：按"满行换行"排成一摞居中的行，卡宽用各自的屏幕宽（缩放后）。行内等距、行间 20，
    /// 整块竖直居中；视口太窄时最少留 32 的边。
    /// </summary>
    private Dictionary<string, (double X, double Y)> LayoutLensSlots(IReadOnlyList<string> ids)
    {
        var slots = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        if (ids.Count == 0)
        {
            return slots;
        }

        const double gapX = 20;
        const double gapY = 20;

        var boardW = Math.Max(320, BoardArea.ActualWidth);
        var boardH = Math.Max(240, BoardArea.ActualHeight);
        var available = Math.Max(160, boardW - 64);

        var rows = new List<List<(string Id, double W, double H)>>();
        var row = new List<(string Id, double W, double H)>();
        var rowWidth = 0.0;

        foreach (var id in ids)
        {
            if (NodeOf(id) is not { } node)
            {
                continue;
            }

            var width = Math.Max(40, node.Width * _view.Zoom);
            var height = Math.Max(32, node.Height * _view.Zoom);

            if (row.Count > 0 && rowWidth + gapX + width > available)
            {
                rows.Add(row);
                row = [];
                rowWidth = 0;
            }

            rowWidth += (row.Count > 0 ? gapX : 0) + width;
            row.Add((id, width, height));
        }

        if (row.Count > 0)
        {
            rows.Add(row);
        }

        var totalHeight = rows.Sum(r => r.Max(card => card.H)) + (Math.Max(0, rows.Count - 1) * gapY);

        // 一屏装得下就竖直居中；装不下就顶格起排、把超出量交给滚轮（他 11-03 报的"超过一屏看不了"）。
        var fits = 52 + totalHeight + 52 <= boardH;

        _lensMaxScroll = fits ? 0 : Math.Max(0, 52 + totalHeight + 52 - boardH);

        var y = fits ? Math.Max(52, (boardH - totalHeight) / 2) : 52;

        foreach (var current in rows)
        {
            var width = current.Sum(card => card.W) + ((current.Count - 1) * gapX);
            var x = Math.Max(32, (boardW - width) / 2);
            var rowHeight = current.Max(card => card.H);

            foreach (var (id, cardWidth, _) in current)
            {
                slots[id] = (x, y);
                x += cardWidth + gapX;
            }

            y += rowHeight + gapY;
        }

        return slots;
    }

    /// <summary>这一格此刻在聚焦投影里的位移（屏幕像素）；不在投影里＝(0,0)。编辑器要跟着它摆。</summary>
    private (double DX, double DY) LensOffsetFor(string id)
        => _lensKind is not null && _lensOffsets.TryGetValue(id, out var offset) ? offset : (0, 0);

    /// <summary>镜头里用滚轮翻槽位：两端限死；一屏装得下时这一发什么都不做（相机照旧不动）。</summary>
    private void ScrollLens(double dy)
    {
        if (_lensKind is null || _lensMaxScroll <= 0)
        {
            return;
        }

        var next = Math.Clamp(_lensScrollY + dy, 0, _lensMaxScroll);

        if (Math.Abs(next - _lensScrollY) < 0.01)
        {
            return;
        }

        _lensScrollY = next;

        ArrangeAll();
    }

    /// <summary>
    /// 镜头里的 Ctrl＋滚轮：缩放整块投影，槽位排版跟着重摞（<see cref="LayoutLensSlots"/> 读的就是当前缩放）。
    /// 限幅带外只许往带里走——进镜头前缩到 0.2 的人，往下滚不会再小，往上滚能回到带内。
    /// </summary>
    private void LensZoomStep(Windows.Foundation.Point screen, int delta)
    {
        var lo = Math.Min(LensZoomMin, _view.Zoom);
        var hi = Math.Max(LensZoomMax, _view.Zoom);

        if (!_view.ZoomAt(screen.X, screen.Y, delta > 0 ? WheelStep : 1 / WheelStep, lo, hi))
        {
            return;
        }

        ArrangeAll();

        // 重摞之后超出量变了：翻页位置按新的底收住。
        if (_lensScrollY > _lensMaxScroll)
        {
            _lensScrollY = _lensMaxScroll;
            ArrangeAll();
        }

        SaveSoon();
    }

    // ---------------------------------------------------------------------
    // 自检
    // ---------------------------------------------------------------------

    /// <summary>
    /// 左下角两档计数 + 聚焦投影。钉四件：计数只数"还欠着"的（完成的不算、+3 天外的"即将"不算）；
    /// 聚合只改投影（文档坐标一个字节不动）；被聚的落在槽位上、其余退成灰且不接鼠标、组框收起；
    /// 退出后位移归零、灰的恢复、压暗收起。编辑框跟着槽位走也在这里量。
    /// </summary>
    private void SelfTestDueLens(List<string> problems)
    {
        var (lateBefore, soonBefore) = DueCounts;
        var today = _view.Today;
        var originX = Math.Round(_view.ToWorldX(260));
        var originY = Math.Round(_view.ToWorldY(240));

        string Due(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var lateA = _document.Add(CanvasNodeKind.Text, originX, originY, 180, 90);
        lateA.Text = $"@d{Due(-2)}\n逾期甲";

        var lateB = _document.Add(CanvasNodeKind.Text, originX + 220, originY, 180, 90);
        lateB.Text = $"@d{Due(-5)}\n逾期乙";

        var soonA = _document.Add(CanvasNodeKind.Text, originX + 440, originY, 180, 90);
        soonA.Text = $"@d{Due(1)}\n即将甲";

        var soonB = _document.Add(CanvasNodeKind.Text, originX + 660, originY, 180, 90);
        soonB.Text = $"@d{Due(3)}\n即将乙";

        var doneLate = _document.Add(CanvasNodeKind.Text, originX, originY + 140, 180, 90);
        doneLate.Text = $"@d{Due(-3)}\n@b1\n做完的";

        var plain = _document.Add(CanvasNodeKind.Text, originX + 220, originY + 140, 180, 90);
        plain.Text = "没有日期";

        var group = _document.Add(CanvasNodeKind.Group, originX - 10, originY - 10, 200, 110);
        group.Label = "自检聚焦组";
        group.SetGroupMembers([lateA.Id]);

        var before = _document.ToJson();

        Rebuild();

        var (lateAfter, soonAfter) = DueCounts;

        if (lateAfter != lateBefore + 2 || soonAfter != soonBefore + 2)
        {
            problems.Add(
                $"selftest 聚焦: 计数 {lateBefore}/{soonBefore} → {lateAfter}/{soonAfter}，应各 +2 " +
                "（完成的那格与 +3 天外的都不该计入）");
        }

        if (DueLateCount.Text != lateAfter.ToString(CultureInfo.InvariantCulture)
            || DueSoonCount.Text != soonAfter.ToString(CultureInfo.InvariantCulture))
        {
            problems.Add($"selftest 聚焦: 状态条上的字与计数对不上（{DueLateCount.Text}/{DueSoonCount.Text}）");
        }

        if (DueStrip.Visibility != Visibility.Visible)
        {
            problems.Add("selftest 聚焦: 有逾期/即将逾期时状态条没出现");
        }

        // 镜头里右下按钮组与左上小地图要让开（他 11-03 报的重叠）：先记下进镜头前的样子，退出后要对上。
        var badgeBefore = BadgeHost.Visibility;
        var mapBefore = _miniMap.Root.Visibility;

        EnterDueLens(LensLate);

        if (LensKind != LensLate)
        {
            problems.Add("selftest 聚焦: 点「逾期」没有进入聚焦");
            ExitDueLens();
            Cleanup();

            return;
        }

        if (BadgeHost.Visibility != Visibility.Collapsed || _miniMap.Root.Visibility != Visibility.Collapsed)
        {
            problems.Add($"selftest 聚焦: 进镜头后右下按钮/小地图没让开（{BadgeHost.Visibility}/{_miniMap.Root.Visibility}）");
        }

        var (ids, offsets) = LensPlan;
        var offsetCount = offsets.Count;

        if (!ids.Contains(lateA.Id) || !ids.Contains(lateB.Id) || ids.Contains(soonA.Id) || ids.Contains(doneLate.Id))
        {
            problems.Add($"selftest 聚焦: 聚起来的是 {string.Join("、", ids)}——逾期那两格之外的多进来了");
        }

        if (offsets.Count == 0 || Math.Abs(LensDimOpacity - 0.38) > 0.01)
        {
            problems.Add($"selftest 聚焦: 位移 {offsets.Count} 格、压暗 {LensDimOpacity:0.##}（应非零且 0.38）");
        }

        if (_document.ToJson() != before)
        {
            problems.Add("selftest 聚焦: 聚合改了文档（它只能是投影，一个字节都不许动）");
        }

        // 槽位：被聚的那几格落点应该在视口里、并且真的被搬过。
        foreach (var id in ids)
        {
            if (!_realized.TryGetValue(id, out var lensed))
            {
                problems.Add($"selftest 聚焦: {id} 没被画出来");

                continue;
            }

            if (lensed.FoldTarget is not { } target || Math.Abs(target.X) < 1)
            {
                problems.Add($"selftest 聚焦: {id} 没落到槽位上（位移 {lensed.FoldTarget}）");
            }

            if (lensed.Opacity < 0.99)
            {
                problems.Add($"selftest 聚焦: 被聚的 {id} 反而被压暗了（{lensed.Opacity:0.##}）");
            }
        }

        // 没被聚的：退成灰、且不接鼠标（点它等于点空白）。
        foreach (var visual in BoardNotes().Where(note => !ids.Contains(note.Id))
                     .Select(note => _realized.TryGetValue(note.Id, out var v) ? v : null)
                     .Where(v => v is not null)
                     .Select(v => v!))
        {
            if (visual.Visibility != Visibility.Visible)
            {
                continue;   // 屏外被剔除的那几格不参与这一发
            }

            if (Math.Abs(visual.Opacity - 0.22) > 0.02 || visual.IsHitTestVisible)
            {
                problems.Add($"selftest 聚焦: 背景那一格没有退成灰（不透明 {visual.Opacity:0.##}、可点 {visual.IsHitTestVisible}）");
            }
        }

        if (_realized.TryGetValue(group.Id, out var groupVisual) && groupVisual.Visibility != Visibility.Collapsed)
        {
            problems.Add("selftest 聚焦: 组框没有收起来（他定的是聚合时不带分组）");
        }

        // 编辑框跟着槽位：点进被聚的一格，框要摆在那格屏幕上该在的地方。
        BeginEdit(lateA.Id);

        if (_editor is null)
        {
            problems.Add("selftest 聚焦: 被聚的格子开不出编辑器");
        }
        else
        {
            var offset = offsets[lateA.Id];
            var wantLeft = _view.ToScreenX(NodeOf(lateA.Id)!.X) + offset.DX;

            if (Math.Abs(Canvas.GetLeft(_editor) - wantLeft) > 0.6)
            {
                problems.Add($"selftest 聚焦: 编辑框在 {Canvas.GetLeft(_editor):0.#}，应跟着槽位到 {wantLeft:0.#}");
            }

            CloseEditor(commit: false);
        }

        // 换档：上一档要边走边回家，新的一档同时出发——这是"衔接感"那一格。
        EnterDueLens(LensSoon);

        if (LensKind != LensSoon || !LensPlan.Ids.Contains(soonA.Id) || LensPlan.Ids.Contains(lateA.Id))
        {
            problems.Add("selftest 聚焦: 从逾期切到即将逾期，聚起来的格子没换档");
        }
        else if (_realized.TryGetValue(lateA.Id, out var leaving)
                 && leaving.FoldTarget is { } home
                 && (Math.Abs(home.X) > 0.01 || Math.Abs(home.Y) > 0.01))
        {
            problems.Add($"selftest 聚焦: 换档时上一档没有往回走（落点 {home.X:0.#},{home.Y:0.#}）");
        }
        else if (_realized.TryGetValue(soonA.Id, out var arriving) && arriving.FoldTarget is null)
        {
            problems.Add("selftest 聚焦: 换档后新的一档没有落点");
        }
        else if (_realized.TryGetValue(lateA.Id, out var faded) && Math.Abs(faded.Opacity - 0.22) > 0.02)
        {
            problems.Add($"selftest 聚焦: 换档后上一档没有退成灰（{faded.Opacity:0.##}）");
        }
        else
        {
            Diagnostics.Write("selftest 聚焦: 换档时上一档回原位、新一档同时出发（两边落点都对）");
        }

        ExitDueLens();

        if (BadgeHost.Visibility != badgeBefore || _miniMap.Root.Visibility != mapBefore)
        {
            problems.Add($"selftest 聚焦: 退出后右下按钮/小地图没按原样还回（{BadgeHost.Visibility}/{_miniMap.Root.Visibility}）");
        }

        var resetOk = LensKind is null
            && Math.Abs(LensDimOpacity) < 0.01
            && _realized.TryGetValue(lateA.Id, out var back)
            && back.FoldTarget is { } backTarget
            && Math.Abs(backTarget.X) < 0.01
            && back.Opacity > 0.99;

        if (!resetOk)
        {
            problems.Add(
                $"selftest 聚焦: 退出后没收干净（kind={LensKind ?? "null"}、压暗 {LensDimOpacity:0.##}、" +
                $"位移 {(_realized.TryGetValue(lateA.Id, out var v2) ? v2.FoldTarget : null)}、" +
                $"不透明 {(_realized.TryGetValue(lateA.Id, out var v3) ? v3.Opacity : -1):0.##}）");
        }
        else
        {
            Diagnostics.Write(
                $"selftest 聚焦: 计数 {lateAfter}/{soonAfter}、聚 {ids.Count} 格（压暗 0.38、位移 {offsetCount}）、" +
                "退出三样都收回；文档零改动");
        }

        // 空档点击＝真 no-op（他 11-03 的闪屏）。把"即将"两格改成没日期，只留"逾期"，再走他那一串点法。
        soonA.Text = "即将甲（自检挪空）";
        soonB.Text = "即将乙（自检挪空）";
        Rebuild();

        if (DueCounts.Soon != 0 || DueCounts.Late < 1)
        {
            problems.Add($"selftest 聚焦: 空档前置没凑成（late={DueCounts.Late}、soon={DueCounts.Soon}，应 ≥1/0）");
        }

        EnterDueLens(LensLate);

        var guardedIds = LensPlan.Ids.Count;

        EnterDueLens(LensSoon);

        if (LensKind != LensLate || LensPlan.Ids.Count != guardedIds || Math.Abs(LensDimOpacity - 0.38) > 0.01)
        {
            problems.Add(
                $"selftest 聚焦: 聚焦里点空档动了状态（kind={LensKind ?? "null"}、" +
                $"聚 {LensPlan.Ids.Count}/{guardedIds} 格、压暗 {LensDimOpacity:0.##}）——投影不许被搅");
        }

        // 再点回有格子的那档：走的是"同档退出"，不是重新进入——不许从原位重播动画。
        ToggleDueLens(LensLate);

        if (LensKind is not null || LensPlan.Ids.Count != 0 || Math.Abs(LensDimOpacity) > 0.01)
        {
            problems.Add("selftest 聚焦: 空档绕一圈后点回原档没有干净退出");
        }
        else
        {
            Diagnostics.Write($"selftest 聚焦: 空档 no-op（聚住 {guardedIds} 格不动）、点回原档干净退出");
        }

        // 进镜头那一下不能把选择框留在原处（他 11-03 报的第一件）：框画的是文档位置，格子聚走了它自己不会跟。
        SelectOnly(lateA.Id);
        ArrangeAll();

        if (SelectionOutline.Width <= 0)
        {
            problems.Add("selftest 聚焦: 前置没立起来——选中了格子，选择框却没画");
        }

        EnterDueLens(LensLate);

        if (SelectionOutline.Width > 0 || SelectionInner.Width > 0)
        {
            problems.Add($"selftest 聚焦: 进镜头后选择框没收回（描边宽 {SelectionOutline.Width:0.#}）");
        }

        ExitDueLens();

        // 溢出翻页（他 11-03 报的第二件）：聚起来超过一屏时，滚轮要把这一摞槽位翻得到底，两端限死。
        var many = new List<CanvasNode>();

        for (var i = 0; i < 42; i++)
        {
            var overflow = _document.Add(
                CanvasNodeKind.Text, originX + (i % 7) * 220, originY + 320 + (i / 7) * 120, 180, 90);

            overflow.Text = $"@d{Due(-1 - (i % 3))}\n溢出{i}";
            many.Add(overflow);
        }

        Rebuild();
        EnterDueLens(LensLate);

        if (_lensMaxScroll <= 0)
        {
            problems.Add($"selftest 聚焦溢出: 46 格没撑出滚动量（max {_lensMaxScroll:0.#}、视口高 {BoardArea.ActualHeight:0}）");
        }
        else
        {
            var homeDy = LensPlan.Offsets[lateA.Id].DY;

            ScrollLens(_lensMaxScroll / 2);

            var midDy = LensPlan.Offsets[lateA.Id].DY;

            if (Math.Abs(homeDy - midDy - _lensMaxScroll / 2) > 1)
            {
                problems.Add($"selftest 聚焦溢出: 滚一发没把槽位翻半屏（{homeDy:0.#}→{midDy:0.#}，应差 {_lensMaxScroll / 2:0.#}）");
            }

            ScrollLens(_lensMaxScroll * 4);

            if (Math.Abs(_lensScrollY - _lensMaxScroll) > 0.01)
            {
                problems.Add($"selftest 聚焦溢出: 滑过底没收住（{_lensScrollY:0.#}/{_lensMaxScroll:0.#}）");
            }

            ScrollLens(-_lensMaxScroll * 4);

            if (Math.Abs(_lensScrollY) > 0.01)
            {
                problems.Add($"selftest 聚焦溢出: 滑回顶没收住（{_lensScrollY:0.#}）");
            }

            // Ctrl＋滚轮：缩放整块投影、排版跟着重摞，两端限死（他 11-03 补的限幅令）。
            // 起点先归到带内的 100%：自检跑到这里时相机可能被前面的档位挪过，带外起步量的是"回带"不是"步进"。
            var zoomBefore = _view.Zoom;
            var panBefore = (_view.PanX, _view.PanY);
            _view.Zoom = 1;
            var startZoom = _view.Zoom;
            ArrangeAll();
            var anchor = new Windows.Foundation.Point(BoardArea.ActualWidth / 2, BoardArea.ActualHeight / 2);
            var firstDx = LensPlan.Offsets[lateA.Id].DX;

            LensZoomStep(anchor, 120);

            if (Math.Abs(_view.Zoom - startZoom * WheelStep) > 0.001)
            {
                problems.Add($"selftest 聚焦缩放: 一发没按档位放大（{startZoom:0.###}→{_view.Zoom:0.###}，档 {WheelStep}）");
            }
            else if (Math.Abs(LensPlan.Offsets[lateA.Id].DX - firstDx) < 0.5)
            {
                problems.Add("selftest 聚焦缩放: 缩放后槽位没重摞（落点位移纹丝不动）");
            }

            for (var i = 0; i < 30; i++)
            {
                LensZoomStep(anchor, 120);
            }

            if (_view.Zoom > LensZoomMax + 0.001)
            {
                problems.Add($"selftest 聚焦缩放: 放大越过上限没限死（{_view.Zoom:0.##}>{LensZoomMax:0.##}）");
            }

            for (var i = 0; i < 60; i++)
            {
                LensZoomStep(anchor, -120);
            }

            if (_view.Zoom < Math.Min(LensZoomMin, startZoom) - 0.001)
            {
                problems.Add($"selftest 聚焦缩放: 缩小越过下限没限死（{_view.Zoom:0.##}<{LensZoomMin:0.##}）");
            }

            (_view.PanX, _view.PanY, _view.Zoom) = (panBefore.PanX, panBefore.PanY, zoomBefore);
            ArrangeAll();

            Diagnostics.Write($"selftest 聚焦缩放: 一档 ×{WheelStep:0.##}、上下限 {LensZoomMin:0.##}–{LensZoomMax:0.##} 收住、排版跟着重摞");
        }

        ExitDueLens();

        foreach (var overflow in many)
        {
            _document.Remove(overflow.Id);
        }

        Rebuild();

        Cleanup();

        void Cleanup()
        {
            foreach (var temp in new[] { group, plain, doneLate, soonB, soonA, lateB, lateA })
            {
                _document.Remove(temp.Id);
            }

            Rebuild();
        }
    }
}
