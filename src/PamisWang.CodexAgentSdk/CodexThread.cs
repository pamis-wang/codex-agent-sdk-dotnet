using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>一段對話，可以接連執行好幾輪。對應 Python SDK 的 <c>AsyncThread</c>。</summary>
public sealed class CodexThread
{
    // 取消 RunAsync 時順手中斷 turn，最多等這麼久
    private static readonly TimeSpan InterruptOnCancelTimeout = TimeSpan.FromSeconds(5);

    private readonly CodexAgentClient _client;

    internal CodexThread(CodexAgentClient client, string id)
    {
        _client = client;
        Id = id;
    }

    /// <summary>thread ID；之後可以用 <see cref="CodexAgentClient.ResumeThreadAsync"/> 接續。</summary>
    public string Id { get; }

    /// <summary>
    /// 執行一輪並等它完成，回傳收集的結果。對應 Python 的 <c>run()</c>。這一輪失敗時丟 <see cref="TurnFailedException"/>。
    /// 取消 <paramref name="cancellationToken"/> 時會先嘗試中斷這一輪，再丟出 <see cref="OperationCanceledException"/>。
    /// </summary>
    /// <param name="input">輸入：字串、<see cref="InputItem"/>、多項輸入，或 <see cref="ExternalMessage"/>。</param>
    /// <param name="options">這一輪的選項。</param>
    /// <param name="cancellationToken">取消等待，並中斷這一輪。</param>
    public async Task<TurnResult> RunAsync(RunInput input, TurnOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var turn = await StartTurnAsync(input, options, cancellationToken).ConfigureAwait(false);
        try
        {
            return await turn.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryInterruptAsync(turn).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 開始一輪，或加入進行中的一般 turn，回傳它的 <see cref="TurnHandle"/>。對應 Python 的 <c>turn()</c>。
    /// 需要逐段讀取、追加指示或中斷時用這個；只要結果的話用 <see cref="RunAsync"/>。
    /// </summary>
    /// <param name="input">輸入：字串、<see cref="InputItem"/>、多項輸入，或 <see cref="ExternalMessage"/>。</param>
    /// <param name="options">這一輪的選項。</param>
    /// <param name="cancellationToken">取消開始這一輪的請求。</param>
    public async Task<TurnHandle> StartTurnAsync(RunInput input, TurnOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (wireInput, toolOutput) = ProtocolMapping.ToTurnInput(input);
        var connection = await _client.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var (approvalPolicy, approvalsReviewer) = ProtocolMapping.ToApprovalSettings(options?.ApprovalMode);
        var parameters = new TurnStartParams
        {
            ThreadId = Id,
            Input = wireInput,
            ToolOutput = toolOutput,
            ApprovalPolicy = approvalPolicy,
            ApprovalsReviewer = approvalsReviewer,
            Cwd = options?.WorkingDirectory,
            Effort = options?.Effort,
            Model = options?.Model,
            OutputSchema = options?.OutputSchema,
            Personality = options?.Personality,
            SandboxPolicy = ProtocolMapping.ToSandboxPolicy(options?.Sandbox),
            ServiceTier = options?.ServiceTier,
            TurnTrigger = options?.Source,
            Summary = options?.Summary,
            ServiceTierForTurn = options?.TurnServiceTier,
        };
        var (started, subscription) = await connection.StartTurnAsync(parameters, cancellationToken).ConfigureAwait(false);
        return new TurnHandle(connection, Id, started.Turn.Id, subscription);
    }

    /// <summary>讀取這個 thread；<paramref name="includeTurns"/> 為 <see langword="true"/> 時含每一輪的歷史。</summary>
    public async Task<ThreadReadResponse> ReadAsync(bool includeTurns = false, CancellationToken cancellationToken = default)
    {
        var connection = await _client.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ThreadReadAsync(Id, includeTurns, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>設定 thread 的名稱。</summary>
    public async Task<ThreadSetNameResponse> SetNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var connection = await _client.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ThreadSetNameAsync(Id, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>壓縮對話的上下文；完成時會收到 <c>thread/compacted</c>。</summary>
    public async Task<ThreadCompactStartResponse> CompactAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _client.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ThreadCompactAsync(Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryInterruptAsync(TurnHandle turn)
    {
        using var timeout = new CancellationTokenSource(InterruptOnCancelTimeout);
        try
        {
            await turn.InterruptAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is CodexAgentException or OperationCanceledException)
        {
            // 這一輪可能剛好結束，或連線已經斷了；取消本身才是呼叫端要的結果
        }
    }
}
