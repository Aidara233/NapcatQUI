using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NapcatQUI.Core.Adapter;

public class NapCatConnection : IAsyncDisposable
{
    private readonly string _wsUrl;
    private readonly string? _accessToken;
    private readonly ILogger<NapCatConnection> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _echoDict = new();

    // ClientWebSocket.SendAsync 不允许并发，所有发送走同一把锁串行化
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // 连接关闭信号：收到 Close 帧或收发异常时完成，供上层等待"掉线"事件
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 最近一次收到数据的时刻（心跳看门狗用）
    private long _lastActivityTicks;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // NapCat 默认 30s 心跳一次；90s 无任何数据可判定连接已死（进程崩溃但 TCP 还活着）
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(15);

    public event Func<string, Task>? OnMessageReceived;
    public event Func<WebSocketState, Task>? OnStateChanged;

    public WebSocketState State => _ws?.State ?? WebSocketState.None;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>连接关闭后完成（网络掉线/远端关闭），正常停用（DisconnectAsync）不会完成</summary>
    public Task Closed => _closedTcs.Task;

    public NapCatConnection(string wsUrl, string? accessToken, ILogger<NapCatConnection> logger)
    {
        _wsUrl = wsUrl;
        _accessToken = accessToken;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Cleanup();
        _ws = new ClientWebSocket();

        var url = _wsUrl;
        if (!string.IsNullOrEmpty(_accessToken))
        {
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
            url = url.Contains('?') ? $"{url}&access_token={_accessToken}" : $"{url}?access_token={_accessToken}";
        }

        _logger.LogInformation("Connecting to NapCat: {Url}", SanitizeUrl(url));

        // 连不上时不要无限挂起（TCP 超时可达几十秒），带 10s 兜底
        var connectTask = _ws.ConnectAsync(new Uri(url), ct);
        var finished = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout, ct));
        if (finished != connectTask)
        {
            // 观测被放弃的连接任务，避免未观察异常
            _ = connectTask.ContinueWith(t => _ = t.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning("WebSocket connect to {Url} timed out", SanitizeUrl(url));
            Cleanup();
            throw new TimeoutException($"WebSocket connect to {SanitizeUrl(url)} timed out after {ConnectTimeout.TotalSeconds}s");
        }
        await connectTask;

        _receiveCts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);
        _ = Task.Run(() => WatchdogAsync(_receiveCts.Token), _receiveCts.Token);

        _ = OnStateChanged?.Invoke(WebSocketState.Open);
        _logger.LogInformation("Connected to NapCat");
    }

    /// <summary>
    /// 心跳看门狗：NapCat 进程崩溃但 TCP 连接还开着时，WebSocket 状态仍是 Open，
    /// 永远不会触发重连。这里一旦发现超过 IdleTimeout 没有收到任何数据，就强制断开，
    /// 让上层走重连逻辑。
    /// </summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(WatchdogInterval, ct);
                var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks));
                if (_ws?.State == WebSocketState.Open && idle > IdleTimeout)
                {
                    _logger.LogWarning("No data from NapCat for {Idle}, forcing reconnect", idle);
                    _closedTcs.TrySetResult();
                    try { _ws.Abort(); } catch { }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>日志用：去掉 query 里的 access_token，避免凭证泄漏到日志</summary>
    private static string SanitizeUrl(string url)
    {
        var idx = url.IndexOf('?');
        if (idx < 0) return url;
        return url[..idx] + "?…";
    }

    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reconnecting to NapCat: {Url}", _wsUrl);
        await ConnectAsync(ct);
    }

    public async Task<JsonDocument?> SendApiRequestAsync(
        string action, Dictionary<string, object?>? @params = null, TimeSpan? timeout = null)
    {
        // 未连接时返回 null 而非抛异常，让调用方走"发送失败"分支，而不是把异常甩给 UI
        if (_ws?.State != WebSocketState.Open) return null;

        var echo = Guid.NewGuid().ToString("N")[..8];
        var request = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["params"] = @params ?? new(),
            ["echo"] = echo
        };
        var json = JsonSerializer.Serialize(request);
        var tcs = new TaskCompletionSource<JsonDocument>();
        _echoDict[echo] = tcs;

        await SendTextAsync(json);

        timeout ??= TimeSpan.FromSeconds(20);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout.Value));
        _echoDict.TryRemove(echo, out _);

        if (completed == tcs.Task)
            return await tcs.Task;

        _logger.LogWarning("API request timeout: {Action} echo={Echo}", action, echo);
        return null;
    }

    public async Task SendApiRequestFireAndForgetAsync(string action, Dictionary<string, object?>? @params = null)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var echo = Guid.NewGuid().ToString("N")[..8];
        var request = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["params"] = @params ?? new(),
            ["echo"] = echo
        };
        var json = JsonSerializer.Serialize(request);
        await SendTextAsync(json);
    }

    public async Task DisconnectAsync()
    {
        _receiveCts?.Cancel();
        try
        {
            if (_ws?.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closing", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error closing WebSocket");
        }
    }

    private void Cleanup()
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        if (_ws != null)
        {
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }

        foreach (var kv in _echoDict)
            kv.Value.TrySetCanceled();
        _echoDict.Clear();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                messageBuffer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close received");
                        _closedTcs.TrySetResult();
                        _ = OnStateChanged?.Invoke(WebSocketState.Closed);
                        return;
                    }

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                var json = messageBuffer.ToString();
                _ = ProcessReceivedJsonAsync(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket error");
            _closedTcs.TrySetResult();
            _ = OnStateChanged?.Invoke(WebSocketState.Aborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop error");
            _closedTcs.TrySetResult();
        }
    }

    private async Task ProcessReceivedJsonAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("echo", out var echoEl))
            {
                var echo = echoEl.GetString();
                if (echo != null && _echoDict.TryGetValue(echo, out var tcs))
                {
                    // 必须给等待方一份独立的 JsonDocument：doc 是 using 声明的，
                    // 方法退出即释放，直接传原 doc 会在读取时抛 ObjectDisposedException
                    tcs.TrySetResult(JsonDocument.Parse(json));
                }
                return;
            }

            if (OnMessageReceived != null)
                await OnMessageReceived.Invoke(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON received");
        }
    }

    private async Task SendTextAsync(string text)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync();
        try
        {
            // 等待锁期间连接可能已断开，再检查一次
            if (_ws?.State != WebSocketState.Open) return;
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        Cleanup();
    }
}
