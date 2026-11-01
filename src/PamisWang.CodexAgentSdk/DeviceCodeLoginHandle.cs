using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 進行中的 ChatGPT device code 登入：使用者在任何裝置開啟 <see cref="VerificationUrl"/> 並輸入 <see cref="UserCode"/>。
/// 對應 Python SDK 的 <c>AsyncDeviceCodeLoginHandle</c>。
/// </summary>
public sealed class DeviceCodeLoginHandle
{
    private readonly AppServerConnection _connection;

    internal DeviceCodeLoginHandle(AppServerConnection connection, string loginId, string verificationUrl, string userCode)
    {
        _connection = connection;
        LoginId = loginId;
        VerificationUrl = verificationUrl;
        UserCode = userCode;
    }

    /// <summary>這次登入的 ID。</summary>
    public string LoginId { get; }

    /// <summary>要開啟的驗證網址。</summary>
    public string VerificationUrl { get; }

    /// <summary>要在驗證網頁輸入的代碼。</summary>
    public string UserCode { get; }

    /// <summary>等這次登入完成；<see cref="AccountLoginCompletedNotification.Success"/> 表示是否成功。</summary>
    public Task<AccountLoginCompletedNotification> WaitAsync(CancellationToken cancellationToken = default) =>
        _connection.WaitForLoginCompletedAsync(LoginId, cancellationToken);

    /// <summary>取消這次登入。</summary>
    public Task<CancelLoginAccountResponse> CancelAsync(CancellationToken cancellationToken = default) =>
        _connection.AccountLoginCancelAsync(LoginId, cancellationToken);
}
