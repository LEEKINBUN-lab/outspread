using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Outspread.Core;
using Windows.Foundation;

namespace Outspread.Board;

public sealed partial class MainWindow
{
    /// <summary>一像素的真实 PNG：文件夹预览会把它解码成图，空文件不行（读取要经得起 BitmapImage）。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private void SelfTestEntityCards(List<string> problems)
    {
        var saved = (_view.PanX, _view.PanY, _view.Zoom);
        var root = Path.Combine(Path.GetTempPath(), "outspread-entity-source");
        var source = Path.Combine(root, $"attachment-{Guid.NewGuid():N}.txt");
        var created = new List<string>();
        string? relative = null;
        string? folder = null;

        Directory.CreateDirectory(root);
        File.WriteAllText(source, "selftest attachment");

        try
        {
            var fileNode = CreateCachedFileCard(source, new Point(40000, 40000), out _);

            if (fileNode is null)
            {
                problems.Add("自检附件卡: 小文件没有收纳成本板文件格");
                return;
            }

            created.Add(fileNode.Id);
            relative = AssetStore.NormalizeRelativePath(fileNode.FilePath);

            // 相机要把这一批改件**摆在屏幕上**：没进可见区的格子根本不走 Update，
            // 于是占位提示、表格命中这些"画出来的东西"全在读一个还没画过的框。
            _view.Zoom = 0.5;
            _view.PanX = 60 - (40000 * 0.5);
            _view.PanY = 60 - (40000 * 0.5);

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            SelfTestFluentIcons(problems);

            if (!_realized.TryGetValue(fileNode.Id, out var card))
            {
                problems.Add("自检附件卡: 文件格没有画出来");
            }
            else
            {
                if (card.EntityTitle != Path.GetFileName(source))
                {
                    problems.Add($"自检附件卡: 卡头读到「{card.EntityTitle}」，应为「{Path.GetFileName(source)}」");
                }

                if (card.EntityBadge != "原件副本")
                {
                    problems.Add($"自检附件卡: 牌子读到「{card.EntityBadge}」，应为「原件副本」");
                }

                if (!card.EntityBadgeTip.Contains("副本", StringComparison.Ordinal))
                {
                    problems.Add($"自检附件卡: 牌子上没有释义句，读到「{card.EntityBadgeTip}」");
                }

                // 悬停那一排：静止时动作与元信息都得藏着；淡入前后卡头高度一个像素都不许动。
                if (card.EntityActionsOpacity > 0.01 || card.EntityMetaOpacity > 0.01)
                {
                    problems.Add($"自检附件卡: 没悬停就亮着动作/元信息（{card.EntityActionsOpacity:0.##}、{card.EntityMetaOpacity:0.##}）");
                }

                var headAtRest = card.EntityHeaderHeight;

                card.RevealEntityHeader(true);

                if (card.EntityHeaderHeight != headAtRest)
                {
                    problems.Add($"自检附件卡: 淡入把卡头从 {headAtRest:0.#} 改到 {card.EntityHeaderHeight:0.#}，卡不许在指针下变高");
                }

                card.RevealEntityHeader(false);

                if (card.EntityHeaderHeight <= 0 || double.IsNaN(card.EntityHeaderHeight))
                {
                    problems.Add("自检附件卡: 卡头没有占位，正文会压在名字上");
                }

                if (!card.PaintsNoteHint)
                {
                    problems.Add($"自检附件卡: 备注为空时没有亮「双击键入备注」的淡色提示（{card.NoteHintDebug}）");
                }
            }

            BeginEdit(fileNode.Id);

            if (_editor is null)
            {
                problems.Add("自检附件卡: 双击没有进入正文编辑");
            }
            else
            {
                // 他 10-25 那张截图：输入区越出卡片下沿。编辑框必须留在正文那一块里。
                // 编辑框是屏幕像素的家具，卡盒现在是世界尺寸——两边同尺要比，先乘回相机。
                var zoom = _view.Zoom;
                var bottom = Canvas.GetTop(_editor) + _editor.Height;
                var cardBottom = Canvas.GetTop(card!) + card!.Height * zoom;

                if (bottom > cardBottom + 1)
                {
                    problems.Add(
                        $"自检附件卡: 编辑框底 {bottom:0.#}（顶 {Canvas.GetTop(_editor):0.#}、高 {_editor.Height:0.#}）"
                        + $"越出卡片底 {cardBottom:0.#}（顶 {Canvas.GetTop(card!):0.#}、高 {card!.Height:0.#}×{zoom:0.##}、编辑中需要 {_editorGrownWorld:0.#} 世界像素）");
                }

                if (Canvas.GetTop(_editor) < Canvas.GetTop(card!) + card!.EntityHeaderHeight * zoom - 1)
                {
                    problems.Add("自检附件卡: 编辑框压在卡头上，用户会以为在改文件名");
                }

                _editor.Text = "自检附件说明";
                CloseEditor(commit: true);
            }

            if (!string.Equals(fileNode.Text, "自检附件说明", StringComparison.Ordinal))
            {
                problems.Add("自检附件卡: 正文提交后没有落到节点上");
            }

            if (fileNode.Height <= EntityCardLayout.BaseWorld)
            {
                problems.Add($"自检附件卡: 高度 {fileNode.Height:0.#} 没把卡头算进去");
            }

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (_realized.TryGetValue(fileNode.Id, out var written) && written.PaintsNoteHint)
            {
                problems.Add("自检附件卡: 已经有备注了还亮着「双击键入备注」");
            }

            // 按住 Ctrl 放下＝一件都不复制：这一格必须是链接型，带那句失效警示。
            var dropped = _document.Nodes.Count;

            PlaceFilesAsync([source], new Point(40200, 40000), forceLink: true).GetAwaiter().GetResult();

            var linkedFile = _document.Nodes.FirstOrDefault(node =>
                node.Kind == CanvasNodeKind.Link && created.Contains(node.Id) is false && node.Url?.Contains(Path.GetFileName(source), StringComparison.Ordinal) == true);

            if (linkedFile is null)
            {
                problems.Add("自检拖入: 按住 Ctrl 放下仍然复制了副本（应为只放链接）");
            }
            else
            {
                created.Add(linkedFile.Id);
                Rebuild();
                ArrangeAll();
                BoardArea.UpdateLayout();

                if (!_realized.TryGetValue(linkedFile.Id, out var linkCard) || linkCard.EntityBadge != "链接原件")
                {
                    problems.Add($"自检拖入: 链接卡牌子读到「{linkCard?.EntityBadge}」，应为「链接原件」");
                }
                else if (!linkCard.EntityBadgeTip.Contains("失效", StringComparison.Ordinal))
                {
                    problems.Add($"自检拖入: 链接卡的牌子释义没提失效风险，读到「{linkCard.EntityBadgeTip}」");
                }
            }

            if (_document.Nodes.Count <= dropped)
            {
                problems.Add("自检拖入: forceLink 放下后板上没有多出格子");
            }

            // 他 10-31 两拍：先"拒建并点名说原因"，随即嫌回执太长，改成只点名。
            // 锁住的文件必须拒建且回执整串就是「N 个文件未建卡：文件名」——谁往回加字谁红。
            var lockedSource = Path.Combine(root, $"locked-{Guid.NewGuid():N}.txt");
            File.WriteAllText(lockedSource, "selftest locked attachment");

            using (var held = File.Open(lockedSource, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var beforeLock = _document.Nodes.Count;

                PlaceFilesAsync([lockedSource], new Point(40600, 40000)).GetAwaiter().GetResult();

                if (_document.Nodes.Count != beforeLock)
                {
                    problems.Add("自检拖入: 被占用的文件仍然建了卡（应拒建）");
                }

                var receipt = _sayRow.Text;
                var expected = $"1 个文件未建卡：{Path.GetFileName(lockedSource)}";

                if (receipt != expected)
                {
                    problems.Add($"自检拖入: 被占用的文件回执要短到只点名，读到「{receipt}」，应为「{expected}」");
                }
            }

            File.Delete(lockedSource);

            var second = CreateCachedFileCard(source, new Point(40400, 40000), out _);

            if (second is null)
            {
                problems.Add("自检附件卡: 第二张同内容卡没能复用副本");
            }
            else
            {
                created.Add(second.Id);

                if (OwnedAttachmentsToRecycleAfterCardRemoval([relative!]).Count != 0)
                {
                    problems.Add("自检附件卡: 两张卡共用同一副本时，删一张就被判成可清理");
                }

                _document.Remove(second.Id);
                created.Remove(second.Id);
            }

            _document.Remove(fileNode.Id);
            created.Remove(fileNode.Id);

            if (OwnedAttachmentsToRecycleAfterCardRemoval([relative!]).Count != 1)
            {
                problems.Add("自检附件卡: 最后一张卡删掉后，副本没有被判成可清理");
            }

            var table = TablePaste.ReadTsv("a\tb\nc\td");

            if (table is null)
            {
                problems.Add("自检表格卡: 制表符文本没被识别成表格");
            }
            else
            {
                var tableNode = CreateTableCard(table, new Point(40800, 40000));
                created.Add(tableNode.Id);

                Rebuild();
                ArrangeAll();
                BoardArea.UpdateLayout();

                if (!_realized.TryGetValue(tableNode.Id, out var tableView) || !tableView.IsTableCard)
                {
                    problems.Add("自检表格卡: 表格没有画成表格卡");
                }
                else
                {
                    if (tableView.TablePlacements != 4)
                    {
                        problems.Add($"自检表格卡: 读到 {tableView.TablePlacements} 个格子，应 4");
                    }

                    // 表格卡不戴卡头：几列几行、"粘贴自 Excel"、XLS 牌子都不画，他要的就是一张表。
                    if (tableView.EntityHeaderHeight > 0)
                    {
                        problems.Add($"自检表格卡: 表格上面压了 {tableView.EntityHeaderHeight:0.#} 像素的卡头");
                    }

                    if (!tableView.PaintsNoteHint)
                    {
                        problems.Add("自检表格卡: 表格下面没有留「双击键入备注」那一行");
                    }
                }

                // 随时能改：双击某一格 → 就地改 → 回车收下，表还是那张表。
                if (tableView is null)
                {
                    problems.Add("自检表格卡: 这一格没能画成表格卡，改格那一发不再试");
                }
                else if (tableView.TableCellAt(new Point(Canvas.GetLeft(tableView) + 12, Canvas.GetTop(tableView) + 12), BoardArea) is not { } firstCell)
                {
                    problems.Add(
                        $"自检表格卡: 双击落不到表格的第一格上（卡在 {Canvas.GetLeft(tableView):0.#}×{Canvas.GetTop(tableView):0.#}、"
                        + $"高 {tableView.Height:0.#}、卡头 {tableView.EntityHeaderHeight:0.#}）");
                }
                else
                {
                    var before = tableNode.Text;

                    BeginEditCell(tableNode.Id, firstCell.Row, firstCell.Column, firstCell.Bounds);

                    if (_editor is null || _editingCell is null)
                    {
                        problems.Add("自检表格卡: 双击某一格没有就地起编辑框");
                    }
                    else
                    {
                        // 框必须就是**那一格**那么大：差一像素就是另一格的字会被改掉。
                        if (Math.Abs(Canvas.GetTop(_editor) - firstCell.Bounds.Y) > 1
                            || Math.Abs(Canvas.GetLeft(_editor) - firstCell.Bounds.X) > 1
                            || Math.Abs(_editor.Height - firstCell.Bounds.Height) > 1
                            || Math.Abs(_editor.Width - firstCell.Bounds.Width) > 1)
                        {
                            problems.Add(
                                $"自检表格卡: 编辑框 {Canvas.GetLeft(_editor):0.#}×{Canvas.GetTop(_editor):0.#} "
                                + $"{_editor.Width:0.#}×{_editor.Height:0.#} 没盖住那一格 "
                                + $"{firstCell.Bounds.X:0.#}×{firstCell.Bounds.Y:0.#} "
                                + $"{firstCell.Bounds.Width:0.#}×{firstCell.Bounds.Height:0.#}");
                        }

                        // 上一条红不许把提交那一步一起跳掉——不然下一发读到的"没落回表里"是假的。
                        _editor.Text = "自检改格";
                        CloseEditor(commit: true);
                    }

                    if (before == tableNode.Text || tableNode.Text?.Contains("自检改格", StringComparison.Ordinal) is not true)
                    {
                        problems.Add($"自检表格卡: 改过的那一格没落回表里（正文「{tableNode.Text}」）");
                    }
                }

                // 下方补的字走备注那一路，不写进表的 markdown。
                BeginEdit(tableNode.Id);

                if (_editor is null)
                {
                    problems.Add("自检表格卡: 双击表格卡没起备注编辑框");
                }
                else
                {
                    _editor.Text = "自检备注";
                    CloseEditor(commit: true);
                }

                if (EntityCards.NoteOf(tableNode) != "自检备注")
                {
                    problems.Add($"自检表格卡: 备注没落到 note 字段，读到「{EntityCards.NoteOf(tableNode)}」");
                }

                if (tableNode.Text?.Contains("自检备注", StringComparison.Ordinal) is true)
                {
                    problems.Add("自检表格卡: 备注写进了表格的 markdown 正文");
                }

                _selection.Clear();
                _selection.Add(tableNode.Id);
                DuplicateSelection();

                var copy = _document.Nodes.FirstOrDefault(node =>
                    node.Id != tableNode.Id && node.Json[EntityCards.TableField] is not null);

                if (copy is null)
                {
                    problems.Add("自检表格卡: 制作副本没有带走 table 字段");
                }
                else
                {
                    created.Add(copy.Id);

                    if (copy.Label != tableNode.Label)
                    {
                        problems.Add("自检表格卡: 制作副本丢了表格标题");
                    }
                }
            }

            var webNode = CreateWebCard(new Uri("https://example.com/docs/self-test"), new Point(41200, 40000));
            created.Add(webNode.Id);

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(webNode.Id, out var webCard) || webCard.EntityBadge != "网页")
            {
                problems.Add("自检网页卡: 没有画成带「网页」牌的卡");
            }
            else if (webCard.EntityBadgeTip.Length == 0)
            {
                problems.Add("自检网页卡: 牌子上没有释义句（说明文字收进牌子，不再常驻卡面）");
            }

            if (webNode.Label != "self test")
            {
                problems.Add($"自检网页卡: 标题兜底读到「{webNode.Label}」，应为「self test」");
            }

            folder = Path.Combine(root, $"photos-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "封面.psd"), string.Empty);
            File.WriteAllBytes(Path.Combine(folder, "终稿.png"), OnePixelPng);
            File.WriteAllBytes(Path.Combine(folder, "草图.jpg"), OnePixelPng);
            File.WriteAllText(Path.Combine(folder, "说明.txt"), string.Empty);

            var folderNode = CreateFolderCard(folder!, new Point(41400, 40000));
            created.Add(folderNode.Id);

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            // 装图片的文件夹给的是缩略图，不是"里头有哪几个文件"的标签。
            if (!_realized.TryGetValue(folderNode.Id, out var folderCard) || folderCard.EntityBadge != "链接原件" || folderCard.EntityThumbCount != 2)
            {
                problems.Add($"自检文件夹卡: 牌子「{folderCard?.EntityBadge}」、缩略图 {folderCard?.EntityThumbCount} 张，应为链接原件与 2 张");
            }

            SelfTestCreateFromLocation(problems, created, folder, root);

            var groupLeft = CreateWebCard(new Uri("https://example.com/docs/one"), new Point(41600, 40000));
            var groupRight = CreateWebCard(new Uri("https://example.com/docs/two"), new Point(41820, 40000));
            var group = _document.Add(CanvasNodeKind.Group, 41580, 39980, 420, 120);
            group.Label = "资料";
            group.SetGroupMembers([groupLeft.Id, groupRight.Id]);
            created.AddRange([groupLeft.Id, groupRight.Id, group.Id]);

            Rebuild();
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(group.Id, out var groupCard))
            {
                problems.Add("自检分组: 实体卡旁边的组没有画出来");
            }
            else if (groupCard.GroupName != "资料" || groupCard.GroupNameMaxWidth < 100)
            {
                problems.Add($"自检分组: 组名读到「{groupCard.GroupName}」，可用宽 {groupCard.GroupNameMaxWidth:0.#}");
            }

            // 他 10-31 拍的两发。其一：双击组内空白＝直接生在组里（长框与名单走组钮拖入同一套）。
            var frameBox = RectOf(group);
            var insidePoint = new Point(frameBox.X + 40, frameBox.Y + 14);

            var home = HomeGroupAt(insidePoint);

            if (home is null || home.Id != group.Id)
            {
                problems.Add($"自检分组: 框内那发双击没有认到这个组（{(home is null ? "null" : home.Label)}）");
            }
            else
            {
                var adopted = CreateTextNote(string.Empty, insidePoint);
                created.Add(adopted.Id);
                AdoptIntoGroup(adopted, home);

                if (!RectOf(home).Holds(RectOf(adopted)))
                {
                    problems.Add("自检分组: 双击建卡后框没有长到罩住新格");
                }

                if (Groups().GroupOf(RectOf(adopted))?.Id != group.Id)
                {
                    problems.Add("自检分组: 生在框里的新格没有被算进组成员");
                }

                var roster = group.GroupMemberIds;

                if (roster is null || !roster.Contains(adopted.Id))
                {
                    problems.Add($"自检分组: 框上有名单却没把新格写进去（{(roster is null ? "名单丢了" : $"{roster.Count} 格")}）");
                }
            }

            // 其二：组钮拖入被打断（指针取消／捕获丢失）后，警示态必须一发清得干净——
            // 蓝框卡死那格的回归锁：亮起、清空、读回必须归位到虚线。
            if (!_realized.TryGetValue(group.Id, out var alerted))
            {
                problems.Add("自检分组: 组框没有画出来，警示态那发不适用");
            }
            else
            {
                alerted.SetGroupAlert("in");

                if (alerted.GroupChrome().Alert != "in")
                {
                    problems.Add("自检分组: 警示态没有亮起来，清不清无从谈起");
                }

                ClearGroupAlerts();

                if (alerted.GroupChrome().Alert is not null)
                {
                    problems.Add($"自检分组: 清理之后组框还亮着（{alerted.GroupChrome().Alert}）——拖入被打断就会卡成那样");
                }
            }
        }
        finally
        {
            foreach (var id in created)
            {
                _document.Remove(id);
            }

            _view.PanX = saved.PanX;
            _view.PanY = saved.PanY;
            _view.Zoom = saved.Zoom;

            Rebuild();
            ArrangeAll();

            try
            {
                if (relative is not null && _boardPath is not null)
                {
                    var full = AttachmentStore.ResolveExisting(_boardPath, relative);

                    if (full is not null)
                    {
                        File.Delete(full);
                    }
                }

                File.Delete(source);

                if (folder is not null)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        if (problems.Count == 0)
        {
            Diagnostics.Write("selftest 实体卡: 六枚 Fluent 形状解析得出、牌子文案与释义、静止不亮动作且淡入不改卡头高、备注占位与编辑区不越卡、Ctrl 放下走链接、被占用的文件拒建且回执只点名、表格就地改格与下方备注、文件夹缩略图、一行路径或网址建得上屏（不存在就拒建并点名）各过一发");
        }
    }

    /// <summary>
    /// 这六枚形状是我们**自己吃 SVG 路径**吃出来的（这版投影没有 Geometry.Parse），所以每一枚都要证明
    /// 它真解析得出、且落在 20×20 的源盒子里。「打开」「在文件夹中打开」平时藏着，静止截图里根本看不见它们
    /// ——没有这一发，路径写歪一格会一路绿到屏幕上出现半截形状。
    /// </summary>
    private static void SelfTestFluentIcons(List<string> problems)
    {
        foreach (var (name, data) in new[]
                 {
                     ("Document", FluentIcons.Document),
                     ("Folder", FluentIcons.Folder),
                     ("FolderOpen", FluentIcons.FolderOpen),
                     ("Open", FluentIcons.Open),
                     ("Link", FluentIcons.Link),
                     ("Play", FluentIcons.Play),
                 })
        {
            if (FluentIcons.Glyph(data, 16, Microsoft.UI.Colors.Gray) is not Viewbox box
                || box.Child is not Microsoft.UI.Xaml.Shapes.Path path
                || path.Data is null)
            {
                problems.Add($"自检图标: Fluent {name} 没解析出形状（这段路径认不下）");
                continue;
            }

            var bounds = path.Data.Bounds;

            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < -0.5 || bounds.Y < -0.5 || bounds.Right > 20.5 || bounds.Bottom > 20.5)
            {
                problems.Add($"自检图标: Fluent {name} 的几何不在 20×20 源盒子里（{bounds.X:0.#},{bounds.Y:0.#} {bounds.Width:0.#}×{bounds.Height:0.#}）");
            }
        }
    }

    /// <summary>
    /// 「一行字 → 一张卡」那三发：路径在→建出来而且**真的画上了屏**（建完不重画就是"粘贴没反应"，
    /// 网页卡与表格卡都漏过这一发）；路径不在→板上一格都不许多、回执点名是哪一条；网址→带「网页」牌的卡。
    /// 弹框那一半不在这里——模态框会把自检卡死，而它只负责收那一行字，认与建都在这一发里。
    /// </summary>
    private void SelfTestCreateFromLocation(List<string> problems, List<string> created, string folder, string root)
    {
        var before = _document.Nodes.Count;

        CreateCardFromLocationAsync(folder, new Point(42040, 40000)).GetAwaiter().GetResult();

        var placed = _document.Nodes.Count - before;

        if (placed != 1)
        {
            problems.Add($"自检新建卡片: 放一个文件夹只该多 1 格，实际多了 {placed} 格");
        }
        else
        {
            var folderId = _document.Nodes[^1].Id;
            created.Add(folderId);
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(folderId, out var folderCard) || folderCard.EntityBadge != "链接原件")
            {
                problems.Add("自检新建卡片: 路径那一行没画成带「链接原件」牌的卡（建完没重画？）");
            }
        }

        var missing = Path.Combine(root, "这一条不存在.pdf");
        var untouched = _document.Nodes.Count;

        // 弹框靠这一发决定"建卡"那按下去关不关框，所以先直接读它自己：认不下就必须给出点名原因。
        if (TryReadLocation(missing, out Uri? probeWeb, out string? probePath, out var probeReason)
            || probeWeb is not null || probePath is not null
            || !probeReason.Contains(missing, StringComparison.Ordinal))
        {
            problems.Add($"自检新建卡片: 不存在的路径仍被读成可建（原因句「{probeReason}」）");
        }

        if (!TryReadLocation("https://example.com/docs/reader", out var readerWeb, out _, out var readerReason)
            || readerWeb?.Host != "example.com")
        {
            problems.Add($"自检新建卡片: 网址那一行没被读出来（{readerWeb?.AbsoluteUri ?? "null"}／「{readerReason}」）");
        }

        CreateCardFromLocationAsync(missing, new Point(42260, 40000)).GetAwaiter().GetResult();

        if (_document.Nodes.Count != untouched || !_sayRow.Text.Contains(missing, StringComparison.Ordinal))
        {
            problems.Add($"自检新建卡片: 不存在的路径也建了卡，或没说清是哪条（多 {_document.Nodes.Count - untouched} 格，回执「{_sayRow.Text}」）");
        }

        CreateCardFromLocationAsync("https://example.com/docs/from-location", new Point(42480, 40000)).GetAwaiter().GetResult();

        if (_document.Nodes.Count != untouched + 1)
        {
            problems.Add($"自检新建卡片: 网址那一行没建出卡（{untouched}→{_document.Nodes.Count}）");
        }
        else
        {
            var webId = _document.Nodes[^1].Id;
            created.Add(webId);
            ArrangeAll();
            BoardArea.UpdateLayout();

            if (!_realized.TryGetValue(webId, out var webCard) || webCard.EntityBadge != "网页")
            {
                problems.Add("自检新建卡片: 网址那一行没画成带「网页」牌的卡（建完没重画？）");
            }
        }

        // 弹框本身弹不起（模态会把自检后面每一步卡死），但"建不建得起来"这一格不必欠着：
        // 输入框、平时藏着的原因行、两枚钮都在真 XamlRoot 上过一遍。它长什么样仍是视觉债。
        var dialog = BuildLocationDialog(out var box, out var reasonLine);

        if (box.Header as string is not { Length: > 0 } || reasonLine.Visibility is not Visibility.Collapsed
            || dialog.PrimaryButtonText != "建卡" || dialog.CloseButtonText != "取消")
        {
            problems.Add("自检新建卡片: 那枚弹框建歪了（输入框标题／原因行／钮，某一项不对）");
        }
    }
}
