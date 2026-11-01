using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>一輪對話結束後收集的結果。對應 Python SDK 的 <c>TurnResult</c>。</summary>
/// <param name="Id">turn ID。</param>
/// <param name="Status">結束時的狀態。</param>
/// <param name="Error">失敗或中斷時的錯誤。</param>
/// <param name="StartedAt">開始時間（Unix 秒）。</param>
/// <param name="CompletedAt">結束時間（Unix 秒）。</param>
/// <param name="DurationMs">花了多少毫秒。</param>
/// <param name="FinalResponse">
/// 最終回覆：最後一則 <see cref="MessagePhase.FinalAnswer"/> 的 agent 訊息，沒有的話是最後一則沒標階段的 agent 訊息；都沒有時是 <see langword="null"/>。
/// </param>
/// <param name="Items">這一輪完成的每個 item，依完成順序。</param>
/// <param name="Usage">token 用量。</param>
public sealed record TurnResult(
    string Id,
    TurnStatus Status,
    TurnError? Error,
    long? StartedAt,
    long? CompletedAt,
    long? DurationMs,
    string? FinalResponse,
    IReadOnlyList<ThreadItem> Items,
    ThreadTokenUsage? Usage)
{
    /// <summary>讀完事件串流並收集結果；turn 失敗時丟 <see cref="TurnFailedException"/>。對應 Python 的 <c>_collect_turn_result</c>。</summary>
    internal static async Task<TurnResult> CollectAsync(IAsyncEnumerable<Notification> stream, string turnId, CancellationToken cancellationToken)
    {
        TurnCompletedNotification? completed = null;
        List<ThreadItem> items = [];
        ThreadTokenUsage? usage = null;

        await foreach (var notification in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (notification.Payload)
            {
                case ItemCompletedNotification item when item.TurnId == turnId:
                    items.Add(item.Item);
                    break;
                case ThreadTokenUsageUpdatedNotification tokens when tokens.TurnId == turnId:
                    usage = tokens.TokenUsage;
                    break;
                case TurnCompletedNotification done when done.Turn.Id == turnId:
                    completed = done;
                    break;
            }
        }

        if (completed is null)
        {
            throw new CodexAgentException("沒有收到 turn/completed 事件。");
        }

        var turn = completed.Turn;
        if (turn.Status == TurnStatus.Failed)
        {
            throw new TurnFailedException(turn);
        }

        return new TurnResult(
            turn.Id, turn.Status, turn.Error, turn.StartedAt, turn.CompletedAt, turn.DurationMs,
            FinalResponseFrom(items), items, usage);
    }

    private static string? FinalResponseFrom(List<ThreadItem> items)
    {
        string? lastUnknownPhase = null;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (items[index] is not AgentMessageThreadItem message)
            {
                continue;
            }

            if (message.Phase == MessagePhase.FinalAnswer)
            {
                return message.Text;
            }

            if (message.Phase is null && lastUnknownPhase is null)
            {
                lastUnknownPhase = message.Text;
            }
        }

        return lastUnknownPhase;
    }
}
