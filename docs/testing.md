# LAN Transfer 测试文档

> 框架：xUnit 2.9.3 + Microsoft.NET.Test.Sdk 17.14.1 · 目标框架：`net10.0`
> 当前结果：**220 个测试全部通过**（Core 117 / Network 41 / Integration 62）

---

## 1. 测试项目结构

```
tests/
├── LanTransfer.Core.Tests/           # 117 通过 —— 纯逻辑单元测试
│   ├── ChunkManagerTests.cs          # 分块读写、乱序、重传、元数据
│   ├── SafePathResolverTests.cs      # 路径安全（安全关键）
│   ├── HashServiceTests.cs           # SHA-256 + 文件扫描
│   ├── TransferStateTests.cs         # 状态机
│   ├── StorageRepositoryTests.cs     # SQLite 仓储
│   └── SettingsServiceTests.cs       # 配置持久化
├── LanTransfer.Network.Tests/        #  41 通过 —— 网络工具与协议序列化
│   ├── ProtocolJsonTests.cs          # JSON 线格式
│   └── NetworkHelperTests.cs         # 子网/广播/端口
└── LanTransfer.IntegrationTests/     #  62 通过 —— 端到端
    ├── LoopbackTransferTests.cs      # 真实 Kestrel 回环传输
    ├── SyncPlannerTests.cs           # 三态比较算法
    └── SyncScannerTests.cs           # 扫描 + 基线 + 墓碑
```

**运行命令**

```bash
dotnet test LanTransfer.sln -c Release
# 或单个项目
dotnet test tests/LanTransfer.IntegrationTests
```

---

## 2. 单元测试矩阵

### 2.1 ChunkManagerTests（9）

| 测试 | 验证点 |
| --- | --- |
| `CalculateTotalChunks_RejectsInvalidChunkSize` | 非法块大小被拒绝 |
| `GetChunkRange_RejectsNegativeIndex` | 负索引被拒绝 |
| `WriteChunkAsync_OutOfOrder_ProducesIdenticalFile` | **乱序到达仍得到正确文件** |
| `WriteChunkAsync_Retransmit_OverwritesSameRegion` | 重传覆盖同一区域，不产生错位 |
| `WriteChunkAsync_ThrowsWhenSourceIsShort` | 源数据不足时抛异常 |
| `ReadChunkAsync_ReadsRequestedSlice` | 精确读取指定切片 |
| `Metadata_RoundTrips_AndIsDeleted` | `.part.json` 元数据往返 + 删除 |
| `ReadMetadataAsync_ReturnsNullWhenMissing` | 缺失时返回 null 而非抛异常 |
| `Metadata_AtomicWrite_LeavesNoTempFile` | 原子写入不留临时文件 |

### 2.2 SafePathResolverTests（13）—— 安全关键

| 测试 | 验证点 |
| --- | --- |
| `TryResolve_AcceptsNormalizedRelativePaths` | 合法相对路径（含规范化变体）被接受 |
| `TryResolve_RejectsTraversalAndAbsolutePaths` | **拒绝 `../`、`C:\...`、`\\server\share`、`/etc/passwd`、ADS（`a.txt:stream`）、首尾空白** |
| `TryResolve_RejectsReservedDeviceNames` | 拒绝 `CON` / `PRN` / `AUX` / `NUL` / `COM1` 等保留设备名 |
| `TryResolve_RejectsTrailingDotOrSpace` | 拒绝尾随点或空格 |
| `TryResolve_RejectsEmptyRoot` | 空根目录被拒绝 |
| `NormalizeRelativePath_ProducesCanonicalForm` | 路径规范化正确 |
| `IsValidFileName_DetectsInvalidNames` | 非法文件名识别 |
| `IsValidFileName_RejectsOverlongName` | 超长文件名被拒绝 |
| `ResolveConflictName_ReturnsSamePathWhenNotExists` | 无冲突时返回原路径 |
| `ResolveConflictName_AppendsIncrementingIndex` | `test.zip` → `test (1).zip` → `test (2).zip` |
| `ResolveConflictName_OverwriteAndSkipReturnOriginalPath` | Overwrite / Skip 策略返回原路径 |
| `GetAvailableFreeSpace_ReturnsPositiveForExistingDrive` | 磁盘剩余空间查询 |

