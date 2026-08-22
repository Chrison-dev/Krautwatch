using Krautwatch.Application;
using Krautwatch.Application.Crawling;
using Krautwatch.Infrastructure;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.Postgresql;

// Krautwatch Ard agent (DR-009). A microservice host crawling the ARD platform (regular ARD + KiKA)
// into Postgres via the Application/Crawling Action, over the durable Wolverine bus.
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

// ARD + KiKA crawlers behind the IBroadcasterCrawler port.
builder.Services.AddArdCrawlers(builder.Configuration);
builder.Services.AddMessageDispatcher(); // Wolverine dispatch for the scheduler (this host has Wolverine)

// Crawl schedule — bound from the "Crawl" config section; seeded with the shows proven live (PR #34).
var crawlOptions = new CrawlOptions();
builder.Configuration.GetSection(CrawlOptions.SectionName).Bind(crawlOptions);
if (crawlOptions.Targets.Count == 0)
    crawlOptions.Targets =
    [
        new CrawlTarget("ard", "Extra 3"),
        new CrawlTarget("kika", "Biene Maja"),
    ];
builder.Services.AddSingleton(crawlOptions);

// Pre-warming from Sonarr/Radarr (#6) needs the outbound *arr client, and only then — an agent that
// does not opt in carries no reach-back at all, which is DR-011's requirement rather than an oversight.
// The handler is registered here rather than in AddApplication() for the same reason: with no
// IArrClient registered, its descriptor fails ValidateOnBuild even though nothing resolves it (#116).
if (crawlOptions.PreWarmFromArrInstances)
{
    builder.Services.AddArrClient();
    builder.Services.AddScoped<PreWarmCrawlTargetsHandler>();
}

builder.Services.AddHostedService<CrawlSchedulerService>();

// Durable Wolverine (Postgres transport) — the shared message store with the API + other agents.
builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString);
    opts.Policies.UseDurableLocalQueues();
    // Wolverine 6 changed the default ServiceLocationPolicy to NotAllowed (5.x was AllowedButWarn),
    // which refuses to generate a handler needing container resolution. CrawlShowHandler needs it:
    // IEnumerable<IBroadcasterCrawler> is an opaque lambda registration, and IEpisodeRepository's
    // graph reaches EF's own DbContextOptions factory — not something we control. Restore the 5.x
    // behaviour: allowed, but keep Wolverine's warning so the nudge to inline stays visible.
    opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;
    // Discover the Crawling Action (CrawlShowHandler) in the Application assembly.
    opts.Discovery.IncludeAssembly(typeof(CrawlShowCommand).Assembly);
});

var app = builder.Build();

app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults

app.Run();
