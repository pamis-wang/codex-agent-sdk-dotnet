namespace PamisWang.CodexAgentSdk;

/// <summary>thread 與 turn 的檔案系統存取等級。對應 Python SDK 的 <c>Sandbox</c>。</summary>
public enum Sandbox
{
    /// <summary>只能讀檔，不能寫入。</summary>
    ReadOnly,

    /// <summary>可以在工作區與設定的可寫目錄內寫入；已記錄信任決定的專案通常用這個。</summary>
    WorkspaceWrite,

    /// <summary>不限制檔案系統存取。</summary>
    FullAccess,
}

/// <summary>核准提升權限請求的方式。對應 Python SDK 的 <c>ApprovalMode</c>。</summary>
public enum ApprovalMode
{
    /// <summary>需要時發出核准請求，先由 Codex 自動審查。開新 thread 時的預設值。</summary>
    AutoReview,

    /// <summary>一律不核准，不發出核准請求。</summary>
    DenyAll,

    /// <summary>
    /// 需要時發出核准請求，交給 <see cref="CodexAgentOptions.ServerRequestHandler"/> 決定，不經過自動審查。
    /// Python SDK 沒有這個模式；對應協定的 <c>on-request</c> 加上審查者 <c>user</c>。
    /// </summary>
    User,
}
