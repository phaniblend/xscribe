using System.IO;

namespace XScribeDesktop;

public static class TranscriptHub
{
    public static event Action<Sentence>? OnSentence;
    public static event Action<Sentence>? OnPinRequested;

    public static void Publish(Sentence s) => OnSentence?.Invoke(s);
    public static void RequestPin(Sentence s) => OnPinRequested?.Invoke(s);

    public static string RepoContext { get; set; } = "";
    public static string Brief { get; set; } = "";

    public static string? ExtractRepoPath(string repoContextBox)
    {
        var text = repoContextBox.Trim();
        var dash = text.LastIndexOf(" — ", StringComparison.Ordinal);
        if (dash >= 0)
        {
            var path = text[(dash + 3)..].Trim();
            if (Directory.Exists(path)) return path;
        }
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = part.Trim().TrimEnd('(', ')');
            if (Directory.Exists(p)) return p;
        }
        return null;
    }
}
