using System.Net;
using FileSync.Core;
using FileSync.Network;
using FileSync.OneDrive;
using FileSync.Platform;
using FileSync.Security;
using FileSync.Sync;

namespace FileSync.Web;

public static class AppHost
{
    public static WebApplication Build(AppOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = [], ContentRootPath = AppContext.BaseDirectory });
        var store = new StateStore(options.DataPath);
        var instanceLock = new InstanceLock(store);
        var identity = new DeviceIdentity(store);
        store.Validate(store.Current);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(IPAddress.Loopback, options.UiPort);
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<FileScanner>();
        builder.Services.AddSingleton<TaskBackupService>();
        RecordRoutes.Register(builder.Services);
        builder.Services.AddSingleton<IFolderPicker, FolderPicker>();
        builder.Services.AddSingleton<SecureTransport>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SecureTransport>());
        builder.Services.AddSingleton<Discovery>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Discovery>());
        builder.Services.AddSingleton<PeerClient>();
        builder.Services.AddSingleton<OneDriveFiles>();
        builder.Services.AddSingleton<OneDrivePublisher>();
        builder.Services.AddSingleton<IRemoteFiles, RemoteFiles>();
        builder.Services.AddSingleton<Pairing>();
        builder.Services.AddSingleton<TransactionFiles>();
        builder.Services.AddSingleton<SyncEngine>();
        builder.Services.AddSingleton<IncomingServices>();
        builder.Services.AddSingleton<IncomingTransactions>();
        builder.Services.AddSingleton<RequestSecurity>();
        var app = builder.Build();
        var discovery = app.Services.GetRequiredService<Discovery>();
        discovery.PeerPort = options.PeerPort;
        discovery.Enabled = options.Discovery && !options.LoopbackOnly;
        var security = app.Services.GetRequiredService<RequestSecurity>();
        app.Use(security.Invoke);
        PeerRoutes.Map(app);
        TransactionRoutes.Map(app);
        LocalRoutes.Map(app);
        RecordRoutes.Map(app);
        app.Lifetime.ApplicationStopped.Register(identity.Dispose);
        app.Lifetime.ApplicationStopped.Register(instanceLock.Dispose);
        return app;
    }
}
