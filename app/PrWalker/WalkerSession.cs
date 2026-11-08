using System.Text.Json;

namespace XScribeDesktop.PrWalker;

public class WalkerSession
{
    public int? BoundTabId { get; private set; }
    public string? BoundTabTitle { get; private set; }
    public string? LastError { get; private set; }
    public WalkerStep? LastRunStep { get; private set; }

    public async Task<IReadOnlyList<BrowserTabInfo>> ListTabsAsync(CancellationToken ct = default)
    {
        var result = await WalkerHub.Instance.SendCommandAsync("listTabs", ct: ct);
        if (!result.TryGetProperty("tabs", out var tabs)) return Array.Empty<BrowserTabInfo>();
        return JsonSerializer.Deserialize<List<BrowserTabInfo>>(tabs.GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    public async Task<bool> BindTabAsync(int tabId)
    {
        var result = await WalkerHub.Instance.SendCommandAsync("bindTab", new Dictionary<string, object?> { ["tabId"] = tabId });
        if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            BoundTabId = tabId;
            BoundTabTitle = result.TryGetProperty("url", out var u) ? u.GetString() : null;
            LastError = null;
            return true;
        }
        LastError = result.TryGetProperty("error", out var e) ? e.GetString() : "bind failed";
        return false;
    }

    public async Task<IReadOnlyList<string>> GetPrFileListAsync()
    {
        var result = await WalkerHub.Instance.SendCommandAsync("getPrFileList");
        if (result.TryGetProperty("files", out var files))
            return JsonSerializer.Deserialize<List<string>>(files.GetRawText()) ?? new();
        return Array.Empty<string>();
    }

    public async Task<bool> RunStepAsync(WalkerStep step, bool skipAnim = false)
    {
        if (!step.Navigable) return false;

        // Plain dictionaries — anonymous types can fail System.Text.Json in Dictionary<object?>
        var lineRanges = step.LineRanges
            .Select(r => new Dictionary<string, object?> { ["start"] = r.Start, ["end"] = r.End })
            .ToList();
        var payload = new Dictionary<string, object?>
        {
            ["path"] = step.Path,
            ["lineRanges"] = lineRanges,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await WalkerHub.Instance.SendCommandAsync("runStep", new Dictionary<string, object?>
        {
            ["payload"] = payload,
            ["skipAnim"] = skipAnim,
        }, cts.Token);

        if (result.TryGetProperty("ok", out var ok) && ok.GetBoolean())
        {
            // Do not set Visited here — must be set on the UI thread
            LastRunStep = step;
            LastError = null;
            return true;
        }
        LastError = result.TryGetProperty("error", out var e) ? e.GetString() : "run failed";
        return false;
    }

    public async Task AbortStepAsync()
    {
        try { await WalkerHub.Instance.SendCommandAsync("abortStep"); }
        catch { /* ignore */ }
    }

    public void Unbind()
    {
        BoundTabId = null;
        BoundTabTitle = null;
    }
}
