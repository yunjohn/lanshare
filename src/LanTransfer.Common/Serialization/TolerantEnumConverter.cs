using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanTransfer.Common.Serialization;

/// <summary>
/// 枚举 ↔ 字符串（让 settings.json 可读），并且**容错**：
/// 遇到无法识别的值回退到 <c>default</c>（约定为最保守的那个值），而不是抛异常。
///
/// 为什么不用 <see cref="JsonStringEnumConverter"/>：它遇到未知字符串会抛
/// <see cref="JsonException"/>，而 SettingsService 把「反序列化异常」视为配置文件损坏，
/// 会把整份配置重置为默认值——用户手改配置写错一个词就丢光所有设置，代价太大。
/// </summary>
public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) ? named : default;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number)
                    ? (T)Enum.ToObject(typeof(T), number)
                    : default;

            default:
                // 类型完全不对（对象/数组等）：跳过该值，同样不让整份配置解析失败
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
