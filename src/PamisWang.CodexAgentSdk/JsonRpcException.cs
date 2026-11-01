using System.Text.Json;

namespace PamisWang.CodexAgentSdk;

/// <summary>app-server 以 JSON-RPC 錯誤回覆請求。對應 Python SDK 的 <c>JsonRpcError</c>。</summary>
public class JsonRpcException : CodexAgentException
{
    /// <summary>建立例外。</summary>
    public JsonRpcException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public JsonRpcException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public JsonRpcException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public JsonRpcException(int code, string message, JsonElement? data)
        : base($"JSON-RPC error {code}: {message}")
    {
        Code = code;
        ErrorMessage = message;
        ErrorData = data;
    }

    /// <summary>JSON-RPC 錯誤代碼，例如 <c>-32600</c>。</summary>
    public int Code { get; }

    /// <summary>app-server 給的錯誤訊息（不含代碼）。</summary>
    public string ErrorMessage { get; } = "";

    /// <summary>錯誤的 <c>data</c>，app-server 常在這裡放結構化的原因，例如 <c>{"reason": "serverShuttingDown"}</c>。</summary>
    public JsonElement? ErrorData { get; }
}

/// <summary>JSON-RPC 失敗的型別化基底。對應 Python SDK 的 <c>CodexRpcError</c>。</summary>
public class CodexRpcException : JsonRpcException
{
    /// <summary>建立例外。</summary>
    public CodexRpcException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CodexRpcException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CodexRpcException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public CodexRpcException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>請求或回應無法解析（<c>-32700</c>）。對應 Python SDK 的 <c>ParseError</c>。</summary>
public sealed class JsonRpcParseException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public JsonRpcParseException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public JsonRpcParseException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public JsonRpcParseException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public JsonRpcParseException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>app-server 拒絕這個請求（<c>-32600</c>），例如 turn 已經不在進行中。對應 Python SDK 的 <c>InvalidRequestError</c>。</summary>
public sealed class InvalidRequestException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public InvalidRequestException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public InvalidRequestException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public InvalidRequestException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public InvalidRequestException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>app-server 沒有這個方法（<c>-32601</c>），多半是 Codex CLI 版本太舊。對應 Python SDK 的 <c>MethodNotFoundError</c>。</summary>
public sealed class MethodNotFoundException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public MethodNotFoundException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public MethodNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public MethodNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public MethodNotFoundException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>參數不正確（<c>-32602</c>）。對應 Python SDK 的 <c>InvalidParamsError</c>。</summary>
public sealed class InvalidParamsException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public InvalidParamsException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public InvalidParamsException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public InvalidParamsException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public InvalidParamsException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>app-server 內部錯誤（<c>-32603</c>）。對應 Python SDK 的 <c>InternalRpcError</c>。</summary>
public sealed class InternalRpcException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public InternalRpcException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public InternalRpcException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public InternalRpcException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public InternalRpcException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>server 過載，稍後重試可能成功。對應 Python SDK 的 <c>ServerBusyError</c>；可以搭配 <see cref="CodexRetry"/>。</summary>
public class ServerBusyException : CodexRpcException
{
    /// <summary>建立例外。</summary>
    public ServerBusyException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public ServerBusyException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public ServerBusyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public ServerBusyException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}

/// <summary>server 自己的重試次數已經用完。對應 Python SDK 的 <c>RetryLimitExceededError</c>。</summary>
public sealed class RetryLimitExceededException : ServerBusyException
{
    /// <summary>建立例外。</summary>
    public RetryLimitExceededException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public RetryLimitExceededException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public RetryLimitExceededException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以 JSON-RPC 錯誤的內容建立例外。</summary>
    public RetryLimitExceededException(int code, string message, JsonElement? data)
        : base(code, message, data)
    {
    }
}
