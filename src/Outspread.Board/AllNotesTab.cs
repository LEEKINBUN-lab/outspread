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
/// 每块板在并集里独占一条竖向带（<see cref="BoardMerge.RegionOffsets"/>）：带内保持它在自己文件里的
/// 相对坐标，带与带之间隔开，所以不同标签页的格子不再叠在同一片坐标上。写回时把这条带的偏移减掉，
/// 源文件里的坐标一个字节不变。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>顶替 _boardPath 的哨兵；尖括号不是合法路径字符，绝不会和真文件撞。</summary>
    public const string AllNotesKey = "<<所有便签>>";

    private static bool IsAllNotes(string? path) => path == AllNotesKey;

    /// <summary>真实板子的数量（哨兵不算）——"最后一块不给关"按这个数。</summary>
    private int RealBoardCount => _openBoards.Count(open => !IsAllNotes(open));

    /// <summary>关掉 closing 这一枚后回到哪块：最近的、且不是哨兵的一块真实板。</summary>
    private string NeighbourBoard(int closing)
    {
        for (var distance = 1; distance < _openBoards.Count; distance++)
        {
            var down = closing - distance;
            if (down >= 0 && !IsAllNotes(_openBoards[down]))
            {
                return _openBoards[down];
            }

            var up = closing + distance;
            if (up < _openBoards.Count && !IsAllNotes(_openBoards[up]))
            {
                return _openBoards[up];
            }
        }

        return _openBoards[closing];
    }

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
    /// <summary>并集里"每块板那条竖向带"的偏移；写回时按同一个数减掉。</summary>
    private readonly Dictionary<string, (double X, double Y)> _allNotesRegionOffsets = new(StringComparer.Ordinal);

    private void LoadAllNotes()
    {
        // 父集没有自己的文件：停掉上一块板的文件监视与同步，也不给哨兵建它们。
        _watcher?.Dispose();
        _watcher = null;
        _sync?.Stop();
        _sync = null;

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

        var offsets = BoardMerge.RegionOffsets(boards);

        _allNotesRegionOffsets.Clear();

        foreach (var pair in offsets)
        {
            _allNotesRegionOffsets[pair.Key] = pair.Value;
        }

        var merged = BoardMerge.Merge(boards, offsets);

        _boardPath = AllNotesKey;
        _document = CanvasDocument.Parse(merged.ToJsonString(), AllNotesKey);
        _baseJson = merged.ToJsonString(); // 哨兵走整表拆分回写，不用 diff

        ResetPerBoardRuntime();

        Rebuild();
        RebuildTabs();

        Say($"所有便签 · {_document.Nodes.Count} 格");
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
        // 并集里的坐标带着"这块板那条带"的偏移：写回前减掉（加的时候加了多少就减多少）。
        if (_allNotesRegionOffsets.TryGetValue(ownerPath, out var shift))
        {
            foreach (var node in parts.Nodes.OfType<JsonObject>())
            {
                BoardMerge.ShiftNode(node, -shift.X, -shift.Y);
            }
        }

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

    /// <summary>
    /// 往返自检（临时副本上、放在最后）：并集数对 → 父集新建落未归档 → 父集删格源文件里消失。
    /// 这发是"父集写回真的没写错文件"的闸，不靠人肉点。
    /// </summary>
    private void SelfTestAllNotesTab(List<string> problems)
    {
        if (_document is null || _boardPath is null || IsAllNotes(_boardPath))
        {
            return;
        }

        var startPath = _boardPath;

        try
        {
            SwitchToBoard(AllNotesKey);

            if (!IsAllNotes(_boardPath))
            {
                problems.Add("切到『所有便签』没切过去");
                return;
            }

            var unfiled = UnfiledPath;

            var expected = 0;
            foreach (var path in _openBoards.Where(p => !IsAllNotes(p)))
            {
                expected += CanvasDocument.Parse(CanvasStore.ReadAllText(path), path).Nodes.Count;
            }

            if (File.Exists(unfiled))
            {
                expected += CanvasDocument.Parse(CanvasStore.ReadAllText(unfiled), unfiled).Nodes.Count;
            }

            if (_document.Nodes.Count != expected)
            {
                problems.Add($"所有便签并集数不对：{_document.Nodes.Count}，应为 {expected}");
            }

            // 每块板的带不许叠在一起：按 y 排一遍，任何一条带的下沿都不能越过下一条带的上沿。
            var bands = new Dictionary<string, (double Top, double Bottom)>(StringComparer.Ordinal);

            foreach (var node in _document.Nodes)
            {
                if (!BoardMerge.TryUnmerge(node.Id, out var owner, out _) || owner.Length == 0)
                {
                    continue;
                }

                bands[owner] = bands.TryGetValue(owner, out var band)
                    ? (Math.Min(band.Top, node.Y), Math.Max(band.Bottom, node.Y + node.Height))
                    : (node.Y, node.Y + node.Height);
            }

            var ordered = bands.Values.OrderBy(band => band.Top).ToList();

            for (var index = 0; index + 1 < ordered.Count; index++)
            {
                if (ordered[index].Bottom > ordered[index + 1].Top)
                {
                    problems.Add("所有便签: 两块板的区域叠在一起了——并集应该按块分带");
                    break;
                }
            }

            // 只开着一块板时量不出"分带"，那是不适用，不是红；两块以上才必须每条带各占一段。
            if (ordered.Count < 2 && _openBoards.Count(open => !IsAllNotes(open)) >= 2)
            {
                problems.Add($"所有便签: 开着 {_openBoards.Count(open => !IsAllNotes(open))} 块板，却只读到 {ordered.Count} 块板的区域");
            }

            // 写回要减掉带偏移：把并集里的一格挪 (+7,+9)，源文件里的坐标也只该动 (+7,+9)。
            var probe = _document.Nodes.FirstOrDefault(node =>
                BoardMerge.TryUnmerge(node.Id, out var owner, out _) && !SameBoard(owner, unfiled));

            if (probe is not null && BoardMerge.TryUnmerge(probe.Id, out var probeOwner, out var probeRaw))
            {
                var probeSource = CanvasDocument.Parse(CanvasStore.ReadAllText(probeOwner), probeOwner)
                    .Nodes.FirstOrDefault(node => node.Id == probeRaw);

                if (probeSource is not null)
                {
                    var beforeX = probeSource.X;
                    var beforeY = probeSource.Y;

                    probe.X += 7;
                    probe.Y += 9;
                    SaveNow();

                    var after = CanvasDocument.Parse(CanvasStore.ReadAllText(probeOwner), probeOwner)
                        .Nodes.FirstOrDefault(node => node.Id == probeRaw);

                    if (after is null
                        || Math.Abs(after.X - (beforeX + 7)) > 0.001
                        || Math.Abs(after.Y - (beforeY + 9)) > 0.001)
                    {
                        problems.Add(
                            $"所有便签: 写回没减掉区域偏移（源里应 {beforeX + 7:0.#},{beforeY + 9:0.#}，" +
                            $"实得 {after?.X.ToString() ?? "没了"},{after?.Y.ToString() ?? "没了"}）");
                    }

                    probe.X -= 7;
                    probe.Y -= 9;
                    SaveNow();
                }
            }

            var unfiledBefore = File.Exists(unfiled)
                ? CanvasDocument.Parse(CanvasStore.ReadAllText(unfiled), unfiled).Nodes.Count
                : 0;

            CreateTextNote("总览往返", new Windows.Foundation.Point(4000, 4000));
            SaveNow();

            var unfiledAfter = File.Exists(unfiled)
                ? CanvasDocument.Parse(CanvasStore.ReadAllText(unfiled), unfiled).Nodes.Count
                : 0;

            if (unfiledAfter != unfiledBefore + 1)
            {
                problems.Add($"在父集新建后未归档文件没多一格：{unfiledBefore} → {unfiledAfter}");
            }

            // 删一格属于真实板的格：源文件里必须消失。
            var victim = _document.Nodes.FirstOrDefault(n =>
                BoardMerge.TryUnmerge(n.Id, out var owner, out _) && !SameBoard(owner, unfiled));

            if (victim is not null && BoardMerge.TryUnmerge(victim.Id, out var ownerPath, out var rawId))
            {
                _document.Remove(victim.Id);
                SaveNow();

                var ownerDoc = CanvasDocument.Parse(CanvasStore.ReadAllText(ownerPath), ownerPath);

                if (ownerDoc.Nodes.Any(n => n.Id == rawId))
                {
                    problems.Add($"父集删格后源文件 {System.IO.Path.GetFileName(ownerPath)} 里那格还在");
                }
            }

            Diagnostics.Write(
                $"selftest 所有便签: 并集 {expected} 格、父集新建落未归档（{unfiledBefore}→{unfiledAfter}）、父集删格源文件消失");
        }
        catch (Exception ex)
        {
            Diagnostics.Write($"all-notes selftest exception: {ex}");
            problems.Add($"所有便签往返自检炸了：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!SameBoard(_boardPath ?? string.Empty, startPath))
            {
                SwitchToBoard(startPath);
            }
        }
    }
}
