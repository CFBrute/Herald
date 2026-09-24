using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Herald.Services.Engines;

public record VoiceInfo(string Id, string DisplayName, string Language)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// A speech backend (Windows built-in voices, Kokoro, ...). Engines that need extra
/// parts report what's missing instead of installing anything on their own.
/// </summary>
public interface ITtsEngine : INotifyPropertyChanged
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }

    bool IsReady { get; }

    /// <summary>Human-readable list of what's missing before the engine can be used.</summary>
    IReadOnlyList<string> MissingParts { get; }

    IReadOnlyList<VoiceInfo> Voices { get; }
    string DefaultVoiceId { get; }

    /// <summary>Writes a WAV file. Speed 1.0 is normal, 1.9 is the fastest Herald uses.</summary>
    Task<bool> SynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct);

    /// <summary>Re-checks installed parts and voices.</summary>
    void Refresh();
}
