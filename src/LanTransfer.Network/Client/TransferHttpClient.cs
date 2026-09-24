using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using LanTransfer.Common.Constants;
using LanTransfer.Common.Protocol;
using LanTransfer.Core.Interfaces;
using LanTransfer.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Client;

/// <summary>
/// 发送端 HTTP(S) 客户端。
/// TLS 采用「应用自维护信任」模式：不安装到系统根证书存储，
/// 而是对已记录指纹的主机做指纹固定（pinning），指纹变化直接拒绝连接。
/// </summary>
public sealed class TransferHttpClient : ITransferClient, IDisposable
{
    private readonly IPeerCertificateRegistry _certificates;
    private readonly ILogger<TransferHttpClient> _logger;
    private readonly HttpClient _http;
    private readonly bool _disposeHttp;

    public TransferHttpClient(IPeerCertificateRegistry certificates, IIdentityService identity,
        ILogger<TransferHttpClient> logger, HttpClient? httpClient = null)
    {
        _certificates = certificates;
        _logger = logger;
        _identity = identity;

        if (httpClient is not null)
        {
            _http = httpClient;
            _disposeHttp = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            };

            // 关闭 TLS 会话复用：复用会话时服务端不会重新出示证书，
            // 证书校验回调被跳过，等于绕过下面的指纹固定（pinning）。
            // 局域网内每次连接重新握手开销可忽略，安全收益更重要。
            handler.SslOptions.AllowTlsResume = false;

            // 应用自行校验证书，不使用系统信任链
            handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;

            // 双向 TLS：向服务端出示本机证书，服务端据此把 DeviceId 与证书指纹绑定。
            //
            // 必须在「每次 TLS 握手时」按需取证书，不能在构造时快照：
            // 本类可能在 IIdentityService.InitializeAsync() 之前就被 DI 构造出来
            // （App.InitializeSubsystemsAsync 先解析全部服务、再逐个初始化），
            // 那一刻 identity.Certificate 会抛「身份服务尚未初始化」。
            // 历史缺陷：构造时 try/catch 掉该异常 → 客户端证书永久缺失 →
            // 对端 /pair 以 400「未收到客户端证书」拒绝，且永远不弹配对确认框。
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => ResolveClientCertificate();

            // 构造时若身份已就绪就顺带填上候选列表；未就绪则留空，交给上面的回调在握手时补。
            var certificate = TryGetCertificate();
            if (certificate is not null)
            {
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            }
            else
            {
                _logger.LogDebug("构造客户端时本机身份尚未就绪，客户端证书将在 TLS 握手时按需获取。");
            }

            _http = new HttpClient(new DeviceHeaderHandler(identity, handler), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _disposeHttp = true;
        }
    }

    private readonly IIdentityService _identity;

    /// <summary>
    /// 握手时选取客户端证书。身份尚未初始化时返回 null（退化为单向 TLS）并记一条警告，
    /// 绝不抛异常——回调里抛异常会让 TLS 握手直接失败。
    /// </summary>
    private X509Certificate2? ResolveClientCertificate()
    {
        var certificate = TryGetCertificate();

        if (certificate is null)
        {
            _logger.LogWarning("本机身份尚未就绪，本次请求未出示客户端证书；" +
                               "对端将无法把 DeviceId 与证书指纹绑定（配对会被拒绝）。");
        }

        return certificate;
    }

    private X509Certificate2? TryGetCertificate()
    {
        try
        {
            return _identity.Certificate;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 证书校验：
    /// 1. 若该主机已有记录指纹，则必须完全一致，否则拒绝（防止中间人替换证书）；
    /// 2. 若首次接触，则记录指纹，后续由配对流程由用户确认后写入信任存储。
    /// sender 为 <see cref="SslStream"/>，通过 TargetHostName 还原目标主机。
    /// </summary>
    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            _logger.LogWarning("TLS 校验失败：对方未提供证书。");
            return false;
        }

        var host = (sender as SslStream)?.TargetHostName ?? string.Empty;
        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(certificate.GetRawCertData()));

        var expected = _certificates.GetExpected(host);

        if (expected is null)
        {
            _certificates.Remember(host, fingerprint);
            return true;
        }

