using System;
using System.Windows;
using Microsoft.Win32;

namespace Herald.Services;

public enum ThemeChoice
{
    System,
    Light,
    Dark
}

/// <summary>
/// Light/dark support. Standard controls use WPF's Fluent theme (Application.ThemeMode),
/// which also follows Windows by itself when set to System. Herald's own colours come
/// from Themes/Light.xaml or Themes/Dark.xaml, swapped here - including when Windows
/// switches between light and dark while Herald runs.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice _choice;
    private static ResourceDictionary? _palette;
    private static bool? _paletteIsDark;

    static ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_choice == ThemeChoice.System) Application.Current?.Dispatcher.BeginInvoke(ApplyPalette);
        };
    }

    public static bool IsDark => _choice == ThemeChoice.Dark || (_choice == ThemeChoice.System && WindowsUsesDarkApps());

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
#pragma warning disable WPF0001 // ThemeMode is marked experimental in .NET 9/10
        Application.Current.ThemeMode = choice switch
        {
            ThemeChoice.Light => ThemeMode.Light,
            ThemeChoice.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };
#pragma warning restore WPF0001
        ApplyPalette();
    }

    private static void ApplyPalette()
    {
        var dark = IsDark;
        if (_paletteIsDark == dark) return;
        _paletteIsDark = dark;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml")
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_palette != null) merged.Remove(_palette);
        merged.Add(palette);
        _palette = palette;
    }

    /// <summary>Windows' "Choose your app mode" setting (0 = dark).</summary>
    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
