# LAN Transfer 通信协议

> 协议版本：`1` · API 前缀：`/api/v1` · 默认端口：UDP 39520（发现）/ TCP 39521（传输）

本文档描述 LAN Transfer 在局域网内使用的全部线格式。任何实现都必须与本文档一致；协议版本不匹配时**必须明确拒绝，不得静默降级**。

---

## 1. 总览

| 层 | 协议 | 端口 | 用途 |
| --- | --- | --- | --- |
| 设备发现 | UDP 广播 + JSON（UTF-8） | 39520 | 互相发现、交换设备名/IP/端口 |
| 控制与数据传输 | HTTPS/1.1 & HTTP/2 + JSON / octet-stream | 39521 | 配对、建立传输、分块上传、同步 |
| 身份 | TLS 双向认证 + 应用层指纹固定 | — | 防中间人，不污染系统根证书 |

所有 JSON 字段均使用**显式 `JsonPropertyName`**（camelCase），与 .NET 命名策略无关，`PropertyNameCaseInsensitive = true`，反序列化失败返回 `null` 而不抛异常。

---

## 2. 设备发现协议（UDP）

### 2.1 报文结构

`DiscoveryMessage`（UTF-8 JSON，单包 ≤ 1 KB）：

```json
{
  "protocol": "lan-transfer",
  "type": "discover",
  "version": 1,
  "deviceId": "0f8c1e2a-...",
  "deviceName": "DESKTOP-A",
  "appVersion": "1.0.0",
  "port": 39521
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `protocol` | string | 固定 `"lan-transfer"`，不匹配即丢弃 |
| `type` | string | `"discover"` 或 `"discover-response"` |
| `version` | int | 协议版本，当前 `1` |
| `deviceId` | string | 设备唯一 ID（GUID），持久化 |
| `deviceName` | string | 用户可见设备名 |
| `appVersion` | string | 应用语义版本 |
| `port` | int | 对端 TCP 传输端口 |

### 2.2 交互时序

```
设备 A                                            设备 B
   │                                                │
   │  每 3 秒：DISCOVER 广播到 255.255.255.255:39520 │
   │  （同时对每个有效网卡的子网广播地址定向发送）    │
   │ ──────────────────────────────────────────────▶│
   │                                                │
   │ ◀──────────────────────────────────────────────│
   │      DISCOVER-RESPONSE 单播回 A:39520           │
   │                                                │
   双方各自记录对方：deviceId / deviceName / IP / port
```

### 2.3 处理规则

1. **只接受 `protocol == "lan-transfer"`** 的报文，其余静默丢弃。
2. **忽略自己发出的报文**（多网卡回环），按 `deviceId` 比对。
3. **IP 一律取 UDP 实际 Source IP**，不使用报文里的任何地址字段，防止伪造。
4. 收到 `discover` → 记录设备 + 单播回 `discover-response`。
5. 收到 `discover-response` → 仅记录设备，不再回复（避免广播风暴）。
6. 单张网卡发送失败只记 Debug 日志，不影响其它网卡。
7. ICMP 端口不可达等 `SocketException` 不终止接收循环。
8. **超过 10 秒未收到任何报文判定离线**（`AppConstants.OfflineThreshold`），每秒扫描一次。

### 2.4 广播地址推导

```
对每个有效的 IPv4 网卡（排除 Loopback / 未启用 / 无效虚拟网卡）：
    broadcast = ip | ~mask        // NetworkHelper.ComputeBroadcast
