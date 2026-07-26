using Krautwatch.Application;
using Krautwatch.Application.Crawling;
using Krautwatch.Infrastructure;
using Wolverine;
using Wolverine.Postgresql;

// Krautwatch Zdf agent (DR-009). A microservice host crawling the ZDF Mediathek into Postgres via
// the Application/Crawling Action, over the durable Wolverine bus.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres connection injected by Aspire (AppHost: agent.WithReference(db)).
var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();

// ZDF crawler behind the IBroadcasterCrawler port.
builder.Services.AddZdfCrawler();

// Crawl schedule — bound from the "Crawl" config section; seeded with the show proven live (PR #34).
var crawlOptions = new CrawlOptions();
builder.Configuration.GetSection(CrawlOptions.SectionName).Bind(crawlOptions);
if (crawlOptions.Targets.Count == 0)
    crawlOptions.Targets = [new CrawlTarget("zdf", "heute-show")];
builder.Services.AddSingleton(crawlOptions);
builder.Services.AddHostedService<CrawlSchedulerService>();

// Durable Wolverine (Postgres transport) — the shared message store with the API + other agents.
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString);
    opts.Policies.UseDurableLocalQueues();
    // Discover the Crawling Action (CrawlShowHandler) in the Application assembly.
    opts.Discovery.IncludeAssembly(typeof(CrawlShowCommand).Assembly);
});

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.Run();
