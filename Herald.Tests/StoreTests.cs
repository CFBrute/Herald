using System.IO;
using System.Windows.Input;
using Herald.Models;
using Herald.Services;

namespace Herald.Tests;

public class SenderSettingsStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string FilePath => _dir.File("sender-settings.json");

    // Like in Herald, there is a UI thread that owns the settings page's list of senders.
    public SenderSettingsStoreTests() => UiThread.Ensure();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Seeds_claude_and_herald()
    {
        var store = new SenderSettingsStore(FilePath);

        Assert.Equal(["claude", "herald"], store.Senders.Select(s => s.Sender));
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void New_senders_get_default_rules()
    {
        var sender = new SenderSettingsStore(FilePath).GetOrCreate("build-bot");

        Assert.False(sender.Muted);
        Assert.Equal(SenderSettings.DefaultEngineId, sender.EngineId);
        Assert.Equal(SenderSettings.DefaultFilterCharacters, sender.FilterCharacters);
        Assert.Single(sender.Replacements);
    }

    [Fact]
    public void Only_claude_is_shown_as_markdown_by_default()
    {
        var store = new SenderSettingsStore(FilePath);

        Assert.True(store.GetOrCreate("claude").ShowAsMarkdown);
        Assert.False(store.GetOrCreate("herald").ShowAsMarkdown);
        Assert.False(store.GetOrCreate("clipboard").ShowAsMarkdown);
    }

    [Fact]
    public void Lookup_ignores_case_and_creates_each_sender_once()
    {
        var store = new SenderSettingsStore(FilePath);

        var first = store.GetOrCreate("Build-Bot");
        var second = store.GetOrCreate("build-bot");

        Assert.Same(first, second);
        Assert.Same(store.GetOrCreate("CLAUDE"), store.Senders[0]);
        // Senders is the settings page's list: new entries reach it on the UI thread.
        Assert.Equal(["claude", "herald", "Build-Bot"], UiThread.Invoke(() => store.Senders.Select(s => s.Sender).ToArray()));
    }

    [Fact]
    public async Task A_sender_created_from_a_background_thread_is_saved_right_away()
    {
        var store = new SenderSettingsStore(FilePath);

        await Task.Run(() => store.GetOrCreate("from-the-hook-server"), TestContext.Current.CancellationToken);

        Assert.Contains(new SenderSettingsStore(FilePath).Senders, s => s.Sender == "from-the-hook-server");
    }

    [Fact]
    public async Task Looking_up_senders_while_the_ui_thread_waits_does_not_deadlock()
    {
        var store = new SenderSettingsStore(FilePath);

        // The UI thread and a background thread both create senders at the same time,
        // which used to be able to hang Herald.
        var fromUi = Task.Run(() => UiThread.Invoke(() =>
        {
            for (var i = 0; i < 200; i++) store.GetOrCreate($"ui-{i}");
        }), TestContext.Current.CancellationToken);
        var fromBackground = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) store.GetOrCreate($"bg-{i}");
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(fromUi, fromBackground).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(402, UiThread.Invoke(() => store.Senders.Count));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_sender_is_filed_as_unknown(string tag)
    {
        Assert.Equal("unknown", new SenderSettingsStore(FilePath).GetOrCreate(tag).Sender);
    }

    [Fact]
    public void Every_change_is_saved()
    {
        var store = new SenderSettingsStore(FilePath);
        var claude = store.GetOrCreate("claude");

        claude.Muted = true;
        claude.EngineId = "piper";
        claude.VoiceId = "sv_SE-nst-medium";
        claude.AnnounceSender = true;
        claude.ShowAsMarkdown = false;
        claude.FilterCharacters = "*";
        claude.Replacements.Add(new ReplacementRule("TTS", "text to speech"));
        claude.LanguageRules = [new LanguageVoiceRule("Swedish", "piper", "sv_SE-nst-medium")];
        store.GetOrCreate("new-one");

        var reloaded = new SenderSettingsStore(FilePath).GetOrCreate("claude");

        Assert.True(reloaded.Muted);
        Assert.True(reloaded.AnnounceSender);
        Assert.False(reloaded.ShowAsMarkdown);
        Assert.Equal("piper", reloaded.EngineId);
        Assert.Equal("sv_SE-nst-medium", reloaded.VoiceId);
        Assert.Equal("*", reloaded.FilterCharacters);
        Assert.Contains(reloaded.Replacements, r => r is { Find: "TTS", Replace: "text to speech" });
        Assert.Equal([new LanguageVoiceRule("Swedish", "piper", "sv_SE-nst-medium")], reloaded.LanguageRules);
        Assert.Contains(new SenderSettingsStore(FilePath).Senders, s => s.Sender == "new-one");
    }

    [Fact]
    public void Resetting_text_rules_restores_the_defaults_keeps_the_rest_and_is_saved()
    {
        var store = new SenderSettingsStore(FilePath);
        var claude = store.GetOrCreate("claude");
        claude.FilterCharacters = "-";
        claude.ShowAsMarkdown = false;
        claude.Replacements.Add(new ReplacementRule("TTS", "text to speech"));
        claude.Muted = true;
        claude.AnnounceSender = true;
        claude.EngineId = "piper";
        claude.VoiceId = "sv_SE-nst-medium";
        claude.LanguageRules = [new LanguageVoiceRule("Swedish", "piper", "sv_SE-nst-medium")];

        claude.ResetTextRules();

        var reloaded = new SenderSettingsStore(FilePath).GetOrCreate("claude");
        Assert.Equal(SenderSettings.DefaultFilterCharacters, reloaded.FilterCharacters);
        Assert.True(reloaded.ShowAsMarkdown);
        Assert.Equal(SenderSettings.DefaultReplacements().Select(r => (r.Find, r.Replace)),
                     reloaded.Replacements.Select(r => (r.Find, r.Replace)));
        Assert.True(reloaded.Muted);
        Assert.True(reloaded.AnnounceSender);
        Assert.Equal("piper", reloaded.EngineId);
        Assert.Equal("sv_SE-nst-medium", reloaded.VoiceId);
        Assert.Single(reloaded.LanguageRules);
    }

    [Fact]
    public void Editing_a_replacement_in_place_is_saved()
    {
        var store = new SenderSettingsStore(FilePath);
        store.GetOrCreate("claude").Replacements[0].Replace = " - ";

        Assert.Equal(" - ", new SenderSettingsStore(FilePath).GetOrCreate("claude").Replacements[0].Replace);
    }

    [Fact]
    public void Entries_from_before_engines_were_selectable_keep_speaking_with_kokoro()
    {
        File.WriteAllText(FilePath, """[{ "Sender": "claude", "Muted": false, "FilterCharacters": "-" }]""");

        var claude = new SenderSettingsStore(FilePath).GetOrCreate("claude");

        Assert.Equal("kokoro", claude.EngineId);
        Assert.Equal("am_michael", claude.VoiceId);
        Assert.Equal("-", claude.FilterCharacters);
        Assert.True(claude.ShowAsMarkdown);
    }

    [Fact]
    public void A_corrupt_file_starts_over_with_the_seeded_senders()
    {
        File.WriteAllText(FilePath, "[{ broken");

        Assert.Equal(["claude", "herald"], new SenderSettingsStore(FilePath).Senders.Select(s => s.Sender));
    }
}