> **回归背景**：`TryResolve_RejectsTraversalAndAbsolutePaths` 曾发现真实漏洞 —— 绝对路径会在规范化阶段被去掉盘符而变成看似合法的相对路径。修复方式是在规范化**之前**先拒绝 `:` 与 `Path.IsPathRooted`。

### 2.3 HashServiceTests（12）

| 测试 | 验证点 |
| --- | --- |
| `ComputeHash_MatchesKnownSha256Vector` | 对齐标准 SHA-256 测试向量 |
| `ComputeFileHashAsync_MatchesFrameworkHash` | 与 `SHA256.HashData` 结果一致 |
| `ComputeFileHashAsync_HandlesEmptyFile` | 0 字节文件（边界） |
| `VerifyFileHashAsync_IsCaseInsensitive` | 摘要大小写不敏感 |
| `VerifyFileHashAsync_ReturnsFalseOnMismatch` | 不匹配返回 false |
| `ComputeFileHashAsync_ReportsProgressMonotonically` | 进度回调单调递增 |
| `ComputeStreamHashAsync_DoesNotCloseCallerStream` | **不关闭调用方流** |
| `ScanFolder_PreservesRelativeStructure` | 文件夹扫描保留相对结构 |
| `ScanFile_ReturnsNameSizeAndTimestamp` | 单文件扫描字段完整 |
| `ScanSelection_CombinesFilesAndFolders` | 混合选择（文件 + 文件夹） |
| `ScanSelection_IgnoresMissingPaths` | 忽略不存在的路径 |
| `ScanFolder_EmptyDirectory_ReturnsEmpty` | 空目录返回空集合 |

### 2.4 TransferStateTests（2）

| 测试 | 验证点 |
| --- | --- |
| `TerminalAndActiveAreDisjoint` | 终态与活动状态集合不相交 |
| `WireString_RoundTripsForEveryState` | **全部 12 个状态的线格式往返一致** |

### 2.5 StorageRepositoryTests（10）

| 测试 | 验证点 |
| --- | --- |
| `Database_IsCreatedAndPassesIntegrityCheck` | 建库 + `quick_check` 通过 |
| `DeviceRepository_UpsertThenUpdateTrust` | 设备 upsert 与信任状态更新 |
| `TransferRepository_PersistsRecordAndFiles` | 传输 + 文件记录持久化 |
| `TransferRepository_BitmapChunkTracking_ScalesToHundredsOfThousandsOfChunks` | **位图扩展到数十万块**（模拟 100 GB 级文件） |
| `TransferRepository_IncompleteTransfers_ExcludesTerminalAndCancelledStates` | 未完成查询排除 Completed/Cancelled/Rejected |
| `TransferRepository_ClearHistory_KeepsUnfinished` | 清空历史保留未完成项 |
| `SyncRepository_PairsBaselineAndConflicts` | 同步关系 + 基线 + 冲突 |
| `SyncRepository_PurgeTombstones_KeepsUnpropagatedDeletions` | **只清理已传播的墓碑** |
| `SyncRepository_DeletePair_RemovesDependents` | 删除同步关系级联删除依赖 |
| `InitializeAsync_IsIdempotent` | 重复初始化安全 |

### 2.6 SettingsServiceTests（12）

| 测试 | 验证点 |
| --- | --- |
| `LoadAsync_CreatesFileWithDefaults` | 首次加载生成默认配置 |
| `DeviceId_IsStableAcrossReloads` | **DeviceId 跨重启稳定** |
| `LoadAsync_GeneratesDistinctDeviceIdsPerInstallation` | 不同安装生成不同 DeviceId |
| `SaveAsync_NormalizesInvalidPorts` | 非法端口被规范化 |
| `SaveAsync_RejectsChunkSizeOutsideAllowedSet` | 分块大小必须在允许集合内 |
| `SaveAsync_NormalizesSyncAndTombstoneIntervals` | 同步/墓碑间隔规范化 |
| `SaveAsync_RaisesSettingsChanged` | 触发 `SettingsChanged` 事件 |
| `LoadAsync_CorruptedFile_FallsBackToDefaultsAndBacksUp` | **配置损坏回退默认并备份** |
| `AutoAcceptTrustedDevice_DefaultsToOff` | 自动接收默认关闭（安全默认） |
| `SaveAsync_DoesNotLeakTemporaryFile` | 原子写入不留 `.tmp` |
| `Clone_ProducesIndependentCopy` | 克隆是深拷贝 |
| `ApplyAsync_IsAliasForSave` | `ApplyAsync` 语义等同 `SaveAsync` |

