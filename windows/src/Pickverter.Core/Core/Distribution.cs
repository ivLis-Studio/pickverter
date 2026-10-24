namespace Pickverter;

/// The two ways Pickverter for Windows ships, built from the same source.
public enum DistributionChannel
{
    /// The installer and the portable zip from GitHub Releases: Pickverter updates itself from there, starts with Windows
    /// through the Run key and works with the Inno Setup installer (--quit, --apply-update, the uninstall entry).
    GitHub,

    /// The MSIX package from the Microsoft Store: the Store updates it, Windows starts it through the package's startup
    /// task, and it keeps its files in the package's own folder. Pickverter's updater and installer helpers stay off.
    Store,
}

/// Which edition this process is. Pickverter.exe sets Channel first thing (a process with MSIX package identity is the
/// Store edition); until then, and in tests and tools, PICKVERTER_CHANNEL=store stands in for the Store edition.
public static class Distribution
{
    public const string ChannelVariable = "PICKVERTER_CHANNEL";

    private static DistributionChannel? detected;

    public static DistributionChannel Channel
    {
        get => detected ?? Resolve(false, false, Environment.GetEnvironmentVariable(ChannelVariable));
        set => detected = value;
    }

    public static bool IsStore => Channel == DistributionChannel.Store;

    /// The package's full name when the process runs with its identity ("ivLis-STUDIO.Pickverter_0.3.1.0_x64__…").
    public static string? PackageFullName { get; set; }

    /// packaged: the process has package identity. insidePackage: the executable sits in an unpacked MSIX package (an
    /// AppxManifest.xml next to its folder) but was started by path, without the identity. variable: PICKVERTER_CHANNEL.
    public static DistributionChannel Resolve(bool packaged, bool insidePackage, string? variable) =>
        packaged || insidePackage || string.Equals(variable?.Trim(), "store", StringComparison.OrdinalIgnoreCase)
            ? DistributionChannel.Store
            : DistributionChannel.GitHub;

    /// The package family name in a package's install folder name: "Name_Version_Architecture_ResourceId_PublisherId"
    /// becomes "Name_PublisherId" (package names never contain an underscore). Null for anything else.
    public static string? FamilyNameFromFolder(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        var parts = folderName.Split('_');
        if (parts.Length != 5 || parts[0].Length == 0 || parts[4].Length == 0 || !Version.TryParse(parts[1], out _)) return null;
        return parts[0] + "_" + parts[4];
    }

    /// Tests: back to "not detected yet".
    internal static void Forget()
    {
        detected = null;
        PackageFullName = null;
    }
}

/// Starting Pickverter when the user signs in. The GitHub edition's Run key is on or off; the Store edition's startup task
/// can also be switched off outside Pickverter (Task Manager › Startup apps, Settings › Apps › Startup) or by policy, and
/// then only the user (or the administrator) can switch it back on, there.
public enum StartupState { Disabled, Enabled, DisabledByUser, DisabledByPolicy, EnabledByPolicy, Unavailable }

public static class StartupStates
{
    public static bool IsOn(this StartupState state) => state is StartupState.Enabled or StartupState.EnabledByPolicy;

    /// Pickverter can't change it from its own switch.
    public static bool IsLocked(this StartupState state) =>
        state is StartupState.DisabledByUser or StartupState.DisabledByPolicy or StartupState.EnabledByPolicy or StartupState.Unavailable;

    /// Why the switch is locked and where it can be changed; null when Pickverter's switch works.
    public static string? Hint(this StartupState state) => state switch
    {
        StartupState.DisabledByUser => L("startup.off_by_user"),
        StartupState.DisabledByPolicy or StartupState.EnabledByPolicy => L("startup.policy"),
        _ => null,
    };
}
