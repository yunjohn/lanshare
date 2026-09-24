using System.Net.Http;
using LanTransfer.Common.Constants;
using LanTransfer.Core.Interfaces;

namespace LanTransfer.Network.Client;

/// <summary>
/// 为每个请求附加本机身份头。服务端据此把请求与「已配对设备 + 证书指纹」绑定，
/// 证书指纹以 TLS 握手中实际出示的客户端证书为准，请求头只提供 DeviceId。
/// </summary>
internal sealed class DeviceHeaderHandler : DelegatingHandler
{
    private readonly IIdentityService _identity;

    public DeviceHeaderHandler(IIdentityService identity, HttpMessageHandler innerHandler)
        : base(innerHandler)
        => _identity = identity;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(HeaderNames.DeviceId, _identity.DeviceId);
        request.Headers.TryAddWithoutValidation(HeaderNames.DeviceName,
            Uri.EscapeDataString(_identity.DeviceName));
        request.Headers.TryAddWithoutValidation(HeaderNames.ProtocolVersion,
            AppConstants.ProtocolVersion.ToString());
        request.Headers.TryAddWithoutValidation(HeaderNames.AppVersion, AppConstants.AppVersion);

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>自定义协议头名称。</summary>
public static class HeaderNames
{
    public const string DeviceId = "X-Lan-Transfer-Device-Id";
    public const string DeviceName = "X-Lan-Transfer-Device-Name";
    public const string ProtocolVersion = "X-Lan-Transfer-Protocol-Version";
    public const string AppVersion = "X-Lan-Transfer-App-Version";
}
