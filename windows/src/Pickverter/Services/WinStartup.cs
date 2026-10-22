using Microsoft.Win32;

namespace Pickverter.Services;

/// "Start with Windows": the per-user Run key (Task Manager's Startup apps page can switch it off too).
internal sealed class WinStartup : IStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Pickverter";

    public static string Command => "\"" + (Environment.ProcessPath ?? "Pickverter.exe") + "\" --background";

    public bool LaunchAtLogin
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(Name) is not string) return false;
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // Odd first byte = switched off in Task Manager.
                return approved?.GetValue(Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value)
                {
                    run.SetValue(Name, Command);
                    using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
                    approved.SetValue(Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                }
                else
                {
                    run.DeleteValue(Name, false);
                }
            }
            catch (Exception error)
            {
                Debug.Log("startup: " + error.Message);
            }
        }
    }

    /// After an update or a move the stored path may be stale.
    public static void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (run?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                run.SetValue(Name, Command);
        }
        catch (Exception)
        {
        }
    }
}
