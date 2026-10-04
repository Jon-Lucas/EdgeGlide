using Microsoft.Win32;

namespace EdgeGlide;

/// <summary>"Start with Windows" is a value under HKCU\...\Run: no admin rights, no service, no scheduled task.</summary>
internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager's Startup tab records that a Run entry was disabled.
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "EdgeGlide";

    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string command
            || !string.Equals(command.Trim(), Command, StringComparison.OrdinalIgnoreCase))
            return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;   // odd = disabled
    }

    public static void Set(bool enable)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enable)
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        run.SetValue(ValueName, Command);
        // If it had been switched off in Task Manager, ticking the box here means "on" again.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
