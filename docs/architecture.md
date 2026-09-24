# LAN Transfer 架构设计

> 版本：1.0.0 · 协议版本：1 · 目标平台：Windows x64（.NET 10 LTS）

## 1. 设计目标与硬约束

LAN Transfer 是纯局域网、点对点的文件/文件夹传输与双向文件夹同步工具。以下约束贯穿全部代码：

| 约束 | 落地方式 |
| --- | --- |
| 不依赖 SMB / Windows 文件共享 | 自研 HTTPS 分块传输协议，端口默认 39521 |
| 不依赖任何中心服务器 / 云 / 互联网 | UDP 广播发现 + 直连 TCP，无注册、无信令服务 |
| 不绕过系统安全策略、不建立外部隧道 | 只监听 `0.0.0.0`，只接受本网段直连；TLS 双向认证 |
| 大文件流式处理（0 B ~ 100 GB+） | 128 KiB 缓冲区流式读写，任何路径都不整文件进内存 |
| 端口被占用不得崩溃 | Kestrel 启动异常被捕获，只写 `LastError` 交由 UI 提示 |
| 跨模块状态一致 | 单一 `TransferState` 枚举 + `ToWireString()` / `FromWireString()` |
| 协议不兼容不得静默降级 | 校验 `ProtocolVersion`，不匹配返回 `409 PROTOCOL_INCOMPATIBLE` |

## 2. 分层结构

```
LanTransfer.sln
├── src/
│   ├── LanTransfer.Common      # 常量、DTO、枚举、状态机、日志引导（无任何项目依赖）
│   ├── LanTransfer.Core        # 业务核心：路径安全、哈希、分块、速度、传输/设备管理、配置
│   ├── LanTransfer.Security    # 证书身份、DPAPI、信任库、配对
│   ├── LanTransfer.Storage     # SQLite 连接工厂、建表、仓储
│   ├── LanTransfer.Network     # Kestrel 服务端 + HttpClient 客户端 + UDP 发现 + 网络诊断
│   ├── LanTransfer.Sync        # 双向同步：扫描、规划、基线、监听、冲突、引擎
│   └── LanTransfer.App         # WPF 表现层（MVVM），唯一可依赖全部下层
└── tests/
    ├── LanTransfer.Core.Tests
    ├── LanTransfer.Network.Tests
    └── LanTransfer.IntegrationTests
```

### 依赖方向（严格单向，无环）

```
                 ┌──────────────┐
                 │  App (WPF)   │
                 └──────┬───────┘
        ┌───────┬───────┼───────┬────────┐
        ▼       ▼       ▼       ▼        ▼
     Network  Security Storage  Sync   Core
        │       │       │        │       │
        └───────┴───────┴────────┴───┬───┘
                                     ▼
                                  Common
```

关键规则（对应任务书第 69 节编码规则）：

- **`Core` 不依赖 WPF，也不依赖任何 UI 类型。** `Core` 只定义接口（`Interfaces/*.cs`），实现由各模块提供、在 `App` 层组装。
- **`Network` 不依赖 UI。** 它通过事件（`IncomingTransferRequested`、`PairingRequested` 等）向外部抛出请求，由 `App` 层用 `UiDispatcher` 切回 UI 线程后再弹窗。
- **`Common` 无项目依赖。** 所有 DTO、枚举、常量、错误码集中在此，任何模块都可以引用而不会形成环。
- 组装点唯一：`src/LanTransfer.App/Startup/ServiceRegistration.cs`。

## 3. 核心模块职责

### 3.1 LanTransfer.Common

