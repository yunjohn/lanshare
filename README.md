# LAN Transfer

Windows 局域网 **点对点**文件/文件夹传输与**双向文件夹同步**工具。

- **纯局域网、无服务器**：UDP 广播自动发现 + 直连 HTTPS，不依赖 SMB、云服务或任何中心节点。
- **大文件友好**：0 B ~ 100 GB+ 全程流式传输，内存占用恒定，绝不整文件读入内存。
- **可续传、可校验**：分块位图持久化，断网/暂停后可精确续传；SHA-256 校验通过前文件始终是 `.part`。
- **双向同步**：三态比较（基线/本地/远端）+ 删除墓碑 + 冲突副本，不做静默覆盖。
- **后台驻留**：支持开机静默启动；关闭窗口时可选择退出或最小化到系统托盘。

技术栈：.NET 10 LTS · WPF (MVVM) · ASP.NET Core Kestrel · SQLite · Serilog

---

## 目录

- [功能特性](#功能特性)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [项目结构](#项目结构)
- [默认端口与数据目录](#默认端口与数据目录)
- [防火墙配置](#防火墙配置)
- [双机测试方法](#双机测试方法)
- [网络诊断](#网络诊断)
- [安全设计](#安全设计)
- [构建与打包](#构建与打包)
- [文档](#文档)
- [常见问题](#常见问题)

---

## 功能特性

### 传输

| 功能 | 说明 |
| --- | --- |
| 自动发现 | UDP 广播，同网段设备 10 秒内自动出现在列表 |
| 手工连接 | 直接输入 IP + 端口连接，适用于广播被交换机阻断的环境 |
| 多文件 / 文件夹 | 保留完整目录结构，支持一次选择混合的文件与文件夹 |
| 断点续传 | 分块位图持久化，重连后只传缺失块 |
| SHA-256 校验 | 接收端流式校验，不通过则文件不被改名 |
| 暂停 / 继续 / 取消 | 暂停时当前块允许完成，状态可持久恢复 |
| 可信配对 | 6 位验证码双向确认 + 证书指纹固定 |
| 接收确认 | 默认每个传输都需接收端用户确认（可对可信设备关闭） |
| 拖放 | 直接把文件/文件夹拖到传输页 |
| 实时反馈 | 速度、进度百分比、ETA、已传/总字节 |
| 历史记录 | 全部传输记录可查、可清空（保留未完成项） |

### 同步

| 功能 | 说明 |
| --- | --- |
| 双向 / 单向 | 双向、仅发送、仅接收三种模式 |
| 实时 + 定期 | `FileSystemWatcher` 实时触发 + 默认 10 分钟全量扫描双保险 |
| 三态比较 | 基线/本地/远端三态，准确区分「新增」与「删除」 |
| 删除传播 | 删除墓碑（默认保留 30 天），避免已删除文件复活 |
| 冲突处理 | 保留双方版本，生成 `name (conflict-PC-B-20260924-154501).ext`，由用户裁决 |
| 相对路径标识 | 以相对路径为唯一标识，绝对路径仅在各端本地解析 |

### 网络诊断

- 本机所有网卡 / IP / 掩码 / 广播地址一览
- 端口占用检查（UDP 39520 / TCP 39521）
- 对目标执行 **DNS → TCP → HTTPS API** 三段式连通性测试
- 网络类别识别（专用 / 公用 / 域），公用网络给出安全提醒

### 桌面体验

- 在「设置」中启用“开机自动启动”后，Windows 登录时使用 `--background` 参数静默启动，只驻留系统托盘。
- 关闭主窗口时会询问“退出 / 最小化到托盘 / 取消”；托盘菜单可重新打开窗口或彻底退出。
- 最小化到托盘后，文件接收、设备发现和实时同步仍会继续运行。

---

## 系统要求

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809+ / Windows 11（x64） |
| 运行时 | .NET 10 Desktop Runtime（框架依赖版）；或使用自包含便携版（免安装） |
| 开发 SDK | .NET SDK 10.0.4xx 及以上 |
| 网络 | 两台设备处于同一局域网（同一交换机 / 同一 WiFi） |
| 权限 | 普通用户即可（`asInvoker`，不申请管理员） |

> 已启用 **长路径感知**（`longPathAware`），支持超过 260 字符的路径。

---

## 快速开始

### 方式一：使用便携版（推荐给最终用户）

1. 下载 `dist/LanTransfer-1.0.0-win-x64-portable.zip`
2. 解压到任意目录
3. 双击 `LanTransfer.exe`
4. 首次运行 Windows 防火墙会弹窗 —— **必须勾选「专用网络」并允许访问**

### 方式二：从源码运行（开发者）

```bash
# 1. 还原 + 构建
dotnet build LanTransfer.sln -c Release

# 2. 运行
dotnet run --project src/LanTransfer.App/LanTransfer.App.csproj -c Release
```

### 方式三：跑测试

```bash
dotnet test LanTransfer.sln -c Release
# 预期：223 个测试全部通过
```

---

## 项目结构

```
LanTransfer.sln
├── src/
│   ├── LanTransfer.Common      # 常量、DTO、枚举、状态机（无项目依赖）
│   ├── LanTransfer.Core        # 路径安全、哈希、分块、速度、传输/设备管理、配置
│   ├── LanTransfer.Security    # 证书身份（DPAPI）、信任库、配对
│   ├── LanTransfer.Storage     # SQLite 连接、建表、仓储
│   ├── LanTransfer.Network     # Kestrel 服务端 + HttpClient + UDP 发现 + 诊断
│   ├── LanTransfer.Sync        # 双向同步：扫描/规划/基线/监听/冲突/引擎
│   └── LanTransfer.App         # WPF 表现层（MVVM）
├── tests/
│   ├── LanTransfer.Core.Tests         # 117 通过
│   ├── LanTransfer.Network.Tests      #  41 通过
│   └── LanTransfer.IntegrationTests   #  65 通过
├── docs/                       # architecture / protocol / api / database / testing
├── tools/                      # dotnet-install.ps1 等辅助脚本
├── Directory.Build.props       # 统一编译属性
├── publish.ps1                 # 打包脚本
└── README.md
```

**依赖方向严格单向**：`App → {Network, Security, Storage, Sync} → Core → Common`。
`Core` 不依赖 WPF，`Network` 不依赖 UI，组装点唯一（`App/Startup/ServiceRegistration.cs`）。

---

## 默认端口与数据目录

### 端口

| 用途 | 协议 | 默认端口 | 可配置 |
| --- | --- | --- | --- |
| 设备发现 | UDP | 39520 | 是 |
| 文件传输 | TCP（HTTPS） | 39521 | 是 |

> **端口被占用不会导致程序崩溃** —— 服务只记录错误，UI 提示你修改端口后重试。可在「网络诊断」页检查占用。

### 分块大小

默认 **4 MiB**，可选 1 / 2 / 4 / 8 / 16 MiB。在「设置」页修改。

### 数据目录

```
%LOCALAPPDATA%\LanTransfer\
├── lan-transfer.db          # 数据库（设备、传输、同步）
├── settings.json            # 配置
├── identity.pfx / identity.key   # 自签名证书与 DPAPI 保护的私钥口令
└── Logs\                    # 日志（按天滚动，保留 14 个）
```

> **完全重置**：删除 `%LOCALAPPDATA%\LanTransfer` 目录即可（会丢失信任关系与历史记录）。

---

## 防火墙配置

首次运行时 Windows 会弹出防火墙提示。**必须允许「专用网络」访问**，否则对端无法连接。

若已误点「取消」，用**管理员 PowerShell** 手动放行：

```powershell
New-NetFirewallRule -DisplayName "LAN Transfer (UDP-In)" -Direction Inbound `
  -Protocol UDP -LocalPort 39520 -Action Allow -Profile Private

New-NetFirewallRule -DisplayName "LAN Transfer (TCP-In)" -Direction Inbound `
  -Protocol TCP -LocalPort 39521 -Action Allow -Profile Private
```

自定义端口时替换 `-LocalPort`。**不建议**应用到 `Public` 配置文件。

查看 / 删除规则：

```powershell
Get-NetFirewallRule -DisplayName "LAN Transfer*" | Format-Table DisplayName, Enabled, Profile
Remove-NetFirewallRule -DisplayName "LAN Transfer*"
```

> 也可临时把当前网络从「公用」改为「专用」：设置 → 网络和 Internet → 以太网/WiFi → 网络配置文件类型 → 专用。

---

## 双机测试方法

### 1. 准备

- 两台 Windows 机器接入**同一局域网**（同一交换机或同一 WiFi）
- 分别启动 `LanTransfer.exe`
- 确认防火墙已放行

### 2. 自动发现

打开「设备」页。正常情况下 **10 秒内**对方设备会出现在列表，状态显示「在线」。
若没有出现，见下方[排查](#设备互相发现不了)。

### 3. 手工连接（广播被阻断时）

在「设备」页点击「手工连接」，输入对方 IP（默认端口 39521）。连接成功后会显示对方设备名与**证书指纹**。

在对方机器上执行 `ipconfig` 可查看其 IPv4 地址。

### 4. 传输

1. 在「传输」页把文件/文件夹**拖入窗口**，或点击「选择文件 / 选择文件夹」
2. 选择目标设备
3. 点击「发送」
4. 接收端弹出确认框 → 点击「接受」
5. 观察进度：速度、百分比、ETA
6. 传输完成后两端状态均为「已完成」

### 5. 验证完整性

两端分别对文件计算 SHA-256，应完全一致：

```powershell
Get-FileHash "C:\path\to\file.zip" -Algorithm SHA256
```

### 6. 测试断点续传

传输大文件（建议 1 GB 以上）时点「暂停」，然后「继续」。
接收端日志与 UI 会显示**只传输剩余块**。也可直接断开网络，恢复后重试。

### 7. 配对与信任

1. 「设备」页对目标设备点击「配对」
2. 两端会显示**相同的 6 位验证码** —— 比对一致后确认
3. 配对成功后该设备标记为「已信任」，后续传输无需再次确认

> 若两端验证码不一致，说明会话被篡改，**不要确认**。

### 8. 双向同步

1. 「同步」页点击「新建同步」
2. 选择本地目录与目标设备
3. 对端弹出请求 → 确认并选择它自己的目录
4. 此后任一端增删改文件，另一端会在 10 秒内自动同步
5. 两端同时修改同一文件会产生冲突副本，在同步页选择「用本地 / 用远端 / 两份都保留」

---

## 网络诊断

「网络诊断」页提供：

| 功能 | 说明 |
| --- | --- |
| 本机网卡列表 | 网卡名、IPv4、子网掩码、广播地址、是否虚拟 |
| 端口占用检查 | 检查 UDP 39520 / TCP 39521 是否被其它进程占用 |
| 连通性测试 | 对目标执行 DNS → TCP → HTTPS API 三段测试，定位卡在哪一步 |
| 网络类别 | 显示当前是「专用 / 公用 / 域」网络 |
| 建议 | 根据失败环节给出具体修复建议（防火墙、IP、服务未启动等） |

**典型用法**：对方设备未出现在列表时，在诊断页直接对其 IP 做连通性测试。
若 TCP 不通 → 防火墙或 IP 错误；若 TCP 通但 API 无响应 → 对方服务未启动；若协议不兼容 → 版本不一致。

---

## 安全设计

| 机制 | 实现 |
| --- | --- |
| 传输加密 | HTTPS（TLS），自签名证书，不安装到系统根存储 |
| 双向认证 | 服务端请求客户端证书；应用层判定信任 |
| 指纹固定 | 客户端记录并校验对端证书 SHA-256；**关闭 TLS 会话复用**（否则复用会跳过证书校验） |
| 配对 | 6 位验证码由会话 ID + 双方设备 ID 推导，两端必须一致 |
| 身份变化告警 | 同一设备 ID 证书指纹变化 → 拒绝传输，要求重新配对 |
| 路径穿越防护 | 拒绝 `../`、绝对路径、UNC、ADS、保留设备名、首尾空白 |
| 落盘安全 | 先写 `.part`，SHA-256 通过后才改名为正式文件名 |
| 私钥保护 | 私钥口令用 Windows DPAPI（CurrentUser）加密 |
| 权限最小化 | `asInvoker`，不申请管理员，不修改系统安全策略，不建立外部隧道 |

---

## 构建与打包

### 构建

```bash
dotnet build LanTransfer.sln -c Release
```

### 测试

```bash
dotnet test LanTransfer.sln -c Release
```

### 打包便携版

```powershell
pwsh -File publish.ps1
```

产出（`dist/` 目录）：

| 文件 | 说明 |
| --- | --- |
| `LanTransfer-1.0.0-win-x64-portable/` | 自包含便携版目录，双击 `LanTransfer.exe` 即可运行（免装 .NET） |
| `LanTransfer-1.0.0-win-x64-portable.zip` | 上述目录的压缩包，便于分发 |

可选参数：

```powershell
# 框架依赖版（体积小，需用户自行安装 .NET 10 Desktop Runtime）
pwsh -File publish.ps1 -SelfContained:$false

# 跳过测试直接打包
pwsh -File publish.ps1 -SkipTests
```

---

## 文档

| 文档 | 内容 |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 分层结构、模块职责、数据流、线程模型、安全设计 |
| [docs/protocol.md](docs/protocol.md) | UDP 发现协议、TLS 握手、分块传输时序、同步协议 |
| [docs/api.md](docs/api.md) | 全部 17 个 API 端点、请求响应结构、错误码全表 |
| [docs/database.md](docs/database.md) | 8 张表的完整 Schema、索引、位图设计 |
| [docs/testing.md](docs/testing.md) | 测试矩阵（220 个）、双机手工验收清单 |
| [docs/defect-log.md](docs/defect-log.md) | 逐轮代码审查与修复日志：每处缺陷的触发条件、根因、修复与反证 |

---

## 常见问题

### 设备互相发现不了

1. 确认两台机器在**同一网段**（`ipconfig` 对比前三段）
2. 确认防火墙已放行 **UDP 39520**
3. 检查路由器/交换机是否**禁止广播**（部分企业网络会）—— 若是，改用「手工连接」输入 IP
4. 在「网络诊断」页做连通性测试定位问题
5. 确认当前网络为「专用网络」（公用网络下防火墙更严格）

### 连接失败 / TCP 不通

- 用「网络诊断」对目标 IP 做连通性测试，看卡在哪一段
- 检查对方是否已启动、端口是否为默认 39521
- 检查对方防火墙是否放行 **TCP 39521**
- 确认没有第三方安全软件拦截

### 端口被占用

程序不会崩溃，只会在 UI 提示。打开「网络诊断」页确认占用情况，然后在「设置」页把端口改成其它值（如 39522）并重启服务。

查看占用进程：

```powershell
netstat -ano | findstr :39521
# 拿到 PID 后
tasklist /FI "PID eq <PID>"
```

### 传输速度慢

- 检查是否走 WiFi（WiFi 通常远慢于有线）
- 在「设置」页调大分块大小（4 → 8 或 16 MiB），减少请求次数
- 确认没有杀毒软件实时扫描传输目录
- 1000Mbps 有线网络理想情况约 100+ MB/s；实际受磁盘 IO 限制

### 传输中断后如何继续

在「历史记录」页找到该传输，点击「继续」。
程序会查询接收端已落盘的分块位图，**只传缺失部分**。若 `.part` 文件仍在，进度不会丢失。

### 接收到的文件在哪里

默认 `%USERPROFILE%\Downloads\LAN Transfer\`。可在「设置」页修改下载目录。
同步的文件则写入各自的同步目录。

### 同名文件会覆盖吗

默认**不会**。冲突策略默认为「重命名」，会生成 `test (1).zip`、`test (2).zip`。
可在发送时选择「覆盖」或「跳过」。

### 如何完全卸载

1. 关闭程序
2. 删除程序目录
3. 删除 `%LOCALAPPDATA%\LanTransfer`（含数据库、配置、证书）
4. 如需，删除防火墙规则：`Remove-NetFirewallRule -DisplayName "LAN Transfer*"`

### 日志在哪

`%LOCALAPPDATA%\LanTransfer\Logs\lantransfer-YYYYMMDD.log`
按天滚动，保留 14 个，单文件上限 64 MB。日志级别可在「设置」页调整。

### 会不会经过互联网

**不会。** 程序只监听本机网卡、只在局域网内收发。无任何云端交互、无遥测、无自动更新联网。
可以在完全断网的局域网（离线交换机）中正常工作。

---

## 许可证

本项目为局域网内部工具。使用前请确保你有权在目标网络中传输相应文件。
