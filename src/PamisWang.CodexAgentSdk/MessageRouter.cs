using System.Text.Json;
using System.Threading.Channels;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 把讀取迴圈收到的訊息交給等待它的操作。對應 Python SDK 的 <c>MessageRouter</c>。
/// stdio 是單一的有序串流，只有讀取迴圈能讀 stdout；每個進行中的請求有自己的等待者，每個 turn 的消費者有自己的事件游標。
/// Python 版還有 goal 操作的路由，那只給內部使用，這裡沒有移植。
/// </summary>
internal sealed class MessageRouter
{
    // 不屬於任何 turn 的通知目前沒有公開的讀取 API，只留最近的一些，避免沒人讀時無限累積
    private const int GlobalNotificationCapacity = 1000;

    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _responseWaiters = [];
    private readonly Dictionary<string, Channel<object>> _loginNotifications = [];
    private readonly Dictionary<string, List<Notification>> _pendingLoginNotifications = [];
    private readonly Dictionary<string, TurnState> _turnStates = [];
    // 正在等 turn/start 回應的 thread；值是等待期間連線失敗的例外
    private readonly Dictionary<string, Exception?> _pendingTurnRequests = [];
    private readonly Channel<Notification> _globalNotifications = Channel.CreateBounded<Notification>(
        new BoundedChannelOptions(GlobalNotificationCapacity) { FullMode = BoundedChannelFullMode.DropOldest });

    private TaskCompletionSource _turnChanged = NewSignal();
    private Exception? _failure;

    internal Lock Gate { get; } = new();

    /// <summary>不屬於任何 turn 的通知，例如 <c>thread/started</c>、<c>account/updated</c>。</summary>
    internal ChannelReader<Notification> GlobalNotifications => _globalNotifications.Reader;

