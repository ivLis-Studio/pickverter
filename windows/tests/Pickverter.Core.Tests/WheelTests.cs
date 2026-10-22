using Xunit;

namespace Pickverter.Tests;

/// Keyboard and pointer behaviour of the wheel (ported from the Mac self-test).
public class WheelTests
{
    private static WheelModel Open(string file, WheelMode mode = WheelMode.Convert)
    {
        var wheel = new WheelModel(demo: true);
        wheel.Present([file], WheelModel.WheelOrigin.Selection, mode, ScreenPoint.Zero);
        wheel.ForceVisibleForTesting();
        return wheel;
    }

    private static string? SlotTitle(WheelModel wheel) => wheel.Active is { } active ? active.Parent?.Title ?? active.Option.Title : null;

    [Fact]
    public void KeyboardWalksTheRings()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var wheel = Open(photo);
        var formats = wheel.Items.Select(item => item.AsOption?.Title).ToList();
        wheel.HandleKey(new KeyInput(WheelKey.Letter, 'w'));
        wheel.HandleKey(new KeyInput(WheelKey.Letter, 'e'));
        Assert.Equal("WebP", SlotTitle(wheel));
        var webp = formats.IndexOf("WebP");
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        Assert.Equal(formats[(webp + 1) % formats.Count], SlotTitle(wheel));
        wheel.HandleKey(new KeyInput(WheelKey.Left));
        wheel.HandleKey(new KeyInput(WheelKey.Left));
        Assert.Equal(formats[(webp - 1 + formats.Count) % formats.Count], SlotTitle(wheel));
        wheel.HandleKey(new KeyInput(WheelKey.Tab));
        Assert.Equal(WheelMode.Tools, wheel.Mode);
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        Assert.Equal(L("tool.compress"), SlotTitle(wheel));
        Assert.Equal(L("q.balanced"), wheel.Active?.Option.Title);
        wheel.HandleKey(new KeyInput(WheelKey.Down));
        Assert.Equal(L("q.balanced"), wheel.Active?.Option.Title);
        wheel.HandleKey(new KeyInput(WheelKey.Down));
        Assert.Equal(L("q.high"), wheel.Active?.Option.Title);
        var tools = wheel.Items.Select(item => item.AsOption?.Id).ToList();
        for (var step = 0; step < tools.IndexOf("tool.rotate"); step++) wheel.HandleKey(new KeyInput(WheelKey.Right));
        Assert.Equal(L("tool.rotate"), SlotTitle(wheel));
        wheel.HandleKey(new KeyInput(WheelKey.Up));
        Assert.Equal(L("rot.flipV"), wheel.Active?.Option.Title);
        wheel.HandleKey(new KeyInput(WheelKey.Tab));
        Assert.Equal(WheelMode.Actions, wheel.Mode);
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        Assert.Equal(L("action.share"), wheel.Active?.Option.Title);
        Assert.True(wheel.HandleKey(new KeyInput(WheelKey.Delete)));
        wheel.HandleKey(new KeyInput(WheelKey.Escape));
        Assert.False(wheel.IsVisible);
    }

    [Fact]
    public void SystemKeysPassThroughAndClose()
    {
        using var sandbox = new Sandbox();
        var wheel = Open(sandbox.Fixture("photo.jpg"));
        Assert.False(wheel.HandleKey(new KeyInput(WheelKey.System)));
        Assert.False(wheel.IsVisible);
        wheel = Open(sandbox.Fixture("photo.jpg"));
        Assert.False(wheel.HandleKey(new KeyInput(WheelKey.Tab, Alt: true)));
        Assert.False(wheel.IsVisible);
    }

    [Fact]
    public void PointerPicksSegmentsAndOuterRing()
    {
        using var sandbox = new Sandbox();
        var wheel = Open(sandbox.Fixture("photo.jpg"), WheelMode.Tools);
        var g = wheel.Geometry;
        var radius = (g.InnerRadius + g.OuterRadius) / 2;
        // Straight up is the first slot.
        wheel.PointerMoved(0, -radius);
        Assert.Equal(0, wheel.Highlighted);
        Assert.NotNull(wheel.SubRing);
        // Move onto the outer ring at the slot's angle: its middle option.
        var ring = wheel.SubRing!;
        var (x, y) = WheelGeometry.Polar((g.SubInner + g.SubOuter) / 2, ring.AngleOf(1));
        wheel.PointerMoved(x, y);
        Assert.Equal(1, wheel.SubHighlighted);
        // The dead zone clears everything.
        wheel.PointerMoved(2, 2);
        Assert.Null(wheel.Highlighted);
        Assert.Null(wheel.SubRing);
        // Tabs sit under the disc.
        var (tx, ty, tw, th) = g.TabRect(WheelMode.Actions);
        Assert.Equal(WheelMode.Actions, wheel.TabHit(tx + tw / 2, ty + th / 2));
        Assert.True(wheel.HandleClick(tx + tw / 2, ty + th / 2, false));
        Assert.Equal(WheelMode.Actions, wheel.Mode);
        // Far outside: not ours, and the wheel closes.
        Assert.False(wheel.HandleClick(0, -(g.DiscRadius + 80), false));
        Assert.False(wheel.IsVisible);
    }

    [Fact]
    public void DestinationChoiceWhenAsking()
    {
        using var sandbox = new Sandbox();
        var wheel = Open(sandbox.Fixture("photo.jpg"));
        wheel.DestinationOverride = ResultDestination.Ask;
        PickOption? launched = null;
        ResultDestination? where = null;
        wheel.Launched += (option, _, _, destination, _) =>
        {
            launched = option;
            where = destination;
        };
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        wheel.HandleKey(new KeyInput(WheelKey.Enter));
        Assert.NotNull(wheel.Choice);
        Assert.Equal(3, wheel.Items.Count);
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        wheel.HandleKey(new KeyInput(WheelKey.Enter));
        Assert.NotNull(launched);
        Assert.Equal(ResultDestination.Clipboard, where);
        // Ctrl+S forces a file even when the preference says clipboard.
        wheel = Open(sandbox.Fixture("photo.jpg"));
        wheel.DestinationOverride = ResultDestination.Clipboard;
        wheel.Launched += (option, _, _, destination, _) => where = destination;
        wheel.HandleKey(new KeyInput(WheelKey.Right));
        wheel.HandleKey(new KeyInput(WheelKey.Letter, 's', Ctrl: true));
        Assert.Equal(ResultDestination.File, where);
    }

    [Fact]
    public void LayoutHidesMovesAndPins()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var key = WheelLayout.Key(WheelMode.Tools, [photo]);
        var layout = WheelLayout.Shared;
        var options = Catalog.Options([photo], WheelMode.Tools);
        var ids = options.Select(option => option.Id).ToList();
        layout.Hide("tool.ocr", "OCR");
        Assert.DoesNotContain("tool.ocr", layout.Arrange(options, key).Select(option => option.Id));
        layout.Move("tool.resize", -1, ids, key);
        Assert.Equal("tool.resize", layout.Arrange(options, key)[0].Id);
        layout.Pin("tool.rotate.left", "Rotate · Left", key);
        var arranged = layout.Arrange(options, key);
        var pinned = arranged.FirstOrDefault(option => option.Id == "tool.rotate.left");
        Assert.NotNull(pinned);
        Assert.Equal("tool.rotate", pinned!.PinnedFrom);
        layout.Reset(key);
        Assert.Equal(ids.Where(id => id != "tool.ocr"), layout.Arrange(options, key).Select(option => option.Id));
    }

    [Fact]
    public void HighlightAndClickShareOneReach()
    {
        using var sandbox = new Sandbox();
        var wheel = Open(sandbox.Fixture("photo.jpg"));
        wheel.DestinationOverride = ResultDestination.File;
        PickOption? launched = null;
        wheel.Launched += (option, _, _, _, _) => launched = option;
        var g = wheel.Geometry;
        // Past the disc (where a click used to close the wheel) a highlighted slot runs.
        var (x, y) = WheelGeometry.Polar((g.DiscRadius + 26 + g.Reach) / 2, Math.PI / 2);
        wheel.PointerMoved(x, y);
        var target = wheel.Active?.Option.Id;
        Assert.NotNull(target);
        Assert.True(wheel.HandleClick(x, y, false));
        Assert.Equal(target, launched?.Id);
        // Past the reach nothing is highlighted, and a click closes the wheel.
        wheel = Open(sandbox.Fixture("photo.jpg"));
        (x, y) = WheelGeometry.Polar(g.Reach + 6, Math.PI / 2);
        wheel.PointerMoved(x, y);
        Assert.Null(wheel.Highlighted);
        Assert.False(wheel.HandleClick(x, y, false));
        Assert.False(wheel.IsVisible);
    }

    [Fact]
    public void WheelMovesInwardAtScreenEdges()
    {
        var g = new WheelGeometry { TabWidth = 136 };
        // A 1920×1080 monitor at 150 %, taskbar at the bottom: the work area ends at 1032.
        const double right = 1920, bottom = 1032, scale = 1.5;
        var drawn = Math.Max(g.SubOuter + 4, Math.Max(g.TabBarWidth / 2, g.TabBarOffset + g.TabBarHeight / 2)) * scale;
        foreach (var (px, py) in new[] { (0.0, 0.0), (1919.0, 1079.0), (4.0, 600.0), (1000.0, 1031.0), (1916.0, 2.0) })
        {
            var (cx, cy) = g.CenterIn(px, py, 0, 0, right, bottom, scale);
            Assert.True(cx - drawn >= 0 && cx + drawn <= right, $"{px},{py} -> x {cx}");
            Assert.True(cy - drawn >= 0 && cy + drawn <= bottom, $"{px},{py} -> y {cy}");
        }
        Assert.Equal((960.0, 500.0), g.CenterIn(960, 500, 0, 0, right, bottom, scale));
        // A display too small for the whole wheel centres it on that axis.
        Assert.Equal((g.Envelope, 200.0), g.CenterIn(10, 10, 0, 0, 600, 400));
    }

    [Fact]
    public void MissingFFmpegIsOfferedNotRefused()
    {
        using var sandbox = new Sandbox();
        Environment.SetEnvironmentVariable("PICKVERTER_NO_FFMPEG", "1");
        try
        {
            var wheel = Open(sandbox.Fixture("tone.wav"));
            wheel.DestinationOverride = ResultDestination.File;
            PickOption? launched = null;
            wheel.Launched += (option, _, _, _, _) => launched = option;
            var slot = wheel.Items.ToList().FindIndex(item => item.AsOption?.Unavailable == Tools.FFmpegHint);
            Assert.True(slot >= 0, "no slot waits for ffmpeg");
            Assert.True(wheel.Segments[slot].NeedsDownload);
            wheel.Commit(slot);
            Assert.NotNull(launched);
            Assert.Null(launched!.Unavailable);
            Assert.Null(wheel.Flash);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICKVERTER_NO_FFMPEG", null);
        }
    }
}
