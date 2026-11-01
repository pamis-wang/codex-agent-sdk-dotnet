using System.Text.Json;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// app-server 送來的通知。對應 Python SDK 的 <c>Notification</c>。
/// 用 <see cref="Payload"/> 的型別區分，例如 <see cref="Protocol.AgentMessageDeltaNotification"/>、<see cref="Protocol.TurnCompletedNotification"/>；
/// 方法名稱的常數在 <see cref="Protocol.ServerNotificationMethods"/>。
/// </summary>
/// <param name="Method">通知的方法，例如 <c>item/agentMessage/delta</c>。</param>
/// <param name="Payload">解析好的內容；這個 SDK 不認得的方法，或內容跟 schema 不符時是 <see cref="UnknownNotification"/>。</param>
public sealed record Notification(string Method, object Payload)
{
    /// <summary>原始的 <c>params</c> JSON；認得的通知也保留，沒有對應屬性的欄位從這裡讀。</summary>
    public JsonElement Raw { get; init; }
}

/// <summary>這個 SDK 不認得的通知，或內容跟 schema 不符的通知。對應 Python SDK 的 <c>UnknownNotification</c>。</summary>
/// <param name="Raw">原始的 <c>params</c> JSON。</param>
public sealed record UnknownNotification(JsonElement Raw);
