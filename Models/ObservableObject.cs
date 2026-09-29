using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Herald.Models;

/// <summary>Change notification for anything the UI binds to.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Stores the value and notifies, unless it didn't change. Returns whether it changed.</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
