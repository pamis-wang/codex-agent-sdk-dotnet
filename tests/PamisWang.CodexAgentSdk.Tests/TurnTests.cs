using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>turn 的事件串流、結果收集與控制。</summary>
public class TurnTests
{
    /// <summary>
    /// 假 server：thread/start 回 thr_1；每次 turn/start 依序給 turn_1、turn_2…，先送 <paramref name="beforeReply"/> 的事件再回覆，
    /// 回覆後送 <paramref name="afterReply"/> 的事件。
    /// </summary>
    private static FakeAppServer TurnServer(
        Action<FakeAppServer, string>? beforeReply = null,
        Action<FakeAppServer, string>? afterReply = null,
        Func<FakeAppServer, ClientMessage, bool>? other = null)
    {
        var turns = 0;
        return new FakeAppServer((s, message) =>
        {
            if (other?.Invoke(s, message) == true)
            {
                return Task.CompletedTask;
            }

            if (message.IsRequest("thread/start"))
            {
                s.Reply(message.Id!.Value, Json.ThreadStartResponse("thr_1"));
            }
            else if (message.IsRequest("turn/start"))
            {
                var turnId = $"turn_{++turns}";
                beforeReply?.Invoke(s, turnId);
                s.Reply(message.Id!.Value, $$"""{"turn": {{Json.Turn(turnId)}}}""");
                afterReply?.Invoke(s, turnId);
            }

            return Task.CompletedTask;
        });
    }

    private static void Complete(FakeAppServer s, string turnId, string text = "done", string? phase = "final_answer")
    {
        s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m_" + turnId, text, phase)));
        s.Notify("thread/tokenUsage/updated", Json.TokenUsage("thr_1", turnId, 42));
        s.Notify("turn/completed", Json.TurnEvent("thr_1", turnId, "completed"));
    }

    [Fact]
    public async Task StreamAsync_EventsSentBeforeTurnStartResponse_AreNotLost()
    {
        var server = TurnServer(
            beforeReply: (s, turnId) =>
            {
                s.Notify("turn/started", Json.TurnEvent("thr_1", turnId));
                s.Notify("item/agentMessage/delta", Json.Delta("thr_1", turnId, "Hel"));
            },
            afterReply: (s, turnId) =>
            {
                s.Notify("item/agentMessage/delta", Json.Delta("thr_1", turnId, "lo"));
                Complete(s, turnId);
            });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        using var turn = await thread.StartTurnAsync("hi");
        var methods = new List<string>();
        var text = "";
        await foreach (var notification in turn.StreamAsync().WithCancellation(new CancellationTokenSource(5000).Token))
        {
            methods.Add(notification.Method);
            if (notification.Payload is AgentMessageDeltaNotification delta)
            {
                text += delta.Delta;
            }
        }

        Assert.Equal(
            ["turn/started", "item/agentMessage/delta", "item/agentMessage/delta", "item/completed", "thread/tokenUsage/updated", "turn/completed"],
            methods);
        Assert.Equal("Hello", text);
    }

