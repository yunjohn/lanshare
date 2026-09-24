using LanTransfer.Common.Models;
using LanTransfer.Core.Interfaces;
using LanTransfer.Storage.Database;
using Microsoft.Data.Sqlite;

namespace LanTransfer.Storage.Repositories;

/// <summary>
/// 传输仓储。分块完成状态以位图 BLOB 持久化（TransferChunks 一行 = 一个文件的全部 Chunk），
/// 因此 100 GB / 4 MiB = 25600 个 Chunk 只占 3200 字节，且查询 O(n/8)。
/// </summary>
public sealed class TransferRepository : ITransferRepository
{
    private readonly SqliteConnectionFactory _factory;
    private readonly DatabaseInitializer _initializer;

    public TransferRepository(SqliteConnectionFactory factory, DatabaseInitializer initializer)
    {
        _factory = factory;
        _initializer = initializer;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _initializer.InitializeAsync(cancellationToken);

    // ---------------------------------------------------------------- Transfers

    public async Task UpsertTransferAsync(TransferRecord record, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Transfers
                (TransferId, Direction, TransferType, RemoteDeviceId, RemoteDeviceName, LocalDeviceId,
                 State, TotalSize, TransferredSize, TotalFiles, CompletedFiles, RootName, DownloadRoot,
                 ErrorCode, ErrorMessage, CreatedAt, CompletedAt)
            VALUES
                ($id, $direction, $type, $remoteId, $remoteName, $localId,
                 $state, $totalSize, $transferred, $totalFiles, $completedFiles, $rootName, $downloadRoot,
                 $errorCode, $errorMessage, $createdAt, $completedAt)
            ON CONFLICT(TransferId) DO UPDATE SET
                Direction        = excluded.Direction,
                TransferType     = excluded.TransferType,
                RemoteDeviceId   = excluded.RemoteDeviceId,
                RemoteDeviceName = excluded.RemoteDeviceName,
                State            = excluded.State,
                TotalSize        = excluded.TotalSize,
                TransferredSize  = excluded.TransferredSize,
                TotalFiles       = excluded.TotalFiles,
                CompletedFiles   = excluded.CompletedFiles,
                RootName         = excluded.RootName,
                DownloadRoot     = COALESCE(excluded.DownloadRoot, Transfers.DownloadRoot),
                ErrorCode        = excluded.ErrorCode,
                ErrorMessage     = excluded.ErrorMessage,
                CompletedAt      = excluded.CompletedAt;
            """;

        command.Parameters.AddWithValue("$id", record.TransferId);
        command.Parameters.AddWithValue("$direction", (int)record.Direction);
        command.Parameters.AddWithValue("$type", (int)record.TransferType);
        command.Parameters.AddWithValue("$remoteId", record.RemoteDeviceId ?? string.Empty);
        command.Parameters.AddWithValue("$remoteName", record.RemoteDeviceName ?? string.Empty);
        command.Parameters.AddWithValue("$localId", record.LocalDeviceId ?? string.Empty);
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$totalSize", record.TotalSize);
        command.Parameters.AddWithValue("$transferred", record.TransferredSize);
        command.Parameters.AddWithValue("$totalFiles", record.TotalFiles);
        command.Parameters.AddWithValue("$completedFiles", record.CompletedFiles);
        command.Parameters.AddWithValue("$rootName", record.RootName ?? string.Empty);
        command.Parameters.AddWithValue("$downloadRoot", (object?)record.DownloadRoot ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)record.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)record.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt",
            record.CompletedAt.HasValue ? record.CompletedAt.Value.ToString("O") : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferRecord?> GetTransferAsync(string transferId,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TransferSelect + " WHERE TransferId = $id;";
        command.Parameters.AddWithValue("$id", transferId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTransfer(reader) : null;
    }

    public async Task<IReadOnlyList<TransferRecord>> GetRecentTransfersAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<TransferRecord>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TransferSelect + " ORDER BY CreatedAt DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadTransfer(reader));

        return result;
    }

    public async Task<IReadOnlyList<TransferRecord>> GetIncompleteTransfersAsync(
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<TransferRecord>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = TransferSelect +
                              " WHERE State IN ($pending,$queued,$waiting,$preparing,$transferring,$paused,$verifying,$failed)" +
                              " ORDER BY CreatedAt ASC;";

        command.Parameters.AddWithValue("$pending", (int)TransferState.Pending);
        command.Parameters.AddWithValue("$queued", (int)TransferState.Queued);
        command.Parameters.AddWithValue("$waiting", (int)TransferState.WaitingApproval);
        command.Parameters.AddWithValue("$preparing", (int)TransferState.Preparing);
        command.Parameters.AddWithValue("$transferring", (int)TransferState.Transferring);
        command.Parameters.AddWithValue("$paused", (int)TransferState.Paused);
        command.Parameters.AddWithValue("$verifying", (int)TransferState.Verifying);
        command.Parameters.AddWithValue("$failed", (int)TransferState.Failed);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadTransfer(reader));

        return result;
    }

    public async Task DeleteTransferAsync(string transferId, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM TransferChunks WHERE TransferId = $id;
            DELETE FROM TransferFiles  WHERE TransferId = $id;
            DELETE FROM Transfers      WHERE TransferId = $id;
            """;
        command.Parameters.AddWithValue("$id", transferId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM TransferChunks
            WHERE TransferId IN (SELECT TransferId FROM Transfers WHERE State IN ($completed,$rejected,$cancelled,$verificationFailed));
            DELETE FROM TransferFiles
            WHERE TransferId IN (SELECT TransferId FROM Transfers WHERE State IN ($completed,$rejected,$cancelled,$verificationFailed));
            DELETE FROM Transfers WHERE State IN ($completed,$rejected,$cancelled,$verificationFailed);
            """;

        command.Parameters.AddWithValue("$completed", (int)TransferState.Completed);
        command.Parameters.AddWithValue("$rejected", (int)TransferState.Rejected);
        command.Parameters.AddWithValue("$cancelled", (int)TransferState.Cancelled);
        command.Parameters.AddWithValue("$verificationFailed", (int)TransferState.VerificationFailed);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgeHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM TransferChunks
            WHERE TransferId IN (SELECT TransferId FROM Transfers WHERE CreatedAt < $cutoff);
            DELETE FROM TransferFiles
            WHERE TransferId IN (SELECT TransferId FROM Transfers WHERE CreatedAt < $cutoff);
            DELETE FROM Transfers WHERE CreatedAt < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", olderThan.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- TransferFiles

    public async Task UpsertFileAsync(TransferFileRecord file, CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TransferFiles
                (FileId, TransferId, FileIndex, RelativePath, FileName, FileSize, Sha256, ChunkSize,
                 TotalChunks, CompletedChunks, State, TargetPath, SourcePath, LastWriteTimeUtc)
            VALUES
                ($fileId, $transferId, $fileIndex, $relativePath, $fileName, $fileSize, $sha256, $chunkSize,
                 $totalChunks, $completedChunks, $state, $targetPath, $sourcePath, $lastWrite)
            ON CONFLICT(TransferId, FileIndex) DO UPDATE SET
                FileId          = excluded.FileId,
                RelativePath    = excluded.RelativePath,
                FileName        = excluded.FileName,
                FileSize        = excluded.FileSize,
                Sha256          = CASE WHEN excluded.Sha256 = '' THEN TransferFiles.Sha256 ELSE excluded.Sha256 END,
                ChunkSize       = excluded.ChunkSize,
                TotalChunks     = excluded.TotalChunks,
                CompletedChunks = excluded.CompletedChunks,
                State           = excluded.State,
                TargetPath      = COALESCE(excluded.TargetPath, TransferFiles.TargetPath),
                SourcePath      = COALESCE(excluded.SourcePath, TransferFiles.SourcePath),
                LastWriteTimeUtc = COALESCE(excluded.LastWriteTimeUtc, TransferFiles.LastWriteTimeUtc);
            """;

        command.Parameters.AddWithValue("$fileId", string.IsNullOrEmpty(file.FileId)
            ? Guid.NewGuid().ToString()
            : file.FileId);
        command.Parameters.AddWithValue("$transferId", file.TransferId);
        command.Parameters.AddWithValue("$fileIndex", file.FileIndex);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath ?? string.Empty);
        command.Parameters.AddWithValue("$fileName", file.FileName ?? string.Empty);
        command.Parameters.AddWithValue("$fileSize", file.FileSize);
        command.Parameters.AddWithValue("$sha256", file.Sha256 ?? string.Empty);
        command.Parameters.AddWithValue("$chunkSize", file.ChunkSize);
        command.Parameters.AddWithValue("$totalChunks", file.TotalChunks);
        command.Parameters.AddWithValue("$completedChunks", file.CompletedChunks);
        command.Parameters.AddWithValue("$state", (int)file.State);
        command.Parameters.AddWithValue("$targetPath", (object?)file.TargetPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourcePath", (object?)file.SourcePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastWrite",
            file.LastWriteTimeUtc.HasValue ? file.LastWriteTimeUtc.Value.ToString("O") : DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferFileRecord?> GetFileAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = FileSelect + " WHERE TransferId = $id AND FileIndex = $index;";
        command.Parameters.AddWithValue("$id", transferId);
        command.Parameters.AddWithValue("$index", fileIndex);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadFile(reader) : null;
    }

    public async Task<IReadOnlyList<TransferFileRecord>> GetFilesAsync(string transferId,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<TransferFileRecord>();
        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = FileSelect + " WHERE TransferId = $id ORDER BY FileIndex ASC;";
        command.Parameters.AddWithValue("$id", transferId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(ReadFile(reader));

        return result;
    }

    // ---------------------------------------------------------------- Chunk bitmap

    public async Task MarkChunkCompletedAsync(string transferId, int fileIndex, int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var bitmap = await LoadBitmapAsync(connection, (SqliteTransaction)transaction, transferId, fileIndex,
            cancellationToken).ConfigureAwait(false);

        var requiredBytes = (chunkIndex >> 3) + 1;
        if (bitmap.Length < requiredBytes)
        {
            var grown = new byte[requiredBytes];
            Buffer.BlockCopy(bitmap, 0, grown, 0, bitmap.Length);
            bitmap = grown;
        }

        bitmap[chunkIndex >> 3] |= (byte)(1 << (chunkIndex & 7));

        await SaveBitmapAsync(connection, (SqliteTransaction)transaction, transferId, fileIndex, bitmap,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<int>> GetCompletedChunksAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var bitmap = await LoadBitmapAsync(connection, null, transferId, fileIndex, cancellationToken)
            .ConfigureAwait(false);

        var result = new List<int>();
        for (var i = 0; i < bitmap.Length * 8; i++)
        {
            if ((bitmap[i >> 3] & (1 << (i & 7))) != 0)
                result.Add(i);
        }

        return result;
    }

    public async Task<int> GetCompletedChunkCountAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default)
    {
        var chunks = await GetCompletedChunksAsync(transferId, fileIndex, cancellationToken).ConfigureAwait(false);
        return chunks.Count;
    }

    public async Task ClearChunksAsync(string transferId, int fileIndex,
        CancellationToken cancellationToken = default)
    {
        await _initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM TransferChunks WHERE TransferId = $id AND FileIndex = $index;";
        command.Parameters.AddWithValue("$id", transferId);
        command.Parameters.AddWithValue("$index", fileIndex);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> LoadBitmapAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string transferId, int fileIndex, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Bitmap FROM TransferChunks WHERE TransferId = $id AND FileIndex = $index;";
        command.Parameters.AddWithValue("$id", transferId);
        command.Parameters.AddWithValue("$index", fileIndex);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as byte[] ?? [];
    }

    private static async Task SaveBitmapAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string transferId, int fileIndex, byte[] bitmap, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TransferChunks (TransferId, FileIndex, TotalChunks, Bitmap, UpdatedAt)
            VALUES ($id, $index, $total, $bitmap, $updatedAt)
            ON CONFLICT(TransferId, FileIndex) DO UPDATE SET
                Bitmap = excluded.Bitmap,
                TotalChunks = excluded.TotalChunks,
                UpdatedAt = excluded.UpdatedAt;
            """;

        command.Parameters.AddWithValue("$id", transferId);
        command.Parameters.AddWithValue("$index", fileIndex);
        command.Parameters.AddWithValue("$total", bitmap.Length * 8);
        command.Parameters.AddWithValue("$bitmap", bitmap);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 读取映射

    private const string TransferSelect = """
        SELECT TransferId, Direction, TransferType, RemoteDeviceId, RemoteDeviceName, LocalDeviceId,
               State, TotalSize, TransferredSize, TotalFiles, CompletedFiles, RootName, DownloadRoot,
               ErrorCode, ErrorMessage, CreatedAt, CompletedAt
        FROM Transfers
        """;

    private const string FileSelect = """
        SELECT FileId, TransferId, FileIndex, RelativePath, FileName, FileSize, Sha256, ChunkSize,
               TotalChunks, CompletedChunks, State, TargetPath, SourcePath, LastWriteTimeUtc
        FROM TransferFiles
        """;

    private static TransferRecord ReadTransfer(SqliteDataReader reader) => new()
    {
        TransferId = reader.GetString(0),
        Direction = (TransferDirection)reader.GetInt32(1),
        TransferType = (TransferType)reader.GetInt32(2),
        RemoteDeviceId = reader.GetString(3),
        RemoteDeviceName = reader.GetString(4),
        LocalDeviceId = reader.GetString(5),
        State = (TransferState)reader.GetInt32(6),
        TotalSize = reader.GetInt64(7),
        TransferredSize = reader.GetInt64(8),
        TotalFiles = reader.GetInt32(9),
        CompletedFiles = reader.GetInt32(10),
        RootName = reader.GetString(11),
        DownloadRoot = reader.IsDBNull(12) ? null : reader.GetString(12),
        ErrorCode = reader.IsDBNull(13) ? null : reader.GetString(13),
        ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14),
        CreatedAt = DeviceRepository.ParseDate(reader.GetString(15)),
        CompletedAt = reader.IsDBNull(16) ? null : DeviceRepository.ParseDate(reader.GetString(16)),
    };

    private static TransferFileRecord ReadFile(SqliteDataReader reader) => new()
    {
        FileId = reader.GetString(0),
        TransferId = reader.GetString(1),
        FileIndex = reader.GetInt32(2),
        RelativePath = reader.GetString(3),
        FileName = reader.GetString(4),
        FileSize = reader.GetInt64(5),
        Sha256 = reader.GetString(6),
        ChunkSize = reader.GetInt32(7),
        TotalChunks = reader.GetInt32(8),
        CompletedChunks = reader.GetInt32(9),
        State = (TransferState)reader.GetInt32(10),
        TargetPath = reader.IsDBNull(11) ? null : reader.GetString(11),
        SourcePath = reader.IsDBNull(12) ? null : reader.GetString(12),
        LastWriteTimeUtc = reader.IsDBNull(13) ? null : DeviceRepository.ParseDate(reader.GetString(13)),
    };
}
