using System.Net;
using System.Text;
using System.Text.Json;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Network.Protocol;
using Xunit;

namespace LanTransfer.Network.Tests;

/// <summary>协议序列化：字段名固定为 camelCase，未知字段容忍，损坏 JSON 不抛异常。</summary>
public class ProtocolJsonTests
{
    [Fact]
    public void HealthResponse_SerializesWithCamelCaseNames()
    {
        var json = Encoding.UTF8.GetString(ProtocolJson.Serialize(new HealthResponse
        {
            ProtocolVersion = 1,
            AppVersion = "1.0.0",
        }));

        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Contains("\"appVersion\":\"1.0.0\"", json);
        Assert.Contains("\"status\":\"ok\"", json);
    }

    [Fact]
    public void DiscoveryMessage_RoundTrips()
    {
        var original = new DiscoveryMessage
        {
            Type = DiscoveryMessageTypes.DiscoverResponse,
            DeviceId = "dev-1",
            DeviceName = "PC-A",
            Port = 39521,
            Version = AppConstants.ProtocolVersion,
        };

        var restored = ProtocolJson.Deserialize<DiscoveryMessage>(ProtocolJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Type, restored!.Type);
        Assert.Equal(original.DeviceId, restored.DeviceId);
        Assert.Equal(original.Port, restored.Port);
        Assert.Equal(AppConstants.ProtocolName, restored.Protocol);
    }

    [Fact]
    public void CreateTransferRequest_RoundTripsAllFields()
    {
        var original = new CreateTransferRequest
        {
            TransferType = "folder",
            TransferId = "t-1",
            FileName = "a.txt",
            RelativePath = "docs\\a.txt",
            FileSize = 123456789L,
            Sha256 = new string('a', 64),
            ChunkSize = AppConstants.DefaultChunkSize,
            TotalChunks = 30,
            FileIndex = 2,
            TotalFiles = 5,
            TotalSize = 987654321L,
            RootName = "docs",
            ConflictPolicy = "rename",
            SyncPairId = "pair-1",
            LastWriteTimeUtc = DateTimeOffset.Parse("2026-09-24T15:45:01+08:00"),
        };

        var restored = ProtocolJson.Deserialize<CreateTransferRequest>(ProtocolJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal("folder", restored!.TransferType);
        Assert.Equal("docs\\a.txt", restored.RelativePath);
        Assert.Equal(123456789L, restored.FileSize);
        Assert.Equal(30, restored.TotalChunks);
        Assert.Equal("pair-1", restored.SyncPairId);
        Assert.Equal(original.LastWriteTimeUtc, restored.LastWriteTimeUtc);
    }

    [Fact]
    public void TransferStatusResponse_RoundTripsChunkBitmap()
    {
        var original = new TransferStatusResponse
        {
            TransferId = "t-1",
            FileId = "f-1",
            State = TransferState.Transferring.ToWireString(),
            CompletedChunks = new List<int> { 0, 1, 5, 9 },
            ReceivedBytes = 4096,
            FileSize = 8192,
            ChunkSize = 1024,
            TotalChunks = 8,
        };

        var restored = ProtocolJson.Deserialize<TransferStatusResponse>(ProtocolJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(new[] { 0, 1, 5, 9 }, restored!.CompletedChunks);
        Assert.Equal("transferring", restored.State);
        Assert.Equal(TransferState.Transferring, TransferStateExtensions.FromWireString(restored.State));
    }

    [Fact]
    public void SyncManifestEntry_OmitsNullSha256()
    {
        var json = Encoding.UTF8.GetString(ProtocolJson.Serialize(new SyncManifestEntry
        {
            RelativePath = "a.txt",
            Sha256 = null,
            Deleted = true,
        }));

        Assert.DoesNotContain("sha256", json);
        Assert.Contains("\"deleted\":true", json);
    }

    [Fact]
    public void ApiErrorResponse_OmitsNullDetails()
    {
        var json = Encoding.UTF8.GetString(ProtocolJson.Serialize(
            ApiErrorResponse.Create(ErrorCodes.PathEscapeDetected, "路径逃逸")));

        Assert.Contains("\"success\":false", json);
        Assert.Contains(ErrorCodes.PathEscapeDetected, json);
        Assert.DoesNotContain("details", json);
    }

    [Fact]
    public void Deserialize_ToleratesUnknownProperties()
    {
        const string json = """
            {"success":true,"status":"ok","protocolVersion":1,"appVersion":"1.0.0",
             "serverTimeUtc":"2026-09-24T07:45:01+00:00","futureField":"ignored"}
            """;

        var health = ProtocolJson.Deserialize<HealthResponse>(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(health);
        Assert.True(health!.Success);
        Assert.Equal("ok", health.Status);
    }

    [Fact]
    public void Deserialize_AcceptsCaseInsensitivePropertyNames()
    {
        const string json = """{"DeviceId":"d1","DeviceName":"PC","Port":39521}""";

        var message = ProtocolJson.Deserialize<DiscoveryMessage>(Encoding.UTF8.GetBytes(json));

        Assert.NotNull(message);
        Assert.Equal("d1", message!.DeviceId);
        Assert.Equal("PC", message.DeviceName);
    }

    [Fact]
    public void Deserialize_ReturnsDefaultOnMalformedJson()
    {
        Assert.Null(ProtocolJson.Deserialize<HealthResponse>(Encoding.UTF8.GetBytes("{ not json")));
        Assert.Null(ProtocolJson.Deserialize<HealthResponse>(Encoding.UTF8.GetBytes("[]")));
    }

    [Fact]
    public void AllErrorCodes_AreUniqueAndUpperSnakeCase()
    {
        var codes = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[A-Z][A-Z0-9_]*$", code));
    }

    [Fact]
    public void ProtocolJson_RoundTripsNonAsciiMessages()
    {
        var original = ApiErrorResponse.Create(ErrorCodes.BadRequest, "请求无效：路径试图逃逸接收目录");

        var restored = ProtocolJson.Deserialize<ApiErrorResponse>(ProtocolJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Message, restored!.Message);
        Assert.Equal(ErrorCodes.BadRequest, restored.ErrorCode);
    }

    [Fact]
    public void Options_AreSharedAndNotIndented()
    {
        Assert.False(ProtocolJson.Options.WriteIndented);
        Assert.True(ProtocolJson.Options.PropertyNameCaseInsensitive);
        Assert.Same(ProtocolJson.Options, ProtocolJson.Options);
    }

    [Fact]
    public void JsonSerializerOptions_IsReusable()
    {
        // 确保序列化器是线程安全的静态实例（避免每次 new 造成性能问题）
        var results = new string[16];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = Encoding.UTF8.GetString(ProtocolJson.Serialize(new HealthResponse
            {
                AppVersion = "1.0.0",
            }));
        });

        Assert.All(results, r => Assert.Contains("\"appVersion\":\"1.0.0\"", r));
    }

    [Fact]
    public void DeviceInfo_SerializationExcludesComputedProperties()
    {
        var json = JsonSerializer.Serialize(new DeviceInfo
        {
            DeviceId = "d1",
            DeviceName = "PC",
            TrustState = TrustState.Trusted,
        });

        Assert.DoesNotContain("isTrusted", json);
        Assert.DoesNotContain("endPoint", json);
    }
}