    [Fact]
    public async Task RunAsync_PrefersFinalAnswer_AndCollectsUsage()
    {
        var server = TurnServer(afterReply: (s, turnId) =>
        {
            s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m1", "thinking aloud", "commentary")));
            s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m2", "the answer", "final_answer")));
            s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m3", "unlabelled", null)));
            s.Notify("thread/tokenUsage/updated", Json.TokenUsage("thr_1", turnId, 42));
            s.Notify("turn/completed", Json.TurnEvent("thr_1", turnId, "completed"));
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        var result = await thread.RunAsync("question").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("the answer", result.FinalResponse);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(42, result.Usage!.Total.TotalTokens);
        Assert.Equal(TurnStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RunAsync_WithoutFinalAnswer_UsesLastUnlabelledMessage()
    {
        var server = TurnServer(afterReply: (s, turnId) =>
        {
            s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m1", "first", null)));
            s.Notify("item/completed", Json.ItemCompleted("thr_1", turnId, Json.AgentMessage("m2", "second", null)));
            s.Notify("turn/completed", Json.TurnEvent("thr_1", turnId, "completed"));
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        var result = await thread.RunAsync("question").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("second", result.FinalResponse);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task RunAsync_FailedTurn_ThrowsWithServerMessage()
    {
        var server = TurnServer(afterReply: (s, turnId) =>
            s.Notify("turn/completed", Json.TurnEvent("thr_1", turnId, "failed", error: "quota exceeded")));
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        var error = await Assert.ThrowsAsync<TurnFailedException>(() => thread.RunAsync("x").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("quota exceeded", error.Message);
        Assert.Equal(TurnStatus.Failed, error.Turn!.Status);
    }

    [Fact]
    public async Task StartTurnAsync_MapsOptionsAndInputs()
    {
        var server = TurnServer(afterReply: (s, turnId) => Complete(s, turnId));
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        await thread.RunAsync(
            new InputItem[] { new TextInput("看這張圖"), new LocalImageInput("C:/a.png") },
            new TurnOptions { Sandbox = Sandbox.ReadOnly, Source = "review_ui", TurnServiceTier = "default", Effort = "high", ApprovalMode = ApprovalMode.User });

        var parameters = server.Received.Single(m => m.Method == "turn/start").Params;
        Assert.Equal("thr_1", parameters.GetProperty("threadId").GetString());
        Assert.Equal("text", parameters.GetProperty("input")[0].GetProperty("type").GetString());
        Assert.Equal("看這張圖", parameters.GetProperty("input")[0].GetProperty("text").GetString());
        Assert.Equal("localImage", parameters.GetProperty("input")[1].GetProperty("type").GetString());
        Assert.Equal("readOnly", parameters.GetProperty("sandboxPolicy").GetProperty("type").GetString());
        Assert.Equal("review_ui", parameters.GetProperty("turnTrigger").GetString());
        Assert.Equal("default", parameters.GetProperty("serviceTierForTurn").GetString());
        Assert.Equal("high", parameters.GetProperty("effort").GetString());
        Assert.Equal("user", parameters.GetProperty("approvalsReviewer").GetString());
    }

    [Fact]
    public async Task ExternalMessage_IsSentAsToolOutput()
    {
        var server = TurnServer(afterReply: (s, turnId) => Complete(s, turnId));
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();

        await thread.RunAsync(new ExternalMessage("notifications", "staging checks failed", "slack"));

        var parameters = server.Received.Single(m => m.Method == "turn/start").Params;
        Assert.Equal(0, parameters.GetProperty("input").GetArrayLength());
        var toolOutput = parameters.GetProperty("toolOutput");
        Assert.Equal("notifications", toolOutput.GetProperty("name").GetString());
        Assert.Equal("slack", toolOutput.GetProperty("namespace").GetString());
        Assert.Equal("staging checks failed", toolOutput.GetProperty("output").GetString());
    }

    [Fact]
    public async Task SteerAndInterrupt_TargetTheTurn()
    {
        var server = TurnServer(other: (s, message) =>
        {
            if (message.IsRequest("turn/steer"))
            {
                s.Reply(message.Id!.Value, """{"turnId": "turn_1"}""");
                return true;
            }

            if (message.IsRequest("turn/interrupt"))
            {
                s.Reply(message.Id!.Value, "{}");
                s.Notify("turn/completed", Json.TurnEvent("thr_1", "turn_1", "interrupted"));
                return true;
            }

            return false;
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();
        using var turn = await thread.StartTurnAsync("start");

        var steered = await turn.SteerAsync("also check tests");
        await turn.InterruptAsync();
        var result = await turn.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("turn_1", steered.TurnId);
        var steer = server.Received.Single(m => m.Method == "turn/steer").Params;
        Assert.Equal("turn_1", steer.GetProperty("expectedTurnId").GetString());
        Assert.Equal("also check tests", steer.GetProperty("input")[0].GetProperty("text").GetString());
        Assert.Equal("turn_1", server.Received.Single(m => m.Method == "turn/interrupt").Params.GetProperty("turnId").GetString());
        Assert.Equal(TurnStatus.Interrupted, result.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => turn.SteerAsync(new ExternalMessage("tool", "x")));
    }

    [Fact]
    public async Task UnknownNotification_IsRoutedByRawTurnId()
    {
        var server = TurnServer(afterReply: (s, turnId) =>
        {
            s.Notify("item/futureThing", $$"""{"threadId": "thr_1", "turnId": "{{turnId}}", "x": 1}""");
            Complete(s, turnId);
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();
        using var turn = await thread.StartTurnAsync("hi");

        var notifications = await turn.StreamAsync().ToListAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var unknown = Assert.IsType<UnknownNotification>(notifications[0].Payload);
        Assert.Equal("item/futureThing", notifications[0].Method);
        Assert.Equal(1, unknown.Raw.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ConcurrentTurnsOnDifferentThreads_AreRoutedSeparately()
    {
        var threads = 0;
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("thread/start"))
            {
                s.Reply(message.Id!.Value, Json.ThreadStartResponse($"thr_{++threads}"));
            }
            else if (message.IsRequest("turn/start"))
            {
                var threadId = message.Params.GetProperty("threadId").GetString()!;
                var turnId = "turn_of_" + threadId;
                s.Reply(message.Id!.Value, $$"""{"turn": {{Json.Turn(turnId)}}}""");
            }

            return Task.CompletedTask;
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var first = await codex.StartThreadAsync();
        var second = await codex.StartThreadAsync();
        using var firstTurn = await first.StartTurnAsync("a");
        using var secondTurn = await second.StartTurnAsync("b");

        // 兩個 turn 的事件交錯送達
        server.Notify("item/completed", Json.ItemCompleted("thr_2", "turn_of_thr_2", Json.AgentMessage("b", "B", "final_answer")));
        server.Notify("item/completed", Json.ItemCompleted("thr_1", "turn_of_thr_1", Json.AgentMessage("a", "A", "final_answer")));
        server.Notify("turn/completed", Json.TurnEvent("thr_2", "turn_of_thr_2", "completed"));
        server.Notify("turn/completed", Json.TurnEvent("thr_1", "turn_of_thr_1", "completed"));

        var results = await Task.WhenAll(firstTurn.RunAsync(), secondTurn.RunAsync()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("A", results[0].FinalResponse);
        Assert.Equal("B", results[1].FinalResponse);
    }

    [Fact]
    public async Task ConnectionLostMidTurn_StreamFails()
    {
        var server = TurnServer(afterReply: (s, turnId) => s.Notify("turn/started", Json.TurnEvent("thr_1", turnId)));
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();
        using var turn = await thread.StartTurnAsync("hi");

        server.Close();

        await Assert.ThrowsAsync<TransportClosedException>(() => turn.RunAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RunAsync_Cancelled_InterruptsTheTurn()
    {
        var server = TurnServer(other: (s, message) =>
        {
            if (message.IsRequest("turn/interrupt"))
            {
                s.Reply(message.Id!.Value, "{}");
                return true;
            }

            return false;
        });
        await using var codex = CodexAgentClientTests.CreateClient(server);
        var thread = await codex.StartThreadAsync();
        using var cancellation = new CancellationTokenSource();

        var run = thread.RunAsync("long task", cancellationToken: cancellation.Token);
        await server.WaitForAsync(m => m.IsRequest("turn/start"));
        await Task.Delay(50);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(server.Received, m => m.Method == "turn/interrupt");
    }
}
