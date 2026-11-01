using System.Text.Json.Nodes;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>開新 thread 的選項。對應 Python SDK <c>thread_start()</c> 的參數；不給的值用 Codex 的設定。</summary>
public sealed record ThreadStartOptions
{
    /// <summary>核准提升權限請求的方式；預設 <see cref="ApprovalMode.AutoReview"/>。</summary>
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.AutoReview;

    /// <summary>取代模型的基礎指示。</summary>
    public string? BaseInstructions { get; init; }

    /// <summary>附加的開發者指示。</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>這個 thread 的設定覆寫，鍵與值跟 <c>config.toml</c> 一樣，例如 <c>{"model_reasoning_effort": "high"}</c>。</summary>
    public JsonObject? Config { get; init; }

    /// <summary>thread 的工作目錄。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>臨時 thread，不存檔。</summary>
    public bool? Ephemeral { get; init; }

    /// <summary>模型，例如 <c>gpt-5.4</c>；可用的模型見 <see cref="CodexAgentClient.ListModelsAsync"/>。</summary>
    public string? Model { get; init; }

    /// <summary>模型供應者。</summary>
    public string? ModelProvider { get; init; }

    /// <summary>已淘汰：<see cref="Personality.Friendly"/> 與 <see cref="Personality.Pragmatic"/> 不再改變語氣；<see cref="Personality.None"/> 會拿掉模型指示裡的 <c># Personality</c> 段落。</summary>
    public Personality? Personality { get; init; }

    /// <summary>檔案系統存取等級。</summary>
    public Sandbox? Sandbox { get; init; }

    /// <summary>服務名稱。</summary>
    public string? ServiceName { get; init; }

    /// <summary>服務等級，例如速度模式。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>開啟這個 session 的來源。</summary>
    public ThreadStartSource? SessionStartSource { get; init; }

    /// <summary>建立 thread 時記錄的來源分類。</summary>
    public string? ThreadSource { get; init; }
}
