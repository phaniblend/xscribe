using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace XScribeDesktop.PrWalker;

public class RipgrepLocator
{
    public record Hit(string Path, int Line, string Text);

    public async Task<IReadOnlyList<Hit>> SearchAsync(
        string repoRoot,
        IReadOnlyList<string> prFiles,
        string query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot) || prFiles.Count == 0)
            return Array.Empty<Hit>();

        var keywords = ExtractKeywords(query);
        if (keywords.Count == 0) return Array.Empty<Hit>();

        var hits = new List<Hit>();
        foreach (var kw in keywords.Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var batch = await RunRgAsync(repoRoot, prFiles, kw, ct);
            hits.AddRange(batch);
        }

        return hits
            .GroupBy(h => $"{h.Path}:{h.Line}")
            .Select(g => g.First())
            .Take(20)
            .ToList();
    }

    static List<string> ExtractKeywords(string query)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(query, @"[A-Za-z_][A-Za-z0-9_]{2,}"))
        {
            var w = m.Value;
            if (IsStopWord(w)) continue;
            if (!result.Contains(w, StringComparer.OrdinalIgnoreCase))
                result.Add(w);
        }
        return result;
    }

    static bool IsStopWord(string w) => w.ToLowerInvariant() is
        "the" or "and" or "where" or "show" or "back" or "that" or "this" or "can" or "you"
        or "are" or "was" or "were" or "file" or "code" or "please" or "let" or "see";

    static async Task<List<Hit>> RunRgAsync(string repoRoot, IReadOnlyList<string> prFiles, string pattern, CancellationToken ct)
    {
        var hits = new List<Hit>();
        var args = new List<string> { "--line-number", "--no-heading", "--fixed-strings", pattern };
        foreach (var f in prFiles.Take(40))
            args.Add(f);

        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            ArgumentList = { },
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return hits;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var pathPart = line[..idx];
                var rest = line[(idx + 1)..];
                var idx2 = rest.IndexOf(':');
                if (idx2 <= 0) continue;
                if (!int.TryParse(rest[..idx2], out var lineNum)) continue;
                var text = rest[(idx2 + 1)..];
                var normalized = pathPart.Replace('\\', '/');
                hits.Add(new Hit(normalized, lineNum, text.Trim()));
            }
        }
        catch
        {
            // rg not installed — fallback empty
        }
        return hits;
    }

    public static (string Path, LineRange Range)? PickBestHit(IReadOnlyList<Hit> hits, string query)
    {
        if (hits.Count == 0) return null;
        var byFile = hits.GroupBy(h => h.Path).ToList();
        if (byFile.Count == 1)
        {
            var g = byFile[0].OrderBy(h => h.Line).ToList();
            var start = g[0].Line;
            var end = g.Count > 1 ? g[^1].Line : start + Math.Min(15, 5);
            return (g[0].Path, new LineRange { Start = start, End = end });
        }
        return null;
    }
}
