using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// The board's own state: where the camera is, and how large each text node's
/// letters are in world units.
/// </summary>
/// <remarks>
/// This lives in a sidecar rather than inside the <c>.canvas</c> file because
/// JSON Canvas 1.0 has no place for either, and inventing top-level keys would put
/// this program's convenience ahead of the format's compatibility. An agent that
/// reads the board therefore reads only what the format describes.
///
/// Losing this file costs a camera position, nothing else, so it is written
/// best-effort and never blocks a board save.
/// </remarks>
public sealed class BoardViewState
{
    /// <summary>The size a note whose own size was never changed is set in: 15 world units, with
    /// its leading and padding carried along by NoteTypography so the three cannot drift apart.</summary>
    public const double DefaultFontSize = NoteTypography.DefaultSize;

    private readonly Dictionary<string, double> _fontSizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manualSizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hiddenRailButtons = new(StringComparer.Ordinal);

    public double PanX { get; set; } = 24;

    public double PanY { get; set; } = 16;

    public double Zoom { get; set; } = 1;

    /// <summary>随文字 or 固定 — whether editing a note also resizes it. Program state, not document
    /// content, so it lives in the sidecar like the camera does.</summary>
    public NoteSizeMode NoteSize { get; set; } = NoteSizeMode.Automatic;

    /// <summary>
    /// 这台设备要不要把这块板同步上远端。null＝没表过态（同步，与既有板一致）；false＝标记不同步——
    /// 性能/回归那种几百格的测试板用它挡在远端仓外（2026-10-31 他拍的：agent 测试板不许污染仓库）。
    /// 设备本地的态，不进 .canvas、不进同步契约：一块板完全可以"这台不推、那台推"。
    /// </summary>
    public bool? SyncEnabled { get; set; }

    public IReadOnlyDictionary<string, double> FontSizes => _fontSizes;

    /// <summary>
    /// 这一格被用户手动拖过大小。自动高度只改没有这个标记的格子；手动定过的格子保持人给的框。
    /// </summary>
    public bool IsManualSize(string id) => _manualSizes.Contains(id);

    /// <summary>被钉住尺寸的格子（外部工具归档时写进来的，载入/并入时要跟着认）。</summary>
    public IReadOnlyCollection<string> ManualSizeIds => _manualSizes;

    public void MarkManualSize(string id) => _manualSizes.Add(id);

    /// <summary>把"手动定过大小"的标记摘掉（只在确认那几像素是误触时用）。</summary>
    public void UnmarkManualSize(string id) => _manualSizes.Remove(id);

    /// <summary>左侧操作条上被用户关掉的钮（group / color / delete）。版面状态，随面板存。</summary>
    public bool IsRailButtonHidden(string name) => _hiddenRailButtons.Contains(name);

    public void SetRailButtonHidden(string name, bool hidden)
    {
        if (hidden)
        {
            _hiddenRailButtons.Add(name);
        }
        else
        {
            _hiddenRailButtons.Remove(name);
        }
    }

    /// <summary>
    /// Today, as far as the marks care. A property rather than a call to <c>DateTime.Now</c> so the
    /// due arithmetic is testable and so a board left open past midnight can be told to re-evaluate.
    /// </summary>
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// View-only filters. Deliberately <b>not</b> in the sidecar: a filter that survived a restart
    /// would hide notes the user cannot see why they are dim, and one that survived an undo would
    /// make the document look changed when only the lens moved.
    /// </summary>
    public string? TagFilter { get; set; }

    public NoteMarks.DueState? DueFilter { get; set; }

    /// <summary>完成状态那一档：null＝不筛、true＝只看已完成、false＝只看还没做完的（含没写 @b 的）。</summary>
    public bool? DoneFilter { get; set; }

    public bool Filtering => TagFilter is { Length: > 0 } || DueFilter is not null || DoneFilter is not null;

    public double ToScreenX(double worldX) => (worldX * Zoom) + PanX;

    public double ToScreenY(double worldY) => (worldY * Zoom) + PanY;

    public double ToWorldX(double screenX) => (screenX - PanX) / Zoom;

    public double ToWorldY(double screenY) => (screenY - PanY) / Zoom;

    public void PanBy(double screenDeltaX, double screenDeltaY)
    {
        PanX += screenDeltaX;
        PanY += screenDeltaY;
    }

    /// <summary>The world-space rectangle currently under a surface of the given size.</summary>
    public (double X, double Y, double Width, double Height) WorldViewport(
        double surfaceWidth,
        double surfaceHeight)
    {
        var x = ToWorldX(0);
        var y = ToWorldY(0);

        return (x, y, surfaceWidth / Zoom, surfaceHeight / Zoom);
    }

    /// <summary>
    /// Zooms by <paramref name="factor"/>, keeping the world point under
    /// <paramref name="screenX"/>/<paramref name="screenY"/> under that pixel.
    /// </summary>
    /// <returns>False when the clamp left the view untouched, so callers can skip work.</returns>
    public bool ZoomAt(double screenX, double screenY, double factor, double minZoom, double maxZoom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minZoom, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minZoom, maxZoom);

        var worldX = ToWorldX(screenX);
        var worldY = ToWorldY(screenY);

        var next = Math.Clamp(Zoom * factor, minZoom, maxZoom);

