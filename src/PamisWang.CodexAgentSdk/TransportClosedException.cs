namespace PamisWang.CodexAgentSdk;

/// <summary>跟 app-server 的連線意外中斷，或已經關閉。對應 Python SDK 的 <c>TransportClosedError</c>。</summary>
public sealed class TransportClosedException : CodexAgentException
{
    /// <summary>建立例外。</summary>
    public TransportClosedException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public TransportClosedException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public TransportClosedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
