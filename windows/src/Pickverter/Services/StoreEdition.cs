using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Pickverter.Services;

/// The Microsoft Store edition: Pickverter.exe from its MSIX package. Program.Main calls Detect before anything else,
/// so the core knows the edition (Distribution) and, in the Store edition, uses the package's own folders. The WinRT
/// calls here (AppInstance, StartupTask) use the Windows SDK projection the app ships for OCR and PDF anyway.
internal static class StoreEdition
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoReboot = 8;

    private delegate int PackageNameReader(ref uint length, char[]? buffer);

    /// APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no package identity.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, [Out] char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string? commandLine, int flags);

    /// Started with the package's identity (Start, the startup task): StartupTask and activation details work.
    public static bool HasIdentity => Distribution.PackageFullName != null;

    /// A Store build started by path from its install folder, without the identity (rare: a shortcut to the exe, the
    /// command line). Still the Store edition: it never updates itself or writes a Run key.
    public static bool InsidePackageFolder { get; private set; }

    public static string? FamilyName { get; private set; }

    /// Settings, cache and scratch files live in the package's LocalCache (AppPaths.InPackage).
    public static bool UsesPackageFolders { get; private set; }

    public static void Detect()
    {
        var fullName = Read(GetCurrentPackageFullName);
        string? family = null;
        if (fullName != null)
        {
            family = Read(GetCurrentPackageFamilyName);
        }
        else
        {
            // An unpacked MSIX package: AppxManifest.xml at its root, Pickverter.exe in the Pickverter folder below it.
            var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
            if (root != null && File.Exists(Path.Combine(root, "AppxManifest.xml")))
            {
                InsidePackageFolder = true;
                family = Distribution.FamilyNameFromFolder(Path.GetFileName(root));
            }
        }
        Distribution.PackageFullName = fullName;
        Distribution.Channel = Distribution.Resolve(fullName != null, InsidePackageFolder, Environment.GetEnvironmentVariable(Distribution.ChannelVariable));
        FamilyName = family;
        if (Distribution.IsStore && family != null) UsePackageFolders(family, create: fullName != null);
    }

    private static void UsePackageFolders(string family, bool create)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) return;
            var cache = Path.Combine(local, "Packages", family, "LocalCache");
            // Without identity, only a package that is installed for this user has its folder already.
            if (create) Directory.CreateDirectory(cache);
            else if (!Directory.Exists(cache)) return;
            var paths = AppPaths.InPackage(cache);
            Directory.CreateDirectory(paths.Temp!);
            Platform.Paths = paths;
            UsesPackageFolders = true;
        }
        catch (Exception)
        {
            // The default folders still work (Windows redirects them into the package).
        }
    }

    private static string? Read(PackageNameReader api)
    {
        try
        {
            uint length = 0;
            if (api(ref length, null) != ErrorInsufficientBuffer || length == 0) return null;
            var buffer = new char[length];
            if (api(ref length, buffer) != ErrorSuccess) return null;
            return new string(buffer, 0, (int)Math.Max(0, length - 1));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Windows started the package's startup task (the user signed in), not a click on Pickverter.
    public static bool StartedAtSignIn()
    {
        if (!HasIdentity) return false;
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Once the app runs (Store edition only): Windows brings it back after the Store replaced the package under it,
    /// in the notification area; and scratch files older than two days go, since nothing else cleans the package's
    /// temp folder.
    public static void Started()
    {
        if (HasIdentity)
        {
            try
            {
                RegisterApplicationRestart("--background", RestartNoCrash | RestartNoHang | RestartNoReboot);
            }
            catch (Exception error)
            {
                Debug.Log("restart registration: " + error.Message);
            }
        }
        if (UsesPackageFolders && Platform.Paths.Temp is { } temp)
            _ = Task.Run(() => FileSystem.PruneEntries(temp, TimeSpan.FromDays(2)));
    }

    /// "cli edition": what this process is and where it keeps its files.
    public static string Describe()
    {
        var lines = new List<string>
        {
            "edition   " + (Distribution.IsStore ? "Microsoft Store" : "GitHub"),
            "package   " + (Distribution.PackageFullName ?? (InsidePackageFolder ? "installed package, started without its identity" : "none")),
        };
        if (FamilyName != null) lines.Add("family    " + FamilyName);
        lines.Add("settings  " + Platform.Paths.Settings);
        lines.Add("cache     " + Platform.Paths.Cache);
        lines.Add("temp      " + FileSystem.TempRoot);
        return string.Join(Environment.NewLine, lines);
    }
}
