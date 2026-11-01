using System.Reflection;

namespace PamisWang.CodexAgentSdk;

/// <summary>套件的版本，以及移植時對照的來源版本。</summary>
internal static class SdkInfo
{
    /// <summary>移植時對照的 openai/codex commit；Python SDK 的行為以它為準。</summary>
    public const string PythonSdkCommit = "67f0994";

    /// <summary>initialize 時送給 app-server 的 client 名稱。不冒用官方 SDK 的名稱。</summary>
    public const string ClientName = "pamiswang_codex_dotnet_sdk";

    /// <summary>initialize 時送給 app-server 的 client 標題。</summary>
    public const string ClientTitle = "Codex .NET SDK (unofficial)";

    /// <summary>套件自己的版本，不含 SourceLink 附加的 <c>+commit</c>。</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = typeof(SdkInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