        if (!string.Equals(expected, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("TLS 指纹不匹配，已拒绝连接: {Host} 期望={Expected} 实际={Actual}",
                host, expected, fingerprint);
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- 基础请求

    private string BaseUrl(string host, int port) => $"https://{host}:{port}{AppConstants.ApiPrefix}";

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(string host, int port, string path,
        HttpMethod method, TRequest? body, CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using var request = new HttpRequestMessage(method, BaseUrl(host, port) + path);

        if (body is not null)
        {
            request.Content = new ByteArrayContent(ProtocolJson.Serialize(body));
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(requestTimeout ?? AppConstants.ApiTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token)
            .ConfigureAwait(false);

        try
        {
            return await System.Text.Json.JsonSerializer
                .DeserializeAsync<TResponse>(stream, ProtocolJson.Options, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "解析服务端响应失败: {Host}:{Port}{Path} HTTP {Status}",
                host, port, path, (int)response.StatusCode);
            return default;
        }
    }

    // ---------------------------------------------------------------- 接口实现

    public async Task<HealthResponse> PingAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<object, HealthResponse>(host, port, "/health", HttpMethod.Get, null,
            cancellationToken).ConfigureAwait(false);

        return result ?? new HealthResponse { Success = false, Status = "no-response" };
    }

    public async Task<DeviceResponse> GetDeviceInfoAsync(string host, int port,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<object, DeviceResponse>(host, port, "/device", HttpMethod.Get, null,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException($"目标 {host}:{port} 未返回设备信息（可能不是 LAN Transfer 服务）。");

        return result;
    }

    public async Task<PairResponse> PairAsync(string host, int port, PairRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PairRequest, PairResponse>(host, port, "/pair", HttpMethod.Post, request,
            cancellationToken, AppConstants.PairingTimeout).ConfigureAwait(false);

        return result ?? new PairResponse
        {
            Success = false,
            Accepted = false,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    public async Task<CreateTransferResponse> CreateTransferAsync(string host, int port,
        CreateTransferRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<CreateTransferRequest, CreateTransferResponse>(host, port, "/transfers",
            HttpMethod.Post, request, cancellationToken).ConfigureAwait(false);

        return result ?? new CreateTransferResponse
        {
            Success = false,
            TransferId = request.TransferId,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应，传输未能建立。",
        };
    }

    public async Task<TransferStatusResponse> GetTransferStatusAsync(string host, int port, string transferId,
        int fileIndex, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<object, TransferStatusResponse>(host, port,
            $"/transfers/{Uri.EscapeDataString(transferId)}?fileIndex={fileIndex}", HttpMethod.Get, null,
            cancellationToken).ConfigureAwait(false);

        return result ?? new TransferStatusResponse
        {
            Success = false,
            TransferId = transferId,
            State = "failed",
            ErrorCode = ErrorCodes.TransferNotFound,
            Message = "无法获取传输状态。",
        };
    }

    public async Task<ChunkUploadResponse> UploadChunkAsync(string host, int port, string transferId,
        int fileIndex, int chunkIndex, Stream chunkStream, int length,
        CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl(host, port)}/transfers/{Uri.EscapeDataString(transferId)}/chunks/{chunkIndex}" +
                  $"?fileIndex={fileIndex}";

        using var request = new HttpRequestMessage(HttpMethod.Put, url);

        var content = new StreamContent(chunkStream, AppConstants.StreamBufferSize);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;
        request.Content = content;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AppConstants.ChunkUploadTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
        var result = ProtocolJson.Deserialize<ChunkUploadResponse>(payload);

        if (result is null)
        {
            return new ChunkUploadResponse
            {
                Success = false,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ErrorCode = ErrorCodes.InternalError,
                Message = $"服务端返回无法解析的响应（HTTP {(int)response.StatusCode}）。",
            };
        }

        return result;
    }

    public async Task<CompleteTransferResponse> CompleteFileAsync(string host, int port, string transferId,
        CompleteTransferRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<CompleteTransferRequest, CompleteTransferResponse>(host, port,
            $"/transfers/{Uri.EscapeDataString(transferId)}/complete", HttpMethod.Post, request,
            cancellationToken).ConfigureAwait(false);

        return result ?? new CompleteTransferResponse
        {
            Success = false,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    public Task<SimpleOperationResponse> ApproveAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default) =>
        SimpleOpAsync(host, port, transferId, "approve", cancellationToken);

    public Task<SimpleOperationResponse> RejectAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default) =>
        SimpleOpAsync(host, port, transferId, "reject", cancellationToken);

    public Task<SimpleOperationResponse> PauseAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default) =>
        SimpleOpAsync(host, port, transferId, "pause", cancellationToken);

    public Task<SimpleOperationResponse> ResumeAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default) =>
        SimpleOpAsync(host, port, transferId, "resume", cancellationToken);

    public Task<SimpleOperationResponse> CancelAsync(string host, int port, string transferId,
        CancellationToken cancellationToken = default) =>
        SimpleOpAsync(host, port, transferId, "cancel", cancellationToken);

    private async Task<SimpleOperationResponse> SimpleOpAsync(string host, int port, string transferId,
        string operation, CancellationToken cancellationToken)
    {
        var result = await SendAsync<object, SimpleOperationResponse>(host, port,
            $"/transfers/{Uri.EscapeDataString(transferId)}/{operation}", HttpMethod.Post, null,
            cancellationToken).ConfigureAwait(false);

        return result ?? new SimpleOperationResponse
        {
            Success = false,
            TransferId = transferId,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    public async Task<SyncRequestResponse> RequestSyncAsync(string host, int port, SyncRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<SyncRequest, SyncRequestResponse>(host, port, "/sync/pairs",
            HttpMethod.Post, request, cancellationToken).ConfigureAwait(false);

        return result ?? new SyncRequestResponse
        {
            Success = false,
            Accepted = false,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    /// <summary>交换同步清单（供 Sync Engine 使用）。</summary>
    public async Task<SyncManifestResponse> ExchangeManifestAsync(string host, int port,
        SyncManifestRequest request, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<SyncManifestRequest, SyncManifestResponse>(host, port,
            $"/sync/pairs/{Uri.EscapeDataString(request.SyncPairId)}/manifest", HttpMethod.Post, request,
            cancellationToken).ConfigureAwait(false);

        return result ?? new SyncManifestResponse
        {
            Success = false,
            SyncPairId = request.SyncPairId,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    public async Task<SimpleOperationResponse> NotifySyncChangedAsync(string host, int port, string syncPairId,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<object, SimpleOperationResponse>(host, port,
            $"/sync/pairs/{Uri.EscapeDataString(syncPairId)}/notify", HttpMethod.Post, null,
            cancellationToken).ConfigureAwait(false);

        return result ?? new SimpleOperationResponse
        {
            Success = false,
            TransferId = syncPairId,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "无法通知对端执行实时同步。",
        };
    }

    public async Task<SimpleOperationResponse> UploadSyncFileAsync(string host, int port, string syncPairId,
        string relativePath, Stream content, long length, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl(host, port)}/sync/pairs/{Uri.EscapeDataString(syncPairId)}/content" +
                  $"?relativePath={Uri.EscapeDataString(relativePath)}";

        using var request = new HttpRequestMessage(HttpMethod.Put, url);

        var streamContent = new StreamContent(content, AppConstants.StreamBufferSize);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        streamContent.Headers.ContentLength = length;
        request.Content = streamContent;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AppConstants.ChunkUploadTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        var payload = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);

        return ProtocolJson.Deserialize<SimpleOperationResponse>(payload) ?? new SimpleOperationResponse
        {
            Success = false,
            ErrorCode = ErrorCodes.InternalError,
            Message = $"服务端返回无法解析的响应（HTTP {(int)response.StatusCode}）。",
        };
    }

    public async Task<bool> DownloadSyncFileAsync(string host, int port, string syncPairId,
        string relativePath, Stream destination, CancellationToken cancellationToken = default)
    {
        var url = $"{BaseUrl(host, port)}/sync/pairs/{Uri.EscapeDataString(syncPairId)}/content" +
                  $"?relativePath={Uri.EscapeDataString(relativePath)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AppConstants.ChunkUploadTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return false;

        await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token)
            .ConfigureAwait(false);

        await source.CopyToAsync(destination, AppConstants.StreamBufferSize, timeoutCts.Token)
            .ConfigureAwait(false);

        return true;
    }

    public async Task<SimpleOperationResponse> DeleteSyncFilesAsync(string host, int port, string syncPairId,
        IReadOnlyList<string> relativePaths, CancellationToken cancellationToken = default)
    {
        var request = new SyncDeleteRequest();
        request.RelativePaths.AddRange(relativePaths);

        var result = await SendAsync<SyncDeleteRequest, SimpleOperationResponse>(host, port,
            $"/sync/pairs/{Uri.EscapeDataString(syncPairId)}/delete", HttpMethod.Post, request,
            cancellationToken).ConfigureAwait(false);

        return result ?? new SimpleOperationResponse
        {
            Success = false,
            ErrorCode = ErrorCodes.NetworkUnreachable,
            Message = "对方无响应。",
        };
    }

    public void Dispose()
    {
        if (_disposeHttp) _http.Dispose();
    }
}
