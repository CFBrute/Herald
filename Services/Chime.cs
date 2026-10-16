using System.IO;
using NAudio.Wave;

namespace Herald.Services;

/// <summary>
/// A short "dong" played when a message arrives, so it's noticed even while speech
/// is off. Has its own volume, and rings at most once every 15 seconds.
/// </summary>
public class Chime : IDisposable
{
    // However many messages or replies arrive, it rings at most this often.
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings;
    private readonly IAudioPlayer _player;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"herald-chime-{Guid.NewGuid():N}.wav");
    private readonly Lock _lock = new();
    private DateTime _lastRing = DateTime.MinValue;

    public Chime(AppSettings settings, IAudioPlayer? player = null)
    {
        _settings = settings;
        // Its own player, so the chime can sound while a message is being spoken.
        _player = player ?? new AudioPlayer();
    }

    /// <summary>Rings, unless switched off in Settings or it just rang. Doesn't wait for the sound.</summary>
    public void Ring()
    {
        if (!_settings.ChimeOnMessage) return;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRing < MinGap) return;
            _lastRing = now;
        }

        Play();
    }

    /// <summary>Plays the sound now, even if switched off - the Settings page's Test button.</summary>
    public void Play()
    {
        _ = Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    if (!File.Exists(_path)) WriteSound(_path);
                }
                _player.Volume = _settings.ChimeVolume / 100f;
                _player.Play(_path);
            }
            catch
            {
                // A missing chime is not worth disturbing anything else.
            }
        });
    }

    /// <summary>One low, decaying bell-like tone, about 0.6 seconds.</summary>
    private static void WriteSound(string path)
    {
        const int rate = 44100;
        var samples = new float[(int)(rate * 0.6)];
        AddTone(samples, rate, 392.00, start: 0.0, length: 0.6); // G4

        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static void AddTone(float[] samples, int rate, double frequency, double start, double length)
    {
        var first = (int)(start * rate);
        var count = Math.Min((int)(length * rate), samples.Length - first);
        for (var i = 0; i < count; i++)
        {
            var t = (double)i / rate;
            var attack = Math.Min(1.0, t / 0.01);            // no click at the start
            var decay = Math.Exp(-t * 6.5);
            var fade = Math.Min(1.0, (count - i) / (rate * 0.02)); // nor at the end
            var tone = Math.Sin(2 * Math.PI * frequency * t)
                     + 0.12 * Math.Sin(2 * Math.PI * frequency * 2 * t) * Math.Exp(-t * 8.0); // a soft overtone keeps it dark
            samples[first + i] += (float)(0.38 * tone * attack * decay * fade);
        }
    }

    public void Dispose()
    {
        _player.Stop();
        SafeFile.TryDelete(_path);
    }
}
