namespace MediathekNext.Api.Endpoints;

/// <summary>
/// Shared HTTP 400 validation error response shape.
/// </summary>
public record ValidationProblem(Dictionary<string, string[]> Errors)
{
    public string Type   => "https://tools.ietf.org/html/rfc7231#section-6.5.1";
    public string Title  => "One or more validation errors occurred.";
    public int    Status => 400;
}

/// <summary>
/// Shared helper used by all endpoint classes.
/// </summary>
internal static class ValidationHelper
{
    public static ValidationProblem ToValidationProblem(
        FluentValidation.Results.ValidationResult result)
    {
        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());
        return new ValidationProblem(errors);
    }
}
