using System.Text.Json;
using System.Text.Json.Nodes;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// 「所有便签」——一枚固定、关不掉的标签，展示所有已打开标签页 + 一个"未归档"文件里格子的并集。
/// </summary>
/// <remarks>
/// <para>
/// 它不是一块真实的板：用一个哨兵"路径" <see cref="AllNotesKey"/> 顶替 _boardPath，选中它时把各
/// 子文件用 <see cref="BoardMerge"/> 并成一张内存文档，存盘时再按归属拆回各文件写。这样
/// "子页新建→父集可见、父集新建→落未归档、父集删→源也消失"都成立，而每块真实板仍是各自的文件、
/// 各自同步（父集只是投影，没有自己的远端文件）。
/// </para>
/// <para>
/// 并集里每格用它在自己文件里的真实坐标（<see cref="BoardMerge"/> 不动坐标），所以排布每次打开
/// 都一样，除非某格被删——这正是他要的稳定性。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>顶替 _boardPath 的哨兵；尖括号不是合法路径字符，绝不会和真文件撞。</summary>
    public const string AllNotesKey = "<<所有便签>>";

    private static bool IsAllNotes(string? path) => path == AllNotesKey;

    /// <summary>父集里"不属于任何子页"的格子落在这块真实文件里（它自己也能同步）。</summary>
    private string UnfiledPath
    {
        get
        {
            var anchor = _openBoards.Find(p => !IsAllNotes(p)) ?? _boardPath;
            var dir = anchor is null
                ? System.IO.Path.Combine(AppContext.BaseDirectory, "boards")
                : BoardLocator.BoardDirectory(anchor);

            return System.IO.Path.Combine(dir, "未归档.canvas");
        }
    }

    private void EnsureUnfiled()
    {
        var path = UnfiledPath;

        if (File.Exists(path))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            CanvasStore.Write(path, CanvasDocument.CreateNew().ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Write($"all-notes: 未归档建不出来 {path}: {ex.Message}");
        }
    }

    /// <summary>选中「所有便签」那一拍：把各子文件 + 未归档并成一张内存文档。不装 watch、不起 sync。</summary>
    private void LoadAllNotes()
    {
        EnsureUnfiled();

        var boards = new List<(string OwnerKey, CanvasDocument Doc)>();
        var unfiled = UnfiledPath;

        foreach (var path in _openBoards.Where(p => !IsAllNotes(p)))
        {
            try
            {
                boards.Add((path, CanvasStore.Load(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or CanvasDocumentException or JsonException)
            {
                Diagnostics.Write($"all-notes: 跳过读不动的 {path}: {ex.Message}");
            }
        }

        if (!boards.Exists(b => SameBoard(b.OwnerKey, unfiled)))
        {
            try
            {
                boards.Add((unfiled, CanvasStore.Load(unfiled)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or CanvasDocumentException or JsonException)
            {
                Diagnostics.Write($"all-notes: 未归档读不动 {unfiled}: {ex.Message}");
            }
        }

        var merged = BoardMerge.Merge(boards);

        _boardPath = AllNotesKey;
        _document = CanvasDocument.Parse(merged.ToJsonString(), AllNotesKey);
        _baseJson = merged.ToJsonString(); // 哨兵走整表拆分回写，不用 diff

        ResetPerBoardRuntime();

        Rebuild();
        RebuildTabs();

        Say($"所有便签 · {_document.Nodes.Count} 格（各标签页 + 未归档的并集，只读总览可编辑但改动写回来源）");
        Diagnostics.Write($"all-notes merged {boards.Count} 块板 → {_document.Nodes.Count} 格");
    }

    /// <summary>在「所有便签」里存盘：按归属把合并文档拆回各真实文件写。父集本身不写盘。</summary>
    private void SaveAllNotes()
    {
        if (_document is null)
        {
            return;
        }

        foreach (var (owner, parts) in BoardMerge.Split(_document.Root))
        {
            if (owner.Length > 0)
            {
                WriteOwnerFromSplit(owner, parts);
            }
        }
    }

    private void WriteOwnerFromSplit(string ownerPath, BoardMerge.MergedParts parts)
    {
        try
        {
            var root = File.Exists(ownerPath)
                ? JsonNode.Parse(CanvasStore.ReadAllText(ownerPath))!.AsObject()
                : new JsonObject { ["specVersion"] = "1.0" };

            root["nodes"] = parts.Nodes;
            root["edges"] = parts.Edges;

            CanvasStore.Write(ownerPath, CanvasDocument.Parse(root.ToJsonString(), ownerPath).ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or CanvasDocumentException)
        {
            Say($"总览存盘没写回 {System.IO.Path.GetFileName(ownerPath)}：{ex.Message}");
            Diagnostics.Write($"all-notes save failed for {ownerPath}: {ex}");
        }
    }
}
