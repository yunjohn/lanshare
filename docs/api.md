# LAN Transfer HTTP API

> Base URL：`https://<device-ip>:39521/api/v1` · 协议版本：`1`

所有端点由接收端 Kestrel 提供，走 **HTTPS（双向 TLS）**。除 `/health` 外，所有请求必须携带设备头（见 [protocol.md §5.1](./protocol.md#51-通用请求头)）。

---

## 端点总览

| # | 方法 | 路径 | 说明 |
| --- | --- | --- | --- |
| 1 | `GET` | `/health` | 健康检查（无需设备头） |
| 2 | `GET` | `/device` | 查询对端设备信息与证书指纹 |
| 3 | `POST` | `/pair` | 发起配对 |
| 4 | `POST` | `/transfers` | 建立传输（单个文件） |
| 5 | `GET` | `/transfers/{transferId}` | 查询传输状态（断点续传核心） |
| 6 | `PUT` | `/transfers/{transferId}/chunks/{chunkIndex}` | 上传单个分块 |
| 7 | `POST` | `/transfers/{transferId}/complete` | 完成并校验 |
| 8 | `POST` | `/transfers/{transferId}/approve` | 接收端批准 |
| 9 | `POST` | `/transfers/{transferId}/reject` | 接收端拒绝 |
| 10 | `POST` | `/transfers/{transferId}/pause` | 暂停 |
| 11 | `POST` | `/transfers/{transferId}/resume` | 继续 |
| 12 | `POST` | `/transfers/{transferId}/cancel` | 取消 |
| 13 | `POST` | `/sync/pairs` | 请求建立同步关系 |
| 14 | `POST` | `/sync/pairs/{syncPairId}/notify` | 通知发起端立即执行实时同步 |
| 15 | `POST` | `/sync/pairs/{syncPairId}/manifest` | 交换同步清单 |
| 16 | `GET` | `/sync/pairs/{syncPairId}/content` | 读取同步文件内容 |
| 17 | `PUT` | `/sync/pairs/{syncPairId}/content` | 写入同步文件内容 |
| 18 | `POST` | `/sync/pairs/{syncPairId}/delete` | 删除传播 |

---

## 1. GET /health

健康检查，**不需要设备头**。

**响应 200**

```json
{
  "success": true,
  "status": "ok",
  "protocolVersion": 1,
  "appVersion": "1.0.0",
  "serverTimeUtc": "2026-09-24T07:45:01.123+00:00"
}
```

---

## 2. GET /device

查询对端设备身份、端口与证书指纹。用于「手工 IP 连接」时确认目标并记录指纹。

**响应 200**

```json
{
  "success": true,
  "deviceId": "0f8c1e2a-...",
  "deviceName": "DESKTOP-B",
  "appVersion": "1.0.0",
  "protocolVersion": 1,
  "port": 39521,
  "certificateFingerprint": "A1B2C3...（SHA-256 大写十六进制）",
  "osVersion": "Microsoft Windows NT 10.0.26100.0",
  "machineName": "DESKTOP-B"
}
```

---

## 3. POST /pair

发起配对。两端独立推导 6 位验证码并比对，接收端弹窗由用户确认。

**请求体** `PairRequest`

```json
{
  "deviceId": "0f8c1e2a-...",
  "deviceName": "DESKTOP-A",
  "appVersion": "1.0.0",
  "protocolVersion": 1,
  "certificateFingerprint": "A1B2C3...",
  "pairingSessionId": "7d3e...",
  "verificationCode": "482913"
}
```

**响应 200（接受）**

```json
{
  "success": true,
  "accepted": true,
  "verificationCode": "482913",
  "deviceId": "9a2b...",
  "deviceName": "DESKTOP-B",
  "certificateFingerprint": "D4E5F6..."
}
```

**响应 200（对方拒绝）**

```json
{
  "success": false,
  "accepted": false,
  "verificationCode": "482913",
  "errorCode": "PAIRING_REJECTED",
  "message": "对方用户拒绝配对。"
}
```

**错误**

| 状态 | errorCode | 触发条件 |
| --- | --- | --- |
| 400 | `BAD_REQUEST` | 请求体非法 / 缺 `deviceId` 或 `pairingSessionId` |
| 400 | `INVALID_PAIRING_CODE` | 验证码不匹配（会话 ID 被篡改） |
| 400 | `UNAUTHORIZED` | 未收到客户端证书，无法绑定身份 |
| 408 | `TIMEOUT` | 等待本机用户确认超时（3 分钟） |

---

## 4. POST /transfers

建立传输。接收端在此完成：协议版本校验 → 信任判定 → 路径安全解析 → 重名处理 → 预分配 `.part`。

**请求体** `CreateTransferRequest`

```json
{
  "transferType": "file",
  "protocolVersion": 1,
  "transferId": "6f1a...",
  "fileName": "report.docx",
  "relativePath": "report.docx",
  "fileSize": 10485760,
  "sha256": "9F86D0...",
  "chunkSize": 4194304,
  "totalChunks": 3,
  "fileIndex": 0,
  "totalFiles": 1,
  "totalSize": 10485760,
  "rootName": "report.docx",
  "conflictPolicy": "rename",
  "lastWriteTimeUtc": "2026-09-24T07:40:00+00:00",
  "syncPairId": null
}
```

| 字段 | 说明 |
| --- | --- |
| `transferType` | `"file"` 或 `"folder"` |
| `relativePath` | 文件夹传输时为「根文件夹名/子路径」；单文件为文件名 |
| `chunkSize` | 1 / 2 / 4 / 8 / 16 MiB |
| `totalChunks` | `ceil(fileSize / chunkSize)`，空文件为 0 |
| `conflictPolicy` | `"overwrite"` / `"rename"`（默认） / `"skip"` |
| `syncPairId` | 非空时写入该同步关系目录而非默认下载目录；**只接受已登记且已授权的同步关系** |

**响应 200**

```json
{
  "success": true,
  "transferId": "6f1a...",
  "fileId": "b21c...",
  "state": "waiting-approval",
  "resolvedFileName": "report (1).docx"
}
```

`resolvedFileName` 在发生重名时返回实际落盘文件名。

**错误**

| 状态 | errorCode | 触发条件 |
| --- | --- | --- |
| 400 | `BAD_REQUEST` | 请求体非法 / 缺设备头 |
| 409 | `PROTOCOL_INCOMPATIBLE` | `protocolVersion` 与本机不符 |
| 409 | `DEVICE_IDENTITY_CHANGED` | 已信任设备的证书指纹发生变化 |
| 400 | `PATH_ESCAPE_DETECTED` | 相对路径越界（`../`、绝对路径、UNC、ADS） |
| 400 | `INVALID_FILE_NAME` | 文件名非法或含首尾空白 |
| 507 | `INSUFFICIENT_DISK_SPACE` | 目标磁盘剩余空间不足 |

---

## 5. GET /transfers/{transferId}

查询传输状态。**断点续传的核心接口** —— `completedChunks` 列出已成功落盘的分块索引。

**响应 200**

```json
{
  "success": true,
  "transferId": "6f1a...",
  "state": "transferring",
  "fileId": "b21c...",
  "fileName": "report.docx",
  "fileSize": 10485760,
  "chunkSize": 4194304,
  "totalChunks": 3,
  "completedChunks": [0, 2],
  "receivedBytes": 8388608,
  "sha256": "9F86D0..."
}
```

发送端重连后只重传 `totalChunks` 中不在 `completedChunks` 里的块。

**错误**：`404 TRANSFER_NOT_FOUND`

---

## 6. PUT /transfers/{transferId}/chunks/{chunkIndex}

上传单个分块。**Body 为分块原始字节**，`Content-Type: application/octet-stream`。接收端 `Seek(chunkIndex * chunkSize)` 随机写入 `.part` 并把位图对应位置位。**允许乱序、允许并发。**

**路径参数**：`chunkIndex`（0-based，`int`）

**请求**

```
PUT /api/v1/transfers/6f1a.../chunks/1 HTTP/1.1
Content-Type: application/octet-stream
Content-Length: 4194304
X-Lan-Transfer-Device-Id: 0f8c1e2a-...
...（其余设备头）

<4 MiB 二进制>
```

**响应 200** `ChunkUploadResponse`

```json
{
  "success": true,
  "transferId": "6f1a...",
  "chunkIndex": 1,
  "completedChunks": 2,
  "totalChunks": 3,
  "state": "transferring"
}
```

**错误**

| 状态 | errorCode | 触发条件 |
| --- | --- | --- |
| 400 | `CHUNK_INDEX_OUT_OF_RANGE` | 索引 ≥ `totalChunks` |
| 400 | `CHUNK_SIZE_MISMATCH` | 分块长度不符合预期（末块除外） |
| 409 | `TRANSFER_STATE_CONFLICT` | 传输已暂停/取消/终态 |
| 507 | `INSUFFICIENT_DISK_SPACE` | 磁盘写入失败（空间不足） |

---

## 7. POST /transfers/{transferId}/complete

全部块到齐后调用。接收端流式计算 `.part` 的 SHA-256，与请求中的 `sha256` 比对。

**请求体** `CompleteTransferRequest`

```json
{ "fileIndex": 0, "sha256": "9F86D0..." }
```

**响应 200（成功）**

```json
{
  "success": true,
  "state": "completed",
  "verified": true,
  "savedPath": "C:\\Users\\xu\\Downloads\\LAN Transfer\\report.docx"
}
```

**响应（校验失败）**

```json
{
  "success": false,
  "state": "verification-failed",
  "verified": false,
  "errorCode": "HASH_MISMATCH",
  "message": "文件校验失败，已保留 .part 文件。"
}
```

> 校验通过前文件始终是 `.part`，**不会**以正式文件名出现在磁盘上。

---

## 8–12. 传输控制

| 端点 | 说明 | 目标状态 |
| --- | --- | --- |
| `POST /transfers/{id}/approve` | 接收端用户接受 | `Preparing` → `Transferring` |
| `POST /transfers/{id}/reject` | 接收端用户拒绝 | `Rejected` |
| `POST /transfers/{id}/pause` | 暂停（当前块允许完成） | `Paused` |
| `POST /transfers/{id}/resume` | 继续 | `Transferring` |
| `POST /transfers/{id}/cancel` | 取消 | `Cancelled` |

均为空请求体（`cancel` 可带 `?deletePartial=true`）。

**响应 200** `SimpleOperationResponse`

```json
{ "success": true, "transferId": "6f1a...", "state": "paused" }
```

**错误**：`409 TRANSFER_STATE_CONFLICT`（当前状态不允许该操作）、`404 TRANSFER_NOT_FOUND`

---

## 13. POST /sync/pairs

请求建立同步关系。**必须由接收端用户确认**，且可修改实际使用的本地目录。

**请求体** `SyncRequest`

```json
{
  "syncPairId": "c8d1...",
  "name": "文档同步",
  "remoteDeviceId": "9a2b...",
  "remoteDeviceName": "DESKTOP-B",
  "requesterLocalPath": "D:\\Docs",
  "targetRemotePath": "D:\\Docs",
  "mode": "two-way",
  "certificateFingerprint": "D4E5F6..."
}
```

`mode`：`"two-way"` / `"send-only"` / `"receive-only"`。

**响应 200**

```json
{
  "success": true,
  "accepted": true,
  "syncPairId": "c8d1...",
  "resolvedLocalPath": "D:\\Sync\\Docs"
}
```

**错误**

| 状态 | errorCode | 触发条件 |
| --- | --- | --- |
| 400 | `BAD_REQUEST` | 请求体非法 |
| 403 | `SYNC_REJECTED` | 接收端用户拒绝 |
| 409 | `SYNC_PAIR_ERROR` | 同步关系已存在或状态冲突 |
| 404 | `DEVICE_NOT_TRUSTED` | 设备未配对 |

---

## 14. POST /sync/pairs/{syncPairId}/manifest

交换同步清单，是「三态比较」的数据源。

**请求体** `SyncManifestRequest`

```json
{
  "syncPairId": "c8d1...",
  "entries": [
    {
      "relativePath": "docs\\a.docx",
      "fileId": "b21c...",
      "isDirectory": false,
      "fileSize": 1048576,
      "lastWriteTimeUtc": "2026-09-24T07:40:00+00:00",
      "sha256": "9F86D0...",
      "version": 3,
      "deleted": false
    }
  ],
  "manifestOnly": false,
  "fullScan": false
}
```

- `manifestOnly=true`：只请求对端清单，不上传本地清单。
- `fullScan=true`：请求对端执行全量扫描后再返回。
- `sha256` **仅在 size/mtime 变化时提供**，可省略。
- `deleted=true` 表示墓碑，用于删除传播。

**响应 200** `SyncManifestResponse`

```json
{
  "success": true,
  "syncPairId": "c8d1...",
  "entries": [ { "relativePath": "...", "fileSize": 0, "deleted": false } ]
}
```

**错误**：`404 SYNC_PAIR_NOT_FOUND`、`403 SYNC_REJECTED`（对端未授权）

---

## 15. GET /sync/pairs/{syncPairId}/content

读取同步目录中的文件内容，**流式返回**。

**查询参数**

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `relativePath` | 是 | 同步目录内的相对路径（URL 编码） |

**响应 200**

```
Content-Type: application/octet-stream
Content-Length: 1048576

<文件原始字节，128 KiB 缓冲流式传输>
```

**错误**：`404 FILE_NOT_FOUND` / `404 SYNC_PAIR_NOT_FOUND` / `400 PATH_ESCAPE_DETECTED`

---

## 16. PUT /sync/pairs/{syncPairId}/content

写入同步目录中的文件。服务端**先写 `.part` 再原子改名**，避免对端读到半截文件。

**查询参数**

| 参数 | 必填 | 说明 |
| --- | --- | --- |
| `relativePath` | 是 | 同步目录内的相对路径 |

**请求**：Body 为文件原始字节，`Content-Type: application/octet-stream`

**响应 200** `SimpleOperationResponse`

```json
{ "success": true, "transferId": "", "state": "completed" }
```

**错误**：`400 PATH_ESCAPE_DETECTED` / `400 BAD_REQUEST` / `507 INSUFFICIENT_DISK_SPACE`

---

## 17. POST /sync/pairs/{syncPairId}/delete

删除传播。对端据此删除文件并写入墓碑。

**请求体** `SyncDeleteRequest`

```json
{ "relativePaths": ["docs\\old.docx", "docs\\sub\\x.txt"] }
```

**响应 200** `SimpleOperationResponse`

```json
{ "success": true, "state": "completed" }
```

**错误**：`400 BAD_REQUEST`（未指定文件）/ `400 PATH_ESCAPE_DETECTED`

---

## 错误码

全部错误码定义于 `LanTransfer.Common.Protocol.ErrorCodes`。

### 通用

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `NONE` | — | 无错误 |
| `BAD_REQUEST` | 400 | 请求格式错误 |
| `UNAUTHORIZED` | 400 | 缺少客户端证书 / 身份不可绑定 |
| `INTERNAL_ERROR` | 500 | 服务端内部错误 |
| `TIMEOUT` | 408 | 操作超时 |
| `ACCESS_DENIED` | 403 | 文件系统权限不足 |

### 协议与信任

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `PROTOCOL_INCOMPATIBLE` | 409 | 协议版本不匹配，拒绝降级 |
| `DEVICE_NOT_TRUSTED` | 404 | 设备未配对 |
| `DEVICE_IDENTITY_CHANGED` | 409 | 设备证书指纹变化，必须重新配对 |
| `PAIRING_REJECTED` | 200 | 对方用户拒绝配对 |
| `INVALID_PAIRING_CODE` | 400 | 配对验证码不匹配 |

### 传输

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `TRANSFER_NOT_FOUND` | 404 | 传输 ID 不存在 |
| `TRANSFER_STATE_CONFLICT` | 409 | 当前状态不允许该操作 |
| `CHUNK_INDEX_OUT_OF_RANGE` | 400 | 分块索引越界 |
| `CHUNK_SIZE_MISMATCH` | 400 | 分块大小不符 |
| `INSUFFICIENT_DISK_SPACE` | 507 | 磁盘空间不足 |
| `HASH_MISMATCH` | 200 | SHA-256 校验失败 |
| `SOURCE_CHANGED_DURING_TRANSFER` | 409 | 源文件传输中被修改 |
| `REJECTED_BY_USER` | — | 接收端用户拒绝 |
| `CANCELLED_BY_USER` | — | 用户取消 |

### 路径安全

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `PATH_ESCAPE_DETECTED` | 400 | 相对路径越界（`../`、绝对路径、UNC、ADS） |
| `INVALID_FILE_NAME` | 400 | 文件名非法（保留字符、首尾空白、超长） |
| `FILE_NOT_FOUND` | 404 | 文件不存在 |

### 网络

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `NETWORK_UNREACHABLE` | — | 网络不可达 |
| `FIREWALL_SUSPECTED` | — | 疑似被防火墙拦截 |

### 同步

| 错误码 | HTTP | 含义 |
| --- | --- | --- |
| `SYNC_PAIR_NOT_FOUND` | 404 | 同步关系不存在 |
| `SYNC_PAIR_ERROR` | 409 | 同步关系状态冲突 |
| `SYNC_CONFLICT` | 200 | 存在冲突待用户裁决 |
| `SYNC_REJECTED` | 403 | 对端拒绝同步请求 |
