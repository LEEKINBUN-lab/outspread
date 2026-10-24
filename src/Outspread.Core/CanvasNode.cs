using System.Globalization;
using System.Text.Json.Nodes;

namespace Outspread.Core;

public enum CanvasNodeKind
{
    Text,
    File,
    Link,
    Group,
}

/// <summary>
/// A live view over one node object inside a <see cref="CanvasDocument.Root"/>.
/// </summary>
/// <remarks>
/// Every getter and setter touches the document's own <see cref="JsonObject"/>
/// instead of a copy. Round-tripping through a typed model would silently drop
/// the properties the 1.0 spec does not cover, which is exactly how an agent's
/// work gets destroyed by an editor that does not know about it.
/// </remarks>
public sealed class CanvasNode
{
    internal CanvasNode(JsonObject json)
    {
        Json = json;
    }

    /// <summary>
    /// The node's own JSON. Public on purpose: this is the only way to reach a
    /// property the board does not model, and reaching it must not require a
    /// rewrite of the document.
    /// </summary>
    public JsonObject Json { get; }

    public string Id
    {
        get => GetString("id") ?? string.Empty;
        set => SetString("id", value);
    }

    public CanvasNodeKind Kind
    {
        get => TryParseKind(GetString("type"), out var kind) ? kind : CanvasNodeKind.Text;
        set => SetString("type", NameOfKind(value));
    }

    public double X
    {
        get => GetNumber("x") ?? 0;
        set => SetNumber("x", value);
    }

    public double Y
    {
        get => GetNumber("y") ?? 0;
        set => SetNumber("y", value);
    }

    public double Width
    {
        get => GetNumber("width") ?? 0;
        set => SetNumber("width", value);
    }

    public double Height
    {
        get => GetNumber("height") ?? 0;
        set => SetNumber("height", value);
    }

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    /// <summary>Hex <c>#RRGGBB</c> or a preset <c>"1"</c> to <c>"6"</c>, per spec.</summary>
    public string? Color
    {
        get => GetString("color");
        set => SetString("color", value);
    }

    /// <summary>Body of a <see cref="CanvasNodeKind.Text"/> node.</summary>
    public string? Text
    {
        get => GetString("text");
        set => SetString("text", value);
    }

    /// <summary>Relative path of a <see cref="CanvasNodeKind.File"/> node.</summary>
    public string? FilePath
    {
        get => GetString("file");
        set => SetString("file", value);
    }

    /// <summary>Target of a <see cref="CanvasNodeKind.Link"/> node.</summary>
    public string? Url
    {
        get => GetString("url");
        set => SetString("url", value);
    }

    /// <summary>Caption of a <see cref="CanvasNodeKind.Group"/> node.</summary>
    public string? Label
    {
        get => GetString("label");
        set => SetString("label", value);
    }

    /// <summary>
    /// 这个组框里到底是哪几格——**显式成员名单**，写在 group 节点的 <c>members</c> 数组里。
    /// null＝这张表不在（别的工具写的板子、10-24 之前存的板子），那时才退回几何包含。
    /// </summary>
    /// <remarks>
    /// 规范里没有这个字段（1.0 的 group 只定义 <c>label/background/backgroundStyle</c>），而且规范
    /// 对未知字段没有任何前向兼容承诺——这两条本仓早查过，写在 docs/canvas-format.md。所以这一格
    /// 明说三件事：读的时候没表就退回几何；被不认识它的程序重写一遍，最坏是退回旧语义而不是丢内容；
    /// 要它的代价是一份别的工具看不懂的字段。换来的是他 10-24 点的那两格 bug 一起消失——
    /// 几何当成员表会让"新建在框里"当场入组、让"拖走一格"把框拉长到吞掉路上别人。
    /// 成员是一张名单，不是一块场地：那是他 10-22 那句话的下一步。
    /// </remarks>
    public IReadOnlyList<string>? GroupMemberIds
    {
        get
        {
            if (Json["members"] is not JsonArray array)
            {
                return null;
            }

            var ids = new List<string>();

            foreach (var item in array)
            {
                if (item is JsonValue value && value.TryGetValue(out string? id) && !string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    /// <summary>写下这张名单；null＝删掉它，让这个组退回几何那一档。</summary>
    public void SetGroupMembers(IReadOnlyList<string>? ids)
    {
        if (ids is null)
        {
            Json.Remove("members");

            return;
        }

        var array = new JsonArray();

        foreach (var id in ids)
        {
            array.Add(JsonValue.Create(id));
        }

        Json["members"] = array;
    }

    public string? Background
    {
        get => GetString("background");
        set => SetString("background", value);
    }

    public string? BackgroundStyle
    {
        get => GetString("backgroundStyle");
        set => SetString("backgroundStyle", value);
    }

    public bool HasProperty(string name) => Json.ContainsKey(name);

    public string? GetString(string name)
    {
        var node = Json[name];
        return node is null ? null : node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToString();
    }

    public double? GetNumber(string name)
    {
        var node = Json[name];

        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException or FormatException)
        {
            // A number written by this process may be backed by Int64 rather
            // than a JsonElement, and GetValue<double> refuses that even though
            // the value is perfectly readable.
            try
            {
                return Convert.ToDouble(node.GetValue<object>(), CultureInfo.InvariantCulture);
            }
            catch (Exception inner) when (inner is InvalidOperationException or InvalidCastException or FormatException)
            {
                return null;
            }
        }
    }

    public void SetString(string name, string? value)
    {
        if (value is null)
        {
            Json.Remove(name);
        }
        else
        {
            Json[name] = value;
        }
    }

    /// <summary>
    /// Geometry is stored as a whole number: the spec describes pixel
    /// coordinates, and it keeps a drag from rewriting a line as
    /// <c>"x": 412.00000000003</c> in a diff.
    /// </summary>
    public void SetNumber(string name, double? value)
    {
        if (value is null)
        {
            Json.Remove(name);
            return;
        }

        Json[name] = (long)Math.Round(value.Value, MidpointRounding.AwayFromZero);
    }

    public static bool TryParseKind(string? name, out CanvasNodeKind kind)
    {
        kind = name switch
        {
            "text" => CanvasNodeKind.Text,
            "file" => CanvasNodeKind.File,
            "link" => CanvasNodeKind.Link,
            "group" => CanvasNodeKind.Group,
            _ => CanvasNodeKind.Text,
        };

        return name is ("text" or "file" or "link" or "group");
    }

    public static string NameOfKind(CanvasNodeKind kind) => kind switch
    {
        CanvasNodeKind.Text => "text",
        CanvasNodeKind.File => "file",
        CanvasNodeKind.Link => "link",
        CanvasNodeKind.Group => "group",
        _ => "text",
    };
}
