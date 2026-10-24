using Xunit;

namespace Pickverter.Tests;

/// The Microsoft Store edition next to the GitHub edition: how it is recognised, where it keeps its files, and that
/// Pickverter's own updater stays out of its way.
public class DistributionTests
{
    [Fact]
    public void PackageIdentityOrTheVariableMakesTheStoreEdition()
    {
        Assert.Equal(DistributionChannel.GitHub, Distribution.Resolve(false, false, null));
        Assert.Equal(DistributionChannel.GitHub, Distribution.Resolve(false, false, ""));
        Assert.Equal(DistributionChannel.GitHub, Distribution.Resolve(false, false, "github"));
        Assert.Equal(DistributionChannel.GitHub, Distribution.Resolve(false, false, "msix"));
        Assert.Equal(DistributionChannel.Store, Distribution.Resolve(true, false, null));
        Assert.Equal(DistributionChannel.Store, Distribution.Resolve(false, true, null));
        Assert.Equal(DistributionChannel.Store, Distribution.Resolve(false, false, "store"));
        Assert.Equal(DistributionChannel.Store, Distribution.Resolve(false, false, " STORE "));
        // Package identity wins over the variable: a packaged process is always the Store edition.
        Assert.Equal(DistributionChannel.Store, Distribution.Resolve(true, false, "github"));
    }

