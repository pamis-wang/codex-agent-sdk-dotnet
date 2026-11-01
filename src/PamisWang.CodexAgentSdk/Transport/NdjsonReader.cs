using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PamisWang.CodexAgentSdk.Transport;

/// <summary>
/// 從 app-server 的 stdout 切出一行一則的 JSON。對應 Python SDK <c>CodexClient</c> 的 <c>_read_message</c>。
/// </summary>
/// <remarks>
/// 讀進來的是任意切開的位元組，一行可能跨好幾次讀取，切點也可能落在中文字的中間。
/// 這裡在位元組層找換行，湊滿一整行才解碼與解析，所以不會在切點把字元弄壞。
/// </remarks>
internal static class NdjsonReader
{
    public static async IAsyncEnumerable<JsonElement> ReadAsync(
        PipeReader reader,
        int maxLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<JsonElement> messages = [];
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            messages.Clear();
            while (TryReadLine(ref buffer, out var line))
            {
                EnsureWithinLimit(line.Length, maxLineBytes);
                if (ParseLine(line) is { } message)
                {
                    messages.Add(message);
                }
            }

            // 還沒等到換行的部分也算進上限，不然沒有換行的輸出會一直累積
            EnsureWithinLimit(buffer.Length, maxLineBytes);

            if (result.IsCompleted)
            {
                // 剩下的尾巴能解析，就是少了最後換行的訊息；解析不了就是寫到一半被切斷，救不回來
                if (TryParseTail(buffer) is { } last)
                {
                    messages.Add(last);
                }

                buffer = buffer.Slice(buffer.End);
            }

            // 先交還已處理的部分再交出訊息：呼叫端讀到一半停下來，reader 也不會卡在沒交還的狀態
            reader.AdvanceTo(buffer.Start, buffer.End);
            foreach (var message in messages)
            {
                yield return message;
            }

            if (result.IsCompleted)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// 解析完整的一行。空行與不是 JSON 物件的行回傳 <see langword="null"/>；
    /// 看起來是 JSON 卻解析失敗就丟例外：行已經完整，不會有後續資料把它補齊。
    /// </summary>
    internal static JsonElement? ParseLine(ReadOnlySequence<byte> line)
    {
        // 一整行可以去掉前後空白（例如 CRLF 的 \r）；只有讀到一半的片段不能動
        var bytes = line.ToArray();
        var trimmed = bytes.AsMemory().Trim(" \t\r\n"u8);
        if (trimmed.IsEmpty || trimmed.Span[0] != (byte)'{')
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            var text = Encoding.UTF8.GetString(trimmed.Span);
            throw new CodexAgentException($"app-server 輸出的 JSON-RPC 訊息解析失敗：{Truncate(text)}", e);
        }
    }

    private static JsonElement? TryParseTail(ReadOnlySequence<byte> tail)
    {
        try
        {
            return ParseLine(tail);
        }
        catch (CodexAgentException)
        {
            return null;
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        if (buffer.PositionOf((byte)'\n') is not { } newline)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, newline);
        buffer = buffer.Slice(buffer.GetPosition(1, newline));
        return true;
    }

    private static void EnsureWithinLimit(long length, int maxLineBytes)
    {
        if (length > maxLineBytes)
        {
            throw new CodexAgentException(
                $"app-server 的單一則訊息超過大小上限 {maxLineBytes} 位元組（已讀 {length} 位元組）；可以調高 CodexAgentOptions.MaxBufferSize。");
        }
    }

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
