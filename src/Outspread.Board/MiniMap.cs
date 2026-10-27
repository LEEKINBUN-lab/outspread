using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 小地图：画板区左上角的一枚方框。它是读数，不是控制器（他 10-28 定档）。
/// </summary>
/// <remarks>
/// <para>
/// 主画板右键拖动平移、滚轮缩放、点「看到全部／回到 100%」、切板——这一枚跟着读；它自己一次输入
/// 都不吃：<see cref="Root"/> 整块 <c>IsHitTestVisible=false</c>，挂在 BoardArea 之上、PanLayer 之外。
/// 所以没有「点小地图跳转」，也没有「在地图上滚滚轮」——那两条是原型里被否掉的旧交互，没搬。
/// </para>
/// <para>
/// 外观定档「就是一个方框」：皮只走 <see cref="ChromeStyling.ApplyFloatingChrome"/>（浮层不许手搓），
/// 里面就是地图那层画布。原型的标题条与底部读数条整条撤了——框里没有任何文字，
/// 这条由界面自检钉住（子树里读到一枚 TextBlock 就红）。
/// </para>
/// <para>
/// 性能与卡顿工单是同一条：<see cref="Sync"/> 只在文档几何（增删、移动、改大小、配色）或主题真的
/// 变了的那一拍拆建小块——对账是 O(n) 比数，拆建才是贵的一发；<see cref="SyncViewport"/> 每一帧
/// 改的只有视口矩形的四个数。地图不排版文字，900 格也只是 900 个矩形。
/// </para>
/// </remarks>
internal sealed class MiniMap
{
    /// <summary>地图那层的尺寸：原型 <c>.mini</c> 的 184 宽、<c>.body</c> 的 126 高。</summary>
    public const double MapWidthPx = 184;
    public const double MapHeightPx = 126;

    /// <summary>原型给小块定的下限：max(1.5, w·s) × max(1.2, h·s)。缩得再远，一格也剩一个点。</summary>
    private const double MinBlockWidthPx = 1.5;
    private const double MinBlockHeightPx = 1.2;

    /// <summary>原型 <c>.mini .vp</c>：1.5px 的强调色描边。</summary>
    private const double ViewportStrokePx = 1.5;

    /// <summary>原型小块的 <c>opacity:.85</c>。</summary>
    private const double BlockOpacity = 0.85;

    private readonly Border _root = new();

