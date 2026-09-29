using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Herald.Models;
using Herald.Services.Engines;

namespace Herald.Services;

/// <summary>
/// The speech queue: cleans and splits incoming text, synthesizes each part (the next one
/// while the current one plays), plays it, and keeps everything that arrived in History.
/// </summary>
public class SpeechEngine : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EngineRegistry _engines;
    private readonly SenderSettingsStore _senders;
    private readonly LanguageProfileStore _languages;
    private readonly IAudioPlayer _player;

    private readonly Queue<QueueItem> _pending = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdownCts = new();

    private volatile QueueItem? _currentItem;
    private volatile bool _skipRequested;

    public ObservableCollection<QueueItem> Queue { get; } = [];
    public ObservableCollection<QueueItem> History { get; } = [];

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetField(ref _enabled, value) && !value)
            {
                Skip();
                ClearQueue();
            }
        }
    }

    public SpeechEngine(AppPaths paths, AppSettings settings, EngineRegistry engines, SenderSettingsStore senders,
                        LanguageProfileStore languages, IAudioPlayer? player = null)
    {
        _paths = paths;
        _settings = settings;
        _engines = engines;
        _senders = senders;
        _languages = languages;
        _player = player ?? new AudioPlayer();

        _player.Volume = _settings.Volume / 100f;
        _settings.PropertyChanged += OnSettingChanged;

        LoadHistory();
        DeleteUnreferencedAudio();

        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpeechEngineWorker"
        };
        worker.Start();
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Volume):
                _player.Volume = _settings.Volume / 100f;
                break;
            case nameof(AppSettings.HistoryLimit):
                RunOnUi(() =>
                {
                    TrimHistory();
                    SaveHistory();
                });
                break;
        }
    }

    /// <summary>Turns speech on or off and says so; "Off" is spoken even though speech just went off.</summary>
    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Announce(enabled ? "Activated" : "Off");
    }

    public void ToggleEnabled() => SetEnabled(!Enabled);

    /// <summary>Changes the speed and speaks the new value, so a hotkey press can be heard.</summary>
    public void SetSpeed(int percent)
    {
        _settings.SpeedPercent = percent;
        EnqueueText($"Speed {_settings.SpeedPercent}", "herald");
    }

    // Property names are part of history.json; keep them when renaming.
    private record HistoryItemDto(Guid Id, string Text, string Sender, DateTime EnqueuedAt, string? AudioFilePath,
                                  QueueItemStatus Status, Guid GroupId, int PartIndex, int PartCount, bool IsCopy,
                                  string? Language = null, string? SynthesisInfo = null);

    /// <summary>Restores History from the last session. Runs on the UI thread at startup.</summary>
    private void LoadHistory()
    {
        var dtos = SafeFile.ReadJson<List<HistoryItemDto>>(_paths.HistoryFile) ?? [];
        foreach (var dto in dtos.Take(_settings.HistoryLimit))
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
                Language = dto.Language,
                SynthesisInfo = dto.SynthesisInfo
            });
        }

        // Re-derive the alternating shading: flip whenever the message (group) changes.
        var band = 0;
        for (var i = 0; i < History.Count; i++)
        {
            if (i > 0 && History[i].GroupId != History[i - 1].GroupId) band ^= 1;
            History[i].Band = band;
        }
        // New messages continue with the shade opposite to the newest one in History.
        _bandCounter = History.Count > 0 ? History[0].Band : 1;
    }

    private int _bandCounter;

    /// <summary>Next shade for a new message: alternates 0, 1, 0, ...</summary>
    private int NextBand() => Interlocked.Increment(ref _bandCounter) & 1;

    /// <summary>
    /// Finds a saved audio file: at its recorded path, or by name in the current history
    /// folder (the data folder may have moved, e.g. when Herald was renamed).
    /// </summary>
    private string? LocateAudio(string? recordedPath)
    {
        if (recordedPath == null) return null;
        if (File.Exists(recordedPath)) return recordedPath;

        var moved = Path.Combine(_paths.HistoryDir, Path.GetFileName(recordedPath));
        return File.Exists(moved) ? moved : null;
    }

    /// <summary>Saves History; must run on the UI thread, which owns the collection.</summary>
    private void SaveHistory() =>
        SafeFile.WriteJson(_paths.HistoryFile,
                           History.Select(i => new HistoryItemDto(i.Id, i.Text, i.Sender, i.EnqueuedAt, i.AudioFilePath,
                                                                  i.Status, i.GroupId, i.PartIndex, i.PartCount, i.IsCopy,
                                                                  i.Language, i.SynthesisInfo)).ToList(),
                           indented: false);

    /// <summary>Drops the oldest History items beyond the limit, deleting their audio.</summary>
    private void TrimHistory()
    {
        while (History.Count > _settings.HistoryLimit)
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

        SafeFile.TryDelete(path);
    }

    /// <summary>
    /// Removes audio files left behind by earlier sessions (before History was saved,
    /// or after a crash) that no History item points to anymore.
    /// </summary>
    private void DeleteUnreferencedAudio()
    {
        var keep = new HashSet<string>(History.Select(i => i.AudioFilePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var files = Directory.GetFiles(_paths.HistoryDir, "*.wav");

        Task.Run(() =>
        {
            foreach (var file in files.Where(f => !keep.Contains(f))) SafeFile.TryDelete(file);
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

        var settings = _senders.GetOrCreate(sender);
        if (settings.Muted) return;

        var cleaned = TextFilter.Clean(text, settings.FilterCharacters, settings.ReplacementSnapshot);
        if (string.IsNullOrWhiteSpace(cleaned)) return;

        var parts = TextChunker.Split(cleaned, _settings.ChunkThreshold, _settings.ChunkTargetLength);
        if (parts.Count == 0) return;
        var groupId = Guid.NewGuid();
        var band = NextBand();
        // Only look for the languages this sender has a voice rule for.
        var ruleLanguages = settings.LanguageRules.Select(r => r.LanguageName).ToHashSet();
        var profiles = _languages.Snapshot.Where(p => ruleLanguages.Contains(p.Name)).ToList();
        var items = new List<QueueItem>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            items.Add(new QueueItem(parts[i], sender)
            {
                // Detected per part, so a mixed message can switch voice part by part.
                Language = LanguageDetector.Detect(parts[i], profiles)?.Name,
                PlayWhenDisabled = playWhenDisabled,
                GroupId = groupId,
                Band = band,
                PartIndex = i + 1,
                PartCount = parts.Count
            });
        }

        Enqueue(items);
    }

    private void Enqueue(IReadOnlyList<QueueItem> items)
    {
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
        _player.Stop();
    }

    /// <summary>Stops the current part and drops the remaining parts of the same message.</summary>
    public void SkipMessage()
    {
        _skipRequested = true;

        if (_currentItem is { } current)
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

        _player.Stop();
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
            Band = NextBand(),
            Language = source.Language,
            // The audio is reused, so the original voice and speed still apply.
            SynthesisInfo = source.SynthesisInfo,
            GroupId = Guid.NewGuid(),
            PreparedAudioPath = source.AudioFilePath is { } path && File.Exists(path) ? path : null
        };

        Enqueue([copy]);
    }

    /// <summary>
    /// Speaks a sample with a specific engine and voice, outside the queue - used by the
    /// settings page's Test button. Returns false if synthesis failed.
    /// </summary>
    public async Task<bool> PreviewAsync(ITtsEngine engine, string voiceId, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"herald-preview-{Guid.NewGuid():N}.wav");
        if (!await engine.SynthesizeAsync(text, voiceId, _settings.SpeedPercent / 100.0, path, _shutdownCts.Token)) return false;

        _ = Task.Run(() =>
        {
            _player.Play(path);
            SafeFile.TryDelete(path);
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
            try
            {
                PlayItem(item, prepared);
            }
            catch
            {
                // One part going wrong must not end this thread, or nothing would be spoken again.
                if (item.Status is not (QueueItemStatus.Done or QueueItemStatus.Skipped or QueueItemStatus.Failed))
                {
                    item.Status = QueueItemStatus.Failed;
                    MoveToHistory(item);
                }
            }
            finally
            {
                _currentItem = null;
            }
        }
    }

    /// <summary>Synthesizes (unless prefetched) and plays one part, then moves it to History.</summary>
    private void PlayItem(QueueItem item, Task<bool>? prepared)
    {
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
            return;
        }

        if (!ok)
        {
            item.Status = QueueItemStatus.Failed;
            MoveToHistory(item);
            return;
        }

        item.AudioFilePath = outPath;
        item.Status = QueueItemStatus.Playing;

        // Synthesize the next part while this one plays, so parts follow each other
        // without waiting for synthesis in between.
        StartPrefetch();

        var playback = _player.Play(outPath);
        LogPlayback(item, outPath, playback);

        item.Status = _skipRequested ? QueueItemStatus.Skipped : QueueItemStatus.Done;
        MoveToHistory(item);
    }

    // The next queued item being synthesized ahead of its turn. Guarded by _lock, since
    // prefetching starts both from the worker and from whichever thread enqueues.
    private QueueItem? _prefetchItem;
    private Task<bool>? _prefetchTask;

    private string AudioPathFor(QueueItem item) =>
        item.PreparedAudioPath ?? Path.Combine(_paths.HistoryDir, $"{item.Id}.wav");

    private Task<bool> Synthesize(QueueItem item, string outPath)
    {
        if (item.PreparedAudioPath != null) return Task.FromResult(File.Exists(item.PreparedAudioPath));

        var senderSettings = _senders.GetOrCreate(item.Sender);
        var spokenText = senderSettings.AnnounceSender && item.PartIndex == 1
            ? $"{item.Sender}: {item.Text}"
            : item.Text;

        var (engine, voiceId) = VoiceFor(item, senderSettings);
        var speed = _settings.SpeedPercent;
        var voiceName = engine.Voices.FirstOrDefault(v => v.Id == voiceId)?.DisplayName ?? voiceId;
        item.SynthesisInfo = $"Engine: {engine.DisplayName}\nVoice: {voiceName}\nSpeed: {speed}%";
        return engine.SynthesizeAsync(spokenText, voiceId, speed / 100.0, outPath, _shutdownCts.Token);
    }

    /// <summary>
    /// The sender's rule for the detected language when its engine is ready; otherwise
    /// the sender's default voice.
    /// </summary>
    private (ITtsEngine Engine, string VoiceId) VoiceFor(QueueItem item, SenderSettings senderSettings)
    {
        if (item.Language != null
            && senderSettings.LanguageRules.FirstOrDefault(r => r.LanguageName == item.Language) is { } rule
            && _engines.Find(rule.EngineId) is { IsReady: true })
        {
            return _engines.Resolve(rule.EngineId, rule.VoiceId);
        }

        return _engines.Resolve(senderSettings.EngineId, senderSettings.VoiceId);
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
        synthesis.ContinueWith(_ => SafeFile.TryDelete(path));
    }

    /// <summary>
    /// One line per played part in playback.log, to diagnose parts that play without sound:
    /// a near-zero peak means the audio file itself was silent; a normal peak means the
    /// file was fine and the sound got lost on the way to the speakers.
    /// </summary>
    private void LogPlayback(QueueItem item, string path, PlaybackResult result)
    {
        try
        {
            var logPath = _paths.PlaybackLog;
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
            {
                File.Move(logPath, _paths.PlaybackOldLog, overwrite: true);
            }

            string voice;
            if (item.PreparedAudioPath != null)
            {
                voice = "copy (reused audio)";
            }
            else
            {
                var (engine, voiceId) = VoiceFor(item, _senders.GetOrCreate(item.Sender));
                voice = $"{engine.Id}/{voiceId}";
            }

            var line = string.Join(" | ",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                $"{item.Sender} {item.PartIndex}/{item.PartCount}",
                voice,
                $"clip {result.Duration.TotalSeconds:0.0}s",
                $"peak {AudioPlayer.PeakLevel(path):0.000}",
                $"{result.End} after {result.Elapsed.TotalSeconds:0.0}s" + (result.Attempts > 1 ? $" (restarted, attempt {result.Attempts})" : string.Empty),
                Path.GetFileName(path) + (result.Error != null ? " | error: " + result.Error : string.Empty));
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // diagnostics must never break playback
        }
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
        _settings.PropertyChanged -= OnSettingChanged;
        _shutdownCts.Cancel();
        _signal.Release();
        _player.Stop();
    }
}
