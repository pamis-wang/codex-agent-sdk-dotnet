using System.Runtime.CompilerServices;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 已經開始的一輪對話：讀取事件、追加指示、中斷。對應 Python SDK 的 <c>AsyncTurnHandle</c>。
/// 用 <see cref="StreamAsync"/> 或 <see cref="RunAsync"/> 讀完它；不讀的話用 <see cref="Dispose"/> 釋放為它保留的事件。
/// </summary>
public sealed class TurnHandle : IDisposable
{
    private readonly AppServerConnection _connection;
    private readonly TurnSubscription _subscription;

    internal TurnHandle(AppServerConnection connection, string threadId, string id, TurnSubscription subscription)
    {
        _connection = connection;
        ThreadId = threadId;
        Id = id;
        _subscription = subscription;
    }

    /// <summary>所屬的 thread ID。</summary>
    public string ThreadId { get; }

    /// <summary>turn ID。</summary>
    public string Id { get; }

    /// <summary>對進行中的這一輪追加使用者輸入。不接受 <see cref="ExternalMessage"/>，外部訊息請用 <see cref="CodexThread.StartTurnAsync"/> 送。</summary>
    public Task<TurnSteerResponse> SteerAsync(RunInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Items is not { } items)
        {
            throw new ArgumentException("SteerAsync 不接受 ExternalMessage；請用 CodexThread.StartTurnAsync 送出外部訊息。", nameof(input));
        }

        return _connection.TurnSteerAsync(ThreadId, Id, ProtocolMapping.ToUserInputs(items), cancellationToken);
    }

    /// <summary>要求中斷這一輪；之後仍會收到這一輪的 <c>turn/completed</c>，狀態是 <see cref="TurnStatus.Interrupted"/>。</summary>
    public Task<TurnInterruptResponse> InterruptAsync(CancellationToken cancellationToken = default) =>
        _connection.TurnInterruptAsync(ThreadId, Id, cancellationToken);

    /// <summary>
    /// 依序讀取這一輪的通知，讀到這一輪的 <c>turn/completed</c> 為止，包含它。只能讀一次。
    /// 例如 <see cref="AgentMessageDeltaNotification"/> 是逐段的文字、<see cref="ItemCompletedNotification"/> 是完成的 item。
    /// 取消 <paramref name="cancellationToken"/> 只會停止讀取，這一輪仍在進行；要停止它請呼叫 <see cref="InterruptAsync"/>。
    /// </summary>
    public async IAsyncEnumerable<Notification> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                var notification = await _subscription.NextAsync(cancellationToken).ConfigureAwait(false);
                yield return notification;
                if (notification.Payload is TurnCompletedNotification completed && completed.Turn.Id == Id)
                {
                    yield break;
                }
            }
        }
        finally
        {
            _subscription.Dispose();
        }
    }

    /// <summary>讀完這一輪並回傳結果。這一輪失敗時丟 <see cref="TurnFailedException"/>。</summary>
    public Task<TurnResult> RunAsync(CancellationToken cancellationToken = default) =>
        TurnResult.CollectAsync(StreamAsync(cancellationToken), Id, cancellationToken);

    /// <summary>不再讀取這一輪的事件。不會中斷這一輪。</summary>
    public void Dispose() => _subscription.Dispose();
}
