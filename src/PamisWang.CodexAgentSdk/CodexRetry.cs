using System.Text.Json;

namespace PamisWang.CodexAgentSdk;

/// <summary>server 過載時的重試工具。對應 Python SDK 的 <c>retry_on_overload</c> 與 <c>is_retryable_error</c>。</summary>
public static class CodexRetry
{
    /// <summary>
    /// 執行 <paramref name="operation"/>；遇到過載這類暫時性錯誤（<see cref="IsRetryableError"/>）就以指數退避加抖動重試，
    /// 其他例外直接丟出。
    /// </summary>
    /// <param name="operation">要執行的操作。</param>
    /// <param name="maxAttempts">最多嘗試幾次，含第一次。</param>
    /// <param name="initialDelay">第一次重試前等多久；預設 0.25 秒。</param>
    /// <param name="maxDelay">每次等待的上限；預設 2 秒。</param>
    /// <param name="jitterRatio">等待時間上下浮動的比例。</param>
    /// <param name="cancellationToken">取消等待與操作。</param>
    public static async Task<T> RetryOnOverloadAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double jitterRatio = 0.2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var delay = initialDelay ?? TimeSpan.FromSeconds(0.25);
        var ceiling = maxDelay ?? TimeSpan.FromSeconds(2);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < maxAttempts && IsRetryableError(e))
            {
                var jitter = delay.TotalSeconds * jitterRatio;
#pragma warning disable CA5394 // 抖動只用來錯開重試時間，不需要密碼學等級的亂數
                var seconds = Math.Min(ceiling.TotalSeconds, delay.TotalSeconds) + ((Random.Shared.NextDouble() * 2) - 1) * jitter;
#pragma warning restore CA5394
                if (seconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
                }

                delay = TimeSpan.FromSeconds(Math.Min(ceiling.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>例外是過載這類暫時性錯誤，可以重試。</summary>
    public static bool IsRetryableError(Exception exception) => exception switch
    {
        ServerBusyException => true,
        JsonRpcException rpc => JsonRpcErrors.IsServerOverloaded(rpc.ErrorData),
        _ => false,
    };
}

/// <summary>把 JSON-RPC 錯誤對應成型別化的例外。對應 Python SDK 的 <c>map_jsonrpc_error</c>。</summary>
internal static class JsonRpcErrors
{
    public static JsonRpcException Map(int code, string message, JsonElement? data) => code switch
    {
        -32700 => new JsonRpcParseException(code, message, data),
        -32600 => new InvalidRequestException(code, message, data),
        -32601 => new MethodNotFoundException(code, message, data),
        -32602 => new InvalidParamsException(code, message, data),
        -32603 => new InternalRpcException(code, message, data),
        >= -32099 and <= -32000 when IsServerOverloaded(data) && ContainsRetryLimitText(message) =>
            new RetryLimitExceededException(code, message, data),
        >= -32099 and <= -32000 when IsServerOverloaded(data) => new ServerBusyException(code, message, data),
        >= -32099 and <= -32000 when ContainsRetryLimitText(message) => new RetryLimitExceededException(code, message, data),
        >= -32099 and <= -32000 => new CodexRpcException(code, message, data),
        _ => new JsonRpcException(code, message, data),
    };

    /// <summary>
    /// 錯誤資料裡有 <c>server_overloaded</c>。Python SDK 只比對蛇形的寫法；v2 協定的 <c>codexErrorInfo</c> 是駝峰的
    /// <c>serverOverloaded</c>，這裡忽略底線一併認得。
    /// </summary>
    public static bool IsServerOverloaded(JsonElement? data)
    {
        if (data is not { } element)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return IsOverloadedText(element.GetString());
            case JsonValueKind.Object:
                foreach (var name in (ReadOnlySpan<string>)["codex_error_info", "codexErrorInfo", "errorInfo"])
                {
                    if (!element.TryGetProperty(name, out var direct))
                    {
                        continue;
                    }

                    if (direct.ValueKind == JsonValueKind.String && IsOverloadedText(direct.GetString()))
                    {
                        return true;
                    }

                    if (direct.ValueKind == JsonValueKind.Object
                        && direct.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && IsOverloadedText(p.Value.GetString())))
                    {
                        return true;
                    }
                }

                return element.EnumerateObject().Any(p => IsServerOverloaded(p.Value));
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item => IsServerOverloaded(item));
            default:
                return false;
        }
    }

    private static bool IsOverloadedText(string? text) =>
        text is not null && string.Equals(text.Replace("_", "", StringComparison.Ordinal), "serveroverloaded", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsRetryLimitText(string message) =>
        message.Contains("retry limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too many failed attempts", StringComparison.OrdinalIgnoreCase);
}
