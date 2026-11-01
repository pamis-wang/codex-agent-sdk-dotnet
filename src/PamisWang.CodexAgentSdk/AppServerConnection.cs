using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using PamisWang.CodexAgentSdk.Protocol;
using PamisWang.CodexAgentSdk.Transport;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 跟 <c>codex app-server</c> 的 JSON-RPC 連線：請求與回應配對、通知分派、回覆 server 的請求。對應 Python SDK 的 <c>CodexClient</c>。
/// </summary>
/// <remarks>
/// app-server 的 JSON-RPC 不帶 <c>"jsonrpc": "2.0"</c> 欄位，送出時也不加。
/// Python 版在讀取迴圈裡直接呼叫核准 handler，handler 沒回覆前整條串流都會停住；這裡改在背景執行，
/// handler 丟例外時以 JSON-RPC 錯誤回覆，而不是讓讀取迴圈結束。
/// </remarks>
internal sealed class AppServerConnection : IAsyncDisposable
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly CodexAgentOptions _options;
    private readonly ITransport _transport;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, CancellationTokenSource> _serverRequests = [];
    private readonly Lock _serverRequestsGate = new();
    private readonly Dictionary<string, ThreadLock> _threadLocks = [];
    private readonly Lock _threadLocksGate = new();
    private CheckoutCapabilities? _capabilities;
    private Task? _readLoop;
    private int _disposed;

    public AppServerConnection(CodexAgentOptions options, ITransport transport)
    {
        _options = options;
        _transport = transport;
    }

    public MessageRouter Router { get; } = new();

    /// <summary>initialize 回報的 Codex CLI 版本，例如 <c>0.166.0</c>。</summary>
    public string? RuntimeVersion { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (_transport is SubprocessTransport { ExecutablePath: { } path })
        {
            _capabilities = new CheckoutCapabilities(path, _options);
        }

        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>initialize 握手：送出 client 資訊，檢查回應，再送 <c>initialized</c> 通知。</summary>
    public async Task<(InitializeResponse Response, CodexServerInfo ServerInfo)> InitializeAsync(CancellationToken cancellationToken)
    {
        RuntimeVersion = null;
        var response = await RequestAsync(
            "initialize",
            new InitializeParams
            {
                ClientInfo = new ClientInfo { Name = _options.ClientName, Title = _options.ClientTitle, Version = _options.ClientVersion },
                Capabilities = new InitializeCapabilities { ExperimentalApi = _options.ExperimentalApi },
            },
            ProtocolJsonContext.Default.InitializeParams,
            ProtocolJsonContext.Default.InitializeResponse,
            cancellationToken).ConfigureAwait(false);
        var serverInfo = InitializeMetadata.Validate(response);
        RuntimeVersion = serverInfo.RuntimeVersion;
        // 版本只用來警告；沒有地方送警告就不檢查
        if (_options.WarningCallback is { } warn
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RuntimeRequirements.SkipVersionCheckVariable)))
        {
            RuntimeRequirements.WarnIfOutdated(serverInfo.RuntimeVersion, (_transport as SubprocessTransport)?.ExecutablePath, warn);
        }

        await NotifyAsync("initialized", cancellationToken).ConfigureAwait(false);
        return (response, serverInfo);
    }

    /// <summary>送出請求並等待回應，參數與回應都是協定型別。</summary>
    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> paramsInfo,
        JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken)
    {
        var result = await RequestRawAsync(method, JsonSerializer.SerializeToElement(parameters, paramsInfo), cancellationToken)
            .ConfigureAwait(false);
        return ParseResult(method, result, resultInfo);
    }

    /// <summary>送出沒有參數的請求並等待回應。</summary>
    public async Task<TResult> RequestAsync<TResult>(string method, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var result = await RequestRawAsync(method, null, cancellationToken).ConfigureAwait(false);
        return ParseResult(method, result, resultInfo);
    }

    /// <summary>送出請求並等待回應的原始 JSON。對應 Python 的 <c>_request_raw</c>。</summary>
    public async Task<JsonElement> RequestRawAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        await CheckRuntimeFieldsAsync(method, parameters, cancellationToken).ConfigureAwait(false);

        var requestId = Guid.NewGuid().ToString();
        var waiter = Router.CreateResponseWaiter(requestId);
        try
        {
            await WriteAsync(
                writer =>
                {
                    writer.WriteString("id", requestId);
                    writer.WriteString("method", method);
                    if (parameters is { } value)
                    {
                        writer.WritePropertyName("params");
                        value.WriteTo(writer);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            return await waiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Router.DiscardResponseWaiter(requestId);
            throw;
        }
    }

    /// <summary>送出不等回應的通知。</summary>
    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        WriteAsync(writer => writer.WriteString("method", method), cancellationToken);

    // ---------------------------------------------------------------- 帳號

    /// <summary>開始登入；ChatGPT 與 device code 登入會開始收集這次登入的通知。</summary>
    public async Task<LoginAccountResponse> AccountLoginStartAsync(LoginAccountParams parameters, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            "account/login/start", parameters,
            ProtocolJsonContext.Default.LoginAccountParams, ProtocolJsonContext.Default.LoginAccountResponse,
            cancellationToken).ConfigureAwait(false);
        switch (response)
        {
            case ChatgptLoginAccountResponse chatgpt:
                Router.RegisterLogin(chatgpt.LoginId);
                break;
            case ChatgptDeviceCodeLoginAccountResponse deviceCode:
                Router.RegisterLogin(deviceCode.LoginId);
                break;
        }

        return response;
    }

    public Task<CancelLoginAccountResponse> AccountLoginCancelAsync(string loginId, CancellationToken cancellationToken) =>
        RequestAsync(
            "account/login/cancel", new CancelLoginAccountParams { LoginId = loginId },
            ProtocolJsonContext.Default.CancelLoginAccountParams, ProtocolJsonContext.Default.CancelLoginAccountResponse,
            cancellationToken);

    public Task<GetAccountResponse> AccountReadAsync(GetAccountParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(
            "account/read", parameters,
            ProtocolJsonContext.Default.GetAccountParams, ProtocolJsonContext.Default.GetAccountResponse,
            cancellationToken);

    public Task<LogoutAccountResponse> AccountLogoutAsync(CancellationToken cancellationToken) =>
        RequestAsync("account/logout", ProtocolJsonContext.Default.LogoutAccountResponse, cancellationToken);

    /// <summary>等這次登入的 <c>account/login/completed</c>。</summary>
    public async Task<AccountLoginCompletedNotification> WaitForLoginCompletedAsync(string loginId, CancellationToken cancellationToken)
    {
        Router.RegisterLogin(loginId);
        try
        {
            while (true)
            {
                var notification = await Router.NextLoginNotificationAsync(loginId, cancellationToken).ConfigureAwait(false);
                if (notification.Payload is AccountLoginCompletedNotification completed && completed.LoginId == loginId)
                {
                    return completed;
                }
            }
        }
        finally
        {
            Router.UnregisterLogin(loginId);
        }
    }

    // ---------------------------------------------------------------- thread

    public Task<ThreadStartResponse> ThreadStartAsync(ThreadStartParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/start", parameters,
            ProtocolJsonContext.Default.ThreadStartParams, ProtocolJsonContext.Default.ThreadStartResponse,
            cancellationToken);

    public Task<ThreadResumeResponse> ThreadResumeAsync(ThreadResumeParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/resume", parameters,
            ProtocolJsonContext.Default.ThreadResumeParams, ProtocolJsonContext.Default.ThreadResumeResponse,
            cancellationToken);

    public Task<ThreadForkResponse> ThreadForkAsync(ThreadForkParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/fork", parameters,
            ProtocolJsonContext.Default.ThreadForkParams, ProtocolJsonContext.Default.ThreadForkResponse,
            cancellationToken);

    public Task<ThreadListResponse> ThreadListAsync(ThreadListParams parameters, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/list", parameters,
            ProtocolJsonContext.Default.ThreadListParams, ProtocolJsonContext.Default.ThreadListResponse,
            cancellationToken);

    public Task<ThreadReadResponse> ThreadReadAsync(string threadId, bool includeTurns, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/read", new ThreadReadParams { ThreadId = threadId, IncludeTurns = includeTurns },
            ProtocolJsonContext.Default.ThreadReadParams, ProtocolJsonContext.Default.ThreadReadResponse,
            cancellationToken);

    public Task<ThreadArchiveResponse> ThreadArchiveAsync(string threadId, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/archive", new ThreadArchiveParams { ThreadId = threadId },
            ProtocolJsonContext.Default.ThreadArchiveParams, ProtocolJsonContext.Default.ThreadArchiveResponse,
            cancellationToken);

    public Task<ThreadUnarchiveResponse> ThreadUnarchiveAsync(string threadId, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/unarchive", new ThreadUnarchiveParams { ThreadId = threadId },
            ProtocolJsonContext.Default.ThreadUnarchiveParams, ProtocolJsonContext.Default.ThreadUnarchiveResponse,
            cancellationToken);

    public Task<ThreadSetNameResponse> ThreadSetNameAsync(string threadId, string name, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/name/set", new ThreadSetNameParams { ThreadId = threadId, Name = name },
            ProtocolJsonContext.Default.ThreadSetNameParams, ProtocolJsonContext.Default.ThreadSetNameResponse,
            cancellationToken);

    public Task<ThreadCompactStartResponse> ThreadCompactAsync(string threadId, CancellationToken cancellationToken) =>
        RequestAsync(
            "thread/compact/start", new ThreadCompactStartParams { ThreadId = threadId },
            ProtocolJsonContext.Default.ThreadCompactStartParams, ProtocolJsonContext.Default.ThreadCompactStartResponse,
            cancellationToken);

    public Task<ModelListResponse> ModelListAsync(bool includeHidden, CancellationToken cancellationToken) =>
        RequestAsync(
            "model/list", new ModelListParams { IncludeHidden = includeHidden },
            ProtocolJsonContext.Default.ModelListParams, ProtocolJsonContext.Default.ModelListResponse,
            cancellationToken);

    // ---------------------------------------------------------------- turn

    /// <summary>
    /// 開始一輪（或加入進行中的一輪），並盡早建立事件訂閱。對應 Python 的 <c>_start_turn</c>。
    /// 同一個 thread 的 turn/start 依序送出：送出到拿到回應之間先暫存這個 thread 的事件，訂閱才不會漏掉開頭。
    /// </summary>
    public async Task<(TurnStartResponse Response, TurnSubscription Subscription)> StartTurnAsync(
        TurnStartParams parameters, CancellationToken cancellationToken)
    {
        var threadId = parameters.ThreadId;
        var threadLock = AcquireThreadLock(threadId);
        try
        {
            await threadLock.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cursors = Router.BeginPendingTurn(threadId);
                try
                {
                    var started = await RequestAsync(
                        "turn/start", parameters,
                        ProtocolJsonContext.Default.TurnStartParams, ProtocolJsonContext.Default.TurnStartResponse,
                        cancellationToken).ConfigureAwait(false);
                    return (started, Router.PrepareTurn(started.Turn.Id, threadId, cursors));
                }
                finally
                {
                    Router.EndPendingTurn(threadId);
                }
            }
            finally
            {
                threadLock.Semaphore.Release();
            }
        }
        finally
        {
            ReleaseThreadLock(threadId, threadLock);
        }
    }

    public Task<TurnInterruptResponse> TurnInterruptAsync(string threadId, string turnId, CancellationToken cancellationToken) =>
        RequestAsync(
            "turn/interrupt", new TurnInterruptParams { ThreadId = threadId, TurnId = turnId },
            ProtocolJsonContext.Default.TurnInterruptParams, ProtocolJsonContext.Default.TurnInterruptResponse,
            cancellationToken);

    public Task<TurnSteerResponse> TurnSteerAsync(
        string threadId, string expectedTurnId, IReadOnlyList<UserInput> input, CancellationToken cancellationToken) =>
        RequestAsync(
            "turn/steer", new TurnSteerParams { ThreadId = threadId, ExpectedTurnId = expectedTurnId, Input = input },
            ProtocolJsonContext.Default.TurnSteerParams, ProtocolJsonContext.Default.TurnSteerResponse,
            cancellationToken);

    // ---------------------------------------------------------------- 收尾

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        Router.FailAll(new TransportClosedException("Codex 連線已經關閉。"));
        _shutdown.Dispose();
    }

    // ---------------------------------------------------------------- 內部

    private static TResult ParseResult<TResult>(string method, JsonElement result, JsonTypeInfo<TResult> resultInfo)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new CodexAgentException($"{method} 的回應必須是 JSON 物件。");
        }

        try
        {
            return result.Deserialize(resultInfo) ?? throw new CodexAgentException($"{method} 的回應是 null。");
        }
        catch (JsonException e)
        {
            throw new CodexAgentException($"{method} 的回應跟 schema 不符：{e.Message}", e);
        }
    }

    /// <summary>
    /// 較新的欄位要確認 CLI 支援才送：舊版 app-server 會默默忽略它們。沒有版本號的開發版改用產生 schema 的方式檢查。
    /// 對應 Python <c>CodexClient.request</c> 開頭的檢查。
    /// </summary>
    private async Task CheckRuntimeFieldsAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (!RuntimeRequirements.GuardedFields.TryGetValue(method, out var fields)
            || parameters is not { ValueKind: JsonValueKind.Object } value)
        {
            return;
        }

        var supplied = fields
            .Where(field => value.TryGetProperty(field, out var item) && item.ValueKind != JsonValueKind.Null)
            .ToList();
        if (supplied.Count == 0)
        {
            return;
        }

        try
        {
            if (RuntimeVersion == "0.0.0")
            {
                if (_capabilities is null)
                {
                    throw new ArgumentException("無法檢查這個開發版 CLI 支援哪些欄位");
                }

                var supported = (await _capabilities.GetFieldsAsync(cancellationToken).ConfigureAwait(false))[method];
                var missing = supplied.Where(field => !supported.Contains(field)).Order(StringComparer.Ordinal).ToList();
                if (missing.Count > 0)
                {
                    throw new ArgumentException($"這個開發版 CLI 不支援 {string.Join(", ", missing)}");
                }
            }
            else
            {
                RuntimeRequirements.RequireRuntimeVersion(RuntimeVersion);
            }
        }
        catch (ArgumentException e)
        {
            throw new CodexAgentException(
                $"{method} 帶了 {string.Join(", ", supplied)}：{e.Message}。請把 CodexAgentOptions.CliPath 指到支援的 Codex CLI。", e);
        }
    }

    private async Task WriteAsync(Action<Utf8JsonWriter> writeProperties, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        await _transport.WriteMessageAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>唯一讀取 stdout 的迴圈：把每則訊息分成 server 請求、通知、回應。對應 Python 的 <c>_reader_loop</c>。</summary>
    private async Task ReadLoopAsync()
    {
        Exception failure;
        try
        {
            await foreach (var message in _transport.ReadMessagesAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Dispatch(message);
            }

            failure = new TransportClosedException("Codex 連線已經關閉。");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            failure = new TransportClosedException("Codex 連線已經關閉。");
        }
#pragma warning disable CA1031 // 讀取迴圈不管為什麼結束，都要叫醒所有等待者，不能讓呼叫端永遠卡住
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = e is CodexAgentException ? e : new TransportClosedException($"讀取 Codex 的輸出失敗：{e.Message}", e);
        }

        Router.FailAll(failure);
        CancelServerRequests();
    }

    private void Dispatch(JsonElement message)
    {
        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        if (!hasMethod)
        {
            Router.RouteResponse(message);
            return;
        }

        var method = methodElement.GetString()!;
        var parameters = message.TryGetProperty("params", out var value) ? value : EmptyObject;
        if (hasId)
        {
            HandleServerRequest(method, id, parameters);
            return;
        }

        var notification = new Notification(method, ParseNotification(method, parameters)) { Raw = parameters };
        if (notification.Payload is ServerRequestResolvedNotification resolved)
        {
            CancelServerRequest(RequestKey(resolved.RequestId.Raw));
        }

        Router.RouteNotification(notification);
    }

    /// <summary>解析通知；不認得的方法或跟 schema 不符的內容變成 <see cref="UnknownNotification"/>。</summary>
    internal static object ParseNotification(string method, JsonElement parameters)
    {
        try
        {
            return ProtocolRegistry.DeserializeNotification(method, parameters) ?? new UnknownNotification(parameters);
        }
        catch (JsonException)
        {
            return new UnknownNotification(parameters);
        }
    }

    private void HandleServerRequest(string method, JsonElement id, JsonElement parameters)
    {
        var key = RequestKey(id);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_serverRequestsGate)
        {
            _serverRequests[key] = cancellation;
        }

        _ = Task.Run(() => RespondAsync(method, id, parameters, key, cancellation), CancellationToken.None);
    }

    private async Task RespondAsync(string method, JsonElement id, JsonElement parameters, string key, CancellationTokenSource cancellation)
    {
        try
        {
            object? typed = null;
            try
            {
                typed = ProtocolRegistry.DeserializeServerRequest(method, parameters);
            }
            catch (JsonException)
            {
                // 跟 schema 不符：handler 仍可從 Raw 讀
            }

            var handler = _options.ServerRequestHandler ?? DefaultServerRequestHandler;
            var result = await handler(new ServerRequest(method, id, typed, parameters), cancellation.Token).ConfigureAwait(false);
            // 處理期間被撤回或連線關閉：不再回覆
            cancellation.Token.ThrowIfCancellationRequested();
            var resultElement = ToResultElement(result);
            await WriteAsync(
                writer =>
                {
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WritePropertyName("result");
                    resultElement.WriteTo(writer);
                },
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (TransportClosedException)
        {
        }
#pragma warning disable CA1031 // handler 的任何錯誤都以 JSON-RPC 錯誤回覆，不能讓 app-server 一直等
        catch (Exception e)
#pragma warning restore CA1031
        {
            await TryWriteErrorAsync(id, e.Message).ConfigureAwait(false);
        }
        finally
        {
            lock (_serverRequestsGate)
            {
                if (_serverRequests.TryGetValue(key, out var current) && current == cancellation)
                {
                    _serverRequests.Remove(key);
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task TryWriteErrorAsync(JsonElement id, string message)
    {
        try
        {
            await WriteAsync(
                writer =>
                {
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    writer.WriteStartObject("error");
                    writer.WriteNumber("code", -32603);
                    writer.WriteString("message", message);
                    writer.WriteEndObject();
                },
                _shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CodexAgentException or OperationCanceledException or ObjectDisposedException)
        {
            // 連線已經斷了，回不了
        }
    }

    /// <summary>沒給 handler 時跟 Python SDK 一樣：指令與檔案變更的核准一律 accept，其他請求回覆空物件。</summary>
    private static Task<object?> DefaultServerRequestHandler(ServerRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<object?>(request.Method switch
        {
            ServerRequestMethods.ItemCommandExecutionRequestApproval =>
                new CommandExecutionRequestApprovalResponse { Decision = CommandExecutionApprovalDecision.Accept },
            ServerRequestMethods.ItemFileChangeRequestApproval =>
                new FileChangeRequestApprovalResponse { Decision = FileChangeApprovalDecision.Accept },
            _ => null,
        });

    private static JsonElement ToResultElement(object? result) => result switch
    {
        null => EmptyObject,
        JsonElement element => element,
        JsonNode node => JsonSerializer.SerializeToElement(node, ProtocolJsonContext.Default.JsonNode),
        _ => JsonSerializer.SerializeToElement(
            result,
            ProtocolJsonContext.Default.GetTypeInfo(result.GetType())
                ?? throw new CodexAgentException($"無法序列化 {result.GetType()}：請回傳協定型別、JsonNode 或 JsonElement。")),
    };

    private static string RequestKey(JsonElement id) =>
        id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetRawText();

    private void CancelServerRequest(string key)
    {
        lock (_serverRequestsGate)
        {
            if (_serverRequests.TryGetValue(key, out var cancellation))
            {
                cancellation.Cancel();
            }
        }
    }

    private void CancelServerRequests()
    {
        lock (_serverRequestsGate)
        {
            foreach (var cancellation in _serverRequests.Values)
            {
                cancellation.Cancel();
            }
        }
    }

    private ThreadLock AcquireThreadLock(string threadId)
    {
        lock (_threadLocksGate)
        {
            if (!_threadLocks.TryGetValue(threadId, out var threadLock))
            {
                _threadLocks[threadId] = threadLock = new ThreadLock();
            }

            threadLock.Users++;
            return threadLock;
        }
    }

    private void ReleaseThreadLock(string threadId, ThreadLock threadLock)
    {
        lock (_threadLocksGate)
        {
            threadLock.Users--;
            if (threadLock.Users == 0)
            {
                _threadLocks.Remove(threadId);
                threadLock.Semaphore.Dispose();
            }
        }
    }

    /// <summary>同一個 thread 的 turn/start 依序送出。對應 Python 的 <c>_ThreadStartLock</c>。</summary>
    private sealed class ThreadLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }
}