追加兜底地址 255.255.255.255
```

`ComputeBroadcast` 会校验地址族为 `InterNetwork`，对 IPv6 直接抛 `ArgumentException`（避免取前 4 字节算出无意义地址）。`PrefixToMask` 对非法前缀（<0 或 >32）回退为 `/24`。

---

## 3. TLS 与身份

### 3.1 证书

- 首次运行生成 **RSA 2048 / SHA-256 自签名证书**，含 SAN。
- 私钥口令用 **Windows DPAPI（CurrentUser）** 加密后存 `identity.key`，证书存 `identity.pfx`。
- `CertificateFingerprint` = 证书 DER 的 **SHA-256 大写十六进制**。
- **不安装到系统根证书存储**。

### 3.2 握手

- 服务端：`UseHttps` + `ClientCertificateMode.AllowCertificate`，`ClientCertificateValidation` 恒 `true`。
- 客户端：出示本机证书。
- **信任判定在应用层完成**（见 §3.3），TLS 层只负责加密与拿到证书。

### 3.3 客户端指纹固定（Pinning）

客户端首次接触某设备时记录其证书指纹；后续所有连接用 `RemoteCertificateValidationCallback` 校验：

```csharp
handler.SslOptions.AllowTlsResume = false;   // 关键：见下
handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
```

> **为什么必须 `AllowTlsResume = false`？**
> TLS 1.3 会话复用时服务端**不会重新出示证书**，证书校验回调被跳过 —— 等于指纹固定完全失效。关闭会话复用后每次连接都会重新验证证书。

校验逻辑：取 `certificate.GetRawCertData()` → SHA-256 → 与登记指纹比对，不一致直接拒绝连接。主机名从 `(sender as SslStream)?.TargetHostName` 获取。

### 3.4 设备信任状态

| 状态 | 含义 | 行为 |
| --- | --- | --- |
| `Unknown` | 从未配对 | 传输需用户确认（`WaitingApproval`） |
| `Pending` | 配对中，等待确认验证码 | 不允许传输 |
| `Trusted` | 指纹已登记且一致 | 可直接传输 |
| `Revoked` | 用户主动解除 | 等同 Unknown |
| `IdentityChanged` | 同一 DeviceId 指纹变化 | **传输请求一律 `409 DEVICE_IDENTITY_CHANGED`** |

---

## 4. 配对协议

### 4.1 验证码推导

两端用**同一算法**从 `pairingSessionId` + 双方 `deviceId` 推导 6 位验证码：

```
verificationCode = DeriveVerificationCode(pairingSessionId, myDeviceId, peerDeviceId)
```

发起方生成 `pairingSessionId` 并显示验证码；接收方独立计算，**若收到的验证码与自己算出的不一致，说明会话 ID 被篡改，直接拒绝**。

### 4.2 时序

```
A（发起方）                                         B（接收方）
   │  POST /api/v1/pair                              │
   │  { deviceId, deviceName, appVersion,            │
   │    protocolVersion, certificateFingerprint,     │
   │    pairingSessionId, verificationCode }         │
   │ ───────────────────────────────────────────────▶│
   │                                                 │ 校验验证码
   │                                                 │ 要求客户端证书存在
   │                                                 │ 弹出「A 请求配对，验证码 123456」
   │                                                 │ 用户点击接受/拒绝
   │ ◀───────────────────────────────────────────────│
   │  { success, accepted, verificationCode,          │
   │    deviceId, deviceName, certificateFingerprint }│
   │                                                 │
   │ 双方各自 TrustStore.Trust(对方 deviceId, 指纹)   │
