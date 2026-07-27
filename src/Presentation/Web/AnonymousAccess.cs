using System.Security.Claims;

namespace Krautwatch.Web;

/// <summary>
/// Support for <c>Auth:Provider = none</c>, where Krautwatch performs no authentication of its own —
/// intended for operators fronting it with reverse-proxy forward-auth. Never the default.
/// </summary>
/// <remarks>
/// Every page carries <c>[Authorize]</c> so a newly added page cannot be accidentally public, which
/// would otherwise make <c>none</c> unusable: each page would bounce to a login that can never succeed.
/// <para>
/// This is applied as middleware assigning <see cref="HttpContext.User"/> rather than by substituting an
/// <c>AuthenticationStateProvider</c>. Blazor derives authentication state from <c>HttpContext.User</c>
/// on the static-SSR path and seeds the interactive circuit from it, so setting the principal here is
/// the one place that both render modes agree on — replacing the provider only affected one of them.
/// </para>
/// </remarks>
public static class AnonymousAccess
{
    /// <summary>A permanently-authenticated admin principal.</summary>
    public static ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "anonymous"), new Claim(ClaimTypes.Role, "admin")],
        authenticationType: "None"));

    /// <summary>Treats every request as an authenticated admin.</summary>
    public static IApplicationBuilder UseAnonymousAccess(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.User = Principal;
            await next(context);
        });
}
