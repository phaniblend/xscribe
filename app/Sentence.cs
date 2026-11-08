using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace XScribeDesktop;

public class Sentence : INotifyPropertyChanged
{
    public required string Speaker { get; init; } // "you" | "them"
    public required string Text { get; init; }
    public long Ts { get; init; }
    public int Index { get; init; }

    private bool _sent;
    public bool Sent
    {
        get => _sent;
        set { _sent = value; OnPropertyChanged(); }
    }

    private bool _pinned;
    public bool Pinned
    {
        get => _pinned;
        set { _pinned = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class WorkerMessage
{
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
    public long Ts { get; set; }
}
