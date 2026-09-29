using Herald.Services.Engines;

namespace Herald.Services;

/// <summary>Everything Herald runs on, created once at startup and handed to the windows.</summary>
public record AppServices(
    AppPaths Paths,
    AppSettings Settings,
    EngineRegistry Engines,
    SenderSettingsStore Senders,
    LanguageProfileStore Languages,
    HotkeySettingsStore Hotkeys,
    SpeechEngine Speech,
    HookServer HookServer,
    ClaudeCodeIntegration Claude);
