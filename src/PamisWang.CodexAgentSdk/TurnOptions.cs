using System.Text.Json.Nodes;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 一輪對話的選項。對應 Python SDK <c>run()</c> 與 <c>turn()</c> 的參數。
/// 除了 <see cref="TurnServiceTier"/> 與 <see cref="Source"/>，這裡的設定會套用到這一輪與之後的每一輪。
/// </summary>
public sealed record TurnOptions
{
    /// <summary>核准提升權限請求的方式。</summary>
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary>工作目錄。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>思考深度，例如 <c>low</c>、<c>medium</c>、<c>high</c>；可用的值見 <see cref="CodexAgentClient.ListModelsAsync"/>。</summary>
    public string? Effort { get; init; }

    /// <summary>模型。</summary>
    public string? Model { get; init; }

    /// <summary>要求最終回覆符合的 JSON schema；回覆的 JSON 在 <see cref="TurnResult.FinalResponse"/>。</summary>
    public JsonNode? OutputSchema { get; init; }

    /// <summary>已淘汰，見 <see cref="ThreadStartOptions.Personality"/>。</summary>
    public Personality? Personality { get; init; }

    /// <summary>檔案系統存取等級。</summary>
    public Sandbox? Sandbox { get; init; }

    /// <summary>thread 的服務等級，套用到這一輪與之後。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>
    /// 標示誰發起這一輪，例如 <c>review_ui</c>。只是中繼資料，不授予任何權限；加入進行中的 turn 時忽略。
    /// 需要 Codex CLI 0.151.0 以上。
    /// </summary>
    public string? Source { get; init; }

    /// <summary>推理摘要的詳細程度。</summary>
    public ReasoningSummary? Summary { get; init; }

    /// <summary>
    /// 只覆寫這一輪的服務等級，不改 thread 的預設；<c>default</c> 是標準速度。加入進行中的 turn 時忽略。
    /// 需要 Codex CLI 0.151.0 以上。
    /// </summary>
    public string? TurnServiceTier { get; init; }
}
