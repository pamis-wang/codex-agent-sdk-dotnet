using System.Text.Json;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>連線、初始化、請求配對、錯誤與關閉。</summary>
public class CodexAgentClientTests
{
    internal static CodexAgentClient CreateClient(FakeAppServer server, CodexAgentOptions? options = null) =>
        new(options ?? new CodexAgentOptions(), () => server);

    [Fact]
    public async Task StartAsync_SendsInitializeThenInitialized()
    {
        var server = new FakeAppServer();
        await using var codex = CreateClient(server, new CodexAgentOptions { ExperimentalApi = false, ClientVersion = "9.9.9" });

        await codex.ConnectAsync();

        var received = server.Received;
        Assert.Equal("initialize", received[0].Method);
        var parameters = received[0].Params;
        Assert.Equal(SdkInfo.ClientName, parameters.GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.9.9", parameters.GetProperty("clientInfo").GetProperty("version").GetString());
        Assert.False(parameters.GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean());
        Assert.False(received[0].Raw.TryGetProperty("jsonrpc", out _));
        Assert.Equal("initialized", received[1].Method);
        Assert.Null(received[1].Id);
        Assert.Equal(new CodexServerInfo("codex_cli_rs", "0.166.0 (Windows 10.0.26300; x86_64)", "0.166.0"), codex.ServerInfo);
        Assert.Equal("C:/Users/test/.codex", codex.Metadata.CodexHome);
    }

    [Fact]
    public async Task StartAsync_UserAgentWithoutVersion_FailsAndClosesTransport()
    {
        var server = new FakeAppServer { UserAgent = "codex" };
        await using var codex = CreateClient(server);

        await Assert.ThrowsAsync<CodexAgentException>(() => codex.ConnectAsync());

        Assert.True(server.Disposed);
        Assert.Throws<InvalidOperationException>(() => codex.Metadata);
    }

    [Fact]
    public async Task Requests_AreMatchedById_EvenWhenAnsweredOutOfOrder()
    {
        var pending = new List<ClientMessage>();
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("thread/archive"))
            {
                pending.Add(message);
                if (pending.Count == 2)
                {
                    // 第二個請求先回覆
                    s.Reply(pending[1].Id!.Value, "{}");
                    s.Reply(pending[0].Id!.Value, "{}");
                }
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);
        await codex.ConnectAsync();

        var first = codex.ArchiveThreadAsync("a");
        var second = codex.ArchiveThreadAsync("b");
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["a", "b"], pending.Select(m => m.Params.GetProperty("threadId").GetString()));
    }

    [Theory]
    [InlineData(-32600, typeof(InvalidRequestException))]
    [InlineData(-32601, typeof(MethodNotFoundException))]
    [InlineData(-32602, typeof(InvalidParamsException))]
    [InlineData(-32603, typeof(InternalRpcException))]
    [InlineData(-32001, typeof(CodexRpcException))]
    [InlineData(-1, typeof(JsonRpcException))]
    public async Task ErrorResponse_MapsToTypedException(int code, Type expected)
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("model/list"))
            {
                s.ReplyError(message.Id!.Value, code, "nope", """{"reason": "x"}""");
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        var error = await Assert.ThrowsAnyAsync<JsonRpcException>(() => codex.ListModelsAsync());

        Assert.IsType(expected, error);
        Assert.Equal(code, error.Code);
        Assert.Equal("nope", error.ErrorMessage);
        Assert.Equal("x", error.ErrorData!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ServerOverloaded_IsRetryable()
    {
        var attempts = 0;
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("model/list"))
            {
                if (++attempts < 3)
                {
                    s.ReplyError(message.Id!.Value, -32000, "busy", """{"codexErrorInfo": "serverOverloaded"}""");
                }
                else
                {
                    s.Reply(message.Id!.Value, """{"data": []}""");
                }
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        var models = await CodexRetry.RetryOnOverloadAsync(
            ct => codex.ListModelsAsync(cancellationToken: ct), initialDelay: TimeSpan.FromMilliseconds(1));

        Assert.Empty(models.Data);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ServerClosesStdout_PendingRequestFailsWithTransportClosed()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("model/list"))
            {
                s.Close();
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        await Assert.ThrowsAsync<TransportClosedException>(() => codex.ListModelsAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        // 連線已經斷了，之後的請求也立刻失敗，不會卡住
        await Assert.ThrowsAnyAsync<CodexAgentException>(() => codex.GetAccountAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DisposeAsync_ClosesTransport_AndRejectsFurtherCalls()
    {
        var server = new FakeAppServer();
        var codex = CreateClient(server);
        await codex.ConnectAsync();

        await codex.DisposeAsync();

        Assert.True(server.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => codex.ListModelsAsync());
    }

    [Fact]
    public async Task StartThreadAsync_MapsOptionsToProtocol()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("thread/start"))
            {
                s.Reply(message.Id!.Value, Json.ThreadStartResponse("thr_42"));
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        var thread = await codex.StartThreadAsync(new ThreadStartOptions
        {
            Sandbox = Sandbox.FullAccess,
            WorkingDirectory = "D:/work",
            Model = "gpt-6",
        });

        Assert.Equal("thr_42", thread.Id);
        var parameters = server.Received.Single(m => m.Method == "thread/start").Params;
        Assert.Equal("danger-full-access", parameters.GetProperty("sandbox").GetString());
        Assert.Equal("on-request", parameters.GetProperty("approvalPolicy").GetString());
        Assert.Equal("auto_review", parameters.GetProperty("approvalsReviewer").GetString());
        Assert.Equal("D:/work", parameters.GetProperty("cwd").GetString());
        Assert.False(parameters.TryGetProperty("ephemeral", out _));
    }

    [Fact]
    public async Task ResumeThreadAsync_IncludeTurns_MapsToExcludeTurns()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("thread/resume"))
            {
                s.Reply(message.Id!.Value, Json.ThreadStartResponse("thr_1"));
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        await codex.ResumeThreadAsync("thr_1", new ThreadResumeOptions { IncludeTurns = false, ApprovalMode = ApprovalMode.DenyAll });

        var parameters = server.Received.Single(m => m.Method == "thread/resume").Params;
        Assert.True(parameters.GetProperty("excludeTurns").GetBoolean());
        Assert.Equal("never", parameters.GetProperty("approvalPolicy").GetString());
        Assert.False(parameters.TryGetProperty("approvalsReviewer", out _));
    }

    [Fact]
    public async Task NewerField_OnOldRuntime_IsRejectedBeforeSending()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("thread/resume"))
            {
                s.Reply(message.Id!.Value, Json.ThreadStartResponse("thr_1"));
            }

            return Task.CompletedTask;
        })
        {
            UserAgent = "codex_cli_rs/0.150.0",
        };
        await using var codex = CreateClient(server);

        var error = await Assert.ThrowsAsync<CodexAgentException>(
            () => codex.ResumeThreadAsync("thr_1", new ThreadResumeOptions { IncludeTurns = true }));

        Assert.Contains("excludeTurns", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Received, m => m.Method == "thread/resume");
    }

    [Fact]
    public async Task LoginChatgpt_CompletionArrivingBeforeWait_IsNotLost()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("account/login/start"))
            {
                s.Reply(message.Id!.Value, """{"type": "chatgpt", "loginId": "L1", "authUrl": "https://example.test/auth"}""");
                // 使用者很快就完成登入：通知在呼叫 WaitAsync 之前就到了
                s.Notify("account/login/completed", """{"loginId": "L1", "success": true}""");
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        var login = await codex.LoginChatgptAsync();
        await Task.Delay(50);
        var completed = await login.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("https://example.test/auth", login.AuthUrl);
        Assert.True(completed.Success);
        Assert.Equal("chatgpt", server.Received.Single(m => m.Method == "account/login/start").Params.GetProperty("type").GetString());
    }

    [Fact]
    public async Task LogoutAsync_SendsRequestWithoutParams()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("account/logout"))
            {
                s.Reply(message.Id!.Value, "{}");
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        await codex.LogoutAsync();

        Assert.False(server.Received.Single(m => m.Method == "account/logout").Raw.TryGetProperty("params", out _));
    }

    [Fact]
    public async Task UnknownResponseShape_IsReportedAsCodexException()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("model/list"))
            {
                s.Reply(message.Id!.Value, "[]");
            }

            return Task.CompletedTask;
        });
        await using var codex = CreateClient(server);

        var error = await Assert.ThrowsAsync<CodexAgentException>(() => codex.ListModelsAsync());

        Assert.Contains("model/list", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseNotification_UnknownMethod_KeepsParams()
    {
        using var document = JsonDocument.Parse("""{"x": 1}""");

        var payload = AppServerConnection.ParseNotification("future/method", document.RootElement);

        Assert.Equal(1, Assert.IsType<UnknownNotification>(payload).Raw.GetProperty("x").GetInt32());
    }
}
