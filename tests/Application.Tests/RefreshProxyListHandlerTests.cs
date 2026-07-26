using Krautwatch.Application.Downloads;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class RefreshProxyListHandlerTests
{
    private static Proxy P(string host) => new()
    {
        Id = $"{host}:3128", Host = host, Port = 3128, Protocol = "http", Source = "geonode", Country = "DE",
    };

    [Fact]
    public async Task Upserts_the_fetched_candidates()
    {
        var source = Substitute.For<IProxyListSource>();
        source.FetchAsync(Arg.Any<CancellationToken>()).Returns([P("1.1.1.1"), P("2.2.2.2")]);
        var repo = Substitute.For<IProxyRepository>();

        await new RefreshProxyListHandler(source, repo, NullLogger<RefreshProxyListHandler>.Instance).HandleAsync();

        await repo.Received(1).UpsertBatchAsync(
            Arg.Is<IEnumerable<Proxy>>(ps => ps != null && ps.Count() == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_empty_fetch_keeps_the_cached_rows_untouched()
    {
        var source = Substitute.For<IProxyListSource>();
        source.FetchAsync(Arg.Any<CancellationToken>()).Returns([]);
        var repo = Substitute.For<IProxyRepository>();

        await new RefreshProxyListHandler(source, repo, NullLogger<RefreshProxyListHandler>.Instance).HandleAsync();

        await repo.DidNotReceive().UpsertBatchAsync(Arg.Any<IEnumerable<Proxy>>(), Arg.Any<CancellationToken>());
    }
}
