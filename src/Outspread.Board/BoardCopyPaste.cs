using System.Text.Json.Nodes;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 板子自己的复制与粘贴（Ctrl+C ↔ Ctrl+V 里"我们那式"的那一半）。
/// </summary>
/// <remarks>
/// <para>
/// 贴进来时排布照原样搬：整份片段的左上角落在粘贴点上，格子之间的相对位置一个像素都不动。
/// 不按"原来的坐标"贴是因为那块板子的相机是另一块板子的相机——照坐标贴会贴到看不见的地方去，
/// 而"我明明贴了怎么什么都没有"是这一发最坏的失败样子。
/// </para>
/// <para>
/// 图那张格子带的是一句相对 <c>assets/…</c>，字节还在**原来那块板**旁边。所以贴的时候去那儿取
/// 一次，取到了就落进这块板自己的 <c>assets/</c>（图片按归属板解析，不搬就是一句指着别人家的路径）；
/// 取不到也照样贴——那一格会自己写"未找到"，回执里说明几张没跟过来。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private async Task CopySelectionAsync()
    {
        if (await CopySelectionCoreAsync() is not { } count)
        {
            return;
        }

        Say($"已复制 {count} 格｜Ctrl+V 贴到别的板子上也在，图跟着搬");
    }

    /// <summary>把选中的放进剪贴板；返回放进去的格数，失败返回 null（回执已经说过了）。</summary>
    private async Task<int?> CopySelectionCoreAsync()
    {
        var notes = SelectedNotes();

        if (notes.Count == 0)
        {
            Say("没有选中任何格子");

            return null;
        }

        var fragment = BoardFragment.From(
            _document,
            notes.ConvertAll(node => node.Id),
            _boardPath ?? string.Empty,
            out var plainText);

        if (fragment is null)
        {
            Say("选中的格子拆不成片段（详情在日志）");
            Diagnostics.Write("copy: nothing to take apart — selection matched no node");

            return null;
        }

        var problem = await ClipboardIngest.PutFragmentAsync(fragment, plainText);

        Diagnostics.Write($"copy: {notes.Count} 格、片段 {fragment.Length} 字、给外面的文字 {plainText.Length} 字"
            + (problem is null ? string.Empty : $"（没放上去：{problem}）"));

        if (problem is not null)
        {
            Say($"复制没成：{problem}");

            return null;
        }

        return notes.Count;
    }

    /// <summary>剪切＝先复制，复制成了才删；不成就原样留着（不留"删了却没进剪贴板"的路）。</summary>
    private async Task CutSelectionAsync()
    {
        var notes = SelectedNotes();

        if (notes.Count == 0)
        {
            Say("没有选中任何格子");

            return;
        }

        if (await CopySelectionCoreAsync() is null)
        {
            return;
        }

        BeginGesture($"剪切 {notes.Count} 格");

        foreach (var node in notes)
        {
            _document.Remove(node.Id);
        }

        ClearSelection();
        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"已剪切 {notes.Count} 格｜Ctrl+V 贴回来");
    }

    /// <summary>
    /// 剪贴板上是我们那式片段就把它贴进来。
    /// </summary>
    /// <returns>假＝那不是片段（或读不出），调用方去走文字／图片／文件那三条老路。</returns>
    /// <returns>假＝那不是片段（或读不出），调用方去走文字／图片／文件那三条老路。</returns>
    private async Task<bool> TryPasteFragmentAsync()
    {
        var fragment = await ClipboardIngest.ReadFragmentAsync();

        return BoardFragment.LooksLikeFragment(fragment) && PasteFragment(fragment!);
    }

    /// <summary>
    /// 把一份片段落到这块板上。同步、不碰剪贴板——所以界面自检能直接量它（拿一段自己造的片段
    /// 贴进当前这块板，看落下来的是不是那几格）。
    /// </summary>
    private bool PasteFragment(string fragment)
    {
        var nodes = BoardFragment.NodesOf(fragment);

        if (nodes.Count == 0)
        {
            return false;
        }

        var source = BoardFragment.SourceBoardOf(fragment);
        var left = nodes.Min(node => Number(node, "x"));
        var top = nodes.Min(node => Number(node, "y"));
        var anchor = PasteWorld();
        var dx = anchor.X - left;
        var dy = anchor.Y - top;
        var intoUnion = IsAllNotes(_boardPath);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var pasted = new List<string>();
        var carried = 0;
        var lost = 0;

        BeginGesture($"贴了 {nodes.Count} 格");

        foreach (var raw in nodes)
        {
            var copy = raw.DeepClone().AsObject();
            var oldId = Text(copy, "id") ?? string.Empty;

            copy["x"] = Number(copy, "x") + dx;
            copy["y"] = Number(copy, "y") + dy;

            if (intoUnion)
            {
                // 并集里每格的名字要带它归属那块板：留着原 id 会跟另一块板的同名格撞在一起。
                copy.Remove("id");
            }

            if (Text(copy, "type") == "file" && Text(copy, "file") is { Length: > 0 } relative)
            {
                var moved = CarryPicture(copy, relative, source);

                if (moved < 0)
                {
                    lost++;
                }
                else
                {
                    carried += moved;
                }
            }

            var node = _document.AppendNode(copy);

            if (intoUnion)
            {
                node.Id = BoardMerge.MergeId(UnfiledPath, node.Id);
            }

            if (oldId.Length > 0)
            {
                ids[oldId] = node.Id;
            }

            pasted.Add(node.Id);
        }

        // 组归属与线头都指着旧名字：不重写，贴过去的就是一堆指着不存在的格子的引用。
        foreach (var id in pasted)
        {
            if (_document.Find(id) is not { } member || member.GetString("group") is not { } group)
            {
                continue;
            }

            if (ids.TryGetValue(group, out var mapped))
            {
                member.SetString("group", mapped);
            }
        }

        var edges = 0;

        foreach (var edge in BoardFragment.EdgesOf(fragment, ids))
        {
            var copy = edge.DeepClone().AsObject();

            copy["id"] = NodeIds.NewAppId();
            copy["fromNode"] = ids[Text(copy, "fromNode") ?? string.Empty];
            copy["toNode"] = ids[Text(copy, "toNode") ?? string.Empty];

            _document.AppendEdge(copy);

            edges++;
        }

        EndGesture();
        Rebuild();
        SaveSoon();

        ClearSelection();

        foreach (var id in pasted)
        {
            _selection.Add(id);
        }

        ProjectSelection();

        Diagnostics.Write($"paste 片段: {pasted.Count} 格、{edges} 根线、图跟来 {Math.Max(carried, 0)} 张"
            + (lost == 0 ? string.Empty : $"、{lost} 张没跟来（源板 {source ?? "没写"}）"));

        Say(lost == 0
            ? $"贴了 {pasted.Count} 格" + (carried > 0 ? $"（图 {carried} 张一起搬过来）" : string.Empty)
            : $"贴了 {pasted.Count} 格｜{lost} 张图没跟来（原来那块板上的文件读不到）");

        return true;
    }

    /// <summary>
    /// 把一句相对 <c>assets/…</c> 从源板旁边搬到这块板旁边，并把节点上那句路径改成新名字。
    /// </summary>
    /// <returns>搬来几张：0＝这张本来就在这儿或根本没搬，-1＝源文件读不到（节点照贴，那一格自己会说）。</returns>
    private int CarryPicture(JsonObject copy, string relative, string? sourceBoard)
    {
        var boardForAsset = IsAllNotes(_boardPath) ? UnfiledPath : _boardPath ?? string.Empty;
        var from = sourceBoard is { Length: > 0 }
            ? Path.Combine(AssetStore.BoardDirectory(sourceBoard), relative.Replace('/', Path.DirectorySeparatorChar))
            : null;

        if (from is null || !File.Exists(from))
        {
            Diagnostics.Write($"paste 图没处可取：{relative}（源板 {sourceBoard ?? "没写"}）");

            return -1;
        }

        try
        {
            var bytes = File.ReadAllBytes(from);
            var written = AssetStore.WriteImage(
                boardForAsset,
                bytes,
                Path.GetExtension(from),
                out var alreadyThere);

            copy["file"] = written;

            return alreadyThere ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Write($"paste 图没搬动 {from}: {ex.Message}");

            return -1;
        }
    }

    /// <summary>
    /// 贴一份片段，量它落下来的是什么。走的是真那一条路（不是模拟）：一份手造的片段、
    /// <see cref="PasteFragment"/>、然后核文档本身。
    /// </summary>
    /// <remarks>
    /// 为什么要贴**两次**：第一次的 id 在这块板上还空着，保留原名是对的；第二次撞上了自己，
    /// 必须另铸一套——而组归属与线头要跟着换到新那套上。只贴一次量不到这一格，也正是
    /// "复制粘贴"最容易做错的那一格（一份片段贴两遍，第二遍指着第一遍的格子）。
    /// </remarks>
    private void SelfTestCopyPaste(List<string> problems)
    {
        var lf = (char)92 + "n";

        var fragment = "{"
            + "\"nodes\":["
            + "{\"id\":\"usrc1\",\"type\":\"group\",\"x\":0,\"y\":0,\"width\":400,\"height\":400,\"label\":\"一组\"},"
            + "{\"id\":\"usrc2\",\"type\":\"text\",\"text\":\"贴进来的第一格\",\"x\":20,\"y\":20,\"width\":200,\"height\":80,\"color\":\"4\",\"group\":\"usrc1\"},"
            + "{\"id\":\"usrc3\",\"type\":\"text\",\"text\":\"贴进来的第二格\",\"x\":120,\"y\":260,\"width\":200,\"height\":80,\"group\":\"usrc1\"},"
            + "{\"id\":\"usrc4\",\"type\":\"link\",\"url\":\"https://jsoncanvas.org\",\"label\":\"一条线\",\"x\":40,\"y\":440,\"width\":200,\"height\":60}"
            + "],"
            + "\"edges\":[{\"id\":\"uesrc1\",\"fromNode\":\"usrc2\",\"fromSide\":\"bottom\",\"toNode\":\"usrc3\",\"toSide\":\"top\"}],"
            + "\"plainText\":\"贴进来的第一格" + lf + "贴进来的第二格" + lf + "一条线\""
            + "}";

        var nodesBefore = _document.Nodes.Count;
        var edgesBefore = _document.Edges.Count;

        if (!PasteFragment(fragment))
        {
            problems.Add("自检复制粘贴：一份合法的片段没贴出来（等于这发直接吞了）");

            return;
        }

        var first = _selection.ToList();

        PasteFragment(fragment);

        var second = _selection.ToList();

        void Check(string label, bool held)
        {
            if (!held)
            {
                problems.Add($"自检复制粘贴：{label}");
            }
        }

        Check("四格没全落下来", _document.Nodes.Count == nodesBefore + 8);
        Check("那根线没跟过来", _document.Edges.Count == edgesBefore + 2);
        Check("两遍贴的是同一批 id（第二遍该另铸一套）", !second.Intersect(first).Any());
        Check("贴进来的那格颜色丢了", _document.Nodes.Count(node => node.Text == "贴进来的第一格" && node.Color == "4") == 2);

        // 组与线头都要指着**这一遍**贴出来的那些格子。
        foreach (var batch in new[] { first, second })
        {
            var grouped = batch
                .Select(id => _document.Find(id))
                .Count(node => node is not null && batch.Contains(node.GetString("group") ?? string.Empty));

            Check("组归属没跟着换到这一批", grouped == 2);

            var touching = _document.Edges.Where(edge => batch.Contains(edge.FromNode) || batch.Contains(edge.ToNode)).ToList();

            Check("线头一头在这一批里、另一头在外面",
                touching.Count == 1 && batch.Contains(touching[0].FromNode) && batch.Contains(touching[0].ToNode));
        }

        var anchor = PasteWorld();
        var groups = second.Select(_document.Find).Where(node => node is not null && node.Kind == CanvasNodeKind.Group).ToList();

        Check("整份片段没落在粘贴点上", groups.Count == 1
            && Math.Abs(groups[0]!.X - anchor.X) <= 1
            && Math.Abs(groups[0]!.Y - anchor.Y) <= 1);

        Undo();
        Undo();

        Check("两发撤销没把板子交回去", _document.Nodes.Count == nodesBefore && _document.Edges.Count == edgesBefore);

        Diagnostics.Write(
            $"自检复制粘贴: 一份片段贴两遍＝两套名字（{first.Count}+{second.Count} 格、组与线头各自跟着换）、撤销交回原样");
    }

    private static double Number(JsonObject obj, string name)    {
        if (!obj.TryGetPropertyValue(name, out var value) || value is null)
        {
            return 0;
        }

        try
        {
            return value.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or FormatException)
        {
            try
            {
                return Convert.ToDouble(value.GetValue<object>(), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception inner) when (inner is InvalidOperationException or InvalidCastException or FormatException)
            {
                return 0;
            }
        }
    }

    private static string? Text(JsonObject? obj, string name)
        => obj is not null
            && obj.TryGetPropertyValue(name, out var value)
            && value?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : null;
}