| 文件 | 职责 |
| --- | --- |
| `Constants/AppConstants.cs` | 端口、分块大小、超时、扫描间隔、墓碑保留期等全部魔法数字 |
| `Constants/AppPaths.cs` | `%LOCALAPPDATA%\LanTransfer` 下的目录约定 |
| `Models/TransferState.cs` | 统一状态机 + 线格式转换 |
| `Models/Enums.cs` | `TransferDirection` / `TransferType` / `TrustState` / `SyncMode` / `SyncStatus` / `SyncEntryState` / `ConflictPolicy` |
| `Models/DeviceInfo.cs` | 设备信息、`NetworkInterfaceInfo` |
| `Protocol/ProtocolDtos.cs` | 传输侧全部请求/响应 DTO 与 `ErrorCodes` |
| `Protocol/SyncDtos.cs` | 同步侧 DTO（清单、请求、删除） |
| `Logging/LogSetup.cs` | Serilog 引导（按天滚动、保留 14 个、单文件 64 MB） |

### 3.2 LanTransfer.Core

| 组件 | 职责 |
| --- | --- |
| `Files/SafePathResolver` | **安全关键**：把对端传来的 `relativePath` 解析到受控根目录下，拒绝路径穿越 |
| `Files/FileScanner` | 递归枚举文件/文件夹，产出相对路径 + 大小 + 修改时间 |
| `Hashing/HashService` | 流式 SHA-256（增量读取，不整文件进内存） |
| `Transfers/ChunkManager` | 分块位图（`BitSet`）的置位/查询/序列化，与 `TransferChunks.Bitmap` 一一对应 |
| `Transfers/SpeedCalculator` | 4 秒滑动窗口速度与 ETA |
| `Transfers/TransferManager` | 发送侧编排：排队 → 握手 → 分块 → 校验 → 落库 |
| `Devices/DeviceManager` | 设备表内存视图、在线判定（10 秒阈值）、手工 IP 设备 |
| `Devices/NetworkHelper` | 子网掩码计算、广播地址推导、多网卡枚举 |
| `Configuration/SettingsService` | `settings.json` 原子写入（写 `.tmp` 再 `Move`），损坏时回退默认并备份 |

### 3.3 LanTransfer.Security

| 组件 | 职责 |
| --- | --- |
| `Certificates/IdentityService` | 首次运行生成自签名证书（RSA 2048 / SHA-256，含 SAN），私钥口令用 **DPAPI（CurrentUser）** 保护后落盘；导出 `CertificateFingerprint`（SHA-256 hex） |
| `Trust/TrustStore` | 已信任设备指纹登记；`EvaluateAsync` 返回 `Trusted` / `IdentityChanged` / `Unknown` |
| `Pairing/PairingService` | 由 `pairingSessionId + 双方 DeviceId` 推导 6 位验证码，两端必须一致 |

> **不安装系统根证书。** 客户端通过应用层指纹固定（pinning）校验服务端证书，见 §5。

### 3.4 LanTransfer.Storage

| 组件 | 职责 |
| --- | --- |
| `Database/SqliteConnectionFactory` | 连接创建、WAL 模式、`busy_timeout` |
| `Database/DatabaseInitializer` | 全部 `CREATE TABLE IF NOT EXISTS`，重复执行安全 |
| `Repositories/DeviceRepository` | `Devices` 表 CRUD |
| `Repositories/TransferRepository` | `Transfers` / `TransferFiles` / `TransferChunks` |
| `Repositories/SyncRepository` | `SyncPairs` / `SyncEntries` / `SyncConflicts` |

无 ORM，手写 SQL，便于精确控制索引与批量写入。

### 3.5 LanTransfer.Network

| 组件 | 职责 |
| --- | --- |
| `Server/TransferServer` | Kestrel 宿主，映射全部 `/api/v1/*` 端点；生命周期用 `SemaphoreSlim` 串行化 |
| `Server/IncomingTransferRegistry` | 接收中的传输内存注册表，`StateChanged` 事件驱动 UI |
| `Client/TransferHttpClient` | 发送侧 HTTP 客户端，`SocketsHttpHandler` + 双向 TLS + 指纹固定 |
| `Client/DeviceHeaderHandler` | 每个请求自动附加 4 个 `X-Lan-Transfer-*` 头 |
| `Discovery/UdpDiscoveryService` | UDP 广播发现：多网卡定向广播 + `255.255.255.255` 兜底 |
| `Diagnostics/NetworkDiagnosticsService` | DNS → TCP → HTTPS 三段式连通性测试、端口占用检查 |
| `Diagnostics/NetworkCategoryDetector` | 通过 Windows NLM（COM 后期绑定）判断公用/专用网络 |

