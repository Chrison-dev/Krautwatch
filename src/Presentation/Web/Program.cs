using System.Threading.RateLimiting;
using Krautwatch.Application;
using Krautwatch.Application.Auth;
using Krautwatch.Application.Settings;
using Krautwatch.Infrastructure;
using Krautwatch.Web;
using Krautwatch.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

// Krautwatch standalone UI (Blazor Server). A first-party console to search the catalog, queue a
// download, and monitor progress — usable without a Sonarr/Radarr instance. Talks to the Application
// layer in-process; the Downloader agent does the actual pulling. Does NOT own EF migrations.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Load the static-web-assets manifest explicitly. The framework only does this in Development, and none of
// our hosts run as Development (there are no launch profiles, and Aspire does not set one), so
// _framework/blazor.web.js returned a 500 and compressed assets returned an empty 200 — the UI loaded
// unstyled and completely inert, with no error anywhere obvious. No-op when the manifest is absent, i.e. in
// a published app where the assets are copied into wwwroot. See #63.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("krautwatch")
    ?? "Host=localhost;Port=5432;Database=krautwatch;Username=postgres;Password=postgres";

builder.Services.AddInfrastructure(new DbProviderOptions
{
    Provider = "postgres",
    ConnectionString = connectionString,
});
builder.Services.AddApplication();

// Outbound Sonarr/Radarr client + the Action that uses it. Paired deliberately: the handler cannot be
// constructed without the client, so whichever host wants one must wire both (#4).
builder.Services.AddArrClient();
builder.Services.AddScoped<TestArrConnectionHandler>();

// TheTVDB (PR 3a) — the settings page reports whether a key is configured and where it came from, so this
// host needs the key source even though it does no matching itself.
builder.Services.AddTvdbCatalog(builder.Configuration);

// ──────────────────────────────────────────────────────────────
// Authentication (#48)
//
// The pluggable part is the *scheme*, chosen here in the composition root — not one Domain interface
// with local and OIDC implementations behind it. Local credentials are a verification concern that fits
// a port (ILocalCredentialStore); OIDC is a redirect/token protocol owned entirely by framework
// middleware, with nothing left for a Domain port to abstract. Both land on the same cookie and the
// same ClaimsPrincipal, so everything downstream is provider-agnostic.
//
// Default is `local`, so a fresh install lands on first-run setup instead of sitting wide open.
// `none` stays available for operators fronting Krautwatch with reverse-proxy forward-auth.
// See docs/plans/2026-07-27 - authentication.md
// ──────────────────────────────────────────────────────────────
var authProvider = (builder.Configuration["Auth:Provider"] ?? "local").ToLowerInvariant();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor(); // the auth pages need HttpContext to write the cookie
builder.Services.AddAuthorization();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Cookie.Name = "krautwatch.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax; // Lax, not Strict, so the post-login redirect works
        // SameAsRequest means "secure over HTTPS". Not Always: plenty of homelab deployments sit behind
        // a proxy that terminates TLS and reach this host over plain HTTP, and Always would silently
        // drop the cookie, making login look like it does nothing at all.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

if (authProvider is not "none")
{
    // A single-admin login with no throttling is an online password-guessing target.
    //
    // This has to be a global limiter with an explicit no-limiter partition for everything else, not a
    // named policy: Blazor routes every page through the single MapRazorComponents endpoint, so
    // RequireRateLimiting there would throttle the whole UI to a handful of requests a minute rather
    // than just the login form.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var isLoginPost = HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase);

            if (!isLoginPost)
                return RateLimitPartition.GetNoLimiter("unlimited");

            return RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                });
        });
    });
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error", createScopeForErrors: true);

app.UseAntiforgery();
app.UseAuthentication();

if (authProvider is "none")
    app.UseAnonymousAccess(); // after UseAuthentication so it wins; see AnonymousAccess
else
    app.UseRateLimiter();

app.UseAuthorization();

app.MapStaticAssets(); // serves wwwroot + the framework's blazor.web.js (.NET 10 static assets)
app.MapDefaultEndpoints(); // /health, /alive from ServiceDefaults
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// First-run: print the gated setup link. The token lives in memory for this process only, so it rotates
// on restart, and whoever can read the log (the operator) is the only one able to claim the instance.
if (authProvider is "local")
    await LogSetupLinkIfRequiredAsync(app);

app.Run();

static async Task LogSetupLinkIfRequiredAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Krautwatch.Setup");

    try
    {
        using var scope = app.Services.CreateScope();
        var setupState = scope.ServiceProvider.GetRequiredService<SetupStateHandler>();
        if (!await setupState.IsSetupRequiredAsync())
            return;

        var token = app.Services.GetRequiredService<SetupToken>();
        logger.LogWarning(
            "Krautwatch has no administrator yet. Open /setup?token={Token} to create one. "
            + "The token is valid until this process restarts.", token.Value);
    }
    catch (Exception ex)
    {
        // Never block startup on this — on a cold deploy the schema may not exist yet.
        logger.LogDebug(ex, "Could not determine whether first-run setup is required.");
    }
}
