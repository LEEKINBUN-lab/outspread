using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// 评论层：徽标挂在格子右上角，线程在浮层里读和写。
/// </summary>
/// <remarks>
/// <para>
/// 这一层做的是原型里那条判据——**评论占不到板面**。它不是 <see cref="ItemLayer"/> 的成员，
/// 不参与排版、不进 `.canvas`、也没有坐标；每个徽标的位置都是按文档现值现算的屏幕坐标，
/// 所以平移缩放时跟着走，但永远不会被框选选中、被遮挡剔除算进去、被同步合并当成几何。
/// </para>
/// <para>
/// 三档显示与原型一一对应：正常缩放＝每格一枚徽标（数字是消息数）；缩到
/// <see cref="CommentClusterZoom"/> 以下＝每簇一枚计数胶囊；已解决的线程退成灰牌，但仍在。
/// </para>
/// </remarks>
partial class MainWindow
{
    /// <summary>缩到这个比例以下，徽标改成每簇一枚计数胶囊（与原型同一条阈值）。</summary>
    private const double CommentClusterZoom = 0.62;

    /// <summary>簇的半径（屏幕像素）：两枚徽标近到这个距离以内就算同一簇。</summary>
    private const double CommentClusterRadius = 340;

    /// <summary>浮层宽（100% 缩放时）。原型 336，但 13.5 的正文在 336 里一行只剩十来个字。</summary>
    private const double CommentPopoverWidth = 356;

    /// <summary>
    /// 评论层的尺寸倍率：徽标、胶囊、浮层、文字全都跟着相机缩放——和便签同一套逻辑（他 11-18 要的）。
    /// 以前这一层是屏幕定尺寸的，板子缩到 200% 时便签字大一倍、评论还是那么小，读起来像贴纸。
    /// 下限 0.85：缩到 80% 时徽标按比例只剩 19px，数字已经读不出来了——"跟着缩放"是为了
    /// 跟便签同一个比例，不是为了把它缩没。上限 3.0 同理，别让浮层在超大缩放下变成一堵墙。
    /// </summary>
    private double CommentScale => Math.Clamp(_view.Zoom, 0.85, 3.0);

    /// <summary>浮层此刻的实际宽度：基准宽 × 倍率。</summary>
    private double CommentPopoverPx => CommentPopoverWidth * CommentScale;

    private BoardComments _comments = new();

    /// <summary>这一层上一次是按什么摆的：相机、尺寸、主题、评论版本、只看未解决。</summary>
    private string _commentSignature = string.Empty;

    /// <summary>浮层此刻锚在哪一格上（null＝没开）。</summary>
    private string? _commentNode;

    /// <summary>浮层里正看的是哪一条线程（null＝这一格还没有线程，正在写第一条）。</summary>
    private string? _commentThread;

    /// <summary>这条线程是不是已经摊开了（超过两条时先折成"展开其余 N 条"）。</summary>
    private bool _commentExpanded;

    /// <summary>正在问"真的要删掉这条线程吗"：确认之前不动账。</summary>
    private bool _commentDeleting;

    private Border? _commentPopover;

    /// <summary>浮层底下那个输入框。留下引用是给自检测"浮层里真有个能写字的地方"。</summary>
    private TextBox? _commentComposer;

    private void LoadComments()
    {
        _comments = _boardPath is null
            ? new BoardComments()
            : BoardComments.Load(CanvasStore.CommentsPath(_boardPath));

        _commentSignature = string.Empty;
        _commentNode = null;
        _commentThread = null;
        _commentExpanded = false;

        if (_comments.Problem is { } problem)
        {
            Diagnostics.Write($"comments unusable, showing none: {problem}");
        }
        else
        {
            Diagnostics.Write($"comments at load: {_comments.Threads.Count} 线程 / {_comments.MessageTotal} 条 ← {(_boardPath is null ? "(未定位)" : CanvasStore.CommentsPath(_boardPath))}");
        }
    }

    private FileWatcher? _commentsWatcher;

    /// <summary>
    /// 盯住 <c>.comments.json</c>：agent 会用 board_comment 往里写，重开板子才看见就太晚了。
    /// 走的是与本窗「外部改动」同一条路（<see cref="FileWatcher"/>），回调切回 UI 线程。
    /// </summary>
    private void StartWatchingComments()
    {
        if (_boardPath is null)
        {
            return;
        }

        _commentsWatcher?.Dispose();
        _commentsWatcher = new FileWatcher(CanvasStore.CommentsPath(_boardPath));
        _commentsWatcher.Changed += () => DispatcherQueue.TryEnqueue(ApplyExternalComments);
    }

    /// <summary>外面改了评论账：内容真的不同才换下来（我们自己保存那一发也会触发这个回调）。</summary>
    private void ApplyExternalComments()
    {
        if (_boardPath is null)
        {
            return;
        }

        var fresh = BoardComments.Load(CanvasStore.CommentsPath(_boardPath));

        if (CommentsSignature(fresh) == CommentsSignature(_comments))
        {
            return;
        }

        _comments = fresh;
        _commentSignature = string.Empty;

        // 正开着的那条线程如果被删了，浮层就该跟着收起；还在就按新的账重画。
        if (_commentThread is { } open && _comments.Find(open) is null)
        {
            _commentNode = null;
            _commentThread = null;
        }

        RefreshCommentLayer();
    }

    /// <summary>评论账的便宜指纹：线程号、状态、条数、最后一句——用来分辨"是不是我们刚写的那一版"。</summary>
    private static string CommentsSignature(BoardComments comments)
        => string.Join(
            "\u0001",
            comments.Threads.Select(thread =>
                $"{thread.Id}:{thread.Node}:{thread.Resolved}:{thread.Unread}:{thread.Messages.Count}"
                + $":{(thread.Messages.Count == 0 ? string.Empty : thread.Messages[^1].Text)}"));