### 3.6 LanTransfer.Sync

| 组件 | 职责 |
| --- | --- |
| `Scanner/DirectoryScanner` | 扫描同步目录，产出 `relativePath → 元数据` |
| `Metadata/SyncMetadataManager` | 读写 `SyncEntries` 基线（Baseline） |
| `Planner/SyncPlanner` | **核心算法**：Baseline / Local / Remote 三态比较，产出上传/下载/删除/冲突计划 |
| `Watcher/FileChangeWatcher` | `FileSystemWatcher` 实时触发 + 定期全量扫描（默认 10 分钟）双保险 |
| `Conflict/ConflictResolver` | 冲突副本命名 `name (conflict-PC-B-20260924-154501).ext` |
| `Engine/SyncEngine` | 编排扫描 → 规划 → 传输 → 更新基线，`ISyncServerHandler` 供服务端调用 |

## 4. 数据流

### 4.1 单文件发送（发送端视角）

```
用户拖放
  → TransferViewModel.DropPathsAsync
  → FileScanner 展开文件夹，得 [相对路径, 大小, mtime]
  → HashService 流式计算 SHA-256
  → TransferManager 入队（Queued）
  → TransferHttpClient POST /transfers          → 收到 fileId + state=waiting-approval
  → 轮询 GET /transfers/{id} 直到 state=transferring
  → 循环：读 4 MiB 块 → PUT /transfers/{id}/chunks/{i}
  → POST /transfers/{id}/complete（带 sha256）
  → 收到 verified=true / savedPath
  → 落库 Completed
```

### 4.2 单文件接收（接收端视角）

```
TransferServer 收到 POST /transfers
  → ResolveIdentity：读 4 个头 + 客户端证书 → 指纹 → TrustStore.Evaluate
  → 身份变化(IdentityChanged) → 409，拒绝
  → 未信任 → 状态 WaitingApproval，触发 IncomingTransferRequested 事件
  → UiDispatcher 切 UI 线程 → IncomingTransferDialog 弹窗
  → 用户点「接受」→ RespondToApproval → 状态 Preparing → Transferring
  → SafePathResolver 解析相对路径（拒绝穿越）
  → 目标已存在 → 按 ConflictPolicy（默认 Rename）解析为 test (1).zip
  → 预分配 <name>.part
  → PUT chunk：Seek(offset) 随机写入 + 位图置位 + 落库
  → POST complete：HashService 校验 .part 的 SHA-256
       一致 → 改名为正式文件名 → Completed
       不一致 → 删除/保留 .part → VerificationFailed
```

### 4.3 双向同步一轮

```
触发：FileSystemWatcher 750 ms 防抖、非发起端变更通知、设备重新上线或定期全量扫描
  → 非发起端检测到变化时 POST /sync/pairs/{id}/notify
  → 发起端合并重复通知并串行执行，运行期间的新事件会排入下一轮
  → DirectoryScanner 扫本地 → Local 快照
  → POST /sync/pairs/{id}/manifest（上传本地清单，取回对端清单）
  → SyncPlanner 三态比较：
        基线 B、本地 L、远端 R
        L == R              → InSync
        L != B && R == B    → 本机改动 → PendingLocalToRemote（上传）
        R != B && L == B    → 对端改动 → PendingRemoteToLocal（下载）
        L != B && R != B && L != R → 冲突 → SyncConflicts + 冲突副本
        L 存在 && R 缺失 && B 存在 → 一端删除 → 传播墓碑
  → 执行计划（PUT/GET /sync/pairs/{id}/content，DELETE /sync/pairs/{id}/delete）
  → 更新基线 SyncEntries（Version / LocalVersion / RemoteVersion / LastSyncedAt）
```

> **相对路径是同步的核心标识**，不是文件名、不是 FileId。绝对路径只在各端本地解析。

## 5. 安全设计

### 5.1 双向 TLS

