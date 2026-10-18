using System.Net;
using System.Security.Authentication;
using FileSync.Core;
using FileSync.Network;
using FileSync.Security;
using FileSync.Sync;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace FileSync.Web;

public static class AppHost
{
    public static WebApplication Build(AppOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = [], ContentRootPath = AppContext.BaseDirectory });
        var store = new StateStore(options.DataPath);
        var identity = new DeviceIdentity(store);
        store.Validate(store.Current);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(IPAddress.Loopback, options.UiPort);
            server.Listen(IPAddress.Any, options.PeerPort, listener => listener.UseHttps(https =>
            {
                https.ServerCertificate = identity.Certificate;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (cert, _, _) => DeviceIdentity.Valid(cert);
            }));
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<FileScanner>();
        builder.Services.AddSingleton<Discovery>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Discovery>());
        builder.Services.AddSingleton<PeerClient>();
        builder.Services.AddSingleton<IRemoteFiles>(sp => sp.GetRequiredService<PeerClient>());
        builder.Services.AddSingleton<Pairing>();
        builder.Services.AddSingleton<TransactionFiles>();
        builder.Services.AddSingleton<SyncEngine>();
        builder.Services.AddSingleton<RequestSecurity>();
        var app = builder.Build();
        var discovery = app.Services.GetRequiredService<Discovery>();
        discovery.PeerPort = options.PeerPort;
        discovery.Enabled = options.Discovery;
        var security = app.Services.GetRequiredService<RequestSecurity>();
        app.Use(security.Invoke);
        PeerRoutes.Map(app);
        LocalRoutes.Map(app);
        app.Lifetime.ApplicationStopped.Register(identity.Dispose);
        return app;
    }
}
