namespace PamisWang.CodexAgentSdk.Transport;

/// <summary>
/// 找 <c>codex</c> 執行檔，並拒絕在 Windows 上執行批次檔。
/// Python SDK 預設用套件附帶的 runtime（<c>openai-codex-cli-bin</c>）；這個套件不附帶，改從 PATH 與官方安裝程式的位置找。
/// </summary>
internal sealed class CliLocator
{
    private readonly string? _pathVariable;
    private readonly string _homeDirectory;
    private readonly string _localAppData;
    private readonly bool _isWindows;

    public CliLocator(string? pathVariable, string homeDirectory, string localAppData, bool isWindows)
    {
        _pathVariable = pathVariable;
        _homeDirectory = homeDirectory;
        _localAppData = localAppData;
        _isWindows = isWindows;
    }

    public static CliLocator ForCurrentProcess() => new(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        OperatingSystem.IsWindows());

    /// <summary>有指定路徑就用它（要存在），否則從 PATH 與常見的安裝位置找。</summary>
    /// <exception cref="CliNotFoundException">找不到。</exception>
    public string Find(string? cliPath)
    {
        if (!string.IsNullOrEmpty(cliPath))
        {
            return File.Exists(cliPath)
                ? cliPath
                : throw new CliNotFoundException($"找不到 Codex：{cliPath}。請把 CodexAgentOptions.CliPath 指到 codex 執行檔。");
        }

        return _isWindows ? FindOnWindows() : FindOnUnix();
    }

    /// <summary>Windows 上拒絕執行批次檔；其他平台什麼都不做。</summary>
    /// <exception cref="CodexAgentException">路徑是 .bat 或 .cmd。</exception>
    public void EnsureRunnable(string cliPath)
    {
        if (!_isWindows || !IsWindowsBatchPath(cliPath))
        {
            return;
        }

        // Windows 用 cmd.exe 執行批次檔，cmd.exe 會重新解讀整串參數，參數裡的字元可能被當成指令執行，
        // 而且沒有可靠的跳脫方式（CVE-2024-27980，BatBadBut）。npm 全域安裝產生的 codex.cmd 就是批次檔。
        throw new CodexAgentException(
            $"拒絕執行批次檔 {cliPath}：Windows 透過 cmd.exe 執行 .bat、.cmd，參數可能被注入指令。"
            + "請改用原生的 codex.exe，或把 CodexAgentOptions.CliPath 指到 codex.exe。");
    }

    /// <summary>
    /// 路徑的任何一段帶 .bat 或 .cmd 副檔名就算批次檔。不只看最後一段：Windows 開檔前會正規化路徑，
    /// 只要有一段是批次檔就拒絕，才不會被 <c>codex.cmd\..\..</c> 這類寫法繞過。冒號也拆開看，涵蓋 NTFS 資料流。
    /// </summary>
    public static bool IsWindowsBatchPath(string path) =>
        path.Replace('\\', '/').Split('/')
            .SelectMany(component => component.Split(':'))
            .Select(segment => segment.TrimEnd('.', ' '))
            .Any(segment => segment.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));

    private string FindOnWindows()
    {
        // 只用原生的 codex.exe；PATH 前面的目錄有 npm 的 codex.cmd 時，也優先用後面目錄的 codex.exe。
        // 官方 PowerShell 安裝程式裝在 %LOCALAPPDATA%\Programs\OpenAI\Codex\bin，並把它加進使用者的 PATH；
        // 剛裝好時已經在跑的程序拿不到新的 PATH，所以也直接找這個位置
        var native = SearchPath("codex.exe")
            ?? ExistingFile(Path.Combine(_localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"))
            ?? ExistingFile(Path.Combine(_homeDirectory, ".local", "bin", "codex.exe"));
        if (native is not null)
        {
            return native;
        }

        // 只找得到批次檔：交給 EnsureRunnable 以「拒絕執行批次檔」的說明失敗，而不是籠統的找不到
        return SearchPath("codex.cmd") ?? SearchPath("codex.bat") ?? throw new CliNotFoundException(
            "找不到 Codex CLI。請安裝原生的 codex.exe（官方 PowerShell 安裝程式會裝在 %LOCALAPPDATA%\\Programs\\OpenAI\\Codex\\bin），"
            + "或把 CodexAgentOptions.CliPath 指到 codex.exe。");
    }

    private string FindOnUnix()
    {
        if (SearchPath("codex") is { } onPath)
        {
            return onPath;
        }

        string[] locations =
        [
            Path.Combine(_homeDirectory, ".local", "bin", "codex"),
            "/usr/local/bin/codex",
            "/opt/homebrew/bin/codex",
            Path.Combine(_homeDirectory, ".npm-global", "bin", "codex"),
        ];
        return locations.FirstOrDefault(File.Exists) ?? throw new CliNotFoundException(
            "找不到 Codex CLI。請用 npm install -g @openai/codex 安裝，或把 CodexAgentOptions.CliPath 指到 codex 執行檔。");
    }

    private string? SearchPath(string fileName)
    {
        if (string.IsNullOrEmpty(_pathVariable))
        {
            return null;
        }

        return _pathVariable
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), fileName))
            .FirstOrDefault(File.Exists);
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;
}
