namespace Herald.Models;

/// <summary>
/// Part of a sender's settings: when the named language profile is detected in a part,
/// speak that part with this engine and voice instead of the sender's default voice.
/// </summary>
public record LanguageVoiceRule(string LanguageName, string EngineId, string VoiceId);
