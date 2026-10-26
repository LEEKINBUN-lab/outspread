using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;
using Windows.Storage;
using Windows.UI;

namespace Outspread.Board;

internal enum EntityBadgeTone
{
    Linked,
    Owned,
    Neutral,
    Missing,
}

/// <summary>A folder card's preview tile: an image path, or a video whose shell thumb loads later.</summary>
internal readonly record struct EntityThumb(string Path, bool IsVideo);

/// <summary>Everything the card header paints, resolved once per rebuild.</summary>
internal sealed record EntityCardInfo(
    EntityCardKind Kind,
    string Title,
    string Meta,
    string Badge,
    EntityBadgeTone BadgeTone,
    /// <summary>停在牌子上时的那句释义。警示走这里，不再在卡面上占一行。</summary>
    string? Tip,
    IReadOnlyList<EntityThumb> Thumbnails,
    string? TargetPath,
    Uri? WebUri,
    TableGrid? Table)
{
    public bool CanOpen => TargetPath is not null || WebUri is not null;

    public bool CanReveal => TargetPath is not null;
}

internal static class EntityCardLayout
{
    /// <summary>
    /// Header band in world units, shared by painting, line fitting and the editor. Two rows:
    /// 名称＋牌子, then 元信息＋动作. Both rows are always there — hovering only fades things in, so a
    /// card never changes size under the pointer（他 10-25：不喜欢卡片动来动去，可以留空白）。
    /// </summary>
    public const double BaseWorld = 56;

    /// <summary>One spreadsheet row in world units: the grid's own height, so the note under it gets the rest.</summary>
    public const double TableLineWorld = 30;

    public const double ThumbWorld = 54;

    public const double MinWidth = 220;

    public static double TableWorld(TableGrid grid) => Math.Max(1, grid.RowCount) * TableLineWorld;

    public static double HeaderWorldFor(EntityCardInfo info) => BaseWorld + (info.Thumbnails.Count > 0 ? ThumbWorld : 0);

    /// <summary>What sits above the editable body: the entity header, or the table itself.</summary>
    public static double ContentTopWorld(CanvasNode node, EntityCardInfo? info)
    {
        if (info?.Table is { } grid)
        {
            return TableWorld(grid);
        }

        return info is null || EntityCards.KindOf(node) == EntityCardKind.None ? 0 : HeaderWorldFor(info);
    }
}

internal static class EntityCardPalette
{
    public static Color TypeColor(EntityCardKind kind, bool dark) => kind switch
    {
        EntityCardKind.File => dark ? Color.FromArgb(255, 0xFF, 0x9B, 0x91) : Color.FromArgb(255, 0xB2, 0x3B, 0x33),
        EntityCardKind.Folder => dark ? Color.FromArgb(255, 0xC8, 0x9A, 0x3F) : Color.FromArgb(255, 0x9A, 0x72, 0x14),
        EntityCardKind.Web => dark ? Color.FromArgb(255, 0x64, 0xA4, 0xF7) : Color.FromArgb(255, 0x31, 0x82, 0xED),
        _ => dark ? Color.FromArgb(255, 0xB9, 0xBE, 0xC5) : Color.FromArgb(255, 0x6A, 0x70, 0x78),
    };

    public static Color BadgeColor(EntityBadgeTone tone, bool dark) => tone switch
    {
        EntityBadgeTone.Linked => dark ? Color.FromArgb(255, 0x64, 0xA4, 0xF7) : Color.FromArgb(255, 0x31, 0x82, 0xED),
        EntityBadgeTone.Owned => dark ? Color.FromArgb(255, 0x79, 0xCF, 0xA0) : Color.FromArgb(255, 0x2C, 0x6F, 0x4B),
        EntityBadgeTone.Missing => dark ? Color.FromArgb(255, 0xFF, 0x9B, 0x91) : Color.FromArgb(255, 0xB2, 0x3B, 0x33),
        _ => dark ? Color.FromArgb(255, 0xB9, 0xBE, 0xC5) : Color.FromArgb(255, 0x5A, 0x60, 0x68),
    };

    public static SolidColorBrush Tint(Color color, byte alpha)
        => new(Color.FromArgb(alpha, color.R, color.G, color.B));
}

/// <summary>
/// The part of an entity card above the editable body.
/// </summary>
/// <remarks>
/// 10-25 他打回"太拥挤"之后的排法：一行只解决一件事。名称那一行不再和元信息、动作抢宽度；
/// 类型色块删掉（它写的 PDF 和元信息里的 PDF 是同一句话）；警示那一行收进牌子的 tooltip。
/// 第二行（元信息＋动作）的高度<b>常驻</b>，悬停只把这两样淡进来——卡不许在指针下变高。
/// </remarks>
internal sealed class EntityHeader : Grid
{
    private readonly EntityCardInfo _info;
    private readonly Border _glyphHost;
    private readonly TextBlock _title;
    private readonly TextBlock _meta;
    private readonly Border _badge;
    private readonly Ellipse _badgeDot;
    private readonly TextBlock _badgeText;
    private readonly StackPanel _actions;
    private readonly Button _open;
    private readonly Button _reveal;
    private readonly StackPanel _thumbs;
    private readonly List<Border> _thumbFrames = [];

