using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace XScribeDesktop.PrWalker;

public sealed class WalkerHub : IDisposable
{
    public static WalkerHub Instance { get; } = new();

    private readonly HttpListener _listener = new();
    private CancellationTokenSource _cts = new();
    private WebSocket? _extension;
    private Task? _listenTask;
    private long _nextId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();

    public bool ExtensionConnected => _extension?.State == WebSocketState.Open;
    public event Action<bool>? ExtensionConnectionChanged;

    public void Start()
    {
        if (_listenTask != null) return;
        try
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add("http://127.0.0.1:17341/walker/");
            _listener.Prefixes.Add("http://localhost:17341/walker/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            Debug.WriteLine("[WalkerHub] listening on ws://127.0.0.1:17341/walker");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WalkerHub] failed to start: {ex.Message}");
            throw;
        }
    }

    public string HubUrl => "ws://127.0.0.1:17341/walker/";

    public bool IsListening => _listenTask != null && _listener.IsListening;

    async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(async () =>
                {
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
                        if (path.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
                        {
                            var body = Encoding.UTF8.GetBytes("ok");
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "text/plain";
                            ctx.Response.ContentLength64 = body.Length;
                            await ctx.Response.OutputStream.WriteAsync(body, ct);
                            ctx.Response.Close();
                            return;
                        }
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                        return;
                    }
                    var wsCtx = await ctx.AcceptWebSocketAsync(null);
                    await HandleExtension(wsCtx.WebSocket, ct);
                }, ct);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch { /* retry */ }
        }
    }

    async Task HandleExtension(WebSocket ws, CancellationToken ct)
    {
        if (_extension != null && _extension != ws)
        {
            var old = _extension;
            _extension = null;
            try { await old.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None); } catch { }
        }
        _extension = ws;
        ExtensionConnectionChanged?.Invoke(true);

        var buf = new byte[8192];
        var msgBuf = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                msgBuf.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    msgBuf.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var json = Encoding.UTF8.GetString(msgBuf.GetBuffer(), 0, (int)msgBuf.Length);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var msgType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

                if (msgType == "reply"
                    && root.TryGetProperty("id", out var idProp)
                    && _pending.TryRemove(idProp.GetInt64(), out var tcs))
                {
                    tcs.TrySetResult(root.GetProperty("result").Clone());
                }
                else if (msgType == "hello")
                {
                    Debug.WriteLine("[WalkerHub] extension connected");
                    ExtensionConnectionChanged?.Invoke(true);
                }
            }
        }
        catch { /* disconnect */ }
        finally
        {
            // Only clear if THIS socket is still the active one (reconnect race fix)
            if (ReferenceEquals(_extension, ws))
            {
                _extension = null;
                ExtensionConnectionChanged?.Invoke(false);
            }
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
        }
    }

    public Task<JsonElement> SendCommandAsync(string type, object? payload = null, CancellationToken ct = default)
    {
        Dictionary<string, object?>? extras = null;
        if (payload != null)
            extras = new Dictionary<string, object?> { ["payload"] = payload };
        return SendCommandAsync(type, extras, ct);
    }

    public async Task<JsonElement> SendCommandAsync(string type, Dictionary<string, object?>? extras, CancellationToken ct = default)
    {
        if (_extension?.State != WebSocketState.Open)
            throw new InvalidOperationException("PR Walker extension not connected — load it in Chrome");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(ct);
        });

        try
        {
            var dict = new Dictionary<string, object?> { ["type"] = type, ["id"] = id };
            if (extras != null)
                foreach (var kv in extras) dict[kv.Key] = kv.Value;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            await _extension.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            return await tcs.Task.WaitAsync(ct);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listenTask = null;
        if (_extension != null)
        {
            try { _extension.Abort(); } catch { }
            _extension = null;
        }
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
    }
}
