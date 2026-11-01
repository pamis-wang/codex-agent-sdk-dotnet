using System.Text.Json;
using System.Text.Json.Serialization;

namespace PamisWang.CodexAgentSdk.Protocol;

/// <summary>
/// 協定裡的字串列舉，例如 <see cref="TurnStatus"/>。不用 C# 的 enum：協定常加新的值，
/// enum 遇到不認得的值會解析失敗，這裡的型別則保留原字串，照常可以比較與印出。
/// </summary>
/// <typeparam name="TSelf">列舉型別本身。</typeparam>
public interface IStringValue<TSelf>
    where TSelf : struct, IStringValue<TSelf>
{
    /// <summary>協定上的字串值。</summary>
    string Value { get; }

    /// <summary>以協定上的字串值建立，不認得的值也接受。</summary>
    static abstract TSelf FromValue(string value);
}

/// <summary>以字串讀寫 <see cref="IStringValue{TSelf}"/>，也能當 JSON 物件的鍵。</summary>
/// <typeparam name="T">列舉型別。</typeparam>
public sealed class StringValueConverter<T> : JsonConverter<T>
    where T : struct, IStringValue<T>
{
    /// <inheritdoc/>
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? T.FromValue(reader.GetString()!)
            : throw new JsonException($"{typeof(T).Name} 應該是字串，卻是 {reader.TokenType}。");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    /// <inheritdoc/>
    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        T.FromValue(reader.GetString()!);

    /// <inheritdoc/>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}