public class SenderSettingsTests
{
    [Fact]
    public void Null_values_fall_back_to_defaults()
    {
        var sender = new SenderSettings("x") { EngineId = null!, VoiceId = null!, FilterCharacters = null!, LanguageRules = null! };

        Assert.Equal(SenderSettings.DefaultEngineId, sender.EngineId);
        Assert.Equal(String.Empty, sender.VoiceId);
        Assert.Equal(String.Empty, sender.FilterCharacters);
        Assert.Empty(sender.LanguageRules);
    }

    [Fact]
    public void The_replacement_snapshot_follows_edits_but_is_a_separate_copy()
    {
        var sender = new SenderSettings("x", replacements: [new ReplacementRule("a", "b")]);
        var before = sender.ReplacementSnapshot;

        sender.Replacements[0].Replace = "c";
        sender.Replacements.Add(new ReplacementRule("d", "e"));

        Assert.Equal("b", before[0].Replace);
        Assert.Equal(["a=c", "d=e"], sender.ReplacementSnapshot.Select(r => $"{r.Find}={r.Replace}"));
    }

    [Fact]
    public void Removed_replacements_no_longer_raise_changes()
    {
        var rule = new ReplacementRule("a", "b");
        var sender = new SenderSettings("x", replacements: [rule]);
        sender.Replacements.Remove(rule);
        var changes = 0;
        sender.PropertyChanged += (_, _) => changes++;

        rule.Replace = "z";

        Assert.Equal(0, changes);
        Assert.Empty(sender.ReplacementSnapshot);
    }
}

