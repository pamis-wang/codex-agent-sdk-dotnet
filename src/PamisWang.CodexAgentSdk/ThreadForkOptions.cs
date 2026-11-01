using System.Text.Json.Nodes;

namespace PamisWang.CodexAgentSdk;

/// <summary>從既有 thread 分岔出新 thread 的選項。對應 Python SDK <c>thread_fork()</c> 的參數。</summary>
public sealed record ThreadForkOptions
{
    /// <summary>核准提升權限請求的方式。</summary>
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary>取代模型的基礎指示。</summary>
    public string? BaseInstructions { get; init; }

    /// <summary>附加的開發者指示。</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>新 thread 的設定覆寫。</summary>
    public JsonObject? Config { get; init; }

    /// <summary>新 thread 的工作目錄。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>新 thread 是臨時的，不存檔。</summary>
    public bool? Ephemeral { get; init; }

    /// <summary>app-server 的回應要不要載入 turn 歷史，見 <see cref="ThreadResumeOptions.IncludeTurns"/>。</summary>
    public bool? IncludeTurns { get; init; }

    /// <summary>模型。</summary>
    public string? Model { get; init; }

    /// <summary>模型供應者。</summary>
    public string? ModelProvider { get; init; }

    /// <summary>檔案系統存取等級。</summary>
    public Sandbox? Sandbox { get; init; }

    /// <summary>服務等級。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>新 thread 記錄的來源分類。</summary>
    public string? ThreadSource { get; init; }
}
