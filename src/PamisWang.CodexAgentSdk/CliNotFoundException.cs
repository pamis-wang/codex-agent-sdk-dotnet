namespace PamisWang.CodexAgentSdk;

/// <summary>找不到 Codex CLI，或指定的路徑不存在。對應 Python SDK 解析執行檔時的 <c>FileNotFoundError</c>。</summary>
public sealed class CliNotFoundException : CodexAgentException
{
    /// <summary>建立例外。</summary>
    public CliNotFoundException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CliNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CliNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
