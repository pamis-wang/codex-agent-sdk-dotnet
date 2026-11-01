using System.Text.Json;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// app-server 送來、要由 client 回覆的請求，交給 <see cref="CodexAgentOptions.ServerRequestHandler"/> 處理。
/// 用 <see cref="Params"/> 的型別區分，例如 <see cref="Protocol.CommandExecutionRequestApprovalParams"/>。
/// </summary>
public sealed class ServerRequest
{
    internal ServerRequest(string method, JsonElement id, object? parameters, JsonElement raw)
    {
        Method = method;
        Id = id;
        Params = parameters;
        Raw = raw;
    }

    /// <summary>請求的方法，例如 <c>item/commandExecution/requestApproval</c>；常數在 <see cref="Protocol.ServerRequestMethods"/>。</summary>
    public string Method { get; }

    /// <summary>JSON-RPC 請求 ID，對應 <c>serverRequest/resolved</c> 通知裡的 <c>requestId</c>。</summary>
    public JsonElement Id { get; }

    /// <summary>
    /// 解析好的參數，例如 <see cref="Protocol.CommandExecutionRequestApprovalParams"/>。
    /// 這個 SDK 不認得的方法，或參數跟 schema 不符時是 <see langword="null"/>，改讀 <see cref="Raw"/>。
    /// </summary>
    public object? Params { get; }

    /// <summary>原始的參數 JSON。</summary>
    public JsonElement Raw { get; }
}
