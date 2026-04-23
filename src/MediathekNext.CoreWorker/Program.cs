using MediathekNext.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var dbPath = builder.Configuration["Database:Path"] ?? "/data/mediathek.db";

builder.Services.AddInfrastructure(new MediathekNext.Infrastructure.DbProviderOptions
{
    ConnectionString = $"Data Source={dbPath}"
});
builder.Services.AddMediathekViewCatalogProvider();
// TODO: builder.Services.AddCatalogRefreshService(); — not yet implemented

var host = builder.Build();

// Run EF Core migrations on startup — only CoreWorker does this (DR-002)
await host.MigrateDatabaseAsync();

host.Run();
