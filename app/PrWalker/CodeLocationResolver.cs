using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XScribeDesktop.PrWalker;

public class CodeLocationResolver
{
    private static readonly HttpClient Http = new();
    private readonly RipgrepLocator _rg = new();

    public string? ApiKey { get; set; }

    public async Task ResolveCallStepAsync(
        WalkerStep step,
        string triggerQuote,
        IReadOnlyList<Sentence> recentTranscript,
        string? repoRoot,
        IReadOnlyList<string> prFiles,
        CancellationToken ct = default)
    {
        try
        {
            // Tier 1: ripgrep
            if (!string.IsNullOrEmpty(repoRoot) && prFiles.Count > 0)
            {
                var hits = await _rg.SearchAsync(repoRoot, prFiles, triggerQuote, ct);
                var best = RipgrepLocator.PickBestHit(hits, triggerQuote);
                if (best != null)
                {
                    ApplyResult(step, best.Value.Path, new List<LineRange> { best.Value.Range },
                        InferLabel(triggerQuote, hits), "high");
                    return;
                }
            }

            // Tier 2: Claude API
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                step.Status = WalkerStepStatus.Failed;
                step.Label = "Set ANTHROPIC_API_KEY for AI resolve";
                return;
            }

            var apiResult = await CallClaudeAsync(triggerQuote, recentTranscript, repoRoot, prFiles, ct);
            if (apiResult != null)
            {
                var conf = apiResult.Confidence?.ToLowerInvariant() ?? "high";
                var status = conf == "low" ? WalkerStepStatus.LowConfidence : WalkerStepStatus.Ready;
                ApplyResult(step, apiResult.Path,
                    apiResult.LineRanges?.Select(r => new LineRange { Start = r.Start, End = r.End }).ToList()
                        ?? new List<LineRange>(),
                    apiResult.Label, conf);
                step.Status = status;
                return;
            }

            step.Status = WalkerStepStatus.Failed;
            step.Label = "Could not resolve location";
        }
        catch (Exception ex)
        {
            step.Status = WalkerStepStatus.Failed;
            step.Label = ex.Message;
        }
    }

    static void ApplyResult(WalkerStep step, string path, List<LineRange> ranges, string label, string _)
    {
        step.Path = path.Replace('\\', '/');
        step.LineRanges = ranges;
        step.Label = label;
        step.Status = ranges.Count > 0 ? WalkerStepStatus.Ready : WalkerStepStatus.Failed;
    }

    static string InferLabel(string quote, IReadOnlyList<RipgrepLocator.Hit> hits)
    {
        if (hits.Count > 0)
        {
            var t = hits[0].Text.Trim();
            if (t.Length > 60) t = t[..60] + "…";
            return t;
        }
        return quote.Length > 50 ? quote[..50] + "…" : quote;
    }

    async Task<LocationResultDto?> CallClaudeAsync(
        string trigger,
        IReadOnlyList<Sentence> transcript,
        string? repoRoot,
        IReadOnlyList<string> prFiles,
        CancellationToken ct)
    {
        var ctx = string.Join("\n", transcript.TakeLast(8).Select(s => $"[{s.Speaker}] {s.Text}"));
        var files = string.Join("\n", prFiles.Take(60));

        var prompt =
            "You are a code navigator for a live PR review call.\n" +
            "Return ONLY valid JSON (no markdown fences) with this shape:\n" +
            "{\"path\":\"repo-relative/path.ts\",\"lineRanges\":[{\"start\":10,\"end\":20}]," +
            "\"label\":\"short description\",\"confidence\":\"high|low\"}\n\n" +
            $"Question/trigger: {trigger}\n\n" +
            "Recent transcript:\n" + ctx + "\n\n" +
            $"Repo root: {repoRoot ?? "unknown"}\n" +
            "PR changed files:\n" + files + "\n\n" +
            "Pick the best file and line range from the PR files list only. If unsure, set confidence to low.";

        var body = new
        {
            model = "claude-sonnet-4-20250514",
            max_tokens = 512,
            messages = new[] { new { role = "user", content = prompt } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        text = text.Trim();
        var m = Regex.Match(text, @"\{[\s\S]*\}");
        if (!m.Success) return null;
        return JsonSerializer.Deserialize<LocationResultDto>(m.Value,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
