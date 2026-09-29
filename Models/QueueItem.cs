using System;

namespace Herald.Models;

public enum QueueItemStatus
{
    Queued,
    Synthesizing,
    Ready,
    Playing,
    Done,
    Skipped,
    Failed
}

public class QueueItem : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The text as it was sent, shown in the lists and copied by "Copy text".</summary>
    public string Text { get; }

    /// <summary>What the voice gets: the text after the sender's filters and replacements.</summary>
    public string SpokenText { get; }

    /// <summary>The text is shown formatted as Markdown (the sender's setting when it arrived).</summary>
    public bool ShowAsMarkdown { get; init; }

    public string Sender { get; }
    public DateTime EnqueuedAt { get; init; } = DateTime.Now;
    public string? AudioFilePath { get; set; }

    /// <summary>
    /// Spoken even while speech is turned off: status messages like "Off", and
    /// items the user explicitly asked to play again.
    /// </summary>
    public bool PlayWhenDisabled { get; init; }

    /// <summary>
    /// Audio that already exists for this text (a replayed copy), so it isn't
    /// synthesized again. The file belongs to the original item.
    /// </summary>
    public string? PreparedAudioPath { get; init; }

    /// <summary>"Speed 160": replaced by the next speed announcement if it hasn't played yet.</summary>
    public bool IsSpeedAnnouncement { get; init; }

    /// <summary>True for a copy put back in the queue with "Play again".</summary>
    public bool IsCopy { get; init; }

    /// <summary>Name of the language profile detected for this part, or null.</summary>
    public string? Language { get; init; }

    private string? _synthesisInfo;
    /// <summary>
    /// Which engine, voice and speed made this part's audio - shown as the item's tooltip.
    /// Set when synthesis starts; null until then.
    /// </summary>
    public string? SynthesisInfo
    {
        get => _synthesisInfo;
        set
        {
            if (SetField(ref _synthesisInfo, value)) OnPropertyChanged(nameof(Details));
        }
    }

    /// <summary>The item's tooltip: how its audio was made and the text the voice got.</summary>
    public string Details => SynthesisInfo is { } info
        ? $"{info}\n\nSpoken text:\n{SpokenText}"
        : $"Spoken text:\n{SpokenText}";

    /// <summary>
    /// 0 or 1, alternating per message, so the lists can shade whole messages (all their
    /// parts together) in alternating backgrounds.
    /// </summary>
    public int Band
    {
        get => _band;
        set => SetField(ref _band, value);
    }
    private int _band;

    private bool _isSelectingText;
    /// <summary>UI state: the item's text is shown as a selectable text box.</summary>
    public bool IsSelectingText
    {
        get => _isSelectingText;
        set => SetField(ref _isSelectingText, value);
    }

    /// <summary>
    /// Parts split from the same message share a GroupId, so skipping one part
    /// skips the rest of that message too.
    /// </summary>
    public Guid GroupId { get; init; }
    public int PartIndex { get; init; } = 1;
    public int PartCount { get; init; } = 1;
    public string PartLabel => PartCount > 1 ? $"{PartIndex}/{PartCount}" : string.Empty;

    private QueueItemStatus _status = QueueItemStatus.Queued;
    public QueueItemStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public QueueItem(string text, string sender, string? spokenText = null)
    {
        Text = text;
        SpokenText = spokenText ?? text;
        Sender = sender;
    }
}
