using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;

namespace ScanFlowOcr.App.Models;

public sealed class PlaylistThumbnailItem : INotifyPropertyChanged
{
    private bool _isActive;

    public required int Index { get; init; }
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public Bitmap? Thumbnail { get; init; }

    public string IndexDisplay => $"#{Index + 1}";

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