- 服务端 `UseHttps` + `ClientCertificateMode.AllowCertificate`：请求客户端证书，但**由应用层判定信任**（`ClientCertificateValidation` 恒返回 true，信任结论交给 `TrustStore`）。
- 客户端 `SocketsHttpHandler` + `SslOptions.ClientCertificates` 出示本机证书。
- 不把自签名证书装进系统根存储，避免污染用户机器。

### 5.2 指纹固定（Pinning）—— 两个必须知道的坑

1. **不能复用 `HttpClientHandler`。** 它没有 `SslOptions`，无法关闭 TLS 会话复用。必须用 `SocketsHttpHandler`。
2. **必须关闭 TLS 1.3 会话复用。**

```csharp
// 复用会话时服务端不会重新出示证书，证书校验回调被跳过，
// 等于绕过下面的指纹固定。
handler.SslOptions.AllowTlsResume = false;
handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
```

`ValidateCertificate` 用 `certificate.GetRawCertData()` 算 SHA-256，与首次接触时记录的指纹比对；不一致直接拒绝。

### 5.3 路径穿越防护

`SafePathResolver.TryResolve` 的拒绝顺序至关重要：

```csharp
// 首尾空白会被 Windows 静默裁剪，导致实际写入路径与预期不一致 → 直接拒绝
if (!string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)) return false;

// 绝对路径 / UNC / 盘符 / ADS 必须在规范化之前就拒绝。
// 否则 "C:\a\b.txt" 会被 NormalizeRelativePath 去掉盘符而变成看似合法的相对路径。
if (relativePath.Contains(':')) return false;
if (Path.IsPathRooted(relativePath)) return false;
```

之后再做规范化与 `StartsWith(root)` 校验，确保结果永远落在受控根目录内。

### 5.4 设备身份变化

`TrustStore.EvaluateAsync(deviceId, fingerprint)` 若发现同一 `deviceId` 的指纹与登记值不同，返回 `IdentityChanged`，服务端对传输请求直接 `409 DEVICE_IDENTITY_CHANGED`，必须重新配对。

## 6. 线程模型与并发

- **UI 线程**：所有 WPF 绑定更新。网络/同步事件通过 `Services/UiDispatcher.cs` 的 `Post`/`InvokeAsync` 切回。
- **Kestrel 线程池**：处理 HTTP 请求。所有 IO 均为 `async`，全程传 `CancellationToken`。
- **UDP 接收循环**：独立 `Task.Run`，用 `CancellationTokenSource` 停止。
- **同步引擎**：串行执行（同一 `SyncPair` 不并发），避免基线写入竞争。
- **限流**：`TransferServer` 生命周期用 `SemaphoreSlim(1,1)`；`SettingsService` 写入用 `SemaphoreSlim(1,1)`；`DatabaseInitializer` 初始化用 `SemaphoreSlim(1,1)`。
- **无 `async void`**，除 WPF 事件处理器（`OnDrop` 等，且内部 `try/catch`）。

## 7. 错误处理策略

- 统一错误体 `ApiErrorResponse { success, errorCode, message, details? }`，错误码集中在 `ErrorCodes`。
- **不吞异常**：底层异常一律记日志后向上抛或转成明确错误码；只有「配置损坏」「客户端证书加载失败」等可降级场景才记录后继续。
- 传输失败写入 `Transfers.ErrorCode` / `ErrorMessage`，历史记录页可查。

## 8. 可扩展点

| 需求 | 扩展位置 |
| --- | --- |
| 新增 API 端点 | `TransferServer.MapEndpoints` + `Common/Protocol` 加 DTO |
| 新增同步模式 | `SyncMode` 枚举 + `SyncPlanner` 分支 |
| 更换发现协议（如 mDNS） | 实现 `IDiscoveryService`，在 `ServiceRegistration` 替换注册 |
| 更换存储（如 LiteDB） | 实现仓储接口，替换 `Storage` 模块 |
| 新 UI 皮肤 | `App/Resources/Styles.xaml` + `AppSettings.Theme` |
