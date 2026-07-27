using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Shouldly;
using Xunit;

namespace Krautwatch.Architecture.Tests;

/// <summary>
/// Blazor has no equivalent of a fallback authorization policy for routable components — every page
/// carries its own decision. That makes "someone adds a page and forgets [Authorize]" a silent way to
/// expose the UI, so the decision is made mandatory here instead of remembered.
/// </summary>
public class PageAuthorizationSpecs
{
    private static IEnumerable<Type> RoutablePages =>
        typeof(Krautwatch.Web.Components.App).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any());

    [Fact]
    public void Every_routable_page_declares_an_authorization_decision()
    {
        var undeclared = RoutablePages
            .Where(t => !t.GetCustomAttributes<AuthorizeAttribute>().Any()
                     && !t.GetCustomAttributes<AllowAnonymousAttribute>().Any())
            .Select(t => t.FullName!)
            .ToList();

        undeclared.ShouldBeEmpty(
            "Every routable page must carry [Authorize] or [AllowAnonymous] — Blazor has no fallback "
            + "policy for components, so an undecorated page is publicly reachable. Offenders: "
            + string.Join(", ", undeclared));
    }

    [Fact]
    public void Only_the_auth_pages_are_anonymous()
    {
        // Anonymous access is the exception and should stay a short, reviewed list — if this fails,
        // either the new page genuinely belongs here or it should not be anonymous.
        var expected = new[] { "Login", "Logout", "Setup" };

        var anonymous = RoutablePages
            .Where(t => t.GetCustomAttributes<AllowAnonymousAttribute>().Any())
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        anonymous.ShouldBe(expected.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Sanity_the_pages_were_actually_discovered()
    {
        // Guards against the two specs above passing vacuously if reflection ever stops finding pages.
        RoutablePages.Count().ShouldBeGreaterThanOrEqualTo(6);
    }
}
