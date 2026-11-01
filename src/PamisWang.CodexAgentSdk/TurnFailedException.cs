using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// turn 以 <see cref="TurnStatus.Failed"/> 結束。<see cref="TurnHandle.RunAsync"/> 與 <see cref="CodexThread.RunAsync"/> 會丟出它；
/// Python SDK 丟的是 <c>RuntimeError</c>。
/// </summary>
public sealed class TurnFailedException : CodexAgentException
{
    /// <summary>建立例外。</summary>
    public TurnFailedException()
    {
    }

    /// <summary>以訊息建立例外。</summary>
    public TurnFailedException(string message)
        : base(message)
    {
    }

    /// <summary>以訊息與內部例外建立例外。</summary>
    public TurnFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>以失敗的 turn 建立例外；訊息取 turn 的錯誤說明。</summary>
    public TurnFailedException(Turn turn)
        : base(turn?.Error?.Message is { Length: > 0 } message ? message : $"turn failed with status {turn?.Status}")
    {
        Turn = turn;
    }

    /// <summary>失敗的 turn，含錯誤資訊 <see cref="Turn.Error"/>。</summary>
    public Turn? Turn { get; }
}
