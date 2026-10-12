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

    public double PanX { get; set; } = 24;

    public double PanY { get; set; } = 16;

    public double Zoom { get; set; } = 1;

    /// <summary>随文字 or 固定 — whether editing a note also resizes it. Program state, not document
    /// content, so it lives in the sidecar like the camera does.</summary>
    public NoteSizeMode NoteSize { get; set; } = NoteSizeMode.Automatic;

    public IReadOnlyDictionary<string, double> FontSizes => _fontSizes;

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

    public bool Filtering => TagFilter is { Length: > 0 } || DueFilter is not null;

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

        if (_fontSizes.Count > 0)
        {
            var fonts = new JsonObject();

            foreach (var (id, size) in _fontSizes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                fonts[id] = size;
            }

            root["fontSize"] = fonts;
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
