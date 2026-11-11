using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media.Imaging;
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

    /// <summary>Small previews of every image file, for the Image files row; empty for other rows.</summary>
    public ObservableCollection<ImageThumbView> Thumbs { get; } = new();
    public bool HasThumbs => Thumbs.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One image file in the Image files row: a small preview, its name and size, and whether it's to do.</summary>
public sealed class ImageThumbView : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private string _status = "loading…";

    public required string RelPath { get; init; }
    public required string Name { get; init; }
    public required string Caption { get; init; }
    public required bool ToDo { get; init; }
    public string ToolTip => $"{RelPath}\n{Caption}. Click for full size.";

    public BitmapSource? Image
    {
        get => _image;
        set { _image = value; Changed(nameof(Image)); Changed(nameof(HasImage)); }
    }

    public bool HasImage => _image is not null;

    /// <summary>Shown instead of the picture while it loads, or when Windows can't draw it.</summary>
    public string Status
    {
        get => _status;
        set { _status = value; Changed(nameof(Status)); }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record CheckGroup(string Category, IReadOnlyList<CheckRowView> Items);
