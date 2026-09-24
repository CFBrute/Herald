using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;

namespace Herald.Services.Engines;

/// <summary>
/// Windows' own voices (the ones added under Settings > Time &amp; language > Speech).
/// Always available, nothing to install from Herald's side.
/// </summary>
public class WindowsTtsEngine : ITtsEngine
{
    public string Id => "windows";
    public string DisplayName => "Windows voices";
    public string Description =>
        "Built into Windows, nothing to install. More languages (e.g. Swedish) are added through Windows Settings > Time & language > Speech.";

    public bool IsReady => Voices.Count > 0;
    public IReadOnlyList<string> MissingParts => IsReady ? [] : ["No Windows voices installed"];

    public IReadOnlyList<VoiceInfo> Voices { get; private set; } = [];
    public string DefaultVoiceId { get; private set; } = string.Empty;

    public WindowsTtsEngine()
    {
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            Voices = SpeechSynthesizer.AllVoices
                .Select(v => new VoiceInfo(v.Id, $"{v.DisplayName} ({v.Language})", v.Language))
                .OrderBy(v => v.DisplayName)
                .ToList();
            DefaultVoiceId = SpeechSynthesizer.DefaultVoice?.Id ?? Voices.FirstOrDefault()?.Id ?? string.Empty;
        }
        catch
        {
            Voices = [];
            DefaultVoiceId = string.Empty;
        }

        OnPropertyChanged(nameof(Voices));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(MissingParts));
    }

    public async Task<bool> SynthesizeAsync(string text, string voiceId, double speed, string outPath, CancellationToken ct)
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId) ?? SpeechSynthesizer.DefaultVoice;
            if (voice != null) synth.Voice = voice;
            synth.Options.SpeakingRate = Math.Clamp(speed, 0.5, 6.0);

            using var stream = await synth.SynthesizeTextToStreamAsync(text);
            using var input = stream.AsStreamForRead();
            await using var file = File.Create(outPath);
            await input.CopyToAsync(file, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens the Windows page where more voices and languages are added.</summary>
    public static void OpenWindowsSpeechSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true });
        }
        catch { }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
