using Xunit;

namespace Pickverter.Tests;

/// Settings › Wheel: slots taken out of, or brought into, one ring for one file type; "Use for All File Types";
/// settings files from earlier versions.
public class WheelLayoutTests
{
    private static List<string> Ids(IEnumerable<PickOption> options) => options.Select(option => option.Id).ToList();

    /// The other two rings' slots for the same files, as the live wheel passes them.
    private static List<PickOption> Pool(IReadOnlyList<string> files, WheelMode mode) =>
        WheelModes.All.Where(other => other != mode).SelectMany(other => Catalog.Options(files, other)).ToList();

    [Fact]
    public void RemovingTakesASlotOutOfOneRingForOneFileType()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var pdf = sandbox.Fixture("report.pdf");
        var layout = WheelLayout.Shared;
        var imageTools = WheelLayout.Key(WheelMode.Tools, [photo]);
        var pdfTools = WheelLayout.Key(WheelMode.Tools, [pdf]);
        Assert.Equal(WheelLayout.Key(WheelMode.Tools, FileKind.Image), imageTools);
        var tools = Catalog.Options([photo], WheelMode.Tools);
        layout.Remove("tool.zip", imageTools);
        layout.Remove("tool.zip", imageTools);
        Assert.Equal(["tool.zip"], layout.Removed[imageTools]);
        Assert.DoesNotContain("tool.zip", Ids(layout.Arrange(tools, imageTools, Pool([photo], WheelMode.Tools))));
        Assert.Contains("tool.zip", Ids(layout.Arrange(Catalog.Options([pdf], WheelMode.Tools), pdfTools, Pool([pdf], WheelMode.Tools))));
        Assert.True(layout.IsCustomizedKey(imageTools));
        Assert.False(layout.IsCustomizedKey(pdfTools));
        Assert.True(layout.IsCustomized);
        Assert.Empty(layout.Hidden);
        layout.Restore("tool.zip", imageTools);
        Assert.Equal(Ids(tools), Ids(layout.Arrange(tools, imageTools)));
        Assert.False(layout.IsCustomizedKey(imageTools));
        Assert.False(layout.IsCustomized);
        // A pinned option leaves by being unpinned, not by being set aside.
        layout.Pin("tool.rotate.left", "Rotate · Left 90°", imageTools);
        Assert.Contains("tool.rotate.left", Ids(layout.Arrange(tools, imageTools)));
        layout.Remove("tool.rotate.left", imageTools);
        Assert.DoesNotContain("tool.rotate.left", Ids(layout.Arrange(tools, imageTools)));
        Assert.False(layout.Removed.ContainsKey(imageTools));
        Assert.False(layout.IsPinned("tool.rotate.left", imageTools));
    }

    [Fact]
    public void BorrowedSlotsComeFromTheOtherRings()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(WheelMode.Actions, [photo]);
        var actions = Catalog.Options([photo], WheelMode.Actions);
        var pool = Pool([photo], WheelMode.Actions);
        layout.AddFromRing("tool.resize", key);
        layout.AddFromRing("tool.rotate.left", key);
        var arranged = layout.Arrange(actions, key, pool);
        Assert.Equal(["tool.resize", "tool.rotate.left"], Ids(arranged.TakeLast(2)));
        // A whole slot comes with its options; one option comes as a slot of its own ("Rotate · Left 90°").
        Assert.NotEmpty(arranged[^2].Variants);
        var left = arranged[^1];
        Assert.Equal(L("tool.rotate") + " · " + L("rot.left"), left.Title);
        Assert.Empty(left.Variants);
        Assert.Null(left.PinnedFrom);
        Assert.Equal("rotate.left", left.Symbol);
        // Without the other rings to take them from (files that don't have them) they're skipped.
        Assert.Equal(Ids(actions), Ids(layout.Arrange(actions, key)));
        Assert.Equal(Ids(actions), Ids(layout.Arrange(actions, key, Catalog.Options([photo], WheelMode.Convert))));
        // Only this ring borrows them.
        var convertKey = WheelLayout.Key(WheelMode.Convert, [photo]);
        Assert.DoesNotContain("tool.resize", Ids(layout.Arrange(Catalog.Options([photo], WheelMode.Convert), convertKey, Pool([photo], WheelMode.Convert))));
        // The live wheel passes the other rings along.
        var wheel = new WheelModel();
        wheel.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Actions, ScreenPoint.Zero);
        Assert.Equal(["tool.resize", "tool.rotate.left"], Ids(wheel.OptionsFor(WheelMode.Actions).TakeLast(2)));
        Assert.DoesNotContain("tool.resize", Ids(wheel.OptionsFor(WheelMode.Convert)));
    }

    [Fact]
    public void ASlotNeverShowsTwice()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(WheelMode.Actions, [photo]);
        var actions = Catalog.Options([photo], WheelMode.Actions);
        var pool = Pool([photo], WheelMode.Actions);
        var native = actions[0].Id;
        layout.AddFromRing(native, key);
        layout.AddFromRing("tool.resize", key);
        layout.AddFromRing("tool.resize", key);
        Assert.Equal([native, "tool.resize"], layout.Added[key]);
        var ids = Ids(layout.Arrange(actions, key, pool));
        Assert.Single(ids, id => id == native);
        Assert.Single(ids, id => id == "tool.resize");
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Sending a borrowed slot back doesn't set it aside.
        layout.Remove("tool.resize", key);
        Assert.DoesNotContain("tool.resize", Ids(layout.Arrange(actions, key, pool)));
        Assert.False(layout.Removed.ContainsKey(key));
        // Bringing a removed slot in takes it off the removed list.
        layout.Remove("tool.resize", WheelLayout.Key(WheelMode.Tools, [photo]));
        layout.AddFromRing("tool.resize", WheelLayout.Key(WheelMode.Tools, [photo]));
        Assert.False(layout.Removed.ContainsKey(WheelLayout.Key(WheelMode.Tools, [photo])));
        Assert.Single(Ids(layout.Arrange(Catalog.Options([photo], WheelMode.Tools), WheelLayout.Key(WheelMode.Tools, [photo]), Pool([photo], WheelMode.Tools))),
            id => id == "tool.resize");
    }

    [Fact]
    public void ResettingARingClearsOnlyThatRing()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(WheelMode.Tools, FileKind.Image);
        var pdfKey = WheelLayout.Key(WheelMode.Tools, FileKind.Pdf);
        var tools = Catalog.Options([photo], WheelMode.Tools);
        var pool = Pool([photo], WheelMode.Tools);
        layout.Remove("tool.zip", key);
        layout.Pin("tool.rotate.left", "Rotate · Left 90°", key);
        layout.AddFromRing("action.copypath", key);
        layout.SetOrder(Ids(layout.Arrange(tools, key, pool)).AsEnumerable().Reverse(), key);
        layout.SetDefault("tool.resize", "tool.resize.1920");
        layout.Remove("tool.zip", pdfKey);
        Assert.Equal("action.copypath", layout.Arrange(tools, key, pool)[0].Id);
        layout.Reset(key);
        Assert.False(layout.IsCustomizedKey(key));
        Assert.Equal(Ids(tools), Ids(layout.Arrange(tools, key, pool)));
        Assert.Empty(layout.PinnedList);
        Assert.True(layout.IsCustomizedKey(pdfKey));
        // Which option a click runs belongs to the slot, not to a ring.
        Assert.Equal("tool.resize.1920", layout.Defaults["tool.resize"]);
        layout.ResetAll();
        Assert.False(layout.IsCustomized);
        Assert.Empty(layout.Removed);
        Assert.Empty(layout.Added);
    }

    [Fact]
    public void OneRingCanBeUsedForEveryFileType()
    {
        using var sandbox = new Sandbox();
        var pdf = sandbox.Fixture("report.pdf");
        var layout = WheelLayout.Shared;
        string Tools(FileKind kind) => WheelLayout.Key(WheelMode.Tools, kind);
        var key = Tools(FileKind.Image);
        layout.Remove("tool.zip", key);
        layout.Pin("tool.rotate.left", "Rotate · Left 90°", key);
        layout.AddFromRing("action.copypath", key);
        layout.SetOrder(["tool.rotate", "tool.compress"], key);
        // Replaced, and dropped:
        layout.AddFromRing("action.share", Tools(FileKind.Video));
        layout.SetOrder(["tool.mute"], Tools(FileKind.Video));
        layout.Remove("tool.compress", Tools(FileKind.Audio));
        // Another ring stays as it is.
        var pdfConvert = WheelLayout.Key(WheelMode.Convert, FileKind.Pdf);
        layout.Remove("convert.png", pdfConvert);
        layout.ApplyToAllKinds(key);
        foreach (var kind in WheelLayout.Kinds)
        {
            Assert.Equal(["tool.zip"], layout.Removed[Tools(kind)]);
            Assert.Equal(["action.copypath"], layout.Added[Tools(kind)]);
            Assert.Equal(["tool.rotate.left"], layout.Pinned[Tools(kind)]);
            Assert.Equal(["tool.rotate", "tool.compress"], layout.Orders[Tools(kind)]);
        }
        Assert.Equal(["convert.png"], layout.Removed[pdfConvert]);
        Assert.False(layout.IsCustomizedKey(WheelLayout.Key(WheelMode.Actions, FileKind.Image)));
        // A PDF's Tools ring now looks like the image one, minus what PDFs don't have.
        var ids = Ids(layout.Arrange(Catalog.Options([pdf], WheelMode.Tools), WheelLayout.Key(WheelMode.Tools, [pdf]), Pool([pdf], WheelMode.Tools)));
        Assert.Equal("tool.rotate", ids[0]);
        Assert.Equal("tool.rotate.left", ids[1]);
        Assert.DoesNotContain("tool.zip", ids);
        Assert.Contains("action.copypath", ids);
        // Using an untouched ring for every file type clears theirs.
        layout.Reset(key);
        layout.ApplyToAllKinds(key);
        Assert.All(WheelLayout.Kinds, kind => Assert.False(layout.IsCustomizedKey(Tools(kind))));
        Assert.Empty(layout.PinnedList);
        Assert.True(layout.IsCustomizedKey(pdfConvert));
    }

    [Fact]
    public void SlotsHiddenByEarlierVersionsStayHiddenEverywhere()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var pdf = sandbox.Fixture("report.pdf");
        var layout = WheelLayout.Shared;
        layout.Hide("tool.zip", "ZIP");
        Assert.DoesNotContain("tool.zip", Ids(layout.Arrange(Catalog.Options([photo], WheelMode.Tools), WheelLayout.Key(WheelMode.Tools, [photo]))));
        Assert.DoesNotContain("tool.zip", Ids(layout.Arrange(Catalog.Options([pdf], WheelMode.Tools), WheelLayout.Key(WheelMode.Tools, [pdf]))));
        // Borrowing it into another ring doesn't bring it back either.
        var key = WheelLayout.Key(WheelMode.Actions, [photo]);
        var actions = Catalog.Options([photo], WheelMode.Actions);
        layout.AddFromRing("tool.zip", key);
        Assert.DoesNotContain("tool.zip", Ids(layout.Arrange(actions, key, Pool([photo], WheelMode.Actions))));
        layout.Unhide("tool.zip");
        Assert.Contains("tool.zip", Ids(layout.Arrange(actions, key, Pool([photo], WheelMode.Actions))));
        Assert.Contains("tool.zip", Ids(layout.Arrange(Catalog.Options([pdf], WheelMode.Tools), WheelLayout.Key(WheelMode.Tools, [pdf]))));
    }

    [Fact]
    public void SettingsFromEarlierVersionsStillLoad()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var path = Path.Combine(sandbox.Root, "wheel.json");
        var key = WheelLayout.Key(WheelMode.Tools, [photo]);
        File.WriteAllText(path, $$"""
            {
              "hidden": { "tool.ocr": "OCR" },
              "orders": { "{{key}}": ["tool.rotate", "tool.compress"] },
              "pinned": { "{{key}}": ["tool.rotate.left"] },
              "pinnedTitles": { "tool.rotate.left": "Rotate · Left 90°" },
              "defaults": { "tool.resize": "tool.resize.1920" },
              "lastUsed": { "tool.crop": "tool.crop.square" },
              "remembersLast": false
            }
            """);
        var layout = WheelLayout.Load(path);
        var tools = Catalog.Options([photo], WheelMode.Tools);
        var ids = Ids(layout.Arrange(tools, key, Pool([photo], WheelMode.Tools)));
        Assert.Equal(["tool.rotate", "tool.rotate.left"], ids.Take(2));
        Assert.DoesNotContain("tool.ocr", ids);
        Assert.False(layout.RemembersLast);
        Assert.Equal("tool.resize.1920", layout.Defaults["tool.resize"]);
        Assert.Empty(layout.Removed);
        Assert.Empty(layout.Added);
        Assert.True(layout.IsCustomizedKey(key));
        // New edits are saved with the old ones and come back after a restart.
        layout.Remove("tool.zip", key);
        layout.AddFromRing("action.copypath", key);
        var again = WheelLayout.Load(path);
        Assert.Equal(["tool.zip"], again.Removed[key]);
        Assert.Equal(["action.copypath"], again.Added[key]);
        Assert.Equal("OCR", again.Hidden["tool.ocr"]);
        Assert.Equal(["tool.rotate.left"], again.Pinned[key]);
        // Nulls and empty lists written by hand don't get in the way.
        File.WriteAllText(path, """{ "removed": null, "added": { "tools|image": null, "tools|pdf": [] }, "orders": null, "hidden": null }""");
        var tolerant = WheelLayout.Load(path);
        Assert.Empty(tolerant.Removed);
        Assert.Empty(tolerant.Added);
        Assert.Empty(tolerant.Orders);
        Assert.False(tolerant.IsCustomized);
        Assert.Equal(Ids(tools), Ids(tolerant.Arrange(tools, key, Pool([photo], WheelMode.Tools))));
    }

    [Fact]
    public void SettingsListsEverySlotOfAFileTypeOnce()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(Enum.GetValues<FileKind>(), WheelLayout.Kinds);
        foreach (var kind in WheelLayout.Kinds)
        {
            Assert.All(LayoutSamples.Files(kind), file => Assert.Equal(kind, FileKinds.Of(file)));
            // Each sample on its own, then with a second file of its kind beside it.
            var selections = LayoutSamples.Selections(kind);
            Assert.Equal(LayoutSamples.Files(kind).Count * 2, selections.Count);
            Assert.All(selections, selection => Assert.All(selection, file => Assert.Equal(kind, FileKinds.Of(file))));
            Assert.All(selections.Where(selection => selection.Count == 2), pair => Assert.NotEqual(pair[0], pair[1]));
            Assert.False(string.IsNullOrEmpty(LayoutSamples.Title(kind)));
            foreach (var mode in WheelModes.All)
            {
                var ids = Ids(LayoutSamples.Native(kind, mode));
                Assert.Equal(ids.Count, ids.Distinct().Count());
            }
        }
        Assert.Equal("photo 2.jpg", Path.GetFileName(LayoutSamples.Sibling(LayoutSamples.Files(FileKind.Image)[0])));
        Assert.True(Directory.Exists(LayoutSamples.Sibling(LayoutSamples.Files(FileKind.Folder)[0])));
        Assert.Equal(OfficeEngine.Exts.Order(), LayoutSamples.OfficeExts.Order());
        // JPEG's formats in wheel order; the ones only a GIF has go right after the format before them there, or last.
        Assert.Equal(["convert.png", "convert.jpg", "convert.webp", "convert.avif", "convert.heic", "convert.tiff", "convert.gif", "convert.bmp",
                      "convert.pdf", "convert.ico", "convert.mp4", "convert.webm"], Ids(LayoutSamples.Native(FileKind.Image, WheelMode.Convert)));
        // Slots that only come with several files (Combine to PDF, Collage, Join, Merge…) sit right after the slot before them there.
        Assert.Equal(["tool.compress", "tool.resize", "tool.crop", "tool.removebg", "tool.redact", "tool.ocr", "tool.rotate", "tool.meta",
                      "tool.topdf", "tool.collage", "tool.picturebook", "tool.zip"], Ids(LayoutSamples.Native(FileKind.Image, WheelMode.Tools)));
        Assert.Equal(["tool.compress", "tool.join"], Ids(LayoutSamples.Native(FileKind.Video, WheelMode.Tools)).Take(2));
        Assert.Equal(["tool.cut", "tool.join"], Ids(LayoutSamples.Native(FileKind.Audio, WheelMode.Tools)).Take(2));
        Assert.Equal(["tool.compress", "tool.merge"], Ids(LayoutSamples.Native(FileKind.Pdf, WheelMode.Tools)).Take(2));
        Assert.Contains("tool.epub", Ids(LayoutSamples.Native(FileKind.Document, WheelMode.Tools)));
        Assert.Contains("convert.hwpx", Ids(LayoutSamples.Native(FileKind.Hwp, WheelMode.Convert)));
        Assert.Contains("convert.hwp", Ids(LayoutSamples.Native(FileKind.Hwp, WheelMode.Convert)));
        Assert.True(LayoutSamples.Native(FileKind.Office, WheelMode.Convert).Count > 3);
        Assert.Empty(LayoutSamples.Native(FileKind.Folder, WheelMode.Convert));
        // What Settings shows is what the wheel shows, the layout and the other rings' slots included.
        var layout = WheelLayout.Shared;
        layout.AddFromRing("action.copypath", WheelLayout.Key(WheelMode.Tools, FileKind.Image));
        layout.Remove("tool.ocr", WheelLayout.Key(WheelMode.Tools, FileKind.Image));
        var arranged = LayoutSamples.Arranged(LayoutSamples.Rings(FileKind.Image), FileKind.Image, layout);
        Assert.Equal("action.copypath", arranged[WheelMode.Tools][^1].Id);
        Assert.DoesNotContain("tool.ocr", Ids(arranged[WheelMode.Tools]));
        Assert.Equal(Ids(LayoutSamples.Native(FileKind.Image, WheelMode.Convert)), Ids(arranged[WheelMode.Convert]));
    }

    [Fact]
    public void TheWheelsMenuRemovesFromThisRingAndOpensItsLayout()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var wheel = new WheelModel();
        wheel.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        var g = wheel.Geometry;
        var radius = (g.InnerRadius + g.OuterRadius) / 2;
        var first = wheel.Items[0].Id;
        WheelMode? openedRing = null;
        FileKind? openedKind = null;
        void Open(WheelMode ring, FileKind? kind)
        {
            openedRing = ring;
            openedKind = kind;
        }
        var menu = wheel.ContextMenu(0, -radius, Open);
        Assert.NotNull(menu);
        Assert.DoesNotContain(menu.Value.Entries, entry => entry.Title == L("ctx.reset_ring"));
        menu.Value.Entries.First(entry => entry.Title == L("ctx.hide")).Run!();
        var key = WheelLayout.Key(WheelMode.Tools, [photo]);
        Assert.Equal([first], WheelLayout.Shared.Removed[key]);
        Assert.Empty(WheelLayout.Shared.Hidden);
        wheel.AfterContextMenu(menu.Value.Keep);
        Assert.DoesNotContain(first, wheel.Items.Select(item => item.Id));
        menu = wheel.ContextMenu(0, -radius, Open);
        Assert.NotNull(menu);
        Assert.Contains(menu.Value.Entries, entry => entry.Title == L("ctx.reset_ring"));
        menu.Value.Entries.First(entry => entry.Title == L("ctx.settings")).Run!();
        Assert.Equal(WheelMode.Tools, openedRing);
        Assert.Equal(FileKind.Image, openedKind);
        Assert.False(wheel.IsVisible);
    }

    [Fact]
    public void PinningAnOptionOfABorrowedSlotBringsItOver()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(WheelMode.Tools, [photo]);
        layout.AddFromRing("action.copypath", key);
        var wheel = new WheelModel();
        wheel.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        var g = wheel.Geometry;
        var slot = wheel.Items.ToList().FindIndex(item => item.Id == "action.copypath");
        Assert.True(slot >= 0);
        var angle = slot * g.MainSpan(wheel.Items.Count);
        var (x, y) = WheelGeometry.Polar((g.InnerRadius + g.OuterRadius) / 2, angle);
        wheel.PointerMoved(x, y);
        Assert.NotNull(wheel.SubRing);
        (x, y) = WheelGeometry.Polar((g.SubInner + g.SubOuter) / 2, wheel.SubRing!.AngleOf(0));
        var menu = wheel.ContextMenu(x, y, (_, _) => { });
        Assert.NotNull(menu);
        menu.Value.Entries.First(entry => entry.Title == L("ctx.pin")).Run!();
        var variant = Catalog.Options([photo], WheelMode.Actions).First(option => option.Id == "action.copypath").Variants[0].Id;
        Assert.Equal(["action.copypath", variant], layout.Added[key]);
        Assert.False(layout.Pinned.ContainsKey(key));
        wheel.AfterContextMenu(menu.Value.Keep);
        Assert.Contains(variant, Ids(wheel.OptionsFor(WheelMode.Tools)));
    }
}
