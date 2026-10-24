using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pickverter.Input;
using Pickverter.Services;

namespace Pickverter.UI;

/// Settings, laid out like Windows 11 Settings: pages on the left, rows of cards on the right.
internal sealed class SettingsWindow : AppWindow
{
    private SettingsPage page = SettingsPage.General;
    private readonly Grid layout = new();
    private readonly SpacedStack navigation = new() { Spacing = 2, Margin = new Thickness(12, 8, 8, 12) };
    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly SpacedStack body = new() { Spacing = 4, Margin = new Thickness(28, 4, 28, 28), MaxWidth = 860 };
    private readonly FocusRegistry focus = new();
    private readonly WheelLayoutEditor layoutEditor;
    private readonly IStartup startup = Platform.Startup;

    public SettingsWindow()
    {
        Width = 920;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        layoutEditor = new WheelLayoutEditor(focus, QueueRebuild);
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(244) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = new DockPanel();
        var brand = Ui.Row(10, new Image { Source = BrandArt.Glyph(), Width = 22, Height = 22 }, Ui.Text("Pickverter", 13, FontWeights.SemiBold));
        brand.Margin = new Thickness(22, 16, 0, 14);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        side.Children.Add(navigation);
        layout.Children.Add(side);
        scroller.Content = body;
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(scroller);
        Content = layout;
        Preferences.Shared.PropertyChanged += OnPreference;
        WheelLayout.Shared.PropertyChanged += OnLayout;
        Updater.Shared.PropertyChanged += OnUpdater;
        FFmpegSetup.Changed += OnEngine;
        HwpSetup.Changed += OnEngine;
        startup.Changed += OnStartupState;
        Activated += (_, _) =>
        {
            // Start with Windows may have been switched in Task Manager meanwhile (Store edition; a no-op otherwise).
            _ = startup.RefreshAsync();
            if (page == SettingsPage.Integrations)
            {
                Tools.Refresh();
                Rebuild();
            }
        };
        Closed += (_, _) =>
        {
            Preferences.Shared.PropertyChanged -= OnPreference;
            WheelLayout.Shared.PropertyChanged -= OnLayout;
            Updater.Shared.PropertyChanged -= OnUpdater;
            FFmpegSetup.Changed -= OnEngine;
            HwpSetup.Changed -= OnEngine;
            startup.Changed -= OnStartupState;
        };
        Rebuild();
    }

    private bool building;

    private void OnPreference(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // Controls changed by the user already show the new value; rebuild only for values that change wording.
        if (building) return;
        if (args.PropertyName is nameof(Preferences.TriggerKey) or nameof(Preferences.Language) or "") Rebuild();
    }

    private void OnLayout(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (page == SettingsPage.Wheel && !building) QueueRebuild();
    }

    private bool rebuildQueued;

