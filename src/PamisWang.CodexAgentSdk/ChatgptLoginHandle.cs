using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>進行中的 ChatGPT 瀏覽器登入。對應 Python SDK 的 <c>AsyncChatgptLoginHandle</c>。</summary>
public sealed class ChatgptLoginHandle
{
    private readonly AppServerConnection _connection;

    internal ChatgptLoginHandle(AppServerConnection connection, string loginId, string authUrl)
    {
        _connection = connection;
        LoginId = loginId;
        AuthUrl = authUrl;
    }

    /// <summary>這次登入的 ID。</summary>
    public string LoginId { get; }

    /// <summary>要在瀏覽器開啟的登入網址。</summary>
    public string AuthUrl { get; }

    /// <summary>等這次登入完成；<see cref="AccountLoginCompletedNotification.Success"/> 表示是否成功。</summary>
    public Task<AccountLoginCompletedNotification> WaitAsync(CancellationToken cancellationToken = default) =>
        _connection.WaitForLoginCompletedAsync(LoginId, cancellationToken);

    /// <summary>取消這次登入。</summary>
    public Task<CancelLoginAccountResponse> CancelAsync(CancellationToken cancellationToken = default) =>
        _connection.AccountLoginCancelAsync(LoginId, cancellationToken);
}
