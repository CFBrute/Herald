using System;
using Microsoft.Win32;

namespace Herald.Services;

/// <summary>
/// "Start with Windows" through the current user's Run key: works for a plain exe (no
/// installer, no admin rights) and shows up under Windows Settings > Apps > Startup.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Herald";
    public const string MinimizedArgument = "--minimized";

    private static string Command => $"\"{Environment.ProcessPath}\" {MinimizedArgument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, Command);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If start-with-Windows is on but points at an old exe location, point it at this one.</summary>
    public static void RepairPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is string current && current != Command) Enable();
        }
        catch
        {
            // registry unavailable - leave it as it is
        }
    }
}
