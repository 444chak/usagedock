using Microsoft.Win32;

namespace UsageDock.Services;

/// <summary>
/// Start with Windows, via the per user Run key. No admin rights and no scheduled task needed.
/// </summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsageDock";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read autostart state", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{HookInstaller.CurrentExecutablePath()}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not change autostart state", ex);
            return false;
        }
    }
}