    // 地图层裁剪到自己那圈：视口矩形探出内容包围盒（相机停在空白处）时，
    // 它该被方框整条收掉，而不是糊到浮层的皮上去。这一投影里 Canvas 没有 ClipToBounds，
    // 用的是矩形 Clip。
    private readonly Canvas _map = new()
    {
        Width = MapWidthPx,
        Height = MapHeightPx,
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, MapWidthPx, MapHeightPx) },
    };
    private readonly Rectangle _viewport = new();
    private readonly Dictionary<string, Rectangle> _blocks = new(StringComparer.Ordinal);

    private string _stamp = string.Empty;
    private bool _hasContent;
    private double _originX;
    private double _originY;
    private double _scale;

    public MiniMap()
    {
        ChromeStyling.ApplyFloatingChrome(_root);

        // 纯读数：整块不吃输入，指针从它头上过＝从板子上过。
        _root.IsHitTestVisible = false;
        _root.HorizontalAlignment = HorizontalAlignment.Left;
        _root.VerticalAlignment = VerticalAlignment.Top;
        _root.Margin = new Thickness(10, 10, 0, 0);
        _root.Child = _map;

        // 视口矩形说「你现在在看哪一块」：语言取自选中环与框选那层半透明，不手搓第三种蓝。
        _viewport.Stroke = ChromeStyling.Ring;
        _viewport.StrokeThickness = ViewportStrokePx;
        _viewport.Fill = ChromeStyling.MarqueeFill;
        _viewport.IsHitTestVisible = false;
        _viewport.Visibility = Visibility.Collapsed;

        // 矩形永远压在小块之上（Children 的末尾＝最上层），拆建小块时它会跟着 Clear，
        // 所以 Sync 每一拍都重新添回来。
        _map.Children.Add(_viewport);
    }

    /// <summary>挂在 BoardArea 上的那枚方框。</summary>
    public FrameworkElement Root => _root;

    /// <summary>视口矩形：手势期间唯一被允许动的东西，自检直接读它的四个数。</summary>
    public Rectangle Viewport => _viewport;

    /// <summary>小块集合真正被拆建过几次。平移/缩放期间这个数一格都不许跳——那是卡顿工单的小地图版本。</summary>
    public int BlocksRebuilds { get; private set; }

    /// <summary>当前地图把世界投进方框用的那把尺与那个原点（s ＝ min(框宽/内容宽, 框高/内容高)）。</summary>
    public double Scale => _scale;

    public double OriginX => _originX;

    public double OriginY => _originY;

    /// <summary>某一格在这一拍画出来的小矩形（没有＝null）。自检按号点名。</summary>
    public Rectangle? BlockFor(string id) => _blocks.TryGetValue(id, out var block) ? block : null;

    /// <summary>
    /// 把这一拍的文档几何对进来。账（主题＋每格 id/坐标/尺寸/配色）没变就一次都不拆建——
    /// 提交那一拍（ArrangeAll）常常只是相机动了，几何没动。
    /// </summary>
    public void Sync(IReadOnlyList<CanvasNode> notes)
    {
        var stamp = Stamp(notes);

        if (stamp == _stamp)
        {
            return;
        }

        _stamp = stamp;
        BlocksRebuilds++;

        // 主题翻转也走到这一发（账里带着主题位）：重穿一次皮、重取一次颜色令牌，还是那个方法，
        // 不是手搓第二份。
        ChromeStyling.ApplyFloatingChrome(_root);

        _map.Children.Clear();
        _blocks.Clear();

        if (notes.Count == 0)
        {
            _hasContent = false;
            _map.Children.Add(_viewport);
            return;
        }

        _originX = notes.Min(node => node.X);
        _originY = notes.Min(node => node.Y);

        var contentWidth = Math.Max(notes.Max(node => node.X + node.Width) - _originX, 1);
        var contentHeight = Math.Max(notes.Max(node => node.Y + node.Height) - _originY, 1);

        // 原型那一发：s = min(mw / W, mh / H)，内容整幅塞进方框，两个轴都装得下的那一档。
        _scale = Math.Min(MapWidthPx / contentWidth, MapHeightPx / contentHeight);
        _hasContent = true;

        foreach (var note in notes)
        {
            var block = new Rectangle
            {
                Width = Math.Max(MinBlockWidthPx, note.Width * _scale),
                Height = Math.Max(MinBlockHeightPx, note.Height * _scale),
                Fill = BlockBrush(note.Color),
                Opacity = BlockOpacity,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(block, (note.X - _originX) * _scale);
            Canvas.SetTop(block, (note.Y - _originY) * _scale);

            _blocks[note.Id] = block;
            _map.Children.Add(block);
        }

        // 矩形最后添：永远压在小块之上。
        _map.Children.Add(_viewport);
    }

    /// <summary>
    /// 每一帧只走这里：把此刻（或预览中）的相机投成视口矩形的四个数。
    /// 平移预览传的是 <c>_view</c> 本身（右键拖的每一帧相机已经记在账上）；缩放缓动传的是
    /// 缓动那一档的虚拟相机——与点阵 <c>RepaintGridAt</c> 同一条几何，卡片在走，矩形也在走，
    /// 不等提交啪一下补（他上次就是被「卡片在走、点阵不动」的错位硌到的）。
    /// </summary>
    public void SyncViewport(double zoom, double panX, double panY, double surfaceWidth, double surfaceHeight)
    {
        // 静态刷具在主题翻转时换的是对象，这里每次现取现挂（两个赋值，不拆不建）。
        _viewport.Stroke = ChromeStyling.Ring;
        _viewport.Fill = ChromeStyling.MarqueeFill;

        if (!_hasContent || zoom <= 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
        {
            _viewport.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(_viewport, (((0 - panX) / zoom) - _originX) * _scale);
        Canvas.SetTop(_viewport, (((0 - panY) / zoom) - _originY) * _scale);
        _viewport.Width = surfaceWidth / zoom * _scale;
        _viewport.Height = surfaceHeight / zoom * _scale;
        _viewport.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 这一拍画过的那本账。对账每拍都跑（O(n) 比数），贵的那一发（拆建矩形）只在账变了才走。
    /// </summary>
    private string Stamp(IReadOnlyList<CanvasNode> notes)
    {
        var stamp = new StringBuilder();

        stamp.Append(NoteStyling.IsDark ? 'D' : 'L');

        foreach (var note in notes)
        {
            stamp.Append(note.Id)
                .Append(':')
                .Append(Num(note.X)).Append(',')
                .Append(Num(note.Y)).Append(',')
                .Append(Num(note.Width)).Append(',')
                .Append(Num(note.Height)).Append(',')
                .Append(note.Color ?? string.Empty)
                .Append(';');
        }

        return stamp.ToString();

        static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 小块的颜色沿用卡片那套预设号：agent 写的＝5（青，产品里当「蓝」）、decision＝1（红）、
    /// risk＝2（橙），色取该档预设自己的发丝色（step 7）——底色（step 1–3）在几像素的小块上等于没画。
    /// 其余（没上色、裸 hex）一律中性灰：这张地图要说的是「哪一格是要紧的、哪一格是 agent 写的」，
    /// 不复读每张卡自己的配色。
    /// </summary>
    private static Brush BlockBrush(string? color)
    {
        var dark = NoteStyling.IsDark;

        if (NotePalette.PaletteFor(color, dark) is { } preset)
        {
            return new SolidColorBrush(NoteStyling.ToColor(preset.Line));
        }

        // 中性灰取浮层那一套里的「安静墨」压淡一档——原型 .n 在浅板上就是这种淡灰蓝。
        return new SolidColorBrush(NoteStyling.WithAlpha(Sheet.Quiet(dark), 0.55));
    }
}
