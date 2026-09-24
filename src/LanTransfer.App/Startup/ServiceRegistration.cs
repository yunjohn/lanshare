using LanTransfer.App.Services;
using LanTransfer.App.ViewModels;
using LanTransfer.App.Views;
using LanTransfer.Core.Configuration;
using LanTransfer.Core.Devices;
using LanTransfer.Core.Files;
using LanTransfer.Core.Hashing;
using LanTransfer.Core.Interfaces;
using LanTransfer.Core.Transfers;
using LanTransfer.Network.Client;
using LanTransfer.Network.Diagnostics;
using LanTransfer.Network.Discovery;
using LanTransfer.Network.Server;
using LanTransfer.Security.Certificates;
using LanTransfer.Security.Pairing;
using LanTransfer.Security.Trust;
using LanTransfer.Storage.Database;
using LanTransfer.Storage.Repositories;
using LanTransfer.Sync.Conflict;
using LanTransfer.Sync.Engine;
using LanTransfer.Sync.Metadata;
using LanTransfer.Sync.Planner;
using LanTransfer.Sync.Scanner;
using Microsoft.Extensions.DependencyInjection;

namespace LanTransfer.App.Startup;

/// <summary>依赖注入注册。所有模块通过接口解耦，UI 不直接触碰网络与存储实现。</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddLanTransfer(this IServiceCollection services)
    {
        // ---------- 配置 ----------
        services.AddSingleton<ISettingsService, SettingsService>();

        // ---------- 存储 ----------
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IDeviceRepository, DeviceRepository>();
        services.AddSingleton<ITransferRepository, TransferRepository>();
        services.AddSingleton<ISyncRepository, SyncRepository>();

        // ---------- 安全 ----------
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<ITrustStore, TrustStore>();
        services.AddSingleton<IPairingService, PairingService>();
        services.AddSingleton<IPeerCertificateRegistry, PeerCertificateRegistry>();

        // ---------- 核心 ----------
        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IChunkManager, ChunkManager>();
        services.AddSingleton<IFileScanner, FileScanner>();
        services.AddSingleton<ISafePathResolver, SafePathResolver>();

        // ---------- 网络 ----------
        services.AddSingleton<ITransferClient, TransferHttpClient>();
        services.AddSingleton<IncomingTransferRegistry>();
        services.AddSingleton<ITransferServer, TransferServer>();
        services.AddSingleton<IDeviceManager, DeviceManager>();
        services.AddSingleton<ITransferManager, TransferManager>();
        services.AddSingleton<IDiscoveryService, UdpDiscoveryService>();
        services.AddSingleton<NetworkCategoryDetector>();
        services.AddSingleton<NetworkDiagnosticsService>();
        services.AddSingleton<INetworkDiagnosticsService>(sp =>
            sp.GetRequiredService<NetworkDiagnosticsService>());

        // ---------- 同步 ----------
        services.AddSingleton<DirectoryScanner>();
        services.AddSingleton<SyncPlanner>();
        services.AddSingleton<SyncMetadataManager>();
        services.AddSingleton<ConflictResolver>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<ISyncServerHandler>(sp => sp.GetRequiredService<SyncEngine>());

        // ---------- UI ----------
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IAutoStartService, AutoStartService>();

        services.AddSingleton<TransferViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();

        return services;
    }
}
