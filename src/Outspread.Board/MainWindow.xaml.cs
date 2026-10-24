using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Outspread.Core;
using Windows.Foundation;
using Windows.UI;

namespace Outspread.Board;

public sealed partial class MainWindow : Window
{
    private const double MinZoom = SelectionChrome.MinZoom;

    private const double MaxZoom = SelectionChrome.MaxZoom;

    private const double WheelStep = 1.12;

    /// <summary>编辑中那一格滚轮走多少屏幕像素。第一版这个数按"两行正文多一点"挑的
    /// （正文一行 21 像素），手感归他判；几何本身在 <see cref="EditorScroll.Reveal"/>。</summary>
    private const double EditorScrollPxPerNotch = 48;

    /// <summary>缩放缓动期间点阵重画的最小间隔（毫秒）＝一帧（60Hz）。这一档是量出来的：
    /// 预览那一发（1360×842、不数墨）现读 4.6–6.8ms，占一帧预算（16.7ms）的三成上下，
    /// 所以点阵可以跟卡片同帧；数墨那一发（3.7–14.5ms）只在提交时跑。</summary>
    private const long GridTweenMinIntervalMs = 15;

    /// <summary>How far outside the visible area nodes are still kept realised.</summary>
    private const double CullMargin = 400;

    private const double MinNodeSize = 12;

    private BoardViewState _view = new();

    private readonly Dictionary<string, NodeVisual> _realized = new(StringComparer.Ordinal);

    private readonly HashSet<string> _selection = new(StringComparer.Ordinal);

    private readonly DispatcherQueueTimer _saveTimer;

    private readonly List<FrameworkElement> _handles = [];

    private CanvasDocument _document = CanvasDocument.CreateNew();

    private string _baseJson = "{ \"nodes\": [] }";

    private string? _boardPath;

    private bool _panning;

    private double _panStartX;

    private double _panStartY;

    private bool _marqueeing;

    // 手势词汇与网页那一版对齐（判定函数在 Core 的 BoardGestures，两边共用同一份语义）：
    // 左键拖空白＝框选 · 右键拖＝平移 · 静止的一发右键＝菜单。
    // 上一版是左键拖＝平移、Shift＋拖＝框选，于是"想框选要先按住一个键"。
    // 10-24 撤掉的只有"空格＋拖＝平移"那一条：空格从此不参与手势（按一下＝复位），右键那档留着。
    private bool _rightDrag;
    private Point _rightPressAt;
    private Point _panOrigin;
    private double _panTravel;

    private Point _marqueeOrigin;

    private Rectangle? _marqueeRect;

    private NodeGesture? _nodeGesture;

    /// <summary>这一发按下已经走过 4 像素的拖动阈值没有：没过就不许写格子的位置（手抖不是拖动）。
    /// 每一发按下重新归零，所以它跟着 <see cref="_nodeGesture"/> 的两处创建点各写一次 false。</summary>
    private bool _nodeDragStarted;

    private ResizeGesture? _resizeGesture;

    /// <summary>Last pointer position on the surface, in screen pixels: where a paste lands.</summary>
    private Point _lastPointer;

    private TextBox? _editor;

    /// <summary>编辑中内容需要的高度（世界像素）：只长不缩，提交时再按卡片规则落定。</summary>
    private double _editorGrownWorld;

    private string? _editingId;

    // 组改名那枚就地小编辑器（组没有正文，改的是 label）——和便签正文编辑器分开，各管各的。
    private TextBox? _groupRenameBox;
    private string? _groupRenameId;

    private TextBlock? _measureProbe;

    /// <summary>The same block the card paints, measured instead of a single TextBlock, when the body
    /// carries markdown — a heading's line box is taller than the body's and one uniform leading would
    /// fit a frame the paint then overflows.</summary>
    private StackPanel? _measureMd;

    /// <summary>Colour is not geometry: the fit-measure only needs the boxes, so every brush slot
    /// takes this one.</summary>
    private readonly SolidColorBrush _measureInk = new(Microsoft.UI.Colors.Black);

    private readonly History _history = new();

    private string? _pendingSnapshot;

    private string? _pendingLabel;

    private (CanvasNode Node, double Height)? _shotShrunk;

    private FileWatcher? _watcher;

    private ShotServer? _shotServer;
    private SyncHost? _sync;

    private byte[]? _lastWrittenHash;

    private bool _reloadPending;

