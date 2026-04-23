using MediathekNext.Web.ApiClient;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Use Aspire service discovery name "api" in deployed envs,
// or the configured ApiBaseUrl in standalone / dev without Aspire
var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? "http://api";

builder.Services.AddHttpClient<MediathekApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<MediathekNext.Web.Components.App>()
   .AddInteractiveServerRenderMode();
app.MapDefaultEndpoints();

app.Run();
