using System.IO;
using NAudio.Wave;

namespace Herald.Services;

/// <summary>
/// A short "ding-dong" played when a message arrives, so it's noticed even while speech
/// is off. Follows Herald's volume; several messages arriving at once ring only once.
/// </summary>
public class Chime : IDisposable
{
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1.5);

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

        _ = Task.Run(() =>
        {
            try
            {
                lock (_lock)
                {
                    if (!File.Exists(_path)) WriteSound(_path);
                }
                _player.Volume = _settings.Volume / 100f;
                _player.Play(_path);
            }
            catch
            {
                // A missing chime is not worth disturbing anything else.
            }
        });
    }

    /// <summary>Two decaying bell-like tones, high then low, about a second in all.</summary>
    private static void WriteSound(string path)
    {
        const int rate = 44100;
        var samples = new float[(int)(rate * 1.1)];
        AddTone(samples, rate, 659.25, start: 0.0, length: 0.6);  // E5
        AddTone(samples, rate, 523.25, start: 0.28, length: 0.8); // C5

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
            var attack = Math.Min(1.0, t / 0.005);           // no click at the start
            var decay = Math.Exp(-t * 5.0);
            var fade = Math.Min(1.0, (count - i) / (rate * 0.02)); // nor at the end
            var tone = Math.Sin(2 * Math.PI * frequency * t)
                     + 0.3 * Math.Sin(2 * Math.PI * frequency * 2 * t) * Math.Exp(-t * 4.0);
            samples[first + i] += (float)(0.3 * tone * attack * decay * fade);
        }
    }

    public void Dispose()
    {
        _player.Stop();
        SafeFile.TryDelete(_path);
    }
}
