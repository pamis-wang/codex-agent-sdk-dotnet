namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>連線時的版本警告與連線狀態。改環境變數的測試放在同一個類別，依序執行。</summary>
public class WarningCallbackTests
{
    [Fact]
    public async Task OutdatedCli_WarnsButStillConnects()
    {
        var warnings = new List<string>();
        var server = new FakeAppServer { UserAgent = "codex_cli_rs/0.150.0 (Windows 10; x86_64)" };
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions { WarningCallback = warnings.Add });

        await codex.ConnectAsync();

        var warning = Assert.Single(warnings);
        Assert.Contains("0.150.0", warning, StringComparison.Ordinal);
        Assert.Contains(RuntimeRequirements.MinimumRuntimeVersion, warning, StringComparison.Ordinal);
        Assert.True(codex.IsConnected);
    }

    [Theory]
    [InlineData("codex_cli_rs/0.166.0")]
    [InlineData("codex_cli_rs/0.0.0")]
    [InlineData("codex_cli_rs/nightly")]
    public async Task SupportedOrUnknownVersion_DoesNotWarn(string userAgent)
    {
        var warnings = new List<string>();
        var server = new FakeAppServer { UserAgent = userAgent };
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions { WarningCallback = warnings.Add });

        await codex.ConnectAsync();

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task SkipVariable_DisablesTheCheck()
    {
        var warnings = new List<string>();
        var server = new FakeAppServer { UserAgent = "codex_cli_rs/0.100.0" };
        await using var codex = CodexAgentClientTests.CreateClient(server, new CodexAgentOptions { WarningCallback = warnings.Add });
        Environment.SetEnvironmentVariable(RuntimeRequirements.SkipVersionCheckVariable, "1");
        try
        {
            await codex.ConnectAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(RuntimeRequirements.SkipVersionCheckVariable, null);
        }

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task IsConnected_TracksLifecycle()
    {
        var codex = CodexAgentClientTests.CreateClient(new FakeAppServer());
        Assert.False(codex.IsConnected);

        await codex.ConnectAsync();
        Assert.True(codex.IsConnected);

        await codex.DisposeAsync();
        Assert.False(codex.IsConnected);
    }
}
