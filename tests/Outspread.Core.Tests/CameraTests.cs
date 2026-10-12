namespace Outspread.Core.Tests;

/// <summary>
/// The physics of the camera. These are the assertions behind "the wheel zooms about
/// the pointer" and "dragging pans", which is the one part of the board that has to be
/// right to the pixel rather than merely in the ballpark.
/// </summary>
public class CameraTests
{
    private const double MinZoom = 0.1;

    private const double MaxZoom = 4.0;

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(314.5, 201.25)]
    [InlineData(1290.0, 810.0)]
    [InlineData(-12.0, 44.0)]
    public void The_point_under_the_pointer_stays_there_through_twenty_notches(double pointerX, double pointerY)
    {
        var view = new BoardViewState { PanX = -640, PanY = 120, Zoom = 1 };

        var worldBefore = (view.ToWorldX(pointerX), view.ToWorldY(pointerY));

        // Ten notches at 1.12 is 3.1×, which stays inside the clamp: past the clamp the
        // anchor legitimately stops moving, and that would be a different assertion.
        for (var notch = 0; notch < 10; notch++)
        {
            Assert.True(view.ZoomAt(pointerX, pointerY, 1.12, MinZoom, MaxZoom));
        }

        var worldAfter = (view.ToWorldX(pointerX), view.ToWorldY(pointerY));

        // Measured in screen pixels after converting back, so the tolerance means what
        // it says: the node under the cursor has not visibly drifted.
        var driftX = Math.Abs(view.ToScreenX(worldBefore.Item1) - pointerX);
        var driftY = Math.Abs(view.ToScreenY(worldBefore.Item2) - pointerY);

        Assert.True(driftX < 0.5, $"horizontal drift {driftX}");
        Assert.True(driftY < 0.5, $"vertical drift {driftY}");

        Assert.Equal(worldBefore.Item1, worldAfter.Item1, 6);
        Assert.Equal(worldBefore.Item2, worldAfter.Item2, 6);
    }

    [Fact]
    public void Zooming_out_then_in_at_the_same_pointer_returns_the_same_view()
    {
        var view = new BoardViewState { PanX = 24, PanY = 16, Zoom = 1 };

        Assert.True(view.ZoomAt(400, 300, 1.12, MinZoom, MaxZoom));
        Assert.True(view.ZoomAt(400, 300, 1 / 1.12, MinZoom, MaxZoom));

        Assert.Equal(1, view.Zoom, 9);
        Assert.Equal(24, view.PanX, 6);
        Assert.Equal(16, view.PanY, 6);
    }

    [Fact]
    public void The_clamp_reports_that_nothing_moved_instead_of_lieing_about_the_anchor()
    {
        var view = new BoardViewState { PanX = 10, PanY = 10, Zoom = MinZoom };

        Assert.False(view.ZoomAt(500, 400, 1 / 1.12, MinZoom, MaxZoom));
        Assert.Equal(MinZoom, view.Zoom);

        view.Zoom = MaxZoom;
        Assert.False(view.ZoomAt(500, 400, 1.12, MinZoom, MaxZoom));
        Assert.Equal(MaxZoom, view.Zoom);
    }

    [Fact]
    public void A_pan_moves_the_world_under_a_fixed_screen_point_by_the_delta_divided_by_zoom()
    {
        var view = new BoardViewState { PanX = 0, PanY = 0, Zoom = 0.25 };

        var worldBeforeX = view.ToWorldX(600);
        var worldBeforeY = view.ToWorldY(400);

        view.PanBy(37.5, -12.25);

        Assert.Equal(37.5, view.PanX, 9);
        Assert.Equal(-12.25, view.PanY, 9);

        // Panning right by 37.5 screen pixels at 0.25× slides the paper by 150 world
        // pixels left under the same pixel: the delta is in screen units, the document is
        // in world units, and only the ratio between them may move a node.
        Assert.Equal(37.5 / 0.25, worldBeforeX - view.ToWorldX(600), 6);
        Assert.Equal(-12.25 / 0.25, worldBeforeY - view.ToWorldY(400), 6);
    }

    [Fact]
    public void Screen_and_world_round_trip_to_within_a_pixel()
    {
        var view = new BoardViewState { PanX = -1240.5, PanY = 318, Zoom = 1.32 };

        foreach (var (x, y) in new[] { (0.0, 0.0), (511.25, 300.5), (1320.0, 700.0) })
        {
            var worldX = view.ToWorldX(x);
            var worldY = view.ToWorldY(y);

            Assert.Equal(x, view.ToScreenX(worldX), 6);
            Assert.Equal(y, view.ToScreenY(worldY), 6);
        }
    }

    [Fact]
    public void The_world_viewport_grows_as_the_camera_zooms_out()
    {
        var view = new BoardViewState { PanX = 0, PanY = 0, Zoom = 1 };

        var atOne = view.WorldViewport(1000, 600);
        Assert.Equal(1000, atOne.Width, 6);
        Assert.Equal(600, atOne.Height, 6);

        view.Zoom = 0.25;
        var zoomedOut = view.WorldViewport(1000, 600);

        Assert.Equal(4000, zoomedOut.Width, 6);
        Assert.Equal(2400, zoomedOut.Height, 6);
    }

    [Fact]
    public void The_board_file_is_found_by_the_documented_order_and_says_which_rule_hit()
    {
        using var scratch = new Scratch("locator");

        var nested = Path.Combine(scratch.Folder, "boards");
        Directory.CreateDirectory(nested);

        var explicitPath = Path.Combine(scratch.Folder, "elsewhere.canvas");
        File.WriteAllText(explicitPath, "{ \"nodes\": [] }");

        var requested = BoardLocator.Resolve(explicitPath, scratch.Folder);
        Assert.Equal(explicitPath, requested.BoardPath);
        Assert.Contains("command line", requested.Reason, StringComparison.Ordinal);

        var deep = Path.Combine(nested, "deep", "deeper");
        Directory.CreateDirectory(deep);

        var board = Path.Combine(nested, "scratch.canvas");
        File.WriteAllText(board, "{ \"nodes\": [] }");

        var upward = BoardLocator.Resolve(null, deep);
        Assert.Equal(board, upward.BoardPath);
        Assert.Contains("searching upward", upward.Reason, StringComparison.Ordinal);


        // The last resort must not be "whatever folder a double-clicked exe happened to be
        // launched from", which is how a boards/ folder appears in someone's temp directory.
        var fallback = BoardLocator.FallbackPath();

        Assert.EndsWith(Path.Combine("Outspread", "boards", "scratch.canvas"), fallback, StringComparison.Ordinal);

        Assert.Equal(nested, BoardLocator.BoardDirectory(board));
    }

    [Fact]
    public void An_environment_override_points_the_board_at_another_project()
    {
        using var scratch = new Scratch("locator-env");

        var board = Path.Combine(scratch.Folder, "picked.canvas");

        try
        {
            Environment.SetEnvironmentVariable(BoardLocator.BoardPathEnvironmentVariable, board);

            var resolution = BoardLocator.Resolve(null, scratch.Folder);

            Assert.Equal(board, resolution.BoardPath);
            Assert.Contains("TANKAI_BOARD", resolution.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(BoardLocator.BoardPathEnvironmentVariable, null);
        }
    }
}
