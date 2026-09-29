using System.Diagnostics;
using NAudio.Wave;

namespace Herald.Services;

public enum PlaybackEnd { Finished, Stopped, TimedOut, Stalled, Error }

public record PlaybackResult(PlaybackEnd End, TimeSpan Duration, TimeSpan Elapsed, string? Error = null, int Attempts = 1);

/// <summary>What the speech queue plays its parts through (replaced by a silent fake in tests).</summary>
public interface IAudioPlayer
{
    /// <summary>0.0 to 1.0. Applies to the clip that's playing too.</summary>
    float Volume { get; set; }

    /// <summary>Plays a file and blocks until it ends or <see cref="Stop"/> is called.</summary>
    PlaybackResult Play(string path);

    /// <summary>Ends the clip that's playing; safe to call from any thread.</summary>
    void Stop();
}

/// <summary>
/// Plays WAV files through WASAPI (the modern Windows audio path). The older WaveOut now
/// and then started a clip but never played it, silently waiting out its whole length;
/// a clip whose playback doesn't move forward is started once more from the top.
/// </summary>
public class AudioPlayer : IAudioPlayer
{
    // The file being played right now, so a volume change is heard immediately.
    private volatile AudioFileReader? _currentReader;
    private ManualResetEventSlim? _currentStop;

    private volatile float _volume = 1f;
    /// <summary>0.0 to 1.0. Applies to the clip that's playing too.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_currentReader is { } reader) reader.Volume = value;
        }
    }

    /// <summary>
    /// Plays a file and blocks until it ends or <see cref="Stop"/> is called.
    /// The player is only ever touched from this thread; other threads just signal it,
    /// since calling Stop() on the player from another thread silently did nothing.
    /// </summary>
    public PlaybackResult Play(string path)
    {
        var stop = new ManualResetEventSlim(false);
        _currentStop = stop;
        var watch = Stopwatch.StartNew();
        var duration = TimeSpan.Zero;
        try
        {
            using var reader = new AudioFileReader(path);
            reader.Volume = _volume;
            _currentReader = reader;
            duration = reader.TotalTime;

            const int maxAttempts = 2;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                reader.Position = 0;

                using var output = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).Build();
                using var done = new ManualResetEventSlim(false);

                Exception? playbackError = null;
                output.PlaybackStopped += (_, e) =>
                {
                    playbackError = e.Exception;
                    done.Set();
                };

                output.Init(reader);
                output.Play();

                var attemptStart = watch.Elapsed;
                var lastPosition = -1L;
                var lastMove = watch.Elapsed;
                var stalled = false;

                while (true)
                {
                    var signalled = WaitHandle.WaitAny([done.WaitHandle, stop.WaitHandle], 200);

                    switch (signalled)
                    {
                        case 0:
                            return playbackError != null
                                ? new PlaybackResult(PlaybackEnd.Error, duration, watch.Elapsed, playbackError.Message, attempt)
                                : new PlaybackResult(PlaybackEnd.Finished, duration, watch.Elapsed, Attempts: attempt);
                        case 1:
                            output.Stop();
                            return new PlaybackResult(PlaybackEnd.Stopped, duration, watch.Elapsed, Attempts: attempt);
                    }

                    var position = reader.Position;
                    if (position != lastPosition)
                    {
                        lastPosition = position;
                        lastMove = watch.Elapsed;
                    }
                    else if (position < reader.Length && watch.Elapsed - lastMove > TimeSpan.FromSeconds(1.5))
                    {
                        stalled = true;
                        break;
                    }

                    // Safety net: never wait much longer than the clip itself.
                    if (watch.Elapsed - attemptStart > duration + TimeSpan.FromSeconds(5)) break;
                }

                output.Stop();
                done.Wait(TimeSpan.FromSeconds(1));
                if (!stalled) return new PlaybackResult(PlaybackEnd.TimedOut, duration, watch.Elapsed, Attempts: attempt);
            }

            return new PlaybackResult(PlaybackEnd.Stalled, duration, watch.Elapsed, Attempts: maxAttempts);
        }
        catch (Exception ex)
        {
            return new PlaybackResult(PlaybackEnd.Error, duration, watch.Elapsed, ex.Message);
        }
        finally
        {
            _currentReader = null;
            Interlocked.CompareExchange(ref _currentStop, null, stop);
            stop.Dispose();
        }
    }

    /// <summary>Ends the clip that's playing; safe to call from any thread.</summary>
    public void Stop()
    {
        try { _currentStop?.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Loudest sample in a file (0 = silence, 1 = full scale), or -1 if unreadable.</summary>
    public static float PeakLevel(string path)
    {
        try
        {
            var buffer = new float[16384];
            var peak = 0f;
            int read;

            using var reader = new AudioFileReader(path);
            while ((read = reader.Read(buffer.AsSpan())) > 0)
            {
                for (var i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
            }

            return peak;
        }
        catch
        {
            return -1;
        }
    }
}