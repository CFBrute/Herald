namespace Herald.Models;

/// <summary>
/// One of the voices a sender's sessions take turns with: the first session heard from
/// gets the first voice, the next session the next one, starting over at the end.
/// An empty VoiceId means the engine's default voice.
/// </summary>
public record SenderVoice(string EngineId, string VoiceId);
