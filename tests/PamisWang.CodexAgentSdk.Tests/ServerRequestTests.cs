using System.Text.Json;
using System.Text.Json.Nodes;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>app-server 送來、要由 client 回覆的請求，例如核准。</summary>
public class ServerRequestTests
{
    private const string CommandApprovalParams =
        """{"threadId": "thr_1", "turnId": "turn_1", "itemId": "cmd_1", "startedAtMs": 1, "command": "rm -rf build", "reason": "clean"}""";

    [Fact]
    public async Task Handler_ReceivesTypedParams_AndResultIsSentBack()
    {
        ServerRequest? seen = null;
        var server = new FakeAppServer();
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions
        {
            ServerRequestHandler = (request, _) =>
            {
                seen = request;
                return Task.FromResult<object?>(new CommandExecutionRequestApprovalResponse { Decision = CommandExecutionApprovalDecision.Decline });
            },
        });
        await codex.ConnectAsync();

        server.Request("req-1", "item/commandExecution/requestApproval", CommandApprovalParams);
        var reply = await server.WaitForAsync(m => m.Method is null && m.Id?.GetString() == "req-1");

        Assert.Equal(ServerRequestMethods.ItemCommandExecutionRequestApproval, seen!.Method);
        var parameters = Assert.IsType<CommandExecutionRequestApprovalParams>(seen.Params);
        Assert.Equal("rm -rf build", parameters.Command);
        Assert.Equal("decline", reply.Raw.GetProperty("result").GetProperty("decision").GetString());
    }

    [Fact]
    public async Task NoHandler_AcceptsCommandAndFileApprovals_LikePythonSdk()
    {
        var server = new FakeAppServer();
        await using var codex = CodexAgentClientTests.CreateClient(server);
        await codex.ConnectAsync();

        server.Request("r1", "item/commandExecution/requestApproval", CommandApprovalParams);
        server.Request("r2", "item/fileChange/requestApproval", """{"threadId": "thr_1", "turnId": "turn_1", "itemId": "f1", "startedAtMs": 1}""");
        server.Request("r3", "item/tool/requestUserInput", """{"threadId": "thr_1", "turnId": "turn_1", "itemId": "q1", "questions": []}""");

        var replies = new Dictionary<string, JsonElement>();
        while (replies.Count < 3)
        {
            var reply = await server.WaitForAsync(m => m.Method is null);
            replies[reply.Id!.Value.GetString()!] = reply.Raw.GetProperty("result");
        }

        Assert.Equal("accept", replies["r1"].GetProperty("decision").GetString());
        Assert.Equal("accept", replies["r2"].GetProperty("decision").GetString());
        Assert.Equal(JsonValueKind.Object, replies["r3"].ValueKind);
        Assert.Empty(replies["r3"].EnumerateObject());
    }

    [Fact]
    public async Task HandlerReturningJsonNode_IsSentAsIs()
    {
        var server = new FakeAppServer();
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions
        {
            ServerRequestHandler = (_, _) => Task.FromResult<object?>(new JsonObject { ["answers"] = new JsonObject() }),
        });
        await codex.ConnectAsync();

        server.Request("r1", "item/tool/requestUserInput", "{}");
        var reply = await server.WaitForAsync(m => m.Method is null);

        Assert.True(reply.Raw.GetProperty("result").TryGetProperty("answers", out _));
    }

    [Fact]
    public async Task HandlerThrows_RepliesWithJsonRpcError_AndConnectionSurvives()
    {
        var server = new FakeAppServer((s, message) =>
        {
            if (message.IsRequest("model/list"))
            {
                s.Reply(message.Id!.Value, """{"data": []}""");
            }

            return Task.CompletedTask;
        });
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions
        {
            ServerRequestHandler = (_, _) => throw new InvalidOperationException("handler broke"),
        });
        await codex.ConnectAsync();

        server.Request("r1", "item/commandExecution/requestApproval", CommandApprovalParams);
        var reply = await server.WaitForAsync(m => m.Method is null);

        Assert.Equal(-32603, reply.Raw.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("handler broke", reply.Raw.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty((await codex.ListModelsAsync()).Data);
    }

    [Fact]
    public async Task ServerRequestResolved_CancelsHandler_WithoutReply()
    {
        var cancelled = new TaskCompletionSource();
        var server = new FakeAppServer();
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions
        {
            ServerRequestHandler = async (_, ct) =>
            {
                await using var registration = ct.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
        });
        await codex.ConnectAsync();

        server.Request("r1", "item/commandExecution/requestApproval", CommandApprovalParams);
        await Task.Delay(50);
        server.Notify("serverRequest/resolved", """{"threadId": "thr_1", "requestId": "r1"}""");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.DoesNotContain(server.Received, m => m.Method is null && m.Id?.GetString() == "r1");
    }

    [Fact]
    public async Task UnknownServerRequest_HandlerGetsRawParams()
    {
        ServerRequest? seen = null;
        var server = new FakeAppServer();
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions
        {
            ServerRequestHandler = (request, _) =>
            {
                seen = request;
                return Task.FromResult<object?>(null);
            },
        });
        await codex.ConnectAsync();

        server.Request("r9", "future/request", """{"hello": "world"}""");
        await server.WaitForAsync(m => m.Method is null);

        Assert.Null(seen!.Params);
        Assert.Equal("world", seen.Raw.GetProperty("hello").GetString());
        Assert.Equal("r9", seen.Id.GetString());
    }
}
