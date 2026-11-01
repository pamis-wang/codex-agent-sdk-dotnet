namespace PamisWang.CodexAgentSdk;

/// <summary>這個套件所有例外的基底。對應 Python SDK 的 <c>CodexError</c>。</summary>
public class CodexAgentException : Exception
{
    /// <summary>建立例外。</summary>
    public CodexAgentException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public CodexAgentException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public CodexAgentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
