using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Models;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using LanTransfer.Network.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Server;

/// <summary>
/// 接收端 Kestrel 服务。暴露 /api/v1/* 接口，负责设备信息、配对、建立传输、Chunk 接收、
/// 状态查询、暂停/取消与完整性校验。
/// 端口被占用时不得崩溃：只记录 LastError，由 UI 提示用户修改端口后重启。
/// </summary>
public sealed class TransferServer : ITransferServer
{
    private readonly ISettingsService _settings;
    private readonly IIdentityService _identity;
    private readonly IDeviceManager _devices;
    private readonly ITrustStore _trustStore;
    private readonly IPairingService _pairing;
    private readonly IncomingTransferRegistry _registry;
    private readonly ILogger<TransferServer> _logger;
    private readonly ISyncServerHandler? _syncServer;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<PairingDecision>> _pairingWaiters =
        new(StringComparer.OrdinalIgnoreCase);

    private WebApplication? _app;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public TransferServer(
        ISettingsService settings,
        IIdentityService identity,
        IDeviceManager devices,
        ITrustStore trustStore,
        IPairingService pairing,
        IncomingTransferRegistry registry,
        ILogger<TransferServer> logger,
        ISyncServerHandler? syncServer = null)
    {
        _settings = settings;
        _identity = identity;
        _devices = devices;
        _trustStore = trustStore;
        _pairing = pairing;
        _registry = registry;
        _logger = logger;
        _syncServer = syncServer;

        _registry.StateChanged += (_, transfer) => RaiseIncomingStateChanged(transfer);
    }

    public bool IsRunning => _app is not null;

    public int Port { get; private set; }

    public string? LastError { get; private set; }

    public string CertificateFingerprint => _identity.CertificateFingerprint;

    public event EventHandler<IncomingTransferEventArgs>? IncomingTransferRequested;
    public event EventHandler<IncomingTransferEventArgs>? IncomingTransferStateChanged;
    public event EventHandler<IncomingFileCompletedEventArgs>? IncomingFileCompleted;
    public event EventHandler<PairingRequestedEventArgs>? PairingRequested;

