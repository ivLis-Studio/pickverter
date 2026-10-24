using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.Services;
using Pickverter.UI;

namespace Pickverter;

internal sealed class PickverterApp : Application
{
    private readonly string[] args;
    private readonly bool background;
    private readonly EventWaitHandle showSignal;
    private readonly EventWaitHandle quitSignal;

    /// background: started at sign-in (the Run key's --background, the Store edition's startup task): no window.
    public PickverterApp(string[] args, bool background, EventWaitHandle showSignal, EventWaitHandle quitSignal)
    {
        this.args = args;
        this.background = background;
        this.showSignal = showSignal;
        this.quitSignal = quitSignal;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeMode = ThemeMode.System;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Logging.Start();
        DispatcherUnhandledException += (_, error) =>
        {
            Logging.Crash(error.Exception);
            error.Handled = true;
        };
        WindowsPlatform.Install(Dispatcher);
        Preferences.Load();
        WheelLayout.Load();
        Theme.Start(Dispatcher);
        JobCenter.Shared.Presenter = new CardPresenter();
        Updater.Shared.ShowUpdates = () => WindowManager.ShowUpdates();
        WindowManager.StartTray();
        InputMonitor.Shared.Start();
        WindowsPlatform.WarmUp();
        Updater.Shared.Start();
        if (Distribution.IsStore)
        {
            StoreEdition.Started();
        }
        else
        {
            WinStartup.Refresh();
            if (args.Contains("--updated")) Installer.CleanUpAfterUpdate();
        }
        WatchForSecondLaunch();
        WelcomeOrUpdated();
    }

    private void WelcomeOrUpdated()
    {
        var prefs = Preferences.Shared;
        if (!prefs.Onboarded)
        {
            WindowManager.ShowOnboarding();
            return;
        }
        if (Updater.Shared.UpdatedFrom != null)
        {
            Updater.Shared.AcknowledgeUpdate();
            JobCenter.Shared.Notice(L("update.card.done", Updater.Shared.CurrentVersion), L("update.card.done.d"), "checkmark.circle", JobTone.Info,
                new NoticeAction(L("update.whats_new"), "info.circle", () => Platform.Shell.OpenUrl(Updater.Shared.ReleasesPage + "/tag/v" + Updater.Shared.CurrentVersion)), 10);
            return;
        }
        if (!background) WindowManager.ShowSettings();
    }

    private void WatchForSecondLaunch()
    {
        var quitter = new Thread(() =>
        {
            try
            {
                quitSignal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Dispatcher.BeginInvoke(WindowManager.Quit);
        })
        {
            IsBackground = true,
            Name = "Pickverter quit",
        };
        quitter.Start();
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    showSignal.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (!Preferences.Shared.Onboarded) WindowManager.ShowOnboarding();
                    else WindowManager.ShowSettings();
                });
            }
        })
        {
            IsBackground = true,
            Name = "Pickverter instance",
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        InputMonitor.Shared.Stop();
        WindowManager.StopTray();
        base.OnExit(e);
    }
}

internal enum SettingsPage { General, Shortcuts, Wheel, Integrations, Updates, About }

/// Opens (and reuses) the app's windows.
internal static class WindowManager
{
    private static TrayIcon? tray;
    private static TrayFlyout? flyout;
    private static SettingsWindow? settings;
    private static OnboardingWindow? onboarding;
    private static UpdateWindow? updates;

    public static void StartTray()
    {
        tray = new TrayIcon();
        tray.Clicked += _ => ToggleFlyout();
        tray.MenuRequested += _ => TrayMenu();
        InputMonitor.Shared.StateChanged += () => tray?.Refresh();
        Preferences.Shared.PropertyChanged += (_, _) => tray?.Refresh();
        Loc.Changed += () => tray?.Refresh();
        Theme.Changed += () => tray?.Refresh();
    }

    public static void StopTray()
    {
        tray?.Dispose();
        tray = null;
    }

    public static void ToggleFlyout()
    {
        flyout ??= new TrayFlyout();
        flyout.Toggle(tray?.Bounds);
    }

    public static void TrayMenu()
    {
        var owner = Windowing.Owner();
        var prefs = Preferences.Shared;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = owner, FontFamily = Theme.TextFont };
        menu.Items.Add(TrayFlyout.MenuItem(L("menu.settings"), "gearshape", () => ShowSettings()));
        var enabled = TrayFlyout.MenuItem(L("set.enable"), null, () => prefs.Enabled = !prefs.Enabled);
        enabled.IsChecked = prefs.Enabled;
        menu.Items.Add(enabled);
        if (!Distribution.IsStore)
        {
            var check = TrayFlyout.MenuItem(L("menu.check_updates"), "arrow.down.circle", () => ShowUpdates(check: true));
            check.IsEnabled = Updater.Shared.IsEnabled;
            menu.Items.Add(check);
        }
        menu.Items.Add(TrayFlyout.MenuItem(L("menu.guide"), "sparkles", ShowOnboarding));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayFlyout.MenuItem(L("menu.quit"), "power", Quit));
        menu.Closed += (_, _) => owner.Close();
        menu.IsOpen = true;
    }

    public static void ShowSettings(SettingsPage page = SettingsPage.General)
    {
        var window = Settings();
        window.Select(page);
        Bring(window);
    }

    /// Settings › Wheel on a ring and, when the files were all of one type, that file type ("Edit Wheel Layout…").
    public static void ShowWheelLayout(WheelMode ring, FileKind? kind)
    {
        var window = Settings();
        window.ShowLayout(ring, kind);
        Bring(window);
    }

    private static SettingsWindow Settings()
    {
        if (settings == null)
        {
            settings = new SettingsWindow();
            settings.Closed += (_, _) => settings = null;
        }
        return settings;
    }

    public static void ShowOnboarding()
    {
        if (onboarding == null)
        {
            onboarding = new OnboardingWindow();
            onboarding.Closed += (_, _) => onboarding = null;
        }
        Bring(onboarding);
    }

    public static void ShowUpdates(bool check = false)
    {
        // The Store edition has no Software Update window: the Store updates it.
        if (Distribution.IsStore) return;
        if (check) _ = Updater.Shared.CheckAsync(true);
        if (updates == null)
        {
            updates = new UpdateWindow();
            updates.Closed += (_, _) => updates = null;
        }
        Bring(updates);
    }

    private static void Bring(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        Windowing.Activate(window);
    }

    public static void Quit()
    {
        InputMonitor.Shared.Stop();
        StopTray();
        Application.Current.Shutdown();
    }
}

/// Crash reports and (with PICKVERTER_DEBUG=1) a trace in %LOCALAPPDATA%\Pickverter\Logs.
internal static class Logging
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(Platform.Paths.Cache, "Logs");

    public static void Start()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Crash(error.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            Write("unobserved: " + error.Exception);
            error.SetObserved();
        };
        if (Debug.Enabled) Debug.Sink = Write;
    }

    public static void Crash(Exception? error)
    {
        if (error == null) return;
        Write("crash: " + error);
    }

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var file = Path.Combine(Folder, "pickverter.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000) File.Move(file, file + ".1", true);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ", CultureInfo.InvariantCulture) + line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
        }
    }
}
