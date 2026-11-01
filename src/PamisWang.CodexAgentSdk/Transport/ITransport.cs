using System.Text.Json;

namespace PamisWang.CodexAgentSdk.Transport;

/// <summary>跟 app-server 收發一行一則的 JSON。正式環境是子程序；測試換成記憶體裡的假 server。</summary>
internal interface ITransport : IAsyncDisposable
{
    /// <summary>stderr 最後幾行，放進連線中斷時的例外訊息。</summary>
    string StandardErrorTail { get; }

    /// <summary>啟動 app-server。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>寫入一則訊息（UTF-8 的 JSON，不含換行）；會補上換行。可以同時被多個呼叫端使用。</summary>
    Task WriteMessageAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken);

    /// <summary>讀取 app-server 送來的每一則訊息，直到 stdout 關閉。只能有一個讀取者。</summary>
    IAsyncEnumerable<JsonElement> ReadMessagesAsync(CancellationToken cancellationToken);
}
