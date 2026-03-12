using ServiceBusProxy.Services;

// ─── ServiceBus Emulator Proxy ───────────────────────────────────────────────
// Reads a large Config.json (potentially >50 entities), shards it across
// multiple Azure ServiceBus Emulator instances, and provides a single AMQP
// + HTTP management endpoint that applications can connect to transparently.
//
// See README.md for usage and architecture details.

var builder = WebApplication.CreateBuilder(args);

// ── Configuration from environment variables ─────────────────────────────────

var configPath       = Environment.GetEnvironmentVariable("PROXY_CONFIG_PATH")       ?? "/config/Config.json";
var statePath        = Environment.GetEnvironmentVariable("PROXY_STATE_PATH")        ?? "/state/proxy-state.json";
var emulatorConfigDir = Environment.GetEnvironmentVariable("EMULATOR_CONFIG_DIR")    ?? "/emulator-configs";
var proxyMgmtPort    = Environment.GetEnvironmentVariable("PROXY_MGMT_PORT")         ?? "5300";
var proxyAmqpPort    = Environment.GetEnvironmentVariable("PROXY_AMQP_PORT")         ?? "5672";

// Bind management HTTP server to the configured port
builder.WebHost.UseUrls($"http://0.0.0.0:{proxyMgmtPort}");

// Add environment variables as IConfiguration keys for DI
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PROXY_CONFIG_PATH"]       = configPath,
    ["PROXY_STATE_PATH"]        = statePath,
    ["EMULATOR_CONFIG_DIR"]     = emulatorConfigDir,
    ["PROXY_AMQP_PORT"]         = proxyAmqpPort,
    ["PROXY_MGMT_PORT"]         = proxyMgmtPort,
    ["SERVICEBUS_SQL_PASSWORD"] = Environment.GetEnvironmentVariable("SERVICEBUS_SQL_PASSWORD") ?? "ServiceBus_P@ss1",
    ["DOCKER_NETWORK"]          = Environment.GetEnvironmentVariable("DOCKER_NETWORK") ?? "local-infrastructure_default"
});

// ── Register services ────────────────────────────────────────────────────────

builder.Services.AddSingleton<EntityRouter>();
builder.Services.AddSingleton<StateManager>(sp =>
    new StateManager(statePath, sp.GetRequiredService<ILogger<StateManager>>()));
builder.Services.AddSingleton<EmulatorOrchestrator>();
builder.Services.AddSingleton<AmqpProxy>();
builder.Services.AddSingleton<ManagementProxy>();
builder.Services.AddHttpClient("management", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

var logger       = app.Services.GetRequiredService<ILogger<Program>>();
var orchestrator = app.Services.GetRequiredService<EmulatorOrchestrator>();
var amqpProxy    = app.Services.GetRequiredService<AmqpProxy>();
var mgmtProxy    = app.Services.GetRequiredService<ManagementProxy>();

// ── Banner ───────────────────────────────────────────────────────────────────

logger.LogInformation("╭─────────────────────────────────────────────────────╮");
logger.LogInformation("│    ServiceBus Emulator Proxy  ·  starting…         │");
logger.LogInformation("╰─────────────────────────────────────────────────────╯");
logger.LogInformation("Config:   {Path}", configPath);
logger.LogInformation("State:    {Path}", statePath);
logger.LogInformation("AMQP:     port {Port}", proxyAmqpPort);
logger.LogInformation("Mgmt:     port {Port}", proxyMgmtPort);

// ── Initialise orchestrator (shard config, spin up emulators) ────────────────

try
{
    await orchestrator.InitialiseAsync(configPath);
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Failed to initialise emulator orchestrator");
    return 1;
}

// ── Start AMQP proxy ─────────────────────────────────────────────────────────

try
{
    amqpProxy.Start();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Failed to start AMQP proxy");
    return 1;
}

// ── Configure HTTP management proxy routes ───────────────────────────────────

app.Map("/{**path}", mgmtProxy.HandleRequestAsync);

// ── Graceful shutdown ────────────────────────────────────────────────────────

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Shutting down AMQP proxy…");
    amqpProxy.Stop();
});

logger.LogInformation("ServiceBus Emulator Proxy is ready");
logger.LogInformation("  AMQP endpoint: amqp://localhost:{AmqpPort}", proxyAmqpPort);
logger.LogInformation("  Management:    http://localhost:{MgmtPort}", proxyMgmtPort);

// ── Run ──────────────────────────────────────────────────────────────────────

await app.RunAsync();
return 0;