        if (Math.Abs(next - Zoom) < double.Epsilon)
        {
            return false;
        }

        Zoom = next;

        // Solving the fixed-point condition: worldX * next + pan' must equal screenX.
        PanX = screenX - (worldX * next);
        PanY = screenY - (worldY * next);

        return true;
    }

    public double FontSizeFor(string id, double fallback = DefaultFontSize)
        => _fontSizes.TryGetValue(id, out var size) ? size : fallback;

    public void SetFontSize(string id, double size)
    {
        _fontSizes[id] = Math.Round(Math.Clamp(size, 6, 400), 1);
    }

    /// <summary>Drops entries for nodes that no longer exist. Returns how many went.</summary>
    public int Prune(IEnumerable<string> liveIds)
    {
        var live = new HashSet<string>(liveIds, StringComparer.Ordinal);
        var dropped = 0;

        foreach (var id in _fontSizes.Keys.ToList())
        {
            if (!live.Contains(id))
            {
                _fontSizes.Remove(id);
                dropped++;
            }
        }

        foreach (var id in _manualSizes.ToList())
        {
            if (!live.Contains(id))
            {
                _manualSizes.Remove(id);
                dropped++;
            }
        }

        return dropped;
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["panX"] = PanX,
            ["panY"] = PanY,
            ["zoom"] = Zoom,
            ["noteSize"] = NoteSize == NoteSizeMode.Fixed ? "fixed" : "auto",
        };

        // 只在表过态时写：没这枚字段的板就是同步的，旧侧车一个字节不用动。
        if (SyncEnabled is { } sync)
        {
            root["sync"] = sync;
        }

        if (_fontSizes.Count > 0)
        {
            var fonts = new JsonObject();

            foreach (var (id, size) in _fontSizes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                fonts[id] = size;
            }

            root["fontSize"] = fonts;
        }

        if (_manualSizes.Count > 0)
        {
            var manual = new JsonArray();

            foreach (var id in _manualSizes.OrderBy(id => id, StringComparer.Ordinal))
            {
                manual.Add(id);
            }

            root["manualSize"] = manual;
        }

        if (_hiddenRailButtons.Count > 0)
        {
            var hidden = new JsonArray();

            foreach (var name in _hiddenRailButtons.OrderBy(name => name, StringComparer.Ordinal))
            {
                hidden.Add(name);
            }

            root["railHidden"] = hidden;
        }

        var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            IndentSize = 2,
        }))
        {
            root.WriteTo(writer);
        }

        var text = JsonText.NormalizeNewLines(Encoding.UTF8.GetString(stream.ToArray()));

        return text.EndsWith('\n') ? text : text + "\n";
    }

    public static BoardViewState Parse(string json)
    {
        var state = new BoardViewState();

        if (JsonNode.Parse(json) is not JsonObject root)
        {
            throw new JsonException("sidecar root is not an object");
        }

        state.PanX = Number(root["panX"], state.PanX);
        state.PanY = Number(root["panY"], state.PanY);
        state.Zoom = Math.Max(0.02, Number(root["zoom"], state.Zoom));

        // A noteSize that is not the word we wrote is read as the shipped default instead of
        // reported. The camera numbers above are the part a broken sidecar really damages, and a
        // junk size flag must not take the camera down with it.
        if (root["noteSize"] is JsonValue sizeNode && sizeNode.TryGetValue(out string? sizeWord))
        {
            state.NoteSize = sizeWord == "fixed" ? NoteSizeMode.Fixed : NoteSizeMode.Automatic;
        }

        // 读不懂当没表过态（同步）：这枚字段挡的是测试板上仓，不能反过来把真板的同步弄哑。
        if (root["sync"] is JsonValue syncNode)
        {
            try
            {
                if (syncNode.TryGetValue(out bool sync))
                {
                    state.SyncEnabled = sync;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
            }
        }

        if (root["fontSize"] is JsonObject fonts)
        {
            foreach (var (id, value) in fonts)
            {
                var size = Number(value, null);

                if (size is not null)
                {
                    state._fontSizes[id] = size.Value;
                }
            }
        }

        if (root["manualSize"] is JsonArray manual)
        {
            foreach (var item in manual)
            {
                if (item is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                {
                    state._manualSizes.Add(id);
                }
            }
        }

        if (root["railHidden"] is JsonArray hiddenRail)
        {
            foreach (var item in hiddenRail)
            {
                if (item is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrWhiteSpace(name))
                {
                    state._hiddenRailButtons.Add(name);
                }
            }
        }

        return state;
    }

    /// <summary>
    /// Reads the sidecar, or explains why it could not. A broken camera file is a
    /// reported condition, not a silent reset to the default view.
    /// </summary>
    public static (BoardViewState State, string? Problem) TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return (new BoardViewState(), null);
        }

        try
        {
            return (Parse(CanvasStore.ReadAllText(path)), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                   or CanvasDocumentException or InvalidOperationException or FormatException)
        {
            return (new BoardViewState(), $"{path}: {ex.Message}");
        }
    }

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported by the caller through its own log; the board file is the part
            // that must not fail.
        }
    }

    private static double Number(JsonNode? node, double fallback)
        => Number(node, (double?)fallback) ?? fallback;

    private static double? Number(JsonNode? node, double? fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or FormatException)
        {
            return fallback;
        }
    }
}
