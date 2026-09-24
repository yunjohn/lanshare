using System.Reflection;
using System.Runtime.InteropServices;
using LanTransfer.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace LanTransfer.Network.Diagnostics;

/// <summary>
/// 通过 Windows Network List Manager (NLM) 判断当前网络类别。
/// 使用 IDispatch 后期绑定，避免手写 vtable 带来的兼容性风险；
/// 任何失败都降级为 Unknown，绝不抛异常中断启动。
/// </summary>
public sealed class NetworkCategoryDetector
{
    private const string NetworkListManagerClsid = "DCB00C01-570F-4A9B-8D69-199FDBA5723B";
    private const int EnumNetworkConnected = 0x1;

    private readonly ILogger<NetworkCategoryDetector> _logger;

    public NetworkCategoryDetector(ILogger<NetworkCategoryDetector> logger) => _logger = logger;

    /// <summary>
    /// 返回当前活动网络类别。多张网卡同时连接时，只要存在「公用网络」即返回 Public，
    /// 因为安全策略必须按最保守的情况处理。
    /// </summary>
    public NetworkCategory Detect()
    {
        if (!OperatingSystem.IsWindows()) return NetworkCategory.Unknown;

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(NetworkListManagerClsid));
            if (type is null) return NetworkCategory.Unknown;

            var manager = Activator.CreateInstance(type);
            if (manager is null) return NetworkCategory.Unknown;

            var networks = Invoke(manager, "GetNetworks", EnumNetworkConnected);
            if (networks is null) return NetworkCategory.Unknown;

            var count = Convert.ToInt32(Invoke(networks, "GetCount") ?? 0);
            if (count <= 0) return NetworkCategory.Unknown;

            var hasPrivate = false;
            var hasPublic = false;
            var hasDomain = false;

            for (var i = 0; i < count; i++)
            {
                var network = NextNetwork(networks);
                if (network is null) continue;

                var category = Convert.ToInt32(Invoke(network, "Category") ?? -1);

                switch (category)
                {
                    case 0:
                        hasPublic = true;
                        break;
                    case 1:
                        hasPrivate = true;
                        break;
                    case 2:
                        hasDomain = true;
                        break;
                }

                Release(network);
            }

            Release(networks);
            Release(manager);

            if (hasPublic) return NetworkCategory.Public;
            if (hasPrivate) return NetworkCategory.Private;
            if (hasDomain) return NetworkCategory.DomainAuthenticated;
            return NetworkCategory.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "无法通过 NLM 判断网络类别，降级为 Unknown");
            return NetworkCategory.Unknown;
        }
    }

    private static object? NextNetwork(object enumNetworks)
    {
        var args = new object?[] { 1u, null, null };
        var modifiers = new[] { new ParameterModifier(3) };
        modifiers[0][1] = true;
        modifiers[0][2] = true;

        enumNetworks.GetType().InvokeMember(
            "Next",
            BindingFlags.InvokeMethod,
            binder: null,
            target: enumNetworks,
            args: args,
            modifiers: modifiers,
            culture: null,
            namedParameters: null);

        return args[1];
    }

    private static object? Invoke(object target, string member, params object?[] args)
        => target.GetType().InvokeMember(member, BindingFlags.InvokeMethod | BindingFlags.GetProperty,
            null, target, args);

    private static void Release(object? comObject)
    {
        if (comObject is null) return;

        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 忽略释放失败
        }
    }
}
