using PamisWang.CodexAgentSdk.Protocol;
using PamisWang.CodexAgentSdk.Transport;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 透過本機的 <c>codex app-server</c> 建立 thread、執行 turn。對應 Python SDK 的 <c>AsyncCodex</c>（這個套件只有非同步 API）。
/// 第一次呼叫 API 時才啟動 app-server 並完成初始化；要提早啟動就呼叫 <see cref="ConnectAsync"/>。
/// 預設沿用 <c>codex login</c> 存下的登入狀態，不需要 API Key。
/// </summary>
/// <example>
/// <code>
/// await using var codex = new CodexAgentClient();
/// var thread = await codex.StartThreadAsync(new ThreadStartOptions { Sandbox = Sandbox.WorkspaceWrite });
/// var result = await thread.RunAsync("用三點說明這個專案。");
/// Console.WriteLine(result.FinalResponse);
/// </code>
/// </example>
public sealed class CodexAgentClient : IAsyncDisposable
{
    private readonly CodexAgentOptions _options;
    private readonly Func<ITransport> _transportFactory;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private AppServerConnection? _connection;
    private InitializeResponse? _metadata;
    private CodexServerInfo? _serverInfo;
    private bool _disposed;

    /// <summary>建立 client；第一次呼叫 API（或 <see cref="ConnectAsync"/>）時才啟動 Codex。</summary>
    public CodexAgentClient(CodexAgentOptions? options = null)
        : this(options ?? new CodexAgentOptions(), transportFactory: null)
    {
    }

    internal CodexAgentClient(CodexAgentOptions options, Func<ITransport>? transportFactory)
    {
        _options = options;
        _transportFactory = transportFactory ?? (() => new SubprocessTransport(options));
    }

    /// <summary>initialize 的回應，含 <c>userAgent</c>、<c>codexHome</c> 與平台。對應 Python 的 <c>metadata</c>。</summary>
    /// <exception cref="InvalidOperationException">還沒初始化。</exception>
    public InitializeResponse Metadata => _metadata ?? throw NotInitialized();

    /// <summary>從 <c>userAgent</c> 取出的伺服器名稱與版本。</summary>
    /// <exception cref="InvalidOperationException">還沒初始化。</exception>
    public CodexServerInfo ServerInfo => _serverInfo ?? throw NotInitialized();

    /// <summary>已經連線，而且還沒關閉。</summary>
    public bool IsConnected => Volatile.Read(ref _connection) is not null;

    /// <summary>啟動 app-server 並完成初始化握手；已經初始化就什麼都不做。失敗時會關掉已經啟動的程序，下次呼叫會重試。</summary>
    /// <exception cref="CliNotFoundException">找不到 Codex CLI。</exception>
    public Task ConnectAsync(CancellationToken cancellationToken = default) => EnsureInitializedAsync(cancellationToken);

    // ---------------------------------------------------------------- 帳號

    /// <summary>以 API Key 登入。</summary>
    public async Task LoginApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await connection.AccountLoginStartAsync(new ApiKeyLoginAccountParams { ApiKey = apiKey }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>開始 ChatGPT 瀏覽器登入；在瀏覽器開啟 <see cref="ChatgptLoginHandle.AuthUrl"/>，再等 <see cref="ChatgptLoginHandle.WaitAsync"/>。</summary>
    public async Task<ChatgptLoginHandle> LoginChatgptAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var response = await connection.AccountLoginStartAsync(new ChatgptLoginAccountParams(), cancellationToken).ConfigureAwait(false);
        return response is ChatgptLoginAccountResponse chatgpt
            ? new ChatgptLoginHandle(connection, chatgpt.LoginId, chatgpt.AuthUrl)
            : throw new CodexAgentException($"ChatGPT 登入的回應不符預期：{response.Type}");
    }

