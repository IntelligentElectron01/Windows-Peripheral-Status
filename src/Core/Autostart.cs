using Microsoft.Win32;

namespace BTBatteryIndicator.Core;

/// <summary>R9: per-user "start with Windows" via the HKCU Run key. No admin rights involved.</summary>
public sealed class Autostart(string keyPath = Autostart.RunKey, string valueName = "BTBatteryIndicator")
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string QuietArg = "--tray";

    public static string CommandFor(string exePath) => $"\"{exePath}\" {QuietArg}";

    public bool IsEnabled(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(keyPath);
        return k?.GetValue(valueName) is string v
               && string.Equals(v, CommandFor(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public bool Set(bool enabled, string exePath)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (enabled) k.SetValue(valueName, CommandFor(exePath), RegistryValueKind.String);
            else k.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
