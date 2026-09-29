using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Herald.Services;

namespace Herald.Tests;

/// <summary>A fresh folder under %TEMP%, deleted again when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "herald-tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch
        {
            // a file may still be closing; %TEMP% gets cleaned eventually
        }
    }
}

/// <summary>
/// Stands in for the speakers: records what would be played. By default a clip "finishes"
/// immediately; with <see cref="HoldPlayback"/> it plays until <see cref="Stop"/> or
/// <see cref="FinishCurrent"/>, so tests can act while something is playing.
/// </summary>
public sealed class FakePlayer : IAudioPlayer
{
    private readonly AutoResetEvent _finish = new(false);
    private volatile ManualResetEventSlim? _stop;

    public float Volume { get; set; } = 1f;
    public bool HoldPlayback { get; set; }
    public ConcurrentQueue<string> Played { get; } = new();

    /// <summary>The file playing right now, or null.</summary>
    public string? Current { get; private set; }

    public PlaybackResult Play(string path)
    {
        Played.Enqueue(path);
        if (!HoldPlayback) return new PlaybackResult(PlaybackEnd.Finished, TimeSpan.Zero, TimeSpan.Zero);

        using var stop = new ManualResetEventSlim(false);
        _stop = stop;
        Current = path;
        try
        {
            var which = WaitHandle.WaitAny([stop.WaitHandle, _finish], TimeSpan.FromSeconds(30));
            return new PlaybackResult(which == 0 ? PlaybackEnd.Stopped : PlaybackEnd.Finished, TimeSpan.Zero, TimeSpan.Zero);
        }
        finally
        {
            Current = null;
            _stop = null;
        }
    }

    public void Stop() => _stop?.Set();

    public void FinishCurrent() => _finish.Set();
}

/// <summary>
/// One WPF application with its own UI thread for the whole test run, so the speech queue
/// updates its lists on a UI thread exactly as it does in Herald, never from two threads at once.
/// </summary>
public static class UiThread
{
    private static readonly Lazy<Dispatcher> Instance = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Test UI thread"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    });

    public static void Ensure() => _ = Instance.Value;

    // Background priority: whatever was already queued for the UI thread runs first, so a
    // read sees every update posted before it.
    public static T Invoke<T>(Func<T> read) => Instance.Value.Invoke(read, DispatcherPriority.Background);

    public static void Invoke(Action action) => Instance.Value.Invoke(action, DispatcherPriority.Background);
}

public static class Wait
{
    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }
}
