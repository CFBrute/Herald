using System;
using System.ComponentModel;

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

public class QueueItem : INotifyPropertyChanged
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; }
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

    /// <summary>True for a copy put back in the queue with "Play again".</summary>
    public bool IsCopy { get; init; }

    /// <summary>Name of the language profile detected for this part, or null.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// 0 or 1, alternating per message, so the lists can shade whole messages (all their
    /// parts together) in alternating backgrounds.
    /// </summary>
    public int Band { get; set; }

    private bool _isSelectingText;
    /// <summary>UI state: the item's text is shown as a selectable text box.</summary>
    public bool IsSelectingText
    {
        get => _isSelectingText;
        set
        {
            if (_isSelectingText == value) return;
            _isSelectingText = value;
            OnPropertyChanged(nameof(IsSelectingText));
        }
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
        set
        {
            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    public QueueItem(string text, string sender)
    {
        Text = text;
        Sender = sender;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
