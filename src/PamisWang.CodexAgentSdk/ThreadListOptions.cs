using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>列出已儲存 thread 的篩選與分頁。對應 Python SDK <c>thread_list()</c> 的參數。</summary>
public sealed record ThreadListOptions
{
    /// <summary><see langword="true"/> 只列封存的，<see langword="false"/> 只列未封存的；不給兩者都列。</summary>
    public bool? Archived { get; init; }

    /// <summary>上一頁回應的 <c>nextCursor</c>。</summary>
    public string? Cursor { get; init; }

    /// <summary>只列這個（或這些）工作目錄的 thread。</summary>
    public ThreadListCwdFilter? WorkingDirectory { get; init; }

    /// <summary>每頁筆數，上限 100。</summary>
    public long? Limit { get; init; }

    /// <summary>只列這些模型供應者的 thread。</summary>
    public IReadOnlyList<string>? ModelProviders { get; init; }

    /// <summary>搜尋字詞。</summary>
    public string? SearchTerm { get; init; }

    /// <summary>只列這個分區的 thread。</summary>
    public string? SectionId { get; init; }

    /// <summary>排序方向。</summary>
    public SortDirection? SortDirection { get; init; }

    /// <summary>排序依據。</summary>
    public ThreadSortKey? SortKey { get; init; }

    /// <summary>只列這些來源的 thread。</summary>
    public IReadOnlyList<ThreadSourceKind>? SourceKinds { get; init; }

    /// <summary>只查本機狀態資料庫，不掃描檔案。</summary>
    public bool? UseStateDbOnly { get; init; }
}
