# 缺陷审查与修复日志

本文档是 2026-09-24 对 LanTransfer 全量代码（`src/**` + `tests/**`）做逐轮审查时留下的工作日志，
按轮次记录了**每一处真实缺陷**的：现象与触发条件、根因、修复方式、回归测试位置，以及
「把修复改回去测试必须变红」的反证结果。

写作时间在原代码之后，措辞偏工作笔记（含踩坑记录），保留原样以便对照排查。

---
# 2026-09-24 — LAN Transfer 项目

## 交付物完成（Phase 0 + Phase 1）

按 `任务书.md` 完成 Windows 局域网 P2P 文件传输 + 双向同步工具的全部代码与文档。

### 代码（此前会话完成，本次验证）
- 10 个项目：Common / Core / Security / Storage / Network / Sync / App + 3 个测试项目
- Release 构建：**0 警告 0 错误**
- 测试：**220 全绿**（Core 117 / Network 41 / Integration 62）

### 本次新建（文档与打包）
- `docs/architecture.md` — 分层、模块职责、数据流、线程模型、安全设计
- `docs/protocol.md` — UDP 发现、TLS + 指纹固定、分块时序、同步协议、三态算法
- `docs/api.md` — 17 个端点 + 错误码全表
- `docs/database.md` — 8 张表 Schema、索引、位图设计
- `docs/testing.md` — 测试矩阵 + 双机验收清单
- `README.md` — 功能、系统要求、快速开始、防火墙、双机测试、诊断、FAQ
- `publish.ps1` — 便携版打包（自包含 win-x64）

### 打包验证
```
pwsh -File publish.ps1 -SkipTests
→ dist/LanTransfer-1.0.0-win-x64-portable/  （162.86 MB，409 文件）
→ dist/LanTransfer-1.0.0-win-x64-portable.zip（73.21 MB）
```

## 踩过的坑（本机环境相关）

1. **PowerShell 工具不回传 stdout** → 必须 `*>&1 | Out-File -FilePath <log> -Encoding UTF8`，再用 Read 读。
2. **Windows PowerShell 5.1 控制台编码非 UTF-8** → dotnet/MSBuild 的中文输出会乱码。
   脚本开头加 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` 解决。
3. **`PROCESSOR_ARCHITECTURE` 被清空** → 脚本里兜底设 `AMD64`。
4. **dotnet 路径**：`本机 .NET 10 SDK（需设置 DOTNET_ROOT 与 PATH）`，需 `export DOTNET_ROOT` + `PATH`。

## 两个已修复的真实安全漏洞（有回归测试锁定）

1. `SafePathResolver` 会把绝对路径/UNC 规范化成看似合法的相对路径
   → 修复：在 `NormalizeRelativePath` **之前**拒绝 `:` 与 `Path.IsPathRooted`。
2. TLS 1.3 会话复用会跳过证书校验回调 → 指纹固定失效
   → 修复：`SocketsHttpHandler` + `SslOptions.AllowTlsResume = false`。

## 待办
- 双机真实验收（需两台同网段 Windows 机器，见 docs/testing.md §5）

## 启动即崩的两个真实缺陷（已修复，2026-09-24 二次会话）

现象：双击便携版弹出「操作失败：No service for type
'LanTransfer.App.Views.MainWindow' has been registered.」，主窗口永不出现。

1. **DI 未注册任何 UI 类型** —— `ServiceRegistration.AddLanTransfer` 只注册到同步层就
   `return`，`App.OnStartup` 的 `GetRequiredService<MainWindow>()` 必然抛异常。
   `ValidateOnBuild = false` 让这个错误只在运行期暴露（所以 Release 构建仍 0 警告）。
   修复：补 `IDialogService` / 6 个页面 ViewModel / MainViewModel / MainWindow 注册。
2. **`InvariantGlobalization=true` 与 WPF 不兼容**（`Directory.Build.props`）——修好第 1 条后
   `MainWindow.Show()` 立刻抛 `InvalidOperationException: Cannot find non-neutral culture
   related to 'en-us'`（WPF 字体缓存需要真实文化数据）。修复：改为 `false`。

附带加固：`App.OnStartup` 现在把「解析/显示主窗口」包在 try/catch 里，失败时记日志 +
弹框 + `Shutdown(1)`。此前异常被 `OnDispatcherUnhandledException` 吞掉，而
`ShutdownMode=OnMainWindowClose` 又没有窗口可关，导致进程无窗口驻留并占住 TCP 39521
（再次启动就报端口被占用）。修复过程中清掉了 3 个这样的僵尸进程。

验证：Release 构建 0 警告 0 错误；220 测试全绿；bin\Release 与重新打包的
dist 便携版均实测启动出窗口、日志无异常。

## 六个页面同时渲染（已修复，同日晚些时候）

现象：设置页顶部一片糊——标题、说明、其它页面按钮、状态文字全叠在一起。

根因在 `MainWindow.xaml` 的页面切换绑定：

```xml
Visibility="{Binding SelectedNavigationIndex,
             RelativeSource={RelativeSource AncestorType=Window}, ...}"
