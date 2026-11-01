using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>只在設定 <c>CODEX_INTEGRATION_TESTS=1</c> 時執行：會啟動本機的 Codex，並消耗帳號的額度。</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CODEX_INTEGRATION_TESTS") != "1")
        {
            Skip = "設定 CODEX_INTEGRATION_TESTS=1 才執行：會啟動本機的 Codex 並消耗額度。";
        }
    }
}

/// <summary>對真的 <c>codex app-server</c> 跑一遍。對應 Python SDK 的 <c>test_real_app_server_integration.py</c>。</summary>
public class IntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static string WorkingDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-dotnet-sdk-integration");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [IntegrationFact]
    public async Task Initialize_ReportsServerAndModels()
    {
        await using var codex = new CodexAgentClient();

        await codex.ConnectAsync();
        var models = await codex.ListModelsAsync();

        RuntimeRequirements.RequireRuntimeVersion(codex.ServerInfo.RuntimeVersion);
        Assert.NotEmpty(models.Data);
    }

    [IntegrationFact]
    public async Task RunAsync_ReturnsFinalResponse_AndStreamsDeltas()
    {
        await using var codex = new CodexAgentClient();
        var thread = await codex.StartThreadAsync(new ThreadStartOptions
        {
            Ephemeral = true,
            Sandbox = Sandbox.ReadOnly,
            WorkingDirectory = WorkingDirectory(),
        });

        using var turn = await thread.StartTurnAsync("Reply with exactly the word OK and nothing else.");
        var deltas = 0;
        await foreach (var notification in turn.StreamAsync().WithCancellation(new CancellationTokenSource(Timeout).Token))
        {
            if (notification.Payload is AgentMessageDeltaNotification)
            {
                deltas++;
            }
        }

        var result = await thread.RunAsync("Now reply with exactly the word DONE.").WaitAsync(Timeout);

        Assert.True(deltas > 0);
        Assert.Equal(TurnStatus.Completed, result.Status);
        Assert.Contains("DONE", result.FinalResponse, StringComparison.Ordinal);
        Assert.NotNull(result.Usage);
    }

    [IntegrationFact]
    public async Task ApprovalModeUser_SendsApprovalToHandler()
    {
        var calls = 0;
        await using var codex = new CodexAgentClient(new CodexAgentOptions
        {
            ServerRequestHandler = (request, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<object?>(request.Params switch
                {
                    CommandExecutionRequestApprovalParams => new CommandExecutionRequestApprovalResponse { Decision = CommandExecutionApprovalDecision.Decline },
                    FileChangeRequestApprovalParams => new FileChangeRequestApprovalResponse { Decision = FileChangeApprovalDecision.Decline },
                    _ => null,
                });
            },
        });
        var directory = WorkingDirectory();
        var target = Path.Combine(directory, $"approval-{Guid.NewGuid():N}.txt");
        var thread = await codex.StartThreadAsync(new ThreadStartOptions
        {
            Ephemeral = true,
            Sandbox = Sandbox.ReadOnly,
            ApprovalMode = ApprovalMode.User,
            WorkingDirectory = directory,
        });

        await thread.RunAsync(
            $"Create a file named {Path.GetFileName(target)} containing the word hi in the current directory. "
            + "The sandbox is read-only, so request escalated permissions for the write command and wait for approval.")
            .WaitAsync(Timeout);

        Assert.True(calls > 0);
        Assert.False(File.Exists(target));
    }
}
