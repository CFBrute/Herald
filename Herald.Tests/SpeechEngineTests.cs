using System.IO;
using Herald.Models;
using Herald.Services;
using Herald.Services.Engines;

namespace Herald.Tests;

/// <summary>
/// A real speech queue on temporary files: parts are really synthesized with Windows'
/// voices (to WAV files, no sound), and "played" by a <see cref="FakePlayer"/>.
/// </summary>
public sealed class SpeechHarness : IDisposable
{
    public TempFolder Folder { get; } = new();
    public AppPaths Paths { get; }
    public AppSettings Settings { get; }
    public EngineRegistry Engines { get; }
    public SenderSettingsStore Senders { get; }
    public LanguageProfileStore Languages { get; }
    public FakePlayer Player { get; } = new();
    public SpeechEngine Speech { get; private set; }

    public SpeechHarness()
    {
        UiThread.Ensure();
        Paths = new AppPaths(Folder.Path);
        Settings = new AppSettings(Paths.AppSettingsFile);
        Engines = new EngineRegistry(Paths.EnginesDir);
        Assert.SkipUnless(Engines.Windows.IsReady, "Needs at least one Windows voice to synthesize with");
        Senders = new SenderSettingsStore(Paths.SenderSettingsFile);
        Languages = new LanguageProfileStore(Paths.LanguageProfilesFile);
        Speech = Start();
    }

    private SpeechEngine Start() => new(Paths, Settings, Engines, Senders, Languages, Player);

    /// <summary>Stops this queue and starts a new one on the same files, like restarting Herald.</summary>
    public SpeechEngine Restart()
    {
        Speech.Dispose();
        Speech = Start();
        return Speech;
    }

    /// <summary>History and Queue belong to the UI thread; these read them there.</summary>
    public QueueItem[] History => UiThread.Invoke(() => Speech.History.ToArray());
    public QueueItem[] Queue => UiThread.Invoke(() => Speech.Queue.ToArray());

    public string[] AudioFiles => Directory.GetFiles(Paths.HistoryDir, "*.wav");

    public Task HistoryCount(int count) =>
        Wait.Until(() => UiThread.Invoke(() => Speech.History.Count == count && Speech.Queue.Count == 0),
                   $"{count} items in History (has {History.Length})");

    public void Dispose()
    {
        Speech.Dispose();
        Engines.Dispose();
        Folder.Dispose();
    }
}

public class SpeechEngineTests : IDisposable
{
    private readonly SpeechHarness _h = new();
    private SpeechEngine Speech => _h.Speech;

    public void Dispose() => _h.Dispose();

    /// <summary>Settings that split anything over 50 characters into parts of about 40.</summary>
    private void SplitEagerly()
    {
        _h.Settings.ChunkThreshold = 50;
        _h.Settings.ChunkTargetLength = 40;
    }

    private const string LongMessage =
        "The first sentence is here. The second sentence follows it. A third one comes next. And a fourth to finish.";

    [Fact]
    public async Task A_message_is_cleaned_synthesized_played_and_kept_in_history()
    {
        Speech.EnqueueText("Hello **there**", "tester");

        await _h.HistoryCount(1);
        var item = _h.History[0];
        // Shown as it was sent; the voice gets the cleaned text, which the tooltip shows.
        Assert.Equal("Hello **there**", item.Text);
        Assert.Equal("Hello there.", item.SpokenText);
        Assert.EndsWith("Spoken text:\nHello there.", item.Details);
        Assert.Equal("tester", item.Sender);
        Assert.Equal(QueueItemStatus.Done, item.Status);
        Assert.StartsWith("Engine: Windows voices", item.SynthesisInfo);
        Assert.True(File.Exists(item.AudioFilePath));
        Assert.Equal([item.AudioFilePath], _h.Player.Played);
        Assert.Contains("Hello there.", File.ReadAllText(_h.Paths.HistoryFile));
    }