    /// <summary>登記一個 request ID 的回應等待者。連線已經失敗時直接帶著那個例外。</summary>
    public Task<JsonElement> CreateResponseWaiter(string requestId)
    {
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_failure is not null)
            {
                waiter.SetException(_failure);
            }
            else
            {
                _responseWaiters[requestId] = waiter;
            }
        }

        return waiter.Task;
    }

    /// <summary>請求沒送出去時移除等待者。</summary>
    public void DiscardResponseWaiter(string requestId)
    {
        lock (Gate)
        {
            _responseWaiters.Remove(requestId);
        }
    }

    /// <summary>把回應或錯誤交給對應的等待者。</summary>
    public void RouteResponse(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id))
        {
            return;
        }

        var key = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
        TaskCompletionSource<JsonElement>? waiter;
        lock (Gate)
        {
            if (!_responseWaiters.Remove(key, out waiter))
            {
                return;
            }
        }

        if (message.TryGetProperty("error", out var error))
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                waiter.SetException(new CodexAgentException("JSON-RPC 錯誤回應的格式不正確。"));
                return;
            }

            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : -32000;
            var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "unknown";
            JsonElement? data = error.TryGetProperty("data", out var d) ? d : null;
            waiter.SetException(JsonRpcErrors.Map(code, text, data));
            return;
        }

        waiter.SetResult(message.TryGetProperty("result", out var result) ? result : default);
    }

    /// <summary>把通知交給登入、turn 或全域的佇列。</summary>
    public void RouteNotification(Notification notification)
    {
        if (LoginId(notification) is { } loginId)
        {
            Channel<object>? channel;
            lock (Gate)
            {
                if (!_loginNotifications.TryGetValue(loginId, out channel))
                {
                    // 登入結束的通知可能比 account/login/start 的回應先到，先存起來
                    if (!_pendingLoginNotifications.TryGetValue(loginId, out var pending))
                    {
                        _pendingLoginNotifications[loginId] = pending = [];
                    }

                    pending.Add(notification);
                    return;
                }
            }

            channel.Writer.TryWrite(notification);
            return;
        }

        var turnId = TurnId(notification);
        if (turnId is null)
        {
            _globalNotifications.Writer.TryWrite(notification);
            return;
        }

        var threadId = ThreadId(notification);
        lock (Gate)
        {
            var state = GetOrAddTurn(turnId, threadId);
            state.ThreadId = threadId ?? state.ThreadId;
            state.Append(notification);
            if (notification.Method == ServerNotificationMethods.TurnCompleted)
            {
                state.Completed = true;
            }

            PruneTurnEvents(state);
            SignalTurnChanged();
        }
    }

    /// <summary>讀取迴圈結束時叫醒所有等待者，不讓任何呼叫卡在永遠不會來的回應。</summary>
    public void FailAll(Exception exception)
    {
        List<TaskCompletionSource<JsonElement>> waiters;
        List<Channel<object>> logins;
        lock (Gate)
        {
            _failure ??= exception;
            waiters = [.. _responseWaiters.Values];
            _responseWaiters.Clear();
            logins = [.. _loginNotifications.Values];
            _loginNotifications.Clear();
            _pendingLoginNotifications.Clear();
            foreach (var threadId in _pendingTurnRequests.Keys.ToList())
            {
                _pendingTurnRequests[threadId] = exception;
            }

            foreach (var state in _turnStates.Values.ToList())
            {
                state.Append(exception);
                state.Completed = true;
                PruneTurnEvents(state);
            }

            SignalTurnChanged();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(exception);
        }

        foreach (var login in logins)
        {
            login.Writer.TryWrite(exception);
        }

        _globalNotifications.Writer.TryComplete(exception);
    }

    // ---------------------------------------------------------------- 登入

    /// <summary>開始把這次登入的通知收進它自己的佇列；之前先到的通知一併放進去。</summary>
    public void RegisterLogin(string loginId)
    {
        lock (Gate)
        {
            if (_loginNotifications.ContainsKey(loginId))
            {
                return;
            }

            var channel = Channel.CreateUnbounded<object>();
            if (_failure is not null)
            {
                channel.Writer.TryWrite(_failure);
            }

            if (_pendingLoginNotifications.Remove(loginId, out var pending))
            {
                foreach (var notification in pending)
                {
                    channel.Writer.TryWrite(notification);
                }
            }

            _loginNotifications[loginId] = channel;
        }
    }

    /// <summary>不再收這次登入的通知。</summary>
    public void UnregisterLogin(string loginId)
    {
        lock (Gate)
        {
            _loginNotifications.Remove(loginId);
        }
    }

    /// <summary>等這次登入的下一則通知。</summary>
    public async Task<Notification> NextLoginNotificationAsync(string loginId, CancellationToken cancellationToken)
    {
        Channel<object>? channel;
        lock (Gate)
        {
            if (!_loginNotifications.TryGetValue(loginId, out channel))
            {
                throw new InvalidOperationException($"登入 {loginId} 沒有登記等待。");
            }
        }

        var item = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return item is Exception e ? throw Rethrow(e) : (Notification)item;
    }

    // ---------------------------------------------------------------- turn

    /// <summary>
    /// 從送出 <c>turn/start</c> 起暫存這個 thread 的事件，直到拿到 turn ID、建立訂閱為止；
    /// 回傳送出請求當下各個既有 turn 的事件位置。對應 Python 的 <c>pending_turn</c>。
    /// </summary>
    public Dictionary<string, int> BeginPendingTurn(string threadId)
    {
        lock (Gate)
        {
            _pendingTurnRequests[threadId] = _failure;
            return _turnStates.ToDictionary(pair => pair.Key, pair => pair.Value.NextEvent);
        }
    }

    /// <summary>結束暫存；沒有人訂閱的事件可以丟了。</summary>
    public void EndPendingTurn(string threadId)
    {
        lock (Gate)
        {
            _pendingTurnRequests.Remove(threadId);
            foreach (var state in _turnStates.Values.ToList())
            {
                if (state.ThreadId is null || state.ThreadId == threadId)
                {
                    PruneTurnEvents(state);
                }
            }
        }
    }

    /// <summary>
    /// 為剛開始（或剛加入）的 turn 建立訂閱。新的 turn 從第一個事件讀起；加入既有的 turn 從送出請求當下的位置讀起。
    /// 對應 Python 的 <c>prepare_turn</c>（<c>for_handle=True</c>）。
    /// </summary>
    public TurnSubscription PrepareTurn(string turnId, string threadId, Dictionary<string, int> cursors)
    {
        lock (Gate)
        {
            var state = GetOrAddTurn(turnId, threadId);
            state.ThreadId = threadId;
            if (!state.Completed && _pendingTurnRequests.GetValueOrDefault(threadId) is { } failure)
            {
                state.Append(failure);
                state.Completed = true;
            }

            return new TurnSubscription(this, state, cursors.GetValueOrDefault(turnId, 0));
        }
    }

    /// <summary>目前等待事件的訊號；有新事件或狀態改變時完成。呼叫端要持有 <see cref="Gate"/>。</summary>
    internal Task TurnChanged => _turnChanged.Task;

    internal void SignalTurnChanged()
    {
        _turnChanged.TrySetResult();
        _turnChanged = NewSignal();
    }

    /// <summary>丟掉所有訂閱者都讀過的事件；沒有訂閱者就移除整個 turn。呼叫端要持有 <see cref="Gate"/>。</summary>
    internal void PruneTurnEvents(TurnState state)
    {
        // 有 turn/start 還在等回應時先別丟：回應拿到的 turn ID 可能正是這個 turn
        if ((state.ThreadId is { } threadId && _pendingTurnRequests.ContainsKey(threadId))
            || (state.ThreadId is null && _pendingTurnRequests.Count > 0))
        {
            return;
        }

        var consumed = state.Subscribers.Count == 0 ? state.NextEvent : state.Subscribers.Values.Min();
        state.DiscardBefore(consumed);
        if (state.Subscribers.Count == 0 && _turnStates.TryGetValue(state.Id, out var current) && current == state)
        {
            _turnStates.Remove(state.Id);
        }
    }

    internal static Exception Rethrow(Exception exception) => exception switch
    {
        TransportClosedException closed => new TransportClosedException(closed.Message, closed),
        CodexAgentException codex => new CodexAgentException(codex.Message, codex),
        _ => new TransportClosedException(exception.Message, exception),
    };

    private TurnState GetOrAddTurn(string turnId, string? threadId)
    {
        if (!_turnStates.TryGetValue(turnId, out var state))
        {
            _turnStates[turnId] = state = new TurnState(turnId, threadId);
        }

        return state;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? LoginId(Notification notification)
    {
        if (notification.Method != ServerNotificationMethods.AccountLoginCompleted)
        {
            return null;
        }

        return notification.Payload switch
        {
            AccountLoginCompletedNotification completed => completed.LoginId,
            UnknownNotification unknown => StringProperty(unknown.Raw, "loginId"),
            _ => null,
        };
    }

    private static string? TurnId(Notification notification)
    {
        if (notification.Payload is not UnknownNotification unknown)
        {
            return ProtocolRegistry.NotificationTurnId(notification.Payload);
        }

        // 不認得的通知從原始 JSON 找：turnId 欄位，或 turn 物件的 id
        if (StringProperty(unknown.Raw, "turnId") is { } turnId)
        {
            return turnId;
        }

        return unknown.Raw.ValueKind == JsonValueKind.Object && unknown.Raw.TryGetProperty("turn", out var turn)
            ? StringProperty(turn, "id")
            : null;
    }

    private static string? ThreadId(Notification notification) =>
        notification.Payload is UnknownNotification unknown
            ? StringProperty(unknown.Raw, "threadId")
            : ProtocolRegistry.NotificationThreadId(notification.Payload);

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>一個 turn 共用的未讀事件；每個訂閱者各自有游標。對應 Python 的 <c>_TurnState</c>。</summary>
internal sealed class TurnState(string id, string? threadId)
{
    private readonly Dictionary<int, object> _events = [];

    public string Id { get; } = id;

    public string? ThreadId { get; set; } = threadId;

    public int FirstEvent { get; private set; }

    public int NextEvent { get; private set; }

    public Dictionary<TurnSubscription, int> Subscribers { get; } = [];

    public bool Completed { get; set; }

    /// <summary>加入通知或連線失敗的例外。</summary>
    public void Append(object item)
    {
        _events[NextEvent] = item;
        NextEvent++;
    }

    public object EventAt(int index) => _events[index];

    public void DiscardBefore(int index)
    {
        while (FirstEvent < index)
        {
            _events.Remove(FirstEvent);
            FirstEvent++;
        }
    }
}

/// <summary>一個消費者在 turn 事件上的游標。對應 Python 的 <c>_TurnSubscription</c>。</summary>
internal sealed class TurnSubscription : IDisposable
{
    private readonly MessageRouter _router;
    private readonly TurnState _state;
    private int _cursor;
    private bool _closed;

    public TurnSubscription(MessageRouter router, TurnState state, int cursor)
    {
        _router = router;
        _state = state;
        _cursor = cursor;
        state.Subscribers[this] = cursor;
    }

    /// <summary>等這個 turn 的下一個事件。</summary>
    /// <exception cref="TransportClosedException">訂閱已關閉、turn 已經結束沒有更多事件，或連線中斷。</exception>
    public async Task<Notification> NextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_router.Gate)
            {
                if (_closed)
                {
                    throw new TransportClosedException("turn 的訂閱已經關閉。");
                }

                if (_cursor < _state.NextEvent)
                {
                    var item = _state.EventAt(_cursor);
                    _cursor++;
                    _state.Subscribers[this] = _cursor;
                    _router.PruneTurnEvents(_state);
                    return item is Exception e ? throw MessageRouter.Rethrow(e) : (Notification)item;
                }

                if (_state.Completed)
                {
                    throw new TransportClosedException("turn 已經結束，沒有更多事件。");
                }

                changed = _router.TurnChanged;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>不再讀取；這個游標之後的事件不再為它保留。</summary>
    public void Dispose()
    {
        lock (_router.Gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _state.Subscribers.Remove(this);
            _router.PruneTurnEvents(_state);
            _router.SignalTurnChanged();
        }
    }
}
