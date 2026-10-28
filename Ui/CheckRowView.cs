using System.ComponentModel;
using AppConsistency.Core;

namespace AppConsistency.Ui;

/// <summary>A check result as shown in the details panel, plus whether its fix is queued.</summary>
public sealed class CheckRowView : INotifyPropertyChanged
{
    private bool _isQueued;

    public CheckRowView(CheckResult result, bool isQueued)
    {
        Result = result;
        _isQueued = isQueued;
    }

    public CheckResult Result { get; }

    public string Rule => Result.Rule;
    public string Detail => Result.Detail;
    public CheckStatus Status => Result.Status;
    public string StatusLabel => Result.StatusLabel;
    public string StatusWord => Result.StatusWord;
    public bool CanFix => Result.CanFix;
    public FixKind Fix => Result.Fix;
    public FixTarget Target => Result.Target;

    public bool IsQueued
    {
        get => _isQueued;
        set
        {
            if (_isQueued == value) return;
            _isQueued = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsQueued)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FixLabel)));
        }
    }

    public string FixLabel => IsQueued ? "Queued" : "Fix";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CheckGroup(string Category, IReadOnlyList<CheckRowView> Items);
