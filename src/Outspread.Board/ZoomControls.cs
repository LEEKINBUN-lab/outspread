using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// The bottom-right cluster: funnel · undo · redo · zoom out · the ratio · zoom in — one hairline
/// container whose members carry no chrome of their own.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are <see cref="Badge"/>'s, and they were read off <c>design/feel.html#badge</c> with
/// <c>getComputedStyle</c> rather than chosen here. That is the whole point of this file: the first
/// version of this corner was three default <see cref="Button"/>s written straight into XAML, which
/// meant the screen showed Microsoft's control design (grey boxes, borders, 20px symbols) beside the
/// prototype's one quiet pill — and the complaint that followed was fair.
/// </para>
/// <para>
/// Zooming about the centre of what is on screen, not about the pointer: the pointer is not available
/// here. The step is the same <see cref="WheelStep"/> the wheel uses, so the two controls cannot
/// disagree about how much one click is.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(WheelStep);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / WheelStep);

    private void OnZoomActualSize(object sender, RoutedEventArgs e)
    {
        // 只把比例改到 100% 是不够的：视野若正停在空白处，放大以后还是一片空白（10-25 他
        // 第二次报）。这一个按钮的文案就是"回到 100%"，所以它和 Ctrl＋0 走同一条复位（空格 10-25 撤了）。
        ResetView();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void ZoomBy(double factor) => ZoomTo(_view.Zoom * factor);

    private void ZoomTo(double goal)
    {
        var width = BoardArea.ActualWidth;
        var height = BoardArea.ActualHeight;

        var clamped = Math.Clamp(goal, MinZoom, MaxZoom);

        if (Math.Abs(clamped - _view.Zoom) < 0.0001 || width <= 0 || height <= 0)
        {
            return;
        }

        // Keep the world point at the centre of the viewport where it is.
        var centreWorldX = _view.ToWorldX(width / 2);
        var centreWorldY = _view.ToWorldY(height / 2);

        _view.Zoom = clamped;
        _view.PanX = (width / 2) - (centreWorldX * clamped);
        _view.PanY = (height / 2) - (centreWorldY * clamped);

        ArrangeAll();
        ProjectSelection();
        SaveSoon();
    }

    /// <summary>The ratio button always says what the wheel and the buttons just did.</summary>
    private void RefreshZoomLabel()
    {
        if (ZoomLevelText is null)
        {
            return;
        }

        ZoomLevelText.Text = Percent();

        // 开板时那句「N 个便签 · 缩放 X%」如果还亮着，也要跟着改：右上角已经写着 80%，
        // 中间那句还停在 125%，就是两块牌子互相打脸（10-26 取证图里读到的这一格）。
        if (SayHost.Visibility == Visibility.Visible)
        {
            var said = _sayRow.Text;
            var at = said.LastIndexOf("缩放 ", StringComparison.Ordinal);

            if (at >= 0 && said.Contains("个便签", StringComparison.Ordinal))
            {
                _sayRow.Text = string.Concat(said.AsSpan(0, at), "缩放 ", Percent());
            }
        }
    }

    /// <summary>
    /// Paints the cluster from the prototype's numbers. Called from the same pass that arranges the
    /// board, so a theme flip cannot leave one member wearing the other theme's ink.
    /// </summary>
    private void StyleBadge()
    {
        var dark = NoteStyling.IsDark;
        var line = new SolidColorBrush(NoteStyling.WithAlpha(Badge.LineColour(dark), Badge.LineAlpha(dark)));
        var muted = new SolidColorBrush(NoteStyling.ToColor(Badge.Muted(dark)));

        BadgeHost.Background = new SolidColorBrush(NoteStyling.ToColor(Badge.PanelColour(dark)));
        BadgeHost.BorderBrush = line;
        BadgeHost.BorderThickness = new Thickness(1);
        BadgeHost.CornerRadius = new CornerRadius(Chrome.Radius);
        BadgeHost.Padding = new Thickness(Badge.ContainerPaddingPx);

        foreach (var button in new[] { UndoButton, RedoButton, ZoomOutButton, ZoomLevelButton, ZoomInButton })
        {
            // 真 Button 留着（键盘按得到、焦点走得进），但控件自己的边框与底色一律剥掉。
            button.Padding = new Thickness(Badge.MemberPaddingX, Badge.MemberPaddingY, Badge.MemberPaddingX, Badge.MemberPaddingY);
            button.MinWidth = 0;
            button.MinHeight = Badge.LineHeight + (2 * Badge.MemberPaddingY);
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(Badge.MemberRadiusPx);
            button.FontSize = Badge.FontSize;
            // 字形走系统的 Segoe UI 那一串，与原型量到的 font-family 同序（正文用的是雅黑，这两组不同源是故意的）。
            button.Foreground = muted;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
        }

        // 比例在原型里是一枚 span（52 宽、没有内距），不是按钮；这边它仍可点（点＝100%），
        // 所以形状照 span 走、命中区靠 MinHeight 28 保住——宽度 52、高度 28，两样都过 24。
        ZoomLevelButton.Padding = new Thickness(0);

        // 比例是这一组里唯一说话的一句正文：原型给它 --text，其余成员在 --muted 里等着。
        ZoomLevelText.FontSize = Badge.FontSize;
        ZoomLevelText.LineHeight = Badge.LineHeight;
        ZoomLevelText.MinWidth = Badge.RatioMinWidth;
        ZoomLevelText.Foreground = new SolidColorBrush(NoteStyling.ToColor(Badge.Ink(dark)));

        foreach (var separator in new[] { BadgeSep2 })
        {
            separator.Fill = line;
            separator.Width = Badge.SeparatorWidthPx;
            separator.Margin = new Thickness(Badge.SeparatorMarginX, Badge.SeparatorMarginY, Badge.SeparatorMarginX, Badge.SeparatorMarginY);
            separator.VerticalAlignment = VerticalAlignment.Stretch;
        }

        RefreshHistoryButtons();

        // 那句浮出来的话用的就是浮层那一套皮；它那一行是菜单那一枚控件（建窗时搭好，这里只穿皮）。
        ChromeStyling.ApplyFloatingChrome(SayHost);
    }

    /// <summary>回执那一行：建窗时搭好的菜单行，之后只换字。</summary>
    private PanelKit.SheetRow _sayRow = null!;

    /// <summary>
    /// The only way anything speaks on screen now: the floating chip appears with the sentence and is
    /// gone the next time the hand touches the board. Routine receipts (已存盘、选中 N 个) stopped
    /// writing here at all — a save that worked is not news.
    /// </summary>
    private void Say(string message)
    {
        _sayRow.Text = message;

        SayHost.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

        // 说给他看的每一句都留一行底：他回报"没生效／效果不对"时，我去日志读那一句的读数，
        // 不用猜也不用反问。自检那一发把 Say 当 assertion 台账用，喊得凶，不灌进真日志。
        if (message.Length > 0 && !App.SelfTest)
        {
            Diagnostics.Write($"say: {message}");
        }
    }

    /// <summary>自检读的那句：浮层上此刻写着什么。</summary>
    internal string SayTextForSelfTest => _sayRow.Text;

    /// <summary>点那句话说完的浮层＝"知道了，收起"。它不是一行日志的复读，所以也不该一直占着。</summary>
    private void OnSayPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;

        SayHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Undo and redo are the only members whose availability is not theirs: they say what the history
    /// stack holds, in the same words the status line uses, and go quiet at 38% when there is nothing.
    /// </summary>
    private void RefreshHistoryButtons()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        UndoButton.Opacity = _history.CanUndo ? 1d : Badge.DisabledOpacity;
        RedoButton.Opacity = _history.CanRedo ? 1d : Badge.DisabledOpacity;

        ToolTipService.SetToolTip(UndoButton, _history.NextUndoLabel is { } undo ? $"撤回：{undo}（Ctrl＋Z）" : "没有可撤回的一步");
        ToolTipService.SetToolTip(RedoButton, _history.NextRedoLabel is { } redo ? $"重做：{redo}（Ctrl＋Y）" : "没有可重做的一步");
    }
}
