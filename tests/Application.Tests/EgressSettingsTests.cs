using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

/// <summary>
/// Covers egress settings moving into the database so the UI can reach them (#45 surfaced by #54).
/// A proxy URL can embed credentials, so it is treated as one throughout.
/// </summary>
public class EgressSettingsTests
{
    private readonly ISettingsRepository _repository = Substitute.For<ISettingsRepository>();

    private AppSettings Given(string? proxyUrl = null, bool listEnabled = false)
    {
        var settings = new AppSettings
        {
            Id = 1,
            DownloadDirectory = "/downloads",
            EgressProxyUrl = proxyUrl,
            EgressProxyListEnabled = listEnabled,
        };
        _repository.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);
        return settings;
    }

    private Task<SettingsResponse> Save(SaveSettingsRequest request) =>
        new SaveSettingsHandler(_repository).HandleAsync(request, TestContext.Current.CancellationToken);

    private static SaveSettingsRequest Request(
        string? proxyUrl = null, bool listEnabled = false, int candidates = 5) =>
        new("/downloads", 2, 6, SearchWaitMode.ReturnFast, 8, null, proxyUrl, listEnabled, candidates);

    [Fact]
    public async Task A_blank_proxy_leaves_the_stored_one_alone()
    {
        // Same rule as the TVDB key: the read model only returns it masked, so a blank field must not
        // wipe a configured credential the UI could not have echoed back.
        var stored = Given(proxyUrl: "http://de-vps:3128");

        await Save(Request(proxyUrl: ""));

        stored.EgressProxyUrl.ShouldBe("http://de-vps:3128");
    }

    [Fact]
    public async Task The_clear_sentinel_removes_the_proxy()
    {
        // Without a distinct value there would be no way to remove a proxy at all, since blank already
        // means "unchanged".
        var stored = Given(proxyUrl: "http://de-vps:3128");

        await Save(Request(proxyUrl: SaveSettingsRequestValidator.ClearSentinel));

        stored.EgressProxyUrl.ShouldBeNull();
    }

    [Fact]
    public async Task The_proxy_is_never_returned_in_full()
    {
        Given(proxyUrl: "http://user:hunter2@de-vps:3128");

        var response = await Save(Request());

        response.EgressProxyUrlMasked.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task A_secret_reference_is_shown_verbatim_because_a_pointer_is_not_a_credential()
    {
        Given(proxyUrl: "env:DE_PROXY");

        (await Save(Request())).EgressProxyUrlMasked.ShouldBe("env:DE_PROXY");
    }

    [Fact]
    public async Task Mode_B_and_its_candidate_count_round_trip()
    {
        var stored = Given();

        await Save(Request(listEnabled: true, candidates: 9));

        stored.EgressProxyListEnabled.ShouldBeTrue();
        stored.EgressProxyListMaxCandidates.ShouldBe(9);
    }

    // ── validation ────────────────────────────────────────────

    [Theory]
    [InlineData("http://de-vps:3128")]
    [InlineData("https://de-vps:3128")]
    [InlineData("env:DE_PROXY")]           // secret reference, resolved at use time
    [InlineData("file:/run/secrets/px")]
    [InlineData("-")]                       // the clear sentinel
    [InlineData("")]                        // unchanged
    [InlineData(null)]
    public void Usable_proxy_values_validate(string? proxyUrl) =>
        new SaveSettingsRequestValidator().Validate(Request(proxyUrl)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("de-vps:3128")]             // no scheme
    [InlineData("ftp://de-vps:3128")]       // wrong scheme — HttpClient cannot use it
    [InlineData("not a url")]
    public void Unusable_proxy_values_are_rejected(string proxyUrl)
    {
        // A malformed proxy otherwise surfaces much later as an unexplained download failure.
        var result = new SaveSettingsRequestValidator().Validate(Request(proxyUrl));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("Egress proxy"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public void An_absurd_candidate_count_is_rejected(int candidates) =>
        new SaveSettingsRequestValidator().Validate(Request(candidates: candidates))
            .IsValid.ShouldBeFalse();
}
