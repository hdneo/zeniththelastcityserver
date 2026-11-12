using System.Net;
using Zenith.Protocol.Net;
using ZenithServer;
using ZenithServer.Accounts;
using ZenithServer.Http;
using ZenithServer.Storage;
using ZenithServer.ZenFabric;

// =====================================================================================
//  ZenithServer – community server skeleton for "Zenith: The Last City" (Unity/IL2CPP client)
//
//  Services:
//   * HTTP/HTTPS : Skywave login, Nexus (realm list/join), PlayFab emulation, stubs
//   * UDP        : ZenFabric runtime (LiteNetLib 1.0, RSA+XOR handshake, SpatialOS-style ops)
//
//  Start:  dotnet run --project server/ZenithServer
//  Config: appsettings.json (section "Zenith")
// =====================================================================================

var builder = WebApplication.CreateBuilder(args);
var opts = builder.Configuration.GetSection("Zenith").Get<ServerOptions>() ?? new ServerOptions();
var lootOptions = builder.Configuration.GetSection("Zenith:EnemyLoot")
    .Get<EnemyLootOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new();
opts.DataDirectory = Path.GetFullPath(opts.DataDirectory);
Directory.CreateDirectory(opts.DataDirectory);

var ca = LocalCertificateAuthority.LoadOrCreate(Path.Combine(opts.DataDirectory, "certs"));
var jsonStorage = new JsonGameStorage(opts.DataDirectory);
var mysqlConnection = builder.Configuration.GetConnectionString("ZenithMySql");

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(lootOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(ca);
builder.Services.AddSingleton<IGameStorage>(services => string.IsNullOrWhiteSpace(mysqlConnection)
    ? jsonStorage
    : new MySqlGameStorage(mysqlConnection, jsonStorage, services.GetRequiredService<ILogger<MySqlGameStorage>>()));
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<CharacterCommands>();
builder.Services.AddSingleton<IGameLogic, DefaultGameLogic>();
builder.Services.AddSingleton<ZenFabricRuntime>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ZenFabricRuntime>());
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);

builder.WebHost.ConfigureKestrel(k =>
{
    var ip = IPAddress.Parse(opts.Http.BindAddress);
    if (opts.Http.HttpPort > 0) k.Listen(ip, opts.Http.HttpPort);
    if (opts.Http.HttpsPort > 0)
        k.Listen(ip, opts.Http.HttpsPort, l => l.UseHttps(h =>
            h.ServerCertificateSelector = (_, sni) => ca.GetServerCertificate(sni ?? opts.PublicHost)));
});

var app = builder.Build();
app.UseMiddleware<HttpLogging>();

GameApiEndpoints.Map(app);
ClientBootstrapEndpoints.Map(app);
PlayFabEndpoints.Map(app);

// Everything unknown: 200 + empty JSON, marked as not implemented in http.jsonl.
app.MapFallback((HttpContext ctx) => Results.Json(new { }));

app.Lifetime.ApplicationStarted.Register(() =>
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ZenithServer");
    log.LogInformation("HTTP  : {Bind}:{Http} / HTTPS :{Https}", opts.Http.BindAddress, opts.Http.HttpPort, opts.Http.HttpsPort);
    log.LogInformation("Data  : {Dir}", opts.DataDirectory);
    log.LogInformation("Game storage: {Backend}", string.IsNullOrWhiteSpace(mysqlConnection) ? "JSON files" : "MySQL");
    log.LogInformation("Root CA for HTTPS (import it once yourself, see docs/04_Server_Addresses.md): {Cer}", ca.RootCerPath);
});

app.Run();
