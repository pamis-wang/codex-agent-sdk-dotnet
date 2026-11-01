using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PamisWang.CodexAgentSdk.Protocol;

/// <summary>
/// 協定裡沒有辨識欄位的聯集，例如 <see cref="AskForApproval"/> 可能是字串 <c>"on-request"</c>，也可能是 <c>{"granular": {...}}</c>。
/// 保留原始 JSON；字串值用靜態成員比較（<c>value == AskForApproval.OnRequest</c>），其他變體用產生的 <c>As…()</c> 取出。
/// </summary>
public abstract class JsonUnion : IEquatable<JsonUnion>
{
    /// <summary>以原始 JSON 建立。</summary>
    protected JsonUnion(JsonElement raw)
    {
        Raw = raw;
    }

    /// <summary>原始 JSON。</summary>
    public JsonElement Raw { get; }

    /// <summary>值是字串時取出字串，否則是 <see langword="null"/>。</summary>
    public string? StringValue => Raw.ValueKind == JsonValueKind.String ? Raw.GetString() : null;

    /// <summary>兩個值的型別相同、JSON 內容相同。</summary>
    public static bool operator ==(JsonUnion? left, JsonUnion? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>兩個值的型別或 JSON 內容不同。</summary>
    public static bool operator !=(JsonUnion? left, JsonUnion? right) => !(left == right);

    /// <inheritdoc/>
    public bool Equals(JsonUnion? other) =>
        other is not null && other.GetType() == GetType() && JsonElement.DeepEquals(Raw, other.Raw);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as JsonUnion);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(GetType(), Raw.ValueKind, StringValue);

    /// <summary>原始 JSON 的文字。</summary>
    public override string ToString() => Raw.GetRawText();

    /// <summary>字串的 JSON 值。</summary>
    protected static JsonElement StringElement(string value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.String);

    /// <summary>整數的 JSON 值。</summary>
    protected static JsonElement Int64Element(long value) =>
        JsonSerializer.SerializeToElement(value, ProtocolJsonContext.Default.Int64);

    /// <summary>把協定型別的值轉成 JSON。</summary>
    protected static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());

    /// <summary>JSON 符合 <typeparamref name="T"/> 時取出，否則是 <see langword="null"/>。</summary>
    protected T? As<T>()
        where T : class
    {
        if (Raw.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return null;
        }

        try
        {
            return Raw.Deserialize(TypeInfo<T>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>?)ProtocolJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new InvalidOperationException($"ProtocolJsonContext 沒有註冊 {typeof(T)}。");
}

/// <summary>讓 <see cref="JsonUnionConverter{T}"/> 能從 JSON 建立聯集。</summary>
/// <typeparam name="TSelf">聯集型別本身。</typeparam>
public interface IJsonUnion<TSelf>
    where TSelf : JsonUnion, IJsonUnion<TSelf>
{
    /// <summary>從原始 JSON 建立。</summary>
    static abstract TSelf FromJson(JsonElement json);
}

/// <summary>原樣讀寫 <see cref="JsonUnion"/> 的 JSON。</summary>
/// <typeparam name="T">聯集型別。</typeparam>
public sealed class JsonUnionConverter<T> : JsonConverter<T>
    where T : JsonUnion, IJsonUnion<T>
{
    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return T.FromJson(document.RootElement);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        value.Raw.WriteTo(writer);
}
