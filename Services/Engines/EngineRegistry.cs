using System;
using System.Collections.Generic;
using System.Linq;

namespace Herald.Services.Engines;

public class EngineRegistry : IDisposable
{
    public WindowsTtsEngine Windows { get; }
    public KokoroEngine Kokoro { get; }
    public PiperEngine Piper { get; }
    public IReadOnlyList<ITtsEngine> All { get; }

    public EngineRegistry(string appDataDir)
    {
        Windows = new WindowsTtsEngine();
        Kokoro = new KokoroEngine(appDataDir);
        Piper = new PiperEngine(appDataDir);
        All = [Windows, Kokoro, Piper];
    }

    public ITtsEngine? Find(string? id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The engine and voice to actually use: the sender's choice when that engine is
    /// ready, otherwise Windows' default voice, so an uninstalled engine never goes silent.
    /// </summary>
    public (ITtsEngine Engine, string VoiceId) Resolve(string? engineId, string? voiceId)
    {
        var engine = Find(engineId);
        if (engine is { IsReady: true })
        {
            var voice = engine.Voices.Any(v => v.Id == voiceId) ? voiceId! : engine.DefaultVoiceId;
            return (engine, voice);
        }

        return (Windows, Windows.DefaultVoiceId);
    }

    public void Dispose() => Kokoro.Dispose();
}