    private void SaveComments()
    {
        if (_boardPath is null)
        {
            return;
        }

        try
        {
            _comments.Save(CanvasStore.CommentsPath(_boardPath));
        }
        catch (Exception ex)
        {
            Say($"评论存不下：{ex.Message}");
        }

        // 版本变了：下一拍重铺徽标层。
        _commentSignature = string.Empty;
    }

    /// <summary>重铺徽标与浮层。每一拍排布末尾叫一次；签名没变就什么都不做。</summary>
    private void RefreshCommentLayer()
    {
        if (_boardPath is null)
        {
            return;
        }

        var signature = $"{_view.Zoom:0.###}|{_view.PanX:0.#}|{_view.PanY:0.#}"
            + $"|{BoardArea.ActualWidth:0}|{BoardArea.ActualHeight:0}"
            + $"|{NoteStyling.IsDark}|{_comments.Revision}|{_commentNode}|{_commentThread}|{_commentExpanded}|{_commentDeleting}"
            + $"|{SoleSelection()}";

        if (signature == _commentSignature)
        {
            return;
        }

        _commentSignature = signature;
        CommentLayer.Children.Clear();
        _commentPopover = null;
        _commentComposer = null;

        if (_view.Zoom <= CommentClusterZoom)
        {
            RenderCommentClusters();
        }
        else
        {
            RenderCommentBadges();
        }

        // 浮层守的是"锚点还在"而不是"还有没有线程"：新建线程那一档还没有任何消息，
        // 按线程数收掉的话，输入框会在打字之前自己消失。
        if (_commentNode is { } node && _document.Find(node) is not null)
        {
            RenderCommentPopover(node);
        }
        else
        {
            _commentNode = null;
        }

        // 小地图画在评论层之上，位置又固定在左上角：浮层一开就压在它下面，看着像破了块洞。
        // 让它让位，比让浮层去躲它靠谱——窄窗口里根本躲不开（实测 560 宽的浮层在 754 的板面上
        // 往右挪不动、往下挪又出底）。进镜头时它本来也是这么让的（见 DueLens）。
        if (_lensKind is null && _miniMap is { } map)
        {
            map.Root.Visibility = _commentNode is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>小地图那块地方（板面坐标）。对齐它要用的那几件东西时读这里，别各算一份。</summary>
    private static Windows.Foundation.Rect MinimapRect()
        => new(MiniMap.MapInsetPx, MiniMap.MapInsetPx, MiniMap.MapWidthPx, MiniMap.MapHeightPx);

    /// <summary>某件常驻叠层（小地图、底部读数）在板面坐标系里的盒子；没摆出来时 null。</summary>
    private Windows.Foundation.Rect? OverlayBox(FrameworkElement? element)
    {
        if (element is null || element.Visibility != Visibility.Visible || element.ActualWidth < 1)
        {
            return null;
        }

        return element.TransformToVisual(BoardArea).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
    }

    /// <summary>只选中一格时返回它，否则空串。用来决定要不要摆那枚"写第一条"的占位徽标。</summary>
    private string SoleSelection()
        => _selection.Count == 1 ? _selection.First() : string.Empty;

    private IReadOnlyList<CommentThread> NodeComments(string nodeId)
        => _comments.ForNode(nodeId);

    /// <summary>徽标的锚：格子世界盒的右上角，换算成屏幕坐标。</summary>
    private (double X, double Y)? CommentAnchor(string nodeId)
    {
        if (!_realized.TryGetValue(nodeId, out var visual) || visual.Visibility == Visibility.Collapsed)
        {
            return null;
        }

        var box = visual.OwnWorldBox;

        if (box.Width <= 0 || box.Height <= 0)
        {
            return null;
        }

        var x = _view.ToScreenX(box.X + box.Width);
        var y = _view.ToScreenY(box.Y);

        // 屏外的不摆（不是不存）：滚回来下一拍自己会出来。
        if (x < -80 || y < -80 || x > BoardArea.ActualWidth + 80 || y > BoardArea.ActualHeight + 120)
        {
            return null;
        }

        return (x, y);
    }

    private void RenderCommentBadges()
    {
        var dark = NoteStyling.IsDark;

        foreach (var node in _document.Nodes)
        {
            var threads = NodeComments(node.Id);

            if (threads.Count == 0 || CommentAnchor(node.Id) is not { } anchor)
            {
                continue;
            }

            var messages = threads.Sum(thread => thread.Messages.Count);
            var unresolved = threads.Any(thread => !thread.Resolved);
            var unread = threads.Any(thread => thread.Unread && !thread.Resolved);

            CommentLayer.Children.Add(BuildCommentBadge(node.Id, anchor.X, anchor.Y, messages, unresolved, unread, dark));
        }

        // 这里原来还有一枚"写第一条"的淡徽标：选中一格、没有任何线程时，钉在同一根锚线上。
        // 撤掉它有三个理由，都是他 11-19 那一下点出来的：
        //   ① 锚线就是格子的右上角，而选中框的把手画在 CommentLayer **之上**——徽标整个被把手盖住，
        //      点下去是拖大小，永远点不着（"在选中框下方，没反应"）；
        //   ② "写"的入口已经搬到操作条第五枚（选中就有、位置固定），不需要一枚只在选中时出现的小钮；
        //   ③ 它和无评论的格子同时出现，等于每选一格就多一枚用不上的图标。
        // 有线程的格子仍然有真徽标（那一条是"读"的入口）。


    }

    /// <summary>缩得小了就整簇合成一枚胶囊：板上永远不会出现"一盘小点"。</summary>
    private void RenderCommentClusters()
    {
        var dark = NoteStyling.IsDark;
        var anchors = new List<(string Node, double X, double Y, int Messages, int Unresolved)>();

        foreach (var node in _document.Nodes)
        {
            var threads = NodeComments(node.Id);

            if (threads.Count == 0 || CommentAnchor(node.Id) is not { } anchor)
            {
                continue;
            }

            anchors.Add((
                node.Id,
                anchor.X,
                anchor.Y,
                threads.Sum(thread => thread.Messages.Count),
                threads.Count(thread => !thread.Resolved)));
        }

        var used = new bool[anchors.Count];

        for (var i = 0; i < anchors.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var members = new List<int> { i };
            used[i] = true;

            for (var j = i + 1; j < anchors.Count; j++)
            {
                if (used[j])
                {
                    continue;
                }

                if (Math.Sqrt(
                        ((anchors[j].X - anchors[i].X) * (anchors[j].X - anchors[i].X))
                        + ((anchors[j].Y - anchors[i].Y) * (anchors[j].Y - anchors[i].Y))) <= CommentClusterRadius)
                {
                    members.Add(j);
                    used[j] = true;
                }
            }

            var left = members.Min(index => anchors[index].X);
            var top = members.Min(index => anchors[index].Y) - (30 * CommentScale);
            var messages = members.Sum(index => anchors[index].Messages);
            var unresolved = members.Sum(index => anchors[index].Unresolved);
            var first = anchors[members[0]].Node;

            CommentLayer.Children.Add(BuildCommentCluster(first, left, top, messages, unresolved, dark));
        }
    }

    /// <summary>
    /// 把一枚徽标变成"点得动的"。板子的按下处理挂在 <c>BoardArea</c> 上，徽标只挂 <c>Tapped</c>
    /// 是不够的：按下先冒到板子那儿，板子按文档几何认出底下的便签、把指针捕获走，这一发就再也
    /// 回不到徽标——手指明明落在气泡上，选中的却是便签（他 11-18 报的"根本没办法看评论"）。
    /// 所以按下这一发要在徽标自己这里吃掉，动作放在抬起那一拍。
    /// </summary>
    private static void MakeCommentHitTarget(UIElement target, Action activate)
    {
        target.PointerPressed += (_, args) => args.Handled = true;

        target.PointerReleased += (_, args) =>
        {
            args.Handled = true;
            activate();
        };
    }

    /// <summary>浮层自己也要吃按下：点在它的留白上不该被当成"点了板子"而把线程收掉。</summary>
    private static void MakeCommentSurface(UIElement surface)
        => surface.PointerPressed += (_, args) => args.Handled = true;

    private Border BuildCommentBadge(string nodeId, double x, double y, int messages, bool unresolved, bool unread, bool dark)
    {
        var k = CommentScale;

        var badge = new Border
        {
            Name = "CommentBadge",
            Height = 26 * k,
            CornerRadius = new CornerRadius(13 * k),
            Padding = new Thickness(8 * k, 0, 9 * k, 0),
            Background = new SolidColorBrush(dark ? Rgb(42, 45, 49) : Rgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(dark ? Rgb(70, 73, 78) : Rgb(219, 215, 206)),
            BorderThickness = new Thickness(1),
            Tag = nodeId,
            Opacity = unresolved ? 1 : 0.55,
            Child = BuildCommentBadgeContent(messages, unread, dark, k),
        };

        ToolTipService.SetToolTip(badge, CommentPreview(nodeId));

        MakeCommentHitTarget(badge, () => OpenCommentThread(nodeId));

        // 悬停换一档底色：这一枚是"点得开"的，不该跟板上的装饰长得一样。
        // 已解决那档本来就淡，悬停时一并抬起来——不然会读成"按不动"。
        var rest = new SolidColorBrush(dark ? Rgb(42, 45, 49) : Rgb(255, 255, 255));
        var over = new SolidColorBrush(dark ? Rgb(54, 58, 63) : Rgb(247, 245, 240));

        badge.PointerEntered += (_, _) =>
        {
            badge.Background = over;
            badge.Opacity = 1;
        };

        badge.PointerExited += (_, _) =>
        {
            badge.Background = rest;
            badge.Opacity = unresolved ? 1 : 0.55;
        };

        // 先量一次再落到"右上角外侧"：徽标宽随数字变，用固定偏移会让 1 和 12 摆得不一样。
        badge.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        badge.Width = Math.Max(30 * k, badge.DesiredSize.Width);

        Canvas.SetLeft(badge, x - (badge.Width / 2));
        Canvas.SetTop(badge, y - (13 * k));

        return badge;
    }

    /// <summary>徽标与浮层头共用的那枚对话气泡。路径只写一遍，免得三处各画一个不一样的。</summary>
    private const string CommentBubblePath = "M3 4H17V13H8L4 16V13H3Z";

    private static UIElement? CommentBubble(double pixels, Color ink)
        => FluentIcons.Glyph(CommentBubblePath, pixels, ink);

    private UIElement BuildCommentBadgeContent(int messages, bool unread, bool dark, double k)
    {
        var ink = dark ? Rgb(228, 230, 232) : Rgb(60, 66, 73);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 * k };

        if (CommentBubble(15 * k, ink) is { } glyph)
        {
            glyph.Opacity = 0.85;
            row.Children.Add(glyph);
        }

        row.Children.Add(new TextBlock
        {
            Text = messages.ToString(),
            FontSize = 12.5 * k,
            Foreground = new SolidColorBrush(ink),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (unread)
        {
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6 * k,
                Height = 6 * k,
                Fill = new SolidColorBrush(Rgb(227, 154, 43)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return row;
    }

    private Border BuildCommentCluster(string nodeId, double x, double y, int messages, int unresolved, bool dark)
    {
        var k = CommentScale;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * k };

        row.Children.Add(new TextBlock
        {
            Text = messages.ToString(),
            FontSize = 12 * k,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Rgb(255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = $"条 · {unresolved} 未解决",
            FontSize = 11.5 * k,
            Foreground = new SolidColorBrush(Rgb(232, 234, 236)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var pill = new Border
        {
            Name = "CommentCluster",
            Height = 28 * k,
            CornerRadius = new CornerRadius(14 * k),
            Padding = new Thickness(11 * k, 0, 11 * k, 0),
            Background = new SolidColorBrush(dark ? Rgb(70, 74, 80) : Rgb(44, 46, 49)),
            Tag = nodeId,
            Child = row,
        };

        ToolTipService.SetToolTip(pill, "缩得太小，整簇合成一个数；点它回到 100% 看位置");

        MakeCommentHitTarget(pill, () =>
        {
            _view.Zoom = 1;
            SaveSoon();
            ArrangeAll();
            OpenCommentThread(nodeId);
        });

        Canvas.SetLeft(pill, x);
        Canvas.SetTop(pill, y);

        return pill;
    }

    private string CommentPreview(string nodeId)
    {
        var threads = NodeComments(nodeId);

        if (threads.Count == 0)
        {
            return "评论";
        }

        var last = threads[^1].Messages;

        return last.Count == 0
            ? $"{threads.Count} 条线程"
            : $"{last[^1].Text}\n—— {threads.Count} 条线程 · {threads.Sum(t => t.Messages.Count)} 条消息";
    }

    /// <summary>
    /// 操作条那枚「评论」钮：对选中的那一格开浮层。
    /// </summary>
    /// <remarks>
    /// 这是"写"的入口——徽标只在**已经有线程**时才出现，光靠它是"不知道怎么新建"（他 11-18 问的）。
    /// 选中一格 → 操作条上按评论 → 浮层开在该格；还没有线程时它就是"写第一条"那一屏。
    /// </remarks>
    private void OpenCommentsForSelection()
    {
        if (_selection.Count != 1)
        {
            Say(_selection.Count == 0 ? "先选中一格再评论" : "评论一次只对一格");

            return;
        }

        var id = _selection.First();

        if (_document.Find(id) is not { } node || node.Kind == CanvasNodeKind.Group)
        {
            return;
        }

        OpenCommentThread(id);
    }

    private void OpenCommentThread(string nodeId)
    {
        // 点徽标＝进这一格最该看的那一条：还有没结的就先开它，全结了才开最后一条。
        var threads = NodeComments(nodeId);
        var wanted = threads.FirstOrDefault(thread => !thread.Resolved) ?? threads.LastOrDefault();

        OpenCommentThread(nodeId, wanted?.Id);
    }

    private void OpenCommentThread(string nodeId, string? threadId)
    {
        _commentNode = nodeId;
        _commentThread = threadId;
        _commentExpanded = false;
        _commentDeleting = false;

        // 打开即算看过。要落盘才真的清得掉那枚橙点——不写的话下次开板它又亮回来，
        // 而"我看过了"这件事本身就该留下痕迹。
        if (threadId is not null && _comments.MarkRead(threadId))
        {
            SaveComments();
        }

        // 落盘那条路只清签名不重铺（保存是被动方），所以这里必须自己再叫一次——
        // 少了这一发，浮层会"开是开了，画面上什么都没有"。
        _commentSignature = string.Empty;
        RefreshCommentLayer();
    }

    /// <summary>同一个格子上有几条线程时翻页（原型里靠右侧列表，这里收进浮层头一行）。</summary>
    private void StepCommentThread(int delta)
    {
        if (_commentNode is not { } node)
        {
            return;
        }

        var threads = NodeComments(node);

        if (threads.Count < 2)
        {
            return;
        }

        var index = 0;

        for (var i = 0; i < threads.Count; i++)
        {
            if (string.Equals(threads[i].Id, _commentThread, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        OpenCommentThread(node, threads[(index + delta + threads.Count) % threads.Count].Id);
    }

    /// <summary>返回 true 表示这一发 Esc 被浮层吃掉了。</summary>
    private bool CloseCommentThread()
    {
        if (_commentNode is null)
        {
            return false;
        }

        _commentNode = null;
        _commentThread = null;
        _commentExpanded = false;
        _commentDeleting = false;
        _commentSignature = string.Empty;
        RefreshCommentLayer();

        return true;
    }

    /// <summary>
    /// 取证用：打开第一格有未解决线程的评论浮层（<c>shot --panel comments</c>）；
    /// <paramref name="blank"/> 时改开一格还没有评论的（<c>shot --panel comments-new</c>），
    /// 那是"写第一条"那一屏。
    /// </summary>
    /// <returns>开在哪一格上；板上一句评论都没有时 null——取证不许造数据。</returns>
    private string? OpenFirstCommentThreadForShot(bool blank = false, bool askDelete = false)
    {
        var wanted = blank
            ? _document.Nodes.FirstOrDefault(node => NodeComments(node.Id).Count == 0 && CommentAnchor(node.Id) is not null)
            : _document.Nodes.FirstOrDefault(node => NodeComments(node.Id).Any(thread => !thread.Resolved))
                ?? _document.Nodes.FirstOrDefault(node => NodeComments(node.Id).Count > 0);

        if (wanted is null)
        {
            return null;
        }

        OpenCommentThread(wanted.Id);

        // "确认删除"那一屏也是一屏：它顶掉的是输入行，只有真删之前才看得到，
        // 没有取证件就只能靠想象。
        if (askDelete && _commentThread is not null)
        {
            _commentDeleting = true;
            _commentSignature = string.Empty;
            RefreshCommentLayer();
        }

        BoardArea.UpdateLayout();

        return wanted.Id;
    }

    /// <summary>
    /// 浮层：一条线程一屏（与原型一致），头一行放线程计数、翻页、结掉与收起。
    /// </summary>
    /// <remarks>
    /// 一条线程一屏而不是"这一格的全部对话堆在一起"，是因为回复要落在具体某一条上：
    /// 堆在一起就只能猜"我现在回的是哪一句"，而一条被结掉的线程被回复就该重新打开——
    /// 那件事只有一个明确的目标时才说得清。
    /// </remarks>
    private void RenderCommentPopover(string nodeId)
    {
        var k = CommentScale;
        var dark = NoteStyling.IsDark;
        var threads = NodeComments(nodeId);
        var thread = threads.FirstOrDefault(candidate => string.Equals(candidate.Id, _commentThread, StringComparison.Ordinal));

        if (thread is null)
        {
            _commentThread = null;
        }

        var background = new SolidColorBrush(dark ? Rgb(35, 38, 42) : Rgb(255, 255, 255));
        var line = new SolidColorBrush(dark ? Rgb(58, 61, 66) : Rgb(225, 221, 212));
        var ink = dark ? Rgb(228, 230, 232) : Rgb(37, 39, 42);
        var quiet = dark ? Rgb(154, 160, 166) : Rgb(107, 112, 118);

        var grid = new Grid();

        // 头 / 正文 / 输入。正文那一格是星号：浮层被窗口高度夹住时，由它把多余的那截吃掉
        // （里面是 ScrollViewer，滚得动），头与输入框永远留在视野里。
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── 头 ──
        var head = new Grid { Padding = new Thickness(12 * k, 10 * k, 8 * k, 9 * k) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };

        if (CommentBubble(13, ink) is { } headGlyph)
        {
            headGlyph.Opacity = 0.8;
            title.Children.Add(headGlyph);
        }

        title.Children.Add(new TextBlock
        {
            Text = "评论",
            FontSize = 13.5 * k,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ink),
            VerticalAlignment = VerticalAlignment.Center,
        });

        title.Children.Add(new TextBlock
        {
            Text = thread is null
                ? "新线程"
                : $"{thread.Messages.Count} 条{(thread.Resolved ? " · 已解决" : string.Empty)}",
            FontSize = 11.5 * k,
            Foreground = new SolidColorBrush(quiet),
            VerticalAlignment = VerticalAlignment.Center,
        });

        head.Children.Add(title);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        if (threads.Count > 1 && thread is not null)
        {
            var ordinal = 0;

            for (var i = 0; i < threads.Count; i++)
            {
                if (string.Equals(threads[i].Id, thread.Id, StringComparison.Ordinal))
                {
                    ordinal = i + 1;
                    break;
                }
            }

            acts.Children.Add(SmallGhostButton("‹", "上一条线程", quiet, () => StepCommentThread(-1)));

            acts.Children.Add(new TextBlock
            {
                Text = $"{ordinal}/{threads.Count}",
                FontSize = 10.5 * k,
                Foreground = new SolidColorBrush(quiet),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1 * k, 0, 1 * k, 0),
            });

            acts.Children.Add(SmallGhostButton("›", "下一条线程", quiet, () => StepCommentThread(1)));
        }

        if (thread is not null)
        {
            var resolved = thread.Resolved;

            acts.Children.Add(SmallGhostButton(
                string.Empty,
                resolved ? "重新打开" : "标为已解决",
                quiet,
                () =>
                {
                    _comments.SetResolved(thread.Id, !resolved);
                    SaveComments();
                    OpenCommentThread(nodeId, thread.Id);
                },
                FluentIcons.Glyph(resolved ? FluentIcons.History : FluentIcons.Checkmark, 15 * k, quiet)));
        }

        if (thread is not null)
        {
            // 删线程：没有撤销账，所以按下先问一句（正文那一格换成确认条），不直接动手。
            acts.Children.Add(SmallGhostButton(
                string.Empty,
                "删掉这条线程",
                quiet,
                () =>
                {
                    _commentDeleting = true;
                    _commentSignature = string.Empty;
                    RefreshCommentLayer();
                },
                FluentIcons.Glyph(FluentIcons.Delete, 15 * k, quiet)));
        }

        acts.Children.Add(SmallGhostButton(
            string.Empty,
            "收起（Esc）",
            quiet,
            () => CloseCommentThread(),
            FluentIcons.Glyph(FluentIcons.Dismiss, 15 * k, quiet)));

        Grid.SetColumn(acts, 1);
        head.Children.Add(acts);
        grid.Children.Add(head);

        // ── 正文 ──
        var body = new StackPanel { Spacing = 0, Padding = new Thickness(14 * k, 0, 14 * k, 10 * k) };

        if (thread is null)
        {
            // 一条都还没有：这里不是"空列表"，是"这一格还没人说过话"。
            body.Children.Add(new TextBlock
            {
                Text = "这一格还没有评论。写下第一句，它就成了这条线程的开篇。",
                FontSize = 12.5 * k,
                LineHeight = 19 * k,
                Margin = new Thickness(0, 8 * k, 0, 8 * k),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(quiet),
            });
        }
        else
        {
            // 超过两屏就先折起来：浮层是拿来读一眼的，不是把整段对话灌到板面上。
            var shown = _commentExpanded ? thread.Messages.Count : Math.Min(2, thread.Messages.Count);

            for (var i = 0; i < shown; i++)
            {
                body.Children.Add(BuildCommentMessage(thread.Messages[i], dark, ink, quiet));
            }

            if (thread.Messages.Count > shown)
            {
                var rest = thread.Messages.Count - shown;

                body.Children.Add(SmallGhostButton(
                    $"展开其余 {rest} 条",
                    "把这条线程剩下的都摊开",
                    quiet,
                    () =>
                    {
                        _commentExpanded = true;
                        _commentSignature = string.Empty;
                        RefreshCommentLayer();
                    },
                    null,
                    wide: true));
            }

            if (thread.Resolved)
            {
                var bar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Thickness(0, 10 * k, 0, 0),
                    Padding = new Thickness(10 * k, 7 * k, 10 * k, 7 * k),
                    Background = new SolidColorBrush(dark ? Rgb(32, 35, 39) : Rgb(247, 246, 242)),
                };

                bar.Children.Add(new TextBlock
                {
                    Text = "已解决 · 徽标已退成灰牌",
                    FontSize = 12.5 * k,
                    Foreground = new SolidColorBrush(quiet),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                body.Children.Add(bar);
            }
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 300 * k,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        };

        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        // ── 输入 ──
        var composer = new Grid
        {
            Padding = new Thickness(12 * k, 10 * k, 12 * k, 10 * k),
            Background = new SolidColorBrush(dark ? Rgb(30, 33, 37) : Rgb(252, 251, 248)),
        };

        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var input = new TextBox
        {
            FontSize = 13.5 * k,
            MinHeight = 34 * k,
            Padding = new Thickness(10 * k, 6 * k, 10 * k, 6 * k),
            PlaceholderText = thread is null
                ? "写下第一条…（回车发送）"
                : thread.Resolved ? "已解决，回复会重新打开" : "回复…（回车发送）",
            Background = new SolidColorBrush(dark ? Rgb(42, 45, 49) : Rgb(255, 255, 255)),
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Foreground = new SolidColorBrush(ink),
        };

        var send = new Button
        {
            Content = "发送",
            FontSize = 12.5 * k,
            Height = 34 * k,
            Margin = new Thickness(8 * k, 0, 0, 0),
            Padding = new Thickness(13 * k, 0, 13 * k, 0),
        };

        void Post()
        {
            var text = input.Text.Trim();

            if (text.Length == 0)
            {
                return;
            }

            if (thread is null)
            {
                // 这一格还没有线程：这条就是开篇（先有第一句，才有线程）。
                _commentThread = _comments.AddThread(nodeId, "我", text, agent: false).Id;
            }
            else
            {
                _comments.Reply(thread.Id, "我", text, agent: false);
            }

            SaveComments();
            OpenCommentThread(nodeId, _commentThread);
        }

        send.Click += (_, _) => Post();

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                Post();
            }
        };

        if (_commentDeleting && thread is not null)
        {
            // 确认那一屏顶掉输入行：同一块地方，改问一句——浮层不长高，眼睛也不用挪。
            // 只在"真要问"的时候才建：这一屏原来是**无条件**铸的，而"还没有线程"那一屏
            // thread 是 null，于是点任何评论钮都在这里 NRE 闪退（他 11-19 报的）。
            composer.Children.Clear();
            composer.ColumnDefinitions.Clear();
            composer.Children.Add(BuildDeleteConfirm(nodeId, thread, ink, quiet, dark, k));
        }
        else
        {
            composer.Children.Add(input);
            Grid.SetColumn(send, 1);
            composer.Children.Add(send);
        }

        Grid.SetRow(composer, 2);
        grid.Children.Add(composer);

        // ── 外壳 ──
        // 跟着相机放大以后，浮层的基准宽也可能比板面还宽：夹到板面内，正文自己换行。
        var popoverWidth = Math.Min(CommentPopoverPx, Math.Max(240, BoardArea.ActualWidth - 16));

        var popover = new Border
        {
            Name = "CommentPopover",
            Width = popoverWidth,
            Background = background,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12 * k),
            Child = grid,
            Tag = nodeId,
        };

        // 先量高度：浮层从"新线程"到"整条摊开"能从 120 长到 400，落位要按它的实际高度来夹。
        popover.Measure(new Windows.Foundation.Size(popoverWidth, double.PositiveInfinity));

        // 跟着相机放大以后，浮层可能比板面还高（194% 下正文一行就是 26px）。夹到窗口内，
        // 多的那截交给中间那格去滚——"整个浮层跑出屏幕"比"字小"更离谱。
        var height = Math.Min(popover.DesiredSize.Height, Math.Max(160, BoardArea.ActualHeight - 16));

        if (height < popover.DesiredSize.Height)
        {
            popover.Height = height;
        }

        double left;
        double top;

        if (CommentAnchor(nodeId) is { } anchor)
        {
            var gap = 18 * k;

            left = anchor.X + gap;

            if (left + popoverWidth > BoardArea.ActualWidth - 8)
            {
                left = anchor.X - popoverWidth - gap;
            }

            left = Math.Max(8, Math.Min(left, Math.Max(8, BoardArea.ActualWidth - popoverWidth - 8)));
            top = Math.Max(8, Math.Min(anchor.Y - (10 * k), Math.Max(8, BoardArea.ActualHeight - height - 8)));
        }
        else
        {
            // 锚点算不出来＝那一格在视口外（从操作条按评论、或点过徽标之后又平移走了）。
            // 这时候浮层没有"旁边"可放，就摆到板面正中——总比留在 (0,0) 让左上角那几件 HUD 盖住它强。
            left = Math.Max(8, (BoardArea.ActualWidth - popoverWidth) / 2);
            top = Math.Max(8, (BoardArea.ActualHeight - height) / 2);
        }

        // 底部那枚读数（"18 个便签 · 缩放 157%"）也在浮层之上：浮层压到它就等于把最后一行字糊掉。
        // 它比小地图矮得多，往上让一格就够——让不开（窗口太矮）才作罢。
        if (OverlayBox(BadgeHost) is { } badge
            && left < badge.Right + 8
            && left + popoverWidth > badge.Left - 8
            && top + height > badge.Top - 8
            && top < badge.Bottom)
        {
            top = Math.Max(8, badge.Top - 8 - height);
        }

        Diagnostics.Write(
            $"comment popover: {left:0},{top:0} {popoverWidth:0}×{height:0} anchor={CommentAnchor(nodeId) is not null}");

        Canvas.SetLeft(popover, left);
        Canvas.SetTop(popover, top);

        MakeCommentSurface(popover);
        CommentLayer.Children.Add(popover);
        _commentPopover = popover;
        _commentComposer = input;

        DispatcherQueue.TryEnqueue(() => input.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// "删掉这条线程？"那一行：一句问话＋取消／删除。
    /// </summary>
    /// <remarks>
    /// 评论是旁挂账，删掉不进撤销账——所以这条问话不是客套，是唯一一道闸。
    /// 删除钮用警告色，取消是普通墨色：误点的代价不对称，视觉上也该不对称。
    /// </remarks>
    private UIElement BuildDeleteConfirm(
        string nodeId, CommentThread? thread, Color ink, Color quiet, bool dark, double k)
    {
        var row = new Grid();

        if (thread is null)
        {
            return row;
        }


        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = $"删掉这条线程？{thread.Messages.Count} 条消息会一起消失",
            FontSize = 12 * k,
            Foreground = new SolidColorBrush(ink),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 8 * k, 0),
        });

        var cancel = new Button
        {
            Content = "取消",
            FontSize = 12 * k,
            Height = 30 * k,
            Padding = new Thickness(10 * k, 0, 10 * k, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(quiet),
        };

        cancel.Click += (_, _) =>
        {
            _commentDeleting = false;
            _commentSignature = string.Empty;
            RefreshCommentLayer();
        };

        var remove = new Button
        {
            Content = "删除",
            FontSize = 12 * k,
            Height = 30 * k,
            Margin = new Thickness(6 * k, 0, 0, 0),
            Padding = new Thickness(12 * k, 0, 12 * k, 0),
            Background = new SolidColorBrush(dark ? Rgb(92, 44, 44) : Rgb(176, 58, 58)),
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Colors.White),
        };

        remove.Click += (_, _) =>
        {
            _comments.RemoveThread(thread.Id);
            SaveComments();

            // 还有别的线程就接着看下一条；这是最后一格就没得看了，收起来。
            _commentDeleting = false;

            if (NodeComments(nodeId).Count > 0)
            {
                OpenCommentThread(nodeId);
            }
            else
            {
                CloseCommentThread();
            }
        };

        Grid.SetColumn(cancel, 1);
        row.Children.Add(cancel);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    /// <summary>一枚只在这条线程里出现的按钮：无底无边，只有字形与提示。</summary>
    private Button SmallGhostButton(
        string text, string help, Color ink, Action click, UIElement? icon = null, bool wide = false)
    {
        var k = CommentScale;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5 * k,
            HorizontalAlignment = wide ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };

        if (icon is not null)
        {
            content.Children.Add(icon);
        }

        if (text.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = (wide ? 12.5 : 14) * k,
                Foreground = new SolidColorBrush(ink),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        // 命中区给足：这两枚（结掉／收起）原来是 4px 内缩的小字，指着它点很难点中。
        var button = new Button
        {
            Content = content,
            Padding = new Thickness((wide ? 8 : 7) * k, (wide ? 4 : 5) * k, (wide ? 8 : 7) * k, (wide ? 4 : 5) * k),
            MinWidth = (wide ? 0 : 30) * k,
            MinHeight = (wide ? 0 : 30) * k,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = wide ? HorizontalAlignment.Center : HorizontalAlignment.Right,
        };

        ToolTipService.SetToolTip(button, help);
        button.Click += (_, _) => click();

        return button;
    }

    private UIElement BuildCommentMessage(CommentMessage message, bool dark, Color ink, Color quiet)
    {
        var k = CommentScale;
        var row = new StackPanel { Spacing = 3 * k, Margin = new Thickness(0, 10 * k, 0, 0) };

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 * k };

        meta.Children.Add(new TextBlock
        {
            Text = message.Author,
            FontSize = 12 * k,
            Foreground = new SolidColorBrush(message.Agent ? Rgb(63, 111, 94) : quiet),
        });

        meta.Children.Add(new TextBlock
        {
            Text = message.Time,
            FontSize = 11 * k,
            Foreground = new SolidColorBrush(quiet),
        });

        if (message.Agent)
        {
            meta.Children.Add(new Border
            {
                Padding = new Thickness(5 * k, 0, 5 * k, 1 * k),
                CornerRadius = new CornerRadius(4 * k),
                Background = new SolidColorBrush(dark ? Rgb(46, 62, 55) : Rgb(234, 240, 236)),
                Child = new TextBlock
                {
                    Text = "agent",
                    FontSize = 10 * k,
                    Foreground = new SolidColorBrush(Rgb(63, 111, 94)),
                },
            });
        }

        row.Children.Add(meta);

        row.Children.Add(new TextBlock
        {
            Text = message.Text,
            FontSize = 13.5 * k,
            LineHeight = 21 * k,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ink),
        });

        return row;
    }
    private static Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    // -------------------------------------------------------------------------
    // 自检
    // -------------------------------------------------------------------------

    /// <summary>
    /// 评论层：徽标贴没贴住锚点、没评论的格上会不会自己冒出来、浮层开得起来收得掉、
    /// 缩到簇档是不是只剩一枚计数胶囊。
    /// </summary>
    /// <remarks>
    /// 只动内存里的 <see cref="_comments"/>，一步都不落盘——自检跑在临时副本上，但侧车文件
    /// 不跟着重置，留下一条假线程会让下一发从别人的状态起步（闸门那几条注释说的就是这个坑）。
    /// </remarks>
    private void SelfTestCommentLayer(List<string> problems)
    {
        var savedNode = _commentNode;
        var savedSelection = _selection.ToList();
        var savedZoom = _view.Zoom;
        var before = _comments.Threads.Count;

        Border? Find(string name) => CommentLayer.Children
            .OfType<Border>()
            .FirstOrDefault(border => string.Equals(border.Name, name, StringComparison.Ordinal));

        int Count(string name) => CommentLayer.Children
            .OfType<Border>()
            .Count(border => string.Equals(border.Name, name, StringComparison.Ordinal));

        try
        {
            // 挑格子的判据必须与渲染器一字不差：徽标只摆在"锚点算得出来"的格子上，
            // 拿一枚屏外的格子来量，量的永远是 0 枚——那是尺子错了，不是层错了。
            var onScreen = _document.Nodes
                .Where(node => node.Kind != CanvasNodeKind.Group)
                .Select(node => node.Id)
                .Where(id => CommentAnchor(id) is not null)
                .ToList();

            if (onScreen.Count == 0)
            {
                problems.Add("评论层自检: 屏幕上没有一格便签，徽标无从可量");

                return;
            }

            var commented = onScreen[0];
            var blank = onScreen.FirstOrDefault(id => !string.Equals(id, commented, StringComparison.Ordinal));

            // ① 一格还没有评论：板上不该有徽标（"写"的入口在操作条），但**必须能开出浮层**。
            //    这一发就是他那次闪退的复现位：没有线程那一屏原来会踩 NRE（thread.Messages）。
            _commentNode = null;
            _selection.Clear();
            _selection.Add(commented);
            _commentSignature = string.Empty;
            RefreshCommentLayer();

            if (Count("CommentBadge") != 0)
            {
                problems.Add($"评论层自检: {commented} 还没有评论，却画出了 {Count("CommentBadge")} 枚真徽标");
            }

            OpenCommentThread(commented);
            BoardArea.UpdateLayout();

            if (_commentPopover is null || _commentComposer is null)
            {
                problems.Add("评论层自检: 没有线程的格子开不出浮层／没有输入框（点评论钮会闪退的那一发）");
            }

            CloseCommentThread();

            // ② 写下一句之后：占位退场、真徽标上场，而且落在格子右上角。
            var thread = _comments.AddThread(commented, "自检", "徽标自检", agent: false, time: "00:00");

            RefreshCommentLayer();

            var badge = Find("CommentBadge");

            if (badge is null)
            {
                problems.Add("评论层自检: 有了一条线程，徽标却没画出来");
            }
            else
            {
                // 量的是 Measure 出来的 DesiredSize：ActualHeight 要等一趟布局，自检里那一趟还没来，
                // 读它只会读到 0，一条"量不出高度"的假红灯。
                if (badge.Width < 28 * CommentScale
                    || badge.DesiredSize.Width > badge.Width + 0.5
                    || badge.DesiredSize.Height > (26 * CommentScale) + 0.5)
                {
                    problems.Add(
                        $"评论层自检: 徽标 {badge.Width:0.#}×{26 * CommentScale:0.#} 兜不住内容 {badge.DesiredSize.Width:0.#}×{badge.DesiredSize.Height:0.#}");
                }

                if (CommentAnchor(commented) is { } anchor)
                {
                    var dx = Math.Abs(Canvas.GetLeft(badge) + (badge.Width / 2) - anchor.X);
                    var dy = Math.Abs(Canvas.GetTop(badge) + (13 * CommentScale) - anchor.Y);

                    if (dx > 1 || dy > 1)
                    {
                        problems.Add($"评论层自检: 徽标离开锚点 {dx:0.0}×{dy:0.0}px（应在格子右上角）");
                    }
                }

            }

            // ③ 浮层：开得起来、宽度对、里面有能写字的输入框、四边都在视口里。
            OpenCommentThread(commented);
            BoardArea.UpdateLayout();

            if (_commentPopover is not { } popover)
            {
                problems.Add("评论层自检: 点了线程却没有浮层");
            }
            else
            {
                var left = Canvas.GetLeft(popover);
                var top = Canvas.GetTop(popover);

                // 期望值要考虑"板面装不下时被夹住"那一档：浮层跟着相机放大，基准宽 356 在 2.5x
                // 下就是 890，比板面还宽——那时它该夹到板面内，而不是探出去。
                var wantWidth = Math.Min(CommentPopoverPx, Math.Max(240, BoardArea.ActualWidth - 16));

                if (Math.Abs(popover.Width - wantWidth) > 0.5)
                {
                    problems.Add($"评论层自检: 浮层宽 {popover.Width:0.#}，应为 {wantWidth:0.#}（基准 {CommentPopoverWidth} × 缩放 {CommentScale:0.##}，上限板面宽）");
                }

                if (left < 0 || top < 0
                    || left + popover.Width > BoardArea.ActualWidth + 1
                    || top + Math.Max(140, popover.ActualHeight) > BoardArea.ActualHeight + 1)
                {
                    problems.Add($"评论层自检: 浮层跑出视口——左 {left:0} 上 {top:0}，视口 {BoardArea.ActualWidth:0}×{BoardArea.ActualHeight:0}");
                }

                if (_commentComposer is null)
                {
                    problems.Add("评论层自检: 浮层里没有输入框，写了也没地方发");
                }
            }

            // ④ Esc 收得掉：收起后浮层与输入框一起走，徽标留着。
            if (!CloseCommentThread())
            {
                problems.Add("评论层自检: 浮层开着，Esc 却没接住");
            }
            else if (_commentPopover is not null || _commentComposer is not null)
            {
                problems.Add("评论层自检: 收起以后浮层／输入框还挂在层上");
            }

            if (Count("CommentBadge") != 1)
            {
                problems.Add("评论层自检: 收起浮层不该动徽标");
            }

            // ⑤ 缩到簇档：整层只剩计数胶囊，一枚徽标都不该留（"一盘小点"就是这么消掉的）。
            if (blank is not null)
            {
                _view.Zoom = CommentClusterZoom - 0.1;
                ArrangeAll();

                if (Count("CommentCluster") != 1)
                {
                    problems.Add($"评论层自检: 缩到 {CommentClusterZoom - 0.1:0.00} 时应是 1 枚计数胶囊，实得 {Count("CommentCluster")} 枚");
                }

                if (Count("CommentBadge") != 0)
                {
                    problems.Add($"评论层自检: 簇档下还留着 {Count("CommentBadge")} 枚徽标");
                }
            }
        }
        finally
        {
            // 收场：假线程摘掉、相机与选中还原、不落盘。
            while (_comments.Threads.Count > before)
            {
                _comments.Threads.RemoveAt(_comments.Threads.Count - 1);
            }

            _view.Zoom = savedZoom;
            _commentNode = savedNode;
            _selection.Clear();

            foreach (var id in savedSelection)
            {
                _selection.Add(id);
            }

            _commentSignature = string.Empty;
            ArrangeAll();
        }
    }
}
