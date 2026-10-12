namespace Outspread.Core.Tests;

public class ValidationAssetAndViewTests
{
    private static CanvasDocument Board(string nodeJson, string edgesJson = "[]")
        => CanvasDocument.Parse($"{{ \"nodes\": [ {nodeJson} ], \"edges\": {edgesJson} }}");

    [Fact]
    public void A_board_written_to_spec_has_no_errors_and_no_warnings()
    {
        var document = Board("""
            { "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 100, "height": 40,
              "text": "note", "color": "3" }
            """);

        Assert.Empty(CanvasValidator.Validate(document));
    }

    [Theory]
    [InlineData("\"color\": \"7\"")]
    [InlineData("\"color\": \"#GGHHJJ\"")]
    [InlineData("\"color\": \"lightgreen\"")]
    public void A_color_outside_the_two_forms_the_spec_allows_is_a_warning_not_a_refusal(string color)
    {
        var document = Board($$"""
            { "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 100, "height": 40,
              "text": "note", {{color}} }
            """);

        var issues = CanvasValidator.Validate(document);

        var issue = Assert.Single(issues);
        Assert.Equal(CanvasIssueSeverity.Warning, issue.Severity);
        Assert.Contains("the spec allows", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_node_reaching_outside_the_board_folder_is_an_error()
    {
        foreach (var path in new[] { "../../Windows/win.ini", "C:/secrets.txt", "/etc/passwd" })
        {
            var document = Board($$"""
                { "id": "u111111111111", "type": "file", "x": 0, "y": 0, "width": 10, "height": 10,
                  "file": "{{path}}" }
                """);

            var issue = Assert.Single(CanvasValidator.Validate(document));
            Assert.Equal(CanvasIssueSeverity.Error, issue.Severity);
            Assert.Contains("outside the board folder", issue.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_legal_colour_that_no_ink_can_make_readable_is_warned_about()
    {
        var document = Board("""
            { "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 100, "height": 40,
              "text": "note", "color": "#7F7F7F" }
            """);

        var issue = Assert.Single(CanvasValidator.Validate(document));

        Assert.Equal(CanvasIssueSeverity.Warning, issue.Severity);
        Assert.Contains("hard to read", issue.Message, StringComparison.Ordinal);
        Assert.Contains("4.", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_node_type_is_an_error_that_names_the_type_it_found()
    {
        var document = Board("""
            { "id": "u111111111111", "type": "sticky", "x": 0, "y": 0, "width": 10, "height": 10 }
            """);

        var issue = Assert.Single(CanvasValidator.Validate(document));
        Assert.Equal(CanvasIssueSeverity.Error, issue.Severity);
        Assert.Contains("sticky", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_geometry_field_that_is_absent_or_not_a_number_is_reported()
    {
        var document = Board("""
            { "id": "u111111111111", "type": "text", "x": "left", "y": 0, "width": 10, "text": "t" }
            """);

        var issues = CanvasValidator.Validate(document);

        Assert.Contains(issues, issue => issue.Message.Contains("\"x\"", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("\"height\"", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edge_that_points_at_a_missing_node_is_reported_even_though_the_board_does_not_draw_edges()
    {
        var document = Board(
            """{ "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "t" }""",
            """[ { "id": "e1", "fromNode": "u111111111111", "fromSide": "bottom", "fromEnd": "none", "toNode": "u-typo", "toSide": "top", "toEnd": "arrow" } ]""");

        var issue = Assert.Single(CanvasValidator.Validate(document));
        Assert.Contains("u-typo", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edge_missing_a_side_is_an_error()
    {
        var document = Board(
            """{ "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "t" }""",
            """[ { "id": "e1", "fromNode": "u111111111111", "toNode": "u111111111111" } ]""");

        var issues = CanvasValidator.Validate(document);

        Assert.Equal(4, issues.Count);
        Assert.All(issues, issue => Assert.Equal(CanvasIssueSeverity.Error, issue.Severity));
    }

    [Fact]
    public void Rotation_is_kept_in_the_file_and_declared_ignored_out_loud()
    {
        var document = Board("""
            { "id": "u111111111111", "type": "text", "x": 0, "y": 0, "width": 10, "height": 10,
              "text": "t", "rotation": 15 }
            """);

        var issue = Assert.Single(CanvasValidator.Validate(document));
        Assert.Equal(CanvasIssueSeverity.Warning, issue.Severity);
        Assert.Contains("ignored on screen", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_pastes_of_the_same_image_are_one_file_named_by_its_content()
    {
        using var scratch = new Scratch("assets");

        byte[] png = [137, 80, 78, 71, 1, 2, 3, 4];

        var first = AssetStore.WriteImage(scratch.BoardPath, png, ".png", out var reusedOnce);
        var second = AssetStore.WriteImage(scratch.BoardPath, png, ".png", out var reusedTwice);

        Assert.Equal(first, second);
        Assert.False(reusedOnce);
        Assert.True(reusedTwice);
        Assert.StartsWith("assets/img_", first, StringComparison.Ordinal);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(AssetStore.AssetsDirectory(scratch.BoardPath)));
        Assert.Equal(first, AssetStore.NormalizeRelativePath(first.Replace('/', '\\')));
        Assert.NotNull(AssetStore.ResolveExisting(scratch.BoardPath, first));
    }

    [Theory]
    [InlineData(".jpg", ".jpg")]
    [InlineData("JPEG", ".jpeg")]
    [InlineData(@"\..png", ".png")]
    [InlineData("", ".png")]
    [InlineData(".weirdext", ".png")]
    public void The_stored_name_carries_the_extension_the_bytes_actually_have(string given, string expected)
    {
        Assert.EndsWith(expected, AssetStore.RelativeName([1, 2, 3], given), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_that_leaves_the_board_folder_is_refused_rather_than_resolved()
    {
        Assert.False(AssetStore.IsSafeRelativePath("../escape.png"));
        Assert.False(AssetStore.IsSafeRelativePath("C:/Windows/win.ini"));
        Assert.False(AssetStore.IsSafeRelativePath("/etc/passwd"));
        Assert.False(AssetStore.IsSafeRelativePath(@"\\server\share\x.png"));
        Assert.False(AssetStore.IsSafeRelativePath("   "));
        Assert.False(AssetStore.IsSafeRelativePath(null));

        Assert.True(AssetStore.IsSafeRelativePath("assets/img_a.png"));
        Assert.True(AssetStore.IsSafeRelativePath(@"assets\img_a.png"));
        Assert.Equal("assets/img_a.png", AssetStore.NormalizeRelativePath(@"assets\img_a.png"));

        // A name that is syntactically fine but does not exist resolves to null,
        // which the board shows as a broken tile rather than crashing on.
        Assert.Null(AssetStore.ResolveExisting(
            System.IO.Path.Combine(Path.GetTempPath(), "outspread-none.canvas"),
            "assets/img_missing.png"));
    }

    [Fact]
    public void Unreferenced_images_are_reported_and_left_on_disk()
    {
        using var scratch = new Scratch("orphans");

        AssetStore.WriteImage(scratch.BoardPath, [1, 2, 3], ".png", out _);
        var kept = AssetStore.WriteImage(scratch.BoardPath, [4, 5, 6], ".jpg", out _);

        var document = CanvasDocument.CreateNew();
        document.Add(CanvasNodeKind.File, 0, 0, 100, 100).FilePath = kept;

        var orphans = AssetStore.UnreferencedImages(scratch.BoardPath, document);

        Assert.NotEmpty(orphans);
        Assert.Contains(orphans, orphan => orphan != kept);
        Assert.True(Directory.Exists(AssetStore.AssetsDirectory(scratch.BoardPath)));
    }

    [Fact]
    public void The_camera_file_round_trips_and_reports_that_it_cannot_read_one()
    {
        using var scratch = new Scratch("view");

        var state = new BoardViewState { PanX = -1240.5, PanY = 318, Zoom = 1.32, NoteSize = NoteSizeMode.Fixed };
        state.SetFontSize("u111111111111", 24);

        state.Save(scratch.SidecarPath);
        var loaded = BoardViewState.TryLoad(scratch.SidecarPath);

        Assert.Null(loaded.Problem);
        Assert.Equal(-1240.5, loaded.State.PanX);
        Assert.Equal(318, loaded.State.PanY);
        Assert.Equal(1.32, loaded.State.Zoom);
        Assert.Equal(24, loaded.State.FontSizeFor("u111111111111"));
        Assert.Equal(BoardViewState.DefaultFontSize, loaded.State.FontSizeFor("u-other"));

        // The note-size switch has to survive a save, or flipping it to 固定 would silently come
        // back as 随文字 next time the board opens.
        Assert.Equal(NoteSizeMode.Fixed, loaded.State.NoteSize);

        // A sidecar written before the switch existed (or one with junk in that key) is the
        // shipped default, not a reported problem.
        File.WriteAllText(scratch.SidecarPath, "{ \"panX\": 12, \"panY\": 8, \"zoom\": 2, \"noteSize\": 7 }");
        var junk = BoardViewState.TryLoad(scratch.SidecarPath);

        Assert.Null(junk.Problem);
        Assert.Equal(12, junk.State.PanX);
        Assert.Equal(NoteSizeMode.Automatic, junk.State.NoteSize);

        File.WriteAllText(scratch.SidecarPath, "{ this is not valid");
        var broken = BoardViewState.TryLoad(scratch.SidecarPath);

        Assert.NotNull(broken.Problem);
        Assert.Contains(scratch.SidecarPath, broken.Problem, StringComparison.Ordinal);
        Assert.Equal(1, broken.State.Zoom);
    }

    [Fact]
    public void The_camera_file_forgets_font_sizes_of_nodes_that_are_gone()
    {
        using var scratch = new Scratch("view-prune");

        var state = new BoardViewState();
        state.SetFontSize("u111111111111", 24);
        state.SetFontSize("u-dead0000", 30);

        Assert.Equal(1, state.Prune(["u111111111111"]));
        Assert.Equal(24, state.FontSizeFor("u111111111111"));
        Assert.Equal(BoardViewState.DefaultFontSize, state.FontSizeFor("u-dead0000"));
    }

    [Fact]
    public void A_missing_camera_file_is_a_normal_first_run_not_a_problem()
    {
        using var scratch = new Scratch("view-absent");

        var loaded = BoardViewState.TryLoad(scratch.SidecarPath);

        Assert.Null(loaded.Problem);
        Assert.Equal(1, loaded.State.Zoom);
    }

    [Fact]
    public void Identifier_namespaces_keep_the_board_and_an_agent_from_minting_the_same_id()
    {
        Assert.StartsWith("u", NodeIds.NewAppId(), StringComparison.Ordinal);
        Assert.StartsWith("a", NodeIds.NewAgentId(), StringComparison.Ordinal);
        Assert.Equal(NodeIds.Length, NodeIds.NewAppId().Length);
        Assert.True(NodeIds.FollowsNamespaceConvention("a1b2c3d4e5f67"));
        Assert.False(NodeIds.FollowsNamespaceConvention("a1b2c3d4"));
        Assert.False(NodeIds.FollowsNamespaceConvention("n-registry"));

        Assert.True(NodeIds.IsValidId("hypothesis-a"));
        Assert.False(NodeIds.IsValidId("bad id"));
        Assert.False(NodeIds.IsValidId(""));
        Assert.False(NodeIds.IsValidId(null));

        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 20_000; index++)
        {
            Assert.True(ids.Add(NodeIds.NewAppId()));
        }
    }

    [Fact]
    public void An_id_that_ignores_the_convention_still_loads_and_only_costs_a_warning()
    {
        var document = Board("""
            { "id": "hypothesis-a", "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "t" }
            """);

        var issue = Assert.Single(CanvasValidator.Validate(document));
        Assert.Equal(CanvasIssueSeverity.Warning, issue.Severity);
        Assert.Equal("hypothesis-a", issue.NodeId);
    }

    [Fact]
    public void Pasted_text_is_stored_with_one_newline_form()
    {
        Assert.Equal("a\nb\nc", JsonText.NormalizeTextNewlines("a\r\nb\nc"));
        Assert.Equal("a\nb", JsonText.NormalizeTextNewlines("a\u2028b"));
        Assert.Empty(JsonText.NormalizeTextNewlines(null));
    }
}
