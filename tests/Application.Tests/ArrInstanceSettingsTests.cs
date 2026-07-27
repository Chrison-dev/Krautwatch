using Krautwatch.Application.Settings;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class ArrInstanceReadModelTests
{
    [Fact]
    public async Task The_read_model_never_carries_the_full_api_key()
    {
        // The security-relevant assertion for this page: masking is what stops the settings UI being usable
        // to harvest credentials.
        var repo = Substitute.For<IArrInstanceRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([new ArrInstance
        {
            Name = "Sonarr", Kind = ArrKind.Sonarr, BaseUrl = "http://sonarr:8989",
            ApiKey = "supersecretkey1234",
        }]);

        var result = await new GetArrInstancesHandler(repo).HandleAsync(TestContext.Current.CancellationToken);

        var instance = result.ShouldHaveSingleItem();
        instance.ApiKeyMasked.ShouldBe("••••1234");
        instance.ApiKeyMasked.ShouldNotContain("supersecret");

        // Belt and braces: no property anywhere on the DTO holds the real key.
        instance.GetType().GetProperties()
            .Select(prop => prop.GetValue(instance) as string)
            .ShouldNotContain("supersecretkey1234");
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("abc", "••••")]        // too short to reveal any of it
    [InlineData("abcd", "••••")]
    [InlineData("abcde", "••••bcde")]
    public void Masking_never_reveals_more_than_the_last_four_characters(string key, string expected)
    {
        ArrInstanceMapper.Mask(key).ShouldBe(expected);
    }
}

public class SaveArrInstanceHandlerTests
{
    private static ArrInstance Existing(string apiKey = "original-key") => new()
    {
        Id = Guid.NewGuid(), Name = "Sonarr", Kind = ArrKind.Sonarr,
        BaseUrl = "http://sonarr:8989", ApiKey = apiKey, Enabled = true,
    };

    [Fact]
    public async Task Creates_a_new_instance()
    {
        var repo = Substitute.For<IArrInstanceRepository>();

        var saved = await new SaveArrInstanceHandler(repo).HandleAsync(
            new SaveArrInstanceRequest(null, " Sonarr ", ArrKind.Sonarr, "http://sonarr:8989/", "key", true),
            TestContext.Current.CancellationToken);

        saved.ShouldNotBeNull();
        await repo.Received(1).AddAsync(
            Arg.Is<ArrInstance>(i => i != null
                && i.Name == "Sonarr"                       // trimmed
                && i.BaseUrl == "http://sonarr:8989"),      // trailing slash normalised away
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blank_key_on_update_keeps_the_existing_one()
    {
        // The UI never receives the real key, so it cannot echo it back — blank has to mean "unchanged",
        // otherwise editing a name would wipe the credential.
        var repo = Substitute.For<IArrInstanceRepository>();
        var existing = Existing();
        repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        await new SaveArrInstanceHandler(repo).HandleAsync(
            new SaveArrInstanceRequest(existing.Id, "Renamed", ArrKind.Sonarr, "http://sonarr:8989", "", true),
            TestContext.Current.CancellationToken);

        existing.Name.ShouldBe("Renamed");
        existing.ApiKey.ShouldBe("original-key");
    }

    [Fact]
    public async Task A_supplied_key_on_update_replaces_the_existing_one()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        var existing = Existing();
        repo.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        await new SaveArrInstanceHandler(repo).HandleAsync(
            new SaveArrInstanceRequest(existing.Id, "Sonarr", ArrKind.Sonarr, "http://sonarr:8989",
                " rotated-key ", true),
            TestContext.Current.CancellationToken);

        existing.ApiKey.ShouldBe("rotated-key"); // trimmed
    }

    [Fact]
    public async Task Updating_something_that_no_longer_exists_reports_rather_than_creating_it()
    {
        var repo = Substitute.For<IArrInstanceRepository>();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ArrInstance?)null);

        var saved = await new SaveArrInstanceHandler(repo).HandleAsync(
            new SaveArrInstanceRequest(Guid.NewGuid(), "Gone", ArrKind.Sonarr, "http://x:1", "key", true),
            TestContext.Current.CancellationToken);

        saved.ShouldBeNull();
        await repo.DidNotReceive().AddAsync(Arg.Any<ArrInstance>(), Arg.Any<CancellationToken>());
    }
}

public class SaveArrInstanceRequestValidatorTests
{
    private static readonly SaveArrInstanceRequestValidator Validator = new();

    private static SaveArrInstanceRequest Request(
        Guid? id = null, string name = "Sonarr", string baseUrl = "http://sonarr:8989", string? key = "key") =>
        new(id, name, ArrKind.Sonarr, baseUrl, key, true);

    [Fact]
    public void Accepts_a_well_formed_request() =>
        Validator.Validate(Request()).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("sonarr:8989")]          // no scheme
    [InlineData("//sonarr:8989")]        // protocol-relative
    [InlineData("ftp://sonarr:8989")]    // wrong scheme
    [InlineData("not a url")]
    public void Rejects_a_base_url_that_is_not_absolute_http(string baseUrl) =>
        Validator.Validate(Request(baseUrl: baseUrl)).IsValid.ShouldBeFalse();

    [Fact]
    public void Accepts_a_reverse_proxy_subpath() =>
        Validator.Validate(Request(baseUrl: "https://media.example.com/sonarr")).IsValid.ShouldBeTrue();

    [Fact]
    public void Requires_a_key_when_creating() =>
        Validator.Validate(Request(key: "")).IsValid.ShouldBeFalse();

    [Fact]
    public void Allows_a_blank_key_when_updating() =>
        Validator.Validate(Request(id: Guid.NewGuid(), key: "")).IsValid.ShouldBeTrue();

    [Fact]
    public void Requires_a_name() =>
        Validator.Validate(Request(name: "")).IsValid.ShouldBeFalse();
}

public class SearchWaitSettingsTests
{
    [Fact]
    public async Task Round_trips_the_search_wait_preference()
    {
        var repo = Substitute.For<ISettingsRepository>();
        var settings = new AppSettings();
        repo.GetAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var saved = await new SaveSettingsHandler(repo).HandleAsync(
            new SaveSettingsRequest("/downloads", 2, 6, SearchWaitMode.WaitForComplete, 42),
            TestContext.Current.CancellationToken);

        settings.SearchWaitMode.ShouldBe(SearchWaitMode.WaitForComplete);
        settings.SearchWaitSeconds.ShouldBe(42);
        saved.SearchWaitMode.ShouldBe(SearchWaitMode.WaitForComplete);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Rejects_an_out_of_range_wait(int seconds) =>
        new SaveSettingsRequestValidator()
            .Validate(new SaveSettingsRequest("/downloads", 2, 6, SearchWaitMode.ReturnFast, seconds))
            .IsValid.ShouldBeFalse();
}