    /// <summary>開始 ChatGPT device code 登入；沒有瀏覽器的環境用這個。</summary>
    public async Task<DeviceCodeLoginHandle> LoginChatgptDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var response = await connection.AccountLoginStartAsync(new ChatgptDeviceCodeLoginAccountParams(), cancellationToken)
            .ConfigureAwait(false);
        return response is ChatgptDeviceCodeLoginAccountResponse deviceCode
            ? new DeviceCodeLoginHandle(connection, deviceCode.LoginId, deviceCode.VerificationUrl, deviceCode.UserCode)
            : throw new CodexAgentException($"device code 登入的回應不符預期：{response.Type}");
    }

    /// <summary>讀取目前的帳號狀態。</summary>
    /// <param name="refreshToken">先更新權杖再讀。</param>
    /// <param name="cancellationToken">取消請求。</param>
    public async Task<GetAccountResponse> GetAccountAsync(bool refreshToken = false, CancellationToken cancellationToken = default)
    {
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.AccountReadAsync(new GetAccountParams { RefreshToken = refreshToken }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>登出。</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await connection.AccountLogoutAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- thread

    /// <summary>開新 thread。對應 Python 的 <c>thread_start()</c>；核准模式預設 <see cref="ApprovalMode.AutoReview"/>。</summary>
    public async Task<CodexThread> StartThreadAsync(ThreadStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ThreadStartOptions();
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var (approvalPolicy, approvalsReviewer) = ProtocolMapping.ToApprovalSettings(options.ApprovalMode);
        var started = await connection.ThreadStartAsync(
            new ThreadStartParams
            {
                ApprovalPolicy = approvalPolicy,
                ApprovalsReviewer = approvalsReviewer,
                BaseInstructions = options.BaseInstructions,
                Config = options.Config,
                Cwd = options.WorkingDirectory,
                DeveloperInstructions = options.DeveloperInstructions,
                Ephemeral = options.Ephemeral,
                Model = options.Model,
                ModelProvider = options.ModelProvider,
                Personality = options.Personality,
                Sandbox = ProtocolMapping.ToSandboxMode(options.Sandbox),
                ServiceName = options.ServiceName,
                ServiceTier = options.ServiceTier,
                SessionStartSource = options.SessionStartSource,
                ThreadSource = options.ThreadSource,
            },
            cancellationToken).ConfigureAwait(false);
        return new CodexThread(this, started.Thread.Id);
    }

    /// <summary>列出已儲存的 thread。對應 Python 的 <c>thread_list()</c>。</summary>
    public async Task<ThreadListResponse> ListThreadsAsync(ThreadListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ThreadListOptions();
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ThreadListAsync(
            new ThreadListParams
            {
                Archived = options.Archived,
                Cursor = options.Cursor,
                Cwd = options.WorkingDirectory,
                Limit = options.Limit,
                ModelProviders = options.ModelProviders,
                SearchTerm = options.SearchTerm,
                SectionId = options.SectionId,
                SortDirection = options.SortDirection,
                SortKey = options.SortKey,
                SourceKinds = options.SourceKinds,
                UseStateDbOnly = options.UseStateDbOnly,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>以 ID 接續既有的 thread。對應 Python 的 <c>thread_resume()</c>。</summary>
    public async Task<CodexThread> ResumeThreadAsync(string threadId, ThreadResumeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        options ??= new ThreadResumeOptions();
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var (approvalPolicy, approvalsReviewer) = ProtocolMapping.ToApprovalSettings(options.ApprovalMode);
        var resumed = await connection.ThreadResumeAsync(
            new ThreadResumeParams
            {
                ThreadId = threadId,
                ApprovalPolicy = approvalPolicy,
                ApprovalsReviewer = approvalsReviewer,
                BaseInstructions = options.BaseInstructions,
                Config = options.Config,
                Cwd = options.WorkingDirectory,
                DeveloperInstructions = options.DeveloperInstructions,
                ExcludeTurns = !options.IncludeTurns,
                Model = options.Model,
                ModelProvider = options.ModelProvider,
                Personality = options.Personality,
                Sandbox = ProtocolMapping.ToSandboxMode(options.Sandbox),
                ServiceTier = options.ServiceTier,
            },
            cancellationToken).ConfigureAwait(false);
        return new CodexThread(this, resumed.Thread.Id);
    }

    /// <summary>從既有的 thread 分岔出新的 thread。對應 Python 的 <c>thread_fork()</c>。</summary>
    public async Task<CodexThread> ForkThreadAsync(string threadId, ThreadForkOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        options ??= new ThreadForkOptions();
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var (approvalPolicy, approvalsReviewer) = ProtocolMapping.ToApprovalSettings(options.ApprovalMode);
        var forked = await connection.ThreadForkAsync(
            new ThreadForkParams
            {
                ThreadId = threadId,
                ApprovalPolicy = approvalPolicy,
                ApprovalsReviewer = approvalsReviewer,
                BaseInstructions = options.BaseInstructions,
                Config = options.Config,
                Cwd = options.WorkingDirectory,
                DeveloperInstructions = options.DeveloperInstructions,
                Ephemeral = options.Ephemeral,
                ExcludeTurns = !options.IncludeTurns,
                Model = options.Model,
                ModelProvider = options.ModelProvider,
                Sandbox = ProtocolMapping.ToSandboxMode(options.Sandbox),
                ServiceTier = options.ServiceTier,
                ThreadSource = options.ThreadSource,
            },
            cancellationToken).ConfigureAwait(false);
        return new CodexThread(this, forked.Thread.Id);
    }

    /// <summary>封存已儲存的 thread。</summary>
    public async Task<ThreadArchiveResponse> ArchiveThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ThreadArchiveAsync(threadId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>還原封存的 thread。</summary>
    public async Task<CodexThread> UnarchiveThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var unarchived = await connection.ThreadUnarchiveAsync(threadId, cancellationToken).ConfigureAwait(false);
        return new CodexThread(this, unarchived.Thread.Id);
    }

    /// <summary>
    /// 列出可用的模型，含每個模型支援的思考深度。對應 Python 的 <c>models()</c>。
    /// 已淘汰的 <c>supportsPersonality</c> 在目前的 app-server 一律是 <see langword="false"/>。
    /// </summary>
    public async Task<ModelListResponse> ListModelsAsync(bool includeHidden = false, CancellationToken cancellationToken = default)
    {
        var connection = await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ModelListAsync(includeHidden, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>關閉 app-server：關 stdin 讓它結束，逾時才強制結束；進行中的請求會收到 <see cref="TransportClosedException"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        AppServerConnection? connection;
        await _initializeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connection = _connection;
            _connection = null;
        }
        finally
        {
            _initializeLock.Release();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 第一次使用時啟動並初始化。對應 Python <c>AsyncCodex</c> 的 <c>_ensure_initialized</c>；
    /// 失敗時關掉程序並保持未初始化，下次呼叫會重試。
    /// </summary>
    internal async Task<AppServerConnection> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) is { } ready)
        {
            return ready;
        }

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is { } existing)
            {
                return existing;
            }

            var connection = new AppServerConnection(_options, _transportFactory());
            try
            {
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var (response, serverInfo) = await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);
                _metadata = response;
                _serverInfo = serverInfo;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    private static InvalidOperationException NotInitialized() =>
        new("CodexAgentClient 還沒初始化。請先呼叫 ConnectAsync，或等第一次呼叫 API 時自動初始化。");
}
