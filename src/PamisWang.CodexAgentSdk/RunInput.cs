namespace PamisWang.CodexAgentSdk;

/// <summary>
/// turn 的輸入：一段文字、一項或多項 <see cref="InputItem"/>，或一則 <see cref="ExternalMessage"/>。對應 Python SDK 的 <c>RunInput</c>。
/// 可以直接傳字串、<see cref="InputItem"/>、陣列或清單，會自動轉換。
/// </summary>
public sealed class RunInput
{
    private RunInput(IReadOnlyList<InputItem>? items, ExternalMessage? externalMessage)
    {
        Items = items;
        ExternalMessage = externalMessage;
    }

    /// <summary>一般輸入；是 <see cref="ExternalMessage"/> 時為 <see langword="null"/>。</summary>
    public IReadOnlyList<InputItem>? Items { get; }

    /// <summary>外部訊息；是一般輸入時為 <see langword="null"/>。</summary>
    public ExternalMessage? ExternalMessage { get; }

    /// <summary>以文字建立，等同 <see cref="TextInput"/>。</summary>
    public static implicit operator RunInput(string text) => FromText(text);

    /// <summary>以一項輸入建立。</summary>
    public static implicit operator RunInput(InputItem item) => FromItem(item);

    /// <summary>以多項輸入建立。</summary>
    public static implicit operator RunInput(InputItem[] items) => FromItems(items);

    /// <summary>以多項輸入建立。</summary>
    public static implicit operator RunInput(List<InputItem> items) => FromItems(items);

    /// <summary>以外部訊息建立。</summary>
    public static implicit operator RunInput(ExternalMessage message) => FromExternalMessage(message);

    /// <summary>以文字建立，等同 <see cref="TextInput"/>。</summary>
    public static RunInput FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new([new TextInput(text)], null);
    }

    /// <summary>以一項輸入建立。</summary>
    public static RunInput FromItem(InputItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new([item], null);
    }

    /// <summary>以多項輸入建立。</summary>
    public static RunInput FromItems(IEnumerable<InputItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new([.. items], null);
    }

    /// <summary>以外部訊息建立。</summary>
    public static RunInput FromExternalMessage(ExternalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(null, message);
    }
}
