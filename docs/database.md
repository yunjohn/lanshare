# LAN Transfer 数据库设计

> 引擎：SQLite 3（`Microsoft.Data.Sqlite` 10.0.0）· 文件：`%LOCALAPPDATA%\LanTransfer\lan-transfer.db`
> 无 ORM，手写 SQL，便于精确控制索引与批量写入。

---

## 1. 连接与调优

```csharp
new SqliteConnectionStringBuilder
{
    DataSource = DatabasePath,          // %LOCALAPPDATA%\LanTransfer\lan-transfer.db
    Mode       = SqliteOpenMode.ReadWriteCreate,
    Cache      = SqliteCacheMode.Shared,
    Pooling    = true,
    DefaultTimeout = 30,
}
```

每次开连接执行：

```sql
PRAGMA journal_mode = WAL;      -- 读写并发：传输写入不阻塞 UI 查询
PRAGMA synchronous = NORMAL;    -- WAL 下兼顾性能与安全
PRAGMA foreign_keys = ON;
```

**建表策略**：全部语句使用 `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS`，**重复执行安全**，无需版本迁移框架。

**完整性检查**：`PRAGMA quick_check`（`CheckIntegrityAsync`），损坏时返回 `false` 由调用方降级处理。

**设计要点**

- 时间统一存 **ISO 8601 字符串**（`TEXT`），便于直接排序与调试。
- 布尔值用 `INTEGER`（0/1）。
- 枚举用 `INTEGER`，取值与 C# 枚举一致（见各表说明）。
- 主键：设备/传输用 `TEXT` GUID；分块状态用复合主键 + **位图 BLOB**。

---

## 2. 实体关系

```
Devices ────┐
            │ (RemoteDeviceId)
            ▼
        Transfers ──1:N──▶ TransferFiles ──1:1──▶ TransferChunks
            │
            │ (SyncPairId，可选)
            ▼
        SyncPairs ──1:N──▶ SyncEntries
            │
            └──1:N──▶ SyncConflicts

Settings（独立键值表）
```

---

## 3. 表结构

### 3.1 Devices —— 设备表

```sql
CREATE TABLE IF NOT EXISTS Devices (
    DeviceId                TEXT PRIMARY KEY,
    DeviceName              TEXT NOT NULL DEFAULT '',
    IpAddress               TEXT NOT NULL DEFAULT '',
    Port                    INTEGER NOT NULL DEFAULT 0,
    AppVersion              TEXT NOT NULL DEFAULT '',
    ProtocolVersion         INTEGER NOT NULL DEFAULT 0,
    CertificateFingerprint  TEXT NULL,
    TrustState              INTEGER NOT NULL DEFAULT 0,
    FirstSeen               TEXT NOT NULL,
    LastSeen                TEXT NOT NULL,
    IsManual                INTEGER NOT NULL DEFAULT 0
);
```

| 列 | 类型 | 说明 |
| --- | --- | --- |
| `DeviceId` | TEXT PK | 设备 GUID，持久化不变 |
| `DeviceName` | TEXT | 用户可见名 |
| `IpAddress` | TEXT | 最近一次见到的 IP（UDP Source IP 或手工填写） |
| `Port` | INTEGER | 对端 TCP 传输端口 |
| `AppVersion` | TEXT | 对端应用版本 |
| `ProtocolVersion` | INTEGER | 对端协议版本 |
| `CertificateFingerprint` | TEXT NULL | 证书 SHA-256（大写十六进制），用于指纹固定 |
| `TrustState` | INTEGER | `0=Unknown 1=Pending 2=Trusted 3=Revoked 4=IdentityChanged` |
| `FirstSeen` / `LastSeen` | TEXT | ISO 8601 |
| `IsManual` | INTEGER | 1 = 手工添加 IP 的设备 |

---

### 3.2 Transfers —— 传输任务表