```

- 等待用户确认超时 **3 分钟** → `408 TIMEOUT`。
- 用户拒绝 → HTTP 200 + `accepted=false` + `errorCode=PAIRING_REJECTED`。
- **未收到客户端证书** → `400 UNAUTHORIZED`（无法把 DeviceId 与指纹绑定）。

---

## 5. 传输协议（HTTPS）

### 5.1 通用请求头

所有 `/api/v1/*` 请求（除 `health`）必须携带：

| 头 | 说明 |
| --- | --- |
| `X-Lan-Transfer-Device-Id` | 发送方设备 ID（必填，缺失返回 `400`） |
| `X-Lan-Transfer-Device-Name` | 发送方设备名（URI 编码） |
| `X-Lan-Transfer-Protocol-Version` | 协议版本 |
| `X-Lan-Transfer-App-Version` | 应用版本 |

### 5.2 统一响应与错误

成功响应各自定义；失败统一为：

```json
{
  "success": false,
  "errorCode": "PATH_ESCAPE_DETECTED",
  "message": "相对路径越界。",
  "details": { "relativePath": "../etc/passwd" }
}
```

`details` 为可选，未设置时不输出（`JsonIgnoreCondition.WhenWritingNull`）。

**错误码全表见 [api.md](./api.md#错误码)。**

### 5.3 分块传输时序（单个文件）

```
发送端 A                                          接收端 B
   │  POST /transfers                                │
   │  { transferType, transferId, fileName,          │
   │    relativePath, fileSize, sha256, chunkSize,   │
   │    totalChunks, fileIndex, totalFiles,          │
   │    totalSize, rootName, conflictPolicy,         │
   │    lastWriteTimeUtc, syncPairId? }              │
   │ ───────────────────────────────────────────────▶│
   │                                                 │ 校验协议版本
   │                                                 │ 校验信任状态（IdentityChanged → 409）
   │                                                 │ SafePathResolver 解析路径
   │                                                 │ 处理重名（默认 Rename）
   │                                                 │ 预分配 .part
   │ ◀───────────────────────────────────────────────│
   │  { success, transferId, fileId,                  │
   │    state: "waiting-approval", resolvedFileName } │
   │                                                 │
   │  GET /transfers/{transferId}   （轮询直到 transferring）
   │ ───────────────────────────────────────────────▶│
   │ ◀───────────────────────────────────────────────│
   │  { state: "transferring", completedChunks: [...] }│
   │                                                 │
   │  PUT /transfers/{id}/chunks/{i}                 │
   │  Content-Type: application/octet-stream         │
   │  Body: 4 MiB 原始字节                            │
   │ ───────────────────────────────────────────────▶│
   │                                                 │ Seek(i*chunkSize) 随机写入 .part
   │                                                 │ 位图置位 + 落库
   │ ◀───────────────────────────────────────────────│
   │  { success, chunkIndex, completedChunks, totalChunks }│
   │  （重复 i 次，可并发，可乱序）                    │
   │                                                 │
   │  POST /transfers/{id}/complete                  │
   │  { fileIndex, sha256 }                          │
   │ ───────────────────────────────────────────────▶│
   │                                                 │ 流式 SHA-256 校验 .part
   │                                                 │ 一致 → 改名正式文件
   │                                                 │ 不一致 → VerificationFailed
   │ ◀───────────────────────────────────────────────│
   │  { success, state: "completed", verified: true,  │
   │    savedPath }                                   │
```

### 5.4 断点续传

中断后重连，发送端先查状态：

```
GET /api/v1/transfers/{transferId}
→ { state, chunkSize, totalChunks, completedChunks: [0,1,2,5,7], receivedBytes, sha256 }
```

`completedChunks` 是**已成功落盘的块索引列表**（来自 `TransferChunks.Bitmap` 位图）。发送端只重传缺失块。

- 位图存储：`100 GB / 4 MiB = 25600 块` → 仅 **3200 字节** BLOB。
- `.part` 文件用 `Seek` 随机写入，允许乱序到达。
- 全部块到齐 → `Verifying` → SHA-256 通过才改名为正式文件名。

### 5.5 暂停 / 继续 / 取消

| 操作 | 端点 | 语义 |
| --- | --- | --- |
| 暂停 | `POST /transfers/{id}/pause` | 当前块允许完成，之后停止接收 |
| 继续 | `POST /transfers/{id}/resume` | 回到 `Transferring` |
| 取消 | `POST /transfers/{id}/cancel` | 终止；可选删除 `.part` |
| 批准 | `POST /transfers/{id}/approve` | 接收端用户接受 |
| 拒绝 | `POST /transfers/{id}/reject` | 接收端用户拒绝 → `Rejected` |

### 5.6 状态机线格式

| 枚举 | 线字符串 |
| --- | --- |
| `Pending` | `pending` |
| `Queued` | `queued` |
| `WaitingApproval` | `waiting-approval` |
| `Preparing` | `preparing` |
| `Transferring` | `transferring` |
| `Paused` | `paused` |
| `Verifying` | `verifying` |
| `Completed` | `completed` |
| `Rejected` | `rejected` |
| `Cancelled` | `cancelled` |
| `Failed` | `failed` |
| `VerificationFailed` | `verification-failed` |

---

## 6. 同步协议

同步复用同一 HTTPS 通道。**相对路径是同步的唯一标识**。

### 6.1 建立同步关系

```
POST /api/v1/sync/pairs
{
  "syncPairId": "...",
  "name": "文档同步",
  "remoteDeviceId": "...",
  "remoteDeviceName": "DESKTOP-B",
  "requesterLocalPath": "D:\\Docs",      // 仅展示用，对端不会使用
  "targetRemotePath": "D:\\Docs",        // 对端应使用的目录（可被对端用户修改）
  "mode": "two-way",
  "certificateFingerprint": "..."
}
→ { success, accepted, syncPairId, resolvedLocalPath }
```

`resolvedLocalPath` 是接收端**实际使用**的目录（用户可能改了），发起方以此为准。

### 6.2 清单交换（三态比较的数据源）

```
POST /api/v1/sync/pairs/{syncPairId}/manifest
{
  "syncPairId": "...",
  "entries": [ { relativePath, fileId, isDirectory, fileSize,
                 lastWriteTimeUtc, sha256?, version, deleted } ],
  "manifestOnly": false,   // true = 只要对端清单，不上传本地清单
  "fullScan": false        // true = 请求对端做全量扫描
}
→ { success, syncPairId, entries: [ ... 对端清单 ... ] }
```

- `sha256` **仅在 size/mtime 变化时提供**，避免每次全量哈希（大目录性能关键）。
- `deleted=true` 表示**墓碑**（Tombstone），用于删除传播。

### 6.3 内容读写与删除

| 操作 | 端点 | 说明 |
| --- | --- | --- |
| 读取 | `GET /api/v1/sync/pairs/{id}/content?relativePath=...` | 返回 `application/octet-stream`，流式 |
| 写入 | `PUT /api/v1/sync/pairs/{id}/content?relativePath=...` | Body 为文件原始字节 |
| 删除 | `POST /api/v1/sync/pairs/{id}/delete` | `{ relativePaths: [...] }` |

### 6.4 三态比较算法（`SyncPlanner`）

设 `B` = 基线（上次同步记录）、`L` = 本地、`R` = 远端：

| 条件 | 判定 | 动作 |
| --- | --- | --- |
| `L == R` | 一致 | `InSync`，无动作 |
| `L != B && R == B` | 仅本机改动 | `PendingLocalToRemote` → 上传 |
| `R != B && L == B` | 仅对端改动 | `PendingRemoteToLocal` → 下载 |
| `L != B && R != B && L != R` | 双方都改 | `Conflict` → 冲突副本 + 用户裁决 |
| `L 存在 && R 缺失 && B 存在` | 对端删除 | 传播删除（墓碑） |
| `L 缺失 && R 存在 && B 存在` | 本机删除 | 传播删除（墓碑） |
| `L 存在 && B 不存在 && R 缺失` | 新增 | 按模式上传 |

> **禁止只比较 `LastWriteTime`。** 判定"是否改动"必须综合 size + mtime，必要时用 SHA-256 确认。

### 6.5 冲突处理

冲突时保留两份，冲突副本命名：

```
report (conflict-PC-B-20260924-154501).docx
         └─ 冲突标记 ─┘ └ 对端机器名 ┘└ 时间戳 ┘
```

- 记录写入 `SyncConflicts` 表（含 `LocalSha256` / `RemoteSha256` / `ConflictCopyPath`）。
- 用户在同步页选择：**用本地** / **用远端** / **两份都保留**。
- 解析后置 `Resolved=1` 与 `ResolvedAt`。

### 6.6 触发方式（双保险）

1. **`FileSystemWatcher`** 实时监听，带防抖（避免编辑器多次写入触发风暴）。
2. **定期全量扫描**，默认 **10 分钟**（可选 5 / 10 / 30 / 60），兜底 watcher 丢事件的情况。

### 6.7 墓碑保留

- 删除操作写墓碑（`SyncEntries.Deleted=1` + `DeletedAt`）。
- 保留期默认 **30 天**（可选 7 / 30 / 90），过期清理。
- `DeletePropagated` 标记删除是否已传播到对端，避免重复删除请求。

---

## 7. 兼容性约定

1. **协议版本不匹配 → 拒绝**，返回 `409 PROTOCOL_INCOMPATIBLE`，不尝试降级。
2. 未知 JSON 字段**忽略**（`System.Text.Json` 默认行为）。
3. `ProtocolVersion == 0` 视为"未声明"，允许通过（兼容早期客户端）。
4. 所有 DTO 均显式标注 `JsonPropertyName`，重命名 C# 属性不影响线格式。