    [Fact]
    public void TheVariableStandsInUntilStartupDecides()
    {
        var before = Environment.GetEnvironmentVariable(Distribution.ChannelVariable);
        try
        {
            Distribution.Forget();
            Environment.SetEnvironmentVariable(Distribution.ChannelVariable, null);
            Assert.False(Distribution.IsStore);
            Environment.SetEnvironmentVariable(Distribution.ChannelVariable, "store");
            Assert.True(Distribution.IsStore);
            Distribution.Channel = DistributionChannel.GitHub;
            Assert.False(Distribution.IsStore);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Distribution.ChannelVariable, before);
            Distribution.Forget();
        }
    }

    [Fact]
    public void FamilyNameComesFromThePackageFolder()
    {
        Assert.Equal("ivLis-STUDIO.Pickverter_8wekyb3d8bbwe", Distribution.FamilyNameFromFolder("ivLis-STUDIO.Pickverter_0.3.1.0_x64__8wekyb3d8bbwe"));
        Assert.Equal("ivLis-STUDIO.Pickverter_8wekyb3d8bbwe", Distribution.FamilyNameFromFolder("ivLis-STUDIO.Pickverter_0.3.2.0_arm64__8wekyb3d8bbwe"));
        Assert.Null(Distribution.FamilyNameFromFolder("Pickverter"));
        Assert.Null(Distribution.FamilyNameFromFolder("stage-x64"));
        Assert.Null(Distribution.FamilyNameFromFolder("ivLis-STUDIO.Pickverter_latest_x64__8wekyb3d8bbwe"));
        Assert.Null(Distribution.FamilyNameFromFolder(""));
        Assert.Null(Distribution.FamilyNameFromFolder(null));
    }

    [Fact]
    public void StoreFoldersSitWhereWindowsWouldRedirectThem()
    {
        using var sandbox = new Sandbox();
        var localCache = Path.Combine(sandbox.Root, "Packages", "ivLis-STUDIO.Pickverter_8wekyb3d8bbwe", "LocalCache");
        var paths = AppPaths.InPackage(localCache);
        Assert.Equal(Path.Combine(localCache, "Roaming", "Pickverter"), paths.Settings);
        Assert.Equal(Path.Combine(localCache, "Local", "Pickverter"), paths.Cache);
        Assert.Equal(Path.Combine(localCache, "Local", "Temp"), paths.Temp);

        // The GitHub edition keeps the system's temp folder.
        Assert.Null(Platform.Paths.Temp);
        Assert.Equal(Path.GetTempPath(), FileSystem.TempRoot);

        var before = Platform.Paths;
        try
        {
            Platform.Paths = paths;
            Assert.Equal(paths.Temp, FileSystem.TempRoot);
            Assert.True(Directory.Exists(paths.Temp));
            var scratch = FileSystem.TempFolder("pickverter-test");
            Assert.Equal(paths.Temp, Path.GetDirectoryName(scratch));
            Assert.Equal(Path.Combine(paths.Cache, "Clipboard"), ClipboardStore.Root);
            if (Environment.GetEnvironmentVariable("PICKVERTER_FFMPEG_DIR") is not { Length: > 0 })
                Assert.StartsWith(paths.Cache, FFmpegSetup.Folder, StringComparison.Ordinal);
        }
        finally
        {
            Platform.Paths = before;
        }
    }

    [Fact]
    public void OldScratchFilesGoAndNewOnesStay()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "temp");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, "old.png");
        var fresh = Path.Combine(folder, "fresh.png");
        var oldFolder = Path.Combine(folder, "edge-old");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        Directory.CreateDirectory(oldFolder);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        Directory.SetLastWriteTimeUtc(oldFolder, DateTime.UtcNow.AddDays(-3));
        Assert.Equal(2, FileSystem.PruneEntries(folder, TimeSpan.FromDays(2)));
        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(oldFolder));
        Assert.True(File.Exists(fresh));
        Assert.Equal(0, FileSystem.PruneEntries(Path.Combine(folder, "missing"), TimeSpan.FromDays(2)));
    }

    [Fact]
    public async Task UpdaterStaysOffInTheStoreEdition()
    {
        using var sandbox = new Sandbox();
        var updater = Updater.Shared;
        var main = new RecordingMain();
        var installer = new RecordingInstaller();
        var test = Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_TEST");
        var url = Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_URL");
        try
        {
            // A release build as far as the updater can tell, pointed at an address that refuses connections.
            Environment.SetEnvironmentVariable("PICKVERTER_UPDATE_TEST", "1");
            Environment.SetEnvironmentVariable("PICKVERTER_UPDATE_URL", "http://127.0.0.1:9/update-windows.json");
            Platform.Main = main;
            updater.Installer = installer;

            Distribution.Channel = DistributionChannel.GitHub;
            Assert.True(updater.IsEnabled);
            updater.Start();
            Assert.Equal(1, main.Timers);

            Distribution.Channel = DistributionChannel.Store;
            Assert.False(updater.IsEnabled);
            updater.Start();
            Assert.Equal(1, main.Timers);

            // Checking: nothing happens, not even a "disabled" failure.
            updater.Simulate(Updater.UpdatePhase.Idle, null);
            await updater.CheckAsync(true);
            updater.CheckIfDue();
            Assert.Equal(Updater.UpdatePhase.Idle, updater.Phase);
            Assert.Null(updater.Failure);
            Assert.Null(Preferences.Shared.LastUpdateCheck);
            Assert.Equal(L("update.store"), updater.StatusText);

            // An update it somehow knew about: no download, no installer, no relaunch.
            updater.Simulate(Updater.UpdatePhase.Available, new UpdateManifest { Version = "9.9.9" });
            updater.Download();
            Assert.Equal(Updater.UpdatePhase.Available, updater.Phase);
            updater.InstallAndRelaunch();
            Assert.Equal(Updater.UpdatePhase.Available, updater.Phase);
            Assert.Equal(0, installer.Calls);
            Assert.Null(Preferences.Shared.UpdatedFrom);
            await Assert.ThrowsAsync<PickException>(() => updater.Stage(new UpdateManifest { Version = "9.9.9" }, CancellationToken.None));
        }
        finally
        {
            updater.Simulate(Updater.UpdatePhase.Idle, null);
            updater.Installer = null;
            Distribution.Forget();
            Platform.Main = new InlineMainThread();
            Environment.SetEnvironmentVariable("PICKVERTER_UPDATE_TEST", test);
            Environment.SetEnvironmentVariable("PICKVERTER_UPDATE_URL", url);
        }
    }

    [Fact]
    public async Task StartupStatesSayWhenPickverterCantSwitchIt()
    {
        using var sandbox = new Sandbox();
        Assert.True(StartupState.Enabled.IsOn());
        Assert.True(StartupState.EnabledByPolicy.IsOn());
        Assert.False(StartupState.Disabled.IsOn());
        Assert.False(StartupState.DisabledByUser.IsOn());
        Assert.False(StartupState.Unavailable.IsOn());

        Assert.False(StartupState.Enabled.IsLocked());
        Assert.False(StartupState.Disabled.IsLocked());
        foreach (var locked in new[] { StartupState.DisabledByUser, StartupState.DisabledByPolicy, StartupState.EnabledByPolicy, StartupState.Unavailable })
            Assert.True(locked.IsLocked());

        Assert.Null(StartupState.Enabled.Hint());
        Assert.Null(StartupState.Disabled.Hint());
        Assert.Contains("Task Manager", StartupState.DisabledByUser.Hint());
        Assert.Equal(L("startup.policy"), StartupState.DisabledByPolicy.Hint());
        Assert.Equal(L("startup.policy"), StartupState.EnabledByPolicy.Hint());

        // The Run key (and anything else with only on and off) gets the plain states for free.
        IStartup runKey = new NoStartup();
        Assert.Equal(StartupState.Disabled, runKey.State);
        runKey.LaunchAtLogin = true;
        Assert.Equal(StartupState.Enabled, runKey.State);
        Action changed = () => { };
        runKey.Changed += changed;
        runKey.Changed -= changed;
        await runKey.RefreshAsync();
    }

    [Fact]
    public void StoreStringsAreTranslated()
    {
        foreach (var key in new[] { "update.store", "startup.off_by_user", "startup.policy", "perm.cfa.store" })
        foreach (var language in Loc.Languages)
            Assert.False(string.IsNullOrWhiteSpace(Loc.Value(key, language.Code)), key + " [" + language.Code + "]");
    }

    private sealed class RecordingMain : IMainThread
    {
        public int Timers { get; private set; }
        public bool IsMainThread => true;
        public void Post(Action action) => action();

        public Task RunAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> RunAsync<T>(Func<T> func) => Task.FromResult(func());

        public IDisposable After(TimeSpan delay, Action action)
        {
            Timers++;
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        public int Calls { get; private set; }
        public bool CanInstallInPlace => true;

        public Task<string> StageAsync(string zipPath, string version, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(zipPath);
        }

        public void InstallAndRelaunch(string stagedExecutable) => Calls++;
    }
}