public class HotkeySettingsStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string FilePath => _dir.File("hotkeys.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Has_the_default_bindings()
    {
        var bindings = new HotkeySettingsStore(FilePath).Bindings;

        Assert.Equal(6, bindings.Count);
        var toggle = bindings.Single(b => b.Action == HotkeyAction.Toggle);
        Assert.Equal("Alt+Shift+S", toggle.Display);
        Assert.Equal("Alt+Shift+Plus", bindings.Single(b => b.Action == HotkeyAction.SpeedUp).Display);
        Assert.Equal("Alt+Shift+Minus", bindings.Single(b => b.Action == HotkeyAction.SpeedDown).Display);
    }

    [Fact]
    public void A_hotkey_shows_whether_it_works()
    {
        var binding = new HotkeySettingsStore(FilePath).Bindings[0];
        var changed = new List<string?>();
        binding.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal("Works", binding.Status);
        binding.Problem = "In use by another program";

        Assert.Equal("In use by another program", binding.Status);
        Assert.Contains(nameof(HotkeyBinding.Status), changed);
    }

    [Fact]
    public void Rebinding_is_saved_and_restored()
    {
        var store = new HotkeySettingsStore(FilePath);
        var skip = store.Bindings.Single(b => b.Action == HotkeyAction.Stop);

        skip.Modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        skip.Key = Key.F8;

        var restored = new HotkeySettingsStore(FilePath).Bindings.Single(b => b.Action == HotkeyAction.Stop);
        Assert.Equal("Alt+Control+F8", restored.Display);
    }

    [Fact]
    public void The_file_only_overrides_keys_so_new_actions_still_appear()
    {
        // A file from a version that only knew the toggle hotkey, rebound to F9.
        File.WriteAllText(FilePath, $$"""
            [
              { "Id": 1, "Label": "Toggle", "Action": "Toggle", "Modifiers": 0, "Key": {{(int)Key.F9}} },
              { "Id": 999, "Label": "Gone", "Action": "Removed", "Modifiers": 0, "Key": 1 }
            ]
            """);

        var bindings = new HotkeySettingsStore(FilePath).Bindings;

        Assert.Equal(6, bindings.Count);
        Assert.Equal("F9", bindings.Single(b => b.Id == 1).Display);
        Assert.DoesNotContain(bindings, b => b.Id == 999);
    }

    [Fact]
    public void Display_updates_when_the_key_changes()
    {
        var binding = new HotkeyBinding(1, "Test", HotkeyAction.Toggle, ModifierKeys.Alt, Key.A);
        var changed = new List<string?>();
        binding.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        binding.Key = Key.B;
        binding.Key = Key.B;

        Assert.Equal(["Key", "Display"], changed);
        Assert.Equal("Alt+B", binding.Display);
    }
}

public class LanguageProfileStoreTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string FilePath => _dir.File("language-profiles.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Starts_with_a_switched_off_swedish_example()
    {
        var store = new LanguageProfileStore(FilePath);

        var swedish = Assert.Single(store.Profiles);
        Assert.Equal("Swedish", swedish.Name);
        Assert.False(swedish.Enabled);
        Assert.Empty(store.Snapshot);
    }

    [Fact]
    public void Only_enabled_named_profiles_are_used_for_detection()
    {
        var store = new LanguageProfileStore(FilePath);
        store.Profiles[0].Enabled = true;
        store.Profiles.Add(new LanguageProfile { Name = "", Markers = "x y" });
        store.Profiles.Add(new LanguageProfile { Name = "German", Markers = "und der" });

        Assert.Equal(["Swedish", "German"], store.Snapshot.Select(p => p.Name));
    }

    [Fact]
    public void Edits_update_detection_and_are_saved()
    {
        var store = new LanguageProfileStore(FilePath);
        store.Profiles[0].Enabled = true;
        store.Profiles[0].Markers = "hej";

        Assert.Equal(["hej"], store.Snapshot[0].Words);

        var reloaded = new LanguageProfileStore(FilePath);
        Assert.True(reloaded.Profiles[0].Enabled);
        Assert.Equal("hej", reloaded.Profiles[0].Markers);
    }

    [Fact]
    public void Removing_every_profile_is_remembered()
    {
        var store = new LanguageProfileStore(FilePath);
        store.Profiles.Clear();

        Assert.Empty(new LanguageProfileStore(FilePath).Profiles);
    }

    [Fact]
    public void Min_matches_is_at_least_one()
    {
        Assert.Equal(1, new LanguageProfile { MinMatches = 0 }.MinMatches);
    }
}
