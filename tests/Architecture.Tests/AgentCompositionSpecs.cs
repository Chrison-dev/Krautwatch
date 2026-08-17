using Krautwatch.Application;
using Krautwatch.Application.Crawling;
using Krautwatch.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wolverine;
using Xunit;

namespace Krautwatch.Architecture.Tests;

/// <summary>
/// Builds each crawler agent's service graph the way its host does, with the validation the .NET host
/// turns on in Development.
/// </summary>
/// <remarks>
/// <para>
/// Nothing exercised the agents' DI graph before, and two faults lived there undetected: a singleton
/// <c>BackgroundService</c> consuming a scoped dispatcher, and a handler registered without its
/// dependency. Both killed the host at <c>builder.Build()</c> under the dev fleet, and neither could
/// fail a build or a test (#116).
/// </para>
/// <para>
/// These specs mirror the hosts' registrations rather than invoking their top-level <c>Program</c>,
/// which cannot be composed without also starting Postgres and Wolverine. <b>That mirroring is the
/// weakness: change an agent's Program.cs and change these too.</b> The alternative — a real host
/// start — needs containers and would not run in the PR gate.
/// </para>
/// </remarks>
public class AgentCompositionSpecs
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_ard_agent_composes(bool preWarm) =>
        AgentServices(preWarm, services => services.AddArdCrawlers()).ShouldBuild();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_zdf_agent_composes(bool preWarm) =>
        AgentServices(preWarm, services => services.AddZdfCrawler()).ShouldBuild();

    /// <summary>Everything an agent's Program.cs registers, minus the Wolverine transport itself.</summary>
    private static IServiceCollection AgentServices(bool preWarm, Action<IServiceCollection> crawlers)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddInfrastructure(new DbProviderOptions
        {
            Provider = "postgres",
            ConnectionString = "Host=localhost;Database=krautwatch;Username=postgres;Password=postgres",
        });
        services.AddApplication();

        crawlers(services);
        services.AddMessageDispatcher();

        // UseWolverine registers the real one against a live transport. The dispatcher only needs the
        // interface to exist for its call site to validate.
        services.AddScoped(_ => Substitute.For<IMessageBus>());

        var options = new CrawlOptions
        {
            Targets = [new CrawlTarget("ard", "Extra 3")],
            PreWarmFromArrInstances = preWarm,
        };
        services.AddSingleton(options);

        if (options.PreWarmFromArrInstances)
        {
            services.AddArrClient();
            services.AddScoped<PreWarmCrawlTargetsHandler>();
        }

        services.AddHostedService<CrawlSchedulerService>();

        return services;
    }
}

internal static class ServiceCollectionValidation
{
    /// <summary>
    /// Asserts the graph builds under the options a Development host uses.
    /// </summary>
    /// <remarks>
    /// <c>ValidateScopes</c> catches a scoped service captured by a singleton; <c>ValidateOnBuild</c>
    /// checks every registered descriptor rather than only what something happens to resolve. Both
    /// default to true in Development and to false in Production, which is exactly why these faults
    /// reached a released image while looking fine in CI.
    /// </remarks>
    public static void ShouldBuild(this IServiceCollection services)
    {
        var build = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        build.ShouldNotThrow();
    }
}
