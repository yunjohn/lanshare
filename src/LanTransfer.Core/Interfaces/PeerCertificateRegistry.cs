using System.Collections.Concurrent;

namespace LanTransfer.Core.Interfaces;

/// <summary>
/// 记录「主机 → 证书指纹」的映射，用于 TLS 指纹固定（pinning）。
/// 应用自行维护信任，不依赖 Windows 根证书存储。
/// </summary>
public interface IPeerCertificateRegistry
{
    /// <summary>记录主机当前证书指纹。</summary>
    void Remember(string host, string fingerprint);

    /// <summary>取已记录指纹；未知返回 null。</summary>
    string? GetExpected(string host);

    /// <summary>移除记录（例如用户解除信任后重新配对）。</summary>
    void Forget(string host);
}

/// <summary>默认实现（进程内）。</summary>
public sealed class PeerCertificateRegistry : IPeerCertificateRegistry
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Remember(string host, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fingerprint)) return;
        _map[host] = fingerprint;
    }

    public string? GetExpected(string host) =>
        _map.TryGetValue(host, out var value) ? value : null;

    public void Forget(string host) => _map.TryRemove(host, out _);
}
