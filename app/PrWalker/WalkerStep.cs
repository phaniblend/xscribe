using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace XScribeDesktop.PrWalker;

public enum WalkerStepSource { Guide, Call }
public enum WalkerStepStatus { Ready, Resolving, Failed, LowConfidence, NoNavigate }

public class LineRange
{
    public int Start { get; set; }
    public int End { get; set; }

    public string Display => Start == End ? $"{Start}" : $"{Start} → {End}";

    public bool Overlaps(LineRange other) =>
        Start <= other.End && other.Start <= End;
}

public class WalkerStep : INotifyPropertyChanged
{
    public WalkerStepSource Source { get; init; }
    public string Title { get; init; } = "";
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public string? TriggerQuote { get; init; }
    public List<LineRange> LineRanges { get; set; } = new();
    public bool Navigable => Status is WalkerStepStatus.Ready or WalkerStepStatus.LowConfidence
        && !string.IsNullOrWhiteSpace(Path) && LineRanges.Count > 0;

    private WalkerStepStatus _status = WalkerStepStatus.Ready;
    public WalkerStepStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayStatus)); OnPropertyChanged(nameof(Navigable)); }
    }

    private bool _visited;
    public bool Visited
    {
        get => _visited;
        set { _visited = value; OnPropertyChanged(); }
    }

    public string FileName => string.IsNullOrEmpty(Path) ? "" : Path.Replace('\\', '/').Split('/').Last();
    public string LinesText => LineRanges.Count == 0 ? "" : string.Join(", ", LineRanges.Select(r => r.Display));
    public string DisplayStatus => Status switch
    {
        WalkerStepStatus.Resolving => "…",
        WalkerStepStatus.Failed => "failed",
        WalkerStepStatus.LowConfidence => "?",
        WalkerStepStatus.NoNavigate => "—",
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// Guide JSON shapes
public class GuideStepDto
{
    public string Title { get; set; } = "";
    public string Hint { get; set; } = "";
    public List<GuideBlockDto> Blocks { get; set; } = new();
}

public class GuideBlockDto
{
    public string File { get; set; } = "";
    public string Fname { get; set; } = "";
    public List<GuideMethodDto> Methods { get; set; } = new();
}

public class GuideMethodDto
{
    public string Name { get; set; } = "";
    public string Lines { get; set; } = "";
}

public class LocationResultDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("lineRanges")]
    public List<LineRangeDto>? LineRanges { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "high";
}

public class LineRangeDto
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }
}

public class BrowserTabInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public int WindowId { get; set; }
    public bool Active { get; set; }

    public string Display
    {
        get
        {
            var url = Url ?? "";
            var kind = url.Contains("/files", StringComparison.OrdinalIgnoreCase) ? "files"
                : url.Contains("/changes", StringComparison.OrdinalIgnoreCase) ? "changes" : "pr";
            var monitor = Active ? " · active" : "";
            return $"{Title ?? "(untitled)"}  ({kind}{monitor})";
        }
    }
}