```sql
CREATE TABLE IF NOT EXISTS Transfers (
    TransferId        TEXT PRIMARY KEY,
    Direction         INTEGER NOT NULL DEFAULT 0,
    TransferType      INTEGER NOT NULL DEFAULT 0,
    RemoteDeviceId    TEXT NOT NULL DEFAULT '',
    RemoteDeviceName  TEXT NOT NULL DEFAULT '',
    LocalDeviceId     TEXT NOT NULL DEFAULT '',
    State             INTEGER NOT NULL DEFAULT 0,
    TotalSize         INTEGER NOT NULL DEFAULT 0,
    TransferredSize   INTEGER NOT NULL DEFAULT 0,
    TotalFiles        INTEGER NOT NULL DEFAULT 0,
    CompletedFiles    INTEGER NOT NULL DEFAULT 0,
    RootName          TEXT NOT NULL DEFAULT '',
    DownloadRoot      TEXT NULL,
    ErrorCode         TEXT NULL,
    ErrorMessage      TEXT NULL,
    CreatedAt         TEXT NOT NULL,
    CompletedAt       TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Transfers_CreatedAt ON Transfers (CreatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_Transfers_State     ON Transfers (State);
```

| 列 | 说明 |
| --- | --- |
| `Direction` | `0=Send 1=Receive` |
| `TransferType` | `0=File 1=Folder` |
| `State` | 与 `TransferState` 枚举一致：`0=Pending 1=Queued 2=WaitingApproval 3=Preparing 4=Transferring 5=Paused 6=Verifying 7=Completed 8=Rejected 9=Cancelled 10=Failed 11=VerificationFailed` |
| `TotalSize` / `TransferredSize` | 字节数（多文件为汇总） |
| `RootName` | 根文件名 / 根文件夹名 |
| `DownloadRoot` | 接收端落盘根目录（发送端为 NULL） |
| `CreatedAt` / `CompletedAt` | ISO 8601，`CompletedAt` 仅终态时非空 |

**索引用途**：`IX_Transfers_CreatedAt DESC` 服务历史记录页倒序分页；`IX_Transfers_State` 服务「未完成传输」查询（断点续传列表）。

---

### 3.3 TransferFiles —— 传输文件表

```sql
CREATE TABLE IF NOT EXISTS TransferFiles (
    FileId           TEXT NOT NULL,
    TransferId       TEXT NOT NULL,
    FileIndex        INTEGER NOT NULL,
    RelativePath     TEXT NOT NULL DEFAULT '',
    FileName         TEXT NOT NULL DEFAULT '',
    FileSize         INTEGER NOT NULL DEFAULT 0,
    Sha256           TEXT NOT NULL DEFAULT '',
    ChunkSize        INTEGER NOT NULL DEFAULT 0,
    TotalChunks      INTEGER NOT NULL DEFAULT 0,
    CompletedChunks  INTEGER NOT NULL DEFAULT 0,
    State            INTEGER NOT NULL DEFAULT 0,
    TargetPath       TEXT NULL,
    SourcePath       TEXT NULL,
    LastWriteTimeUtc TEXT NULL,
    PRIMARY KEY (TransferId, FileIndex)
);
```

| 列 | 说明 |
| --- | --- |
| `FileId` | 文件 GUID（发送端生成） |
| `FileIndex` | 文件夹传输中的序号（0-based） |
| `RelativePath` | **同步的核心标识**：根名 + 子路径 |
| `Sha256` | 期望的文件摘要 |
| `ChunkSize` / `TotalChunks` / `CompletedChunks` | 分块参数与进度计数 |
| `TargetPath` | 接收端最终落盘绝对路径（发送端为 NULL） |
| `SourcePath` | 发送端源文件绝对路径（接收端为 NULL） |

**主键 `(TransferId, FileIndex)`**：同一传输内文件序号唯一；支持一个传输包含整个文件夹。

---

### 3.4 TransferChunks —— 分块位图表（断点续传核心）

```sql
-- 每行保存一个文件的全部分块完成状态位图（BLOB）。
-- 例如 100 GB / 4 MiB = 25600 个 Chunk，仅需 3200 字节。
CREATE TABLE IF NOT EXISTS TransferChunks (
    TransferId  TEXT NOT NULL,
    FileIndex   INTEGER NOT NULL,
    TotalChunks INTEGER NOT NULL,
    Bitmap      BLOB NOT NULL,
    UpdatedAt   TEXT NOT NULL,
    PRIMARY KEY (TransferId, FileIndex)
);
```

| 列 | 说明 |
| --- | --- |
| `Bitmap` | 每 bit 代表一个分块是否已完成，第 `i` 块 → `Bitmap[i/8] & (1 << (i%8))` |
| `TotalChunks` | 冗余保存，便于校验位图长度 |
| `UpdatedAt` | 最后更新时间 |