    /// One new page for a burst of changes (Reset touches several things), once the control that made them is done.
    private void QueueRebuild()
    {
        if (rebuildQueued) return;
        rebuildQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            rebuildQueued = false;
            Rebuild();
        });
    }

    private void OnUpdater(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (page == SettingsPage.Updates && !building) Rebuild();
    }

    /// Windows answered for Start with Windows (Store edition): the switch and its hint follow.
    private void OnStartupState()
    {
        if (page == SettingsPage.General && !building) QueueRebuild();
    }

    private FFmpegSetup.SetupPhase shownPhase = FFmpegSetup.Phase;
    private HwpSetup.SetupPhase shownHwpPhase = HwpSetup.Phase;
    private DateTime lastEngineRebuild = DateTime.MinValue;

    /// The ffmpeg and rhwp rows follow their downloads: every phase change, progress at most a few times a second.
    private void OnEngine()
    {
        if (page != SettingsPage.Integrations || building) return;
        var phaseChanged = FFmpegSetup.Phase != shownPhase || HwpSetup.Phase != shownHwpPhase;
        if (!phaseChanged && (DateTime.UtcNow - lastEngineRebuild).TotalMilliseconds < 300) return;
        shownPhase = FFmpegSetup.Phase;
        shownHwpPhase = HwpSetup.Phase;
        lastEngineRebuild = DateTime.UtcNow;
        Rebuild();
    }

    public void Select(SettingsPage target)
    {
        page = target;
        Rebuild();
    }

    /// Settings › Wheel on a ring and, when given, a file type and slot ("Edit Wheel Layout…" in the wheel).
    public void ShowLayout(WheelMode ring, FileKind? kind, string? slot = null)
    {
        layoutEditor.Open(ring, kind, slot);
        Select(SettingsPage.Wheel);
        scroller.ScrollToTop();
    }

    protected override void Rebuild()
    {
        // The page is built anew: the keyboard focus goes back to the same control afterwards.
        var restore = focus.Take(IsKeyboardFocusWithin);
        focus.Clear();
        building = true;
        try
        {
            Title = L("settings.title");
            BuildNavigation();
            body.Children.Clear();
            var title = Ui.Text(PageName(page), 28, FontWeights.SemiBold);
            title.Margin = new Thickness(2, 18, 0, 14);
            body.Children.Add(title);
            switch (page)
            {
                case SettingsPage.General: General(); break;
                case SettingsPage.Shortcuts: Shortcuts(); break;
                case SettingsPage.Wheel: Wheel(); break;
                case SettingsPage.Integrations: Integrations(); break;
                case SettingsPage.Updates: Updates(); break;
                default: About(); break;
            }
        }
        finally
        {
            building = false;
        }
        if (restore != null) focus.Restore(restore, Dispatcher);
    }

    private static string PageName(SettingsPage page) => page switch
    {
        SettingsPage.General => L("tab.general"),
        SettingsPage.Shortcuts => L("tab.shortcuts"),
        SettingsPage.Wheel => L("tab.wheel"),
        SettingsPage.Integrations => L("tab.integrations"),
        SettingsPage.Updates => L("tab.updates"),
        _ => L("tab.about"),
    };

    private static string Symbol(SettingsPage page) => page switch
    {
        SettingsPage.General => "gearshape",
        SettingsPage.Shortcuts => "keyboard",
        SettingsPage.Wheel => "target",
        SettingsPage.Integrations => "puzzlepiece.extension",
        SettingsPage.Updates => "arrow.down.circle",
        _ => "info.circle",
    };

    private void BuildNavigation()
    {
        navigation.Children.Clear();
        foreach (var entry in Enum.GetValues<SettingsPage>())
        {
            var selected = entry == page;
            var indicator = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                Background = selected ? Theme.Accent : Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var row = Ui.Row(12, Glyphs.Icon(Symbol(entry), 16, Theme.TextPrimary), Ui.Text(PageName(entry), 14));
            var grid = new DockPanel();
            DockPanel.SetDock(indicator, Dock.Left);
            grid.Children.Add(indicator);
            grid.Children.Add(row);
            var item = new Border
            {
                Height = 38,
                CornerRadius = new CornerRadius(4),
                Background = selected ? Theme.SubtleHover : Brushes.Transparent,
                Child = grid,
                Cursor = Cursors.Hand,
                Focusable = true,
                FocusVisualStyle = Ui.FocusRing(4),
            };
            AutomationProperties.SetName(item, PageName(entry));
            var target = entry;
            item.MouseEnter += (_, _) =>
            {
                if (target != page) item.Background = Theme.SubtleHover;
            };
            item.MouseLeave += (_, _) =>
            {
                if (target != page) item.Background = Brushes.Transparent;
            };
            item.MouseLeftButtonUp += (_, _) => Select(target);
            item.KeyDown += (_, args) =>
            {
                if (args.Key is Key.Enter or Key.Space) Select(target);
            };
            navigation.Children.Add(item);
        }
    }

    private void Add(UIElement element) => body.Children.Add(element);
    private void Header(string text) => Add(Ui.SectionHeader(text));

    private void Footer(params string[] lines)
    {
        var stack = Ui.Column(4);
        foreach (var line in lines) stack.Children.Add(Ui.Caption(line));
        stack.Margin = new Thickness(2, 6, 0, 0);
        Add(stack);
    }

    // General

    private void General()
    {
        var prefs = Preferences.Shared;
        Add(Ui.SettingRow(L("set.enable"), L("set.enable.d"), Ui.Toggle(prefs.Enabled, value => prefs.Enabled = value, L("set.enable")), "power"));
        // Locked when it was switched off in Task Manager or by policy (Store edition): the hint says where to change it.
        var login = startup.State;
        var loginToggle = Ui.Toggle(login.IsOn(), value => prefs.LaunchAtLogin = value, L("set.login"));
        loginToggle.IsEnabled = !login.IsLocked();
        Add(Ui.SettingRow(L("set.login"), login.Hint() ?? L("set.login.d"), loginToggle, "arrow.clockwise"));
        Add(Ui.SettingRow(L("set.sounds"), null, Ui.Toggle(prefs.PlaySounds, value => prefs.PlaySounds = value, L("set.sounds")), "speaker.wave.2"));
        var languages = new List<(string, string)> { ("system", L("set.language.system")) };
        languages.AddRange(Loc.Languages.Select(language => (language.Code, language.Name)));
        Add(Ui.SettingRow(L("set.language"), null, Ui.Combo(languages, prefs.Language, value => prefs.Language = value, L("set.language")), "globe"));

        Header(L("set.results"));
        Add(Ui.SettingRow(L("set.destination"), null,
            Ui.Combo(ResultDestinations.All.Select(value => (value, value.Label())), prefs.Destination, value => prefs.Destination = value, L("set.destination")),
            "doc.on.clipboard"));
        Add(Ui.SettingRow(L("set.location"), null,
            Ui.Combo(OutputLocations.All.Select(value => (value, value.Label())), prefs.OutputLocation, value => prefs.OutputLocation = value, L("set.location")),
            "folder"));
        Footer(L("set.override_hint"), L("set.never_overwrite"));

        Header(L("set.cards"));
        Add(Ui.SettingRow(L("set.hud"), null,
            Ui.Combo(HudPositions.All.Select(value => (value, value.Label())), prefs.HudPosition, value => prefs.HudPosition = value, L("set.hud")),
            "rectangle.on.rectangle"));
        var jobs = new List<(int, string)> { (0, L("set.jobs.auto", WorkQueue.Shared.LimitForAuto.ToString(Loc.Culture))) };
        jobs.AddRange(Enumerable.Range(1, 4).Select(count => (count, count.ToString(Loc.Culture))));
        Add(Ui.SettingRow(L("set.jobs"), L("set.jobs.d"), Ui.Combo(jobs, prefs.MaxJobs, value => prefs.MaxJobs = value, L("set.jobs"), 140), "square.grid.2x2"));
    }

    // Shortcuts

    private void Shortcuts()
    {
        var prefs = Preferences.Shared;
        var trigger = prefs.TriggerKey.Glyph();
        Header(L("set.finder_section"));
        Add(Ui.SettingRow(L("set.trigger"), L("set.trigger.d"),
            Ui.Combo(TriggerKeys.All.Select(value => (value, value.Label())), prefs.TriggerKey, value => prefs.TriggerKey = value, L("set.trigger"), 150),
            "keyboard"));
        Add(Ui.SettingRow(L("set.tap", trigger), null, Ui.Toggle(prefs.TapTrigger, value => prefs.TapTrigger = value, L("set.tap", trigger))));
        Add(Ui.SettingRow(L("set.click", trigger), null, Ui.Toggle(prefs.ClickTrigger, value => prefs.ClickTrigger = value, L("set.click", trigger))));
        Add(Ui.SettingRow(L("set.double"), null, Ui.Toggle(prefs.DoubleTap, value => prefs.DoubleTap = value, L("set.double"))));

        Header(L("set.drag_section"));
        Add(Ui.SettingRow(L("set.drag"), null, Ui.Toggle(prefs.DragTrigger, value => prefs.DragTrigger = value, L("set.drag"))));
        Add(KeyRows([([KeyNames.Shift], L("mode.convert")), ([KeyNames.Ctrl], L("mode.tools")), ([KeyNames.Alt], L("mode.actions"))]));

        Header(L("set.inwheel"));
        Add(KeyRows(
        [
            (["←", "→", "1–0"], L("keys.arrows")),
            (["↑", "↓"], L("keys.updown")),
            (["a–z"], L("keys.type")),
            ([KeyNames.Tab, KeyNames.Shift, KeyNames.Ctrl, KeyNames.Alt], L("keys.tab")),
            ([KeyNames.Combo(KeyNames.Ctrl, "C"), KeyNames.Combo(KeyNames.Ctrl, "S"), KeyNames.Combo(KeyNames.Alt, L("key.click"))], L("keys.destination")),
            ([KeyNames.Enter, KeyNames.Esc], L("keys.enter")),
        ]));
    }

    private static Border KeyRows(IReadOnlyList<(string[] Keys, string Text)> rows)
    {
        var stack = Ui.Column(0);
        for (var index = 0; index < rows.Count; index++)
        {
            var (keys, text) = rows[index];
            var grid = new Grid { MinHeight = 44 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var caps = Ui.Row(6);
            foreach (var key in keys) caps.Children.Add(KeyCap.Make(key, 12));
            caps.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(caps);
            var label = Ui.Text(text, 14, brush: Theme.TextSecondary, wrap: true, alignment: TextAlignment.Right);
            label.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
            stack.Children.Add(grid);
            if (index < rows.Count - 1) stack.Children.Add(new Border { Height = 1, Background = Theme.Divider });
        }
        return Ui.Card(stack, new Thickness(16, 2, 16, 2));
    }

    // Wheel

    private void Wheel()
    {
        var layout = WheelLayout.Shared;
        Header(L("set.layout"));
        Add(layoutEditor.Build());

        var remember = Ui.SettingRow(L("set.wheel.remember"), L("set.wheel.remember.d"),
            focus.Register(Ui.Toggle(layout.RemembersLast, value => layout.RemembersLast = value, L("set.wheel.remember")), "wheel.remember"), "clock",
            keepWords: true);
        remember.Margin = new Thickness(0, 12, 0, 0);
        Add(remember);

        // Slots hidden in every ring by earlier versions (removing now works one ring and file type at a time).
        if (layout.Hidden.Count > 0)
        {
            var hidden = layout.Hidden.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            var show = Ui.DropDown(L("set.wheel.show"), null, () =>
            {
                var menu = new ContextMenu();
                foreach (var id in hidden) menu.Items.Add(TrayFlyout.MenuItem(layout.Title(id, layout.Hidden[id]), null, () => layout.Unhide(id)));
                return menu;
            });
            focus.Register(show, "wheel.hidden");
            Add(Ui.SettingRow(L("set.wheel.hidden"), string.Join(", ", hidden.Select(id => layout.Title(id, layout.Hidden[id]))), show, "eye.slash",
                keepWords: true));
        }

        var footer = new Grid { Margin = new Thickness(2, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Ui.Paragraph(L("set.wheel.footer"), 12, Theme.TextSecondary);
        note.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(note);
        var reset = Ui.Button(L("set.wheel.reset_all"), layout.ResetAll);
        reset.IsEnabled = layout.IsCustomized || layout.LastUsed.Count > 0;
        reset.Margin = new Thickness(16, 0, 0, 0);
        reset.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(reset, 1);
        footer.Children.Add(focus.Register(reset, "wheel.reset_all"));
        Add(footer);
    }

    // Integrations

    private void Integrations()
    {
        var prefs = Preferences.Shared;
        Header(L("set.permissions"));
        var input = InputMonitor.Shared.IsRunning;
        Add(Ui.SettingRow(L("perm.input"), input ? L("perm.input.d") : L("perm.input.failed"), Status(input, input ? L("perm.active") : L("perm.off")), "cursorarrow.click"));
        Add(FolderRow());

        Header(L("set.apps"));
        var terminals = Terminals.Installed.Count > 0 ? Terminals.Installed : Terminals.All;
        Add(Ui.SettingRow(L("set.terminal"), null,
            Ui.Combo(terminals.Select(kind => (kind, kind.Label())), Terminals.Preferred, value => prefs.Terminal = value, L("set.terminal")),
            "terminal"));
        Add(Ui.SettingRow(L("set.editor"), string.Join(", ", global::Pickverter.Editors.Installed.Select(editor => editor.Label)), null, "chevron.left.forwardslash.chevron.right"));

        Header(L("set.engines"));
        Add(FFmpegRow());
        Add(HwpRow());
        var ocr = WinVision.OcrLanguages;
        Add(Ui.SettingRow(L("engine.ocr"), ocr.Count > 0 ? string.Join(", ", ocr) : L("engine.ocr.none"),
            ocr.Count > 0 ? Status(true, L("office.installed")) : Ui.Button(L("engine.ocr.add"), () => Platform.Shell.OpenUrl("ms-settings:regionlanguage")),
            "text.viewfinder"));
        Add(Ui.SettingRow(L("engine.heic"), L("engine.heic.d"),
            Platform.Codecs.CanEncodeHeic ? Status(true, L("office.installed")) : Ui.Button(L("engine.heic.get"), () => Platform.Shell.OpenUrl("ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7")),
            "photo"));
        Add(ModelRow());
        Footer(L("engine.footer"));

        Header(L("set.office"));
        foreach (var (name, app, purpose) in new (string, OfficeApp, string)[]
        {
            ("Microsoft Word", OfficeApp.Word, L("office.word")),
            ("Microsoft Excel", OfficeApp.Excel, L("office.excel")),
            ("Microsoft PowerPoint", OfficeApp.PowerPoint, L("office.powerpoint")),
        })
        {
            var installed = Platform.Office.Has(app);
            Add(Ui.SettingRow(name, purpose, Status(installed, installed ? L("office.installed") : L("office.missing")), "doc.text"));
        }
        var libre = Tools.LibreOffice != null;
        Add(Ui.SettingRow("LibreOffice", L("office.libre"), Status(libre, libre ? L("office.installed") : L("office.missing")), "doc.text"));
        Footer(L("office.footer"));
    }

    private Border FolderRow()
    {
        var state = FolderProtection.State;
        var store = Distribution.IsStore;
        UIElement control = state switch
        {
            // The Store edition doesn't elevate: Windows Security, where the user adds Pickverter themselves.
            FolderProtectionState.Blocking when store => Ui.Button(L("perm.cfa.open"), FolderProtection.OpenSettings, prominent: true),
            FolderProtectionState.Blocking => Ui.Row(8,
                Ui.Button(L("perm.cfa.allow"), () => _ = AllowFolders(), prominent: true),
                Ui.Button(L("perm.cfa.open"), FolderProtection.OpenSettings)),
            FolderProtectionState.Allowed => Status(true, L("perm.cfa.allowed")),
            _ => Status(true, L("perm.cfa.off")),
        };
        var detail = state != FolderProtectionState.Blocking ? L("perm.cfa.d") : store ? L("perm.cfa.store") : L("perm.cfa.blocking");
        return Ui.SettingRow(L("perm.cfa.title"), detail, control, "lock.shield");
    }

    /// ffmpeg: download it right here (progress in the row), remove the downloaded copy, or fall back to winget.
    private Border FFmpegRow()
    {
        var ffmpeg = Tools.FFmpeg;
        var actions = Ui.Row(8);
        string detail;
        if (FFmpegSetup.IsWorking)
        {
            detail = FFmpegSetup.StatusLine ?? "";
            actions.Children.Add(Ui.Button(L("common.cancel"), FFmpegSetup.Cancel));
        }
        else if (ffmpeg == null)
        {
            var failed = FFmpegSetup.Phase == FFmpegSetup.SetupPhase.Failed;
            detail = failed ? FFmpegSetup.Failure ?? "" : L("engine.ffmpeg.offer");
            if (failed && EngineInstaller.HasWinget) actions.Children.Add(Ui.Button(L("ffmpeg.winget"), EngineInstaller.InstallFFmpeg));
            actions.Children.Add(Ui.Button(failed ? L("update.retry") : L("engine.install_ffmpeg"), FFmpegSetup.Start, prominent: true));
        }
        else if (FFmpegSetup.IsManaged)
        {
            detail = L("engine.ffmpeg.managed", FFmpegSetup.InstalledVersion ?? "ffmpeg");
            actions.Children.Add(Ui.Button(L("engine.remove"), () =>
            {
                FFmpegSetup.Remove();
                Rebuild();
            }));
        }
        else
        {
            detail = ffmpeg;
        }
        if (!FFmpegSetup.IsWorking)
        {
            actions.Children.Add(Ui.Button(L("engine.rescan"), () =>
            {
                Tools.Refresh();
                if (Platform.Codecs is WinCodecs codecs) codecs.Refresh();
                Rebuild();
            }));
        }
        return Ui.SettingRow("ffmpeg · " + L("engine.media"), detail, actions, "film");
    }

    /// rhwp, the HWP engine: downloaded the first time an HWP file is converted, or here ahead of time (progress in the
    /// row); Remove deletes the downloaded copy.
    private Border HwpRow()
    {
        var actions = Ui.Row(8);
        string detail;
        if (HwpSetup.IsWorking)
        {
            detail = HwpSetup.StatusLine ?? "";
            actions.Children.Add(Ui.Button(L("common.cancel"), HwpSetup.Cancel));
        }
        else if (HwpSetup.Executable is not { } rhwp)
        {
            var failed = HwpSetup.Phase == HwpSetup.SetupPhase.Failed;
            if (HwpSetup.RunsHere)
            {
                detail = failed ? HwpSetup.Failure ?? "" : L("engine.hwp.offer");
                actions.Children.Add(Ui.Button(failed ? L("update.retry") : L("engine.install_hwp"), HwpSetup.Start, prominent: true));
            }
            else
            {
                // Windows 10 on Arm can't run the x64 build: say so instead of offering a download that can't work.
                detail = L("err.hwp_arm");
            }
        }
        else if (HwpSetup.IsManaged)
        {
            detail = L("engine.hwp.managed", "rhwp " + (HwpSetup.InstalledVersion ?? HwpSetup.Version));
            actions.Children.Add(Ui.Button(L("engine.remove"), () =>
            {
                HwpSetup.Remove();
                Rebuild();
            }));
        }
        else
        {
            detail = rhwp;
        }
        return Ui.SettingRow("rhwp · " + L("engine.hwp"), detail, actions, "doc.text");
    }

    private static CancellationTokenSource? modelDownload;
    private static double modelProgress = -1;

    /// The background-removal model downloads once (on first use, or here ahead of time).
    private Border ModelRow()
    {
        if (BackgroundModel.IsInstalled)
            return Ui.SettingRow(L("engine.bg"), L("engine.bg.ready", BackgroundModel.SizeLabel), Status(true, L("office.installed")), "scissors");
        if (modelDownload != null)
        {
            var percent = ((int)(Math.Max(0, modelProgress) * 100)).ToString(CultureInfo.InvariantCulture);
            return Ui.SettingRow(L("engine.bg"), L("bg.model.downloading", percent),
                Ui.Button(L("common.cancel"), () => modelDownload?.Cancel()), "scissors");
        }
        return Ui.SettingRow(L("engine.bg"), L("engine.bg.d", BackgroundModel.SizeLabel), Ui.Button(L("bg.model.download"), () => _ = DownloadModel()), "scissors");
    }

    private async Task DownloadModel()
    {
        modelDownload = new CancellationTokenSource();
        modelProgress = 0;
        Rebuild();
        var last = DateTime.MinValue;
        try
        {
            await BackgroundModel.DownloadAsync(fraction =>
            {
                modelProgress = fraction;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return;
                last = DateTime.UtcNow;
                Platform.Main.Post(() =>
                {
                    if (page == SettingsPage.Integrations) Rebuild();
                });
            }, modelDownload.Token);
        }
        catch (Exception error)
        {
            Debug.Log("model download: " + error.Message);
        }
        finally
        {
            modelDownload = null;
            modelProgress = -1;
            Rebuild();
        }
    }

    private async Task AllowFolders()
    {
        await FolderProtection.AllowAsync();
        Rebuild();
    }

    public static FrameworkElement Status(bool good, string text)
    {
        var icon = Glyphs.Icon(good ? "checkmark.circle.fill" : "minus.circle", 16, good ? Theme.Success : Theme.TextTertiary);
        var row = Ui.Row(6, icon, Ui.Text(text, 13, brush: Theme.TextSecondary));
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    // Updates

    private void Updates()
    {
        var prefs = Preferences.Shared;
        var updater = Updater.Shared;
        if (Distribution.IsStore)
        {
            // The Microsoft Store edition: the Store installs updates, Pickverter's own updater is off.
            Add(Ui.SettingRow(L("update.current"), L("update.store"), Ui.Text(updater.CurrentVersion, 14, brush: Theme.TextSecondary), "info.circle"));
            return;
        }
        Add(Ui.SettingRow(L("update.current"), updater.IsReleaseBuild ? null : L("about.dev_build"),
            Ui.Text(updater.CurrentVersion, 14, brush: Theme.TextSecondary), "info.circle"));
        var buttons = Ui.Row(8);
        var check = Ui.Button(L("update.check_now"), () => WindowManager.ShowUpdates(check: true));
        check.IsEnabled = updater.IsEnabled && !updater.IsBusy;
        buttons.Children.Add(check);
        if (updater.Phase == Updater.UpdatePhase.Ready) buttons.Children.Add(Ui.Button(L("update.restart"), updater.InstallAndRelaunch, prominent: true));
        Add(Ui.SettingRow(L("update.status"), updater.StatusText, buttons, "arrow.down.circle"));

        Header(L("tab.updates"));
        Add(Ui.SettingRow(L("update.auto_check"), null, Ui.Toggle(prefs.AutoCheckUpdates, value => prefs.AutoCheckUpdates = value, L("update.auto_check"))));
        var download = Ui.Toggle(prefs.AutoInstallUpdates, value => prefs.AutoInstallUpdates = value, L("update.auto_download"));
        download.IsEnabled = prefs.AutoCheckUpdates;
        Add(Ui.SettingRow(L("update.auto_download"), null, download));
        Footer(updater.IsEnabled ? L("update.footer") : L("update.disabled"));
        var link = Ui.Link(L("update.releases"), () => Platform.Shell.OpenUrl(updater.ReleasesPage));
        link.Margin = new Thickness(0, 8, 0, 0);
        Add(link);
    }

    // About

    private void About()
    {
        var updater = Updater.Shared;
        var stack = Ui.Column(10);
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(0, 16, 0, 0);
        var icon = new Image { Source = BrandArt.AppIcon(), Width = 112, Height = 112, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(icon);
        var name = Ui.Text("Pickverter", 26, FontWeights.SemiBold, alignment: TextAlignment.Center);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(name);
        var studio = Ui.Text("by ivLis STUDIO", 13, brush: Theme.TextSecondary, alignment: TextAlignment.Center);
        studio.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(studio);
        var version = Ui.Text(L("about.version", updater.CurrentVersion) + (updater.IsReleaseBuild ? "" : " · " + L("about.dev_build")), 13,
            brush: Theme.TextSecondary, alignment: TextAlignment.Center);
        version.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(version);
        var tagline = Ui.Text(L("about.tagline"), 13, brush: Theme.TextSecondary, wrap: true, alignment: TextAlignment.Center);
        tagline.MaxWidth = 460;
        tagline.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(tagline);
        var links = Ui.Row(12,
            Ui.Link(L("about.github"), () => Platform.Shell.OpenUrl(updater.RepositoryPage)),
            Ui.Link(L("about.issues"), () => Platform.Shell.OpenUrl(updater.RepositoryPage + "/issues")));
        links.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(links);
        var license = Ui.Text(L("about.license"), 12, brush: Theme.TextTertiary, alignment: TextAlignment.Center);
        license.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(license);
        Add(stack);
    }
}

/// Fallback when the download fails: installs ffmpeg with winget in a visible terminal (or opens the download page).
internal static class EngineInstaller
{
    private static string Winget => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");

    public static bool HasWinget => File.Exists(Winget);

    public static void InstallFFmpeg()
    {
        if (!HasWinget)
        {
            Platform.Shell.OpenUrl("https://www.gyan.dev/ffmpeg/builds/");
            return;
        }
        try
        {
            var command = "chcp 65001 >nul & winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements & echo. & echo " +
                          L("engine.install_done").Replace("&", "and") + " & pause";
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/c " + command)
            {
                UseShellExecute = true,
            };
            using var process = Process.Start(info);
            if (process == null) return;
            _ = process.WaitForExitAsync().ContinueWith(_ => Platform.Main.Post(Tools.Refresh));
        }
        catch (Exception error)
        {
            Debug.Log("ffmpeg install: " + error.Message);
            Platform.Shell.OpenUrl("https://www.gyan.dev/ffmpeg/builds/");
        }
    }
}
