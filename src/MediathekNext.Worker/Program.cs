using MediathekNext.Infrastructure;
using MediathekNext.Worker.Roles;

// ──────────────────────────────────────────────────────────────
// Role detection — first match wins:
//   1. CLI:      dotnet run -- --role worker
//   2. Env var:  MEDIATHEK_ROLE=worker
//   3. Config:   appsettings.json → "Role": "worker"
//   4. Default:  standalone
// ──────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var role = ResolveRole(args, builder.Configuration);

var dbOptions = new DbProviderOptions
{
    Provider         = builder.Configuration["Database:Provider"] ?? "sqlite",
    ConnectionString = builder.Configuration["Database:ConnectionString"]
                       ?? "Data Source=/data/mediathek.db"
};

Console.WriteLine($"[MediathekNext] role={role}  db={dbOptions.Provider}");

switch (role)
{
    case "standalone": StandaloneRole.Register(builder, dbOptions); break;
    case "core":       CoreRole.Register(builder, dbOptions);       break;
    case "worker":     WorkerRole.Register(builder, dbOptions);     break;
    default: throw new InvalidOperationException(
        $"Unknown role '{role}'. Valid values: standalone, core, worker");
}

var app = builder.Build();

if (role is "standalone" or "core")
    await app.MigrateDatabaseAsync();

switch (role)
{
    case "standalone": StandaloneRole.Configure(app); break;
    case "core":       CoreRole.Configure(app);       break;
    case "worker":
        app.MapDefaultEndpoints();
        WorkerRole.Configure(app);   // registers app.UseTickerQ()
        break;
}

await app.RunAsync();

// ──────────────────────────────────────────────────────────────
// Role resolution helper
// ──────────────────────────────────────────────────────────────

static string ResolveRole(string[] args, IConfiguration config)
{
    // 1. CLI arg: --role <value>
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--role", StringComparison.OrdinalIgnoreCase))
            return args[i + 1].ToLowerInvariant();

    // 2. Environment variable
    var fromEnv = Environment.GetEnvironmentVariable("MEDIATHEK_ROLE");
    if (!string.IsNullOrWhiteSpace(fromEnv))
        return fromEnv.ToLowerInvariant();

    // 3. appsettings.json / appsettings.{env}.json
    var fromConfig = config["Role"];
    if (!string.IsNullOrWhiteSpace(fromConfig))
        return fromConfig.ToLowerInvariant();

    // 4. Default
    return "standalone";
}
