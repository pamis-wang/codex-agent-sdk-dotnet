using System.Text.Json;
using PamisWang.CodexAgentSdk.Transport;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>版本檢查、userAgent 解析、錯誤對應與執行檔定位。</summary>
public class SupportTests
{
    [Theory]
    [InlineData("codex_cli_rs/0.166.0 (Windows 10; x86_64)", "codex_cli_rs", "0.166.0 (Windows 10; x86_64)")]
    [InlineData("codex 1.2.3", "codex", "1.2.3")]
    [InlineData("codex", "codex", null)]
    [InlineData("", null, null)]
    public void SplitUserAgent_FollowsPythonRules(string userAgent, string? name, string? version)
    {
        Assert.Equal((name, version), InitializeMetadata.SplitUserAgent(userAgent));
    }

    [Theory]
    [InlineData("0.151.0", true)]
    [InlineData("0.166.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.152.0-alpha.1", true)]
    [InlineData("0.151.0-alpha.1", false)]
    [InlineData("0.150.9", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void RequireRuntimeVersion_AcceptsOnlySupportedVersions(string? version, bool supported)
    {
        if (supported)
        {
            RuntimeRequirements.RequireRuntimeVersion(version);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => RuntimeRequirements.RequireRuntimeVersion(version));
        }
    }

    [Theory]
    [InlineData(-32000, "busy", """{"codex_error_info": "server_overloaded"}""", typeof(ServerBusyException))]
    [InlineData(-32000, "retry limit reached", """{"errorInfo": {"kind": "serverOverloaded"}}""", typeof(RetryLimitExceededException))]
    [InlineData(-32050, "too many failed attempts", null, typeof(RetryLimitExceededException))]
    [InlineData(-32700, "bad json", null, typeof(JsonRpcParseException))]
    public void MapJsonRpcError_RecognisesOverloadAndRetryLimit(int code, string message, string? data, Type expected)
    {
        JsonElement? element = data is null ? null : JsonDocument.Parse(data).RootElement.Clone();

        var exception = JsonRpcErrors.Map(code, message, element);

        Assert.IsType(expected, exception);
        Assert.Equal(expected.IsAssignableTo(typeof(ServerBusyException)), CodexRetry.IsRetryableError(exception));
    }

    [Theory]
    [InlineData(@"C:\npm\codex.cmd", true)]
    [InlineData(@"C:\npm\CODEX.BAT", true)]
    [InlineData(@"C:\npm\codex.cmd\..\..\codex.exe", true)]
    [InlineData(@"C:\tools\codex.exe", false)]
    public void IsWindowsBatchPath_DetectsBatchFiles(string path, bool expected)
    {
        Assert.Equal(expected, CliLocator.IsWindowsBatchPath(path));
    }

    [Fact]
    public void Locator_OnWindows_FallsBackToInstallerLocation()
    {
        var root = Directory.CreateTempSubdirectory("codex-locator-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "Programs", "OpenAI", "Codex", "bin"));
            var exe = Path.Combine(bin.FullName, "codex.exe");
            File.WriteAllText(exe, "");
            var locator = new CliLocator(pathVariable: "", homeDirectory: root.FullName, localAppData: root.FullName, isWindows: true);

            Assert.Equal(exe, locator.Find(null));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Locator_OnWindows_OnlyBatchOnPath_IsRejected()
    {
        var root = Directory.CreateTempSubdirectory("codex-locator-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "codex.cmd"), "");
            var locator = new CliLocator(pathVariable: root.FullName, homeDirectory: root.FullName, localAppData: root.FullName, isWindows: true);

            var found = locator.Find(null);

            Assert.Throws<CodexAgentException>(() => locator.EnsureRunnable(found));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Locator_MissingExplicitPath_Throws()
    {
        var locator = new CliLocator("", "", "", isWindows: true);

        Assert.Throws<CliNotFoundException>(() => locator.Find(@"C:\does\not\exist\codex.exe"));
    }

    [Fact]
    public void ExternalMessage_EmptyToolName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ProtocolMapping.ToTurnInput(new ExternalMessage(" ", "x")));
    }
}
