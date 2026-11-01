using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PamisWang.CodexAgentSdk.Transport;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 較新的請求欄位需要的 Codex CLI 版本。對應 Python SDK 的 <c>_runtime_requirements.py</c>：
/// 舊版 app-server 會默默忽略不認得的欄位，所以送出前先檢查，而不是讓設定無聲失效。
/// </summary>
internal static partial class RuntimeRequirements
{
    /// <summary>送出 <see cref="GuardedFields"/> 裡的欄位需要的最低版本，也是連線時警告的門檻。</summary>
    public const string MinimumRuntimeVersion = "0.151.0";

    /// <summary>設成任何非空的值就不在連線時檢查版本。名稱比照 Claude 版的 <c>CLAUDE_AGENT_SDK_SKIP_VERSION_CHECK</c>。</summary>
    public const string SkipVersionCheckVariable = "CODEX_AGENT_SDK_SKIP_VERSION_CHECK";

    /// <summary>需要檢查版本的方法與欄位。對應 Python <c>CodexClient.request</c> 的 <c>runtime_fields</c>。</summary>
    public static IReadOnlyDictionary<string, string[]> GuardedFields { get; } = new Dictionary<string, string[]>
    {
        ["turn/start"] = ["toolOutput", "turnTrigger", "serviceTierForTurn"],
        ["thread/resume"] = ["excludeTurns"],
        ["thread/fork"] = ["excludeTurns"],
    };

    /// <summary>
    /// 版本不夠新就丟例外；讀不懂的版本也拒絕。最低版本的預先發行版（例如 <c>0.151.0-alpha.1</c>）同樣不接受。
    /// </summary>
    /// <exception cref="ArgumentException">版本不符合。</exception>
    public static void RequireRuntimeVersion(string? version)
    {
        if (TryParse(version, out var parsed, out var prerelease)
            && TryParse(MinimumRuntimeVersion, out var minimum, out _)
            && (parsed > minimum || (parsed == minimum && !prerelease)))
        {
            return;
        }

        throw new ArgumentException(
            $"需要 Codex CLI {MinimumRuntimeVersion} 以上；回報的版本是 {(version is null ? "unknown" : $"'{version}'")}");
    }

    /// <summary>
    /// 版本低於 <see cref="MinimumRuntimeVersion"/> 就呼叫 <paramref name="warn"/>。對應 Claude 版的 <c>CliVersionCheck</c>。
    /// 讀不懂的版本與沒有版本號的開發版（<c>0.0.0</c>）不警告：這只是提醒，真的用到較新的欄位時 <see cref="RequireRuntimeVersion"/> 會再擋。
    /// </summary>
    public static void WarnIfOutdated(string runtimeVersion, string? cliPath, Action<string> warn)
    {
        if (runtimeVersion == "0.0.0"
            || !TryParse(runtimeVersion, out var parsed, out var prerelease)
            || !TryParse(MinimumRuntimeVersion, out var minimum, out _)
            || parsed > minimum
            || (parsed == minimum && !prerelease))
        {
            return;
        }

        var location = cliPath is null ? "" : $"（{cliPath}）";
        warn($"Codex CLI {runtimeVersion}{location}低於這個套件支援的最低版本 {MinimumRuntimeVersion}，部分功能可能無法正常運作。"
            + "請更新 Codex CLI。");
    }

    private static bool TryParse(string? version, out Version parsed, out bool prerelease)
    {
        parsed = new Version();
        prerelease = false;
        var match = VersionPattern().Match(version ?? "");
        if (!match.Success)
        {
            return false;
        }

        parsed = new Version(
            int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture));
        prerelease = match.Groups[4].Success;
        return true;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d+)(-\S+)?$")]
    private static partial Regex VersionPattern();
}

/// <summary>
/// 沒有版本號的開發版 CLI（回報 <c>0.0.0</c>）支援哪些欄位：用同一個執行檔與設定產生 experimental schema 來看。
/// 對應 Python SDK 的 <c>CheckoutCapabilities</c>；第一次需要時才產生，之後重用。
/// </summary>
internal sealed class CheckoutCapabilities(string cliPath, CodexAgentOptions options)
{
    private static readonly (string Method, string Schema)[] Schemas =
    [
        ("turn/start", "TurnStartParams"),
        ("thread/resume", "ThreadResumeParams"),
        ("thread/fork", "ThreadForkParams"),
    ];

