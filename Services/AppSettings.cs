using System;
using System.Runtime.CompilerServices;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Herald's general options (speed, volume, splitting, theme, ...), saved to
/// engine-settings.json whenever one changes. Every value is clamped to its valid range,
/// both when set and when read from the file.
/// </summary>
public class AppSettings : ObservableObject
{
    private readonly string _filePath;

    public const int MinSpeedPercent = 50;
    public const int MaxSpeedPercent = 300;

    private int _speedPercent = 150;
    public int SpeedPercent
    {
        get => _speedPercent;
        set => Set(ref _speedPercent, Math.Clamp(value, MinSpeedPercent, MaxSpeedPercent));
    }

    private int _chunkThreshold = 300;
    /// <summary>Messages longer than this many characters are split into parts.</summary>
    public int ChunkThreshold
    {
        get => _chunkThreshold;
        set => Set(ref _chunkThreshold, Math.Clamp(value, 50, 5000));
    }

    private int _chunkTargetLength = 250;
    /// <summary>Approximate size of each part, in characters.</summary>
    public int ChunkTargetLength
    {
        get => _chunkTargetLength;
        set => Set(ref _chunkTargetLength, Math.Clamp(value, 40, 2000));
    }

    private int _historyLimit = 200;
    /// <summary>How many History items (and their audio files) are kept; older ones are deleted.</summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set => Set(ref _historyLimit, Math.Clamp(value, 10, 5000));
    }

    private bool _askToConnectClaude = true;
    /// <summary>Whether Herald asks at startup to connect Claude Code when it isn't connected.</summary>
    public bool AskToConnectClaude
    {
        get => _askToConnectClaude;
        set => Set(ref _askToConnectClaude, value);
    }

    private int _uiScalePercent = 100;
    /// <summary>
    /// Size of everything inside Herald's windows, 70-130%. The Fluent theme's controls
    /// are large on small high-DPI screens; this lets the user zoom the UI out (or in).
    /// </summary>
    public int UiScalePercent
    {
        get => _uiScalePercent;
        set => Set(ref _uiScalePercent, Math.Clamp(value, 70, 130));
    }

    private int _volume = 100;
    /// <summary>
    /// Herald's own playback volume, 0-100%. Scales the audio Herald plays (all engines),
    /// not the Windows volume. Applies to a part that's already playing too.
    /// </summary>
    public int Volume
    {
        get => _volume;
        set => Set(ref _volume, Math.Clamp(value, 0, 100));
    }

    private ThemeChoice _theme = ThemeChoice.System;
    /// <summary>Light, dark, or follow Windows (the default).</summary>
    public ThemeChoice Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    private bool _trayHintShown;
    /// <summary>Whether the "Herald is still running in the tray" notice was ever shown.</summary>
    public bool TrayHintShown
    {
        get => _trayHintShown;
        set => Set(ref _trayHintShown, value);
    }

    private bool _readClipboardAutomatically;
    /// <summary>Read any text copied to the clipboard (sender "clipboard"). Off by default.</summary>
    public bool ReadClipboardAutomatically
    {
        get => _readClipboardAutomatically;
        set => Set(ref _readClipboardAutomatically, value);
    }

    public AppSettings(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        if (SafeFile.ReadJson<SettingsDto>(_filePath) is not { } dto) return;

        // Assigned through the properties so the file's values are clamped too.
        _loading = true;
        // Files saved before these options existed read them as 0 (or null): keep the defaults.
        if (dto.SpeedPercent > 0) SpeedPercent = dto.SpeedPercent;
        if (dto.ChunkThreshold > 0) ChunkThreshold = dto.ChunkThreshold;
        if (dto.ChunkTargetLength > 0) ChunkTargetLength = dto.ChunkTargetLength;
        if (dto.HistoryLimit > 0) HistoryLimit = dto.HistoryLimit;
        if (dto.AskToConnectClaude is { } ask) AskToConnectClaude = ask;
        if (dto.ReadClipboardAutomatically is { } readClipboard) ReadClipboardAutomatically = readClipboard;
        if (dto.TrayHintShown is { } hintShown) TrayHintShown = hintShown;
        if (Enum.TryParse<ThemeChoice>(dto.Theme, out var theme)) Theme = theme;
        if (dto.Volume is { } volume) Volume = volume;
        if (dto.UiScalePercent is { } scale) UiScalePercent = scale;
        _loading = false;
    }

    private bool _loading;

    private void Save() =>
        SafeFile.WriteJson(_filePath, new SettingsDto(_speedPercent, _chunkThreshold, _chunkTargetLength, _historyLimit,
                                                      _askToConnectClaude, _readClipboardAutomatically, _trayHintShown,
                                                      _theme.ToString(), _volume, _uiScalePercent));

    // Property names are part of the file format; keep them when renaming properties.
    private record SettingsDto(int SpeedPercent, int ChunkThreshold = 0, int ChunkTargetLength = 0, int HistoryLimit = 0,
                               bool? AskToConnectClaude = null, bool? ReadClipboardAutomatically = null,
                               bool? TrayHintShown = null, string? Theme = null, int? Volume = null,
                               int? UiScalePercent = null);

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (SetField(ref field, value, name) && !_loading) Save();
    }
}