    public MainWindow()
    {
        InitializeComponent();

        Diagnostics.Write($"startup: 皮与控件建好 +{App.SinceBoot():0}ms");

        Title = "铺陈";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 840));

        // 不再用 ExtendsContentIntoTitleBar/SetTitleBar 把整条宿主交给 WinUI：它会在客户区
        // 顶端留 1px 标题栏条（正常态的白线），最大化时又悄悄消失。标题条的拖动/双击改由
        // WindowChrome 的 WndProc + InputNonClient 的 Caption 区承担；标签/加号/按钮仍是客户区。

        // 标题栏左上角那枚图标：无包工程不会自己带上 csproj 的 ApplicationIcon，运行时拿到的是
        // 系统默认那枚彩色方块。要显式喂一个 .ico 的路径给 AppWindow（任务栏跟着它走）。
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "outspread.ico");

        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        else
        {
            Diagnostics.Write($"窗口图标没设：找不到 {iconPath}");
        }

        // Read before anything is painted, so a theme can be forced for capture and for
        // checking that the dark palette really moved rather than being assumed.
        var forced = Environment.GetEnvironmentVariable("TANKAI_THEME")?.Trim().ToLowerInvariant();

        Root.RequestedTheme = forced switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        NoteStyling.IsDark = forced == "dark";

        // 系统框整条去掉，标题按钮自绘。这台 Win10 上 DWMWA_BORDER_COLOR 不支持、
        // DwmExtendFrameIntoClientArea 也不改那条白/黑边，所以走这一档
        // （调研与实测见 docs/research-chrome-and-properties.md 的 10-22 第二轮）。
        BuildWindowChrome();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveNow();

        // 浮层的影落在板子自己身上：ThemeShadow 只往 receivers 上画，没有这一句它就是贴上去的一块白。
        ChromeStyling.ShadowReceiver = BoardArea;

        // 系统那三枚按钮（最小化／最大化／关闭）画在客户端区右上，标签条必须给它们让出宽度，
        // 否则最后一枚标签会被盖住。让多少不能拍脑袋：读 AppWindow.TitleBar.RightInset（设备像素）
        // 再除以这扇窗的缩放。要等 XamlRoot 有缩放才量得到，所以挂在第一次布局之后。
        Root.Loaded += (_, _) => ReserveCaptionButtons();

        // 两块"只有一行"的浮层：筛选横幅与回执那一句话。行就是菜单那一枚控件，文字由各自的事件改，
        // 所以建窗时搭一次、以后只换字——换字不改结构，就不会长成第三、第四种组件。
        _bannerRow = PanelKit.OneLine(string.Empty, null, ClearFilters);
        FilterBanner.Child = PanelKit.Host(_bannerRow);

        _sayRow = PanelKit.OneLine("正在读取板面…");
        SayHost.Child = PanelKit.Host(_sayRow);

        BuildSearchSurface();
        BuildBoardTabs();

        // 浮层出现/收起那一下走平台的"弹出层"transition：右键菜单用的就是这一族，
        // 板子上这四块浮层以前是"啪"地出现——同一套皮配两种动法，正是"不是一个组件"的读法。
        BoardArea.ChildrenTransitions = new TransitionCollection { new PopupThemeTransition() };

        BoardArea.PointerPressed += OnSurfacePressed;
        BoardArea.PointerMoved += OnSurfaceMoved;
        BoardArea.PointerReleased += OnSurfaceReleased;
        BoardArea.PointerCanceled += OnSurfaceReleased;
        BoardArea.PointerWheelChanged += OnWheel;
        BoardArea.SizeChanged += (_, _) => ArrangeAll();
        BoardArea.DragOver += OnDragOver;
        BoardArea.Drop += OnDrop;
        BoardArea.DoubleTapped += OnSurfaceDoubleTapped;
        // Right-click is handled in the pointer paths rather than through
        // ContextRequested: this projection gives no pointer position on that event, and a
        // menu that opens at the wrong place is worse than no keyboard-menu shortcut.

        // Keyboard shortcuts arrive through the surface: a Window is not a UIElement in
        // WinUI 3, and nothing else here holds focus until a note is being edited.
        BoardArea.IsTabStop = true;

        // 状态文字从"常驻一行"变成"有事才浮出来的一句话"：Say() 一被调用就出现，手再碰到板子就收起。
        Activated += (_, args) =>
        {
            // 离开这个窗口＝按住 Tab 的那只手不见了：清单必须收起，否则它会留在一块再也松不开键的板上。
            if (args.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            {
                CommitWheelPreview();
                CloseCheatsheet();
                CloseFilterPanel();
                CloseSearchPanel();
                CloseRestorePanel();
                CollapseExpandedGroup();

                // 手不见了，摊开的那一层也不能留着：它是 hover 叫出来的，只有这只手知道它为什么开着。
                return;
            }

            BoardArea.Focus(FocusState.Programmatic);

            // 睡着的机器醒来第一件事是补一轮：远端在这期间被手机改过几次，只有这里知道要看一眼。
            _sync?.NotifyForeground();
        };

        Root.ActualThemeChanged += (_, _) =>
        {
            NoteStyling.IsDark = Root.ActualTheme == ElementTheme.Dark;
            StyleCaptionButtons();
            StyleTabStripChrome();
            RepaintAllHeaders();
            Rebuild();
        };

        Root.KeyDown += OnKeyDown;
        Root.KeyUp += OnCheatKeyUp;

        // Tab 在 XAML 里先被焦点导航吃掉，KeyDown 往往看不到它 ⇒ 用 PreviewKeyDown 截。
        // 守卫在 OnCheatPreviewKeyDown 里：不在编辑/文本/浮层里就归板子，第一下就开；归不了板子
        // 的那一发也被这一句截住——他 10-23 要的就是"别再去通用地找按钮"。
        Root.PreviewKeyDown += OnCheatPreviewKeyDown;
        Root.PreviewKeyUp += OnSpacePreviewKeyUp;   // 空格：按下时截住，松手那一下复位视图（它不再兼任平移）

        // 任何一次按下都先把滚轮预览落到真布局：手柄/命中区不建立在"缩放层"上。
        Root.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => CommitWheelPreview()),
            handledEventsToo: true);

        // 空格那一发只要被板子上的任何一次按下用过，就不算"按一下复位"了。10-24 他报的正是这一发：
        // 按住空格再左键框选，视野跟着跳——撤掉"空格＋拖＝平移"之后，框选不再理空格，可松手那一下
        // 还是把复位打了出去。复位只该留给"什么也没干地敲了一下空格"。
        BoardArea.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _spaceTap = false),
            handledEventsToo: true);

        Closed += (_, _) =>
        {
            var at = Environment.TickCount64;

            CommitWheelPreview();
            _watcher?.Dispose();
            _shotServer?.Dispose();
            _sync?.Stop();

            Diagnostics.Write($"window closed · 收尾 {Environment.TickCount64 - at}ms · 活着 {App.SinceBoot():0}ms");
        };

        Diagnostics.Write($"window created; log at {Diagnostics.LogPath ?? "disabled"} · 构造完 +{App.SinceBoot():0}ms");

        // 试过在这里先 Activate() 再读板子，让窗口早 200ms 亮出来。量下来没有用：
        // 启动→窗口句柄 1091ms（改前）对 980/1213ms（改后），因为 Activate() 自己就是那
        // 270ms 的第一帧合成。白多一屏空板子的闪，所以撤。
        LoadBoard();

        // 命令行可以一次点几块板：第一块是启动那块，后面的开成标签。这一条不只是入口——
        // 三枚标签的标签条只有真开着三块板才拍得到，取证要走的也是它。
        foreach (var extra in Environment.GetCommandLineArgs()
                     .Skip(1)
                     .Where(argument => argument.EndsWith(CanvasStore.Extension, StringComparison.OrdinalIgnoreCase))
                     .Skip(1))
        {
            SwitchToBoard(extra);
        }
    }

    // -------------------------------------------------------------------------
    // Loading and saving
    // -------------------------------------------------------------------------

    /// <summary>
    /// 启动时开哪块板：命令行／环境变量说得最响，其次是"上次最后打开的那块"，再其次才是首次
    /// 开屏的教程板，最后落回 <see cref="BoardLocator.Resolve"/> 的老规矩。
    /// </summary>
    /// <remarks>
    /// 只在这一处用记忆：切板（<see cref="SwitchToBoard"/>）照旧由传进来的路径说了算，否则切走
    /// 一块以后又会被"上次那块"拽回去。记忆过期（文件没了）时 <see cref="BoardSession.ReadLastBoard"/>
    /// 已经返回 null，所以这里不会打开一个不存在的路径。
    /// </remarks>
    private static BoardLocator.Resolution ResolveStartupBoard(string? requested)
    {
        var searchStart = AppContext.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(requested)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BoardLocator.BoardPathEnvironmentVariable)))
        {
            return BoardLocator.Resolve(requested, searchStart);
        }

        if (BoardSession.ReadLastBoard() is { } last)
        {
            return new BoardLocator.Resolution(last, "the board this window was last on");
        }

        if (BoardLocator.FindQuickStart(searchStart) is { } quickStart)
        {
            return new BoardLocator.Resolution(quickStart, "first launch: the quick-start board");
        }

        return BoardLocator.Resolve(null, searchStart);
    }

    /// <summary>
    /// 记住这一次的开屏状态：当前这一块 + 开着的那一排（顺序＝标签顺序），下一次启动整排回来。
    /// 自检跑在临时副本上，不写这份记忆——否则自检会把它的临时标签排进他下一次开屏。
    /// </summary>
    private void RememberLastBoard(string path)
    {
        if (App.SelfTest)
        {
            return;
        }

        if (BoardSession.RememberLastBoard(path) is { } problem)
        {
            Diagnostics.Write($"记不住上次打开的板（不影响打开这块）：{problem}");
        }

        // 开着的这一排跟着当前这块一起写：每一次切板/开板/关板都会走到这里，所以不需要在每个
        // 改动 _openBoards 的地方各插一刀（那正是会漏的地方）。固定的「所有便签」不进记忆：
        // 它永远排第一，由启动那一段自己插。
        var open = _openBoards.Where(candidate => candidate != AllNotesKey).ToList();

        if (BoardSession.RememberOpenBoards(open) is { } openProblem)
        {
            Diagnostics.Write($"记不住开着的那一排板（不影响这一块）：{openProblem}");
        }
    }

    /// <param name="explicitPath">
    /// 切板时传进来的那块板。不给就走启动那一条（命令行 → 环境变量 → 上次打开的那块 →
    /// 首次开屏的教程板 → 向上找 boards/scratch.canvas）——那条路只在启动时走一次，
    /// 切板不许掉回它，否则会跳回启动那块板。
    /// </param>
    private void LoadBoard(string? explicitPath = null)
    {
        if (IsAllNotes(explicitPath))
        {
            LoadAllNotes();
            return;
        }

        try
        {
            var requested = explicitPath ?? Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(argument => argument.EndsWith(CanvasStore.Extension, StringComparison.OrdinalIgnoreCase));

            var resolution = ResolveStartupBoard(requested);

            var path = resolution.BoardPath;

            if (resolution.CreatedIfMissing)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                CanvasStore.Write(path, CanvasDocument.CreateNew().ToJson());
            }

            var text = CanvasStore.ReadAllText(path);
            var document = CanvasDocument.Parse(text, path);

            // 到这一行之前一个字段都没动：一块读不懂的板子不该把窗口切过去——那等于让人以为
            // 自己在改这块板，而屏幕上还是上一块。
            _boardPath = path;
            _document = document;
            _baseJson = text;

            // 先把上一次开着的那一排**读进局部量**：`RememberLastBoard` 现在两样一起写，而它必须写在
            // 恢复**之后**——写在前面就会把这一排按"此刻只剩这一块"覆盖掉，于是每开一次软件少一块板
            // （真机两发都红过：第一发 restore 空手而归，第二发只剩 2 枚标签）。
            IReadOnlyList<string> remembered = App.SelfTest ? [] : BoardSession.ReadOpenBoards();

            var sidecar = BoardViewState.TryLoad(CanvasStore.SidecarPath(_boardPath));

            _view = sidecar.State;
            _view.Zoom = Math.Clamp(_view.Zoom, MinZoom, MaxZoom);

            if (sidecar.Problem is not null)
            {
                Diagnostics.Write($"camera file unusable, using the default view: {sidecar.Problem}");
            }

            NoteStyling.IsDark = Root.ActualTheme == ElementTheme.Dark;
            StyleTabStripChrome();

            // 固定的「所有便签」永远排第一：只在它还没入列时插到 0，之后它关不掉、也不重插。
            if (!_openBoards.Contains(AllNotesKey))
            {
                _openBoards.Insert(0, AllNotesKey);
            }

            // 开着的那一排整排回来（2026-10-23 他报"我打开后没有快速入门的页面了"：以前只记当前那一块，
            // 关一次软件其余标签就凭空没了）。盘上已经不在了的那些不回来；当前这一块排在它原来的位置上。
            foreach (var board in remembered)
            {
                if (!_openBoards.Exists(open => SameBoard(open, board)))
                {
                    _openBoards.Add(board);
                }
            }

            if (!_openBoards.Exists(open => SameBoard(open, _boardPath)))
            {
                _openBoards.Add(_boardPath);
            }

            // 写记忆放在恢复之后：这一刻 `_openBoards` 才是"开着的这一排"的真相。
            RememberLastBoard(path);

            ResetPerBoardRuntime();

            // 页眉那一栏删了（板子该占满）。名字改在这行说一次，同时把两条入口一并交代清楚。
            Say($"板子 · {System.IO.Path.GetFileNameWithoutExtension(_boardPath)}｜双击空白记一件事，按住 Tab 看全部键位");

            Diagnostics.Write($"opened {_boardPath} — {resolution.Reason}; {_document.Nodes.Count} nodes");

            var issues = CanvasValidator.Validate(_document);
            var errors = issues.Count(issue => issue.Severity == CanvasIssueSeverity.Error);

            foreach (var issue in issues)
            {
                Diagnostics.Write($"{issue.Severity}: {issue.Message}");
            }

            Say(errors > 0
                ? $"{_document.Nodes.Count} 个节点 · {errors} 处不合格式（详情在日志） · 缩放 {Percent()}"
                : $"{_document.Nodes.Count} 个节点 · 缩放 {Percent()}");

            // 组框是派生的：开板时先把有成员的组框贴回成员包围盒。旧板/外部改动留下的
            // 旧框只在这一处归位一次，空组保持原样（渲染层会把它藏起来）。
            if (RefitLoadedGroups())
            {
                SaveSoon();
            }

            Rebuild();
            StartWatching();
            StartShotServer();
            RebuildTabs();

            // 同步要的是"这块板"，不是"这个窗口"：换板（--board 指过去）之后账本目录也跟着走，
            // 所以每个板子建一个，旧的先停表。没定远端仓库时它自己一声不响。
            _sync?.Stop();
            _sync = new SyncHost(_boardPath, Say, DispatcherQueue, FollowRemoteRename);
            _sync.Start();
        }
        catch (Exception ex) when (ex is CanvasDocumentException or JsonException or IOException
                                   or UnauthorizedAccessException or InvalidOperationException)
        {
            Say($"打不开板面：{ex.Message}");
            Diagnostics.Write($"load failed: {ex}");

            // 开不成也要让标签条说真话：切板失败时它已经指到那块板上了。
            RebuildTabs();
        }
    }

    /// <summary>
    /// Writes what the human actually did onto whatever is on disk right now, rather
    /// than dumping this process's copy of the board. That is what lets an agent's edit
    /// land in the same second without being overwritten.
    /// </summary>
    private void SaveNow()
    {
        if (_boardPath is null || _nodeGesture is not null || _resizeGesture is not null)
        {
            return;
        }

        // 「所有便签」是投影：存盘＝按归属拆回各真实文件写，父集本身没有文件。
        if (IsAllNotes(_boardPath))
        {
            SaveAllNotes();
            return;
        }

        try
        {
            // The camera moves even when nothing on the board does, so the sidecar is
            // written before the "nothing changed" check.
            _view.Prune(_document.Nodes.Select(node => node.Id));
            _view.Save(CanvasStore.SidecarPath(_boardPath));

            var patch = DocDiff.Between(CanvasDocument.Parse(_baseJson, _boardPath), _document);

            if (patch.IsEmpty)
            {
                return;
            }

            var (reports, writtenHash) = CanvasStore.SavePatch(_boardPath, patch);

            _lastWrittenHash = writtenHash;
            _baseJson = CanvasStore.ReadAllText(_boardPath);

            foreach (var report in reports.Where(report =>
                         report.Outcome is PatchOutcome.MissingNode or PatchOutcome.AlreadyPresent))
            {
                Diagnostics.Write($"save report {report.Outcome} for {report.Id}: {report.Detail}");
            }

            Diagnostics.Write($"saved {patch.NodeCount} changes, hash {CanvasStore.HashHex(writtenHash)[..8]}");

            _sync?.NotifyChanged();

            // 这里以前每次都浮出一句"已存盘 · N 个节点 · M 处改动"。存盘是常态，不是消息：
            // 它留在日志里，界面上不再说话——只有**没**存上才说（下面那个 catch 保留）。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CanvasDocumentException
                                  or JsonException or InvalidOperationException)
        {
            // The board keeps working with an unsaved document, but that has to be on screen.
            Say($"没存上：{ex.Message}");
            Diagnostics.Write($"save failed: {ex}");
        }
    }

    private void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private string Percent() => $"{Math.Round(_view.Zoom * 100)}%";

    /// <summary>
    /// Brushes for the shapes this app draws in code. The values are Core's
    /// <see cref="SelectionChrome"/>, so the tests on that class are tests on what gets painted.
    /// </summary>
    private Brush SelectionBrush => ChromeStyling.Ring;

    private Brush HandleFillBrush => ChromeStyling.HandleFace;

    private CanvasNode? NodeOf(string id) => _document.Find(id);

    // -------------------------------------------------------------------------
    // Realisation
    // -------------------------------------------------------------------------

    private void Rebuild()
    {
        ItemLayer.Children.Clear();
        _realized.Clear();

        // 卡片是重铸的，摊开那一层也就没了宿主：状态跟着清，别留一个指向旧格子的组名。
        ResetFolding();

        // 先算一次画面状态：紧接着的读数行与横幅要说"折进了几格"，而那件事只有这里知道。
        BuildDisplays();

        // 读数行和格子是同一次重建的产物：文字一改，逾期/标签的计数与横幅就得跟着变，
        // 否则它变成另一份"只在打开时算一次"的说明书。
        RebuildFilterPanel();
        ClearSelection();

        foreach (var node in _document.Nodes)
        {
            var visual = CreateVisual(node);

            _realized[node.Id] = visual;
            ItemLayer.Children.Add(visual);
        }

        ArrangeAll();
        ProjectSelection();
    }

    private NodeVisual CreateVisual(CanvasNode node)
    {
        // 「所有便签」里每格的图片要按它**所属的那块板**解析（相对路径相对源文件的 assets/），
        // 不能拿哨兵当 base——那会把全部图片解析成"找不到"，甚至踩到 Border.Child 的异常。
        var visual = new NodeVisual(node, relative =>
        {
            if (_boardPath is null)
            {
                return null;
            }

            var basePath = _boardPath;

            if (IsAllNotes(_boardPath))
            {
                basePath = BoardMerge.TryUnmerge(node.Id, out var owner, out _) ? owner : UnfiledPath;
            }

            return AssetStore.ResolveExisting(basePath, relative);
        });

        visual.PointerPressed += OnNodePressed;

        // 悬停摊开折叠那一层：每一格都挂，谁算"这一组的门"由 BuildDisplays 那张表说
        // （框本身＋接住东西的那一格）。挂在格子上而不是只挂在框上，是因为门要够大，
        // 而精确地瞄一条 10 像素的边正是他不肯要的那种操作。
        visual.PointerEntered += OnVisualEntered;
        visual.PointerExited += OnVisualExited;

        // 卡片左下角那排牌子的删除路径走这里：它删的是正文里那一枚 token，所以是一步可撤销的文档改动，
        // 不是视图状态。
        visual.RemoveMark = RemoveMarkFrom;

        return visual;
    }

    private void ArrangeAll()
    {
        ChromeStyling.Ensure(NoteStyling.IsDark);
        RefreshZoomLabel();
        StyleBadge();
        PaintLinks();
        UpdateAtmosphere();

        var width = BoardArea.ActualWidth;
        var height = BoardArea.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var scale = BoardArea.XamlRoot?.RasterizationScale ?? 1;
        var viewport = _view.WorldViewport(width, height);
        var margin = CullMargin / _view.Zoom;

        var window = new Rect(
            viewport.X - margin,
            viewport.Y - margin,
            viewport.Width + (2 * margin),
            viewport.Height + (2 * margin));

        var visible = 0;
        var started = Stopwatch.GetTimestamp();

        // 筛选折起来的那几格：它们画在锚点上，所以剔除要按"画在哪儿"判，不按文档里的真位置判——
        // 否则锚点在屏内、真位置在屏外的那一格会在摊开时是空的。
        var displays = BuildDisplays();
        var emptyGroups = EmptyGroupIds();

        foreach (var node in _document.Nodes)
        {
            if (!_realized.TryGetValue(node.Id, out var visual))
            {
                continue;
            }

            // 空组没有成员可框：不画、不接鼠标，别拿一个旧的大虚线框压着旁边的便签。
            // 一旦有便签挪进它的矩形，EmptyGroupIds 不再含它，这里会把它恢复可见。
            if (node.Kind == CanvasNodeKind.Group && emptyGroups.Contains(node.Id))
            {
                visual.Visibility = Visibility.Collapsed;
                visual.IsHitTestVisible = false;

                continue;
            }

            visual.Visibility = Visibility.Visible;

            var display = displays.TryGetValue(node.Id, out var planned) ? planned : NodeVisual.Display.None;

            var box = display.ShownFrame ?? RectOf(node);
            var drawnX = box.X + ((display.FoldDX ?? 0) / _view.Zoom);
            var drawnY = box.Y + ((display.FoldDY ?? 0) / _view.Zoom);

            var intersects = drawnX + box.Width >= window.X
                && drawnX <= window.X + window.Width
                && drawnY + box.Height >= window.Y
                && drawnY <= window.Y + window.Height;

            if (intersects)
            {
                // 折进去的那一格不能点：它不在那儿，只是滑过去的那一下让人看见了。
                visual.IsHitTestVisible = !display.FoldedAway;

                visual.Update(node, _view, scale, display);
                visible++;
            }
            else
            {
                // Hidden, not discarded: an element that is thrown away has to be
                // decoded again on the next pan back over a board of screenshots.
                // Visibility is the part that was missing: the card's three shadow bands are
                // separate rectangles outside its zero-sized box, so Width=0 alone left them
                // painted at the old place — the "拖完还留着一排阴影" he sent on 10-22.
                visual.Visibility = Visibility.Collapsed;
                visual.IsHitTestVisible = false;
                visual.Width = 0;
                visual.Height = 0;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        // The editor is screen-space furniture: panning or zooming while typing has to
        // move it with the note, or the caret ends up somewhere else on the board.
        PlaceEditor();

        if (elapsed.TotalMilliseconds > 16)
        {
            Diagnostics.Write(
                $"arrange took {elapsed.TotalMilliseconds:0.0} ms for {_document.Nodes.Count} nodes ({visible} visible)");
        }
    }

    // -------------------------------------------------------------------------
    // Pointer: pan, zoom, marquee
    // -------------------------------------------------------------------------

    private void OnSurfacePressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled || _nodeGesture is not null || _resizeGesture is not null)
        {
            return;
        }

        // 按到板子上任何别处＝这一格编辑到此为止，然后这一发照常去选中/框选/平移（网页上
        // mousedown 在别处也是先提交再动作）。不能只等 LostFocus：WinUI 里按一块不持焦点的
        // 区域不会把焦点从 TextBox 拿走，所以"点了别处还在编辑"是必然，不是偶发。
        if (_editingId is not null)
        {
            CloseEditor(commit: true);
        }

        // 面板是"问一次答一次"的东西：手回到板子上，它就该收起。浮着的那句话同理——
        // 不靠计时，靠下一次动作。
        CloseFilterPanel();
        SayHost.Visibility = Visibility.Collapsed;
        CloseCheatsheet();
        CloseSearchPanel();
        CloseRestorePanel();
        ClearGroupHint();
        CloseColorPopover();

        var point = e.GetCurrentPoint(BoardArea);

        // 拉线中按到空白＝这根线没画过（一个字节都没动，所以也不需要撤销）。
        if (Pulling && FinishPull(point.Position))
        {
            e.Handled = true;
            return;
        }

        // 右键那一发先当作平移，松手时没拖过阈值再改成菜单。空格不参与（10-24 撤了"空格＋拖＝平移"，
        // 它现在只有一件事：按一下＝复位，见 Cheatsheet 的 OnSpacePreviewKeyUp）。
        var action = BoardGestures.OnEmptyPress(
            point.Properties.IsRightButtonPressed, point.Properties.IsLeftButtonPressed);

        if (action == SurfaceAction.Ignore)
        {
            return;
        }

        var position = point.Position;

        if (action == SurfaceAction.Pan)
        {
            _rightDrag = point.Properties.IsRightButtonPressed;
            _rightPressAt = position;
            _panning = true;
            _panStartX = position.X;
            _panStartY = position.Y;
            _panOrigin = position;
            _panTravel = 0;

            BoardArea.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        _marqueeing = true;
        _marqueeOrigin = position;

        _marqueeRect = new Rectangle
        {
            Stroke = ChromeStyling.Ring,
            StrokeThickness = SelectionChrome.RingThicknessPx,
            Fill = ChromeStyling.MarqueeFill,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(_marqueeRect, position.X);
        Canvas.SetTop(_marqueeRect, position.Y);
        HandleLayer.Children.Add(_marqueeRect);

        BoardArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSurfaceMoved(object sender, PointerRoutedEventArgs e)
    {
        var screen = e.GetCurrentPoint(BoardArea).Position;

        _lastPointer = screen;

        if (Pulling)
        {
            TrackPull(screen);
        }

        if (_panning)
        {
            // 右键平移的前提是"那枚键还按着"。双击右键时菜单吃掉了一次抬起，_panning 就留在原地，
            // 于是下一发不管按哪个键都被当成平移。这里按按键的真实状态当场认出来并收掉——
            // 是"不应期"，但不靠计时：抬起过没抬起，指针的当前状态知道，固定等待只会让它更慢。
            if (_rightDrag && !e.GetCurrentPoint(BoardArea).Properties.IsRightButtonPressed)
            {
                _panning = false;
                _rightDrag = false;

                PanShift.X = 0;
                PanShift.Y = 0;

                ArrangeAll();
                ProjectSelection();

                BoardArea.ReleasePointerCapture(e.Pointer);
                e.Handled = true;

                return;
            }

            _panTravel = Math.Max(_panTravel, Math.Abs(screen.X - _panOrigin.X) + Math.Abs(screen.Y - _panOrigin.Y));

            var dx = screen.X - _panStartX;
            var dy = screen.Y - _panStartY;

            _view.PanBy(dx, dy);

            _panStartX = screen.X;
            _panStartY = screen.Y;

            // 平移期间只搬这一层，不逐格重排：日志里那行 "arrange took 20–50 ms" 就是滞涩感的来源，
            // 每帧重排一次＝20fps。松手时再结算成真正的排布（见 OnSurfaceReleased）。
            // 位移不改缩放，所以字形不会因为走变换而变糊——那条"不用 render transform"的规矩针对的是缩放。
            PanShift.X += dx;
            PanShift.Y += dy;

            UpdateAtmosphere();
            e.Handled = true;
            return;
        }

        if (_marqueeing && _marqueeRect is not null)
        {
            var left = Math.Min(screen.X, _marqueeOrigin.X);
            var top = Math.Min(screen.Y, _marqueeOrigin.Y);

            Canvas.SetLeft(_marqueeRect, left);
            Canvas.SetTop(_marqueeRect, top);
            _marqueeRect.Width = Math.Abs(screen.X - _marqueeOrigin.X);
            _marqueeRect.Height = Math.Abs(screen.Y - _marqueeOrigin.Y);

            e.Handled = true;
            return;
        }

        if (_nodeGesture is { } gesture)
        {
            ApplyNodeDrag(screen);

            // 组钮那一路的拖动也走同一条位移；多出来的只有框上的蓝/红与光标那枚淡环。
            if (gesture.GroupMode)
            {
                UpdateGroupAlerts(screen);
            }

            e.Handled = true;
            return;
        }

        if (_resizeGesture is { } resize)
        {
            ApplyResize(screen, resize);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 一格（或一组）跟着指针走的那段位移：板面拖动和组钮拖动共用——帧预算的规矩在同一条上
    /// （只搬被拖的格子，松手再结算完整排布）。
    /// </summary>
    private void ApplyNodeDrag(Point screen)
    {
        if (_nodeGesture is not { } gesture)
        {
            return;
        }

        var worldX = _view.ToWorldX(screen.X);
        var worldY = _view.ToWorldY(screen.Y);

        var deltaX = worldX - gesture.StartPointerX;
        var deltaY = worldY - gesture.StartPointerY;

        // 没过 4 像素的那一发是手抖，不是拖动。以前每一发 PointerMoved 都直接写进格子，
        // 于是"单击选中"偶尔留下一两像素的位移（10-24 他报这一格；日志里的凭据是
        // "drag ended: 1 node(s), 2.0 world px"）。阈值与框选读的是同一枚数。
        if (!_nodeDragStarted && !BoardGestures.IsDrag((Math.Abs(deltaX) + Math.Abs(deltaY)) * _view.Zoom))
        {
            return;
        }

        _nodeDragStarted = true;

        var (adjustedX, adjustedY, guides) = SnapDrag(gesture, deltaX, deltaY);

        deltaX = adjustedX;
        deltaY = adjustedY;

        foreach (var (id, start) in gesture.StartGeometry)
        {
            if (NodeOf(id) is { } node)
            {
                node.X = start.X + deltaX;
                node.Y = start.Y + deltaY;
            }
        }

        // 帧预算：拖一格时只有被拖的那几格换了位置，其余格子的尺寸、裁切、行数都没变——
        // 逐格 Update 一次要 16–60ms（日志 "arrange took"），所以这里只搬被拖的格子，
        // 松手再结算成完整的排布（补剔除、补别的格子）。
        foreach (var id in gesture.StartGeometry.Keys)
        {
            if (NodeOf(id) is { } moved && _realized.TryGetValue(id, out var visual))
            {
                Canvas.SetLeft(visual, _view.ToScreenX(moved.X));
                Canvas.SetTop(visual, _view.ToScreenY(moved.Y));
            }
        }

        DrawGuides(guides);
        PaintLinks();
        ProjectSelection();
    }

    private void OnSurfaceReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_edgeDrag is { } drag)
        {
            _edgeDrag = null;

            var at = e.GetCurrentPoint(BoardArea).Position;
            var worldX = _view.ToWorldX(at.X);
            var worldY = _view.ToWorldY(at.Y);

            var changed = _document.Find(drag.NodeId) is { } card
                && _document.Edges.Any(edge => edge.Id == drag.EdgeId)
                && _document.SetEdgeSide(drag.EdgeId, drag.End == CanvasEdge.FromEnd, CanvasEdge.NearestSide(card, worldX, worldY));

            EndGesture();
            Rebuild();

            if (changed)
            {
                SaveSoon();
            }
            else
            {
                Say("这一头本来就在那一面");
            }

            e.Handled = true;
            return;
        }

        if (_panning)
        {
            _panning = false;

            // 结算：临时位移归零，真正的逐格排布跑一次。松手后看到的和拖的过程中看到的是同一张板子。
            PanShift.X = 0;
            PanShift.Y = 0;

            ArrangeAll();
            ProjectSelection();

            if (_rightDrag)
            {
                _rightDrag = false;

                if (BoardGestures.IsDrag(_panTravel))
                {
                    SaveSoon();
                }
                else
                {
                    // 静止的一发右键＝菜单（tldraw 的 rightClickPanning 就是这个语义：
                    // 拖＝平移，静止一按＝在松手的位置弹菜单）
                    ShowBoardMenu(_rightPressAt);
                }
            }
            else
            {
                SaveSoon();
            }

            _panTravel = 0;
        }

        if (_marqueeing)
        {
            _marqueeing = false;

            if (_marqueeRect is not null)
            {
                var left = _view.ToWorldX(Canvas.GetLeft(_marqueeRect));
                var top = _view.ToWorldY(Canvas.GetTop(_marqueeRect));
                var width = _marqueeRect.Width / _view.Zoom;
                var height = _marqueeRect.Height / _view.Zoom;

                HandleLayer.Children.Remove(_marqueeRect);
                _marqueeRect = null;

                if (BoardGestures.IsDrag(width) && BoardGestures.IsDrag(height))
                {
                    // Shift 或 Ctrl 都算加选（网页用 Shift，本程序原先只认 Ctrl ⇒ 两枚都收）
                    var append = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)
                        || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);

                    SelectInside(new Rect(left, top, width, height), append: append);
                }
                else if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
                {
                    // 没拖过阈值的一按＝一次点击：清空选中。上一版这个动作发生在"按下"那一帧，
                    // 于是左键框选根本不存在。
                    ClearSelection();
                }

                ProjectSelection();
            }
        }

        if (_nodeGesture is { } gesture)
        {
            var moved = gesture.StartGeometry.Sum(pair =>
                Math.Abs((NodeOf(pair.Key)?.X ?? pair.Value.X) - pair.Value.X)
                + Math.Abs((NodeOf(pair.Key)?.Y ?? pair.Value.Y) - pair.Value.Y));

            _nodeGesture = null;
            DrawGuides([]);

            Diagnostics.Write($"drag ended: {gesture.StartGeometry.Count} node(s), {moved:0.0} world px");

            // 普通拖动到这里就结束了：它既不写成员名单，也不为了"接住这一格"把框拉长——
            // 框在下面那一拍按名单自己 hug（EndGesture → RefitFrames → Groups()）。
            // 10-24 之前这里叫 KeepDraggedMemberInFrame：靠把框拉长去接那一格做到"不默默脱组"，
            // 代价就是框长到吞掉路上别人、虚线边拉到屏幕外看着像组消失了。

            EndGesture();
            SaveSoon();

            // 结算：把拖中省下的那一次完整排布补回来（剔除视口外的格子、重算别的格子）。
            ArrangeAll();

            // C 那一拍：挪的是一枚散格（不是组钮那一路）、松手时它压着另两枚以上 ⇒ 格下浮「编成组？」。
            if (gesture.StartGeometry.Count == 1 && moved > 0 && !gesture.GroupMode)
            {
                OfferGroupHint(NodeOf(gesture.PrimaryId));
                PlaceGroupHint(); // 浮出之后还贴着：胶囊要落在松手的终点，不是拖中途经的某一帧
            }
        }

        if (_resizeGesture is { } resize)
        {
            // 手动拖过大小的文字格：以后回车不再改它的框；这是他自己定下的尺寸。
            foreach (var (node, _, _, startWidth, startHeight) in resize.Start.Values)
            {
                if (node.Kind == CanvasNodeKind.Text
                    && (Math.Abs(node.Width - startWidth) > 0.5 || Math.Abs(node.Height - startHeight) > 0.5))
                {
                    _view.MarkManualSize(node.Id);
                }
            }

            _resizeGesture = null;
            EndGesture();
            SaveSoon();

            ArrangeAll();
        }

        // Released unconditionally: capture is taken for pan, marquee, drag and resize,
        // and a stuck capture would leave the board panning with no button held.
        BoardArea.ReleasePointerCapture(e.Pointer);

        // An agent may have written while this gesture was held; that reload was deferred,
        // and it is applied the moment nothing is in flight.
        DrainPendingReload();

        e.Handled = true;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(BoardArea);
        var delta = point.Properties.MouseWheelDelta;

        if (delta == 0)
        {
            return;
        }

        // 正在写那一格时，滚轮不再是"缩放整块板子"：那一发要把正在编辑的内容滚进视野
        // （他 10-24 点的）。一格走多少屏幕像素是手感，所以这个数留在界面，几何在 Core。
        RouteWheel(point.Position, delta);
        e.Handled = true;
    }

    /// <summary>
    /// 滚轮那一发的落点：编辑中＝滚这一格，其余＝缩放整块板子。
    /// </summary>
    /// <remarks>
    /// 事件那一层只负责"拿到 delta ＋ 交出去"，判据全在这里——界面自检走的也是这一句，
    /// 所以"编辑中不许缩放"这条接线被摘掉时红的是自检，不是一句注释。
    /// </remarks>
    private void RouteWheel(Point position, int delta)
    {
        if (_editingId is not null)
        {
            if (!ScrollEditorIntoView(delta))
            {
                Say("这一格已经全部看得见：编辑中滚轮不缩放整块板子");
            }

            return;
        }

        ApplyWheel(position, delta);
    }

    /// <summary>
    /// 把正在编辑的那一格朝滚轮那一头搬进视野。返回 false＝这一发没地方可滚。
    /// </summary>
    private bool ScrollEditorIntoView(int delta)
    {
        if (_editor is null)
        {
            return false;
        }

        var step = EditorScroll.Reveal(
            Canvas.GetTop(_editor),
            _editor.ActualHeight > 0 ? _editor.ActualHeight : _editor.Height,
            BoardArea.ActualHeight,
            delta,
            EditorScrollPxPerNotch);

        if (step == 0)
        {
            return false;
        }

        _view.PanBy(0, step);
        ArrangeAll();
        PlaceEditor();
        ProjectSelection();
        SaveSoon();

        return true;
    }

    // -------------------------------------------------------------------------
    // Wheel zoom: a transform during the gesture, one crisp rebuild when it stops
    // -------------------------------------------------------------------------

    /// <summary>
    /// 手势期间的缩放预览：卡片走一枚合成变换、点阵按缓动中那一档重画，都不重排、不存盘。
    /// </summary>
    /// <remarks>
    /// 2026-10-23 他报"快速滚轮卡顿"。现读：每一发 zoom+arrange 是 64–83ms（其中节点重排 31–44ms、
    /// 网格重绘另算，最慢一发 132ms）；滚轮事件只能排在那后面，手一快就攒成一队。macOS 那种丝滑
    /// 靠的不是把同一件事做快，而是手势期间只动合成变换、停手再落一次真布局。
    /// <para>
    /// 所以：滚轮期间 <see cref="PanLayer"/> 套一枚以指针为圆心的 <see cref="ScaleTransform"/>，
    /// 每一发只改 <see cref="_wheelTargetZoom"/>；把画面推上去的是 <see cref="OnWheelFrame"/>
    /// ——每帧朝目标走一小步（<see cref="ZoomTween"/>），120ms 没有新滚轮才提交：相机走一次
    /// <see cref="BoardViewState.ZoomAt"/>、重排一次、网格按真相机重画一张清晰的。
    /// </para>
    /// <para>
    /// 10-24 这一档改了两件事，都是他点的"缩放突兀"：① 台阶→缓动（原来每一发滚轮把缩放**立刻**
    /// 推到 ×1.12，快速滚就是一截一截）；② 点阵不再冻在旧缩放档上（原来它整个手势期间不动、
    /// 提交才啪地补一张，卡片在走而点不走的错位最显眼）。现读的代价：预览那一发点阵 4.6–6.8ms
    /// （不数墨），一帧预算 16.7ms ⇒ 点阵与卡片同帧；数墨那一发（3.7–14.5ms）只在提交时跑。
    /// </para>
    /// <para>
    /// 代价说清楚：手势中文字会暂时被合成缩放（略糊）；缩小预览时，视口外原本被剔除的格子
    /// 要等提交那一拍才出现。两者都只活到 120ms 的提交点。编辑中或正在拖拽时走旧的一发路径
    /// （<see cref="WheelZoomStep"/>），不把输入法/手势卷进缩放层。
    /// </para>
    /// </remarks>
    private bool _wheelPreview;
    private Point _wheelAnchor;
    private double _wheelStartZoom;
    private double _wheelTargetZoom;
    private ScaleTransform? _wheelPanScale;
    private DispatcherQueueTimer? _wheelSettle;

    /// <summary>预览现在显示到哪一档缩放——缓动走的就是它，不等于 <see cref="_wheelTargetZoom"/>。</summary>
    private double _wheelShownZoom;

    private bool _wheelTweening;

    /// <summary>点阵上次按哪一档重画的，以及那一发落在哪一毫秒（节流用）。</summary>
    private double _wheelGridZoom = -1;

    private long _wheelGridPaintedAt;

    private void ApplyWheel(Point position, int delta)
    {
        // 编辑中／正在拖／正在平移：这几档对"指针底下的世界"有依赖，走一次到位的旧路径。
        if (_editingId is not null
            || _nodeGesture is not null
            || Math.Abs(PanShift.X) > 0.01
            || Math.Abs(PanShift.Y) > 0.01)
        {
            WheelZoomStep(position, delta);
            return;
        }

        EnsureWheelSettle();

        if (!_wheelPreview)
        {
            _wheelPreview = true;
            _wheelAnchor = position;
            _wheelStartZoom = _view.Zoom;
            _wheelTargetZoom = _view.Zoom;
            _wheelShownZoom = _view.Zoom;
            _wheelGridZoom = _view.Zoom;

            // 只缩放界面层；网格由 <see cref="RepaintGridAt"/> 跟着缓动那一档重画——10-24 之前
            // 它是冻在旧档上、提交才补一张，那一下"忽然放大缩小"就是这里来的。
            _wheelPanScale = new ScaleTransform
            {
                CenterX = position.X,
                CenterY = position.Y,
            };

            PanLayer.RenderTransform = _wheelPanScale;
        }

        _wheelTargetZoom = Math.Clamp(
            _wheelTargetZoom * (delta > 0 ? WheelStep : 1 / WheelStep), MinZoom, MaxZoom);

        // 这一发只改目标；把画面推上去的是 <see cref="OnWheelFrame"/>（每帧一小步，不是一发一档台阶）。
        EnsureWheelTween();

        _wheelSettle!.Stop();
        _wheelSettle.Start();
    }

    private void EnsureWheelSettle()
    {
        if (_wheelSettle is not null)
        {
            return;
        }

        _wheelSettle = DispatcherQueue.CreateTimer();
        _wheelSettle.Interval = TimeSpan.FromMilliseconds(120);
        _wheelSettle.IsRepeating = false;
        _wheelSettle.Tick += (_, _) => CommitWheelPreview();
    }

    /// <summary>挂上每帧那一小步（已经在走就不重复挂）。</summary>
    private void EnsureWheelTween()
    {
        if (_wheelTweening)
        {
            return;
        }

        _wheelTweening = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnWheelFrame;
    }

    private void StopWheelTween()
    {
        if (!_wheelTweening)
        {
            return;
        }

        _wheelTweening = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnWheelFrame;
    }

    /// <summary>
    /// 一帧：把预览比例朝目标推一小步，并让点阵按这一帧那一档重画。
    /// </summary>
    private void OnWheelFrame(object? sender, object args)
    {
        if (!_wheelPreview)
        {
            StopWheelTween();

            return;
        }

        _wheelShownZoom = ZoomTween.Step(_wheelShownZoom, _wheelTargetZoom);

        if (_wheelPanScale is { } preview)
        {
            preview.ScaleX = preview.ScaleY = _wheelShownZoom / _wheelStartZoom;
        }

        // 比例那一格跟着缓动走；提交后 ArrangeAll 会按真相再刷一次。
        ZoomLevelText.Text = $"{Math.Round(_wheelShownZoom * 100)}%";

        RepaintGridAt(_wheelShownZoom);

        if (_wheelShownZoom == _wheelTargetZoom)
        {
            StopWheelTween();
        }
    }

    /// <summary>
    /// 点阵跟着缓动那一档走。两重节流：缩放差不到 3% 不重画，且一帧预算最多 33ms 一发。
    /// </summary>
    /// <remarks>
    /// 代价现读（1360×842 那张）：预览那一发 4.6–6.8ms（不数墨），提交那一发 8.7–21.5ms。
    /// 相机以指针为不动点，所以这一帧的 pan 由锚点反解：点阵与卡片必须走同一条几何，
    /// 不然缓动中点与字会各滑各的。
    /// </remarks>
    private void RepaintGridAt(double zoom)
    {
        var now = Environment.TickCount64;

        if (_wheelGridZoom > 0 && Math.Abs((zoom / _wheelGridZoom) - 1) < 0.03)
        {
            return;
        }

        if (now - _wheelGridPaintedAt < GridTweenMinIntervalMs)
        {
            return;
        }

        _wheelGridZoom = zoom;
        _wheelGridPaintedAt = now;

        var worldX = _view.ToWorldX(_wheelAnchor.X);
        var worldY = _view.ToWorldY(_wheelAnchor.Y);

        UpdateAtmosphereAt(zoom, _wheelAnchor.X - (worldX * zoom), _wheelAnchor.Y - (worldY * zoom), forensics: false);
    }

    /// <summary>停手 120ms 后落一次真布局；进拍图、切板、失焦、指针按下时也先落到这里。</summary>
    private void CommitWheelPreview()
    {
        if (!_wheelPreview)
        {
            return;
        }

        _wheelPreview = false;
        _wheelSettle?.Stop();
        StopWheelTween();

        PanLayer.RenderTransform = PanShift;
        _wheelPanScale = null;

        var factor = _wheelTargetZoom / _wheelStartZoom;

        if (Math.Abs(factor - 1) <= 1e-9)
        {
            return;
        }

        _view.ZoomAt(_wheelAnchor.X, _wheelAnchor.Y, factor, MinZoom, MaxZoom);

        // 缓动中途点阵可能停在离目标 3% 以内的那一档；提交这一拍按真相机重画一张清晰的。
        _gridKey = string.Empty;

        ArrangeAll();
        ProjectSelection();
        SaveSoon();

        if (_wheelTargetZoom <= MinZoom + 1e-9 || _wheelTargetZoom >= MaxZoom - 1e-9)
        {
            Say($"缩放已到边界（{Percent()}）");
        }
    }

    /// <summary>一次到位的旧路径：编辑、拖拽、平移中仍走它（这些场景不允许指针底下的世界先变形）。</summary>
    private void WheelZoomStep(Point position, int delta)
    {
        var before = _view.Zoom;

        _view.ZoomAt(position.X, position.Y, delta > 0 ? WheelStep : 1 / WheelStep, MinZoom, MaxZoom);

        ArrangeAll();
        ProjectSelection();
        SaveSoon();

        var clamped = Math.Abs(before - _view.Zoom) < double.Epsilon;

        Say(clamped ? $"缩放已到边界（{Percent()}）" : string.Empty);
    }

    private int VisibleCount() => _realized.Count(pair => pair.Value.IsHitTestVisible);

    // -------------------------------------------------------------------------
    // Nodes: select, drag
    // -------------------------------------------------------------------------

    /// <summary>
    /// 这一发按下该选中什么、组这一层归谁：按在组上（名牌或环带）＝组里所有便签，框自己不进
    /// 选中；按在别处＝那一枚。界面只问这一次，规则本体在 <see cref="Grouping.OnFramePressed"/>。
    /// </summary>
    private (List<string> Ids, string? Focus) PressTarget(CanvasNode pressed)
        => pressed.Kind == CanvasNodeKind.Group
            ? ([.. Groups().OnFramePressed(RectOf(pressed))], pressed.Id)
            : ([pressed.Id], null);

    private void OnNodePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not NodeVisual visual)
        {
            return;
        }

        var point = e.GetCurrentPoint(visual);

        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        var id = visual.NodeId;

        // 同一条规矩：按在另一格上也是"这一格改完了"。先提交，再决定这一发是选中还是拖动。
        if (_editingId is not null)
        {
            CloseEditor(commit: true);
        }

        CloseFilterPanel();

        SayHost.Visibility = Visibility.Collapsed;

        CloseCheatsheet();

        CloseSearchPanel();
        CloseRestorePanel();
        ClearGroupHint();

        var pointOnSurface = e.GetCurrentPoint(BoardArea).Position;

        if (point.Properties.IsRightButtonPressed)
        {
            var rightNode = NodeOf(id);

            if (rightNode is not null)
            {
                var already = rightNode.Kind == CanvasNodeKind.Group ? _groupFocus == id : _selection.Contains(id);

                if (!already)
                {
                    var (ids, focus) = PressTarget(rightNode);

                    ClearSelection();

                    foreach (var selected in ids)
                    {
                        _selection.Add(selected);
                    }

                    _groupFocus = focus;
                    ProjectSelection();
                }
            }

            ShowBoardMenu(pointOnSurface);
            e.Handled = true;
            return;
        }

        if (NodeOf(visual.NodeId) is not { } pressedNode)
        {
            return;
        }

        var additive = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control);

        if (additive)
        {
            if (pressedNode.Kind == CanvasNodeKind.Group)
            {
                // Ctrl 点在名牌／环带上＝整组加进选出、再点一次整组退出。
                var memberIds = Groups().OnFramePressed(RectOf(pressedNode));
                var allIn = memberIds.All(_selection.Contains);

                foreach (var member in memberIds)
                {
                    if (allIn)
                    {
                        _selection.Remove(member);
                    }
                    else
                    {
                        _selection.Add(member);
                    }
                }

                _groupFocus = allIn ? null : pressedNode.Id;
            }
            else if (!_selection.Remove(id))
            {
                _selection.Add(id);
            }

            ProjectSelection();
            e.Handled = true;
            return;
        }

        if (!_selection.Contains(id))
        {
            // 单击的落点（他 10-22 定的语义）：点便签＝只选中那一枚，**包括组里的**；点组的
            // 名牌或边缘虚线＝选中组里所有便签。组是一个关系，不是一块场地：框自己从不进
            // 选中，所以不会再出现"整个组的范围变蓝"。判定抽在 PressTarget，自检直接问它。
            var (ids, focus) = PressTarget(pressedNode);

            ClearSelection();

            foreach (var selected in ids)
            {
                _selection.Add(selected);
            }

            _groupFocus = focus;
        }

        // 谁后被点击谁在最上面：把这一枚提到最上层（不重铸、不清选中）。组本身不抬（它的框是派生的）。
        RaiseToFront(id);

        var startGeometry = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        foreach (var selectedId in _selection)
        {
            if (NodeOf(selectedId) is { } node)
            {
                startGeometry[selectedId] = (node.X, node.Y);
            }
        }

        if (startGeometry.Count == 0)
        {
            return;
        }

        var screen = e.GetCurrentPoint(BoardArea).Position;

        // 拉线中：这一发按在这格上就是落点，不归移动也不归缩放。
        if (Pulling && FinishPull(screen))
        {
            e.Handled = true;
            return;
        }

        // 压边＝缩放：离某条边 6 屏幕像素以内，这一发归那条边。网页那一侧同一格就是这个语义，
        // 而"抓哪儿都是移动"会让人以为边缘拖缩放没做——它做了，只是被移动抢走了。
        // 组框不吃这一发：它的边是"点边＝选整组"的门，缩放一个派生出来的框也没有意义。
        if (_selection.Count == 1 && NodeOf(id) is { Kind: not CanvasNodeKind.Group } pressed && EdgeOf(pressed, screen) is { } edge)
        {
            BeginResize(edge, screen);

            BoardArea.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        BeginGesture(startGeometry.Count > 1 ? $"移动 {startGeometry.Count} 格" : "移动一格");

        // 普通拖动要记住"这一格此刻在谁的框里、那框此刻都有谁"：松手时如果它被甩出了框，
        // 框跟着长（sticky）而不是把它踢出去——拆组只归组钮那一条路（NoteActionRail.cs）。
        var startRect = RectOf(pressedNode);
        var startGroup = pressedNode.Kind == CanvasNodeKind.Group
            ? null
            : Groups().GroupOf(startRect);
        IReadOnlyList<string>? startMembers = null;

        if (startGroup is { } sg)
        {
            startMembers = [.. Groups().Members(sg.Id).Select(member => member.Id)];
        }

        _nodeDragStarted = false;

        _nodeGesture = new NodeGesture(
            _view.ToWorldX(screen.X),
            _view.ToWorldY(screen.Y),
            startGeometry,
            id,
            GroupMode: false,
            StartGroupId: startGroup?.Id,
            StartMembers: startMembers);

        ProjectSelection();

        // Captured on the surface, not the node: a fast drag leaves the node's bounds
        // within a frame, and capture on the node would drop the gesture mid-drag.
        BoardArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SelectInside(Rect worldRect, bool append)
    {
        if (!append)
        {
            ClearSelection();
        }

        var emptyGroups = EmptyGroupIds();

        foreach (var node in _document.Nodes)
        {
            if (node.Kind == CanvasNodeKind.Group && emptyGroups.Contains(node.Id))
            {
                continue;
            }

            var intersects = node.X + node.Width >= worldRect.X
                && node.X <= worldRect.X + worldRect.Width
                && node.Y + node.Height >= worldRect.Y
                && node.Y <= worldRect.Y + worldRect.Height;

            if (intersects)
            {
                _selection.Add(node.Id);
            }
        }

        Diagnostics.Write($"marquee selected {_selection.Count} node(s)");
    }

    // -------------------------------------------------------------------------
    // Resize
    // -------------------------------------------------------------------------

    private void BeginResize(string direction, Point screenPointer)
    {
        if (SelectionBox() is not { } box)
        {
            return;
        }

        var start = new Dictionary<string, (CanvasNode Node, double X, double Y, double W, double H)>(StringComparer.Ordinal);

        foreach (var id in _selection)
        {
            if (NodeOf(id) is { } node)
            {
                start[id] = (node, node.X, node.Y, node.Width, node.Height);
            }
        }

        if (start.Count == 0)
        {
            return;
        }

        _resizeGesture = new ResizeGesture(direction, box, start, _view.ToWorldX(screenPointer.X), _view.ToWorldY(screenPointer.Y));

        BeginGesture(start.Count > 1 ? $"缩放 {start.Count} 格" : "缩放一格");
    }

    private void ApplyResize(Point screenPointer, ResizeGesture gesture)
    {
        var worldX = _view.ToWorldX(screenPointer.X);
        var worldY = _view.ToWorldY(screenPointer.Y);

        var box = gesture.StartBox;
        var left = box.X;
        var top = box.Y;
        var right = box.X + box.Width;
        var bottom = box.Y + box.Height;

        if (gesture.Direction.Contains('w'))
        {
            left = Math.Min(worldX, right - MinNodeSize);
        }

        if (gesture.Direction.Contains('e'))
        {
            right = Math.Max(worldX, left + MinNodeSize);
        }

        if (gesture.Direction.Contains('n'))
        {
            top = Math.Min(worldY, bottom - MinNodeSize);
        }

        if (gesture.Direction.Contains('s'))
        {
            bottom = Math.Max(worldY, top + MinNodeSize);
        }

        var scaleX = box.Width <= 0 ? 1 : (right - left) / box.Width;
        var scaleY = box.Height <= 0 ? 1 : (bottom - top) / box.Height;

        // 图片格子的框必须跟着图的形状走。框的长宽比一旦和图不一致，contain 就在框里留出
        // 一圈"白边"（其实是没画到的板底色）——他报的"图变小了边上却有白框"就是这个。
        // 文字格不锁：它的框就是要独立于内容。
        var single = gesture.Start.Values.Count == 1 ? gesture.Start.Values.First() : default;
        var lockAspect = single.Node?.Kind == CanvasNodeKind.File;

        foreach (var (node, x, y, width, height) in gesture.Start.Values)
        {
            var scaledWidth = Math.Max(MinNodeSize, width * scaleX);
            var scaledHeight = Math.Max(MinNodeSize, height * scaleY);

            var anchorY = y;

            if (lockAspect && width > 0)
            {
                var locked = scaledWidth * (height / width);

                if (gesture.Direction.Contains('n'))
                {
                    anchorY -= locked - scaledHeight;
                }

                scaledHeight = locked;
            }

            node.X = left + ((x - box.X) * scaleX);
            node.Y = top + ((anchorY - box.Y) * scaleY);
            node.Width = scaledWidth;
            node.Height = scaledHeight;
        }

        // 帧预算：只重量被拖的那几格。ArrangeAll 会重排整块板子（日志里那行 16–60ms 就是它），
        // 拖一个角不需要。松手时 OnSurfaceReleased 补一次完整排布。
        var raster = BoardArea.XamlRoot?.RasterizationScale ?? 1;

        foreach (var (node, _, _, _, _) in gesture.Start.Values)
        {
            if (_realized.TryGetValue(node.Id, out var visual))
            {
                visual.Update(node, _view, raster);
            }
        }

        PaintLinks();
        ProjectSelection();
    }

    private Rect? SelectionBox()
    {
        double left = double.MaxValue;
        double top = double.MaxValue;
        double right = double.MinValue;
        double bottom = double.MinValue;
        var any = false;

        foreach (var id in _selection)
        {
            if (NodeOf(id) is not { } node)
            {
                continue;
            }

            any = true;
            left = Math.Min(left, node.X);
            top = Math.Min(top, node.Y);
            right = Math.Max(right, node.X + node.Width);
            bottom = Math.Max(bottom, node.Y + node.Height);
        }

        return any ? new Rect(left, top, right - left, bottom - top) : null;
    }

    /// <summary>
    /// Draws the selection and its handles in screen space. Handles stay eight pixels
    /// whatever the zoom, because a grab target that shrinks with the content is not a
    /// grab target.
    /// </summary>
    private void ProjectSelection()
    {
        // 删除那一发会把描边与内对比线一起淡到 0（见 SelectionChromeVisuals）：这两枚是常驻元素、
        // 不是每次新建的，所以每一次投影都要先抬回满不透明——不然"套子"再也回不来。
        SelectionOutline.Opacity = 1;
        SelectionInner.Opacity = 1;

        foreach (var handle in _handles)
        {
            HandleLayer.Children.Remove(handle);
        }

        _handles.Clear();

        // 这一拍在拖动中也会被叫到（OnSurfaceMoved 结算选中框时）：操作条与提议胶囊若在场就跟着
        // 那一格走——不然它们停在拖中途经的旧位置，画面上的它就指错了格。
        EnsureRail();
        PlaceGroupHint();

        if (SelectionBox() is not { } box || _selection.Count == 0)
        {
            SelectionOutline.Stroke = null;
            SelectionOutline.Fill = null;
            SelectionOutline.Width = 0;
            SelectionOutline.Height = 0;
            SelectionInner.Stroke = null;
            SelectionInner.Width = 0;
            SelectionInner.Height = 0;
            return;
        }

        var screenLeft = _view.ToScreenX(box.X);
        var screenTop = _view.ToScreenY(box.Y);
        var screenWidth = box.Width * _view.Zoom;
        var screenHeight = box.Height * _view.Zoom;

        SelectionOutline.Stroke = ChromeStyling.Ring;
        // 选中只描边，不往格子里灌一层蓝——那层蓝会把便签本色染偏，看不清它到底是哪个预设色
        // （10-22 他指着绿格发蓝那张说的）。内部那一圈 InnerContrast 是描边、不是填充，留着。
        SelectionOutline.Fill = null;
        Canvas.SetLeft(SelectionOutline, screenLeft);
        Canvas.SetTop(SelectionOutline, screenTop);
        SelectionOutline.Width = screenWidth;
        SelectionOutline.Height = screenHeight;

        // The prototype gets its inner line from `inset 0 0 0 1px`; the closest WinUI shape is a
        // second stroke one pixel inside, which is also why it must not be hit-testable.
        SelectionInner.Stroke = ChromeStyling.InnerContrast;
        Canvas.SetLeft(SelectionInner, screenLeft + SelectionChrome.RingThicknessPx);
        Canvas.SetTop(SelectionInner, screenTop + SelectionChrome.RingThicknessPx);
        SelectionInner.Width = Math.Max(0, screenWidth - (2 * SelectionChrome.RingThicknessPx));
        SelectionInner.Height = Math.Max(0, screenHeight - (2 * SelectionChrome.RingThicknessPx));

        var anchors = new (double X, double Y, string Direction)[]
        {
            (screenLeft, screenTop, "nw"),
            (screenLeft + (screenWidth / 2), screenTop, "n"),
            (screenLeft + screenWidth, screenTop, "ne"),
            (screenLeft + screenWidth, screenTop + (screenHeight / 2), "e"),
            (screenLeft + screenWidth, screenTop + screenHeight, "se"),
            (screenLeft + (screenWidth / 2), screenTop + screenHeight, "s"),
            (screenLeft, screenTop + screenHeight, "sw"),
            (screenLeft, screenTop + (screenHeight / 2), "w"),
        };

        foreach (var (x, y, direction) in anchors)
        {
            var handle = MakeHandle(direction);

            Canvas.SetLeft(handle, x - (SelectionChrome.HandleHitPx / 2));
            Canvas.SetTop(handle, y - (SelectionChrome.HandleHitPx / 2));

            HandleLayer.Children.Add(handle);
            _handles.Add(handle);
        }
    }

    /// <summary>
    /// One resize grip: the 8px square the user sees, inside a 24px square the pointer can find.
    /// </summary>
    /// <remarks>
    /// The two sizes are deliberately different — 24px is WCAG 2.5.8's floor. Growing the drawn
    /// square is what the prototype's <c>.handle:hover i</c> does; it happens on the inner border so
    /// the hit target never moves under the cursor.
    /// </remarks>
    private FrameworkElement MakeHandle(string direction)
    {
        var seen = SelectionChrome.HandleSeenPx;
        var hover = SelectionChrome.HandleHoverPx;

        // 看得见的那一格不许吃掉命中：ring 与 face 都是 IsHitTestVisible=false，只有下面那枚
        // 24×24 的透明 target 收事件。上一版 face 压在 target 之上，于是"看得见的那 8 像素"
        // 正好是唯一点不到的地方——他报的"左键右键都改不了尺寸"就是这一处。
        var ring = new Border
        {
            Width = seen + (2 * SelectionChrome.RingThicknessPx),
            Height = seen + (2 * SelectionChrome.RingThicknessPx),
            BorderBrush = ChromeStyling.HandleEdge,
            BorderThickness = new Thickness(SelectionChrome.RingThicknessPx),
            IsHitTestVisible = false,
        };

        var face = new Border
        {
            Width = seen,
            Height = seen,
            Background = ChromeStyling.HandleFace,
            BorderBrush = ChromeStyling.Ring,
            BorderThickness = new Thickness(SelectionChrome.RingThicknessPx),
            IsHitTestVisible = false,
        };

        var target = new Rectangle
        {
            Width = SelectionChrome.HandleHitPx,
            Height = SelectionChrome.HandleHitPx,
            Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = direction,
        };

        var grip = new Canvas { Width = SelectionChrome.HandleHitPx, Height = SelectionChrome.HandleHitPx };
        grip.Children.Add(target);
        grip.Children.Add(ring);
        grip.Children.Add(face);

        void Centre(FrameworkElement element, double size)
        {
            Canvas.SetLeft(element, (SelectionChrome.HandleHitPx - size) / 2);
            Canvas.SetTop(element, (SelectionChrome.HandleHitPx - size) / 2);
        }

        Centre(ring, seen + (2 * SelectionChrome.RingThicknessPx));
        Centre(face, seen);

        target.PointerPressed += OnHandlePressed;

        target.PointerEntered += (_, _) => Grow(hover);
        target.PointerExited += (_, _) => Grow(seen);

        void Grow(double size)
        {
            face.Width = size;
            face.Height = size;
            ring.Width = size + (2 * SelectionChrome.RingThicknessPx);
            ring.Height = size + (2 * SelectionChrome.RingThicknessPx);

            Centre(ring, size + (2 * SelectionChrome.RingThicknessPx));
            Centre(face, size);
        }

        return grip;
    }

    /// <summary>
    /// Which edge a press lands on, or null when it is a plain grab.
    /// </summary>
    /// <remarks>
    /// Measured in screen pixels, not world ones: the band is a target for the pointer, and a
    /// target that shrinks as the user zooms out is the opposite of what a hit area is for.
    /// </remarks>
    private string? EdgeOf(CanvasNode node, Point screen)
    {
        var candidates = new (string Edge, double Away)[]
        {
            ("w", Math.Abs(screen.X - _view.ToScreenX(node.X))),
            ("e", Math.Abs(screen.X - _view.ToScreenX(node.Right))),
            ("n", Math.Abs(screen.Y - _view.ToScreenY(node.Y))),
            ("s", Math.Abs(screen.Y - _view.ToScreenY(node.Bottom))),
        };

        var nearest = candidates.MinBy(candidate => candidate.Away);

        return nearest.Away <= SelectionChrome.EdgeBandPx ? nearest.Edge : null;
    }

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Rectangle { Tag: string direction } handle)
        {
            return;
        }

        if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginResize(direction, e.GetCurrentPoint(BoardArea).Position);

        BoardArea.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // External edits: an agent or the CLI rewriting the file under us
    // -------------------------------------------------------------------------

    private void StartWatching()
    {
        if (_boardPath is null)
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = new FileWatcher(_boardPath);

        // The watcher fires on a thread-pool thread; everything touching the visual tree
        // has to come back to the dispatcher.
        _watcher.Changed += () => DispatcherQueue.TryEnqueue(ApplyExternalChange);

        Diagnostics.Write($"watching {_boardPath} for outside writes");
    }

    /// <summary>
    /// Reads what someone else wrote and applies only their changes onto this document.
    /// </summary>
    /// <remarks>
    /// The merge is between "what this process last saw on disk" and "what is on disk now"
    /// — the same patch shape a save uses, replayed the other way. Replacing the document
    /// instead would drop whatever the human did in the last second, and a full write back
    /// would drop whatever the agent did. Neither is acceptable when both are editing at
    /// the same time, which is the entire point of this board.
    /// </remarks>
    private void ApplyExternalChange()
    {
        if (_boardPath is null)
        {
            return;
        }

        if (_nodeGesture is not null || _resizeGesture is not null || _editingId is not null)
        {
            _reloadPending = true;
            Diagnostics.Write("external change held back: a gesture or the editor is active");
            return;
        }

        string text;

        try
        {
            text = CanvasStore.ReadAllText(_boardPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"板面文件此刻读不到：{ex.Message}");
            return;
        }

        var hash = CanvasStore.Sha256Of(System.Text.Encoding.UTF8.GetBytes(text));

        if (_lastWrittenHash is not null && hash.AsSpan().SequenceEqual(_lastWrittenHash))
        {
            return;
        }

        CanvasDocument disk;

        try
        {
            disk = CanvasDocument.Parse(text, _boardPath);
        }
        catch (Exception ex) when (ex is CanvasDocumentException or JsonException)
        {
            // The board keeps showing what it has, and refuses to write over a file nobody
            // can read: silently continuing would next save and destroy that content.
            Say($"外面改成了读不懂的样子，本次不并入，也不覆盖：{ex.Message}");
            Diagnostics.Write($"external parse refused: {ex.Message}");
            return;
        }

        var before = _document.ToJson();

        try
        {
            var fromOutside = DocDiff.Between(CanvasDocument.Parse(_baseJson, _boardPath), disk);

            if (!fromOutside.IsEmpty)
            {
                var reports = PatchApplier.Apply(_document, fromOutside);
                var rejected = reports.Count(report => report.Outcome is PatchOutcome.MissingNode or PatchOutcome.AlreadyPresent);

                Diagnostics.Write($"merged {fromOutside.NodeCount} outside change(s), {rejected} reported");
            }

            _baseJson = text;
            _history.Record($"并入外部改动（{fromOutside.NodeCount} 处）", before, _document.ToJson());

            Rebuild();
            SaveSoon();

            Say($"并入了外面的改动（{fromOutside.NodeCount} 处） · {_document.Nodes.Count} 个节点 · 可 Ctrl＋Z 回退");

            // 外来改动落盘后 _baseJson 已经跟上，随后那次 SaveSoon 会因为"没变化"而直接返回，
            // 所以 agent 写的那一块只能靠这一发去推；少了它，它要等到下一次轮询才上得了线。
            _sync?.NotifyChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say($"并入失败：{ex.Message}");
            Diagnostics.Write($"merge failed: {ex}");
        }
    }

    private void DrainPendingReload()
    {
        if (!_reloadPending)
        {
            return;
        }

        _reloadPending = false;
        ApplyExternalChange();
    }

    // -------------------------------------------------------------------------
    // Paste, drop, create, in-place edit
    // -------------------------------------------------------------------------

    /// <summary>Where a new node goes: under the pointer, or the middle of the view if it never moved.</summary>
    private Point PasteWorld()
    {
        if (_lastPointer.X == 0 && _lastPointer.Y == 0)
        {
            return new Point(
                _view.ToWorldX(BoardArea.ActualWidth / 2),
                _view.ToWorldY(BoardArea.ActualHeight / 2));
        }

        return new Point(_view.ToWorldX(_lastPointer.X), _view.ToWorldY(_lastPointer.Y));
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_boardPath is null)
        {
            Say("没有可写的板面文件，粘不了");
            return;
        }

        // 先问一句"这是不是一块板子上的那几格"：是就整份搬过来（颜色、大小、图、线头、组都在），
        // 不是才去读文字／图片／文件那三条老路。顺序反了会把一次 Ctrl+C 降级成一格纯文字。
        if (await TryPasteFragmentAsync())
        {
            return;
        }

        var result = await ClipboardIngest.FromClipboardAsync();

        Diagnostics.Write($"paste: {result.Kind} — {result.Description}" +
                          (result.Failed ? $" (failure: {result.Failure})" : string.Empty));

        BeginGesture("贴了一格");

        switch (result.Kind)
        {
            case IngestKind.Text when result.Text is { Length: > 0 } text:
            {
                var node = CreateTextNote(text, PasteWorld());
                Say($"贴了一格文字 · {result.Description}");
                SelectOnly(node.Id);
                break;
            }

            case IngestKind.Image when result.ImageBytes is { Length: > 0 } bytes:
            {
                var node = CreateImageNote(bytes, ".png", result.Width, result.Height, PasteWorld());
                Say($"贴了图 · {result.Description}");
                SelectOnly(node.Id);
                break;
            }

            case IngestKind.Files when result.Files is { Count: > 0 } files:
            {
                await PlaceFilesAsync(files, PasteWorld());
                break;
            }

            default:
                // A paste that put nothing on the board says so; a silent no-op is the
                // shape that makes people click it five times.
                Say(result.Description);
                break;
        }

        EndGesture();
    }

    private async Task PlaceFilesAsync(IReadOnlyList<string> files, Point worldAt)
    {
        BeginGesture($"放下 {files.Count} 个文件");

        var placed = 0;
        var links = 0;

        for (var index = 0; index < files.Count; index++)
        {
            var path = files[index];
            var spot = new Point(worldAt.X + (index * 24), worldAt.Y + (index * 24));

            if (ClipboardIngest.IsImage(path))
            {
                try
                {
                    var read = await ClipboardIngest.FromImageFileAsync(path);

                    if (read.ImageBytes is { Length: > 0 } bytes)
                    {
                        CreateImageNote(bytes, System.IO.Path.GetExtension(path), read.Width, read.Height, spot);
                        placed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Write($"dropped file unreadable {path}: {ex.Message}");
                }
            }
            else
            {
                // The format's file node is for something inside the board folder. A file
                // that stayed where it was becomes a link node, which is what the spec means.
                var node = _document.Add(CanvasNodeKind.Link, spot.X, spot.Y, 260, 60);
                node.Url = new Uri(path).AbsoluteUri;
                node.Label = System.IO.Path.GetFileName(path);
                links++;
            }
        }

        EndGesture();
        Rebuild();

        Say(placed + links == 0
            ? "拖进来的东西一个都没放下（详情在日志）"
            : $"放了 {placed} 张图、{links} 个链接");
    }

    private CanvasNode CreateTextNote(string text, Point worldAt)
    {
        const double width = 260;

        var node = _document.Add(CanvasNodeKind.Text, worldAt.X - (width / 2), worldAt.Y - 20, width, 60);

        // 在「所有便签」里新建＝落进未归档文件：给它打上未归档的命名空间，拆分回写时才归得对。
        if (IsAllNotes(_boardPath))
        {
            node.Id = BoardMerge.MergeId(UnfiledPath, node.Id);
        }

        node.Text = text;
        FitNoteHeight(node);

        Rebuild();

        return node;
    }

    private CanvasNode CreateImageNote(byte[] bytes, string extension, int naturalWidth, int naturalHeight, Point worldAt)
    {
        // 并集里贴图也归未归档：资源写进未归档那块板旁边的 assets，拆分后路径仍解得开。
        var boardForAsset = IsAllNotes(_boardPath) ? UnfiledPath : _boardPath!;
        var relative = AssetStore.WriteImage(boardForAsset, bytes, extension, out var reused);

        Diagnostics.Write(reused ? $"image already on disk: {relative}" : $"wrote {relative} ({bytes.Length} bytes)");

        // 一张原生尺寸的截图会盖满整块板子，所以新贴进来的图先按**长边**收：
        // 360 世界像素＝板子上文字格（240–270）的同一量级，贴进来不会一上来就压过全板。
        // 这个数是我方判断值（原型没有公布过粘贴上限），和堆叠/均分用的 24 一样记在契约表里。
        const double maxWorldLongSide = 360;

        var naturalLongest = Math.Max(naturalWidth, naturalHeight);
        var scale = naturalLongest > 0 ? Math.Min(1, maxWorldLongSide / naturalLongest) : 1;

        var width = naturalWidth > 0 ? naturalWidth * scale : 360;
        var height = naturalWidth > 0 && naturalHeight > 0 ? naturalHeight * scale : width * 0.6;

        var node = _document.Add(CanvasNodeKind.File, worldAt.X - (width / 2), worldAt.Y - (height / 2), width, height);

        if (IsAllNotes(_boardPath))
        {
            node.Id = BoardMerge.MergeId(UnfiledPath, node.Id);
        }

        node.FilePath = relative;

        Rebuild();

        return node;
    }

    /// <summary>
    /// Asks a text block what height this text needs at this width. The note's stored
    /// height is then the content's height, so a re-wrap never hides lines.
    /// </summary>
    /// <remarks>
    /// It measures the <b>body</b> — the text as the card paints it, with the property lines taken
    /// off — and then adds the band the readout row needs. Measuring the raw text instead would make
    /// a note grow for a line that is not drawn, and measuring only the body would cut the readout
    /// that the same card promises to show.
    /// </remarks>
    private double MeasureTextHeight(string text, double widthWorld, double fontSizeWorld, bool paintsChips = true)
    {
        var type = NoteTypography.For(fontSizeWorld);
        var marks = Tags.Read(text);

        /* 牌子那一排占不占高度，问的是"卡片会不会画它"，不是"有没有日期"。
           以前这里读的是 Readout()（只有日期才非空），于是只写 @t 的那格被算矮了一排：
           框比内容短 22 世界像素，最后一行被横切——他 10-12 报的"敲回车有时便签变小"就是这一格。
           分组不画牌子（NodeVisual 那一支传的是空表），所以也不给它留位。 */
        var band = paintsChips && NoteMarks.Chips(marks, _view.Today).Count > 0 ? NoteType.ChipBand(1) : 0;

        if (_measureProbe is null)
        {
            _measureProbe = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsHitTestVisible = false,
                FontFamily = NoteStyling.NoteFont,
                // A detached element needs the same root as the board, or WinUI measures
                // it at zero and every note comes out the default height.
                XamlRoot = BoardArea.XamlRoot,
            };
        }

        // Measured with the same leading and padding the card is drawn with, or the frame and
        // the text disagree and the last line is the one that gets clipped.
        _measureProbe.FontSize = type.Size;
        _measureProbe.LineHeight = type.Leading;
        _measureProbe.Text = marks.Body;

        double measured;

        if (Markdown.LooksLikeMarkdown(marks.Body))
        {
            /* A heading is taller than the body it sits between, so one TextBlock cannot hold the
               block and the fit-measure has to compose the same block the card paints. Same call,
               same elements — the one place that could otherwise let a heading push the last line
               out of the frame. */
            _measureMd ??= new StackPanel
            {
                IsHitTestVisible = false,
                XamlRoot = BoardArea.XamlRoot,
            };

            // The card's own width, less its inset, on every call: the block must wrap where it will
            // wrap, and a note two widths down the board is a different answer.
            _measureMd.Width = Math.Max(40, widthWorld - (type.Padding * 2));
            _measureMd.Children.Clear();

            foreach (var line in Markdown.Lines(marks.Body))
            {
                _measureMd.Children.Add(MarkdownPaint.Line(
                    type,
                    1,
                    line,
                    _measureInk,
                    _measureInk,
                    _measureInk));
            }

            _measureMd.Measure(new Size(Math.Max(40, widthWorld - (type.Padding * 2)), double.PositiveInfinity));
            measured = _measureMd.DesiredSize.Height;
        }
        else
        {
            _measureProbe.Measure(new Size(Math.Max(40, widthWorld - (type.Padding * 2)), double.PositiveInfinity));
            measured = _measureProbe.DesiredSize.Height;
        }

        return Math.Max(type.MinimumHeight + band, Math.Ceiling(measured) + (type.Padding * 2) + band);
    }

    /// <summary>
    /// 随文字 / 固定 — the note-size switch. Fixed means editing changes the text only, and the
    /// frame is sized by the handles alone.
    /// </summary>
    private void FitNoteHeight(CanvasNode node)
    {
        if (_view.NoteSize == NoteSizeMode.Fixed || _view.IsManualSize(node.Id))
        {
            return;
        }

        node.Height = MeasureTextHeight(
            node.Text ?? string.Empty,
            node.Width,
            _view.FontSizeFor(node.Id),
            paintsChips: node.Kind != CanvasNodeKind.Group);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var holdsFiles = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);

        e.AcceptedOperation = holdsFiles ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

        e.DragUIOverride.Caption = holdsFiles ? "放到板上" : "放不下";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        // WinUI's deferral is not disposable; it has to be completed by hand, including
        // on the paths that throw, or the drag source waits forever.
        var deferral = e.GetDeferral();

        try
        {
            _lastPointer = e.GetPosition(BoardArea);

            var paths = new List<string>();

            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                paths = items.Select(item => item.Path).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Diagnostics.Write($"drop unreadable: {ex.Message}");
            }

            if (paths.Count > 0)
            {
                await PlaceFilesAsync(paths, PasteWorld());
            }
            else
            {
                Say("拖进来的不是文件");
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>双击那一发落到哪儿：格子的边缘带＝拉线，格内＝编辑，空白＝新建。</summary>
    private (string Route, CanvasNode? Node, string? Side) DoubleTapTarget(Point at)
    {
        if (NodeAt(at) is not { } under)
        {
            return ("新建", null, null);
        }

        return EdgeOf(under, at) is { } side ? ("拉线", under, side) : ("编辑", under, null);
    }

    private void OnSurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var at = e.GetPosition(BoardArea);

        /* 三条路都在板子这一侧判，不在卡片自己的 DoubleTapped 里判：按下时板子把指针捕获走了
           （快速拖动不能掉捕获），于是手势事件发到 BoardArea，卡片那个 handler 根本收不到——
           结果双击格内也走到"新建一格"，他 10-12 报的就是这个。判据抽在 DoubleTapTarget 里，
           自检那一发直接问它，不靠把这发手势演出来。 */
        var (route, hit, side) = DoubleTapTarget(at);

        if (route != "新建")
        {
            SelectOnly(hit!.Id);

            if (route == "拉线")
            {
                StartPull(hit.Id, side!);
            }
            else
            {
                BeginEdit(hit.Id);
            }

            e.Handled = true;

            return;
        }

        BeginGesture("新建一格");

        var node = CreateTextNote(string.Empty, FreeOfGroups(e.GetPosition(BoardArea) is { } screen
            ? new Point(_view.ToWorldX(screen.X), _view.ToWorldY(screen.Y))
            : PasteWorld()));

        EndGesture();

        _provisionalId = node.Id;

        BeginEdit(node.Id);
        e.Handled = true;
    }

    private void BeginEdit(string id)
    {
        CloseEditor(commit: true);

        if (NodeOf(id) is not { } node)
        {
            return;
        }

        if (node.Kind == CanvasNodeKind.Group)
        {
            // 组没有正文，可改的是它的描述（label）——双击名牌就地改名。
            BeginRenameGroup(node);
            return;
        }

        if (node.Kind != CanvasNodeKind.Text)
        {
            Say($"这一格是 {CanvasNode.NameOfKind(node.Kind)}，不是文字，改不了");
            return;
        }

        _editingId = id;

        var surface = NoteStyling.SurfaceFor(node.Color);

        var editor = new TextBox
        {
            // 先声明成多行、再喂文字：控件在赋值那一刻就得知道自己是能换行的。
            // （傍晚那次"开一次编辑器就少一段字"的机制**没有**在这里复现——负对照里把
            // AcceptsReturn 关掉，赋多行文字仍是一个字不少。所以这一行只是把顺序摆对，
            // 真正的守卫是自检里那发"开编辑器、不打字、提交，文字必须原样"。）
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = NoteStyling.NoteFont,
            FontSize = _view.FontSizeFor(id) * _view.Zoom,
            Foreground = new SolidColorBrush(NoteStyling.InkFor(node.Color, surface)),
            Background = new SolidColorBrush(surface),
            BorderBrush = new SolidColorBrush(NoteStyling.LineFor(node.Color, surface)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(NoteTypography.For(_view.FontSizeFor(id)).Padding * _view.Zoom),
            IsSpellCheckEnabled = false,
            UseLayoutRounding = false,
        };

        // 文本栏里看到的是正文；标记那几行由 _editorProps 保管，提交时原样接回去。
        var seededMarks = Tags.Read(node.Text);

        _editorProps = seededMarks.Props;

        editor.Text = seededMarks.Body;

        editor.KeyDown += OnEditorKeyDown;
        editor.LostFocus += (_, _) => CloseEditor(commit: true);

        // 编辑中也要能右键改颜色、删格、成组。走的是"点到别处"那同一条路：先提交这一格，
        // 再开菜单——不提交就开菜单的话，菜单里的动作会把没落盘的文字连带丢掉。
        editor.PointerPressed += (_, args) =>
        {
            var at = args.GetCurrentPoint(BoardArea);

            if (!at.Properties.IsRightButtonPressed)
            {
                return;
            }

            args.Handled = true;

            CloseEditor(commit: true);
            ShowBoardMenu(at.Position);
        };

        _editor = editor;
        _editorGrownWorld = 0;

        // 回车换行时编辑框立刻长到装得下，不等到提交才补高度；手动拖过大小的格子不参与。
        editor.TextChanged += (_, _) => GrowEditorToFit();

        // 编辑器里也挂着同一排牌子：按下回车的那一瞬间它就该从文本栏消失、出现在左下角，
        // 而不是等这次编辑提交完才变。
        _editorChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };

        EditorLayer.Children.Add(editor);
        EditorLayer.Children.Add(_editorChips);

        PlaceEditor();
        PaintEditorChips();

        editor.Focus(FocusState.Programmatic);
        editor.Select(editor.Text.Length, 0);

        Say("编辑中：Ctrl＋Enter 或点到别处提交，Esc 放弃");
    }

    private void PlaceEditor()
    {
        if (_editor is null || _editingId is null || NodeOf(_editingId) is not { } node)
        {
            return;
        }

        Canvas.SetLeft(_editor, _view.ToScreenX(node.X));
        Canvas.SetTop(_editor, _view.ToScreenY(node.Y));
        _editor.Width = node.Width * _view.Zoom;
        _editor.Height = Math.Max(node.Height, _editorGrownWorld) * _view.Zoom;
        var editorType = NoteTypography.For(_view.FontSizeFor(node.Id));
        // TextBox carries no LineHeight (it is a TextBlock property), so the editor wraps at the
        // platform default while the card itself uses the frozen leading. Recorded as a gap, not fixed here.
        _editor.FontSize = Math.Max(4, editorType.Size * _view.Zoom);
        _editor.Padding = new Thickness(Math.Clamp(editorType.Padding * _view.Zoom, 0, 60));
    }

    /// <summary>
    /// 打字/回车时把编辑框长到能装下正文。只改屏幕上的编辑框，不动文档；提交时
    /// <see cref="FitNoteHeight"/> 再按同一把尺落定。用户手动定过大小的格子保持原框。
    /// </summary>
    private void GrowEditorToFit()
    {
        if (_editor is null || _editingId is null || NodeOf(_editingId) is not { } node)
        {
            return;
        }

        if (_view.NoteSize == NoteSizeMode.Fixed || _view.IsManualSize(node.Id))
        {
            return;
        }

        var text = NoteProperties.WithProps(_editorProps, JsonText.NormalizeTextNewlines(_editor.Text));
        var fontSize = _view.FontSizeFor(node.Id);
        var type = NoteTypography.For(fontSize);

        // 多留一行给 TextBox 的平台行高：卡片按冻结的 leading 量，编辑框并不吃这个属性。
        var needed = Math.Ceiling(MeasureTextHeight(text, node.Width, fontSize, paintsChips: true) + type.Leading);

        if (needed <= _editorGrownWorld + 0.5)
        {
            return;
        }

        Diagnostics.Write(
            $"editor grew {_editorGrownWorld:0.#}→{needed:0.#} world px for {node.Id} " +
            $"({_editor.Text.Length} chars, node {node.Height:0.#})");

        _editorGrownWorld = needed;
        _editor.Height = Math.Max(node.Height, _editorGrownWorld) * _view.Zoom;
        PaintEditorChips();
    }

    /// <summary>
    /// 编辑框自己的按键。这里只剩 Esc——**回车不能指望这一层**：10-24 查"Ctrl+Enter 不收笔"量的，
    /// 同一发回车在 <c>Root.PreviewKeyDown</c> 里看得见、在这里一次都没到过（换行却照样插进去），
    /// 所以"Ctrl+Enter 收笔"与"回车收下标记行"都挪到了 <c>OnCheatPreviewKeyDown</c>（Cheatsheet.cs）。
    /// </summary>
    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseEditor(commit: false);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Closes the editor. The fields are cleared before the control is removed, because
    /// removing it fires LostFocus and that would otherwise commit a second time — or
    /// commit after an Escape that said it would not.
    /// </summary>
    private void CloseEditor(bool commit)
    {
        var editor = _editor;
        var id = _editingId;

        _editor = null;
        _editingId = null;

        if (editor is null)
        {
            return;
        }

        editor.KeyDown -= OnEditorKeyDown;
        EditorLayer.Children.Remove(editor);

        if (_editorChips is not null)
        {
            EditorLayer.Children.Remove(_editorChips);
            _editorChips = null;
        }

        // A note made by a double-tap is provisional until it carries text: the card the user
        // never wrote in must not survive as an empty rectangle, and must not leave a step in the
        // undo stack either (invariant 1 of docs/note-lifecycle.md — "空" was a state a card could
        // be stranded in, and it was landing on disk).
        var provisional = _provisionalId is not null && id is not null && _provisionalId == id;

        _provisionalId = null;

        if (!commit || id is null)
        {
            if (provisional)
            {
                Undo();

                Say("取消了新建：那张空卡没有留下");
                return;
            }

            Say("已放弃这次编辑");
            return;
        }

        var node = NodeOf(id);

        if (node is null || node.Kind != CanvasNodeKind.Text)
        {
            return;
        }

        var text = NoteProperties.WithProps(_editorProps, JsonText.NormalizeTextNewlines(editor.Text));

        _editorProps = string.Empty;

        if (provisional && text.Trim().Length == 0)
        {
            Undo();

            // 这里原来会飘出一句"没写字就不留格：新建已取消"。取消本身就是看得见的结果
            // ——格子没了、板上少了一格——再解释一句是替用户复述他刚做完的事。
            return;
        }

        if (string.Equals(text, node.Text, StringComparison.Ordinal))
        {
            return;
        }

        BeginGesture("改了一格文字");

        node.Text = text;
        FitNoteHeight(node);

        EndGesture();
        Rebuild();
        SaveSoon();

        DrainPendingReload();
    }

    // ---------------------------------------------------------------------
    // 组改名：双击名牌就地给这一组起个名（组没有正文，可改的是 label）
    // ---------------------------------------------------------------------

    private void BeginRenameGroup(CanvasNode group)
    {
        CloseRename();

        if (group.Kind != CanvasNodeKind.Group)
        {
            return;
        }

        _groupRenameId = group.Id;

        var paper = NoteStyling.SurfaceFor(null);

        var box = new TextBox
        {
            Text = group.Label ?? string.Empty,
            AcceptsReturn = false,
            FontFamily = NoteStyling.NoteFont,
            FontSize = Math.Max(11, 14 * _view.Zoom),
            Foreground = new SolidColorBrush(NoteStyling.InkFor(paper)),
            Background = new SolidColorBrush(paper),
            BorderBrush = ChromeStyling.Ring,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2, 5, 2),
            PlaceholderText = "组名",
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CommitRename();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseRename();
                Say("没改名");
                e.Handled = true;
            }
        };

        box.LostFocus += (_, _) => CommitRename();

        _groupRenameBox = box;

        EditorLayer.Children.Add(box);
        PlaceRename(group);

        box.Focus(FocusState.Programmatic);
        box.Select(box.Text.Length, 0);

        Say("给这一组起个名：回车或点到别处定名，Esc 不改");
    }

    private void PlaceRename(CanvasNode group)
    {
        if (_groupRenameBox is null)
        {
            return;
        }

        // 贴在名牌那一行（框顶上方），宽度跟着框走但不超过一屏。
        Canvas.SetLeft(_groupRenameBox, _view.ToScreenX(group.X));
        Canvas.SetTop(_groupRenameBox, _view.ToScreenY(group.Y) - 32 * _view.Zoom);
        _groupRenameBox.Width = Math.Clamp(group.Width * _view.Zoom, 120, 360);
    }

    private void CommitRename()
    {
        if (_groupRenameBox is null || _groupRenameId is not { } id)
        {
            return;
        }

        var group = NodeOf(id);
        var text = _groupRenameBox.Text.Trim();

        CloseRename();

        if (group is null || string.Equals(group.Label ?? string.Empty, text, StringComparison.Ordinal))
        {
            return;
        }

        BeginGesture(text.Length == 0 ? "抹掉了组名" : "给组起了名");

        group.Label = text.Length == 0 ? null : text;

        EndGesture();
        Rebuild();
        SaveSoon();
    }

    private void CloseRename()
    {
        if (_groupRenameBox is not null)
        {
            EditorLayer.Children.Remove(_groupRenameBox);
            _groupRenameBox = null;
        }

        _groupRenameId = null;
    }

    private void SelectOnly(string id)
    {
        ClearSelection();
        _selection.Add(id);
        ProjectSelection();
    }

    // -------------------------------------------------------------------------
    // Snapping, guides, history, menu
    // -------------------------------------------------------------------------

    /// <summary>Eight screen pixels, converted to the world units the drag works in.</summary>
    private double SnapThreshold => 8 / Math.Max(0.01, _view.Zoom);

    private (double DeltaX, double DeltaY, IReadOnlyList<Guide> Guides) SnapDrag(
        NodeGesture gesture, double deltaX, double deltaY)
    {
        if (gesture.StartGeometry.TryGetValue(gesture.PrimaryId, out var primaryStart) && NodeOf(gesture.PrimaryId) is { } primary)
        {
            var moving = new Box(primaryStart.X + deltaX, primaryStart.Y + deltaY, primary.Width, primary.Height);

            var selected = gesture.StartGeometry.Keys;
            var others = new List<Box>();

            foreach (var node in _document.Nodes)
            {
                if (selected.Contains(node.Id))
                {
                    continue;
                }

                // Only what is on screen can be aligned to, which also keeps a huge board
                // from testing thousands of edges per pointer message.
                if (_realized.TryGetValue(node.Id, out var visual) && visual.IsHitTestVisible)
                {
                    others.Add(new Box(node.X, node.Y, node.Width, node.Height));
                }
            }

            var viewport = _view.WorldViewport(BoardArea.ActualWidth, BoardArea.ActualHeight);

            var result = Snapping.Snap(moving, others, new Box(viewport.X, viewport.Y, viewport.Width, viewport.Height), SnapThreshold);

            return (result.X - moving.X + deltaX, result.Y - moving.Y + deltaY, result.Guides);
        }

        return (deltaX, deltaY, []);
    }

    private void DrawGuides(IReadOnlyList<Guide> guides)
    {
        GuideLayer.Children.Clear();

        foreach (var guide in guides)
        {
            var line = new Rectangle
            {
                Stroke = ChromeStyling.Snap,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };

            if (guide.Vertical)
            {
                var screenX = _view.ToScreenX(guide.Position);

                Canvas.SetLeft(line, screenX);
                Canvas.SetTop(line, 0);
                line.Height = BoardArea.ActualHeight;
            }
            else
            {
                var screenY = _view.ToScreenY(guide.Position);

                Canvas.SetLeft(line, 0);
                Canvas.SetTop(line, screenY);
                line.Width = BoardArea.ActualWidth;
            }

            GuideLayer.Children.Add(line);
        }
    }

    private void BeginGesture(string label)
    {
        _pendingSnapshot = _document.ToJson();
        _pendingLabel = label;

        // 框是派生的：先记下这一步开始时每个框"应该是多大"，EndGesture 才分得清
        // 是这一步把它挪了，还是本来就该重算（不该顺手写与这一步无关的框）。
        _derivedFramesAtBegin = DerivedFrames();
    }

    private void EndGesture()
    {
        if (_pendingSnapshot is null)
        {
            return;
        }

        var label = _pendingLabel ?? "改动";
        var before = _pendingSnapshot;

        _pendingSnapshot = null;
        _pendingLabel = null;

        // 贴回去再记这一步：撤销一发的结果必须是"格子和框一起回去"。
        RefitFrames();
        _derivedFramesAtBegin = null;

        _history.Record(label, before, _document.ToJson());
    }

    private void Undo()
    {
        var current = _document.ToJson();

        if (_history.TakeUndo(current) is not { } entry)
        {
            Say("没有可撤销的动作了");
            return;
        }

        ApplyHistory(entry, "撤销");
    }

    private void Redo()
    {
        var current = _document.ToJson();

        if (_history.TakeRedo(current) is not { } entry)
        {
            Say("没有可重做的动作了");
            return;
        }

        ApplyHistory(entry, "重做");
    }

    private void ApplyHistory(History.Entry entry, string verb)
    {
        try
        {
            _document = CanvasDocument.Parse(entry.Json, _boardPath);
        }
        catch (Exception ex) when (ex is CanvasDocumentException or JsonException)
        {
            // A snapshot this program wrote should always read back; if one does not,
            // saying so beats leaving the board looking unchanged with no explanation.
            Say($"{verb}失败：{ex.Message}");
            return;
        }

        Rebuild();
        SaveSoon();

        Say($"{verb}：{entry.Label} · {entry.Json.Length} 字节的状态已回退 · 剩下 {_history.UndoCount} 步");
        Diagnostics.Write($"{verb} {entry.Label}");
    }

    private void RemoveSelection()
    {
        if (_selection.Count == 0)
        {
            Say("没有选中任何格子");
            return;
        }

        var count = _selection.Count;

        BeginGesture($"删掉 {count} 格");

        foreach (var id in _selection)
        {
            _document.Remove(id);
        }

        ClearSelection();

        EndGesture();

        Rebuild();
        SaveSoon();

        Say($"删掉了 {count} 格（Ctrl＋Z 可回退）");
    }

    /// <summary>
    /// Run one arranging command over the selection — or say out loud why it cannot. The command
    /// sees <b>units</b>: one note is one unit, one group is one unit (see GroupArrange.cs).
    /// </summary>
    /// <remarks>
    /// Three exits, and none of them is silence: refused (not enough units), already-there (the
    /// command ran but nothing moved, so no transaction and no undo step), and done. A user who
    /// presses 对齐 twice must not find a no-op in their undo history.
    /// </remarks>
    private void Arrange(string label, Func<IReadOnlyList<Aligning.Placed>, Aligning.Outcome> command)
    {
        var units = SelectedUnits();

        ApplyUnitMoves(label, units, command([.. units.Select(PlacedOf)]));
    }

    private static Aligning.Placed PlacedOf(Grouping.Unit unit)
        => new(unit.Id, unit.Box.X, unit.Box.Y, unit.Box.Width, unit.Box.Height);

    /// <summary>What the right-click landed on. The menu is built from this, so an empty board does not
    /// offer "change the colour of nothing" and one card does not offer "distribute".</summary>
    private enum MenuWhere
    {
        Empty,
        OneNote,
        ManyNotes,

        /// <summary>The selection is a group as a unit (<see cref="_groupFocus"/> is set): the group
        /// itself is the thing being addressed, so its own verbs come first.</summary>
        Group,
    }

    private MenuWhere WhereMenuIs()
    {
        if (_selection.Count == 0)
        {
            return MenuWhere.Empty;
        }

        if (_groupFocus is not null)
        {
            return MenuWhere.Group;
        }

        return _selection.Count == 1 ? MenuWhere.OneNote : MenuWhere.ManyNotes;
    }

    /// <summary>
    /// Whether a section of the command table belongs in this menu. Clustering is deliberately kept
    /// everywhere: it acts on the whole board, so an empty selection is not a reason to hide it — the
    /// prototype behaves the same way, and a command that vanishes when you have not selected anything
    /// is a command you cannot discover.
    /// </summary>
    /// <remarks>
    /// 2026-10-23 他点名把「板子」「换板子」从右键菜单里删掉：这两节管的不是板上那件事，而是
    /// "窗口现在开哪块板"，顶栏标签、加号与 Ctrl+T/Ctrl+W 已经把它们的入口说全了。命令表本身不动——
    /// 键盘分发与按住 Tab 的那份清单读的仍是整张表，所以删的只是这一个菜单面，不是能力。
    /// 同一天「恢复…」也跟着配对/筛选搬去了标题条右端的 ⋮——那三件是整块板/整台设备的动作。
    /// </remarks>
    private static bool SectionApplies(string section, MenuWhere where) => section switch
    {
        "排布" => where is MenuWhere.ManyNotes or MenuWhere.Group,
        "板子" or "换板子" => false,
        _ => true,
    };

    /// <summary>Commands that would lie about their target: 成组/拆组 need a pile, and the arranging
    /// verbs need two cards. On empty board only the whole-board and view commands survive.</summary>
    private static bool CommandApplies(Command command, MenuWhere where) => where switch
    {
        MenuWhere.Empty => command.Section is "聚拢" or "板子",
        MenuWhere.OneNote => command.Label is not ("成组" or "拆组"),
        _ => true,
    };

    private void ShowBoardMenu(Point screenPosition)
    {
        var where = WhereMenuIs();
        var menu = new MenuFlyout();

        if (where == MenuWhere.Group)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = $"整组 · {_selection.Count} 格", IsEnabled = false });
        }

        if (where == MenuWhere.Empty)
        {
            var here = new MenuFlyoutItem { Text = "在这里新建一格", KeyboardAcceleratorTextOverride = "双击空白" };
            here.Click += (_, _) => NewNoteAt(screenPosition);
            menu.Items.Add(here);
        }
        else
        {
            // 换色不再走右键菜单——它长在便签操作条的色板弹层上（NoteActionRail.cs），
            // 一处入口比两处各摆一份更好维护，也避免"菜单里能选、条上不能选"这种两套语义。
            var order = new MenuFlyoutSubItem { Text = "叠放" };

            var front = new MenuFlyoutItem { Text = "提到最前" };
            front.Click += (_, _) => Reorder(frontmost: true);
            var back = new MenuFlyoutItem { Text = "压到最后" };
            back.Click += (_, _) => Reorder(frontmost: false);

            order.Items.Add(front);
            order.Items.Add(back);
            menu.Items.Add(order);
        }

        if (where == MenuWhere.Group)
        {
            var ungroup = new MenuFlyoutItem { Text = "拆组", KeyboardAcceleratorTextOverride = "Ctrl+Shift+G" };
            ungroup.Click += (_, _) => UngroupSelection();
            menu.Items.Add(ungroup);
        }

        // 排布／聚拢／组织：菜单项由那张表生成，和键盘读的是同一份 ⇒ 一条命令不可能只在其中
        // 一处存在（单键快捷没做之前，菜单就是 WCAG 2.5.7 要求的那个替代面）。
        foreach (var section in Sections())
        {
            var rows = section.Where(command => CommandApplies(command, where)).ToList();

            if (rows.Count == 0 || !SectionApplies(section.Key, where))
            {
                continue;
            }

            var submenu = new MenuFlyoutSubItem { Text = section.Key };

            foreach (var command in rows)
            {
                var action = command.Run;
                var item = new MenuFlyoutItem { Text = command.Label, KeyboardAcceleratorTextOverride = command.Chord };
                item.Click += (_, _) => action();
                submenu.Items.Add(item);
            }

            menu.Items.Add(submenu);
        }

        if (where != MenuWhere.Empty)
        {
            var delete = new MenuFlyoutItem { Text = $"删掉这 {_selection.Count} 格", KeyboardAcceleratorTextOverride = "Delete" };
            delete.Click += (_, _) => RemoveSelection();
            menu.Items.Add(delete);
        }

        var reset = new MenuFlyoutItem { Text = "回到 100%", KeyboardAcceleratorTextOverride = "Ctrl+0" };
        reset.Click += (_, _) => ResetView();
        menu.Items.Add(reset);

        // 筛选 / 恢复 / 配对 2026-10-23 搬到标题条右端的 ⋮（SettingsMenu.cs）：这三件是整块板/
        // 整台设备的动作，不是"右击这格要改什么"。右键菜单只留板面上能看见的那几件。
        menu.ShowAt(BoardArea, new FlyoutShowOptions { Position = screenPosition });
    }

    private void ApplyColor(string? preset)
    {
        if (_selection.Count == 0)
        {
            return;
        }

        BeginGesture($"换了 {_selection.Count} 格的颜色");

        foreach (var id in _selection)
        {
            if (NodeOf(id) is { } node)
            {
                node.Color = preset;
            }
        }

        EndGesture();

        Rebuild();
        SaveSoon();
    }

    private void Reorder(bool frontmost)
    {
        BeginGesture(frontmost ? "提到最前" : "压到最后");

        foreach (var id in _selection)
        {
            if (frontmost)
            {
                _document.BringToFront(id);
            }
            else
            {
                _document.SendToBack(id);
            }
        }

        EndGesture();

        Rebuild();
        SaveSoon();
    }

    /// <summary>
    /// 谁后被点击谁在最上面（10-22 他：新建的总压在旧的上面，不对）。把这一枚提到最前，
    /// 但<b>不重铸整层</b>——Rebuild 会清掉选中，而这一发正要点中它。只把那张卡片挪到
    /// ItemLayer 末尾（z 序＝数组顺序），文档顺序跟着改、落盘，但不占一条撤销步（点一下不该刷一条撤回）。
    /// </summary>
    private void RaiseToFront(string id)
    {
        if (_document is null || NodeOf(id) is not { } node || node.Kind == CanvasNodeKind.Group)
        {
            return;
        }

        if (_document.Nodes.LastOrDefault()?.Id == id)
        {
            return; // 已经在最前，别白改一次盘
        }

        _document.BringToFront(id);

        if (_realized.TryGetValue(id, out var visual))
        {
            ItemLayer.Children.Remove(visual);
            ItemLayer.Children.Add(visual);
        }

        SaveSoon();
    }

    private void ResetView()
    {
        _view.PanX = 24;
        _view.PanY = 16;
        _view.Zoom = 1;

        ArrangeAll();
        ProjectSelection();
        SaveSoon();

        Say("回到 100%");
    }

    // -------------------------------------------------------------------------
    // Self test
    // -------------------------------------------------------------------------

    /// <summary>
    /// Exercises the camera, the realisation and the adorner without a mouse, so the
    /// parts that can be measured alone are measured on every run rather than only when
    /// somebody happens to drag.
    /// </summary>
    /// <returns>Empty when every reading came out as specified.</returns>
    public IReadOnlyList<string> RunSelfTest()
    {
        var problems = new List<string>();

        var anchor = new Point(400, 300);
        var worldUnderAnchor = (_view.ToWorldX(anchor.X), _view.ToWorldY(anchor.Y));

        _view.ZoomAt(anchor.X, anchor.Y, Math.Pow(WheelStep, 8), MinZoom, MaxZoom);
        ArrangeAll();
        ProjectSelection();

        // 组框与空组的读数要取在这一拍：后面的排布/拖拽会真的改动成员。
        SelfTestGroups(problems);
        SelfTestCulledVisuals(problems);
        SelfTestFreshNoteIsFree(problems);

        var driftX = Math.Abs(_view.ToScreenX(worldUnderAnchor.Item1) - anchor.X);
        var driftY = Math.Abs(_view.ToScreenY(worldUnderAnchor.Item2) - anchor.Y);

        if (driftX > 0.5 || driftY > 0.5)
        {
            problems.Add($"zoom drifted the anchor by {driftX:0.00}×{driftY:0.00} px");
        }

        _view.ZoomAt(anchor.X, anchor.Y, 1 / Math.Pow(WheelStep, 8), MinZoom, MaxZoom);
        ArrangeAll();

        foreach (var node in _document.Nodes)
        {
            _selection.Add(node.Id);
        }

        ProjectSelection();

        if (_selection.Count > 0 && _handles.Count != 8)
        {
            problems.Add($"expected 8 resize handles, found {_handles.Count}");
        }

        var scrollers = CountBoardScrollers();

        if (scrollers != 0)
        {
            problems.Add($"{scrollers} scroll host(s) on the canvas — a scrollbar becomes possible");
        }

        if (_realized.Count == 0)
        {
            problems.Add("nothing was realised; the board would show an empty surface");
        }

        // Undo and redo, driven through the same calls a drag makes.
        if (_document.Nodes.FirstOrDefault() is { } target)
        {
            var originalX = target.X;

            BeginGesture("自检移动");
            target.X = originalX + 500;
            EndGesture();

            Undo();

            if (Math.Abs((NodeOf(target.Id)?.X ?? double.NaN) - originalX) > 0.001)
            {
                problems.Add("undo did not put the node back where it started");
            }

            Redo();

            if (Math.Abs((NodeOf(target.Id)?.X ?? double.NaN) - (originalX + 500)) > 0.001)
            {
                problems.Add("redo did not put the node forward again");
            }

            // Leave the file as it was found: this last undo makes the pending patch empty.
            Undo();
        }

        // The snapping call the drag makes, with a target three pixels away.
        var neighbour = new Box(200, 0, 100, 40);
        var dragged = Snapping.Snap(new Box(197, 500, 60, 30), [neighbour], null, SnapThreshold);

        if (Math.Abs(dragged.X - 200) > 0.001)
        {
            problems.Add($"snapping left the node at {dragged.X} instead of 200");
        }

        DrawGuides(dragged.Guides);

        if (GuideLayer.Children.Count != dragged.Guides.Count)
        {
            problems.Add($"drew {GuideLayer.Children.Count} guide lines for {dragged.Guides.Count} hits");
        }

        DrawGuides([]);

        // 排布命令走真路径，而不是只测 Core 的算术：一格时必须明着拒绝并且**不留下撤销步**，
        // 两格时必须真的搬动、并且能一步还原。少了这一发，"命令在 Core 里对、界面上没接线"
        // 这种红是量不出来的。
        // 挑的是**散格**：一个组在排布眼里只算一件（GroupArrange.cs），拿同组两枚来测"两格对齐"
        // 量的会是拒绝那一路——那一路下面单独有一发。
        var groupsForPair = Groups();
        var arrangePair = _document.Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .Where(node => groupsForPair.GroupOf(RectOf(node)) is null)
            .Take(2)
            .ToList();

        if (arrangePair.Count == 2)
        {
            var undoBefore = _history.UndoCount;

            ClearSelection();
            _selection.Add(arrangePair[0].Id);
            Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left));

            if (_history.UndoCount != undoBefore || !_sayRow.Text.Contains("要至少 2 件"))
            {
                problems.Add($"一格排布没有明着拒绝：status『{_sayRow.Text}』undo {_history.UndoCount} vs {undoBefore}");
            }

            var originalX = arrangePair.ConvertAll(node => node.X);
            var originalY = arrangePair.ConvertAll(node => node.Y);

            _selection.Add(arrangePair[1].Id);
            Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left));

            if (Math.Abs(NodeOf(arrangePair[0].Id)!.X - NodeOf(arrangePair[1].Id)!.X) > 0.001)
            {
                problems.Add(
                    $"两格左对齐后还差 {Math.Abs(NodeOf(arrangePair[0].Id)!.X - NodeOf(arrangePair[1].Id)!.X):0.###} px");
            }

            if (_history.UndoCount != undoBefore + 1)
            {
                problems.Add($"一次排布留下了 {_history.UndoCount - undoBefore} 步历史，应为 1 步");
            }

            Undo();

            // 撤销是整份文档换掉，所以这里必须按 id 重新查——手上那枚节点对象属于撤销前那份文档。
            for (var i = 0; i < 2; i++)
            {
                var restored = NodeOf(arrangePair[i].Id)!;

                if (Math.Abs(restored.X - originalX[i]) > 0.001 || Math.Abs(restored.Y - originalY[i]) > 0.001)
                {
                    problems.Add($"撤销排布没回到原位：{restored.Id} 在 {restored.X},{restored.Y}，应在 {originalX[i]},{originalY[i]}");
                }
            }
        }
        else
        {
            Diagnostics.Write($"selftest 排布: 板上凑不出两格散格（找到 {arrangePair.Count} 格），这一发不适用");
        }

        // 同组的两枚：只剩一件东西可排，所以退到排组内那两枚；排完框必须 hug 全组（包括没选中的
        // 那枚），这一步挡的是"把组劈开"——以前那一发会把组里两格拉到同一列，框留在原地。
        {
            var grouped = _document.Nodes
                .Where(node => node.Kind != CanvasNodeKind.Group)
                .Select(node => (node, frame: groupsForPair.GroupOf(RectOf(node))))
                .Where(item => item.frame is not null)
                .GroupBy(item => item.frame!.Value.Id)
                .FirstOrDefault();

            if (grouped is null || grouped.Count() < 2)
            {
                Diagnostics.Write("selftest 排组内: 没有哪个组里有两格可挑，这一发不适用");
            }
            else
            {
                var members = grouped.Select(item => item.node).ToList();
                var groupId = grouped.Key;
                var undoBefore = _history.UndoCount;
                var frameBefore = RectOf(NodeOf(groupId)!);

                ClearSelection();

                foreach (var member in members.Take(2))
                {
                    _selection.Add(member.Id);
                }

                Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left));

                var alignedX = NodeOf(members[0].Id)!.X;

                if (Math.Abs(alignedX - NodeOf(members[1].Id)!.X) > 0.001)
                {
                    problems.Add($"排组内那两枚没对齐：还差 {Math.Abs(alignedX - NodeOf(members[1].Id)!.X):0.###} px");
                }

                var frameNow = RectOf(NodeOf(groupId)!);
                var hug = DerivedFrames()[groupId];

                if (Math.Abs(frameNow.X - hug.X) > 0.001 || Math.Abs(frameNow.Width - hug.Width) > 0.001
                    || Math.Abs(frameNow.Y - hug.Y) > 0.001 || Math.Abs(frameNow.Height - hug.Height) > 0.001)
                {
                    problems.Add(
                        $"排完组内，{groupId} 的框在 {frameNow.X:0.#},{frameNow.Y:0.#} {frameNow.Width:0.#}×{frameNow.Height:0.#}，" +
                        $"全组 {members.Count} 格要的是 {hug.X:0.#},{hug.Y:0.#} {hug.Width:0.#}×{hug.Height:0.#}");
                }

                Undo();

                var back = RectOf(NodeOf(groupId)!);

                if (_history.UndoCount != undoBefore || Math.Abs(back.X - frameBefore.X) > 0.001
                    || Math.Abs(back.Width - frameBefore.Width) > 0.001)
                {
                    problems.Add($"撤销排组内没把框带回 {frameBefore.X:0.#},{frameBefore.Y:0.#}：读到 {back.X:0.#},{back.Y:0.#}");
                }
                else
                {
                    Diagnostics.Write(
                        $"selftest 排组内: {groupId} 里两枚对齐到 X={alignedX:0.#}，框跟着全组 {members.Count} 格重算，撤销一发框和格子一起回去");
                }

                Rebuild();
            }
        }

        // 排布与组：上面那段注释写着"排布命令本来就不作用于组"，可 SelectedInOrder() 并没有过滤组框，
        // 而那一发是**躲开组**挑的格子——所以它量不到那句话是假的。这一发走用户真会走的路：
        // Ctrl+A（所有节点都进选中，含组框）→ 左对齐 → 每个框必须等于它现在那批成员的包围盒；
        // 再撤销一发，格子和框必须一起回去。
        {
            static bool SameBox(Grouping.Rect left, Grouping.Rect right)
                => Math.Abs(left.X - right.X) <= 0.001 && Math.Abs(left.Y - right.Y) <= 0.001
                    && Math.Abs(left.Width - right.Width) <= 0.001 && Math.Abs(left.Height - right.Height) <= 0.001;

            var frames = GroupRects();

            if (frames.Count == 0)
            {
                Diagnostics.Write("selftest 排布与组: 板上没有组，这一发不适用（不是跳过，是没有可测的框）");
            }
            else
            {
                var undoBefore = _history.UndoCount;

                // "整块走"要在这里量：命令前记下每组成员的位置，命令后它们彼此的差必须一模一样。
                var membersBefore = frames.ToDictionary(
                    frame => frame.Id,
                    frame => BoardNotes()
                        .Where(node => frame.Holds(RectOf(node)))
                        .Select(node => (node.Id, node.X, node.Y))
                        .ToList());

                SelectAll();
                Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left));

                var split = new List<string>();

                foreach (var (groupId, members) in membersBefore)
                {
                    if (members.Count < 2)
                    {
                        continue;
                    }

                    var offsets = members
                        .Select(member =>
                        {
                            var node = NodeOf(member.Id)!;

                            return (member.Id, DX: node.X - member.X, DY: node.Y - member.Y);
                        })
                        .ToList();

                    var offenders = offsets
                        .Where(offset => Math.Abs(offset.DX - offsets[0].DX) > 0.001
                            || Math.Abs(offset.DY - offsets[0].DY) > 0.001)
                        .ToList();

                    if (offenders.Count > 0)
                    {
                        split.Add($"{groupId} 里 {offenders.Count}/{offsets.Count} 格位移不同（首格 {offsets[0].DX:0.#},{offsets[0].DY:0.#}）");
                    }
                }

                if (split.Count > 0)
                {
                    problems.Add($"左对齐把一个组劈开了：{string.Join("；", split)}");
                }

                var derived = DerivedFrames();
                var strays = new List<string>();

                foreach (var frame in frames)
                {
                    var node = NodeOf(frame.Id);

                    if (node is null || !derived.TryGetValue(frame.Id, out var hug))
                    {
                        continue;
                    }

                    var now = RectOf(node);

                    if (!SameBox(now, hug))
                    {
                        var nowMembers = BoardNotes()
                            .Where(note => now.Holds(RectOf(note)))
                            .Select(note => $"{note.Id}({note.X:0.#},{note.Y:0.#} {note.Width:0.#}×{note.Height:0.#})");

                        strays.Add(
                            $"{frame.Id} 框在 {now.X:0.#},{now.Y:0.#} {now.Width:0.#}×{now.Height:0.#}，"
                            + $"成员要的是 {hug.X:0.#},{hug.Y:0.#} {hug.Width:0.#}×{hug.Height:0.#}；"
                            + $"对齐前 {string.Join("、", membersBefore[frame.Id].Select(m => m.Id))}，现在 {string.Join("、", nowMembers)}");
                    }
                }

                if (strays.Count > 0)
                {
                    problems.Add($"左对齐以后有 {strays.Count} 个组框没跟着成员走：{string.Join("；", strays)}");
                }

                Undo();

                var leftBehind = new List<string>();

                foreach (var frame in frames)
                {
                    if (NodeOf(frame.Id) is { } node && !SameBox(RectOf(node), frame))
                    {
                        var now = RectOf(node);

                        leftBehind.Add($"{frame.Id} 在 {now.X:0.#},{now.Y:0.#}，应在 {frame.X:0.#},{frame.Y:0.#}");
                    }
                }

                if (leftBehind.Count > 0)
                {
                    problems.Add($"撤销排布没把框带回去：{string.Join("；", leftBehind)}");
                }

                if (_history.UndoCount != undoBefore)
                {
                    problems.Add($"排布与组那一发撤销以后步数不对：{_history.UndoCount} vs {undoBefore}");
                }

                Diagnostics.Write(
                    $"selftest 排布与组: Ctrl+A＋左对齐以后 {frames.Count} 个框都等于成员包围盒、组内每格位移一致（整块走），撤销一发把格子和框一起带回原位");

                Rebuild();
            }
        }

        // 普通挪出一格**不许改分组**（10-24 他定的规矩："只有按住分组图标才能发生入组和出组"）。
        // 这一发走的是 BeginGesture/EndGesture 那对外壳（拖拽用的就是它），测的是松手以后：
        // 名单还是那几格、框按名单长过去罩住它。10-23 之前这一发断言的是反过来的旧语义
        // （挪出去＝框缩回来），那条规矩的代价就是"框长到吞掉路上别人"。
        {
            var groups = Groups();
            var roomy = GroupRects()
                .Select(frame => (frame, members: groups.Members(frame.Id).Select(member => NodeOf(member.Id)!).ToList()))
                .FirstOrDefault(candidate => candidate.members.Count >= 2);

            if (roomy.frame.Id is null)
            {
                Diagnostics.Write("selftest 拖出组: 没有哪个组里有两格可挪，这一发不适用");
            }
            else
            {
                var escaped = roomy.members
                    .OrderByDescending(node => node.X + node.Width)
                    .First();

                var before = RectOf(NodeOf(roomy.frame.Id)!);
                var undoBefore = _history.UndoCount;
                var rosterBefore = groups.MemberIds(roomy.frame.Id).ToList();

                BeginGesture("自检挪出一格");

                escaped.X = roomy.frame.Right + 400;

                EndGesture();

                var after = RectOf(NodeOf(roomy.frame.Id)!);
                var rosterAfter = Groups().MemberIds(roomy.frame.Id).ToList();

                if (!rosterAfter.SequenceEqual(rosterBefore))
                {
                    problems.Add(
                        $"普通挪动改了成员名单：{string.Join("、", rosterBefore)} → {string.Join("、", rosterAfter)}");
                }

                if (!after.Holds(RectOf(NodeOf(escaped.Id)!)))
                {
                    problems.Add("框没按名单长过去罩住那一格：它还在名单里，框却把它丢下了");
                }

                if (after.Width <= before.Width)
                {
                    problems.Add($"挪远一格以后框没跟着长：宽 {before.Width:0.#} → {after.Width:0.#}");
                }

                Undo();

                var back = RectOf(NodeOf(roomy.frame.Id)!);

                if (_history.UndoCount != undoBefore || Math.Abs(back.Width - before.Width) > 0.001
                    || Math.Abs(back.X - before.X) > 0.001)
                {
                    problems.Add($"撤销那一挪没把框带回 {before.X:0.#} 宽 {before.Width:0.#}：读到 {back.X:0.#} 宽 {back.Width:0.#}");
                }
                else
                {
                    Diagnostics.Write(
                        $"selftest 挪出一格: 名单 {rosterAfter.Count} 格未变、框宽 {before.Width:0.#}→{after.Width:0.#}" +
                        "（按名单长过去罩住它），撤销一发带回");
                }

                Rebuild();
            }
        }

        // 标记这条链要在**消费端**量一次：Core 的解析有测试，不等于卡片真的把那行摘掉了，也不等于
        // 那行读数真的画上去了、横幅真的只在筛着时出现。这一发自检跑在 %TEMP% 的副本上。
        if (_document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Text) is { } marked)
        {
            var textBefore = marked.Text;
            var heightBefore = marked.Height;

            marked.Text = $"@d{_view.Today:yyyy-MM-dd} @t自检\n{marked.Text ?? string.Empty}";
            FitNoteHeight(marked);
            Rebuild();

            if (_realized.TryGetValue(marked.Id, out var painted))
            {
                if (painted.PaintsTokenMarkup)
                {
                    problems.Add("属性行没从卡片上摘掉：还画着 @due(...)");
                }

                if (!painted.PaintsReadout)
                {
                    problems.Add("摘掉了属性行却没画上那行读数——等于把标记藏起来，不是显示它");
                }
            }
            else
            {
                problems.Add($"改了 {marked.Id} 的文字却没落进已实现的格子，自检读不到卡片");
            }

            // "画上这一行"与"给这一行留出高度"必须是同一件事。这里不拿某格的实际高度当基线：
            // 那块板上的格子本来就有标记，加上属性行也不该长——第一次跑就是这样误红了一发（存的 140、
            // 量的 80）。比的是同一段正文加与不加一行 token。
            var probe = "自检用的一行正文";
            var band = NoteType.ChipBand(1);
            var plainHeight = MeasureTextHeight(probe, 280, NoteTypography.DefaultSize);
            var markedHeight = MeasureTextHeight($"@d{_view.Today:yyyy-MM-dd}\n{probe}", 280, NoteTypography.DefaultSize);

            if (markedHeight < plainHeight + band - 0.001)
            {
                problems.Add($"量高度少算了那行读数：{plainHeight:0.#} 加 {band:0.#} 应该 ≥ {plainHeight + band:0.#}，实得 {markedHeight:0.#}");
            }

            _view.TagFilter = "#自检";
            Rebuild();

            // 每次 Rebuild 都会重铸卡片，所以要读的是筛上以后那一批，不是上面那枚已经摘下的。
            var stillLit = _realized.TryGetValue(marked.Id, out var filtered) && filtered!.Opacity > 0.99;
            var banner = _bannerRow.Text;

            if (FilterBanner.Visibility != Visibility.Visible || banner is null || !banner.Contains("只看 #自检", StringComparison.Ordinal))
            {
                problems.Add($"筛上以后横幅没出现或文案不对：{FilterBanner.Visibility} 『{banner}』");
            }

            if (!stillLit)
            {
                problems.Add("带着 #自检 的那格被自己的标签筛暗了");
            }

            _view.TagFilter = null;
            marked.Text = textBefore;
            marked.Height = heightBefore;
            Rebuild();

            if (FilterBanner.Visibility == Visibility.Visible)
            {
                problems.Add("清了筛选，板顶横幅还挂着");
            }
        }

        // 筛选折叠那一层要在消费端量：折进去的格子必须 ① 画在锚点上 ② 点不到 ③ 文档一个字节不动
        // ④ 框 hug 看得见的那几格 ⑤ 框上写着"折起 N 格"。摊开以后 ①② 反过来。
        // 演示板上没有"一组两枚、只有一枚带标签"的形状，所以这一发自己造一枚——造完交回去。
        {
            var host = GroupRects()
                .Select(frame => (frame, members: BoardNotes().Where(node => frame.Holds(RectOf(node))).ToList()))
                .FirstOrDefault(candidate => candidate.members.Count >= 2);

            if (host.frame.Id is null)
            {
                Diagnostics.Write("selftest 折叠: 没有哪个组里有两格，这一发不适用（不是跳过，是没有可测的组）");
            }
            else
            {
                var keeper = host.members[0];
                var stray = host.members[^1];
                var textBefore = keeper.Text;

                keeper.Text = "@t折叠自检\n" + (textBefore ?? string.Empty);

                _view.TagFilter = "#折叠自检";
                Rebuild();

                var jsonWhileFolded = _document.ToJson();
                var displays = BuildDisplays();
                var foldedAway = displays.Count(pair => pair.Value.FoldedAway);
                displays.TryGetValue(host.frame.Id, out var plannedFrame);

                var shown = plannedFrame?.ShownFrame;
                var suffix = plannedFrame?.Suffix ?? string.Empty;

                if (foldedAway != host.members.Count - 1)
                {
                    problems.Add($"折叠了几格读到 {foldedAway}，应是 {host.members.Count - 1}（一组 {host.members.Count} 格里只有一枚命中）");
                }

                if (_realized.TryGetValue(stray.Id, out var strayVisual)
                    && (strayVisual.IsHitTestVisible || strayVisual.Opacity > 0.001))
                {
                    problems.Add($"折进去的那一格还点得到（可点 {strayVisual.IsHitTestVisible}、亮度 {strayVisual.Opacity:0.##}）");
                }

                if (shown is null || shown.Value.Width >= host.frame.Width)
                {
                    problems.Add($"框没有跟着看得见的格子收：读到 {(shown?.Width ?? -1):0.#} 宽，原来那副框是 {host.frame.Width:0.#}");
                }

                if (!suffix.Contains("折起 1 格", StringComparison.Ordinal))
                {
                    problems.Add($"框上那句「藏着几格」没出现：读到『{suffix}』");
                }

                // 框上那行会被卡片盖住（框贴着成员收，没给标题留边），所以同一句话必须由横幅再说一遍
                // ——这一发量的就是"屏幕上真有一处说得出藏了几格"。
                if (!_bannerRow.Text.Contains("1 格折进了组", StringComparison.Ordinal))
                {
                    problems.Add($"横幅没说折进了几格：『{_bannerRow.Text}』");
                }

                _expandedGroup = host.frame.Id;

                var opened = BuildDisplays();

                if (opened.Any(pair => pair.Value.FoldedAway))
                {
                    problems.Add("摊开以后还有格子是折着的");
                }

                if (opened.TryGetValue(stray.Id, out var backAgain) && (backAgain.FoldDX != 0 || backAgain.FoldDY != 0))
                {
                    problems.Add($"摊开以后那一格还被位移着：{backAgain.FoldDX:0.##},{backAgain.FoldDY:0.##}");
                }

                if (!string.Equals(_document.ToJson(), jsonWhileFolded, StringComparison.Ordinal))
                {
                    problems.Add("折回／摊开把文档改了：折叠必须只长在画面上，一次筛选不许同步给手机");
                }

                _expandedGroup = null;
                _view.TagFilter = null;
                keeper.Text = textBefore;
                Rebuild();

                Diagnostics.Write(
                    $"selftest 折叠: {host.members.Count} 格里 1 枚命中 ⇒ 折 {foldedAway} 格、点不到、" +
                    $"框从 {host.frame.Width:0.#} 收到 {shown?.Width ?? 0:0.#} 宽、框上『{suffix.Trim()}』；" +
                    "摊开以后文档字节一个没变");
            }
        }

        // markdown 那一格必须在**装好文字的卡片**上量一次：Core 的 Lines/Fits 有测试，不等于卡片
        // 真的按块画了，更不等于"框"和"画进框里的东西"用的是同一个数——标题把首行撑高、末行被切
        // 这类缺陷只会在消费端现形。这一段跑在 %TEMP% 的副本上。
        if (_document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Text
                                                  && !Markdown.LooksLikeMarkdown(node.Text)) is { } plain)
        {
            var textBefore = plain.Text;
            var heightBefore = plain.Height;
            var sizeBefore = _view.NoteSize;

            // 没有 markup 的便签，画法必须一格都不变——这条先立住，才谈得上"这一整个文件没动过普通便签"。
            Rebuild();

            if (_realized.TryGetValue(plain.Id, out var untouched) && untouched!.PaintsMarkdown)
            {
                problems.Add("一段没有 markup 的正文被当成 markdown 画了");
            }

            plain.Text = "# 自检标题\n正文 **粗** 与 `码`\n- [ ] 待办\n> 引用一行";

            // 按内容定高这一档是被页眉开关管着的；不替它拨到 Automatic，下面这两条高度断言
            // 在"固定尺寸"的板上会静默地什么都不比——那就等于没有这一发。
            _view.NoteSize = NoteSizeMode.Automatic;
            FitNoteHeight(plain);
            Rebuild();

            var lines = Markdown.Lines(plain.Text);
            var type = NoteTypography.For(_view.FontSizeFor(plain.Id));
            var zoom = _view.Zoom;

            /* 行高规则要跟**这台投影的文本引擎**对齐，不是跟浏览器：Leading×Scale 是小数
               (28.35)，而引擎一行只给整像素。实测到的表是四舍五入(half-up)：
               21→21 25.2→25 28.35→28 21.5→22 28.5→29 28.6→29 30.5→31。
               Core.LineHeight 就是照这张表写的；这一发把它钉住——将来 SDK 改了量化规则，
               当场红，并且把表打出来，不用靠人眼在卡面上找哪一行被切了。 */
            var drift = new System.Text.StringBuilder();
            var table = new System.Text.StringBuilder();

            foreach (var scale in new[] { 1d, 1.2, 1.35, 21.5 / 21, 28.5 / 21, 28.6 / 21, 30.5 / 21 })
            {
                var probeLine = new MarkdownLine(BlockKind.Plain, scale, string.Empty, Markdown.Inlines("行"));
                var probeBox = MarkdownPaint.Line(type, 1, probeLine, _measureInk, _measureInk, _measureInk);

                probeBox.XamlRoot = BoardArea.XamlRoot;
                probeBox.Measure(new Size(240, double.PositiveInfinity));

                var want = Markdown.LineHeight(type, probeLine);
                var got = probeBox.DesiredSize.Height;

                table.Append($"{type.Leading * scale:0.###}→{got:0.###}  ");

                if (Math.Abs(got - want) > 0.001)
                {
                    drift.Append($"{type.Leading * scale:0.###}: 引擎 {got:0.###} 而 Core 说 {want:0.###}  ");
                }
            }

            if (drift.Length > 0)
            {
                problems.Add("文本引擎的行盒和 Core.LineHeight 不一致 → " + drift + $"（实测量：{table}）");
            }
            else
            {
                Diagnostics.Write("selftest 行盒（与 Core.LineHeight 一致）" + table);
            }

            if (_realized.TryGetValue(plain.Id, out var blocky))
            {
                if (!blocky!.PaintsMarkdown)
                {
                    problems.Add("带 markup 的正文没按块画，还是单个 TextBlock（标题会和正文同一个行距）");
                }
                else if (blocky.PaintedMarkdownLines != lines.Count)
                {
                    problems.Add($"块画了 {blocky.PaintedMarkdownLines} 行，这段文字有 {lines.Count} 行——刚按内容定过高，不该少画");
                }
                else if (!string.Equals(blocky.PaintedMarkdownWords, string.Join('\n', lines.Select(line => line.Text)), StringComparison.Ordinal))
                {
                    // 元素在、字不在：行数与算式都过得去，卡面却是空的。只有把词逐行读回来才看得见。
                    problems.Add($"块里读回『{blocky.PaintedMarkdownWords.Replace('\n', '⏎')}』，应为『{string.Join('⏎', lines.Select(line => line.Text))}』");
                }

                // The frame and the block have to be the same arithmetic. If the measure rounds a
                // heading away and the paint keeps it, this is where it shows.
                var needed = lines.Sum(line => MarkdownPaint.Box(type, zoom, line)) + Math.Clamp(type.Padding * zoom, 0, 60) * 2;

                if (blocky.Height + 0.001 < needed)
                {
                    problems.Add($"框比画进去的块矮：{blocky.Height:0.##} < {needed:0.##}，末行会被切");
                }
            }
            else
            {
                problems.Add($"改了 {plain.Id} 的文字却没落进已实现的格子，自检读不到卡片");
            }

            plain.Text = textBefore;
            plain.Height = heightBefore;
            _view.NoteSize = sizeBefore;
            Rebuild();
        }

        // 开编辑器、一个字不打、直接提交：这一发必须原样吐回同样的字。
        // 傍晚在一张临时板子上真的少过一段（文件里只剩属性行那一行，高度也被按"空正文"重算成 80），
        // 但那次的机制**没有在这里复现**（负对照：把 AcceptsReturn 关掉，赋多行文字仍是一个字不少）。
        // 所以这一发的身份是哨兵，不是修复的凭据：下一次再少字，它会当场说是哪一发。
        if (_document.Nodes.FirstOrDefault(node => (node.Text ?? string.Empty).Contains('\n')) is { } multi)
        {
            var kept = multi.Text ?? string.Empty;
            var before = Tags.Read(kept);

            BeginEdit(multi.Id);

            var seeded = JsonText.NormalizeTextNewlines(_editor?.Text);

            CloseEditor(commit: true);

            var after = Tags.Read(NodeOf(multi.Id)?.Text ?? string.Empty);

            // 文本栏里本来就不该有标记那几行（它们已经变成牌子），所以这里比的是"正文原样、标记一枚不少"。
            if (!string.Equals(seeded, before.Body, StringComparison.Ordinal))
            {
                problems.Add($"编辑器种下去的正文与卡片上那一段不是一回事：{before.Body.Length} 字符 → {seeded.Length} 字符");
            }

            if (!string.Equals(after.Body, before.Body, StringComparison.Ordinal)
                || !string.Equals(after.Props, before.Props, StringComparison.Ordinal)
                || after.Tags.Count != before.Tags.Count
                || after.Due != before.Due)
            {
                problems.Add(
                    $"开一次编辑器再提交丢了东西：正文 {before.Body.Length}→{after.Body.Length}，" +
                    $"标记行 {before.Props.Length}→{after.Props.Length}，标签 {before.Tags.Count}→{after.Tags.Count}，到期 {before.Due?.ToString() ?? "无"}→{after.Due?.ToString() ?? "无"}");
            }

            if (_editingId is not null || _editor is not null)
            {
                problems.Add("提交以后编辑器还挂着");
            }
        }
        else
        {
            Diagnostics.Write("selftest: 板上没有多行文字的格子 ⇒ 编辑器换行往返这一发不适用（不是跳过，是没有可测的格子）");
        }

        // 回车换行时编辑框要立刻长高，不要等提交；手动定过大小的格子必须保持原框。
        {
            var growProbe = BoardNotes()
                .Where(node => node.Kind == CanvasNodeKind.Text && !_view.IsManualSize(node.Id))
                .OrderBy(node => node.Height)
                .FirstOrDefault();

            if (growProbe is not null)
            {
                var savedText = growProbe.Text;
                var savedHeight = growProbe.Height;

                BeginEdit(growProbe.Id);

                if (_editor is null)
                {
                    problems.Add("编辑增长那一发没有开出输入框");
                }
                else
                {
                    var start = _editor.Height;
                    _editor.Text = string.Join("\n", Enumerable.Range(1, 20).Select(n => $"第 {n} 行"));

                    // WinUI 的 TextChanged 会晚一拍；这里直接走同一个测量函数，验的是增长算式。
                    GrowEditorToFit();

                    if (_editor.Height <= start + 1)
                    {
                        problems.Add($"多行输入没有让编辑框长高：{start:0.#} → {_editor.Height:0.#}");
                    }
                }

                CloseEditor(commit: false);
                growProbe.Text = savedText;
                growProbe.Height = savedHeight;
            }

            var manual = _document.Add(CanvasNodeKind.Text, 40, 40, 220, 60);
            manual.Text = string.Join("\n", Enumerable.Range(1, 10).Select(n => $"第 {n} 行"));
            var manualHeight = manual.Height;
            _view.MarkManualSize(manual.Id);
            FitNoteHeight(manual);

            if (Math.Abs(manual.Height - manualHeight) > 0.5)
            {
                problems.Add($"手动定过大小的格子被自动高度改成了 {manual.Height:0.#}（原 {manualHeight:0.#}）");
            }

            _document.Remove(manual.Id);
        }

        // 箭头：加一条边，线上必须真的画出"一根线 + 一枚箭头"两个图元；撤掉就归零。
        // 只在内存里加，不存盘——自检要的是"画没画"，不是"文件里有没有"。
        {
            var pair = _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).Take(2).ToList();

            if (pair.Count == 2)
            {
                var edge = _document.AddEdge(pair[0].Id, pair[1].Id);

                ArrangeAll();

                var drawn = LinkLayer.Children.Count;

                _document.RemoveEdge(edge.Id);
                ArrangeAll();

                if (drawn != 2)
                {
                    problems.Add($"一条边画出了 {drawn} 个图元，应为 2（一根贝塞尔 + 一枚箭头）");
                }

                if (LinkLayer.Children.Count != 0)
                {
                    problems.Add($"撤掉边以后还剩 {LinkLayer.Children.Count} 个图元");
                }
            }
        }

        /* 拉线那一发问的是状态机：起一根、放弃、再接上。
           放弃必须一个字节都不动（不变量 5），接上必须把人挑的那两面写进文档
           ——不然以后格子被拖过中线，线会自己改路。 */
        {
            var ends = BoardNotes().Take(2).ToList();

            if (ends.Count == 2)
            {
                var untouched = _document.ToJson();

                StartPull(ends[0].Id, "e");

                if (!Pulling)
                {
                    problems.Add("起了拉线却没进入拉线态");
                }

                CancelPull();

                if (_document.ToJson() != untouched)
                {
                    problems.Add("放弃一根线改了文档：Esc 必须是纯放弃");
                }

                if (_pullGhost is not null)
                {
                    problems.Add("放弃以后那根虚线还挂在导层上");
                }

                StartPull(ends[0].Id, "e");

                FinishPull(new Point(
                    _view.ToScreenX(ends[1].X + ends[1].Width / 2),
                    _view.ToScreenY(ends[1].Y + ends[1].Height / 2)));

                var joined = _document.Edges.FirstOrDefault(edge =>
                    edge.FromNode == ends[0].Id && edge.ToNode == ends[1].Id);

                if (joined is null)
                {
                    problems.Add("点中另一格没有接上线");
                }
                else if (joined.FromSide != CanvasEdge.Right)
                {
                    problems.Add($"起点挑的那一面没存下来：读到 {joined.FromSide ?? "自动"}");
                }

                if (Pulling)
                {
                    problems.Add("接上之后还留在拉线态");
                }

                if (joined is not null)
                {
                    _document.RemoveEdge(joined.Id);
                }

                ArrangeAll();
            }
        }

        /* 压小以后：藏起来的行必须还够得着，够得着的时候滚轮归这一格、不归整页缩放。
           "还有没有藏行"按视觉行算（长句在窄格里会绕行），所以这里用一枚 12 行的正文测。 */
        {
            // 要的是文字格：文件格的正文是文件名，往它身上写 12 行是量不到东西的。
            var probe = BoardNotes().FirstOrDefault(node => node.Kind == CanvasNodeKind.Text);

            if (probe is not null && _realized.TryGetValue(probe.Id, out var card))
            {
                var was = probe.Height;
                var wasText = probe.Text;

                probe.Text = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"第 {n} 行"));

                Rebuild();

                if (!_realized.TryGetValue(probe.Id, out card))
                {
                    problems.Add("重画以后找不到那一格");
                }
                else
                {
                    // 自动高度那一半的判据：换成 12 行正文并走一次提交时走的那条路，
                    // 格子必须长到装得下；装得下了，滚轮就该还给板子去缩放。
                    FitNoteHeight(probe);

                    ArrangeAll();
                    BoardArea.UpdateLayout();

                    var fit = card.WindowState();
                    var moreAbove = FindDescendant<Microsoft.UI.Xaml.Shapes.Rectangle>(card, "OverflowFadeAbove");
                    var moreBelow = FindDescendant<Microsoft.UI.Xaml.Shapes.Rectangle>(card, "OverflowFadeBelow");

                    if (fit.Visible < fit.Total)
                    {
                        problems.Add($"自动高度以后还差 {fit.Total - fit.Visible} 行没地方放（可见 {fit.Visible} / 共 {fit.Total}）");
                    }

                    if (moreAbove is null || moreBelow is null)
                    {
                        problems.Add("便签上没有溢出渐隐遮罩（OverflowFadeAbove/Below）");
                    }
                    else if (moreBelow.Visibility != Visibility.Collapsed)
                    {
                        problems.Add("放得下的便签仍压着底部渐隐遮罩");
                    }

                    if (card.ScrollByLines(1))
                    {
                        problems.Add($"这格放得下（可见 {fit.Visible} / 共 {fit.Total}），滚轮却抢了板子的缩放");
                    }

                    card.ScrollByLines(-99);
                    probe.Height = Math.Max(40, fit.Visible * 2);

                    ArrangeAll();
                    BoardArea.UpdateLayout();

                    var cut = card.WindowState();

                    if (moreBelow is not null && moreBelow.Visibility != Visibility.Visible)
                    {
                        problems.Add($"压到 {probe.Height:0}px（可见 {cut.Visible} 行 / 共 {cut.Total} 行）却没有底部渐隐遮罩");
                    }

                    if (!card.ScrollByLines(2))
                    {
                        problems.Add($"压到 {probe.Height:0}px（可见 {cut.Visible} 行 / 共 {cut.Total} 行）滚轮仍不归这一格：整页会被缩放，剩下的字够不着");
                    }

                    if (moreAbove is not null && moreAbove.Visibility != Visibility.Visible)
                    {
                        problems.Add("往下翻两行以后顶部没有渐隐遮罩");
                    }

                    if (card.WindowState().First != 2)
                    {
                        problems.Add($"滚两行以后停在第 {card.WindowState().First} 行");
                    }

                    card.ScrollByLines(-99);

                    if (card.WindowState().First != 0)
                    {
                        problems.Add("滚回顶部以后没有回到第一行");
                    }

                    if (moreAbove is not null && moreAbove.Visibility != Visibility.Collapsed)
                    {
                        problems.Add("回到顶部以后顶部渐隐遮罩还压着");
                    }

                    // 他 10-22 报的第四条：手动拉长以后窗口还停在中间，"↑ 更多"亮着、底下全是空的
                    // ——比句末三个点更糟。这一发先滚下去再拉高：放得下了就必须自己回顶、两牌同闭，
                    // 而不是等人自己去滚。框本身不许自己缩（manual 尺寸是另一条规矩）。
                    card.ScrollByLines(2);
                    probe.Height = 2000;

                    ArrangeAll();
                    BoardArea.UpdateLayout();

                    var grown = card.WindowState();

                    if (grown.First != 0)
                    {
                        problems.Add($"拉长到全放得下以后窗口还停在第 {grown.First} 行（可见 {grown.Visible} / 共 {grown.Total}）：该自动回顶");
                    }

                    if ((moreAbove is not null && moreAbove.Visibility != Visibility.Collapsed)
                        || (moreBelow is not null && moreBelow.Visibility != Visibility.Collapsed))
                    {
                        problems.Add($"拉长到全放得下（可见 {grown.Visible} / 共 {grown.Total}）以后渐隐遮罩还压着");
                    }
                }

                probe.Text = wasText;
                probe.Height = was;

                Rebuild();
            }

            // 他 10-22 报的第一、二条：单击组里的便签＝只选那一枚（组是一个关系，不是一块场地）；
            // 点组的名牌或虚线环带＝整组，且框自己不进选中——那才是"整个组的范围变蓝"的来源。
            // 量的是点击路径的唯一判定源 PressTarget，和组卡片上的实物形状：名牌必须抬到顶边上方。
            {
                var emptyFrames = EmptyGroupIds();
                var host = GroupRects().FirstOrDefault(frame => !emptyFrames.Contains(frame.Id));

                if (host.Id is null)
                {
                    Diagnostics.Write("selftest 组点击: 板上没有组，这一发不适用（不是跳过，是没有可测的框）");
                }
                else
                {
                    var members = BoardNotes().Where(node => host.Holds(RectOf(node))).ToList();

                    var (noteIds, noteFocus) = PressTarget(NodeOf(members[0].Id)!);

                    if (noteIds.Count != 1 || noteIds[0] != members[0].Id || noteFocus is not null)
                    {
                        problems.Add($"点组里的便签选中了 {noteIds.Count} 格、焦点 {noteFocus ?? "无"}：单击就该直选那一枚");
                    }

                    var (groupIds, groupFocus) = PressTarget(NodeOf(host.Id)!);

                    if (groupFocus != host.Id || groupIds.Count != members.Count || groupIds.Contains(host.Id))
                    {
                        problems.Add($"点组的边选中了 {groupIds.Count} 格、焦点 {groupFocus ?? "无"}，应是全组 {members.Count} 格且框不进选中");
                    }

                    if (!_realized.TryGetValue(host.Id, out var frameVisual))
                    {
                        problems.Add("组框没有实例化的视觉");
                    }
                    else
                    {
                        var chrome = frameVisual.GroupChrome();

                        if (chrome.SurfaceHittable)
                        {
                            problems.Add("组框还在整片接鼠标：单击组里的便签会被框吃掉");
                        }

                        if (chrome.Grab != Visibility.Visible || chrome.Plate != Visibility.Visible)
                        {
                            problems.Add($"点边选整组的门没开（环带 {chrome.Grab}、名牌 {chrome.Plate}）");
                        }

                        if (chrome.PlateLift >= 0)
                        {
                            problems.Add($"组名牌还压在框内：偏移 {chrome.PlateLift:0.#}px，会和成员的字重合");
                        }
                    }

                    Diagnostics.Write(
                        $"selftest 组点击: {host.Id} 里 {members.Count} 格 ⇒ 单击便签只中 1、点边/名牌中 {groupIds.Count}（框不进选中）；" +
                        "名牌出框、环带接点");
                }
            }

            // 成组的两只手（10-22 他批的 C＋D，D 已并进操作条）：先摆三枚互相叠着的散格，问提议
            // 胶囊浮不浮；点它＝走同一条成组路；再选中组里的一枚 ⇒ 操作条浮起，走「移出」那发
            // ＝那格出组、框缩，撤销归位；普通拖动把成员甩出框 ⇒ 框必须跟着长（sticky）。
            // 负对照：只擦肩 1% 的两枚不许浮提议。
            {
                // 这块测的是"合成的一堆散格成组"，不该被板上原有的组干扰：他刚用『创建分组』造的组
                // 正好可能压在合成堆附近，会把 JoinByGrowingFrame 引到错的框上（自检因此假红）。
                // 自检跑在临时副本上，测完这份就丢——先把原有组清掉，基线只跟合成堆比。
                foreach (var seedGroup in _document.Nodes
                             .Where(n => n.Kind == CanvasNodeKind.Group)
                             .Select(n => n.Id)
                             .ToList())
                {
                    _document.Remove(seedGroup);
                }

                // 原有笔记也可能正好落在合成堆 (900,520) 附近（他手工在板上动过格），会把"3 枚叠拢"
                // 数成 4 枚。先把现有笔记整体停到很远，基线只跟合成堆比；测完再挪回来，净改动为零。
                var parked = new List<(string Id, double X, double Y)>();
                foreach (var seedNote in _document.Nodes.Where(n => n.Kind != CanvasNodeKind.Group).ToList())
                {
                    parked.Add((seedNote.Id, seedNote.X, seedNote.Y));
                    seedNote.X += 100000;
                }

                Rebuild();

                var untouched = _document.ToJson();

                // 临时堆放在 (900,520) 一带：那块在演示板上是空的，不会和 u0a/u48 的框
                // 互相套住，测完按 id 拆干净。
                var stackA = _document.Add(CanvasNodeKind.Text, 900, 520, 200, 100);
                var stackB = _document.Add(CanvasNodeKind.Text, 1050, 540, 200, 100);
                var stackC = _document.Add(CanvasNodeKind.Text, 975, 580, 200, 100);

                Rebuild();

                OfferGroupHint(stackA);

                Grouping.Rect madeFrame = default;

                if (_groupHint is null)
                {
                    problems.Add("三枚叠在一起没浮出『编成组？』提议");
                }
                else
                {
                    var hintText = ((TextBlock)_groupHint.Child).Text;

                    if (!hintText.Contains("3 格", StringComparison.Ordinal))
                    {
                        problems.Add($"提议没说清是几格：『{hintText}』");
                    }

                    // 浮出来不等于看得见：量真实布局＋落位要在视口内——塌成一条线、
                    // 或者停在屏幕外，屏幕上都是"没有提议"。
                    ArrangeAll();
                    BoardArea.UpdateLayout();

                    var hintX = Canvas.GetLeft(_groupHint);
                    var hintY = Canvas.GetTop(_groupHint);
                    var hintSeen = _groupHint.ActualWidth > 60 && _groupHint.ActualHeight > 16
                        && hintX >= 0 && hintX < BoardArea.ActualWidth
                        && hintY >= 0 && hintY < BoardArea.ActualHeight;

                    Diagnostics.Write(
                        $"selftest 成组助手·胶囊: {ActualSizeOf(_groupHint)}、位 ({hintX:0.#},{hintY:0.#})、板宽 {BoardArea.ActualWidth:0.#}");

                    if (!hintSeen)
                    {
                        problems.Add($"提议胶囊塌了：{_groupHint.ActualWidth:0.#}×{_groupHint.ActualHeight:0.#}");
                    }

                    AcceptGroupHint();

                    madeFrame = GroupRects().FirstOrDefault(candidate =>
                        candidate.Holds(RectOf(stackA))
                        && candidate.Holds(RectOf(stackB))
                        && candidate.Holds(RectOf(stackC)));

                    if (madeFrame.Id is null)
                    {
                        problems.Add("点提议以后没有产出一个框住三格的框");
                    }
                }

                if (madeFrame.Id is not null)
                {
                    SelectOnly(stackB.Id);
                    ProjectSelection();

                    var rail = FindDescendant<Border>(HandleLayer, "NoteRail");
                    var railGroup = FindDescendant<Border>(HandleLayer, "NoteRailGroup");

                    if (rail is null || rail.Visibility != Visibility.Visible || railGroup is null)
                    {
                        problems.Add("选中组里的一枚没浮出操作条（组/色/删三枚圆钮）");
                    }
                    else
                    {
                        var frameBefore = madeFrame;
                        var detachStart = NodeOf(stackB.Id)!;
                        var detachStartPos = (detachStart.X, detachStart.Y);

                        DetachFromGroup(stackB.Id); // 组钮菜单「移出分组」的落点

                        var frameNow = GroupRects().First(candidate => candidate.Id == frameBefore.Id);

                        // 移出后：框按剩下的成员缩了一圈，那格已经不在（缩小的）框里。
                        if (frameNow.Holds(RectOf(NodeOf(stackB.Id)!)))
                        {
                            problems.Add("走过『移出分组』以后那格还在（缩小的）框里");
                        }

                        if (frameNow.Width >= frameBefore.Width)
                        {
                            problems.Add($"移出以后框没缩：宽 {frameBefore.Width:0.#} → {frameNow.Width:0.#}");
                        }

                        // 而且它不该被甩到整组之外那种远跳——最多挪出最近那条边、约等于它自己那一格的尺寸。
                        var nudged = Math.Abs(NodeOf(stackB.Id)!.X - detachStartPos.X)
                            + Math.Abs(NodeOf(stackB.Id)!.Y - detachStartPos.Y);

                        if (nudged > detachStart.Width + 40)
                        {
                            problems.Add($"移出把便签甩太远了：位移 {nudged:0.#} 超过它自己那格宽 {detachStart.Width:0.#}＋余量");
                        }

                        Undo();

                        // 撤销是按快照重铸文档的：手里那枚 stackB 是旧对象，必须按 id 重新取，
                        // 否则量的是撤销前那份尸体。
                        if (!SameFrame(RectOf(NodeOf(frameBefore.Id)!), frameBefore)
                            || !frameBefore.Holds(RectOf(NodeOf(stackB.Id)!)))
                        {
                            problems.Add("撤销一发没把那格和框一起拉回");
                        }
                    }

                    // 换色钮现在弹一列色板（6 预设＋无色＝7 颗，直接涂颜色不写色名），点谁套谁——
                    // 不再是"点一下循环换一格"；右键菜单里那份"颜色"也删了，换色只剩操作条这一处入口。
                    SelectOnly(stackB.Id);
                    ProjectSelection();
                    ToggleColorPopover();

                    var popover = FindDescendant<Border>(HandleLayer, "NoteColorPopover");

                    if (popover is null || popover.Visibility != Visibility.Visible)
                    {
                        problems.Add("点换色钮没弹出左侧色板（NoteColorPopover）");
                    }
                    else
                    {
                        var none = FindDescendant<Border>(popover, "NoteSwatchNone");
                        var third = FindDescendant<Border>(popover, "NoteSwatch3");

                        if (none is null || third is null)
                        {
                            problems.Add("色板缺颗：该有『无色』和各预设圆钮");
                        }
                        else if (none.Child is null || third.Child is not null)
                        {
                            problems.Add("只有『无色』那枚该带斜杠：无色没斜杠、或有色那枚带了斜杠");
                        }

                        ApplyColor("3");

                        if (NodeOf(stackB.Id)?.Color != "3")
                        {
                            problems.Add($"点色板第 3 颗该把那格定成 3，实得 {NodeOf(stackB.Id)?.Color ?? "null"}");
                        }

                        Undo(); // 收回这一发改色，别给临时副本留脏格
                    }

                    CloseColorPopover();

                    Diagnostics.Write("selftest 换色色板: 弹层 7 颗（『无色』那枚一圈虚线环）、点第 3 颗＝定色不循环；右键菜单已无『颜色』子项");

                    // 普通拖动不许影响分组（10-24 他定的规矩："只有按住分组图标才能发生入组和出组"）。
                    // 把他甩到框外 300 像素：名单一个字都不许多也不许少，框按名单 hug 过去罩住它，
                    // 撤销一发格子和框一起回去。
                    if (rail is not null)
                    {
                        var stickyB = NodeOf(stackB.Id)!;
                        var rosterBefore = Groups().MemberIds(madeFrame.Id).ToList();

                        BeginGesture("自检普通拖出");

                        stickyB.X = madeFrame.Right + 300;

                        EndGesture();

                        var rosterAfter = Groups().MemberIds(madeFrame.Id).ToList();
                        var grown = RectOf(NodeOf(madeFrame.Id)!);

                        if (!rosterAfter.SequenceEqual(rosterBefore))
                        {
                            problems.Add(
                                $"普通拖动改了成员名单：{string.Join("、", rosterBefore)} → {string.Join("、", rosterAfter)}" +
                                "（只有组钮那一路才许入组／出组）");
                        }

                        if (!grown.Holds(RectOf(NodeOf(stackB.Id)!)))
                        {
                            problems.Add("普通拖动把成员甩出了框：框没按名单 hug 过去罩住它");
                        }

                        // 这一发同时钉住那格 bug 的另一半：框大了不等于人多了。
                        var swallowed = BoardNotes()
                            .Where(node => !rosterAfter.Contains(node.Id) && grown.Holds(RectOf(node)))
                            .Select(node => node.Id)
                            .ToList();

                        Diagnostics.Write(
                            $"selftest 普通拖出: 名单 {rosterAfter.Count} 格未变、框长到 {grown.Width:0.#}×{grown.Height:0.#}、" +
                            $"路上被框盖住而**没**算进组的 {swallowed.Count} 格（几何那一档会把它们收进去）");

                        Undo();
                        Rebuild();
                    }

                    // 一角碰上就该入组，而且**格子不动、只有框长过去**（他 10-22 的更正：
                    // "松手之后便签的位置会变，有问题，应该只是组的虚线边框范围变化"）：
                    // 整格在框外不许入；只让一角探进来（中心在框外）→ 松手判定把框长到罩住它，格子留在原地。
                    if (rail is not null)
                    {
                        var cornerB = NodeOf(stackB.Id)!;

                        cornerB.X = madeFrame.Right + 400;

                        if (JoinByGrowingFrame([stackB.Id], null))
                        {
                            problems.Add("整格在框外 400 像素也被吸进了组");
                        }

                        cornerB.X = madeFrame.Right - 30; // 左上一角探进框，中心仍在框外

                        var droppedX = cornerB.X;

                        if (madeFrame.Holds(RectOf(cornerB)))
                        {
                            problems.Add("自检没造出「一角碰上、中心在外」那一形");
                        }
                        else if (!JoinByGrowingFrame([stackB.Id], null))
                        {
                            problems.Add("一角碰上组框却没激活入组（还是只有中心进框才算）");
                        }
                        else
                        {
                            var grownFrame = GroupRects().First(candidate => candidate.Id == madeFrame.Id);

                            if (NodeOf(stackB.Id)!.X != droppedX)
                            {
                                problems.Add("入组把便签挪走了——应该只长框，格子留在松手处");
                            }

                            if (!grownFrame.Holds(RectOf(NodeOf(stackB.Id)!)))
                            {
                                problems.Add("框没长到罩住这格（中心还在框外）");
                            }
                        }

                        Rebuild();
                    }

                    _document.Remove(madeFrame.Id!);
                }

                var grazeA = _document.Add(CanvasNodeKind.Text, 6000, 6000, 200, 100);
                var grazeB = _document.Add(CanvasNodeKind.Text, 6198, 6000, 200, 100);

                Rebuild();
                OfferGroupHint(grazeA);

                if (_groupHint is not null)
                {
                    problems.Add("两枚只擦肩 1% 也浮了『编成组？』——阈值没挡住");
                }

                ClearGroupHint();

                foreach (var temp in new[] { grazeB, grazeA, stackC, stackB, stackA })
                {
                    _document.Remove(temp.Id);
                }

                Rebuild();

                if (!string.Equals(_document.ToJson(), untouched, StringComparison.Ordinal))
                {
                    problems.Add("成组提议／操作条／sticky 那一发在文档里留了字节");
                }
                else
                {
                    Diagnostics.Write(
                        "selftest 成组助手: 3 枚叠拢 ⇒ 提议浮起、点＝同路成组；操作条随单枚选中浮起，『移出』让框缩、撤销归位；" +
                        "普通拖动甩成员＝框跟着长（sticky）；擦肩 1% 不提议");
                }

                // 把停到远处的原有笔记挪回原位（基线已经比过了，这一步只影响后续自检看得见的板面）。
                foreach (var (parkId, parkX, parkY) in parked)
                {
                    if (NodeOf(parkId) is { } parkedNode)
                    {
                        parkedNode.X = parkX;
                        parkedNode.Y = parkY;
                    }
                }

                Rebuild();

                static bool SameFrame(Grouping.Rect left, Grouping.Rect right)
                    => Math.Abs(left.X - right.X) <= 0.001 && Math.Abs(left.Y - right.Y) <= 0.001
                        && Math.Abs(left.Width - right.Width) <= 0.001 && Math.Abs(left.Height - right.Height) <= 0.001;
            }

            // 双击那三条路的判据：同一格的上边正中该被认成"边缘"，中心该被认成"格内"，
            // 而落点必须认得出是哪一格（路由本身在 OnSurfaceDoubleTapped——卡片收不到手势，
            // 因为按下时板子把指针捕获走了）。
            if (BoardNotes().FirstOrDefault(node => node.Kind == CanvasNodeKind.Text) is { } tapped)
            {
                var onEdge = new Point(
                    _view.ToScreenX(tapped.X + (tapped.Width / 2)),
                    _view.ToScreenY(tapped.Y));

                var inMiddle = new Point(
                    _view.ToScreenX(tapped.X + (tapped.Width / 2)),
                    _view.ToScreenY(tapped.Y + (tapped.Height / 2)));

                var farOff = new Point(
                    _view.ToScreenX(tapped.X + 40000),
                    _view.ToScreenY(tapped.Y + 40000));

                if (DoubleTapTarget(onEdge) is not ("拉线", var pullNode, _) || pullNode?.Id != tapped.Id)
                {
                    problems.Add("双击上边正中没走到拉线（他 10-12 点名的那一条）");
                }

                if (DoubleTapTarget(inMiddle) is not ("编辑", var editNode, _))
                {
                    problems.Add("双击格子正中没走到编辑");
                }

                if (DoubleTapTarget(farOff).Route != "新建")
                {
                    problems.Add("双击空白没走到新建");
                }
            }
        }

        // 右下角那一组是不是照原型那枚"发丝边的容器"长的：高度 34（28 的成员 + 2 的内距 + 2 条边），
        // 成员无边框、漏斗是一枚 PathIcon（不是 SymbolIcon 那套微软字形）。
        {
            var badgeHeight = BadgeHost.ActualHeight;

            if (badgeHeight > 0 && Math.Abs(badgeHeight - 39.5) > 1.5)
            {
                problems.Add($"控件簇高 {badgeHeight:0.##} 屏幕像素，原型量的是 39.5（±1.5）");
            }

            var badgeWidth = BadgeHost.ActualWidth;

            if (badgeWidth > 0 && Math.Abs(badgeWidth - 206.91) > 8)
            {
                problems.Add($"控件簇宽 {badgeWidth:0.##}，原型 206.91（±8）——成员的内距或个数对不上");
            }

            if (ZoomOutButton.BorderThickness.Bottom != 0)
            {
                problems.Add("成员还带着自己的边框：这一组又会长成三个方盒子");
            }

            Diagnostics.Write(
                $"selftest badge: host {BadgeHost.ActualWidth:0.##}×{badgeHeight:0.##}（原型 206.91×39.5）, " +
                $"成员 {ZoomOutButton.ActualWidth:0.##}×{ZoomOutButton.ActualHeight:0.##}（原型 ≈31-35×33.5）");
        }

        // 基准那一发的读数：浮层的皮是照右键菜单定的，而菜单自己的皮来自 WinUI 主题资源。
        // 只量自己＝一句没有对端的断言，所以把那一侧的现值也打出来。
        Diagnostics.Write($"selftest 基准: {ChromeStyling.DescribeFlyoutBaseline(BoardArea)}");

        /* 这一发问的不是"数对不对"，是"是不是同一个组件"。前两版把菜单的数（行 32、字 14、字距边 16）
           一项项量过来、也都对上了，他第三次打回：「从字体、排版到动画都完全不一样…要让人感觉只是
           换了一下文字」。差的是**类型系统**：菜单只有一种线，我们有三种（14 的正文、12 的加粗分节、
           键帽）。所以判据跟着换形状：四块浮层里的每一行必须是同一枚 SheetRow，字号与墨色只许一套。 */
        {
            var before = problems.Count;

            ShowCheatsheet();
            SetFilterPanel(true);

            if (CheatScroll.Content is not StackPanel cheatColumns)
            {
                problems.Add("键位清单没有摊成栏");
            }
            else if (cheatColumns.Children.Count != 2)
            {
                problems.Add($"键位清单读到 {cheatColumns.Children.Count} 栏，应 2（10-23 定的宽裕两栏，窗宽 {Root.ActualWidth:0}）");
            }

            // 查找那一层要拿板上真的有的那半句话去搜：命中为 0 的面板只是在演它自己。
            var probeTerm = ProbeTerm();

            if (probeTerm.Length > 0)
            {
                SetSearchPanel(true);
                _searchBox.Text = probeTerm;
                RunSearch();

                Diagnostics.Write(
                    $"selftest 查找: 探词『{probeTerm}』⇒ 框里『{_searchBox.Text}』、命中 {_searchHits.Count} 处、行 {MenuRows(SearchPanel).Count} 条");
            }

            SetRestorePanel(true);

            // 模板要排过一次量才不是 0：行高与字号都是模板给的值，不是控件自带的。
            BoardArea.UpdateLayout();

            // 两族，不是一把尺：列表那一族（要点中某一行）共用 40px 那一档，
            // 只说一句话的那两枚（横幅、回执）共用 30px 那一档。字号、墨、内缩仍然只有一套——
            // 他 10-12 说回执那枚"大了点"，改的是带高，不是类型系统。
            var surfaces = new (string Where, string Family, FrameworkElement Surface)[]
            {
                ("键位清单", "列表", CheatPanel),
                ("筛选面板", "列表", FilterPanel),
                ("查找面板", "列表", SearchPanel),
                ("恢复面板", "列表", RestorePanel),
                ("筛选横幅", "一句话", FilterBanner),
                ("回执那一句话", "一句话", SayHost),
            };

            var heights = new List<(string Where, string Family, int Count, double Height, string Applied)>();

            foreach (var (where, family, surface) in surfaces)
            {
                var rows = MenuRows(surface);

                if (rows.Count == 0)
                {
                    problems.Add($"{where}里一行都没有——它已经不是菜单那一套搭的了");

                    continue;
                }

                // 落的字号与墨色：这一套语言只有两种线——能点的（正文墨）与不能点的（禁用档墨）。
                // 出现第三种数＝有人又开始给这一块手搓。
                var allowed = new[] { Sheet.Ink(NoteStyling.IsDark), Sheet.Quiet(NoteStyling.IsDark) };

                foreach (var row in rows)
                {
                    var ink = row.Ink is SolidColorBrush brush ? brush.Color : default;
                    var hits = allowed.Where(rgb => rgb.R == ink.R && rgb.G == ink.G && rgb.B == ink.B).ToList();

                    if (row.FontPx != Sheet.FontPx || hits.Count == 0)
                    {
                        problems.Add($"{where}有一行是手搓的：{row.Text}『{row.FontPx:0.##}px/{ink.R},{ink.G},{ink.B}』，这一套里只有 {Sheet.FontPx:0.#}px 配 {string.Join(" 或 ", allowed.Select(rgb => $"{rgb.R},{rgb.G},{rgb.B}"))}");
                    }
                }

                var applied = $"{Sheet.FontPx:0.#}px/{rows.Count(row => row.Ink is SolidColorBrush b && b.Color.R == allowed[0].R) + 0} 行正文墨、{rows.Count(row => row.Ink is SolidColorBrush b && b.Color.R == allowed[1].R)} 行禁用墨";

                var tallest = rows.Max(Height);

                if (rows.Any(row => Math.Abs(Height(row) - tallest) > 0.01))
                {
                    problems.Add($"{where}自己内部的行就不齐：读到 {string.Join("/", rows.Select(row => Height(row).ToString("0.##")))}");
                }

                heights.Add((where, family, rows.Count, tallest, applied));
            }

            // 同一族之内行高必须彼此相等——他要的是"一眼看出是一个组件"，
            // 一块 32 一块 40、一处雅黑一处雅 UI 是这句话最直接的破法。
            foreach (var band in heights.GroupBy(item => item.Family))
            {
                if (band.Select(item => item.Height).Distinct().Count() > 1)
                {
                    problems.Add($"{band.Key}那一族的行高不止一个数：{string.Join(" / ", band.Select(item => $"{item.Where} {item.Height:0.##}"))}");
                }
            }

            // 两族必须真的是两档：要是有人把 30 改回 40（或反过来），上面那条就再也问不出东西了。
            var listBand = heights.Where(item => item.Family == "列表").Select(item => item.Height).DefaultIfEmpty().Max();
            var lineBand = heights.Where(item => item.Family == "一句话").Select(item => item.Height).DefaultIfEmpty().Min();

            if (listBand > 0 && lineBand > 0 && Math.Abs(listBand - lineBand) < 4)
            {
                problems.Add($"两族挤成同一档了：列表 {listBand:0.##}、一句话 {lineBand:0.##}——那一枚回执要的就是矮这一截");
            }

            CloseCheatsheet();
            SetFilterPanel(false);

            // 查找那一发问的不是"这层好不好看"，是"这层有用吗"：拿板上真的有的那半句话去搜，
            // 命中为 0 就说明 matcher、面板、或者两者之间的接线断了。
            if (probeTerm.Length > 0)
            {
                if (_searchHits.Count == 0)
                {
                    problems.Add($"查找「{probeTerm}」命中 0 处，可那半句话就在某格的正文里");
                }

                foreach (var hit in _searchHits)
                {
                    if (hit.MatchStart < 0 || hit.MatchLength < 0 || hit.MatchStart + hit.MatchLength > hit.Snippet.Length)
                    {
                        problems.Add($"命中『{hit.Snippet}』的强调区间 {hit.MatchStart}+{hit.MatchLength} 落在摘要外——界面一染色就越界");
                    }
                }
            }

            // 输入框是浮层里唯一一条不是 SheetRow 的行。它必须和行同高，否则"只是换了一下文字"
            // 这句话就从这里开始漏。
            if (listBand > 0 && Math.Abs(_searchBox.ActualHeight - listBand) > 0.5)
            {
                problems.Add($"查找那一格输入框 {_searchBox.ActualHeight:0.##}px，列表那一档行带是 {listBand:0.##}px——那是第二种组件");
            }

            SetSearchPanel(false);
            SetRestorePanel(false);
            _searchBox.Text = string.Empty;

            if (problems.Count == before)
            {
                Diagnostics.Write(
                    $"selftest 同一枚控件: {string.Join("、", heights.Select(item => $"{item.Where} {item.Count} 行"))}，两档行带 列表 {listBand:0.##}px / 一句话 {lineBand:0.##}px、{heights.First().Applied}，族内同一套");
            }

            // 取证那一发：把筛选面板里**带计数的那一行**实际画出来的东西倒出来。右列有没有键位、
            // 字落在哪一格，看图说不算数——这一行就是答案。
            if (MenuRows(FilterPanel).ElementAtOrDefault(2) is { } probe)
            {
                var parts = new List<string>();

                Walk(probe);

                Diagnostics.Write($"selftest 行内: {probe.Text}『{probe.ActualWidth:0.#}×{probe.ActualHeight:0.#}』⇒ {(parts.Count == 0 ? "模板没落" : string.Join(" + ", parts))}");

                void Walk(DependencyObject node)
                {
                    if (node is TextBlock text)
                    {
                        parts.Add($"『{text.Text}』{text.Visibility}/{text.FontSize:0.#}px/{text.ActualWidth:0.#}w");
                    }

                    for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    {
                        Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
                    }
                }
            }
        }

        /* 浮层这一发问的是"四块浮层是不是同一层皮"：圆角、填充、外缩、抬起，逐块现读再互比。
           行里的事归上面那一发，这里只管 Border 身上那层——它是这个产品自己画的，所以要有闸。 */
        {
            var sheets = new (string Where, FrameworkElement Surface, double Radius, Brush Fill, int Lift)[]
            {
                ("键位清单", CheatPanel, CheatPanel.CornerRadius.TopLeft, CheatPanel.Background, ChromeStyling.ShadowCount(CheatPanel)),
                ("筛选面板", FilterPanel, FilterPanel.CornerRadius.TopLeft, FilterPanel.Background, ChromeStyling.ShadowCount(FilterPanel)),
                ("筛选横幅", FilterBanner, FilterBanner.CornerRadius.TopLeft, FilterBanner.Background, ChromeStyling.ShadowCount(FilterBanner)),
                ("回执那一句话", SayHost, SayHost.CornerRadius.TopLeft, SayHost.Background, ChromeStyling.ShadowCount(SayHost)),
                ("查找面板", SearchPanel, SearchPanel.CornerRadius.TopLeft, SearchPanel.Background, ChromeStyling.ShadowCount(SearchPanel)),
                ("恢复面板", RestorePanel, RestorePanel.CornerRadius.TopLeft, RestorePanel.Background, ChromeStyling.ShadowCount(RestorePanel)),
            };

            var beforeChrome = problems.Count;

            foreach (var sheet in sheets)
            {
                // 键位清单是"页面"那一档（760·内缩 16·圆角 12）；其余五枚是菜单/一句话，仍共用 4/8。
                // 两档共用同一枚填充与同一层抬起，所以"一个组件"这条还在，变的是页面该有多大的边。
                var page = ReferenceEquals(sheet.Surface, CheatPanel);
                var expectedPad = page ? Sheet.PagePadPx : Sheet.OuterPx;
                var expectedRadius = page ? Sheet.PageRadiusPx : Chrome.Radius;

                if (sheet.Surface is not Border border || Math.Abs(border.Padding.Left - expectedPad) > 0.01)
                {
                    problems.Add($"{sheet.Where}：面板自己的内缩不是 {expectedPad:0.#}px（{(page ? "页面档" : "菜单档")}）");
                }

                if (Math.Abs(sheet.Radius - expectedRadius) > 0.01)
                {
                    problems.Add($"{sheet.Where}：圆角 {sheet.Radius:0.##}，{(page ? "页面档" : "菜单档")}应是 {expectedRadius:0.#}");
                }

                if (!ReferenceEquals(sheet.Fill, ChromeStyling.PanelSurface))
                {
                    problems.Add($"{sheet.Where}：填充不是浮层共用的那一枚刷子，是手搓的第二份");
                }

                if (sheet.Lift != 1)
                {
                    problems.Add($"{sheet.Where}：抬起（投影）读到 {sheet.Lift} 件，应为 1 —— 没有它这块面板就是贴在板子上的一块白");
                }
            }

            if (problems.Count == beforeChrome)
            {
                // 动画那一格：菜单弹出来那一下的主题动画资源读不到（现读 PopupMenuThemeAnimation＝未定义，
                // 它在 popup 宿主那一侧），所以这一族用的是平台给"弹层"的那个 transition——
                // 这里只能证明它挂在板子上，播得对不对要他眼睛判。
                var motion = BoardArea.ChildrenTransitions
                    .OfType<PopupThemeTransition>()
                    .Count();

                if (motion == 0)
                {
                    problems.Add("板子上没有挂弹出层的 transition ⇒ 浮层又是\"啪\"地出现，和菜单两种动法");
                }
                else
                {
                    Diagnostics.Write(
                        $"selftest 皮: {sheets.Length} 块浮层同枚填充、各抬 1 件（ThemeShadow，抬起 {Lift.ZPx:0.#}）；" +
                        $"键位清单（页面）圆角 {Sheet.PageRadiusPx:0.#}、内缩 {Sheet.PagePadPx:0.#}px，" +
                        $"其余 {sheets.Length - 1} 块（菜单）圆角 {Chrome.Radius:0.#}、外缩 {Sheet.OuterPx:0.#}px；浮层出现走 PopupThemeTransition");
                }
            }
        }

        // 一行的高度：已经排好的读实物，没排好的（收起的浮层）按 400 宽量一次。
        static double Height(FrameworkElement row)
            => row.ActualHeight > 0 ? row.ActualHeight : Measured(row);

        static double Measured(FrameworkElement row)
        {
            row.Measure(new Size(400, double.PositiveInfinity));

            return row.DesiredSize.Height;
        }

        static List<PanelKit.SheetRow> MenuRows(DependencyObject root)
        {
            var rows = new List<PanelKit.SheetRow>();

            Collect(root);

            return rows;

            void Collect(DependencyObject node)
            {
                if (node is PanelKit.SheetRow row)
                {
                    rows.Add(row);
                }

                switch (node)
                {
                    case Panel container:
                        foreach (var child in container.Children)
                        {
                            Collect(child);
                        }

                        break;

                    case Border sheet when sheet.Child is DependencyObject inner:
                        Collect(inner);

                        break;

                    case ContentControl content when content.Content is DependencyObject payload:
                        Collect(payload);

                        break;
                }
            }
        }

        // 一份片段贴两遍＝两套名字，组与线头各自跟着换；撤销两发交回原样。
        SelfTestCopyPaste(problems);

        // 编辑中那一发滚轮走的是"滚这一格"，不是"缩放整块板子"（10-24 他点的）。
        SelfTestEditingWheel(problems);

        // 单击选中不许把格子挪走：没过 4 像素的那一发是手抖（10-24 他报的第三格）。
        SelfTestClickDoesNotNudge(problems);

        // 删除那一发连选中框一起淡，投影要把框抬回来（10-24 他报的第四格）。
        SelfTestChromeComesBackAfterFade(problems);

        // 一块丢了图的板子也要开得成：那一格写"未找到"，而不是把窗口打死（10-23 撞上的那一发）。
        SelfTestMissingImageTile(problems);

        // 换板子那一组断言在 BoardTabs.cs：切走必须落盘、历史不跨板、关标签不删板、最后一块拒关。
        SelfTestBoardSwitching(problems);

        // 直建与行内改名也走界面自己那两条路；跑在自检副本上，收尾把造出来的板子删掉。
        SelfTestCreateAndRename(problems);

        // 同步"跟上一个改名"的那一发搬的是你的文件，所以它也要有 DOM 级读数：搬对、撞名不搬、正在改名不搬。
        SelfTestRemoteRenameFollow(problems);

        // 关掉一块＝删掉这块板：文件、sidecar、历史与本地同步账本一起走；再点加号会重新建出同名板。
        SelfTestBoardDeletion(problems);

        // 滚轮的一发值多少毫秒：他 10-23 报"快速滚轮卡顿"，先把用户真走的那条路量成一条读数，
        // 再决定是合并事件、还是把网格/投影的重活挪出每一发。
        SelfTestWheelBurst(problems);

        Diagnostics.Write(
            $"selftest: zoom now {Percent()}, realised {_realized.Count}/{_document.Nodes.Count}, " +
            $"visible {VisibleCount()}, handles {_handles.Count}, selected {_selection.Count}, " +
            $"canvas scrollers {scrollers} (浮层里的列表宿主 {CountScrollViewers(CheatPanel) + CountScrollViewers(FilterPanel) + CountScrollViewers(FilterBanner) + CountScrollViewers(SayHost)} 枚，动不了相机), " +
            $"drift {driftX:0.###}×{driftY:0.###} px");

        ClearSelection();
        ProjectSelection();

        // 放在最后：它会切到「所有便签」再切回，且会改动临时副本上的文件（父集新建/删格）。
        SelfTestAllNotesTab(problems);

        return problems;
    }

    /// <summary>
    /// 滚轮 burst：同一锚点上来回 8 发，量"每一发多贵、缓动几帧收敛、点阵重画一发多贵、
    /// 停手提交多贵"，跑完把相机交回去。
    /// </summary>
    /// <remarks>
    /// 这是"手感"的装置读数——它不判"好不好看"（那归他的眼睛），只让"卡不卡／缓不缓"有可复算的数。
    /// 两格判据是设计决定，不是装饰：缓动超过 24 帧（≈400ms）＝停手以后画面还在自己走，读作"不跟手"；
    /// 点阵重画一发超过两拍间隔（66ms）＝"缓动中重画点阵"这个选择本身站不住，该退回只在提交时画。
    /// 两个数都是 10-24 为"缩放丝滑"那一刀立的尺，不是拿现测值反推的门槛（现测：收敛 17 帧、重画 9–24ms）。
    /// </remarks>
    private void SelfTestWheelBurst(List<string> problems)
    {
        var savedX = _view.PanX;
        var savedY = _view.PanY;
        var savedZoom = _view.Zoom;
        var at = new Point(BoardArea.ActualWidth / 2, BoardArea.ActualHeight / 2);
        const int steps = 8;

        var started = Stopwatch.GetTimestamp();

        for (var index = 0; index < steps; index++)
        {
            ApplyWheel(at, 120);
        }

        var previewMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

        // 一帧一帧喂给缓动，数它几帧收敛、最慢一帧多久（第一帧含一次真重画）。
        var frames = 0;
        var worstFrameMs = 0d;

        while (frames < 60)
        {
            var frameStart = Stopwatch.GetTimestamp();

            OnWheelFrame(null, new object());

            var frameMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;

            frames++;
            worstFrameMs = Math.Max(worstFrameMs, frameMs);

            if (_wheelShownZoom == _wheelTargetZoom)
            {
                break;
            }
        }

        // 点阵那一发单独量：同步循环里 33ms 的节流只会让头一帧真重画，量不到第二发的代价。
        var paintStart = Stopwatch.GetTimestamp();

        UpdateAtmosphereAt(_wheelTargetZoom, _view.PanX, _view.PanY, forensics: false);

        var paintMs = (Stopwatch.GetTimestamp() - paintStart) * 1000.0 / Stopwatch.Frequency;
        var committed = Stopwatch.GetTimestamp();

        CommitWheelPreview();

        var commitMs = (Stopwatch.GetTimestamp() - committed) * 1000.0 / Stopwatch.Frequency;

        _view.PanX = savedX;
        _view.PanY = savedY;
        _view.Zoom = savedZoom;

        ArrangeAll();
        ProjectSelection();

        Diagnostics.Write(
            $"selftest 滚轮: {steps} 发预览共 {previewMs:0.0}ms（均 {previewMs / steps:0.00}ms/发，只推目标）、" +
            $"缓动 {frames} 帧收敛（最慢一帧 {worstFrameMs:0.0}ms）、点阵重画一发 {paintMs:0.0}ms、" +
            $"停手提交 {commitMs:0.0}ms（一次 ArrangeAll + 网格重绘）");

        if (frames > 24)
        {
            problems.Add($"缩放缓动 {frames} 帧才收敛：停手以后画面还在走，读起来就是不跟手（上限 24 帧≈400ms）");
        }

        if (paintMs > GridTweenMinIntervalMs * 2)
        {
            problems.Add($"点阵重画一发 {paintMs:0.0}ms，比两拍间隔（{GridTweenMinIntervalMs * 2}ms）还长：缓动中逐发重画这个选择不成立，该退回提交时画一张");
        }
    }

    /// <summary>
    /// 编辑中那一发滚轮：把正在写的那一格滚进视野，缩放一档不许动；走到边就明着"没地方可滚"。
    /// </summary>
    /// <remarks>
    /// 为什么放在界面自检而不是 Core 测试里：Core 那九项钉的是几何，这一发要钉的是**接线**——
    /// 自检走的就是 <see cref="RouteWheel"/> 那一句（与 <c>OnWheel</c> 同一扇门），所以把
    /// "编辑中"那一档摘掉，红的是"缩放被这一发改掉了"那条断言，不是一句注释。
    /// 真机上量不成：Computer Use 发滚轮之前必须先拍一张图，而那一发截图会把焦点从编辑框拿走，
    /// 编辑框一失焦就提交（那是设计），"正在编辑"这一档在装置里活不过一次观测——
    /// 10-24 试了两轮都是这样折的。
    /// </remarks>
    private void SelfTestEditingWheel(List<string> problems)
    {
        if (_document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Text) is not { } node)
        {
            problems.Add("selftest 编辑中滚轮: 这块板上没有文字格，这一发不适用");

            return;
        }

        var savedPanX = _view.PanX;
        var savedPanY = _view.PanY;
        var savedZoom = _view.Zoom;
        var viewport = BoardArea.ActualHeight;

        BeginEdit(node.Id);

        if (_editor is null)
        {
            problems.Add("selftest 编辑中滚轮: BeginEdit 之后没有编辑框");

            return;
        }

        // 把编辑框撑到比视口高 200 像素，并把它的顶边钉在视口最上沿：
        // 这样"上面没得滚"与"下面探出 200"两档同时在场，判据不用猜方向。
        _editorGrownWorld = (viewport + 200) / _view.Zoom;
        _view.PanY = -(node.Y * _view.Zoom);
        PlaceEditor();

        var top = Canvas.GetTop(_editor);
        var height = _editor.Height;
        var zoomBefore = _view.Zoom;

        if (Math.Abs(top) > 1 || height <= viewport)
        {
            problems.Add($"selftest 编辑中滚轮: 摆不出那一档（顶边 {top:0.#}、高 {height:0.#}、视口 {viewport:0.#}）");
        }
        else
        {
            var at = new Point(Canvas.GetLeft(_editor) + 40, top + 40);

            // 往上滚：顶边已经贴住视口上沿，上面没东西可滚 ⇒ 画面一格都不许动。
            var panBefore = _view.PanY;

            RouteWheel(at, 120);

            if (Math.Abs(_view.PanY - panBefore) > 0.5)
            {
                problems.Add("selftest 编辑中滚轮: 顶边贴住视口上沿时往上滚，那一发不该搬任何东西（它倒着滚了）");
            }

            var total = 0d;
            var notches = 0;

            // 往下滚：一格一格搬到"编辑框底边贴住视口底"为止；搬不动那一发就是走到头。
            while (notches < 40)
            {
                var before = _view.PanY;

                RouteWheel(at, -120);

                var moved = before - _view.PanY;

                if (moved <= 0.5)
                {
                    break;
                }

                total += moved;
                notches++;
            }

            if (notches >= 40)
            {
                problems.Add("selftest 编辑中滚轮: 滚了 40 发还没到头，钳位那一头不成立");
            }
            else if (Math.Abs(total - 200) > 0.5)
            {
                problems.Add($"selftest 编辑中滚轮: 一共搬了 {total:0.##} 像素，应为 200（编辑框高出视口的那一截）");
            }

            if (Math.Abs(_view.Zoom - zoomBefore) > 1e-9)
            {
                problems.Add($"selftest 编辑中滚轮: 缩放被这一发改掉了（{zoomBefore:0.###} → {_view.Zoom:0.###}）——编辑中那一发不该缩放整块板子");
            }

            Diagnostics.Write(
                $"selftest 编辑中滚轮: 探出 200 像素、{notches} 发滚完（一格上限 {EditorScrollPxPerNotch:0}）、" +
                $"缩放钉在 {Percent()}；顶边贴住上沿那一发往上滚＝明着 no-op，不静默");
        }

        CloseEditor(commit: false);

        _editorGrownWorld = 0;
        _view.PanX = savedPanX;
        _view.PanY = savedPanY;
        _view.Zoom = savedZoom;

        Rebuild();
    }

    /// <summary>
    /// 单击选中不许把格子挪走：没过 4 像素的那一发位移必须是 0，过了的那一发必须整段跟上。
    /// </summary>
    /// <remarks>
    /// 他 10-24 报"单击选中便签时偶尔会轻微移动一下"。凭据在日志里：`drag ended: 1 node(s), 2.0 world px`
    /// ——上一版每一发 PointerMoved 都直接把格子写到指针上，手抖两像素就留两像素。
    /// 这一发摆的是同一条路（真 <see cref="ApplyNodeDrag"/>），两头都判：抖的那一发不许动，
    /// 过了阈值的要整段跟上（不是扣掉那 4 像素再走——那是另一套手感，得明说）。
    /// </remarks>
    private void SelfTestClickDoesNotNudge(List<string> problems)
    {
        if (_document.Nodes.FirstOrDefault(node => node.Kind == CanvasNodeKind.Text) is not { } node)
        {
            problems.Add("selftest 单击不挪格: 这块板上没有文字格，这一发不适用");

            return;
        }

        var startX = node.X;
        var startY = node.Y;
        var screen = new Point(
            _view.ToScreenX(node.X + (node.Width / 2)),
            _view.ToScreenY(node.Y + (node.Height / 2)));

        void Press()
        {
            _nodeGesture = new NodeGesture(
                _view.ToWorldX(screen.X),
                _view.ToWorldY(screen.Y),
                new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal) { [node.Id] = (startX, startY) },
                node.Id);

            _nodeDragStarted = false;
        }

        Press();
        ApplyNodeDrag(new Point(screen.X + 2, screen.Y + 1));   // 曼哈顿 3 屏幕像素：没过 4 那一档

        if (Math.Abs(node.X - startX) > 1e-9 || Math.Abs(node.Y - startY) > 1e-9)
        {
            problems.Add(
                $"selftest 单击不挪格: 抖了 3 屏幕像素就把格子从 ({startX:0.##},{startY:0.##}) 写到了 " +
                $"({node.X:0.##},{node.Y:0.##})，阈值那一头不成立");
        }

        Press();
        ApplyNodeDrag(new Point(screen.X + 40, screen.Y));      // 过了阈值：整段 40 屏幕像素都要跟上

        var movedWorld = _view.ToWorldX(screen.X + 40) - _view.ToWorldX(screen.X);
        var actual = node.X - startX;

        // 那一发走的是"指针位移＋吸附"两笔账：8 世界像素（按缩放折到世界）以内的偏差是吸附带宽，
        // 不算漏。要钉的是"过了阈值就得整段跟上"，不是"一格一像素都不许差"。
        if (actual <= 0)
        {
            problems.Add("selftest 单击不挪格: 过了 4 像素那一发格子一格都没动——那道闸把拖动也挡了");
        }
        else if (Math.Abs(actual - movedWorld) > SnapThreshold)
        {
            problems.Add(
                $"selftest 单击不挪格: 过了阈值那一发走了 {actual:0.##} 世界像素，指针那一发是 {movedWorld:0.##}，" +
                $"超出吸附带宽 {SnapThreshold:0.##}");
        }

        Diagnostics.Write(
            $"selftest 单击不挪格: 抖 3 屏幕像素＝一格未动；走满 40 屏幕像素＝格子跟到 {actual:0.##}（指针 {movedWorld:0.##}、吸附带宽 {SnapThreshold:0.##}）；阈值与框选同一枚 4");

        node.X = startX;
        node.Y = startY;
        _nodeGesture = null;
        _nodeDragStarted = false;
    }

    /// <summary>
    /// 删除那一发现在连描边与内对比线一起淡（10-24 他点的第四格："便签渐渐没了、套子还在，最后忽然消失"）。
    /// 这两枚是常驻元素、不是每次新建的，所以每一次投影都要把它们抬回满不透明——这一发钉的就是那一句。
    /// </summary>
    private void SelfTestChromeComesBackAfterFade(List<string> problems)
    {
        SelectionOutline.Opacity = 0;
        SelectionInner.Opacity = 0;

        ProjectSelection();

        if (SelectionOutline.Opacity < 0.99 || SelectionInner.Opacity < 0.99)
        {
            problems.Add(
                $"淡出以后选中框没抬回来：描边 {SelectionOutline.Opacity:0.##}、内对比线 {SelectionInner.Opacity:0.##}");
        }
        else
        {
            Diagnostics.Write("selftest 选中框回血: 两枚都抬回 1（删除那一发一起淡，投影时负责抬回来）");
        }
    }

    /// <summary>
    /// 被视口剔除的格子必须整件 Collapsed。只把 Width/Height 置零不够：三枚纸影是
    /// 卡盒子外的独立矩形，会留在旧位置——他 10-22 截图里那排"只剩阴影"的空白条。
    /// </summary>
    private void SelfTestCulledVisuals(List<string> problems)
    {
        var culled = _realized.Values
            .Where(visual => visual.Width <= 0.5 || visual.Height <= 0.5)
            .ToList();

        if (culled.Count == 0)
        {
            Diagnostics.Write("selftest 剔除: 这一拍没有格子被剔出，阴影残留这一发不适用（不是通过）");

            return;
        }

        var leaking = culled
            .Where(visual => visual.Visibility != Visibility.Collapsed)
            .ToList();

        if (leaking.Count > 0)
        {
            problems.Add(
                $"{leaking.Count}/{culled.Count} 格被剔除后仍 Visibility={leaking[0].Visibility}：卡片阴影带会留在板上");
        }
        else
        {
            Diagnostics.Write($"selftest 剔除: {culled.Count} 格已 Collapsed，阴影带不再露");
        }
    }

    /// <summary>
    /// 一块丢了图的板子必须还打得开：那一格写"未找到"，而不是把窗口打死。
    /// 10-23 拿一块引用着不在盘上的图的板子开屏，撞上的就是这一发——那格字原本挂在 `_label` 上，
    /// 而 `_label` 已经在 `_textHost` 里，WinUI 对"还有父亲的元素再挂一次"答的是 ArgumentException，
    /// 整个窗口在构造里就死了（自检副本带着 assets 目录，所以闸门一直看不见这条路）。
    /// </summary>
    /// <remarks>
    /// 这里不叫 Rebuild：相机筛掉看不见的格子，把测试格停到远处就量不到它，停到近处又会污染
    /// 后面每一步的计数。造一枚真正的 <see cref="NodeVisual"/> 走的仍是同一条构造路（ctor →
    /// LoadImage），只是不进板子的树，也不动盘。
    /// </remarks>
    private void SelfTestMissingImageTile(List<string> problems)
    {
        const string id = "umissingimg";

        var node = _document.Add(CanvasNodeKind.File, 0, 0, 240, 160, id: id);

        node.FilePath = "assets/这一张不在盘上.png";

        try
        {
            var visual = new NodeVisual(node, _ => null);

            if (!visual.IsBroken)
            {
                problems.Add("自检：图没了的那格没把自己标成 broken");
            }
            else if (visual.MissingText is null || !visual.MissingText.StartsWith("未找到"))
            {
                problems.Add($"自检：丢了图的那格没说出为什么空着（读到「{visual.MissingText ?? "(空)"}」）");
            }
            else
            {
                Diagnostics.Write($"selftest 丢了图的格子: 开得成，那一格写着「{visual.MissingText!.Replace("\n", "／")}」");
            }
        }
        catch (Exception ex)
        {
            // 这一发要写的就是"别打死窗口"：它再抛，就是当年那条路回来了。
            problems.Add($"自检：造一块丢了图的格子把窗口打死了（{ex.GetType().Name}: {ex.Message}）");
        }
        finally
        {
            _document.Remove(id);
        }
    }

    public void BeginSelfTest()
    {
        RoutedEventHandler run = null!;

        // 标签条的 ListView 是在 Root.Loaded 之后才收到自己的 Loaded 的：同一拍量它，
        // 得到的是"items=0、6×5、容器全没长"的假读数（10-22 第一发就红在这里）。
        // 自检要等它真 loaded 再跑；等不到也照跑，让几何断言自己红，而不是把装置问题
        // 读成产品问题。
        void FinishWhenTabsAreLoaded(int attempts)
        {
            var list = FindDescendant<ListView>(BoardTabs, "TabListView");

            if (list is null || list.IsLoaded || attempts >= 120)
            {
                if (list is not null && !list.IsLoaded)
                {
                    Diagnostics.Write($"SELFTEST NOTE: TabListView 等到 {attempts} 拍仍未 loaded，标签条几何会读 0");
                }

                var problems = RunSelfTest();

                foreach (var problem in problems)
                {
                    Diagnostics.Write($"SELFTEST FAIL: {problem}");
                }

                Diagnostics.Write(problems.Count == 0 ? "SELFTEST PASS" : $"SELFTEST FAILED ({problems.Count} problem(s))");

                Environment.Exit(problems.Count == 0 ? 0 : 1);

                return;
            }

            DispatcherQueue.TryEnqueue(() => FinishWhenTabsAreLoaded(attempts + 1));
        }

        run = (_, _) =>
        {
            Root.Loaded -= run;
            FinishWhenTabsAreLoaded(attempts: 0);
        };

        if (Root.IsLoaded)
        {
            run(this, new RoutedEventArgs());
        }
        else
        {
            Root.Loaded += run;

            // A layout pass that never arrives must not leave a window standing there.
            var watchdog = DispatcherQueue.CreateTimer();
            watchdog.Interval = TimeSpan.FromSeconds(10);
            watchdog.IsRepeating = false;
            watchdog.Tick += (_, _) =>
            {
                Diagnostics.Write("SELFTEST FAIL: the board never got a layout pass within 10 s");
                Environment.Exit(2);
            };

            watchdog.Start();
        }
    }

    /// <summary>
    /// Scroll hosts that could move the camera. This is the narrow version of the old "zero
    /// ScrollViewers anywhere", and the narrowing is on purpose: the promise under test is that the
    /// <b>board</b> never scrolls — panning is the only way the camera moves by hand, so a scroll host
    /// anywhere around the item layer would put a scrollbar on the canvas and take the wheel away
    /// from zooming.
    /// <para>
    /// It counts inside <see cref="PanLayer"/> — the camera's own subtree — rather than "everything
    /// except the panels". The floating panels are siblings of that layer, and each of them now hosts
    /// a <c>MenuFlyoutPresenter</c>, whose template carries a ScrollViewer of its own; a subtract-the-
    /// known-list rule would have to grow every time a surface is added, and would read differently
    /// depending on whether a panel had ever been opened (a collapsed subtree has no template yet).
    /// </para>
    /// </summary>
    private int CountBoardScrollers()
        => CountScrollViewers(PanLayer);

    private static int CountScrollViewers(DependencyObject root)
    {
        var found = root is ScrollViewer or ScrollView ? 1 : 0;

        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            found += CountScrollViewers(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index));
        }

        // A ScrollViewer would also arrive through a presenter that has not been
        // expanded yet, so count the declared types too, not only the realised ones.
        return found;
    }

    // -------------------------------------------------------------------------
    // Shot: what the board actually looks like, for the command line
    // -------------------------------------------------------------------------

    private void StartShotServer()
    {
        if (_boardPath is null)
        {
            return;
        }

        _shotServer?.Dispose();

        _shotServer = new ShotServer(_boardPath, request =>
        {
            // The capture has to run where the visual tree lives. This thread is the pipe's
            // own, so blocking it on the dispatcher is safe and keeps the UI thread free.
            var completion = new TaskCompletionSource<ShotReply>(TaskCreationOptions.RunContinuationsAsynchronously);

            DispatcherQueue.TryEnqueue(async () => completion.TrySetResult(await CaptureAsync(request)));

            return completion.Task.GetAwaiter().GetResult();
        });

        Diagnostics.Write($"shot pipe at {_shotServer.PipeName}");
    }

    private async Task<ShotReply> CaptureAsync(ShotRequest request)
    {
        if (_boardPath is null)
        {
            return new ShotReply(false, null, Error: "还没有打开板面，画不出图");
        }

        CommitWheelPreview();

        var mode = (request.Mode ?? "view").Trim().ToLowerInvariant();
        var savedPanX = _view.PanX;
        var savedPanY = _view.PanY;
        var savedZoom = _view.Zoom;
        var reframed = false;
        var hiddenForShot = false;
        var resampled = false;
        var savedSelection = new HashSet<string>(_selection, StringComparer.Ordinal);
        var savedFilterPanel = _filterPanelOpen;
        var savedCheatsheet = _cheatsheetOpen;
        var savedSearch = _searchOpen;
        var savedRestore = _restoreOpen;
        var savedRename = _renamingPath;
        var panelsOpened = false;
        string? savedPanelTagFilter = null;
        Box? framedBox = null;

        // 拍的是"落定以后长什么样"：操作条那三枚圆钮有 120ms 的入场动效，不关掉它，
        // 取证图永远停在 0ms 的三枚透明圆上——那不是用户看到的界面。
        _capturingShot = true;

        try
        {
            if (mode is "fit" or "node")
            {
                Box? toFrame = mode == "fit" ? WholeBoardBox() : NodeBox(request.NodeId ?? string.Empty);
                framedBox = toFrame;

                if (toFrame is null)
                {
                    return new ShotReply(false, null, Error: mode == "fit"
                        ? "板上没有节点，框不出东西"
                        : $"板上没有 id \"{request.NodeId}\"");
                }

                // The capture region is what the layer actually renders, which is the bounds
                // of everything visible in it. So a single-node shot has to hide the rest, or
                // the neighbours come along in the frame.
                if (mode == "node")
                {
                    foreach (var (id, visual) in _realized)
                    {
                        visual.Visibility = string.Equals(id, request.NodeId, StringComparison.Ordinal)
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }

                    hiddenForShot = true;
                }

                Frame(toFrame.Value);
                reframed = true;
            }
            else if (mode != "view" && mode != "chrome")
            {
                // 打错的档位不当场拒的话会降级成 view：拍不到标签条的那张图会被读成"标签条没画出来"。
                return new ShotReply(false, null, Error: $"不懂的 mode \"{request.Mode}\"，可给 view / fit / node / chrome");
            }

            if (request.Select is { Length: > 0 } selectArg)
            {
                ClearSelection();

                foreach (var id in selectArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (NodeOf(id) is null)
                    {
                        return new ShotReply(false, null, Error: $"板上没有 id \"{id}\"，select 只认已经在板上的格子");
                    }

                    _selection.Add(id);
                }

                ProjectSelection();
                resampled = true;
            }

            // 浮层也要能取证：圆角、透不透、字读不读得清，这些以前只能靠嘴说。
            if (request.Panel is { Length: > 0 } panelArg)
            {
                var savedTagFilter = _view.TagFilter;

                switch (panelArg.Trim().ToLowerInvariant())
                {
                    case "filter":
                        SetFilterPanel(true);
                        break;

                    case "cheat":
                        ShowCheatsheet();
                        break;

                    case "search":
                        // 取证要看的是"有命中的那一屏"，所以拿板上真的有的那半句话去搜——
                        // 空的查找框拍出来只是一格光标，量不到行高与摘要的排法。
                        OpenSearch();

                        var term = ProbeTerm();

                        if (term.Length > 0)
                        {
                            _searchBox.Text = term;
                            RunSearch();
                        }

                        break;

                    case "restore":
                        SetRestorePanel(true);
                        break;

                    case "rename":
                        // 行内改名那一帧也得拍得到：活动标签换成了输入框，关闭键收起，输入框
                        // 比原名长一截——否则"c"和原名一模一样，PNG 看不出换没换。
                        // 预览锁：TextField 失焦会走提交，拍图不能因此真把临时板改名。
                        _renamePreview = true;
                        BeginRenameTab(_boardPath);

                        if (_renameBox is not null)
                        {
                            _renameBox.Text = $"{BoardName(_boardPath)}（改名）";
                        }

                        break;

                    case "pair":
                        // 取证要看的是"码真画出来了"那一屏：一层浮窗加几百枚矩形，拍下来的 PNG
                        // 里有没有那块白底，就是它可不可见的证据。
                        OpenPairingPanel();
                        break;

                    case "shrink":
                    case "scrolled":
                        // 取证"压小以后后面的行还在不在"：同一格压到四分之一，一发不滚、
                        // 一发往下走两行——两张图一比就知道够不够得着。
                        {
                            // 只挑**此刻在屏幕上**的那格：压到视野外的一格，两张取证图当然一模一样，
                            // 那种"没有差别"会被读成"滚动没有效果"。
                            var tall = BoardNotes()
                                .Where(candidate => (candidate.Text ?? string.Empty).Contains("\n"))
                                .Where(candidate => _realized.TryGetValue(candidate.Id, out var onScreen) && onScreen.IsHitTestVisible)
                                .OrderByDescending(candidate => candidate.Height)
                                .FirstOrDefault();

                            if (tall is null || !_realized.TryGetValue(tall.Id, out var card))
                            {
                                return new ShotReply(false, null, Error: "屏幕上没有多行文字的格子，这一发没有可压的格");
                            }

                            Diagnostics.Write($"shot {panelArg}: 压的是 {tall.Id}（{_realized[tall.Id].WindowState().ToString()}）");

                            _shotShrunk = (tall, tall.Height);

                            tall.Height = Math.Max(40, tall.Height / 4);

                            ArrangeAll();
                            BoardArea.UpdateLayout();

                            Diagnostics.Write($"shot {panelArg}: 压完 {card.WindowState()}");

                            if (panelArg == "scrolled")
                            {
                                var moved = card.ScrollByLines(2);

                                Diagnostics.Write($"shot scrolled: 接手={moved} 滚完 {card.WindowState()}");
                            }
                        }

                        break;

                    case "pull":
                        // 那根虚线只在拉线途中存在，所以取证也要真的拉起来：起点钉在第一格的
                        // 右边，自由端停在第二格中心前面——拍的就是"还没落定"那一帧。
                        {
                            var ends = BoardNotes().Take(2).ToList();

                            if (ends.Count == 2)
                            {
                                StartPull(ends[0].Id, "e");
                                TrackPull(new Point(
                                    _view.ToScreenX(ends[1].X + (ends[1].Width / 2)),
                                    _view.ToScreenY(ends[1].Y + (ends[1].Height / 2))));
                            }
                        }

                        break;

                    case "banner":
                        // 横幅只在"真在筛着"时存在，所以取证也得真的筛上：拿板上第一个有人在用的标签，
                        // 拍完再交回原来那副 lens（没有就清掉）。
                        _view.TagFilter = NoteMarks.Tally(
                            _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group)
                                .Select(node => node.Text).ToList()).FirstOrDefault().Tag ?? savedTagFilter;
                        Rebuild();
                        break;

                    case "fold":
                    case "unfold":
                        {
                            // 折叠那一层的两个终态。取证不许造数据：板上没有"一组里有的命中、有的不命中"
                            // 的形状就明说——而不是挑一个把整组都筛暗的标签，拍一张看着像折好了的图。
                            var probe = FoldProbe();

                            if (probe is null)
                            {
                                return new ShotReply(false, null,
                                    Error: "板上没有「一组里有的命中、有的不命中」的形状，折不出东西");
                            }

                            _view.TagFilter = probe.Value.Tag;

                            Rebuild();
                            SetGroupExpanded(probe.Value.Group, panelArg is "unfold", animate: false);

                            Diagnostics.Write(
                                $"shot {panelArg}: lens『{probe.Value.Tag}』落在 {probe.Value.Group}，" +
                                $"这一发里折着的格子 {_folded.Count} 枚");

                            break;
                        }

                    default:
                        return new ShotReply(false, null, Error: $"不懂的 panel \"{panelArg}\"，可给 filter / cheat / banner / search / restore / rename / fold / unfold / pair / pull / shrink / scrolled");
                }

                panelsOpened = true;
                savedPanelTagFilter = savedTagFilter;
            }

            // chrome 拍的是整扇窗口：顶上那条标签不在 BoardArea 里，拿板面那档去量它，
            // 只会拍出"图里没有标签"这一条假读数。板面那一档的目标一个字不动——
            // 已有的对比读数（点阵、板底、浮层）都锚在 BoardArea 上。
            FrameworkElement surface = mode == "chrome" ? Root : BoardArea;

            surface.UpdateLayout();

            var bitmap = new RenderTargetBitmap();

            // WinUI's RenderAsync returns nothing: it fills the bitmap, and the pixels come
            // back through GetPixelsAsync. So the PNG is encoded here rather than handed over.
            // The target is BoardArea, not ItemLayer: the parity measurements have to see the
            // layers *under* the notes (dot grid, vignette) and the surface's own fill, and
            // ItemLayer's bounds leave all of that out — that is the defect that made "板底
            // 读回 rgba(0,0,0,0)" and hid S2 from the instrument that was supposed to check it.
            await bitmap.RenderAsync(surface);

            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            {
                return new ShotReply(false, null, Error: "渲染出来是 0×0（这块板上没有可画的面积）");
            }

            var buffer = await bitmap.GetPixelsAsync();
            var pixels = await ReadAllAsync(buffer);
            var frameWidth = bitmap.PixelWidth;
            var frameHeight = bitmap.PixelHeight;

            if (framedBox is { } framed)
            {
                // fit / node still mean "the content, not the viewport".
                (pixels, frameWidth, frameHeight) = CropToFramed(pixels, bitmap.PixelWidth, bitmap.PixelHeight, framed);
            }

            var bytes = await EncodePngAsync(pixels, frameWidth, frameHeight);

            var target = System.IO.Path.IsPathRooted(request.OutPath)
                ? request.OutPath
                : System.IO.Path.Combine(BoardLocator.BoardDirectory(_boardPath), request.OutPath);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes);

            Diagnostics.Write($"shot wrote {target} ({bytes.Length} bytes) at zoom {Percent()} mode {mode}");

            // 取证通道的自身读数：点阵是位图，位图对不对要看"设备像素/布局像素"这一比，
            // 光看 PNG 量出来的间距分不出是 DPI 缩放还是相位被插值糊掉了。
            Diagnostics.Write(
                $"  atmosphere: board {BoardArea.ActualWidth:0.#}×{BoardArea.ActualHeight:0.#} layout, " +
                $"scale {(BoardArea.XamlRoot?.RasterizationScale.ToString() ?? "no XamlRoot")}, " +
                $"frame {frameWidth}×{frameHeight} ratio {(frameWidth / Math.Max(1.0, BoardArea.ActualWidth)):0.####}×{(frameHeight / Math.Max(1.0, BoardArea.ActualHeight)):0.####}, " +
                $"grid {(GridLayer.Source as WriteableBitmap)?.PixelWidth}×{(GridLayer.Source as WriteableBitmap)?.PixelHeight} " +
                $"at left {Canvas.GetLeft(GridLayer):0.###} top {Canvas.GetTop(GridLayer):0.###} " +
                $"size {GridLayer.Width:0.###}×{GridLayer.Height:0.###} visible {GridLayer.Visibility} " +
                $"offset {GridOffset.X:0.###},{GridOffset.Y:0.###} actual {GridLayer.ActualWidth:0.#}×{GridLayer.ActualHeight:0.#})");

            if (mode == "chrome")
            {
                // 这一档的读数必须落在日志里，否则"标签条拍到了"只是我的一句断言。
                Diagnostics.Write(
                    $"  标题栏: 标签 {BoardTabs.TabItems.Count} 枚，选中第 {BoardTabs.SelectedIndex} 枚" +
                    $"（{(BoardTabs.SelectedItem as TabViewItem)?.Header}），" +
                    $"条 {BoardTabs.ActualWidth:0.##}×{BoardTabs.ActualHeight:0.##}，宿主 {TitleBarHost.ActualWidth:0.##}×{TitleBarHost.ActualHeight:0.##}，" +
                    $"右侧内缩 {TitleBarHost.Padding.Right:0.##}，整窗框 {frameWidth}×{frameHeight}");
            }

            return new ShotReply(
                true,
                target,
                frameWidth,
                frameHeight,
                _view.Zoom,
                _document.Nodes.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                  or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Diagnostics.Write($"shot failed: {ex}");
            return new ShotReply(false, null, Error: ex.Message);
        }
        finally
        {
            if (hiddenForShot)
            {
                foreach (var visual in _realized.Values)
                {
                    visual.Visibility = Visibility.Visible;
                }
            }

            if (resampled)
            {
                ClearSelection();

                foreach (var id in savedSelection)
                {
                    _selection.Add(id);
                }

                ProjectSelection();
            }

            if (panelsOpened)
            {
                // 取证不能留下痕迹：面板开过就要按开之前的样子收回去。
                SetFilterPanel(savedFilterPanel);

                if (savedCheatsheet)
                {
                    ShowCheatsheet();
                }
                else
                {
                    CloseCheatsheet();
                }

                if (request.Panel?.Trim() is "banner" or "fold" or "unfold"
                    && !string.Equals(_view.TagFilter, savedPanelTagFilter, StringComparison.Ordinal))
                {
                    _view.TagFilter = savedPanelTagFilter;

                    Rebuild();
                }

                if (!savedSearch)
                {
                    CloseSearchPanel();
                }

                if (!savedRestore)
                {
                    CloseRestorePanel();
                }

                if (savedRename is null && _renamingPath is not null)
                {
                    CancelRename();
                }

                _renamePreview = false;

                if (Pulling)
                {
                    // 取证不许留下一根没落定的线：文档本来也没动过。
                    CancelPull();
                }

                if (_shotShrunk is { } shrunk)
                {
                    // 压小是这一发借去的，不是留下的：格子回到它原来的高度，文档一个字节没写。
                    shrunk.Node.Height = shrunk.Height;
                    _shotShrunk = null;

                    ArrangeAll();
                }
            }

            if (reframed)
            {
                _view.PanX = savedPanX;
                _view.PanY = savedPanY;
                _view.Zoom = savedZoom;

                ArrangeAll();
                ProjectSelection();
            }

            _capturingShot = false;
        }
    }

    private Box? WholeBoardBox()
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        var any = false;

        foreach (var node in _document.Nodes)
        {
            any = true;
            left = Math.Min(left, node.X);
            top = Math.Min(top, node.Y);
            right = Math.Max(right, node.Right);
            bottom = Math.Max(bottom, node.Bottom);
        }

        return any ? new Box(left, top, right - left, bottom - top) : null;
    }

    private Box? NodeBox(string id)
        => NodeOf(id) is { } node ? new Box(node.X, node.Y, node.Width, node.Height) : null;

    /// <summary>
    /// Cut the rendered viewport down to the framed content plus a margin, so <c>fit</c> and
    /// <c>node</c> still return the board rather than the window. The margin has to stay: the
    /// paper shadow lives outside the node rectangle, and a tight crop would cut the very
    /// pixels the parity table measures.
    /// </summary>
    private (byte[] Pixels, int Width, int Height) CropToFramed(byte[] bgra, int frameWidth, int frameHeight, Box box)
    {
        var device = frameWidth / Math.Max(1.0, BoardArea.ActualWidth);
        const double margin = 48;

        var left = (((box.X * _view.Zoom) + _view.PanX) - margin) * device;
        var top = (((box.Y * _view.Zoom) + _view.PanY) - margin) * device;
        var right = (((box.Right * _view.Zoom) + _view.PanX) + margin) * device;
        var bottom = (((box.Bottom * _view.Zoom) + _view.PanY) + margin) * device;

        var x0 = Math.Clamp((int)Math.Floor(left), 0, frameWidth - 1);
        var y0 = Math.Clamp((int)Math.Floor(top), 0, frameHeight - 1);
        var x1 = Math.Clamp((int)Math.Ceiling(right), x0 + 1, frameWidth);
        var y1 = Math.Clamp((int)Math.Ceiling(bottom), y0 + 1, frameHeight);

        var width = x1 - x0;
        var height = y1 - y0;
        var cropped = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(bgra, ((y0 + y) * frameWidth) * 4 + (x0 * 4), cropped, (y * width) * 4, width * 4);
        }

        return (cropped, width, height);
    }

    /// <summary>
    /// Frames a world rectangle in the middle of the surface. The zoom is clamped, so a
    /// board larger than the clamp allows comes back cropped — and the reply carries the
    /// zoom it used, so a crop is visible rather than implied.
    /// </summary>
    private void Frame(Box box)
    {
        var width = BoardArea.ActualWidth;
        var height = BoardArea.ActualHeight;

        if (width <= 0 || height <= 0 || box.Width <= 0 || box.Height <= 0)
        {
            _view.Zoom = 1;
            return;
        }

        const double margin = 48;

        var zoom = Math.Clamp(
            Math.Min((width - (2 * margin)) / box.Width, (height - (2 * margin)) / box.Height),
            MinZoom,
            MaxZoom);

        _view.Zoom = zoom;
        _view.PanX = (width / 2) - (box.CenterX * zoom);
        _view.PanY = (height / 2) - (box.CenterY * zoom);

        ArrangeAll();
    }

    private static Task<byte[]> ReadAllAsync(IBuffer buffer) => Task.FromResult(buffer.ToArray());

    private static async Task<byte[]> EncodePngAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            bgra);

        await encoder.FlushAsync();

        return await ReadAllAsync(stream.GetInputStreamAt(0), (uint)stream.Size);
    }

    private static async Task<byte[]> ReadAllAsync(InMemoryRandomAccessStream stream)
        => await ReadAllAsync(stream.GetInputStreamAt(0), (uint)stream.Size);

    private static async Task<byte[]> ReadAllAsync(IInputStream input, uint length)
    {
        using var reader = new DataReader(input);

        await reader.LoadAsync(length);

        var bytes = new byte[length];
        reader.ReadBytes(bytes);

        return bytes;
    }

    // -------------------------------------------------------------------------
    // Keyboard
    // -------------------------------------------------------------------------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // While a note is being edited the TextBox owns the keyboard: Ctrl+A and Ctrl+V
        // must work inside the text, not on the board behind it. Escape and the commit
        // keys are handled on the editor itself.
        if (_editingId is not null)
        {
            return;
        }

        // 查找那一格持键盘时板子的键位一律不进（不变量 1）：那一格里打进去的每个字都该是查询串，
        // 而不是"Ctrl+A 把整块板子选中"。它自己的 Enter/Esc/F3 在输入框上处理。
        if (_searchOpen)
        {
            // 焦点不在输入框上时（点了右下角那一组再按 Esc）这一层也得关得掉——
            // 一按退一层，不许留一层没人持键盘却还挂在板上。
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseSearchPanel();
                e.Handled = true;
            }

            return;
        }

        // 任何一格 TextBox 持键盘时，打进去的字归它，不归板子。改名那一格就在标签条里，
        // 它没有自己的面板可判，所以这里按焦点类型放行——这也是其余输入框共用的不变量。
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox)
        {
            return;
        }

        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

        var alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

        var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

        // One dispatch for the whole table: the chord is rebuilt in the same order the table writes
        // them ("Ctrl+Shift+G"), so a command cannot be reachable from the menu but not the keyboard.
        var chord = $"{(control ? "Ctrl+" : string.Empty)}{(shift ? "Shift+" : string.Empty)}{(alt ? "Alt+" : string.Empty)}{e.Key}";

        foreach (var command in Commands())
        {
            if (!command.Dispatched || !string.Equals(command.Chord, chord, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            command.Run();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                if (Pulling)
                {
                    // 一按退一层：正在拉的那根线先断掉，板子上什么都没留下。
                    CancelPull();
                    Say("那根线没收了");
                    e.Handled = true;
                    break;
                }

                if (CollapseExpandedGroup())
                {
                    // 摊开的那一层也算一层：Esc 先把它折回去，再管面板与选中。
                    e.Handled = true;
                    break;
                }

                if (_filterPanelOpen)
                {
                    // Esc 先收起眼前这层面板，再管选中：一按退一层，和它在网页里一样。
                    CloseFilterPanel();
                    e.Handled = true;
                    break;
                }

                if (_restoreOpen)
                {
                    CloseRestorePanel();
                    e.Handled = true;
                    break;
                }

                ClearSelection();
                ProjectSelection();
                e.Handled = true;
                break;

            // 方向键＝微调选中的格子（1 世界像素，Shift 是 10 倍）。带 Ctrl 的那一发留给以后的
            // "跳到邻格"，这里先不占——不占也比占错了强。
            case Windows.System.VirtualKey.Up when !control:
                Nudge(0, -(shift ? NudgePx * 10 : NudgePx));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down when !control:
                Nudge(0, shift ? NudgePx * 10 : NudgePx);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Left when !control:
                Nudge(-(shift ? NudgePx * 10 : NudgePx), 0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right when !control:
                Nudge(shift ? NudgePx * 10 : NudgePx, 0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number1 when shift:
                ZoomToFit();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number2 when shift:
                ZoomToSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.A when control:
                foreach (var node in _document.Nodes)
                {
                    _selection.Add(node.Id);
                }

                ProjectSelection();
                Say($"选中 {_selection.Count} 个节点 · 缩放 {Percent()}");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.V when control:
                _ = PasteFromClipboardAsync();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Z when control:
                Undo();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Y when control:
                Redo();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Delete:
                RemoveSelection();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number0 when control:
                ResetView();
                e.Handled = true;
                break;
        }
    }
}

internal sealed record NodeGesture(
    double StartPointerX,
    double StartPointerY,
    Dictionary<string, (double X, double Y)> StartGeometry,
    string PrimaryId,
    bool GroupMode = false,
    string? StartGroupId = null,
    IReadOnlyList<string>? StartMembers = null);

internal sealed record ResizeGesture(
    string Direction,
    Rect StartBox,
    Dictionary<string, (CanvasNode Node, double X, double Y, double W, double H)> Start,
    double StartWorldX,
    double StartWorldY);
