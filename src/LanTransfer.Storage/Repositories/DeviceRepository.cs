using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Storage.Database;
using Microsoft.Data.Sqlite;

namespace LanTransfer.Storage.Repositories;

/// <summary>设备仓储。</summary>
public sealed class DeviceRepository : IDeviceRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseInitializer _initializer;

    public DeviceRepository(SqliteConnectionFactory factory, DatabaseInitializer initializer)
    {
        _factory = factory;
        _initializer = initializer;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _initializer.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<DeviceInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<DeviceInfo>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceId, DeviceName, IpAddress, Port, AppVersion, ProtocolVersion,
                   CertificateFingerprint, TrustState, FirstSeen, LastSeen, IsManual
            FROM Devices;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadDevice(reader));

        return result;
    }

    public async Task<DeviceInfo?> GetAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DeviceId, DeviceName, IpAddress, Port, AppVersion, ProtocolVersion,
                   CertificateFingerprint, TrustState, FirstSeen, LastSeen, IsManual
            FROM Devices WHERE DeviceId = $id;
            """;
        command.Parameters.AddWithValue("$id", deviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDevice(reader) : null;
    }

    public async Task UpsertAsync(DeviceInfo device, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Devices
                (DeviceId, DeviceName, IpAddress, Port, AppVersion, ProtocolVersion,
                 CertificateFingerprint, TrustState, FirstSeen, LastSeen, IsManual)
            VALUES
                ($id, $name, $ip, $port, $app, $proto, $fingerprint, $trust, $first, $last, $manual)
            ON CONFLICT(DeviceId) DO UPDATE SET
                DeviceName             = excluded.DeviceName,
                IpAddress              = excluded.IpAddress,
                Port                   = excluded.Port,
                AppVersion             = excluded.AppVersion,
                ProtocolVersion        = excluded.ProtocolVersion,
                CertificateFingerprint = COALESCE(excluded.CertificateFingerprint, Devices.CertificateFingerprint),
                TrustState             = excluded.TrustState,
                LastSeen               = excluded.LastSeen,
                IsManual               = excluded.IsManual;
            """;

        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$name", device.DeviceName ?? string.Empty);
        command.Parameters.AddWithValue("$ip", device.IpAddress ?? string.Empty);
        command.Parameters.AddWithValue("$port", device.Port);
        command.Parameters.AddWithValue("$app", device.AppVersion ?? string.Empty);
        command.Parameters.AddWithValue("$proto", device.ProtocolVersion);
        command.Parameters.AddWithValue("$fingerprint", (object?)device.CertificateFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$trust", (int)device.TrustState);
        command.Parameters.AddWithValue("$first", device.FirstSeen.ToString("O"));
        command.Parameters.AddWithValue("$last", device.LastSeen.ToString("O"));
        command.Parameters.AddWithValue("$manual", device.IsManual ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateTrustAsync(string deviceId, TrustState trustState, string? fingerprint,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Devices
            SET TrustState = $trust,
                CertificateFingerprint = COALESCE($fingerprint, CertificateFingerprint)
            WHERE DeviceId = $id;
            """;

        command.Parameters.AddWithValue("$trust", (int)trustState);
        command.Parameters.AddWithValue("$fingerprint", (object?)fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", deviceId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Devices WHERE DeviceId = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DeviceInfo ReadDevice(SqliteDataReader reader) => new()
    {
        DeviceId = reader.GetString(0),
        DeviceName = reader.GetString(1),
        IpAddress = reader.GetString(2),
        Port = reader.GetInt32(3),
        AppVersion = reader.GetString(4),
        ProtocolVersion = reader.GetInt32(5),
        CertificateFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
        TrustState = (TrustState)reader.GetInt32(7),
        FirstSeen = ParseDate(reader.GetString(8)),
        LastSeen = ParseDate(reader.GetString(9)),
        IsManual = reader.GetInt32(10) != 0,
        OnlineState = OnlineState.Offline,
    };

    internal static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
