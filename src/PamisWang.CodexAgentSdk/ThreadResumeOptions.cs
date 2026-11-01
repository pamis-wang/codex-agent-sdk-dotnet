using System.Text.Json.Nodes;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>接續既有 thread 的選項。對應 Python SDK <c>thread_resume()</c> 的參數；不給的值沿用 thread 原本的設定。</summary>
public sealed record ThreadResumeOptions
{
    /// <summary>核准提升權限請求的方式。</summary>
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary>取代模型的基礎指示。</summary>
    public string? BaseInstructions { get; init; }

    /// <summary>附加的開發者指示。</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>這個 thread 的設定覆寫。</summary>
    public JsonObject? Config { get; init; }

    /// <summary>thread 的工作目錄。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// app-server 的回應要不要載入 turn 歷史；只影響回應，不影響模型的上下文。不給就用 app-server 的預設。
    /// 要讀歷史請用 <see cref="CodexThread.ReadAsync"/>。需要 Codex CLI 0.151.0 以上。
    /// </summary>
    public bool? IncludeTurns { get; init; }

    /// <summary>模型。</summary>
    public string? Model { get; init; }

    /// <summary>模型供應者。</summary>
    public string? ModelProvider { get; init; }

    /// <summary>已淘汰，見 <see cref="ThreadStartOptions.Personality"/>。</summary>
    public Personality? Personality { get; init; }

    /// <summary>檔案系統存取等級。</summary>
    public Sandbox? Sandbox { get; init; }

    /// <summary>服務等級。</summary>
    public string? ServiceTier { get; init; }
}
