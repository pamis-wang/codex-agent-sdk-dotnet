using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 來自其他 agent、工具或應用程式的<b>不受信任</b>內容。對應 Python SDK 的 <c>ExternalMessage</c>。
/// 內容以工具層級的權限交給模型，低於使用者與開發者指示，不代表使用者授權或核准。
/// 整個當成 <see cref="CodexThread.RunAsync"/> 或 <see cref="CodexThread.StartTurnAsync"/> 的輸入：thread 閒置時開始新的 turn，
/// 有進行中的一般 turn 時加入它。在歷史與通知裡是 <c>functionCallOutput</c> item。需要 Codex CLI 0.151.0 以上。
/// </summary>
public sealed record ExternalMessage
{
    /// <summary>以文字內容建立。</summary>
    /// <param name="toolName">送出這則訊息的工具或應用程式名稱，不能是空字串。</param>
    /// <param name="content">文字內容。</param>
    /// <param name="namespace">工具名稱的命名空間。</param>
    public ExternalMessage(string toolName, string content, string? @namespace = null)
        : this(toolName, FunctionCallOutputBody.FromString(content), @namespace)
    {
    }

    /// <summary>以結構化內容建立，例如文字加圖片；圖片要用內嵌的 data URL。</summary>
    /// <param name="toolName">送出這則訊息的工具或應用程式名稱，不能是空字串。</param>
    /// <param name="content">Responses API 相容的 function output 內容。</param>
    /// <param name="namespace">工具名稱的命名空間。</param>
    public ExternalMessage(string toolName, IReadOnlyList<FunctionCallOutputContentItem> content, string? @namespace = null)
        : this(toolName, FunctionCallOutputBody.From(content), @namespace)
    {
    }

    private ExternalMessage(string toolName, FunctionCallOutputBody content, string? @namespace)
    {
        ToolName = toolName;
        Content = content;
        Namespace = @namespace;
    }

    /// <summary>送出這則訊息的工具或應用程式名稱。只用來標示來源，不證明身分或授權。</summary>
    public string ToolName { get; }

    /// <summary>內容。</summary>
    public FunctionCallOutputBody Content { get; }

    /// <summary>工具名稱的命名空間。</summary>
    public string? Namespace { get; }
}