#### 为什么用位图而不是「每块一行」

| 方案 | 100 GB / 4 MiB 文件 | 说明 |
| --- | --- | --- |
| 每块一行 | **25600 行** | 表膨胀、写入放大、查询需聚合 |
| JSON 字符串 | 约 **150 KB** 文本 | 每次更新需重写整个字符串 |
| **位图 BLOB** | **3200 字节** | 单行、单次更新、按需置位 |

位图由 `Core/Transfers/ChunkManager` 的 `BitSet` 负责序列化/反序列化，与内存表示一一对应。位图置位与 `.part` 文件写入在同一个 chunk 处理流程内完成，保证「位图说完成 = 数据确实落盘」。

---

### 3.5 Settings —— 键值配置表

```sql
CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
```

> 说明：当前运行时配置主要持久化在 `%LOCALAPPDATA%\LanTransfer\settings.json`（`SettingsService` 原子写入）。`Settings` 表作为数据库侧的键值补充（如首次运行标记、迁移版本），两者职责不重叠。

---

### 3.6 SyncPairs —— 同步关系表

```sql
CREATE TABLE IF NOT EXISTS SyncPairs (
    SyncPairId       TEXT PRIMARY KEY,
    Name             TEXT NOT NULL DEFAULT '',
    LocalPath        TEXT NOT NULL DEFAULT '',
    RemoteDeviceId   TEXT NOT NULL DEFAULT '',
    RemoteDeviceName TEXT NOT NULL DEFAULT '',
    RemotePath       TEXT NOT NULL DEFAULT '',
    Mode             INTEGER NOT NULL DEFAULT 0,
    Enabled          INTEGER NOT NULL DEFAULT 1,
    Status           INTEGER NOT NULL DEFAULT 0,
    IsInitiator      INTEGER NOT NULL DEFAULT 1,
    LastError        TEXT NULL,
    CreatedAt        TEXT NOT NULL,
    LastSyncAt       TEXT NULL,
    LastScanAt       TEXT NULL
);
```

| 列 | 说明 |
| --- | --- |
| `LocalPath` | **本机**使用的目录（用户可改） |
| `RemotePath` | 对端使用的目录（仅记录，实际以对端为准） |
| `Mode` | `0=TwoWay 1=SendOnly 2=ReceiveOnly` |
| `Status` | `0=Idle 1=Scanning 2=Syncing 3=Paused 4=Conflict 5=Error 6=WaitingApproval` |
| `IsInitiator` | 1 = 本机发起该同步关系 |
| `LastSyncAt` / `LastScanAt` | 上次同步 / 扫描时间 |

---

### 3.7 SyncEntries —— 同步基线与墓碑表

```sql
CREATE TABLE IF NOT EXISTS SyncEntries (
    SyncPairId        TEXT NOT NULL,
    RelativePath      TEXT NOT NULL,
    FileId            TEXT NOT NULL DEFAULT '',
    IsDirectory       INTEGER NOT NULL DEFAULT 0,
    FileSize          INTEGER NOT NULL DEFAULT 0,
    LastWriteTimeUtc  TEXT NOT NULL,
    Sha256            TEXT NOT NULL DEFAULT '',
    Version           INTEGER NOT NULL DEFAULT 0,
    LocalVersion      INTEGER NOT NULL DEFAULT 0,
    RemoteVersion     INTEGER NOT NULL DEFAULT 0,
    State             INTEGER NOT NULL DEFAULT 0,
    Deleted           INTEGER NOT NULL DEFAULT 0,
    DeletedAt         TEXT NULL,
    DeletePropagated  INTEGER NOT NULL DEFAULT 0,
    LastSyncedAt      TEXT NULL,
    PRIMARY KEY (SyncPairId, RelativePath)
);
CREATE INDEX IF NOT EXISTS IX_SyncEntries_State ON SyncEntries (SyncPairId, State);
```

