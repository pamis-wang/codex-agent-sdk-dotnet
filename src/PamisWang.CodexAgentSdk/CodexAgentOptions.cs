namespace PamisWang.CodexAgentSdk;

/// <summary>啟動與識別本機 Codex 的設定。對應 Python SDK 的 <c>CodexConfig</c>；大多數情況不用設定任何值。</summary>
public sealed class CodexAgentOptions
{
    /// <summary>
    /// <c>codex</c> 執行檔的路徑。不給就從 PATH 與常見的安裝位置找。
    /// Python SDK 預設用套件附帶的 runtime；這個套件不附帶，要自己安裝 Codex CLI。
    /// </summary>
    public string? CliPath { get; init; }

    /// <summary>
    /// 每一項以 <c>--config key=value</c> 傳給 <c>codex</c>，覆寫 <c>~/.codex/config.toml</c> 的設定，例如 <c>model_reasoning_effort="high"</c>。
    /// 值的部分以 TOML 解析。
    /// </summary>
    public IReadOnlyList<string> ConfigOverrides { get; init; } = [];

    /// <summary>app-server 程序的工作目錄。不給就用目前程序的。thread 自己的工作目錄另外在 <see cref="ThreadStartOptions.WorkingDirectory"/> 指定。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>額外的環境變數，合併進目前程序的環境變數後傳給 <c>codex</c>。</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>initialize 時回報的 client 名稱。</summary>
    public string ClientName { get; init; } = SdkInfo.ClientName;

    /// <summary>initialize 時回報的 client 標題。</summary>
    public string ClientTitle { get; init; } = SdkInfo.ClientTitle;

    /// <summary>initialize 時回報的 client 版本；預設是這個套件的版本。</summary>
    public string ClientVersion { get; init; } = SdkInfo.Version;

    /// <summary>
    /// initialize 時是否宣告支援 experimental API。跟 Python SDK 一樣預設開啟：較新的參數（例如 <see cref="TurnOptions.Source"/>）需要它。
    /// </summary>
    public bool ExperimentalApi { get; init; } = true;

    /// <summary>
    /// 處理 app-server 送來、要由 client 回覆的請求，例如執行指令前的核准（<see cref="Protocol.ServerRequestMethods.ItemCommandExecutionRequestApproval"/>）。
    /// 回傳值會序列化成回覆的 <c>result</c>：可以是協定型別（例如 <see cref="Protocol.CommandExecutionRequestApprovalResponse"/>）、
    /// <see cref="System.Text.Json.Nodes.JsonNode"/>、<see cref="System.Text.Json.JsonElement"/>，<see langword="null"/> 回覆空物件。
    /// 丟出例外就以 JSON-RPC 錯誤回覆。
    /// 請求被撤回（<c>serverRequest/resolved</c>）或連線關閉時，<see cref="CancellationToken"/> 會被取消，這時不回覆。
    /// <para>
    /// 不給的話跟 Python SDK 一樣：指令與檔案變更的核准一律 <c>accept</c>，其他請求回覆空物件。
    /// 預設的核准模式 <see cref="ApprovalMode.AutoReview"/> 由 Codex 自動審查；要讓核准請求都交給這個 handler，
    /// 開 thread 或 turn 時用 <see cref="ApprovalMode.User"/>。
    /// </para>
    /// </summary>
    public Func<ServerRequest, CancellationToken, Task<object?>>? ServerRequestHandler { get; init; }

    /// <summary>
    /// 收到 <c>codex</c> 的 stderr 時逐行呼叫，可以用來除錯或記錄。不給也會保留最後幾行，放進連線中斷時的例外訊息。
    /// </summary>
    public Action<string>? StandardErrorCallback { get; init; }

    /// <summary>
    /// 這個套件自己的警告，例如 Codex CLI 的版本低於支援的最低版本。只是提醒，不會中斷連線。
    /// 有指定時，連線時用 initialize 回報的版本檢查（不另外啟動程序）；環境變數 <c>CODEX_AGENT_SDK_SKIP_VERSION_CHECK</c> 設成任何值就不檢查。
    /// Python SDK 沒有這個選項，只在送出較新的欄位前檢查版本；這個套件兩者都做。
    /// </summary>
    public Action<string>? WarningCallback { get; init; }

    /// <summary>單一則訊息的大小上限（位元組）。讀取含完整歷史的 thread 時訊息可能很大；預設 64 MB。</summary>
    public int MaxBufferSize { get; init; } = 64 * 1024 * 1024;
}
