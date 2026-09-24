using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanTransfer.Network.Protocol;

/// <summary>协议序列化配置。所有 DTO 均使用显式 JsonPropertyName，与命名策略无关。</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