### 2.7 ProtocolJsonTests（13）

| 测试 | 验证点 |
| --- | --- |
| `HealthResponse_SerializesWithCamelCaseNames` | camelCase 字段名 |
| `DiscoveryMessage_RoundTrips` | UDP 报文往返 |
| `CreateTransferRequest_RoundTripsAllFields` | 传输请求全字段往返 |
| `TransferStatusResponse_RoundTripsChunkBitmap` | 断点续传状态往返 |
| `SyncManifestEntry_OmitsNullSha256` | `sha256` 为 null 时不输出 |
| `ApiErrorResponse_OmitsNullDetails` | `details` 为 null 时不输出 |
| `Deserialize_ToleratesUnknownProperties` | 忽略未知字段（向前兼容） |
| `Deserialize_AcceptsCaseInsensitivePropertyNames` | 大小写不敏感 |
| `Deserialize_ReturnsDefaultOnMalformedJson` | 非法 JSON 返回 default 而不抛 |
| `AllErrorCodes_AreUniqueAndUpperSnakeCase` | **全部错误码唯一且为大写蛇形** |
| `ProtocolJson_RoundTripsNonAsciiMessages` | 中文消息往返正确 |
| `Options_AreSharedAndNotIndented` | 共享选项且不缩进（省带宽） |
| `JsonSerializerOptions_IsReusable` | 选项可复用 |
| `DeviceInfo_SerializationExcludesComputedProperties` | 计算属性不被序列化 |

### 2.8 NetworkHelperTests（15）

| 测试 | 验证点 |
| --- | --- |
| `PrefixToMask_ReturnsDottedQuad` | 前缀长度 → 点分掩码 |
| `PrefixToMask_FallsBackToSlash24ForInvalidPrefix` | **非法前缀回退 /24**（不回退 0.0.0.0） |
| `ComputeBroadcast_ReturnsDirectedBroadcast` | 定向广播地址计算 |
| `ComputeBroadcast_RejectsIpv6` | **IPv6 抛异常**（不静默取前 4 字节） |
| `ComputeBroadcast_FallsBackToSlash24ForInvalidPrefix` | 非法前缀回退 |
| `GetInterfaces_ExcludesLoopbackAndInterfacesWithoutIPv4` | 过滤回环与无 IPv4 网卡 |
| `GetInterfaces_ReportsConsistentMaskAndBroadcast` | 掩码与广播地址一致 |
| `GetLocalIPv4Addresses_MatchesNonVirtualInterfaceAddresses` | 与实现过滤规则一致 |
| `GetPrimaryIPv4Address_ReturnsAddressFromInterfaceList` | 主地址来自网卡列表 |
| `GetBroadcastTargets_AllHaveDistinctBroadcastAddresses` | 广播目标去重 |
| `IsVirtual_DetectsLoopbackAsVirtual` | 回环判定为虚拟 |
| `IsVirtual_RealEthernetIsNotVirtual` | 真实以太网非虚拟 |
| `BoundTcpPort_IsReportedInUse` | 已占用端口被识别 |
| `ConnectingToClosedPort_FailsFast` | 关闭端口快速失败 |
| `UdpPortCanBeBoundTwiceOnLoopback` | UDP 回环可重复绑定 |

---

## 3. 集成测试

### 3.1 LoopbackTransferTests（18）—— 真实 Kestrel 端到端

测试启动真实 `TransferServer`（Kestrel + TLS），用真实 `TransferHttpClient` 通过 `127.0.0.1` 发起请求，全程走 HTTPS。

