using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Herald.Models;
using Herald.Services.Engines;
using NAudio.Wave;

namespace Herald.Services;

public class SpeechEngine : INotifyPropertyChanged, IDisposable
{
    public EngineRegistry Engines { get; }
    private readonly string _historyDir;
    public SenderSettingsStore SenderSettings { get; }
    private readonly Queue<QueueItem> _pending = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdownCts = new();

    private QueueItem? _currentItem;
    private volatile bool _skipRequested;

    public ObservableCollection<QueueItem> Queue { get; } = new();
    public ObservableCollection<QueueItem> History { get; } = new();

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged(nameof(Enabled));
            if (!value)
            {
                Skip();
                ClearQueue();
            }
        }
    }

    private int _speedPercent = 100;
    public int SpeedPercent
    {
        get => _speedPercent;
        set
        {
            var clamped = Math.Clamp(value, 100, 190);
            if (_speedPercent == clamped) return;
            _speedPercent = clamped;
            OnPropertyChanged(nameof(SpeedPercent));
            SaveSettings();
        }
    }

    private const int DefaultChunkThreshold = 300;
    private const int DefaultChunkTargetLength = 250;

    private int _chunkThreshold = DefaultChunkThreshold;
    /// <summary>Messages longer than this many characters are split into parts.</summary>
    public int ChunkThreshold
    {
        get => _chunkThreshold;
        set
        {
            var clamped = Math.Clamp(value, 50, 5000);
            if (_chunkThreshold == clamped) return;
            _chunkThreshold = clamped;
            OnPropertyChanged(nameof(ChunkThreshold));
            SaveSettings();
        }
    }

    private int _chunkTargetLength = DefaultChunkTargetLength;
    /// <summary>Approximate size of each part, in characters.</summary>
    public int ChunkTargetLength
    {
        get => _chunkTargetLength;
        set
        {
            var clamped = Math.Clamp(value, 40, 2000);
            if (_chunkTargetLength == clamped) return;
            _chunkTargetLength = clamped;
            OnPropertyChanged(nameof(ChunkTargetLength));
            SaveSettings();
        }
    }

    private const int DefaultHistoryLimit = 200;

    private int _historyLimit = DefaultHistoryLimit;
    /// <summary>How many History items (and their audio files) are kept; older ones are deleted.</summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set
        {
            var clamped = Math.Clamp(value, 10, 5000);
            if (_historyLimit == clamped) return;
            _historyLimit = clamped;
            OnPropertyChanged(nameof(HistoryLimit));
            SaveSettings();
            TrimHistory();
            SaveHistory();
        }
    }

    private bool _askToConnectClaude = true;
    /// <summary>Whether Herald asks at startup to connect Claude Code when it isn't connected.</summary>
    public bool AskToConnectClaude
    {
        get => _askToConnectClaude;
        set
        {
            if (_askToConnectClaude == value) return;
            _askToConnectClaude = value;
            OnPropertyChanged(nameof(AskToConnectClaude));
            SaveSettings();
        }
    }

    private bool _readClipboardAutomatically;
    /// <summary>Read any text copied to the clipboard (sender "clipboard"). Off by default.</summary>
    public bool ReadClipboardAutomatically
    {
        get => _readClipboardAutomatically;
        set
        {
            if (_readClipboardAutomatically == value) return;
            _readClipboardAutomatically = value;
            OnPropertyChanged(nameof(ReadClipboardAutomatically));
            SaveSettings();
        }
    }

    private readonly string _settingsFilePath;
    private readonly string _historyFilePath;

    /// <summary>Herald's own data folder (settings, history, engines).</summary>
    public string AppDataDir { get; }
    public string HistoryDir => _historyDir;

    public LanguageProfileStore LanguageProfiles { get; }

    public SpeechEngine(EngineRegistry engines, string historyDir, string appDataDir, SenderSettingsStore senderSettings,
                        LanguageProfileStore languageProfiles)
    {
        Engines = engines;
        AppDataDir = appDataDir;
        LanguageProfiles = languageProfiles;
        _historyDir = historyDir;
        SenderSettings = senderSettings;
        Directory.CreateDirectory(_historyDir);
        Directory.CreateDirectory(appDataDir);
        _settingsFilePath = Path.Combine(appDataDir, "engine-settings.json");
        _historyFilePath = Path.Combine(appDataDir, "history.json");
        LoadSettings();
        LoadHistory();
        DeleteUnreferencedAudio();

        Engines.Kokoro.StartServerIfReady();

        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpeechEngineWorker"
        };
        worker.Start();
    }

    private void LoadSettings()
    {
        if (!File.Exists(_settingsFilePath)) return;

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            var dto = JsonSerializer.Deserialize<EngineSettingsDto>(json);
            if (dto == null) return;

            _speedPercent = Math.Clamp(dto.SpeedPercent, 100, 190);
            // Files saved before splitting existed have no chunk values (read as 0).
            if (dto.ChunkThreshold > 0) _chunkThreshold = Math.Clamp(dto.ChunkThreshold, 50, 5000);
            if (dto.ChunkTargetLength > 0) _chunkTargetLength = Math.Clamp(dto.ChunkTargetLength, 40, 2000);
            if (dto.HistoryLimit > 0) _historyLimit = Math.Clamp(dto.HistoryLimit, 10, 5000);
            _askToConnectClaude = dto.AskToConnectClaude ?? true;
            _readClipboardAutomatically = dto.ReadClipboardAutomatically ?? false;
        }
        catch
        {
            // corrupt or unreadable settings file - fall back to defaults
        }
    }

    private void SaveSettings()
    {
        try
        {
            var dto = new EngineSettingsDto(_speedPercent, _chunkThreshold, _chunkTargetLength, _historyLimit, _askToConnectClaude,
                                            _readClipboardAutomatically);
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }
        catch
        {
            // best-effort persistence - a failed save shouldn't crash the app
        }
    }

    private record EngineSettingsDto(int SpeedPercent, int ChunkThreshold = 0, int ChunkTargetLength = 0, int HistoryLimit = 0,
                                     bool? AskToConnectClaude = null, bool? ReadClipboardAutomatically = null);

    private record HistoryItemDto(Guid Id, string Text, string Sender, DateTime EnqueuedAt, string? AudioFilePath,
                                  QueueItemStatus Status, Guid GroupId, int PartIndex, int PartCount, bool IsCopy,
                                  string? Language = null);

    /// <summary>Restores History from the last session. Runs on the UI thread at startup.</summary>
    private void LoadHistory()
    {
        if (!File.Exists(_historyFilePath)) return;

        try
        {
            var dtos = JsonSerializer.Deserialize<List<HistoryItemDto>>(File.ReadAllText(_historyFilePath));
            if (dtos == null) return;

            foreach (var dto in dtos.Take(_historyLimit))
            {
                History.Add(new QueueItem(dto.Text, dto.Sender)
                {
                    Id = dto.Id,
                    EnqueuedAt = dto.EnqueuedAt,
                    AudioFilePath = LocateAudio(dto.AudioFilePath),
                    Status = dto.Status,
                    GroupId = dto.GroupId,
                    PartIndex = dto.PartIndex,
                    PartCount = dto.PartCount,
                    IsCopy = dto.IsCopy,
                    Language = dto.Language
                });
            }
        }
        catch
        {
            // unreadable history file - start with an empty History
        }
    }

    /// <summary>
    /// Finds a saved audio file: at its recorded path, or by name in the current history
    /// folder (the data folder may have moved, e.g. when Herald was renamed).
    /// </summary>
    private string? LocateAudio(string? recordedPath)
    {
        if (recordedPath == null) return null;
        if (File.Exists(recordedPath)) return recordedPath;

        var moved = Path.Combine(_historyDir, Path.GetFileName(recordedPath));
        return File.Exists(moved) ? moved : null;
    }

    /// <summary>Saves History; must run on the UI thread, which owns the collection.</summary>
    private void SaveHistory()
    {
        try
        {
            var dtos = History.Select(i => new HistoryItemDto(i.Id, i.Text, i.Sender, i.EnqueuedAt, i.AudioFilePath,
                                                              i.Status, i.GroupId, i.PartIndex, i.PartCount, i.IsCopy,
                                                              i.Language)).ToList();
            File.WriteAllText(_historyFilePath, JsonSerializer.Serialize(dtos));
        }
        catch
        {
            // best-effort persistence
        }
    }

    /// <summary>Drops the oldest History items beyond the limit, deleting their audio.</summary>
    private void TrimHistory()
    {
        while (History.Count > _historyLimit)
        {
            var oldest = History[^1];
            History.RemoveAt(History.Count - 1);
            DeleteAudioIfUnused(oldest.AudioFilePath);
        }
    }

    /// <summary>
    /// Deletes an audio file unless something still uses it - a "Play again" copy shares
    /// its original's file, and a queued copy may be about to play it.
    /// </summary>
    private void DeleteAudioIfUnused(string? path)
    {
        if (path == null) return;
        if (History.Any(i => i.AudioFilePath == path) || Queue.Any(i => i.PreparedAudioPath == path)) return;

        try { File.Delete(path); } catch { }
    }

    /// <summary>
    /// Removes audio files left behind by earlier sessions (before History was saved,
    /// or after a crash) that no History item points to anymore.
    /// </summary>
    private void DeleteUnreferencedAudio()
    {
        var keep = new HashSet<string>(History.Select(i => i.AudioFilePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(_historyDir, "*.wav");

        Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (keep.Contains(file)) continue;
                try { File.Delete(file); } catch { }
            }
        });
    }

    public void EnqueueText(string text, string sender)
    {
        if (!Enabled) return;
        EnqueueInternal(text, sender, playWhenDisabled: false);
    }

    /// <summary>
    /// Speaks a short "herald" system status message (e.g. "Activated"/"Off") even
    /// while Enabled is false - used for the toggle confirmation itself, since
    /// EnqueueText would otherwise refuse to queue anything while speech is off.
    /// </summary>
    public void Announce(string text) => EnqueueInternal(text, "herald", playWhenDisabled: true);

    /// <summary>
    /// Text the user explicitly asked to hear (e.g. the clipboard hotkey): goes through the
    /// sender's rules like any message, but plays even while speech is turned off.
    /// </summary>
    public void EnqueueRequested(string text, string sender) => EnqueueInternal(text, sender, playWhenDisabled: true);

    private void EnqueueInternal(string text, string sender, bool playWhenDisabled)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var settings = SenderSettings.GetOrCreate(sender);
        if (settings.Muted) return;

        var cleaned = TextFilter.Clean(text, settings.FilterCharacters, settings.ReplacementSnapshot);
        if (string.IsNullOrWhiteSpace(cleaned)) return;

        var parts = TextChunker.Split(cleaned, ChunkThreshold, ChunkTargetLength);
        if (parts.Count == 0) return;
        var groupId = Guid.NewGuid();
        // Only look for the languages this sender has a voice rule for.
        var ruleLanguages = settings.LanguageRules.Select(r => r.LanguageName).ToHashSet();
        var profiles = LanguageProfiles.Snapshot.Where(p => ruleLanguages.Contains(p.Name)).ToList();
        var items = new List<QueueItem>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            items.Add(new QueueItem(parts[i], sender)
            {
                // Detected per part, so a mixed message can switch voice part by part.
                Language = LanguageDetector.Detect(parts[i], profiles)?.Name,
                PlayWhenDisabled = playWhenDisabled,
                GroupId = groupId,
                PartIndex = i + 1,
                PartCount = parts.Count
            });
        }

        lock (_lock)
        {
            foreach (var item in items) _pending.Enqueue(item);
        }

        RunOnUi(() =>
        {
            foreach (var item in items) Queue.Add(item);
        });
        _signal.Release(items.Count);

        StartPrefetch();
    }

    /// <summary>Stops only the part currently playing; the next part or message follows.</summary>
    public void Skip()
    {
        _skipRequested = true;
        StopPlayback();
    }

    /// <summary>Stops the current part and drops the remaining parts of the same message.</summary>
    public void SkipMessage()
    {
        _skipRequested = true;

        var current = _currentItem;
        if (current != null)
        {
            lock (_lock)
            {
                var keep = new List<QueueItem>();
                while (_pending.Count > 0)
                {
                    var pending = _pending.Dequeue();
                    if (pending.GroupId == current.GroupId)
                    {
                        pending.Status = QueueItemStatus.Skipped;
                        MoveToHistory(pending);
                    }
                    else
                    {
                        keep.Add(pending);
                    }
                }
                foreach (var pending in keep) _pending.Enqueue(pending);
                DropStalePrefetchLocked();
            }
        }

        StopPlayback();
        StartPrefetch();
    }

    public void ClearQueue()
    {
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                var item = _pending.Dequeue();
                item.Status = QueueItemStatus.Skipped;
                MoveToHistory(item);
            }
            DropStalePrefetchLocked();
        }
    }

    /// <summary>
    /// Clears the history list and deletes its cached audio files. Expected to be
    /// called from the UI thread (e.g. a button click).
    /// </summary>
    public void ClearHistory()
    {
        var paths = History.Select(i => i.AudioFilePath).Distinct().ToList();
        History.Clear();
        foreach (var path in paths) DeleteAudioIfUnused(path);
        SaveHistory();
    }

    /// <summary>
    /// Puts a copy of an earlier item back in the queue. Reuses its audio when that still
    /// exists, and plays even if speech is off, since the user asked for it directly.
    /// </summary>
    public void Requeue(QueueItem source)
    {
        var copy = new QueueItem(source.Text, source.Sender)
        {
            PlayWhenDisabled = true,
            IsCopy = true,
            Language = source.Language,
            GroupId = Guid.NewGuid(),
            PreparedAudioPath = source.AudioFilePath is { } path && File.Exists(path) ? path : null
        };

        lock (_lock)
        {
            _pending.Enqueue(copy);
        }

        RunOnUi(() => Queue.Add(copy));
        _signal.Release();
        StartPrefetch();
    }

    /// <summary>
    /// Speaks a sample with a specific engine and voice, outside the queue - used by the
    /// settings page's Test button. Returns false if synthesis failed.
    /// </summary>
    public async Task<bool> PreviewAsync(ITtsEngine engine, string voiceId, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"herald-preview-{Guid.NewGuid():N}.wav");
        if (!await engine.SynthesizeAsync(text, voiceId, SpeedPercent / 100.0, path, _shutdownCts.Token)) return false;

        _ = Task.Run(() =>
        {
            PlayFile(path);
            try { File.Delete(path); } catch { }
        });
        return true;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            try
            {
                _signal.Wait(_shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            QueueItem? item;
            Task<bool>? prepared;
            lock (_lock)
            {
                if (_pending.Count == 0) continue;
                item = _pending.Dequeue();
                prepared = TakePrefetchLocked(item);
            }

            if (!Enabled && !item.PlayWhenDisabled)
            {
                item.Status = QueueItemStatus.Skipped;
                MoveToHistory(item);
                if (prepared != null) DeleteWhenDone(item, prepared);
                continue;
            }

            _skipRequested = false;
            _currentItem = item;

            var outPath = AudioPathFor(item);

            bool ok;
            if (prepared != null)
            {
                ok = prepared.GetAwaiter().GetResult();
            }
            else
            {
                item.Status = QueueItemStatus.Synthesizing;
                ok = Synthesize(item, outPath).GetAwaiter().GetResult();
            }

            if (_skipRequested)
            {
                if (ok) item.AudioFilePath = outPath;
                item.Status = QueueItemStatus.Skipped;
                MoveToHistory(item);
                _currentItem = null;
                continue;
            }

            if (!ok)
            {
                item.Status = QueueItemStatus.Failed;
                MoveToHistory(item);
                _currentItem = null;
                continue;
            }

            item.AudioFilePath = outPath;
            item.Status = QueueItemStatus.Playing;

            // Synthesize the next part while this one plays, so parts follow each other
            // without waiting for synthesis in between.
            StartPrefetch();

            PlayFile(outPath);

            item.Status = _skipRequested ? QueueItemStatus.Skipped : QueueItemStatus.Done;
            MoveToHistory(item);

            _currentItem = null;
        }
    }

    // The next queued item being synthesized ahead of its turn. Guarded by _lock, since
    // prefetching starts both from the worker and from whichever thread enqueues.
    private QueueItem? _prefetchItem;
    private Task<bool>? _prefetchTask;

    private string AudioPathFor(QueueItem item) =>
        item.PreparedAudioPath ?? Path.Combine(_historyDir, $"{item.Id}.wav");

    private Task<bool> Synthesize(QueueItem item, string outPath)
    {
        if (item.PreparedAudioPath != null) return Task.FromResult(File.Exists(item.PreparedAudioPath));

        var senderSettings = SenderSettings.GetOrCreate(item.Sender);
        var spokenText = senderSettings.AnnounceSender && item.PartIndex == 1
            ? $"{item.Sender}: {item.Text}"
            : item.Text;

        var (engine, voiceId) = VoiceFor(item, senderSettings);
        return engine.SynthesizeAsync(spokenText, voiceId, SpeedPercent / 100.0, outPath, _shutdownCts.Token);
    }

    /// <summary>
    /// The sender's rule for the detected language when its engine is ready; otherwise
    /// the sender's default voice.
    /// </summary>
    private (ITtsEngine Engine, string VoiceId) VoiceFor(QueueItem item, SenderSettings senderSettings)
    {
        if (item.Language != null
            && senderSettings.LanguageRules.FirstOrDefault(r => r.LanguageName == item.Language) is { } rule
            && Engines.Find(rule.EngineId) is { IsReady: true })
        {
            return Engines.Resolve(rule.EngineId, rule.VoiceId);
        }

        return Engines.Resolve(senderSettings.EngineId, senderSettings.VoiceId);
    }

    /// <summary>
    /// Starts synthesizing the next queued item so it's ready the moment the current one
    /// finishes. Called when playback starts and whenever new text is queued, so a message
    /// arriving mid-playback doesn't wait for the current one to end.
    /// </summary>
    private void StartPrefetch()
    {
        QueueItem next;
        TaskCompletionSource<bool> done;
        lock (_lock)
        {
            if (_prefetchItem != null || _pending.Count == 0) return;
            next = _pending.Peek();
            done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _prefetchItem = next;
            _prefetchTask = done.Task;
        }

        next.Status = QueueItemStatus.Synthesizing;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await Synthesize(next, AudioPathFor(next)); }
            catch { ok = false; }

            if (ok && next.Status == QueueItemStatus.Synthesizing) next.Status = QueueItemStatus.Ready;
            done.SetResult(ok);
        });
    }

    /// <summary>
    /// Hands the prefetch to the worker if it's for <paramref name="item"/>; otherwise the
    /// prefetched item was removed before its turn, so its audio is thrown away.
    /// </summary>
    private Task<bool>? TakePrefetchLocked(QueueItem item)
    {
        var prefetchItem = _prefetchItem;
        var prefetchTask = _prefetchTask;
        _prefetchItem = null;
        _prefetchTask = null;

        if (prefetchItem == null || prefetchTask == null) return null;
        if (prefetchItem == item) return prefetchTask;

        DeleteWhenDone(prefetchItem, prefetchTask);
        return null;
    }

    /// <summary>Drops the prefetch if its item is no longer queued (skipped or cleared).</summary>
    private void DropStalePrefetchLocked()
    {
        if (_prefetchItem == null || _prefetchTask == null || _pending.Contains(_prefetchItem)) return;

        DeleteWhenDone(_prefetchItem, _prefetchTask);
        _prefetchItem = null;
        _prefetchTask = null;
    }

    private void DeleteWhenDone(QueueItem item, Task<bool> synthesis)
    {
        // A replayed copy's audio belongs to the original history item - keep it.
        if (item.PreparedAudioPath != null) return;

        var path = AudioPathFor(item);
        synthesis.ContinueWith(_ =>
        {
            try { File.Delete(path); } catch { }
        });
    }

    /// <summary>
    /// Plays a file and blocks until it ends or <see cref="StopPlayback"/> is called.
    /// The player is only ever touched from this thread; other threads just signal it,
    /// since calling Stop() on the player from another thread silently did nothing.
    /// </summary>
    private void PlayFile(string path)
    {
        var stop = new ManualResetEventSlim(false);
        _currentStop = stop;
        try
        {
            using var reader = new AudioFileReader(path);
            using var output = new WaveOut();
            using var done = new ManualResetEventSlim(false);
            output.PlaybackStopped += (_, _) => done.Set();

            output.Init(reader);
            output.Play();

            // Safety net: never wait much longer than the clip itself.
            var timeout = reader.TotalTime + TimeSpan.FromSeconds(5);
            WaitHandle.WaitAny([done.WaitHandle, stop.WaitHandle], timeout);

            if (!done.IsSet)
            {
                output.Stop();
                done.Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch
        {
            // ignore playback errors, move on
        }
        finally
        {
            Interlocked.CompareExchange(ref _currentStop, null, stop);
            stop.Dispose();
        }
    }

    private ManualResetEventSlim? _currentStop;

    private void StopPlayback()
    {
        try { _currentStop?.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Every received item ends up in History - spoken, skipped or failed - so nothing
    /// that was delivered silently disappears.
    /// </summary>
    private void MoveToHistory(QueueItem item) => RunOnUi(() =>
    {
        Queue.Remove(item);
        History.Insert(0, item);
        TrimHistory();
        SaveHistory();
    });

    private static void RunOnUi(Action action)
    {
        var app = Application.Current;
        if (app == null)
        {
            action();
            return;
        }
        // Fire-and-forget: never let a caller (e.g. the TCP hook handler) block
        // on the UI thread just to update the queue/history lists.
        app.Dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _signal.Release();
        StopPlayback();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