    [Fact]
    public async Task A_long_message_is_played_part_by_part_in_order()
    {
        SplitEagerly();

        Speech.EnqueueText(LongMessage, "tester");

        await Wait.Until(() => _h.History.Length > 1 && _h.Queue.Length == 0, "all parts played");
        var parts = _h.History.Reverse().ToArray();
        Assert.All(parts, p => Assert.Equal(QueueItemStatus.Done, p.Status));
        Assert.Single(parts.Select(p => p.GroupId).Distinct());
        Assert.Equal(Enumerable.Range(1, parts.Length), parts.Select(p => p.PartIndex));
        Assert.All(parts, p => Assert.Equal(parts.Length, p.PartCount));
        Assert.Equal(parts.Select(p => p.AudioFilePath), _h.Player.Played);
        Assert.Equal(LongMessage, string.Join(" ", parts.Select(p => p.Text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("```\ncode only\n```")]
    public async Task Nothing_speakable_is_ignored(string text)
    {
        Speech.EnqueueText(text, "tester");

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(_h.Queue);
        Assert.Empty(_h.History);
    }

    [Fact]
    public async Task A_muted_sender_is_ignored()
    {
        _h.Senders.GetOrCreate("noisy").Muted = true;

        Speech.EnqueueText("You should not hear this", "noisy");
        Speech.EnqueueText("But this one", "tester");

        await _h.HistoryCount(1);
        Assert.Equal("tester", _h.History[0].Sender);
    }

    [Fact]
    public async Task While_speech_is_off_only_announcements_and_explicit_requests_play()
    {
        Speech.Enabled = false;

        Speech.EnqueueText("Ignored while off", "tester");
        Speech.Announce("Off");
        Speech.EnqueueRequested("Read this anyway", "clipboard");

        await _h.HistoryCount(2);
        Assert.Equal(["Off.", "Read this anyway."], _h.History.Reverse().Select(i => i.SpokenText));
    }

    [Fact]
    public async Task Skip_stops_the_current_part_and_the_next_one_plays()
    {
        _h.Player.HoldPlayback = true;
        Speech.EnqueueText("First message", "tester");
        Speech.EnqueueText("Second message", "tester");
        await Wait.Until(() => _h.Player.Current != null, "first message playing");

        Speech.Skip();

        await Wait.Until(() => _h.Player.Played.Count == 2 && _h.Player.Current != null, "second message playing");
        _h.Player.FinishCurrent();
        await _h.HistoryCount(2);
        Assert.Equal([("First message.", QueueItemStatus.Skipped), ("Second message.", QueueItemStatus.Done)],
                     _h.History.Reverse().Select(i => (i.SpokenText, i.Status)));
    }

    [Fact]
    public async Task Skip_message_drops_the_rest_of_that_message_only()
    {
        SplitEagerly();
        _h.Player.HoldPlayback = true;
        Speech.EnqueueText(LongMessage, "tester");
        Speech.EnqueueText("Another message", "tester");
        await Wait.Until(() => _h.Player.Current != null, "first part playing");

        Speech.SkipMessage();

        await Wait.Until(() => _h.Player.Played.Count == 2 && _h.Player.Current != null, "the other message playing");
        _h.Player.FinishCurrent();
        await Wait.Until(() => _h.Queue.Length == 0 && _h.History.Any(i => i.SpokenText == "Another message."), "all done");
        var skipped = _h.History.Where(i => i.SpokenText != "Another message.").ToArray();
        Assert.True(skipped.Length > 1);
        Assert.All(skipped, i => Assert.Equal(QueueItemStatus.Skipped, i.Status));
        Assert.Equal(QueueItemStatus.Done, _h.History.Single(i => i.SpokenText == "Another message.").Status);
    }

    [Fact]
    public async Task Turning_speech_off_stops_playback_and_clears_the_queue()
    {
        _h.Player.HoldPlayback = true;
        Speech.EnqueueText("One", "tester");
        Speech.EnqueueText("Two", "tester");
        Speech.EnqueueText("Three", "tester");
        await Wait.Until(() => _h.Player.Current != null, "first message playing");

        Speech.Enabled = false;

        await _h.HistoryCount(3);
        Assert.All(_h.History, i => Assert.Equal(QueueItemStatus.Skipped, i.Status));
        Assert.Single(_h.Player.Played);
    }

    [Fact]
    public async Task Play_again_reuses_the_audio_even_while_speech_is_off()
    {
        Speech.EnqueueText("Say it twice", "tester");
        await _h.HistoryCount(1);
        var original = _h.History[0];
        Speech.Enabled = false;

        Speech.Requeue(original);

        await _h.HistoryCount(2);
        var copy = _h.History[0];
        Assert.True(copy.IsCopy);
        Assert.Equal(QueueItemStatus.Done, copy.Status);
        Assert.Equal(original.AudioFilePath, copy.AudioFilePath);
        Assert.Equal(original.SynthesisInfo, copy.SynthesisInfo);
        Assert.Equal([original.AudioFilePath, original.AudioFilePath], _h.Player.Played);
        Assert.Single(_h.AudioFiles);
    }

    [Fact]
    public async Task History_keeps_only_the_latest_items_and_deletes_older_audio()
    {
        _h.Settings.HistoryLimit = 10;

        for (var i = 1; i <= 12; i++) Speech.EnqueueText($"Message {i}", "tester");

        await Wait.Until(() => _h.Player.Played.Count == 12 && _h.Queue.Length == 0, "all 12 played");
        await _h.HistoryCount(10);
        Assert.Equal("Message 12.", _h.History[0].SpokenText);
        Assert.Equal("Message 3.", _h.History[^1].SpokenText);
        Assert.Equal(10, _h.AudioFiles.Length);
    }

    [Fact]
    public async Task Lowering_the_history_limit_trims_right_away()
    {
        for (var i = 1; i <= 12; i++) Speech.EnqueueText($"Message {i}", "tester");
        await _h.HistoryCount(12);

        _h.Settings.HistoryLimit = 10;

        await _h.HistoryCount(10);
        Assert.Equal(10, _h.AudioFiles.Length);
    }

    [Fact]
    public async Task Clearing_history_deletes_its_audio()
    {
        Speech.EnqueueText("Temporary", "tester");
        await _h.HistoryCount(1);

        UiThread.Invoke(Speech.ClearHistory);

        Assert.Empty(_h.History);
        Assert.Empty(_h.AudioFiles);
        Assert.Equal("[]", File.ReadAllText(_h.Paths.HistoryFile));
    }

    [Fact]
    public async Task History_survives_a_restart()
    {
        Speech.EnqueueText("First", "tester");
        Speech.EnqueueText("Second", "other");
        await _h.HistoryCount(2);
        var before = _h.History;

        _h.Restart();

        Assert.Equal(before.Select(i => (i.Id, i.Text, i.SpokenText, i.Sender, i.Status, i.AudioFilePath, i.SynthesisInfo)),
                     _h.History.Select(i => (i.Id, i.Text, i.SpokenText, i.Sender, i.Status, i.AudioFilePath, i.SynthesisInfo)));
        // Neighbouring messages get opposite shading.
        Assert.NotEqual(_h.History[0].Band, _h.History[1].Band);
    }

    [Fact]
    public async Task Audio_nothing_refers_to_is_cleaned_up_at_startup()
    {
        Speech.EnqueueText("Keep me", "tester");
        await _h.HistoryCount(1);
        var stray = Path.Combine(_h.Paths.HistoryDir, "left-over-from-a-crash.wav");
        File.WriteAllText(stray, "");

        _h.Restart();

        await Wait.Until(() => !File.Exists(stray), "stray audio deleted");
        Assert.True(File.Exists(_h.History[0].AudioFilePath));
    }

    [Fact]
    public async Task Set_speed_changes_the_setting_and_says_the_new_speed()
    {
        Speech.SetSpeed(170);

        await _h.HistoryCount(1);
        Assert.Equal(170, _h.Settings.SpeedPercent);
        Assert.Equal("Speed 170.", _h.History[0].SpokenText);
        Assert.EndsWith("Speed: 170%", _h.History[0].SynthesisInfo);
    }

    [Fact]
    public async Task Toggling_announces_the_new_state()
    {
        Speech.ToggleEnabled();
        Speech.ToggleEnabled();

        await _h.HistoryCount(2);
        Assert.True(Speech.Enabled);
        Assert.Equal(["Off.", "Activated."], _h.History.Reverse().Select(i => i.SpokenText));
    }

    [Fact]
    public void The_volume_setting_reaches_the_player()
    {
        _h.Settings.Volume = 40;

        Assert.Equal(0.4f, _h.Player.Volume, 3);
    }

    [Fact]
    public async Task A_language_is_only_detected_for_senders_with_a_rule_for_it()
    {
        _h.Languages.Profiles[0].Enabled = true; // the Swedish example
        var voice = _h.Engines.Windows.Voices[0];
        _h.Senders.GetOrCreate("swede").LanguageRules = [new LanguageVoiceRule("Swedish", "windows", voice.Id)];
        const string swedish = "Det är bra och jag tycker att det fungerar";

        Speech.EnqueueText(swedish, "swede");
        Speech.EnqueueText(swedish, "tester");

        await _h.HistoryCount(2);
        Assert.Equal("Swedish", _h.History.Single(i => i.Sender == "swede").Language);
        Assert.Contains(voice.DisplayName, _h.History.Single(i => i.Sender == "swede").SynthesisInfo);
        Assert.Null(_h.History.Single(i => i.Sender == "tester").Language);
    }

    [Fact]
    public async Task A_voice_rule_for_an_uninstalled_engine_falls_back_to_the_senders_voice()
    {
        _h.Languages.Profiles[0].Enabled = true;
        _h.Senders.GetOrCreate("swede").LanguageRules = [new LanguageVoiceRule("Swedish", "piper", "sv_SE-nst-medium")];

        Speech.EnqueueText("Det är bra och jag tycker att det fungerar", "swede");

        await _h.HistoryCount(1);
        Assert.Equal(QueueItemStatus.Done, _h.History[0].Status);
        Assert.StartsWith("Engine: Windows voices", _h.History[0].SynthesisInfo);
    }

    [Fact]
    public async Task Every_played_part_is_logged_for_diagnostics()
    {
        Speech.EnqueueText("Log me", "tester");
        await _h.HistoryCount(1);

        var log = File.ReadAllText(_h.Paths.PlaybackLog);

        Assert.Contains("tester 1/1", log);
        Assert.Contains("windows/", log);
        Assert.Contains("Finished", log);
    }
}
