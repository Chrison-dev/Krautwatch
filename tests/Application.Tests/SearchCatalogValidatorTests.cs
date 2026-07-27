using Krautwatch.Application.Catalog;
using Shouldly;
using Xunit;

namespace Krautwatch.Application.Tests;

public class SearchCatalogQueryValidatorTests
{
    private readonly SearchCatalogQueryValidator _sut = new();

    [Fact]
    public async Task Validate_ValidQuery_PassesValidation()
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery("tagesschau"), TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_EmptyQuery_FailsValidation(string query)
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery(query), TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Query");
    }

    [Fact]
    public async Task Validate_SingleCharQuery_FailsValidation()
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery("a"), TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("2 characters"));
    }

    [Fact]
    public async Task Validate_QueryExceeding200Chars_FailsValidation()
    {
        var longQuery = new string('a', 201);
        var result = await _sut.ValidateAsync(new SearchCatalogQuery(longQuery), TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("200 characters"));
    }
}