| 测试 | 验证点 |
| --- | --- |
| `Health_ReturnsCompatibleProtocolVersion` | 健康检查协议版本兼容 |
| `Device_ReturnsServerIdentityAndFingerprint` | 设备信息与指纹可获取 |
| `CertificateFingerprint_IsRecordedOnFirstContact` | 首次接触记录指纹 |
| `CertificateFingerprint_PinningRejectsChangedCertificate` | **指纹变化被拒绝（pinning 生效）** |
| `Transfer_OneKilobyte_IsSavedAndVerified` | 1 KB 传输 + 校验 |
| `Transfer_TenMegabytes_IsStreamedAndVerified` | 10 MB 流式传输 + 校验 |
| `Transfer_ExactChunkBoundary_ProducesSingleChunk` | 恰好等于块大小的边界 |
| `Transfer_NestedRelativePath_KeepsDirectoryStructure` | 嵌套相对路径保留目录结构 |
| `Transfer_LargeFile_IsStreamedWithoutLoadingIntoMemory` | **大文件不整文件进内存**（默认 128 MiB 并断言托管堆增量；`LANTRANSFER_LARGE_TESTS=1` 放大到 500 MB，断言同一套） |
| `Resume_UploadsOnlyMissingChunksAndStillVerifies` | **断点续传只传缺失块且仍校验通过** |
| `PathTraversal_IsRejectedOverTheWire` | 路径穿越在真实链路上被拒绝 |
| `InvalidFileName_IsRejectedOverTheWire` | 非法文件名在真实链路上被拒绝 |
| `IncompatibleProtocolVersion_IsRejected` | 协议不兼容被拒绝（不降级） |
| `HashMismatch_IsDetectedAndFileIsNotPromoted` | **哈希不符时文件不被改名** |
| `RejectedTransfer_IsNotWrittenToDisk` | 被拒绝的传输不落盘 |
| `DuplicateFileName_IsRenamedInsteadOfOverwritten` | 重名自动重命名而非覆盖 |
| `PortAlreadyInUse_DoesNotCrash_AndReportsLastError` | **端口占用不崩溃，只报 LastError** |

> **回归背景**：`CertificateFingerprint_PinningRejectsChangedCertificate` 曾失败，根因是 **TLS 1.3 会话复用会跳过证书校验回调**，导致指纹固定失效。修复为 `SocketsHttpHandler` + `SslOptions.AllowTlsResume = false`。该测试拆成「首次接触记录指纹」与「指纹变化被拒绝」两个，分别锁定两个环节。

### 3.2 SyncPlannerTests（27）—— 三态比较算法

覆盖 Baseline / Local / Remote 三态的全部组合。

| 分组 | 测试 |
| --- | --- |
| 无动作 | `EmptyEverything_ProducesNoActions`、`BothSidesIdenticalToBaseline_ProducesNoActions`、`AlreadyDeletedTombstoneOnBothSides_ProducesNoActions` |
| 新增 | `NewLocalFile_UploadsToRemote`、`NewRemoteFile_DownloadsToLocal` |
| 单向改动 | `RemoteModified_LocalUnchanged_Downloads`、`LocalModified_RemoteUnchanged_Uploads` |
| 双方相同改动 | `BothModifiedIdentically_OnlyUpdatesBaseline` |
| 删除传播 | `LocalDeleted_RemoteUnchanged_DeletesRemote`、`RemoteDeleted_LocalUnchanged_DeletesLocal`、`BothDeleted_OnlyUpdatesBaseline_FileDoesNotResurrect` |
| 冲突 | `BothModifiedDifferently_ProducesConflictAndKeepsBothVersions`、`LocalDeletedRemoteModified_IsDeleteModifyConflict_RemoteVersionWins`、`RemoteDeletedLocalModified_IsDeleteModifyConflict_LocalVersionWins`、`ConflictNeverSilentlyDeletesModifiedSide` |
| 同步模式 | `SendOnly_IgnoresRemoteModificationAndDeletion`、`SendOnly_StillUploadsLocalChanges`、`ReceiveOnly_IgnoresLocalModificationAndDeletion`、`ReceiveOnly_StillDownloadsRemoteChanges` |
| 路径与命名 | `NestedPaths_ArePlannedIndependently`、`ConflictName_PreservesDirectorySegment`、`ConflictName_SanitizesDeviceTag`、`ConflictName_FallsBackWhenTagIsEmpty` |
| 目录与确定性 | `DirectoriesAreTrackedWithoutHashing`、`MixedScenario_ProducesExactlyOneActionPerPath`、`Plan_IsDeterministicAndSortedByPath`、`Plan_CarriesSyncPairId` |