    // ---------------------------------------------------------------- 生命周期

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null) return;

            Port = _settings.Current.TransferPort;

            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();

                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
                    options.AddServerHeader = false;

                    options.ListenAnyIP(Port, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1AndHttp2;
                        listen.UseHttps(https =>
                        {
                            https.ServerCertificate = _identity.Certificate;

                            // 双向 TLS：请求客户端证书，但由应用层做信任判定
                            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                            https.ClientCertificateValidation = (_, _, _) => true;
                        });
                    });
                });

                var app = builder.Build();
                MapEndpoints(app);

                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _app = app;
                LastError = null;

                _logger.LogInformation(
                    "传输服务已启动: https://0.0.0.0:{Port}{Api}，证书指纹 {Fingerprint}",
                    Port, AppConstants.ApiPrefix, _identity.CertificateFingerprint);
            }
            catch (Exception ex)
            {
                LastError = $"TCP 端口 {Port} 启动失败：{ex.Message}。请在设置中修改 TCP 传输端口后重试。";
                _logger.LogError(ex, "传输服务启动失败，端口 {Port}", Port);
                _app = null;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is null) return;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "停止传输服务时出现异常");
            }

            _app = null;
            _logger.LogInformation("传输服务已停止");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void RespondToApproval(string transferId, bool approve, bool rememberDevice)
    {
        _registry.RespondToApproval(transferId, approve);

        if (approve && rememberDevice)
        {
            var transfer = _registry.Find(transferId);
            if (transfer is not null)
            {
                var device = _devices.Find(transfer.RemoteDeviceId);
                if (device?.CertificateFingerprint is { Length: > 0 } fingerprint)
                {
                    _ = _trustStore.TrustAsync(transfer.RemoteDeviceId, transfer.RemoteDeviceName, fingerprint)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                _logger.LogWarning(t.Exception, "保存信任关系失败: {DeviceId}",
                                    transfer.RemoteDeviceId);
                        }, TaskScheduler.Default);
                }
            }
        }
    }

    public void RespondToPairing(string pairingSessionId, bool accept)
    {
        if (_pairingWaiters.TryRemove(pairingSessionId, out var waiter))
            waiter.TrySetResult(new PairingDecision(accept));
    }

    public Task<SimpleOperationResponse> PauseIncomingAsync(string transferId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_registry.SetState(transferId, TransferState.Paused));

    public Task<SimpleOperationResponse> ResumeIncomingAsync(string transferId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_registry.SetState(transferId, TransferState.Transferring,
            allowResumeFromTerminal: true));

    public Task<SimpleOperationResponse> CancelIncomingAsync(string transferId, bool deletePartial,
        CancellationToken cancellationToken = default)
        => _registry.CancelAsync(transferId, deletePartial, cancellationToken);

    // ---------------------------------------------------------------- 端点

    private void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup(AppConstants.ApiPrefix);

        api.MapGet("/health", () => Results.Json(new HealthResponse
        {
            Success = true,
            Status = "ok",
            ProtocolVersion = AppConstants.ProtocolVersion,
            AppVersion = AppConstants.AppVersion,
            ServerTimeUtc = DateTimeOffset.UtcNow,
        }));

        api.MapGet("/device", () => Results.Json(new DeviceResponse
        {
            Success = true,
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            AppVersion = AppConstants.AppVersion,
            ProtocolVersion = AppConstants.ProtocolVersion,
            Port = Port,
            CertificateFingerprint = _identity.CertificateFingerprint,
            OsVersion = Environment.OSVersion.VersionString,
            MachineName = Environment.MachineName,
        }));

        api.MapPost("/pair", HandlePairAsync);
        api.MapPost("/transfers", HandleCreateTransferAsync);
        api.MapGet("/transfers/{transferId}", HandleGetStatus);
        api.MapPut("/transfers/{transferId}/chunks/{chunkIndex:int}", HandleUploadChunkAsync);
        api.MapPost("/transfers/{transferId}/complete", HandleCompleteAsync);
        api.MapPost("/transfers/{transferId}/approve",
            (HttpContext ctx, string transferId) => HandleSimpleOp(ctx, transferId, "approve"));
        api.MapPost("/transfers/{transferId}/reject",
            (HttpContext ctx, string transferId) => HandleSimpleOp(ctx, transferId, "reject"));
        api.MapPost("/transfers/{transferId}/pause",
            (HttpContext ctx, string transferId) => HandleSimpleOp(ctx, transferId, "pause"));
        api.MapPost("/transfers/{transferId}/resume",
            (HttpContext ctx, string transferId) => HandleSimpleOp(ctx, transferId, "resume"));
        api.MapPost("/transfers/{transferId}/cancel",
            (HttpContext ctx, string transferId) => HandleSimpleOp(ctx, transferId, "cancel"));

        api.MapPost("/sync/pairs", HandleSyncRequestAsync);
        api.MapPost("/sync/pairs/{syncPairId}/notify", HandleSyncNotificationAsync);
        api.MapPost("/sync/pairs/{syncPairId}/manifest", HandleSyncManifestAsync);
        api.MapGet("/sync/pairs/{syncPairId}/content", HandleSyncReadAsync);
        api.MapPut("/sync/pairs/{syncPairId}/content", HandleSyncWriteAsync);
        api.MapPost("/sync/pairs/{syncPairId}/delete", HandleSyncDeleteAsync);
    }

    // ---------------------------------------------------------------- 请求上下文

    private sealed record RequestIdentity(string DeviceId, string DeviceName, string Fingerprint, bool IsTrusted,
        TrustState TrustState);

    private RequestIdentity ResolveIdentity(HttpContext context)
    {
        var deviceId = context.Request.Headers[HeaderNames.DeviceId].ToString();
        var deviceNameHeader = context.Request.Headers[HeaderNames.DeviceName].ToString();
        var deviceName = string.IsNullOrEmpty(deviceNameHeader)
            ? deviceId
            : Uri.UnescapeDataString(deviceNameHeader);

        var clientCertificate = context.Connection.ClientCertificate;
        var fingerprint = clientCertificate is null
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(clientCertificate.RawData));

        var trustState = TrustState.Unknown;
        var isTrusted = false;

        if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(fingerprint))
        {
            trustState = _trustStore.EvaluateAsync(deviceId, fingerprint).GetAwaiter().GetResult();
            isTrusted = trustState == TrustState.Trusted;
        }
        else if (!string.IsNullOrWhiteSpace(deviceId))
        {
            trustState = TrustState.Unknown;
        }

        return new RequestIdentity(deviceId, deviceName, fingerprint, isTrusted, trustState);
    }

    private static async Task<IResult> ErrorAsync(HttpContext context, int statusCode, string errorCode,
        string message, Dictionary<string, string>? details = null)
    {
        context.Response.StatusCode = statusCode;
        return Results.Json(ApiErrorResponse.Create(errorCode, message, details), statusCode: statusCode);
    }

    // ---------------------------------------------------------------- 配对

    private async Task<IResult> HandlePairAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<PairRequest>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "请求体不是合法的 JSON。").ConfigureAwait(false);

        var identity = ResolveIdentity(context);

        if (string.IsNullOrWhiteSpace(request.DeviceId) || string.IsNullOrWhiteSpace(request.PairingSessionId))
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "缺少 deviceId 或 pairingSessionId。").ConfigureAwait(false);

        if (request.ProtocolVersion != AppConstants.ProtocolVersion)
        {
            return await ErrorAsync(context, StatusCodes.Status409Conflict, ErrorCodes.ProtocolIncompatible,
                $"目标设备 LAN Transfer 版本不兼容（对端协议 {request.ProtocolVersion}，本机 {AppConstants.ProtocolVersion}）。")
                .ConfigureAwait(false);
        }

        // 信任关系必须绑定到「请求身份」，而不是请求体里自称的 deviceId：
        // 否则攻击者用自己的证书 + 别人的 deviceId 发起配对，用户点一次「确认」，
        // 就会把可信设备的指纹改写成攻击者的指纹（真对端从此被拒，攻击者变成可信设备）。
        if (!string.IsNullOrWhiteSpace(identity.DeviceId) &&
            !string.Equals(identity.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("配对请求的 deviceId 与请求身份不一致，已拒绝: body={Body} identity={Identity}",
                request.DeviceId, identity.DeviceId);
            return await ErrorAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Unauthorized,
                "配对请求中的 deviceId 与请求身份不一致。").ConfigureAwait(false);
        }

        // 两端用同一算法推导验证码（含双方证书指纹，中继型中间人无法让两端显示同一个码）
        var expectedCode = _pairing.DeriveVerificationCode(request.PairingSessionId, _identity.DeviceId,
            _identity.CertificateFingerprint, request.DeviceId, identity.Fingerprint);

        if (!string.Equals(expectedCode, request.VerificationCode, StringComparison.Ordinal))
        {
            _logger.LogWarning("配对验证码不匹配，来源 {DeviceId}", request.DeviceId);
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.InvalidPairingCode,
                "配对验证码不匹配。").ConfigureAwait(false);
        }

        // 要求客户端出示证书，否则无法把 DeviceId 与证书指纹绑定
        if (string.IsNullOrWhiteSpace(identity.Fingerprint))
        {
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized,
                "未收到客户端证书，无法完成身份绑定。").ConfigureAwait(false);
        }

        var pairingRequested = PairingRequested;
        if (pairingRequested is null)
        {
            return await ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.InternalError,
                "接收端界面尚未就绪，请稍后重试。").ConfigureAwait(false);
        }

        var waiter = _pairingWaiters.GetOrAdd(request.PairingSessionId,
            _ => new TaskCompletionSource<PairingDecision>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        var deviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? request.DeviceId : request.DeviceName;

        // 先记日志再抛事件：事件处理器会同步弹模态框（UiDispatcher.Send），
        // 若把日志写在后面，弹窗期间日志里看不到任何配对迹象，排查时极易误判为「对端没收到」。
        _logger.LogInformation("收到配对请求: {Device} ({DeviceId})，等待本机用户确认",
            deviceName, request.DeviceId);

        pairingRequested.Invoke(this, new PairingRequestedEventArgs
        {
            PairingSessionId = request.PairingSessionId,
            RemoteDeviceId = request.DeviceId,
            RemoteDeviceName = deviceName,
            RemoteFingerprint = identity.Fingerprint,
            VerificationCode = expectedCode,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        PairingDecision decision;
        try
        {
            decision = await waiter.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pairingWaiters.TryRemove(request.PairingSessionId, out _);
            return await ErrorAsync(context, StatusCodes.Status408RequestTimeout, ErrorCodes.Timeout,
                "等待本机用户确认配对超时。").ConfigureAwait(false);
        }

        if (!decision.Accepted)
        {
            _pairing.Cancel(request.PairingSessionId);
            return Results.Json(new PairResponse
            {
                Success = false,
                Accepted = false,
                ErrorCode = ErrorCodes.PairingRejected,
                Message = "对方用户拒绝配对。",
                VerificationCode = expectedCode,
            }, statusCode: StatusCodes.Status200OK);
        }

        // 用请求身份（TLS 证书 + 头部 deviceId）作为信任锚，而不是请求体里的自称值
        var trustedDeviceId = string.IsNullOrWhiteSpace(identity.DeviceId) ? request.DeviceId : identity.DeviceId;
        var trustedDeviceName = string.IsNullOrWhiteSpace(identity.DeviceName) ? deviceName : identity.DeviceName;

        try
        {
            await _trustStore.TrustAsync(trustedDeviceId, trustedDeviceName, identity.Fingerprint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已信任设备指纹不同（可能是重装程序，也可能是冒充）：必须让用户显式重新确认，
            // 不能因为一次配对请求就静默替换掉原有可信身份。
            _logger.LogWarning(ex, "写入信任关系被拒绝: {DeviceId}", trustedDeviceId);
            return await ErrorAsync(context, StatusCodes.Status409Conflict, ErrorCodes.IdentityChanged,
                "该设备已是可信设备但证书指纹不同，请先解除信任后重新配对。").ConfigureAwait(false);
        }

        await _devices.UpdateTrustAsync(trustedDeviceId, TrustState.Trusted, identity.Fingerprint,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new PairResponse
        {
            Success = true,
            Accepted = true,
            VerificationCode = expectedCode,
            DeviceId = _identity.DeviceId,
            DeviceName = _identity.DeviceName,
            CertificateFingerprint = _identity.CertificateFingerprint,
        });
    }

    private sealed record PairingDecision(bool Accepted);

    // ---------------------------------------------------------------- 建立传输

    private async Task<IResult> HandleCreateTransferAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<CreateTransferRequest>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "请求体不是合法的 JSON。").ConfigureAwait(false);

        if (request.ProtocolVersion != 0 && request.ProtocolVersion != AppConstants.ProtocolVersion)
        {
            return await ErrorAsync(context, StatusCodes.Status409Conflict, ErrorCodes.ProtocolIncompatible,
                $"目标设备 LAN Transfer 版本不兼容（对端协议 {request.ProtocolVersion}，本机 {AppConstants.ProtocolVersion}）。")
                .ConfigureAwait(false);
        }

        var identity = ResolveIdentity(context);

        if (string.IsNullOrWhiteSpace(identity.DeviceId))
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                $"缺少 {HeaderNames.DeviceId} 请求头。").ConfigureAwait(false);

        // 已信任设备的证书指纹发生变化：必须重新配对，不得静默接受
        if (identity.TrustState == TrustState.IdentityChanged)
        {
            _logger.LogWarning("拒绝来自身份变化设备的传输: {DeviceId}", identity.DeviceId);
            return await ErrorAsync(context, StatusCodes.Status409Conflict, ErrorCodes.IdentityChanged,
                "设备身份发生变化，请重新确认设备（重新配对）后再传输。").ConfigureAwait(false);
        }

        var isNewTransfer = _registry.Find(request.TransferId) is null;

        var (response, transfer) = await _registry.CreateOrGetFileAsync(request, identity.DeviceId,
            identity.DeviceName, identity.IsTrusted, cancellationToken).ConfigureAwait(false);

        if (transfer is null)
        {
            // 「待确认请求过多」不是请求格式错误，用 429 语义更准确（客户端只看响应体，不受影响）
            var status = response.ErrorCode == ErrorCodes.TooManyPendingRequests
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status400BadRequest;

            return Results.Json(response, statusCode: status);
        }

        // 只在「真的需要用户点一下」时才抛确认事件。
        // 已按设置自动放行的传输（可信设备 + 自动接收、或同步关系已授权）此刻状态已经是
        // Transferring：再弹一个确认框既与设置自相矛盾，用户点「拒绝」还会把已经开始接收的
        // 传输直接掐成 Rejected。
        if (isNewTransfer && transfer.IsAwaitingUserDecision)
        {
            IncomingTransferRequested?.Invoke(this, BuildEventArgs(transfer,
                transfer.Files.Values.OrderBy(f => f.FileIndex).FirstOrDefault(), identity.IsTrusted));
        }

        // 同一传输只能有一个审批等待流程：发送端按文件调用本接口，
        // 一个 N 文件的传输会在这里经过 N 次，每次都启动一个等待任务就是 N 个多余的
        // 10 分钟定时器（且都在等同一个 TaskCompletionSource）。
        if (response.Success && transfer.State == TransferState.WaitingApproval &&
            transfer.TryBeginApprovalFlow())
        {
            _ = RunApprovalFlowAsync(transfer);
        }

        _logger.LogInformation("建立传输 {TransferId} 来自 {Device}: {File}（{Size}），状态 {State}",
            transfer.TransferId, identity.DeviceName, request.FileName,
            LanTransfer.Common.Extensions.FormattingExtensions.ToSizeString(request.FileSize),
            transfer.State);

        return Results.Json(response);
    }

    private async Task RunApprovalFlowAsync(IncomingTransferState transfer)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var approved = await transfer.Approval.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            if (approved)
            {
                lock (transfer.Gate)
                {
                    if (!transfer.State.IsTerminal())
                        transfer.State = TransferState.Transferring;
                }

                _logger.LogInformation("传输 {TransferId} 已获用户允许，开始接收", transfer.TransferId);
            }
            else
            {
                lock (transfer.Gate)
                {
                    transfer.State = TransferState.Rejected;
                    transfer.ErrorCode = ErrorCodes.RejectedByUser;
                    transfer.ErrorMessage = "本机用户拒绝了本次传输。";
                }

                _logger.LogInformation("传输 {TransferId} 被本机用户拒绝", transfer.TransferId);
            }
        }
        catch (OperationCanceledException)
        {
            lock (transfer.Gate)
            {
                if (transfer.State == TransferState.WaitingApproval)
                {
                    transfer.State = TransferState.Failed;
                    transfer.ErrorCode = ErrorCodes.Timeout;
                    transfer.ErrorMessage = "等待用户确认超时。";
                }
            }

            _logger.LogWarning("传输 {TransferId} 等待确认超时", transfer.TransferId);
        }
        finally
        {
            RaiseIncomingStateChanged(transfer);
        }
    }

    // ---------------------------------------------------------------- 状态 / Chunk / 完成

    private IResult HandleGetStatus(HttpContext context, string transferId)
    {
        var identityError = ValidateTransferOwner(context, transferId, ResolveIdentity(context));
        if (identityError is not null) return identityError;

        var fileIndex = 0;
        if (context.Request.Query.TryGetValue("fileIndex", out var value) &&
            int.TryParse(value.ToString(), out var parsed))
        {
            fileIndex = parsed;
        }

        var status = _registry.GetStatus(transferId, fileIndex);

        if (!status.Success)
            return Results.Json(status, statusCode: StatusCodes.Status404NotFound);

        return Results.Json(status);
    }

    private async Task<IResult> HandleUploadChunkAsync(HttpContext context, string transferId, int chunkIndex,
        CancellationToken cancellationToken)
    {
        var identity = ResolveIdentity(context);

        var ownerError = ValidateTransferOwner(context, transferId, identity);
        if (ownerError is not null) return ownerError;

        var fileIndex = 0;
        if (context.Request.Query.TryGetValue("fileIndex", out var value) &&
            int.TryParse(value.ToString(), out var parsed))
        {
            fileIndex = parsed;
        }

        var (response, file) = await _registry
            .WriteChunkAsync(transferId, fileIndex, chunkIndex, context.Request.Body, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success)
        {
            var statusCode = response.ErrorCode switch
            {
                ErrorCodes.TransferNotFound => StatusCodes.Status404NotFound,
                ErrorCodes.FileNotFound => StatusCodes.Status404NotFound,
                ErrorCodes.ChunkIndexOutOfRange => StatusCodes.Status400BadRequest,
                ErrorCodes.TransferStateConflict => StatusCodes.Status409Conflict,
                ErrorCodes.AccessDenied => StatusCodes.Status507InsufficientStorage,
                _ => StatusCodes.Status400BadRequest,
            };

            return Results.Json(response, statusCode: statusCode);
        }

        if (file is not null)
        {
            var transfer = _registry.Find(transferId);
            if (transfer is not null) RaiseIncomingStateChanged(transfer, file);
        }

        _logger.LogDebug("已接收 {TransferId}/{File} Chunk #{Index}（{Count}/{Total}）",
            transferId, file?.FileName, chunkIndex, response.CompletedChunks, response.TotalChunks);

        return Results.Json(response);
    }

    private async Task<IResult> HandleCompleteAsync(HttpContext context, string transferId,
        CancellationToken cancellationToken)
    {
        var ownerError = ValidateTransferOwner(context, transferId, ResolveIdentity(context));
        if (ownerError is not null) return ownerError;

        var request = await ReadJsonAsync<CompleteTransferRequest>(context, cancellationToken)
            .ConfigureAwait(false);

        var fileIndex = request?.FileIndex ?? 0;
        var sha = request?.Sha256 ?? string.Empty;

        var (response, file) = await _registry.CompleteFileAsync(transferId, fileIndex, sha, cancellationToken)
            .ConfigureAwait(false);

        if (response.Success && file is not null)
        {
            IncomingFileCompleted?.Invoke(this, new IncomingFileCompletedEventArgs
            {
                TransferId = transferId,
                FileIndex = file.FileIndex,
                FileName = file.FileName,
                SavedPath = file.FinalPath,
                FileSize = file.FileSize,
                Verified = true,
            });

            _logger.LogInformation("传输 {TransferId} 文件 {File} 完成并校验通过 -> {Path}",
                transferId, file.FileName, file.FinalPath);
        }
        else
        {
            _logger.LogError("传输 {TransferId} 文件完成处理失败: {Code} {Message}",
                transferId, response.ErrorCode, response.Message);
        }

        var transfer = _registry.Find(transferId);
        if (transfer is not null) RaiseIncomingStateChanged(transfer, file);

        return Results.Json(response, statusCode: response.Success ? StatusCodes.Status200OK :
            response.ErrorCode == ErrorCodes.HashMismatch
                ? StatusCodes.Status422UnprocessableEntity
                : StatusCodes.Status400BadRequest);
    }

    private IResult HandleSimpleOp(HttpContext context, string transferId, string operation)
    {
        // 接收确认/拒绝是本机用户在界面上的决定，远程调用一律拒绝：
        // 否则任意主机都能把自己的传输置为「已批准」，绕过配对与用户确认直接写盘。
        if (operation is "approve" or "reject") return RemoteApprovalNotAllowed();

        var ownerError = ValidateTransferOwner(context, transferId, ResolveIdentity(context));
        if (ownerError is not null) return ownerError;

        var response = operation switch
        {
            "pause" => _registry.SetState(transferId, TransferState.Paused),
            "resume" => _registry.SetState(transferId, TransferState.Transferring,
                allowResumeFromTerminal: true),
            "cancel" => _registry.SetState(transferId, TransferState.Cancelled, ErrorCodes.CancelledByUser,
                "对端取消了该传输。"),
            _ => new SimpleOperationResponse
            {
                Success = false,
                TransferId = transferId,
                ErrorCode = ErrorCodes.BadRequest,
                Message = $"未知操作 {operation}。",
            },
        };

        var transfer = _registry.Find(transferId);
        if (transfer is not null) RaiseIncomingStateChanged(transfer);

        return Results.Json(response, statusCode: response.Success ? StatusCodes.Status200OK :
            StatusCodes.Status404NotFound);
    }

    private SimpleOperationResponse Approve(string transferId)
    {
        RespondToApproval(transferId, true, false);
        return _registry.SetState(transferId, TransferState.Transferring);
    }

    private SimpleOperationResponse Reject(string transferId)
    {
        RespondToApproval(transferId, false, false);
        return _registry.SetState(transferId, TransferState.Rejected, ErrorCodes.RejectedByUser,
            "本机用户拒绝了本次传输。");
    }

    // ---------------------------------------------------------------- 同步端点

    private async Task<IResult> HandleSyncRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        var request = await ReadJsonAsync<SyncRequest>(context, cancellationToken).ConfigureAwait(false);
        if (request is null)
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "请求体不是合法的 JSON。").ConfigureAwait(false);

        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var response = await _syncServer.HandleSyncRequestAsync(request, identity.DeviceId, identity.DeviceName,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(response);
    }

    private async Task<IResult> HandleSyncManifestAsync(HttpContext context, string syncPairId,
        CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        var request = await ReadJsonAsync<SyncManifestRequest>(context, cancellationToken)
            .ConfigureAwait(false);

        request ??= new SyncManifestRequest { SyncPairId = syncPairId };
        request.SyncPairId = syncPairId;

        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var response = await _syncServer.HandleManifestAsync(request, identity.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(response);
    }

    private async Task<IResult> HandleSyncNotificationAsync(HttpContext context, string syncPairId,
        CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var response = await _syncServer.HandleChangeNotificationAsync(syncPairId, identity.DeviceId,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(response, statusCode: response.Success
            ? StatusCodes.Status202Accepted
            : StatusCodes.Status403Forbidden);
    }

    private async Task<IResult> HandleSyncReadAsync(HttpContext context, string syncPairId,
        CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        var relativePath = context.Request.Query["relativePath"].ToString();
        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var result = await _syncServer.OpenReadAsync(syncPairId, relativePath, identity.DeviceId,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success || result.Content is null)
            return Results.Json(
                ApiErrorResponse.Create(result.ErrorCode ?? ErrorCodes.FileNotFound,
                    result.Message ?? "无法读取文件。"),
                statusCode: StatusCodes.Status404NotFound);

        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = result.Length;

        await using (result.Content)
        {
            await result.Content.CopyToAsync(context.Response.Body, AppConstants.StreamBufferSize,
                cancellationToken).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private async Task<IResult> HandleSyncWriteAsync(HttpContext context, string syncPairId,
        CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        // 同步内容是「整文件 PUT」，会超过全局 64MiB 请求体上限；
        // 不在这里放宽的话，大于 64MiB 的文件在同步上传方向永远拿 413（下载方向不受限）。
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = null;

        var relativePath = context.Request.Query["relativePath"].ToString();
        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var response = await _syncServer.WriteAsync(syncPairId, relativePath, context.Request.Body,
            identity.DeviceId, cancellationToken).ConfigureAwait(false);

        return Results.Json(response, statusCode: response.Success
            ? StatusCodes.Status200OK
            : StatusCodes.Status400BadRequest);
    }

    private async Task<IResult> HandleSyncDeleteAsync(HttpContext context, string syncPairId,
        CancellationToken cancellationToken)
    {
        if (_syncServer is null)
            return await ErrorAsync(context, StatusCodes.Status501NotImplemented, ErrorCodes.InternalError,
                "本机未启用同步模块。").ConfigureAwait(false);

        var request = await ReadJsonAsync<SyncDeleteRequest>(context, cancellationToken)
            .ConfigureAwait(false);

        if (request is null || request.RelativePaths.Count == 0)
            return await ErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "请求体不是合法的 JSON 或未指定文件。").ConfigureAwait(false);

        var identity = ResolveIdentity(context);
        var identityError = ValidateTrustedSyncIdentity(identity);
        if (identityError is not null) return identityError;

        var response = await _syncServer.DeleteAsync(syncPairId, request.RelativePaths, identity.DeviceId,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(response, statusCode: response.Success
            ? StatusCodes.Status200OK
            : StatusCodes.Status400BadRequest);
    }

    private static IResult? ValidateTrustedSyncIdentity(RequestIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.DeviceId))
            return Results.Json(ApiErrorResponse.Create(ErrorCodes.BadRequest,
                $"缺少 {HeaderNames.DeviceId} 请求头。"), statusCode: StatusCodes.Status400BadRequest);

        if (identity.TrustState == TrustState.IdentityChanged)
            return Results.Json(ApiErrorResponse.Create(ErrorCodes.IdentityChanged,
                "设备证书指纹已变化，请重新配对后再同步。"), statusCode: StatusCodes.Status409Conflict);

        if (!identity.IsTrusted)
            return Results.Json(ApiErrorResponse.Create(ErrorCodes.DeviceNotTrusted,
                "设备尚未完成可信配对。"), statusCode: StatusCodes.Status403Forbidden);

        return null;
    }

    /// <summary>
    /// 校验请求方是否为该入站传输的属主（即创建它的那台远端设备）。
    ///
    /// 为什么必须校验：这些端点（查状态 / 传分块 / 完成 / 暂停 / 继续 / 取消）此前完全不看调用方，
    /// 局域网内任意主机只要知道（或猜到）transferId，就能查询他人的传输内容、
    /// 取消或暂停他人的传输（DoS）。属主校验让这些操作只能由创建者发起。
    /// </summary>
    private IResult? ValidateTransferOwner(HttpContext context, string transferId, RequestIdentity identity)
    {
        var transfer = _registry.Find(transferId);

        if (transfer is null)
        {
            return Results.Json(ApiErrorResponse.Create(ErrorCodes.TransferNotFound, "找不到该传输。"),
                statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(identity.DeviceId) ||
            !string.Equals(transfer.RemoteDeviceId, identity.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("拒绝非属主的传输操作: {TransferId} 请求方={Requester} 属主={Owner}",
                transferId, identity.DeviceId, transfer.RemoteDeviceId);

            return Results.Json(ApiErrorResponse.Create(ErrorCodes.Unauthorized,
                    "无权操作该传输：只有发起该传输的设备可以操作它。"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>
    /// 接收确认/拒绝是本机用户在界面上的决定，**不存在**合法的远程调用方。
    /// 此前 /approve 允许任意主机把自己的传输置为「已批准」，从而在无需配对、
    /// 无需用户确认的情况下把文件写进接收目录。
    /// </summary>
    private static IResult RemoteApprovalNotAllowed() =>
        Results.Json(ApiErrorResponse.Create(ErrorCodes.Unauthorized,
                "接收确认只能由本机用户在界面上完成。"),
            statusCode: StatusCodes.Status403Forbidden);

    // ---------------------------------------------------------------- 辅助

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static IncomingTransferEventArgs BuildEventArgs(IncomingTransferState transfer,
        IncomingFileState? firstFile, bool isTrusted) => new()
        {
            TransferId = transfer.TransferId,
            RemoteDeviceId = transfer.RemoteDeviceId,
            RemoteDeviceName = transfer.RemoteDeviceName,
            RootName = transfer.RootName,
            TransferType = transfer.TransferType,
            TotalSize = transfer.TotalSize,
            TransferredSize = transfer.TransferredSize,
            TotalFiles = transfer.TotalFiles,
            FirstFileName = firstFile?.FileName ?? string.Empty,
            IsTrusted = isTrusted,
            State = transfer.State,
            ErrorCode = transfer.ErrorCode,
            ErrorMessage = transfer.ErrorMessage,
        };

    private void RaiseIncomingStateChanged(IncomingTransferState transfer, IncomingFileState? file = null)
    {
        IncomingTransferStateChanged?.Invoke(this, BuildEventArgs(transfer,
            file ?? transfer.Files.Values.OrderBy(f => f.FileIndex).FirstOrDefault(), false));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
