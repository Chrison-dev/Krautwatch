using MediathekNext.Application.Catalog;
using Shouldly;
using Xunit;

namespace MediathekNext.Application.Tests;

public class SearchCatalogQueryValidatorTests
{
    private readonly SearchCatalogQueryValidator _sut = new();

    [Fact]
    public async Task Validate_ValidQuery_PassesValidation()
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery("tagesschau"));
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Validate_EmptyQuery_FailsValidation(string query)
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery(query));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "Query");
    }

    [Fact]
    public async Task Validate_SingleCharQuery_FailsValidation()
    {
        var result = await _sut.ValidateAsync(new SearchCatalogQuery("a"));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("2 characters"));
    }

    [Fact]
    public async Task Validate_QueryExceeding200Chars_FailsValidation()
    {
        var longQuery = new string('a', 201);
        var result = await _sut.ValidateAsync(new SearchCatalogQuery(longQuery));
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.ErrorMessage.Contains("200 characters"));
    }
}
