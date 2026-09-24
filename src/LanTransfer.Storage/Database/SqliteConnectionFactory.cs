using LanTransfer.Common.Constants;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Storage.Database;

/// <summary>
/// SQLite 连接工厂。数据库位于 %LOCALAPPDATA%\LanTransfer\lan-transfer.db，
/// 启用 WAL 以支持读写并发。
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(ILogger<SqliteConnectionFactory> logger, string? databasePath = null)
    {
        _logger = logger;
        DatabasePath = databasePath ?? AppPaths.DatabaseFile;

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 刻意不开 Cache=Shared：共享缓存下锁冲突返回的是 SQLITE_LOCKED(6/262)，
            // 该错误**不遵守 busy_timeout**，会立刻抛 SqliteException；
            // 私有缓存 + WAL 时冲突是可等待的 SQLITE_BUSY(5)，会按 DefaultTimeout 重试。
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 不能把半初始化的连接交出去（也不该留在连接池里）：取消/加锁/磁盘异常都会走到这里
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>检测数据库是否可用（损坏时返回 false，调用方需降级处理）。</summary>
    public async Task<bool> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库完整性检查失败: {Path}", DatabasePath);
            return false;
        }
    }
}
