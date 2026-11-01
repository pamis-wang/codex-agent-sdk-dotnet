using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PamisWang.CodexAgentSdk.Transport;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>
/// 記憶體裡的假 app-server：記下 client 送來的每則訊息，交給測試的腳本回應；initialize 自動回覆。
/// 對應 Python SDK 測試的 <c>app_server_harness.py</c>。
/// </summary>
internal sealed class FakeAppServer : ITransport
{
    private readonly Channel<JsonElement> _toClient = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<ClientMessage> _fromClient = Channel.CreateUnbounded<ClientMessage>();
    private readonly Func<FakeAppServer, ClientMessage, Task>? _script;
    private readonly Lock _gate = new();
    private readonly List<ClientMessage> _received = [];

    public FakeAppServer(Func<FakeAppServer, ClientMessage, Task>? script = null)
    {
        _script = script;
    }

    public string UserAgent { get; init; } = "codex_cli_rs/0.166.0 (Windows 10.0.26300; x86_64)";

    public bool Disposed { get; private set; }

    public IReadOnlyList<ClientMessage> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public string StandardErrorTail => "";

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task WriteMessageAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var message = new ClientMessage(document.RootElement.Clone());
        lock (_gate)
        {
            _received.Add(message);
        }

        _fromClient.Writer.TryWrite(message);
        if (message.Method == "initialize" && message.Id is { } id)
        {
            Reply(id, $$"""{"userAgent": {{JsonSerializer.Serialize(UserAgent)}}, "codexHome": "C:/Users/test/.codex", "platformFamily": "windows", "platformOs": "windows"}""");
            return;
        }

        if (_script is not null)
        {
            await _script(this, message).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _toClient.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    /// <summary>送出一則原始 JSON。</summary>
    public void Send(string json)
    {
        using var document = JsonDocument.Parse(json);
        _toClient.Writer.TryWrite(document.RootElement.Clone());
    }

    public void Reply(JsonElement id, string resultJson) => Send($$"""{"id": {{id.GetRawText()}}, "result": {{resultJson}}}""");

    public void ReplyError(JsonElement id, int code, string message, string? dataJson = null)
    {
        var data = dataJson is null ? "" : ", \"data\": " + dataJson;
        Send($$"""{"id": {{id.GetRawText()}}, "error": {"code": {{code}}, "message": {{JsonSerializer.Serialize(message)}}{{data}}} }""");
    }

    public void Notify(string method, string paramsJson) =>
        Send($$"""{"method": {{JsonSerializer.Serialize(method)}}, "params": {{paramsJson}}}""");

    public void Request(string id, string method, string paramsJson) =>
        Send($$"""{"id": {{JsonSerializer.Serialize(id)}}, "method": {{JsonSerializer.Serialize(method)}}, "params": {{paramsJson}}}""");

    /// <summary>關閉 stdout，模擬 app-server 結束。</summary>
    public void Close() => _toClient.Writer.TryComplete();

    /// <summary>等 client 送出符合條件的下一則訊息。</summary>
    public async Task<ClientMessage> WaitForAsync(Func<ClientMessage, bool> predicate, int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (true)
        {
            var message = await _fromClient.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (predicate(message))
            {
                return message;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        Close();
        return ValueTask.CompletedTask;
    }
}

/// <summary>client 送給 app-server 的一則訊息。</summary>
internal sealed record ClientMessage(JsonElement Raw)
{
    public string? Method => Raw.TryGetProperty("method", out var method) ? method.GetString() : null;

    public JsonElement? Id => Raw.TryGetProperty("id", out var id) ? id : null;

    public JsonElement Params => Raw.TryGetProperty("params", out var parameters) ? parameters : default;

    public bool IsRequest(string method) => Method == method && Id is not null;
}

/// <summary>假 server 用的協定 JSON。只填 schema 要求的欄位。</summary>
internal static class Json
{
    public static string Thread(string id = "thr_1") =>
        $$"""
        {"id": "{{id}}", "cliVersion": "0.166.0", "createdAt": 0, "updatedAt": 0, "cwd": "C:/work", "ephemeral": true,
         "modelProvider": "openai", "preview": "", "projectId": null, "sessionId": "s1", "source": "cli",
         "status": {"type": "idle"}, "turns": []}
        """;

    public static string ThreadStartResponse(string id = "thr_1") =>
        $$"""
        {"approvalPolicy": "on-request", "approvalsReviewer": "auto_review", "cwd": "C:/work", "model": "gpt-6",
         "modelProvider": "openai", "sandbox": {"type": "readOnly"}, "thread": {{Thread(id)}}}
        """;

    public static string Turn(string id, string status = "inProgress", string? error = null)
    {
        var errorJson = error is null ? "" : $$""", "error": {"message": {{JsonSerializer.Serialize(error)}}}""";
        return $$"""{"id": "{{id}}", "items": [], "status": "{{status}}"{{errorJson}}}""";
    }

    public static string TurnEvent(string threadId, string turnId, string status = "inProgress", string? error = null) =>
        $$"""{"threadId": "{{threadId}}", "turn": {{Turn(turnId, status, error)}}}""";

    public static string AgentMessage(string id, string text, string? phase = null) =>
        $$"""{"type": "agentMessage", "id": "{{id}}", "text": {{JsonSerializer.Serialize(text)}}{{(phase is null ? "" : $", \"phase\": \"{phase}\"")}}}""";

    public static string ItemCompleted(string threadId, string turnId, string item) =>
        $$"""{"threadId": "{{threadId}}", "turnId": "{{turnId}}", "completedAtMs": 1, "item": {{item}}}""";

    public static string Delta(string threadId, string turnId, string delta) =>
        $$"""{"threadId": "{{threadId}}", "turnId": "{{turnId}}", "itemId": "msg_1", "delta": {{JsonSerializer.Serialize(delta)}}}""";

    public static string TokenUsage(string threadId, string turnId, long total) =>
        $$"""
        {"threadId": "{{threadId}}", "turnId": "{{turnId}}", "tokenUsage": {
          "last": {"cachedInputTokens": 0, "inputTokens": 1, "outputTokens": 1, "reasoningOutputTokens": 0, "totalTokens": 2},
          "total": {"cachedInputTokens": 0, "inputTokens": 1, "outputTokens": 1, "reasoningOutputTokens": 0, "totalTokens": {{total}}}
        } }
        """;
}