    private readonly Lock _gate = new();
    private Task<Dictionary<string, HashSet<string>>>? _fields;

    /// <summary>每個方法的參數支援的欄位名稱。產生一次後重用；失敗的話下次重試。</summary>
    /// <exception cref="ArgumentException">產生或讀取 schema 失敗。</exception>
    public async Task<IReadOnlyDictionary<string, HashSet<string>>> GetFieldsAsync(CancellationToken cancellationToken)
    {
        Task<Dictionary<string, HashSet<string>>> fields;
        lock (_gate)
        {
            if (_fields is null || _fields.IsFaulted || _fields.IsCanceled)
            {
                // 不綁呼叫端的取消：產生到一半的結果也許下一個呼叫端用得到
                _fields = GenerateAsync(CancellationToken.None);
            }

            fields = _fields;
        }

        return await fields.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, HashSet<string>>> GenerateAsync(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("codex-sdk-schema-");
        try
        {
            var startInfo = SubprocessTransport.CreateStartInfo(cliPath, options);
            startInfo.RedirectStandardInput = false;
            foreach (var argument in (ReadOnlySpan<string>)["app-server", "generate-json-schema", "--experimental", "--out", directory.FullName])
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new ArgumentException("無法啟動 Codex 產生 schema。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new ArgumentException($"產生 schema 失敗（exit code {process.ExitCode}）：{await error.ConfigureAwait(false)}");
            }

            var result = new Dictionary<string, HashSet<string>>();
            foreach (var (method, schema) in Schemas)
            {
                var path = Path.Combine(directory.FullName, "v2", schema + ".json");
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                if (!document.RootElement.TryGetProperty("properties", out var properties)
                    || properties.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException($"{schema} 的 schema 沒有 properties。");
                }

                result[method] = [.. properties.EnumerateObject().Select(p => p.Name)];
            }

            return result;
        }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            throw new ArgumentException("無法檢查沒有版本號的 CLI 的 experimental schema。", e);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // 暫存目錄刪不掉不影響結果
            }
        }
    }
}

/// <summary>initialize 回應的檢查與伺服器資訊。對應 Python SDK 的 <c>_initialize_metadata.py</c>。</summary>
internal static class InitializeMetadata
{
    /// <summary>
    /// 從 <c>userAgent</c>（例如 <c>codex_cli_rs/0.166.0 (Windows 10.0.26300; x86_64)</c>）取出伺服器名稱與版本；
    /// 缺少任何一項就丟例外，跟 Python 的 <c>validate_initialize_metadata</c> 一樣。
    /// </summary>
    public static CodexServerInfo Validate(Protocol.InitializeResponse response)
    {
        var userAgent = (response.UserAgent ?? "").Trim();
        var (name, version) = SplitUserAgent(userAgent);
        name = name?.Trim();
        version = version?.Trim();
        if (userAgent.Length == 0 || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
        {
            throw new CodexAgentException(
                $"initialize 回應缺少必要的中繼資料（userAgent='{userAgent}'，name='{name}'，version='{version}'）。");
        }

        return new CodexServerInfo(name, version, RuntimeVersion(version));
    }

    /// <summary>第一個空白之前的部分，例如 <c>0.166.0 (Windows ...)</c> 的 <c>0.166.0</c>。</summary>
    public static string RuntimeVersion(string version) =>
        version.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? version;

    internal static (string? Name, string? Version) SplitUserAgent(string userAgent)
    {
        var raw = userAgent.Trim();
        if (raw.Length == 0)
        {
            return (null, null);
        }

        var slash = raw.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            var name = raw[..slash];
            var version = raw[(slash + 1)..];
            return (name.Length == 0 ? null : name, version.Length == 0 ? null : version);
        }

        var parts = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : (raw, null);
    }
}