**关键安全性质**：`ConflictNeverSilentlyDeletesModifiedSide` —— 冲突处理**永远不能静默丢弃任何一端被修改过的内容**。

### 3.3 SyncScannerTests（17）—— 扫描、基线、墓碑

| 测试 | 验证点 |
| --- | --- |
| `ScanAsync_ReturnsFilesAndDirectoriesWithRelativePaths` | 扫描产出相对路径 |
| `ScanAsync_FirstScanAlwaysComputesHash` | 首次扫描必定计算哈希 |
| `ScanAsync_SkipsHashingWhenSizeAndTimestampUnchanged` | **size+mtime 未变时跳过哈希**（性能关键） |
| `ScanAsync_RecomputesHashWhenSizeChanges` | size 变化时重算哈希 |
| `ScanAsync_FullHashForcesRecompute` | 全量扫描强制重算 |
| `ScanAsync_MissingDirectory_ReturnsEmptyWithoutThrowing` | 目录缺失不抛异常 |
| `ScanAsync_DeletedBaselineEntry_IsStillReportedAsDeletedByPlanner` | 删除的基线项仍被规划器识别 |
| `Relative_ReturnsEmptyForRootItself` | 根目录自身返回空相对路径 |
| `Resolve_ReturnsNullForTraversal` | 穿越路径解析返回 null |
| `BuildBaseline_ThenLoad_RoundTrips` | 基线构建后加载一致 |
| `BuildBaseline_GeneratesFileIdWhenMissing` | 缺失 FileId 时生成 |
| `Tombstone_IsPersistedAsRow_NotPhysicallyDeleted` | **墓碑以行形式保留，不物理删除** |
| `Tombstone_IsOnlyPurgedAfterPropagationAndRetention` | 墓碑仅在已传播且超期后清理 |
| `ToManifest_CarriesBaselineVersionAndSha` | 清单携带基线版本与摘要 |
| `LoadBaseline_IsCaseInsensitiveOnRelativePath` | 基线加载相对路径大小写不敏感 |
| `BaselineIsIsolatedPerSyncPair` | **不同同步关系的基线相互隔离** |

---

## 4. 测试设计原则

1. **每个测试独立**：临时目录用 `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())`，实现 `IDisposable` 清理。
2. **不依赖真实网络**：集成测试全部走 `127.0.0.1`，端口由 `PortAvailabilityTests` 动态选取，避免与真实服务冲突。
3. **不依赖用户环境**：`SettingsService` / `IdentityService` / `SqliteConnectionFactory` 均支持注入路径，测试用临时目录。
4. **断言行为而非实现**：如 `Transfer_LargeFile_IsStreamedWithoutLoadingIntoMemory` 断言进程托管堆增量远小于文件大小，而非断言代码结构。该用例默认就会执行（128 MiB），不允许用「环境变量 + 静默 return」假跳过：静默返回在 xunit 里记为「通过」，是典型的假绿灯。
5. **安全性质必须双向验证**：既测「合法输入被接受」，也测「非法输入被拒绝」。
6. **回归测试锁定真实漏洞**：每个修复过的安全漏洞都有对应测试（见各处「回归背景」）。

---

## 5. 双机手工验收（Phase 1）

单元/集成测试覆盖不到真实双机环境（多网卡、防火墙、真实交换机）。以下为任务书第 71 节的 Phase 1 验收清单。

### 5.1 环境准备