| 列 | 说明 |
| --- | --- |
| `RelativePath` | **同步唯一标识**（不是 FileId、不是文件名），主键组成部分 |
| `FileSize` / `LastWriteTimeUtc` / `Sha256` | 上次同步时的**基线（Baseline）**快照 |
| `Version` | 基线版本号，每次成功同步 +1 |
| `LocalVersion` / `RemoteVersion` | 本地/远端各自的版本号，用于三态比较 |
| `State` | `0=InSync 1=PendingLocalToRemote 2=PendingRemoteToLocal 3=Conflict 4=Transferring 5=Deleted 6=Error` |
| `Deleted` | 1 = 墓碑（对端删除后保留的记录） |
| `DeletedAt` | 删除时间，配合 `TombstoneRetentionDays` 清理 |
| `DeletePropagated` | 1 = 删除已传播到对端，避免重复请求 |
| `LastSyncedAt` | 该条最后同步成功时间 |

**索引用途**：`IX_SyncEntries_State` 支撑「按同步关系 + 状态」查询待处理项（规划器与 UI 列表）。

> **为什么需要基线表？**
> 三态比较需要「上次同步时的一致状态」作为参照物。只有 Local 与 Remote 两个快照无法区分「对端新增」与「本机删除」。`SyncEntries` 就是那个第三态 `B`。

---

### 3.8 SyncConflicts —— 冲突表

```sql
CREATE TABLE IF NOT EXISTS SyncConflicts (
    ConflictId       TEXT PRIMARY KEY,
    SyncPairId       TEXT NOT NULL,
    RelativePath     TEXT NOT NULL,
    LocalSha256      TEXT NOT NULL DEFAULT '',
    RemoteSha256     TEXT NOT NULL DEFAULT '',
    ConflictCopyPath TEXT NULL,
    Description      TEXT NOT NULL DEFAULT '',
    Resolved         INTEGER NOT NULL DEFAULT 0,
    CreatedAt        TEXT NOT NULL,
    ResolvedAt       TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_SyncConflicts_Pair ON SyncConflicts (SyncPairId, Resolved);
```

| 列 | 说明 |
| --- | --- |
| `LocalSha256` / `RemoteSha256` | 冲突双方的内容摘要，便于用户判断 |
| `ConflictCopyPath` | 生成的冲突副本路径，如 `report (conflict-PC-B-20260924-154501).docx` |
| `Description` | 人类可读描述 |
| `Resolved` | 1 = 用户已裁决 |
| `ResolvedAt` | 裁决时间 |

**索引用途**：`IX_SyncConflicts_Pair` 支撑「某同步关系下未解决冲突」查询（UI 冲突角标）。

---

## 4. 典型查询

**历史记录（倒序分页）**

```sql
SELECT * FROM Transfers ORDER BY CreatedAt DESC LIMIT @limit OFFSET @offset;
```

**未完成的传输（断点续传列表）**

```sql
SELECT * FROM Transfers
WHERE State NOT IN (7, 9, 8)   -- Completed / Cancelled / Rejected
ORDER BY CreatedAt DESC;
```

**某文件的已落盘分块**

```sql
SELECT Bitmap, TotalChunks FROM TransferChunks
WHERE TransferId = @id AND FileIndex = @index;
```

**某同步关系下待处理项**

```sql
SELECT * FROM SyncEntries
WHERE SyncPairId = @id AND State <> 0   -- 非 InSync
ORDER BY RelativePath;
```

**未解决冲突**

```sql
SELECT * FROM SyncConflicts WHERE SyncPairId = @id AND Resolved = 0;
```

**可清理的墓碑**

```sql
SELECT * FROM SyncEntries
WHERE Deleted = 1 AND DeletePropagated = 1
  AND DeletedAt < @cutoff;   -- cutoff = now - TombstoneRetentionDays
```

---

## 5. 数据目录一览

```
%LOCALAPPDATA%\LanTransfer\
├── lan-transfer.db          # 主数据库
├── lan-transfer.db-wal      # WAL 日志
├── lan-transfer.db-shm      # 共享内存索引
├── settings.json            # 应用配置（原子写入）
├── settings.json.tmp        # 写入中转（仅在保存瞬间存在）
├── identity.pfx             # 自签名证书
├── identity.key             # DPAPI 加密的私钥口令
└── Logs\
    └── lantransfer-YYYYMMDD.log   # 按天滚动，保留 14 个，单文件 ≤ 64 MB
```

> **卸载/清理**：直接删除 `%LOCALAPPDATA%\LanTransfer` 即可完全重置（会丢失设备信任关系与历史记录）。
