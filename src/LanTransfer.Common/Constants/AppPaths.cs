namespace LanTransfer.Common.Constants;

/// <summary>
/// 应用目录约定。禁止在业务代码里硬编码用户目录。
/// </summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\LanTransfer</summary>
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppConstants.ProductName);

    /// <summary>%LOCALAPPDATA%\LanTransfer\Logs</summary>
    public static string LogDirectory => Path.Combine(RootDirectory, "Logs");

    /// <summary>%LOCALAPPDATA%\LanTransfer\settings.json</summary>
    public static string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    /// <summary>%LOCALAPPDATA%\LanTransfer\lan-transfer.db</summary>
    public static string DatabaseFile => Path.Combine(RootDirectory, "lan-transfer.db");

    /// <summary>%LOCALAPPDATA%\LanTransfer\identity.pfx（DPAPI 保护，仅本机可解）</summary>
    public static string IdentityCertificateFile => Path.Combine(RootDirectory, "identity.pfx");

    /// <summary>%LOCALAPPDATA%\LanTransfer\identity.key（DPAPI 加密的证书口令）</summary>
    public static string IdentityKeyFile => Path.Combine(RootDirectory, "identity.key");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