```

`RelativeSource` 指定的源是 **Window 元素本身**，而 `SelectedNavigationIndex` 在
Window 的 DataContext（MainViewModel）上；显式指定源的绑定 **不会回退到 DataContext**，
于是绑定静默失败，`Visibility` 保持默认值 `Visible` —— 六个页面全部可见并互相重叠。
（如果只绑错一处就会崩，这里是"错得刚好不报错"：设计期的 `d:DataContext` 也掩盖了它。）

修复：路径改为 `DataContext.SelectedNavigationIndex`。

排查手法（可复用）：用 UI Automation 枚举运行中窗口的元素与坐标，一眼看出六个页面的
标题/按钮都占着同一个 rect `(644,251,848,681)`；修复后 descendants 从 201 降到 38，
且只有当前页面的特征控件可见。相关脚本写在 `%TEMP%\lanshare-uia\`、
`%TEMP%\lanshare-shots\capture.ps1`（PrintWindow 截图，不受遮挡影响）。

注意：给 Windows PowerShell 5.1 跑含中文的 .ps1 必须带 UTF-8 BOM，否则按 GBK 解析直接
语法错误（PowerShell 7 无此问题）。

## 配对失败：客户端证书从未被出示（已修复）

现象：「一端弹出配对确认（显示验证码），另一端毫无反应」。

根因链（每一环都有日志/实验证据）：

1. `App.InitializeSubsystemsAsync` **先解析全部服务（104~110 行，含 `IDeviceManager`→
   `ITransferClient`），第 119 行才 `identity.InitializeAsync()`**。于是
   `TransferHttpClient` 构造时 `identity.Certificate` 抛「身份服务尚未初始化」，
   被 catch 吞掉 → `SslOptions.ClientCertificates` 永久为空（单例）。
   证据：每次启动都有 `[WRN] 加载本机客户端证书失败，将使用单向 TLS。`
2. 服务端 `ResolveIdentity` 用握手时的客户端证书算指纹 → 空串。
3. `HandlePairAsync` 在**抛出 `PairingRequested` 之前**就 400 拒绝
   （`未收到客户端证书，无法完成身份绑定。`）→ 对端永远不弹窗。

修复：`TransferHttpClient` 改为**握手时按需取证书**
（`SslOptions.LocalCertificateSelectionCallback`），身份未就绪时返回 null 并记警告；
不再在构造时快照 + 吞异常。

回归测试：`tests/LanTransfer.IntegrationTests/PairingClientCertificateTests.cs`
固定「先造 HttpClient、后初始化身份」的顺序，断言配对成功且对端拿到的指纹与本机一致。
**已验证该测试在移除修复后会失败**（报 `UNAUTHORIZED 未收到客户端证书`）。

连带影响：指纹为空时 `EvaluateAsync` 恒返回 `Unknown` → 入站传输永远被当「陌生设备」，
「自动接收可信设备」永不生效。证书修好后一并解决。

顺带修：`HandlePairAsync` 原来把「收到配对请求」日志写在抛事件之后，而事件处理器会同步
弹模态框（`UiDispatcher.Send`），所以弹窗期间日志里看不到任何配对迹象——已改为先记日志。

## 两个同步对话框一打开就抛异常（已修复）

现象（用户实测日志）：点「新建同步关系」时 UI 线程连抛 3 个
`InvalidOperationException: A TwoWay or OneWayToSource binding cannot work on the
read-only property 'DefaultPairName' / 'DefaultLocalPath' / 'RemoteDeviceName'`。

根因：`TextBox.Text` 默认 **TwoWay**，而 `CreateSyncPairDialog` 的这几个属性是只读的
「默认值载体」；`SyncRequestDialog` 的 `SuggestedLocalPath` 同理（对端弹同步请求时也会炸）。

修复：这些绑定显式加 `Mode=OneWay`（代码本来就从 `LocalPathBox.Text` 之类的控件读值，
不需要回写）。**同类写法要警惕：只读属性 + TextBox.Text 必须写 Mode=OneWay。**

验证工具：`%TEMP%\dialog-probe\`（STA 探针，直接 new 出真实对话框并 Show()），
修复后三个对话框全部打开成功；把 `Mode=OneWay` 去掉一个即可复现原异常。

## 同步遇到「打开的文件」：两个缺陷（已修复）

现象：文件在编辑器里已保存但没关闭时，同步不处理它。

**缺陷 1（读不到）：读文件时只允许 `FileShare.Read`。**
Windows 的共享检查是双向的——不但要求已有句柄允许我们读，也要求我们声明的共享模式
允许已有句柄的访问方式。所以当 Word/Excel 等仍持有**写**句柄时（哪怕文件已保存），
`FileShare.Read` 的打开会直接共享冲突失败。修法：新增
`AppConstants.FileReadSharing = FileShare.ReadWrite | FileShare.Delete`，
统一用于 HashService、ChunkManager、TransferManager 发送、SyncEngine 上传/供对端下载。
回归测试：`SyncScannerTests.ScanAsync_FileHeldOpenForWritingByAnotherProgram_IsStillHashed`
（改回 `FileShare.Read` 即失败）。

**缺陷 2（读不到被当成删除 = 数据丢失）：**
`DirectoryScanner` 在读不到文件时 `continue` 丢掉该条目，而 `SyncPlanner.LocalState`
把「扫描结果里没有」一律解释为 `Deleted` → 生成 `DeleteRemote`
（**把对端那份删掉！**）。同理，同步根目录不存在时扫描返回空 → 判定两端全部删除。
修法：
- 扫描结果改为 `DirectoryScanResult { Entries, UnreadablePaths }`，读不到的路径记入
  集合（目录记目录本身，其下条目一并视为未知）；
- `SyncPlanner.Plan(..., unreadableLocalPaths)`：命中即整条跳过（状态未知，保持不变）；
- `SyncMetadataManager.ToManifest(scan, baseline)`：读不到的条目用**基线状态**占位，
  否则对端会把「读不到」当成「已删除」而删掉自己的副本；
- `SyncEngine.SyncNowAsync` 增加「同步根不存在 → 整轮跳过并报错」的护栏。
回归测试：`SyncPlannerTests.UnreadableLocalFile_ProducesNoAction_AndNeverDeletesRemote`
与 `UnreadableDirectory_SkipsEverythingBelowIt`（去掉护栏即生成
`DeleteRemote a.txt（本机删除了该文件，对端未修改）`）。

**补齐「关掉程序后自动同步」**：只靠默认 10 分钟周期扫描体感太差。
`SyncEngine` 现在在本轮有跳过/失败条目时按 15s→30s→1m→2m→4m（上限 5 分钟、最多 5 次）
退避重试，重试用全量扫描（顺带规避「同秒保存且大小不变」导致两级检测漏判）；
`SyncRunResult.Skipped/Failed` 会显示到同步页状态栏，不再默默报「同步完成」。

## 关闭窗口反复询问（已修复）

现象：每次点关闭都弹确认框。要求：只询问一次，之后按记住的选择直接执行。

原因：`MainWindow.OnClosing` 用的是 `MessageBox.Show`，每次关闭都问，选择不落盘；
`AppSettings.MinimizeToTrayOnClose` 声明了但**从未被使用**。

修复：
- 新增持久化配置 `AppSettings.CloseAction`（`CloseWindowAction`：Ask / Exit / MinimizeToTray，
  默认 Ask），替换掉没用上的 `MinimizeToTrayOnClose`。
- 新弹窗 `CloseConfirmDialog`：单选「退出程序 / 最小化到托盘」+「记住我的选择，以后不再询问」
  （默认勾选），并**预选记住的那一项**，直接回车即沿用上次选择。
- `OnClosing`：Ask 才弹窗；勾了记住就落盘（同步等待写完，因为紧接着可能就退出进程）；
  之后按记住的行为直接执行，不再询问。
- 可改回来：托盘菜单「恢复『每次询问关闭方式』」，设置页新增「启动与关闭 → 关闭窗口时」。
- 枚举用 `TolerantEnumConverter<T>`（Common.Serialization）：配置里写错值只回退为 Ask，
  不会像 `JsonStringEnumConverter` 那样抛异常导致整份配置被判损坏而重置。

验证（UIA 实测）：Ask→首关闭弹窗且默认选中「最小化到托盘」、「记住」为 On，确定后隐藏到托盘且
落盘 MinimizeToTray；再次关闭不弹窗直接隐藏；Exit 直接退出；改回 Ask 后弹窗点「取消」回到程序。
注意：**关闭确认框在 UIA 树里挂在属主窗口之下**，用桌面顶层窗口枚举找不到它。

## 全项目审查（6 路并行只读审查 + 静态分析），第一批修复

审查方式：6 个只读子代理分模块（Security+Storage / Network / Core+Common / Sync / App / 测试质量）
深挖 + 我本人复查 + `dotnet build -p:AnalysisMode=All`（1632 条警告里 CA2000/CA2213/CA1001/CA1849 有真问题）。
所有结论我都回到代码逐条核对后才动手。

### 本批已修（每条都补了回归测试或明确的代码证据）

1. **`UnreadablePaths` 用空串当哨兵但永不匹配**（我上一次修复自带的新洞）：
   `IsWithin` 的前缀循环对空串不成立 → 根目录读不到时护栏完全失效 → 对端整个同步目录被删空。
   改为空串=命中全部；`DirectoryScanResult` 新增 `RootUnreadable`，`SyncEngine` 在进入规划器前直接中止本轮。
2. **对端读不到的状态没有传递通道**：`SyncManifestResponse` 新增 `unreadablePaths`/`rootUnreadable`，
   被动端根不可读时直接返回失败而不是空清单；发起端把对端未知路径交给规划器整条跳过
   （此前会判成「对端已删除」→ `DeleteLocal` 删掉本机副本）。
3. **`SyncActionType.UpdateBaseline` 没有任何执行分支** → 基线永不收敛、每轮全量哈希、
   「两端都有同一文件但无基线」被误判成冲突。已补执行分支（文件写基线、两端都删则补墓碑）。
4. **目录动作会导致递归删除本机数据**：目录 Upload 只写基线不建对端目录 + 子文件上传失败时，
   下一轮判「对端已删除」→ `Directory.Delete(recursive:true)` 连未上传成功的文件一起删。
   现在目录只保留无害的 Upload 跟踪动作，删除/基线一律抑制；目录删除传播交给文件级动作。
5. **规划器跳过重解析点会被判成「已删除」** → 会把对端真实副本删掉（OneDrive 占位文件即触发）。
   扫描器现在把跳过的重解析点记入「未知」。（同批把根目录哨兵一并修正）
6. **分块重试从未生效**：`HttpRequestMessage` 释放会连带关闭调用方的 `MemoryStream`，
   第 2 次尝试设 `Position` 抛 `ObjectDisposedException` 且不被 catch 过滤 → 任何一次瞬时分块失败都直接终止传输。
   改为每次尝试新建流。
7. **分块上传超时被归类成「用户取消」**：网络故障显示为「已取消（用户取消）」。
   现在按 `job.Cts.IsCancellationRequested` 区分，超时置 `Failed/TIMEOUT`。
8. **同步大文件必然失败**：Kestrel 全局 `MaxRequestBodySize=64MiB`，而同步内容是整文件 PUT →
   >64MiB 的文件上传方向永远 413。已在 `/sync/pairs/{id}/content` 里放宽该限制。
9. **保留设备名可被中间扩展名绕过**：`NUL.tar.gz` / `COM1.foo.txt`（`GetFileNameWithoutExtension` 只取最后一段）。
   改为比较第一个点之前的部分。
10. **接收端不校验分块大小**：`chunkSize=0` 会让 `CalculateTotalChunks` 抛异常 → HTTP 500 且会话残留。
    入口按 `AllowedChunkSizes` 校验，非法返回 `CHUNK_SIZE_MISMATCH`。
11. **`--background`/开机自启时收不到任何文件**：主窗口从未 `Show` 过，`DialogService` 赋 `Owner` 抛异常
    被上层按「拒绝」吞掉。改为只在本窗口 `IsLoaded` 时才设 Owner。
12. **在 HTTP 请求线程上同步弹模态框** → 发送端 20 秒超时并误报「已取消」，而对端弹窗还开着。
    `MainViewModel` 三处 `UiDispatcher.Send` 改为 `Post`（请求立刻返回 WaitingApproval，发送端按 10 分钟轮询）。
13. **`ConflictPolicy.Skip` 会覆盖已有文件**（本批最早修的一处）：接收端在文件登记阶段就按策略拒绝，
    返回新错误码 `FILE_EXISTS_SKIPPED`，已有文件与 `.part` 都不再被碰。

验证：全量 **239 测试全绿**（Core 124 / Network 41 / Integration 74，比修复前 +7）；
关键护栏都做过「去掉修复→测试必失败」的反证（Skip 策略、`UnreadablePaths` 空串、目录递归删除）。

### 尚未修复（已确认、按优先级排队，见下一轮）

- 安全（剩余）：证书 DPAPI 解不开/临近过期时静默换身份（全部已配对设备失效且无提示）；
  未认证请求仍可无限弹接收确认框（已由 Send→Post 缓解阻塞，未做限流）。
  **兼容性提醒**：配对验证码现在绑定双方证书指纹，两端要同时升级，
  否则验证码对不上（会明确报「配对验证码不匹配」）。
- 同步（剩余）：无（大小写基线本轮已修；冲突副本 watcher 多跑一轮无副作用）。
- 传输（剩余）：`_transfers`/`Forget` 无终态回收；
  pause/cancel 对「等待确认中」的任务语义未覆盖。
- App（剩余）：无（本轮全部处理；另有两项低优先观察：主题设置无消费方、
  「仅重启网络服务」不保存输入框里的端口且保存失败仍提示已保存）。
- 测试（剩余）：大文件流式测试默认 `return`；集成测试并行 + 端口 TOCTOU；App 层无测试工程；
  `TransferManager` 完全没有测试（本轮续传修复只有代码路径证据 + 一条协议级回归测试）。

## 第三轮：断点续传（§21 核心要求）修复

`TransferManager` 的续传链路此前有 4 处会让「继续」失效或写坏数据的缺陷，本轮全部修掉：

1. **位图取并集 → 永久卡死**：`completed = 接收端位图 ∪ 本机位图`，而本机位图只增不减。
   接收端丢过块（用户点「删除未完成」、手工删 `.part`）后，本机跳过了接收端其实没有的块，
   `CompleteFile` 永远报「仍有 N 个分块未到达」，点「继续」一个块都不发。
   现在**以接收端位图为准**，只有状态查询失败才退回本机位图；并校验两端分块几何，
   不一致直接失败并提示重新发送（顺带修掉「改分块大小后续传偏移错位」）。
2. **恢复时不通告接收端复位**：接收端 `SetState` 对终态一律拒绝，取消过（或对端暂停过）
   的任务 CreateTransfer 会被「该传输已被终止」拒绝，`/resume` 调用在 Core 里从未被使用。
   现在 `ResumeAsync` 先调 `/resume`，且接收端允许**显式恢复**（Cancelled → Transferring）；
   新增回归测试 `CancelledTransfer_CanBeResumedByOwner`（移除该放行即失败：状态停在 Cancelled）。
3. **终态守卫挡住恢复**：`record.State` 停在 Failed/Cancelled，界面一直显示旧错误、
   `CanPause/CanCancel` 反复失效（等待审批/算哈希阶段连取消都点不了）。
   `UpdateStateAsync` 新增 `force`，恢复时显式重新入队并清掉旧错误。
4. **续传忽略持久化的 ChunkSize**：`JobFile.ChunkSize` 现在按文件记录沿用（写入也用该值），
   不再用「当前设置」重新解释旧块号。
5. 顺带修：进度重复计入首个文件的已传字节（原来会显示 1.2 GB / 1.0 GB 并写进数据库）；
   源文件已消失时不再静默把任务标成「已完成」，而是明确失败。
6. 接收端 `/complete`：已完成时幂等返回成功；**校验被取消（客户端超时/断开）不再记成
   `HASH_MISMATCH`** —— 大文件校验超过客户端 20 秒超时后，原本会把 100% 收完的文件判成校验失败，
   而发送端只看到超时，两端结论不一致只能整文件重传。

## 第四轮：同步基线一致性与删除竞态

1. **被动端从不写基线**（审查员发现，影响面最大）：`HandleManifestAsync`/`WriteAsync`/`DeleteAsync`
   都不落 `SyncEntries`，于是被动端基线永远为空 → 两级检测失效（**每个清单请求都对整棵树重算
   SHA-256**），且「读不到→用基线占位」的保护在被动端无从生效。
   现在：清单服务后批量落基线（一次事务，避免上万次单行事务）、写入/删除成功后也落基线/墓碑。
   新增 `SyncEngineManifestTests`（3 条）：验证基线确实落库、二次请求哈希一致、未知设备被拒、
   根目录缺失时明确返回 `RootUnreadable`（反证：注释掉落基线调用即失败）。
2. **上传后取哈希写基线 → 丢失更新**：上传期间文件被追加/保存时，基线会记成「对端已是新内容」，
   下一轮按「对端修改」用对端旧内容覆盖本机新内容。现在对比「计划哈希」与「上传后哈希」，
   不一致就不写基线并计入失败项（下一轮重传）。
3. **`ResolveConflictAsync` 忽略上传/下载失败仍写基线** → 用户刚做的裁决被静默回滚。
   失败时改为抛错（界面弹「裁决冲突失败」）并保留冲突，不再写基线、不再标记已解决。
4. **`DeletePairAsync`/`SetEnabledAsync` 不受 `_runGate` 保护**：执行中的一轮会在结束时
   `UpsertPairAsync` 把已删除的关系写回库（重启后复活并继续同步）。现在两者都与执行体互斥，
   并清了 `_pendingRuns`/重试计数；`SyncNowAsync` 写库前再确认关系仍在 `_pairs` 里（兜底）。
5. **被动端删除失败却返回成功**：发起端据此写「已传播」墓碑，而文件其实还在对端 →
   下一轮被 Download 复活（删除被静默撤销）。现在有删不掉的文件就返回失败并带错误码。

## 第五轮：App 层（都是用户直接可见的缺陷）

1. **设备页「发送文件」是死按钮**：`DevicesViewModel` 给列表项传的是 `_ => Task.CompletedTask`，
   点击后什么都不发生。现在 `MainViewModel` 把文件传输页的发送入口注入设备页
   （`DevicesViewModel.SendRequested` + `TransferViewModel.StartSendAsync(DeviceInfo, paths)`），
   并顺手把文件选择框收到 `IDialogService.PickFiles`（原来 VM 里直接 new OpenFileDialog）。
   实测：点按钮弹出「发送到 HZLP-DJDK-116」文件选择框。
2. **配对/解除信任后 `CanPair`/`CanRevoke` 不刷新**：`Refresh()` 把同一个实例重新赋给
   `SelectedDevice`，引用相等 → 不触发 `OnSelectedDeviceChanged`，配对成功后仍显示「配对」。
   现在 Refresh 结束显式通知这三个属性。
3. **「显示设备上线/离线通知」设置无消费方**：`Notifications` 只有读写两处，提示条照样弹。
   现在 `ShowToast` 先判断该设置。
4. **启动窗口期请求永不弹窗**：Kestrel/UDP 先启动，而订阅弹窗处理的 `MainViewModel` 是随后
   解析 `MainWindow` 时才构造的 —— 这个窗口期内到达的接收/配对/同步请求无人订阅，被静默忽略。
   现在先构造主窗口（及其 ViewModel），再启动服务；显示仍在初始化之后。
5. **改端口后界面显示旧端口**：`LocalDevice.Port` 是启动快照，重启网络服务后不更新。
   左侧栏与文件传输页改为读 `settings.Current.TransferPort`（与诊断页一致）。
6. 顺带：**硬失败不再干等 10 分钟** —— 设备不在列表、清单请求失败（对端刚启动/正忙/网络抖动）
   现在也会走 15s→30s→1m… 的退避重试。启动时实测到的
   `The SSL connection could not be established ... unexpected EOF`（对端侧 TLS 被中断，
   疑似本机 Mihomo 代理接口劫持了 10.18.x 网段流量）现在能自动恢复。

## 第六轮：两条安全项（配对验证码绑定 + UDP 伪造端点）

1. **配对验证码只由公开输入推导**：`code = SHA256(sorted(idA,idB)|sessionId) % 1e6`，
   而 DeviceId（UDP 广播/GET /device 都公开）与会话 ID（发起方选定）都不是秘密 →
   中继型中间人可以为两条腿各挑一个会话 ID，让两端屏幕显示**同一个数字**，
   从而通过「肉眼比对」这道唯一人工闸门。
   → 现在把**双方证书指纹**纳入推导（按 DeviceId 排序保证两端一致）：
   中间人连到对端时只能出示自己的证书，两条腿的指纹必然不同，两端数字就对不上。
   新增 `SecurityBindingTests.PairingCode_IsSymmetric_AndBindsCertificateFingerprint`；
   反证：把指纹从推导里去掉后，测试立刻失败并打印出两端相同的码 `452814 == 452814`。
   ⚠️ 兼容性：两端需同时升级，否则会报「配对验证码不匹配」。
2. **UDP 报文可伪造 DeviceId 改写可信设备端点 + 按 IP 的 TOFU 使指纹固定失效**：
   `ReportSeenAsync` 直接采信报文里的 IP/Port（哪怕对方是已信任设备），
   而客户端对没记录过的 IP 是「首次接触即信任（TOFU）」→ 攻击者伪造一包就能把文件引到自己机器。
   → 现在**端点被钉在设备已记录的证书指纹上**（`PinTrustedEndpoint`）：启动加载、UDP 上报、
   探测成功、配对/重新信任这四条路径都会 `Remember(ip, 该设备的指纹)`。
   伪造端点连过去要求出示真证书，冒充者过不了 TLS 握手；对端真换了 IP（证书不变）则照常连通。
   新增 `SecurityBindingTests.TrustedDevice_EndpointChange_PinsNewAddressToRecordedCertificate`；
   反证：关闭钉住逻辑后断言失败（`Expected: "AAAA…" Actual: null`）。

## 第七轮：传输临时文件并发 + 陈旧位图

1. **同名并发传输共用一个 `.part`**：临时文件固定叫 `name.part`，而 `ResolveConflictName` 只看正式文件
   是否存在 → 两个传输（两台设备或同机两次发送）解析到同一个 FinalPath 与同一个临时文件，
   并发写入互相踩（共享冲突或内容交错）。
   → 临时文件改为 `name.<transferId 前 8 位>.part`（只用 transferId、不用 FileId——
   FileId 每次登记都会重新生成，用它会让「重启后继续同一传输」找不到临时文件）。
   后缀仍是 `.part`，便于统一忽略。
2. **提交时同名覆盖**：登记时两个传输都解析到 `clash.bin`，后完成的会先删掉再改名，
   把先完成的文件直接抹掉（数据丢失）。
   → 提交前按同一策略再解析一次，Rename（默认）退让成 `clash (1).bin`。
3. **临时文件缺失但数据库位图仍在** → 接收端以为分块都到齐、永远不再收，最终必然校验失败且无法恢复。
   → 新增「陈旧位图」判定：登记前若临时文件不存在**或为空**，清空内存进度与数据库位图。
   注意判定必须放在「预创建 .part」**之前**（否则文件已被自己创建出来，永远判定为存在）。
   这个 bug 是新测试先发现的：我第一版把判定放在预创建之后，测试直接失败。
4. **同步扫描不排除传输临时文件**：崩溃/取消留下的 `*.part`（可能只有半个文件）会被当成正式内容上传。
   → `DirectoryScanner` 跳过 `*.part` / `*.part.json` / `*.part.json.tmp`。

验证：新增两条回归测试（`ConcurrentSameNameTransfers_DoNotClashOrOverwrite`、
`MissingPartFile_DiscardsStaleChunkBitmap`）；反证：同时关闭「提交时重命名」与「陈旧位图检测」后，
两条测试分别报出「后完成的文件没有退让重命名，可能覆盖了先完成的文件」与
`Collection: [0]`（陈旧位图未被清）。

## 第八轮：同步基线主键大小写

**缺陷**：`SyncEntries` 的主键是 `(SyncPairId, RelativePath)`，而 SQLite 默认按 BINARY 比较 ——
与全应用（扫描器/规划器/基线折叠都用 OrdinalIgnoreCase）不一致。Windows 上"只改大小写的重命名"
（readme.md → README.md，Git 切分支/编辑器另存为很常见）会**插入第二行**，
旧行永久残留成幽灵条目（陈旧哈希/陈旧墓碑）→ 多余下载、删除甚至冲突。

**修复**：
- 新库：`RelativePath TEXT NOT NULL COLLATE NOCASE`（主键随之大小写不敏感）。
- 老库：`DatabaseInitializer` 增加一次性迁移（此前没有任何迁移机制，`CREATE TABLE IF NOT EXISTS`
  不会改动既有表结构）：读 `sqlite_master` 判断是否缺 `COLLATE NOCASE`，缺则在一个事务里
  「旧表改名 → 用与 SchemaSql 完全一致的语句重建 → `INSERT OR REPLACE` 只搬版本最高的行 → 删旧表」。
- 新增两条测试：写入大小写变体只留一行；手工造一个旧版（BINARY 主键 + 两行只差大小写）的库，
  初始化后断言合并成一行且保留版本较高者，且表结构已含 COLLATE NOCASE。
  反证：把 SchemaSql 里的 COLLATE NOCASE 去掉，第一条测试立刻失败（2 行）。

**踩坑记录**：这条测试一开始"失败得有迷惑性"——我在 SQL 原始字符串里写了 `'docs\\readme.md'`
（原始字符串不处理转义 → 实际是两个反斜杠），而 C# 里写的是 `"Docs\\Readme.md"`（一个反斜杠），
两者本来就是不同路径，根本不是大小写问题。**测试数据也要当代码审**。


## 第九轮：一真一假两个「测试/资源」缺陷

### 9.1 大文件流式测试其实什么都没测（假通过）

**缺陷**：`LoopbackTransferTests` 里那个「500 MB 大文件不应被整体读进内存」的测试，
函数体第一句就是 `if (Environment.GetEnvironmentVariable("LANTRANSFER_LARGE_TESTS") != "1") return;`
——默认路径下**永远返回成功**，`Assert` 一次都没跑。也就是说"流式传输"这个承诺
（§21 核心要求）在 CI/日常 `dotnet test` 里完全没有回归保护，哪天有人把 `File.ReadAllBytes`
写回去也不会有任何测试变红。而且 `Assert.Skip` 在 xunit 2.9.3 里不存在
（写成 `Assert.Skip(...)` 会被解析成 LINQ 的 `Enumerable.Skip`，报 `error CS0411`），
所以"跳过"这条路本身也走不通。

**修复**：删掉静默 return，改成一个**默认就真跑**的 128 MiB 用例：
- 传输 128 MiB（`FileStream` 生成，避免测试自身占用大内存），传输过程中断言
  `GC.GetTotalMemory(forceFullCollection: true)` 的增量 `< size / 4`（32 MiB）。
  阈值取得足够宽松（真正流式时增量通常是几 MB 级），但足以在"整文件进内存"时必然爆掉。
- 需要更大规模时用 `LANTRANSFER_LARGE_TESTS=1` 把尺寸放大到 500 MB，仍然真断言。
- `docs/testing.md` 同步更新：写清"禁止用静默 return 伪造 skip；要么真断言，要么明确 Skip 机制"。

**反证**：把 `TransferManager` 的发送路径临时改成先 `File.ReadAllBytes` 再发，
`GC.GetTotalMemory` 增量立刻超过阈值 → 测试变红；恢复后转绿。

### 9.2 接收端传输表只增不减（长跑进程内存/句柄泄漏）

**缺陷**：`IncomingTransferRegistry` 的 `_transfers` 字典以 `transferId` 为键，
传输完成/失败/取消后条目**永远留在表里**，只把状态改成终态。
`Forget`/`Remove` 只有测试自己在调，生产代码从不调用。长时间运行（或对端反复重传）
会让这张表无限增长，连带每个条目持有的位图、临时文件路径、锁对象一起泄漏；
每来一个新传输都要在这张越来越大的表上做扫描。

**修复**：
- 每个条目记录 `LastActivity`（注册、收块、查询、完成时更新）。
- `CreateOrGetFileAsync` 入口调用 `SweepTerminalTransfers()`：清理「已是终态 且
  空闲超过保留期」的条目；保留期默认 30 分钟（远大于客户端可能的重试窗口，
  避免刚完成就被清掉导致 `/status` 查不到），清扫间隔 5 分钟避免每次都全表扫。
  两者都做成构造函数参数，测试传 0 即可立刻生效。
- 顺带修正 `SweepTerminalTransfers` 的边界：**正在传输中**（非终态）的条目无论多旧都不动。

**新增测试**（`IncomingTransferReclamationTests`）：注册 → 完成 → 断言仍在表里（保留期内可查）；
用 0 保留期再跑一遍 → 断言已被回收；另有一条断言「传输中的条目不会被回收」。
**反证**：注释掉 `CreateOrGetFileAsync` 里的 `SweepTerminalTransfers()` 调用，
回收断言立刻变成 `Assert.Null() Failure: Value is not null`。

**本轮验证**：全量 254 个测试通过（Core 126 / Network 41 / Integration 87），0 warning 0 error。


## 第十轮：入站请求被刷屏（4 个缺陷 + 新建 App 测试工程）

发现路径：本来只想补「未认证请求能无限弹确认框」这条，顺着事件链读下去，
发现真正的原因有四层，一层比一层隐蔽。

### 10.1 确认弹窗会层层叠起来（模态框的嵌套消息循环）

**缺陷**：接收确认 / 配对确认 / 同步请求三处都是「网络线程抛事件 → `UiDispatcher.Post` →
`ShowDialog()`」。而 `ShowDialog` 会开启一个**嵌套消息循环**，期间 Dispatcher 照样会执行
已经 `BeginInvoke` 排队的回调 —— 第一个确认框还没关，第二个请求的弹窗回调就已经跑起来了。
触发条件很普通：两个设备同时发文件、或发送端连发多次；恶意一点，局域网里任何人换着
transferId 刷一遍就能把界面刷满。

**修复**：新增 `PromptQueue`（`src/LanTransfer.App/Services/PromptQueue.cs`）：
- 同一时刻只有一个弹窗：入队时若已有「驱动循环」在跑就只往队列里放，**不再投递新的
  Dispatcher 回调**（没有第二次投递，就不可能在嵌套消息循环里重入）；
- 驱动循环串行执行队列，单个弹窗抛异常不会打断循环（否则后续请求永远收不到应答）；
- 队列有上限（默认 64），**超限不入队、由调用方显式按「拒绝」应答**——绝不能静默丢弃，
  否则发送端会一直等到审批超时才报错；
- 三个事件处理器统一改成 `try/finally` 无条件应答（原来 `ConfirmIncomingTransfer` 之后的
  「信任该设备」二次确认一旦抛异常，`RespondToApproval` 就被跳过，发送端空等 10 分钟）。

**新建测试工程** `tests/LanTransfer.App.Tests`（net10.0-windows + UseWPF，已加入 sln）——
补上「App 层无任何测试工程」这个缺口。`PromptQueue` 不引用任何 WPF 类型（投递方式注入），
所以可以直接单测：用一个假的 Dispatcher **如实模拟嵌套消息循环**，
断言「同一时刻只有一个弹窗、整条队列只投递一次回调」。
另有一条 `HarnessItself_DetectsStacking...` 用例专门跑「旧写法」并断言 `maxActive == 2`，
证明这套断言不是永远为真的空测试。反证：把 `Enqueue` 改回直接 `_post(prompt)`，
5 条用例立刻失败（含叠窗那条）。

### 10.2 未认证请求能无限登记「待确认」传输

**缺陷**：`/transfers` 只要不是「身份已变化」就放行（`ClientCertificateMode.AllowCertificate`，
证书校验恒真）。每个**新** transferId 都会弹一个模态确认框，并在服务端留下一个最长等 10 分钟的
审批任务。没有上限 = 内存与界面双双被刷爆。

**修复**：`IncomingTransferRegistry` 增加两级闸门（同一设备 ≤ 3、全局 ≤ 16），只对**新传输**
判定（发送端是按文件调用 `/transfers` 的，多文件传输的后续文件不算新请求，否则大目录传到
一半会把自己挡住）；超限返回新错误码 `TOO_MANY_PENDING_REQUESTS`，
`TransferServer` 对这种响应改用 **429**（客户端只看响应体，不受影响）。

**判定口径**：`IsAwaitingUserDecision = !Approval.Task.IsCompleted && !State.IsTerminal()`。
- 只看 `Approval.Task.IsCompleted` 会**永久漏配额**：审批流程超时后状态已变 Failed，
  但那个 `TaskCompletionSource` 永远不会完成，几台设备各超时几次就把额度占满，之后谁也别想再发文件；
- 只看状态会把「自动接收」的传输也算进去。

**测试**（`IncomingApprovalThrottleTests`，5 条）：按设备封顶、多文件传输不被自己挡住、
应答后立刻释放额度、**超时后不永久占额度**、审批流程单飞。
反证：条件恒假 → 前两条失败；判定式去掉 `State.IsTerminal()` → 超时那条失败。

### 10.3 一个 N 文件传输会挂 N 个审批等待任务

**缺陷**：`TransferServer` 在每次 `/transfers` 成功且状态仍是 WaitingApproval 时都
`_ = RunApprovalFlowAsync(transfer)`。发送端按文件调用，一个 100 文件的目录就在等待队列里
挂 100 个 `WaitAsync`，每个带一个 10 分钟的 `CancellationTokenSource` 定时器，全都在等同一个应答。

**修复**：`IncomingTransferState.TryBeginApprovalFlow()`（`Interlocked.Exchange` 单飞闸门），
调用点判一下。反证：让它恒返回 true → 单飞用例失败（并有 `CS0169` 提示字段未使用）。

### 10.4 「自动接收可信设备文件」设置形同虚设，而且点了「拒绝」会掐掉正在接收的传输

**缺陷**（读 10.2 的调用链时顺手发现的，比上面几条更贴近用户）：注册表确实按设置自动放行了
传输（`Approved = true`、状态直接变 `Transferring`），但 `TransferServer` **无条件**为新传输抛
`IncomingTransferRequested`。后果：
1. 用户勾了「自动接收可信设备文件」，对方发文件时本机**照样**弹确认框 —— 设置是假的；
2. 更糟：传输此时已经在接收了，用户在弹窗上点「拒绝」→ `RespondToApproval` 把状态改成
   `Rejected`，正在上传的文件随即失败。

**修复**：只有 `transfer.IsAwaitingUserDecision` 为真（真的在等用户点头）才抛确认事件。

**测试**（`AutoAcceptTrustedTransferTests`，真起 Kestrel + 双向 TLS 走一遍 HTTP）：
可信设备 + 自动接收 → 断言「确认框事件 0 次」「响应状态已是 transferring」；
对照组未配对设备 → 断言「确认框事件 1 次」「状态 waiting-approval」。
反证：把条件改回无条件 → 第一条失败，对照组仍通过（说明测试确实在测这条设置）。

**本轮验证**：全量 268 个测试通过（Core 126 / Network 41 / App 7 / Integration 94），0 warning 0 error。


## 第十一轮：确认流程被绕过（安全）+ 代码入库

### 11.1 `/resume` 是 `/approve` 那道禁令的另一扇门（未经确认即可写盘）

**缺陷**：此前修过一个洞 —— `/transfers/{id}/approve` 不允许远程调用（任意主机都能把自己的传输
置为「已批准」，绕过配对与用户确认直接写盘）。但 **`/resume` 达到的是同一个效果**：
它调用 `SetState(Transferring, allowResumeFromTerminal: true)`，而 `SetState` 一旦把状态置为
`Transferring` 就会顺带 `Approved = true` + 完成审批任务；`WaitingApproval` 又不是终态，
「终态不可复活」那道守卫根本拦不住。

**触发条件**（局域网内任意主机，不需要配对，也不需要用户点任何东西）：
1. `POST /transfers` 建传输（接收端弹确认框，用户还没点）；
2. `POST /transfers/{id}/resume` → 状态被改成 transferring、Approved 被置 true；
3. `PUT` 分块 + `POST /complete` → 文件直接写进下载目录。

**端到端证据**（真起 Kestrel + 双向 TLS 走 HTTP，`ApprovalBypassTests`）：
修复前那条用例的失败信息就是完整的攻击链：
`/resume 把状态改成了 transferring；/status 报出 transferring；未经用户确认就接受了分块；未经用户确认就通过了校验；未经用户确认的文件被写进了接收目录`。

**修复**：`SetState` 增加不变量 —— **只有 `Approved == true` 的传输才能进入接收状态**
（`state == Transferring && !Approved` → 原地返回）。合法路径不受影响：
本机用户点「接收」时 `RespondToApproval` 已经先置 `Approved`；自动接收在登记时就置好了。

### 11.2 `/complete` 缺少写盘前的最后一道闸门

**缺陷**：`CompleteFileAsync` 完全不看状态与确认标记，只检查「分块是否收齐」，然后直接把
`.part` 改成正式文件名。状态在「收完分块」和「/complete」之间是会变的（用户暂停、取消、拒绝），
这一步放过就是文件真落盘。

**修复**：写盘前再判一次 `!Approved || State ∈ {WaitingApproval, Rejected, Cancelled, Paused}`
→ 返回 `TRANSFER_STATE_CONFLICT`。**故意不拦** `Failed` / `VerificationFailed` / `Verifying`：
这三条是既有的「失败后重试」恢复路径（磁盘腾空间、重新上传后重算哈希），拦掉会让传输永远救不回来。
代价是这两条路径目前本来也走不通（收分块那一侧也拦 Failed），属于既有行为，未在本次扩大改动。

**测试**：`ApprovalBypassTests` 6 条，含三条对照用例（正常确认后能写、暂停后继续能写、
0 字节文件确认后能写），确保不是「把正常功能一起挡了」。

**反证**：去掉 `SetState` 的确认守卫 → 11.1 那条用例失败（攻击链完整重现）；
去掉 `/complete` 的守卫 → 11.2 那条用例报「已暂停的传输不得通过 /complete 落盘」。

### 11.3 代码入库（新增要求）

- 新增 `.gitignore` 条目：`.workbuddy-ai/`（AI 工作区，含构建产物）、`*.log`。
- `git init -b main` → 提交 139 个文件（src/tests/docs/publish.ps1/README/任务书/tools），
  bin、obj、dist、*.db、*.pfx、*.part 全部排除；提交前扫过私钥/口令/令牌，未发现敏感内容。
- 推送至 `git@github.com:yunjohn/lanshare.git`（SSH 认证已可用，仓库原已存在且为空）：
  commit `79ccd51`，139 个文件，分支 `main`。
- 提交身份用本机局部配置（`yunjohn` + GitHub noreply 邮箱），未改动全局 git 配置。

**本轮验证**：全量 274 个测试通过（Core 126 / Network 41 / App 7 / Integration 100），0 warning 0 error。


## 第十二轮：长期无活动的非终态入站传输永不回收

**缺陷**：第九轮的回收只清「已进入终态」的条目，非终态的一律不动。但**收分块没有任何超时**，
所以发送端在上传途中崩溃 / 断网 / 被强杀时，接收端这条记录没有任何机制能把它推进到终态 ——
它会永远停在 `Transferring` 常驻内存（连带位图、临时文件路径、锁对象）。
每发生一次对端中断就漏一条，长时间运行会持续累积。（`WaitingApproval` 有 10 分钟审批超时兜底，
`Transferring` 没有。）

**修复**：回收扫描从「只清终态」扩展为「按最后活动时间分级回收」：
- 终态：`TerminalRetention`（30 分钟，原行为不变）；
- 非终态：`StaleRetention`（24 小时无任何活动）。新增构造函数参数 `staleRetention`，测试传 0 即可立即生效。
- 两者共用同一个扫描入口（登记文件时顺带跑，限频 5 分钟），日志分开计数便于排查。

**为什么回收是安全的**：发送端重连后会拿同一个 transferId 重新登记，断点由磁盘上的 `.part`
元数据 + 数据库位图恢复（`RestoreProgressAsync` 就是为此存在的）—— 内存里的进度本来就不是唯一副本。
历史记录与 `.part` 都保留。

**测试**（`IncomingTransferReclamationTests`，4 条）：
- 新增 `StaleTransferringTransfer_IsReclaimed_ButHistoryIsKept`：确认过的传输进 Transferring 后
  「闲置」（保留期设为 0 代表已闲置 24 小时）→ 下一次登记时被回收，但数据库历史仍在；
- 新增对照 `ActiveTransferringTransfer_IsNotReclaimed_WithinRetentionWindow`：保留期未到不得回收；
- 原有两条（终态回收、活动条目不回收）保持通过。

**反证**：把非终态分支改回 `continue`（即第九轮的旧行为）→ 新用例立刻
`Assert.Null() Failure: Value is not null`。

**本轮验证**：全量 276 个测试通过（Core 126 / Network 41 / App 7 / Integration 102），0 warning 0 error；
提交 `8ce4657` 已推送至 `git@github.com:yunjohn/lanshare.git`。



- 同步：未裁决冲突每轮重复下载并新增冲突副本（含监视器自触发）；被动端从不写基线；
  `DeletePairAsync`/`SetEnabledAsync` 不受 `_runGate` 保护（关系复活）；上传后才取哈希写基线（竞态丢更新）；
  `ResolveConflictAsync` 忽略上传/下载失败仍写基线（裁决被静默回滚）。
- 传输：续传取「本地 ∪ 接收端」位图（接收端丢块后永久卡死）；恢复时从不通知接收端复位状态；
  「终态不可覆盖」守卫挡住恢复；续传忽略持久化的 ChunkSize；`/complete` 用请求取消令牌做哈希校验
  （大文件被误判成校验失败）；同名并发传输共用同一个 `.part` 且不复位；`Forget`/`_transfers` 无回收。
- App：设备页「发送文件」是死按钮；配对后 `CanPair/CanRevoke` 不刷新；`Notifications` 设置无消费方；
  启动窗口期服务端先起、弹窗订阅后建；改端口后界面显示旧端口。
- 测试：大文件流式测试默认 `return`（永远"通过"）；集成测试并行 + 端口 TOCTOU + 进程级 `ClearAllPools()`；
  App 层（ViewModel/XAML）无任何测试工程。