| 项 | 要求 |
| --- | --- |
| 两台机器 | 同一局域网（同一交换机 / 同一 WiFi），Windows 10 1809+ / Windows 11 |
| 网络类别 | 「专用网络」（公用网络需在防火墙放行） |
| 防火墙 | 首次运行会弹窗，**必须允许**「专用网络」访问 |
| 端口 | 39520/UDP、39521/TCP 未被占用 |

### 5.2 验收步骤

| # | 步骤 | 预期结果 |
| --- | --- | --- |
| 1 | 两台机器分别启动 `LanTransfer.exe` | 正常启动，无报错 |
| 2 | 观察「设备」页 | 10 秒内互相出现在列表，状态为「在线」 |
| 3 | 在 A 上点「手工连接」，输入 B 的 IP | 连接成功，显示 B 的设备名与证书指纹 |
| 4 | 从 A 向 B 拖放一个 100 MB 文件 | B 弹出接收确认框；接受后开始传输 |
| 5 | 观察进度 | 实时显示速度、进度百分比、ETA |
| 6 | 传输中途点「暂停」 | 传输停止；B 侧 `.part` 保留 |
| 7 | 点「继续」 | **只传剩余块**，速度恢复 |
| 8 | 传输完成 | 两端状态均为「已完成」，SHA-256 校验通过 |
| 9 | 对比两端文件 SHA-256 | 完全一致 |
| 10 | 传输一个含 1000+ 文件的文件夹 | 保留目录结构，全部完成 |
| 11 | 传输一个 5 GB 大文件 | 全程内存占用平稳（**不飙升到文件大小**） |
| 12 | 传输 0 字节文件 | 正常完成 |
| 13 | 传输一个中文名 + 超长路径文件 | 正常完成 |
| 14 | 传输途中断开网络 | 状态变「失败」，恢复网络后可续传 |
| 15 | 在 A 上发起配对（输入 B 的 IP） | 两端显示**相同的 6 位验证码**；确认后互相信任 |
| 16 | 再次传输，观察 B 侧 | 已信任设备可直接传输（无需再次确认） |
| 17 | 打开「网络诊断」页 | 显示本机 IP、网卡、端口占用、网络类别 |
| 18 | 诊断页对 B 执行连通性测试 | DNS/TCP/HTTPS 三段全绿 |
| 19 | 打开「历史记录」页 | 显示全部传输记录，可清空 |
| 20 | 修改设置（端口、分块大小、下载目录） | 重启服务后生效；端口占用时提示错误不崩溃 |

### 5.3 双向同步验收（Phase 2+）

| # | 步骤 | 预期结果 |
| --- | --- | --- |
| 1 | A 发起同步，选择本地目录，B 确认并选目录 | 同步关系建立 |
| 2 | A 目录新增文件 | 10 秒内自动同步到 B |
| 3 | B 目录修改文件 | 自动同步回 A |
| 4 | A 删除文件 | B 对应文件被删除（墓碑传播） |
| 5 | 两端同时修改同一文件 | 产生冲突副本 `name (conflict-XXX-时间戳).ext`，等待用户裁决 |
| 6 | 选择「用本地」 | 对端被覆盖为本地版本，冲突解决 |
| 7 | 选择「两份都保留」 | 两端各保留一份 |
| 8 | 关闭一端 10 分钟后再开 | 自动补同步期间的改动 |
| 9 | 暂停同步后修改文件 | 恢复后一次性补同步 |

### 5.4 防火墙放行命令（管理员 PowerShell）

```powershell
New-NetFirewallRule -DisplayName "LAN Transfer (UDP-In)" -Direction Inbound `
  -Protocol UDP -LocalPort 39520 -Action Allow -Profile Private
New-NetFirewallRule -DisplayName "LAN Transfer (TCP-In)" -Direction Inbound `
  -Protocol TCP -LocalPort 39521 -Action Allow -Profile Private
```

> 若使用自定义端口，替换 `-LocalPort` 值。**不要**把规则应用到 `Public` 配置文件，除非确实需要。

---

## 6. 持续验证

```bash
# 全量构建 + 测试
dotnet build LanTransfer.sln -c Release
dotnet test  LanTransfer.sln -c Release

# 打包便携版
pwsh -File publish.ps1
```

**通过标准**：0 编译错误、0 编译警告、0 测试失败。