    private string _applyKey = string.Empty;
    private string _thumbKey = string.Empty;
    private bool _revealed;

    public EntityHeader(EntityCardInfo info, Action open, Action reveal)
    {
        _info = info;

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _glyphHost = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        _title = new TextBlock
        {
            Text = info.Title,
            FontFamily = NoteStyling.NoteFont,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _meta = new TextBlock
        {
            Text = info.Meta,
            FontFamily = NoteStyling.NoteFont,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };

        _badgeDot = new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Center };
        _badgeText = new TextBlock
        {
            Text = info.Badge,
            FontFamily = NoteStyling.NoteFont,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeContent.Children.Add(_badgeDot);
        badgeContent.Children.Add(_badgeText);

        _badge = new Border
        {
            Child = badgeContent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        if (info.Tip is { Length: > 0 } tip)
        {
            // 停在牌子上就出释义，且不自己走（WCAG 1.4.13 的"指针可移进去"原生 tooltip 做不到，
            // 记在剩余风险里；至少不让它在读完之前消失）。
            // 这版投影的 ToolTip 没有 InitialShowDelay / ShowDelay / Timeout（UWP 上有，WinUI 3 砍了），
            // 所以"停在牌子上立刻出、且不自走"做不到：只剩平台默认的延迟与超时。记进剩余风险，不当已达成。
            ToolTipService.SetToolTip(_badge, tip);
        }

        _open = IconAction(FluentIcons.Open, "打开", open);
        _reveal = IconAction(FluentIcons.FolderOpen, "在文件夹中打开", reveal);

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _actions.Children.Add(_open);
        _actions.Children.Add(_reveal);

        _thumbs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        SetColumn(_glyphHost, 0);
        SetRow(_glyphHost, 0);
        SetColumn(_title, 1);
        SetRow(_title, 0);
        SetColumn(_badge, 2);
        SetRow(_badge, 0);
        SetColumn(_meta, 1);
        SetRow(_meta, 1);
        SetColumn(_actions, 2);
        SetRow(_actions, 1);
        SetRow(_thumbs, 2);
        SetColumn(_thumbs, 0);
        SetColumnSpan(_thumbs, 3);

        Children.Add(_glyphHost);
        Children.Add(_title);
        Children.Add(_badge);
        Children.Add(_meta);
        Children.Add(_actions);
        Children.Add(_thumbs);
    }

    public double HeaderWorld => EntityCardLayout.HeaderWorldFor(_info);

    /// <summary>自检读的那两档透明度：静止必须是 0，否则"悬停才给动作"这句话没落地。</summary>
    internal double ActionsOpacity => _actions.Opacity;

    internal double MetaOpacity => _meta.Opacity;

    internal string BadgeTip => ToolTipService.GetToolTip(_badge) as string ?? string.Empty;

    private Button IconAction(string icon, string name, Action onClick)
    {
        var button = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = FluentIcons.Glyph(icon, 15, Colors.Gray),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => onClick();
        button.DoubleTapped += (_, pointer) => pointer.Handled = true;
        return button;
    }

    /// <summary>悬停／键盘聚焦：把第二行淡进来。只改透明度，一格尺寸都不动。</summary>
    public void Reveal(bool on)
    {
        if (_revealed == on)
        {
            return;
        }

        _revealed = on;
        _actions.IsHitTestVisible = on;

        Fade(_meta, on ? 1 : 0, on ? ControlKit.HoverInMs : ControlKit.HoverOutMs);
        Fade(_actions, on ? 1 : 0, on ? ControlKit.HoverInMs : ControlKit.HoverOutMs);
    }

    private static void Fade(UIElement target, double to, double ms)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var board = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        board.Children.Add(animation);
        board.Begin();
    }

    public void Apply(double zoom, Color ink, Color muted, bool dark)
    {
        var key = $"{zoom:0.####}|{ink}|{muted}|{dark}";

        if (string.Equals(key, _applyKey, StringComparison.Ordinal))
        {
            return;
        }

        _applyKey = key;
        var scale = Math.Max(0.35, zoom);
        var badgeColor = EntityCardPalette.BadgeColor(_info.BadgeTone, dark);

        // 类型不再写两遍：一枚 18px 的官方形状替掉色块，名称当主角。
        var glyph = _info.Kind switch
        {
            EntityCardKind.Folder => FluentIcons.Folder,
            EntityCardKind.Web => FluentIcons.Link,
            _ => FluentIcons.Document,
        };

        _glyphHost.Child = FluentIcons.Glyph(glyph, 18 * scale, EntityCardPalette.TypeColor(_info.Kind, dark));
        _glyphHost.Width = 20 * scale;
        _glyphHost.Height = 22 * scale;
        _glyphHost.Margin = new Thickness(0, 0, 9 * scale, 0);

        _title.FontSize = Math.Max(5, 14 * scale);
        _title.LineHeight = Math.Max(6, 19 * scale);
        _title.Foreground = new SolidColorBrush(ink);

        _meta.FontSize = Math.Max(4, 11.5 * scale);
        _meta.LineHeight = Math.Max(5, 15 * scale);
        _meta.Foreground = new SolidColorBrush(muted);
        _meta.Margin = new Thickness(0, 2 * scale, 0, 0);
        _meta.Opacity = _revealed ? 1 : 0;

        _badge.Padding = new Thickness(7 * scale, 0, 7 * scale, 0);
        _badge.Height = 21 * scale;
        _badge.CornerRadius = new CornerRadius(11 * scale);
        _badge.Background = EntityCardPalette.Tint(badgeColor, 0x1C);
        _badge.Margin = new Thickness(8 * scale, 0, 0, 0);
        _badgeDot.Width = 5 * scale;
        _badgeDot.Height = 5 * scale;
        _badgeDot.Fill = new SolidColorBrush(badgeColor);
        _badgeText.FontSize = Math.Max(4, 11 * scale);
        _badgeText.Foreground = new SolidColorBrush(badgeColor);

        _actions.Visibility = _info.CanOpen || _info.CanReveal ? Visibility.Visible : Visibility.Collapsed;
        _open.Visibility = _info.CanOpen ? Visibility.Visible : Visibility.Collapsed;
        _reveal.Visibility = _info.CanReveal ? Visibility.Visible : Visibility.Collapsed;
        _actions.Opacity = _revealed ? 1 : 0;

        foreach (var (button, icon) in new[] { (_open, FluentIcons.Open), (_reveal, FluentIcons.FolderOpen) })
        {
            button.Width = 26 * scale;
            button.Height = 26 * scale;
            button.CornerRadius = new CornerRadius(5 * scale);
            button.Content = FluentIcons.Glyph(icon, 15 * scale, muted);
        }

        BuildThumbs(scale, muted);
    }

    /// <summary>
    /// 缩略图按<b>这一排文件</b>建一次，缩放那一拍只改尺寸。原来每次 Apply 都重建一遍：位图解码是
    /// 异步的，于是每一次缩放都有一帧把四格画成空白，抓图（和盯着看的人）都会撞上。
    /// </summary>
    private void BuildThumbs(double scale, Color muted)
    {
        var key = string.Join('|', _info.Thumbnails.Select(thumb => thumb.Path));

        if (!string.Equals(key, _thumbKey, StringComparison.Ordinal))
        {
            _thumbKey = key;
            _thumbFrames.Clear();
            _thumbs.Children.Clear();

            foreach (var thumb in _info.Thumbnails.Take(4))
            {
                var frame = new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(NoteStyling.FallbackLine()),
                    Background = EntityCardPalette.Tint(muted, 0x12),
                };

                if (thumb.IsVideo)
                {
                    frame.Child = FluentIcons.Glyph(FluentIcons.Play, 15 * scale, muted);
                    _ = LoadVideoThumbAsync(thumb.Path, frame, scale);
                }
                else if (TryImage(thumb.Path) is { } bitmap)
                {
                    var image = new Image { Stretch = Stretch.UniformToFill, Source = bitmap };
                    image.ImageFailed += (_, args) => Diagnostics.Write($"folder thumb failed {thumb.Path}: {args.ErrorMessage}");
                    frame.Child = image;
                }
                else
                {
                    // 读不出的那一张不许把整次绘制带下来：留一个空格子，比整块板子崩掉诚实。
                    continue;
                }

                _thumbFrames.Add(frame);
                _thumbs.Children.Add(frame);
            }
        }

        var size = 44 * scale;

        foreach (var frame in _thumbFrames)
        {
            frame.Width = size;
            frame.Height = size;
            frame.CornerRadius = new CornerRadius(3 * scale);
        }

        _thumbs.Visibility = _thumbFrames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _thumbs.Margin = new Thickness(0, 8 * scale, 0, 0);
    }

    /// <summary>和 <see cref="NodeVisual.LoadImage"/> 同一条解码路：先建控件、再把 UriSource 赋上去。</summary>
    private static BitmapImage? TryImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = 92 };
            bitmap.UriSource = new Uri(path, UriKind.Absolute);

            return bitmap;
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"folder thumb unreadable {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The shell thumb for a video, fetched off the layout pass so a pan never waits on it.</summary>
    private static async Task LoadVideoThumbAsync(string path, Border frame, double scale)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, (uint)(44 * scale));

            if (thumbnail is null)
            {
                return;
            }

            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);

            if (frame.Child is Viewbox)
            {
                frame.Child = new Image { Stretch = Stretch.UniformToFill, Source = image };
            }
        }
        catch (Exception ex)
        {
            // 播放符号留着：预览读不出某一条视频，它仍然是预览。这一路是绘制时随手起的，
            // 不能因为 shell 给了个 COM 失败就把异常留成没人看的任务。
            Diagnostics.Write($"video thumb failed {path}: {ex.Message}");
        }
    }
}
