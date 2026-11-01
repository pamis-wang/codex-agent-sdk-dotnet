namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 從 initialize 回應的 <c>userAgent</c> 取出的伺服器資訊。對應 Python SDK <c>InitializeResponse.serverInfo</c>。
/// </summary>
/// <param name="Name">伺服器名稱，例如 <c>codex_cli_rs</c>。</param>
/// <param name="Version"><c>userAgent</c> 裡斜線後面的整段，可能帶平台資訊。</param>
/// <param name="RuntimeVersion">Codex CLI 的版本號，例如 <c>0.166.0</c>；沒有版本號的開發版是 <c>0.0.0</c>。</param>
public sealed record CodexServerInfo(string Name, string Version, string RuntimeVersion);
