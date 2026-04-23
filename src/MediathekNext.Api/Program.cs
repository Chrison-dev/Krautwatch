using MediathekNext.Application;
using MediathekNext.Api.Endpoints;
using MediathekNext.Infrastructure;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var dbPath = builder.Configuration["Database:Path"] ?? "/data/mediathek.db";

builder.Services.AddInfrastructure(new DbProviderOptions { ConnectionString = $"Data Source={dbPath}" });
builder.Services.AddApplication();

builder.Services.AddOpenApi();

// Wolverine — API only publishes messages, Worker consumes them
// In-process transport: messages go to the Worker via the durable local queue
builder.UseWolverine(opts =>
{
    opts.PublishMessage<MediathekNext.Application.Downloads.StartDownloadCommand>()
        .ToLocalQueue("downloads");
});

var app = builder.Build();

app.MapOpenApi();
app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.MapCatalogEndpoints();
app.MapDownloadEndpoints();
app.MapSettingsEndpoints();

app.Run();
