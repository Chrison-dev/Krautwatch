using FluentValidation;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Settings;

// ══════════════════════════════════════════════════════════════
// DTOs
// ══════════════════════════════════════════════════════════════

/// <summary>
/// A configured instance, shaped for display. Carries a **masked** API key only.
/// </summary>
/// <remarks>
/// The full key is deliberately absent from every read model. It is a credential, and the UI has no reason
/// to hand it back — masking means the settings page cannot be used to harvest keys, only to set them. See
/// #60 for encrypting them at rest, which is a separate concern.
/// </remarks>
public record ArrInstanceResponse(
    Guid Id,
    string Name,
    ArrKind Kind,
    string BaseUrl,
    string ApiKeyMasked,
    bool Enabled,
    DateTimeOffset? LastTestedAt,
    bool? LastTestOk,
    string? LastTestMessage,
    /// <summary>True when the key is a reference (<c>env:</c>/<c>file:</c>) rather than a stored secret.</summary>
    bool ApiKeyIsReference = false,
    /// <summary>Why a reference does not currently resolve, or null when it is fine.</summary>
    string? ApiKeyProblem = null);

/// <summary>
/// Add or update an instance. A blank <paramref name="ApiKey"/> on an update means "leave it unchanged", so
/// editing a name does not require re-typing the credential.
/// </summary>
public record SaveArrInstanceRequest(
    Guid? Id,
    string Name,
    ArrKind Kind,
    string BaseUrl,
    string? ApiKey,
    bool Enabled);

// ══════════════════════════════════════════════════════════════
// Validator
// ══════════════════════════════════════════════════════════════

public class SaveArrInstanceRequestValidator : AbstractValidator<SaveArrInstanceRequest>
{
    public SaveArrInstanceRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name must not be empty.")
            .MaximumLength(100).WithMessage("Name must be 100 characters or fewer.");

        RuleFor(x => x.BaseUrl)
            .NotEmpty().WithMessage("Base URL must not be empty.")
            .MaximumLength(500).WithMessage("Base URL must be 500 characters or fewer.")
            .Must(BeAnAbsoluteHttpUrl)
            .WithMessage("Base URL must be absolute and start with http:// or https:// — for example "
                       + "http://sonarr:8989.");

        // Required on create, optional on update (blank = unchanged).
        RuleFor(x => x.ApiKey)
            .NotEmpty().When(x => x.Id is null)
            .WithMessage("API key is required.");

        RuleFor(x => x.ApiKey)
            .MaximumLength(200).WithMessage("API key must be 200 characters or fewer.");
    }

    private static bool BeAnAbsoluteHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
}

// ══════════════════════════════════════════════════════════════
// Handlers
// ══════════════════════════════════════════════════════════════

public class GetArrInstancesHandler(IArrInstanceRepository repository, ISecretResolver secrets)
{
    public async Task<IReadOnlyList<ArrInstanceResponse>> HandleAsync(CancellationToken ct = default) =>
        (await repository.GetAllAsync(ct))
        .Select(i => ArrInstanceMapper.ToResponse(i, secrets))
        .ToList();
}

public class SaveArrInstanceHandler(IArrInstanceRepository repository)
{
    /// <summary>Creates or updates an instance. Returns null when the id no longer exists.</summary>
    public async Task<ArrInstanceResponse?> HandleAsync(
        SaveArrInstanceRequest request,
        CancellationToken ct = default)
    {
        var baseUrl = request.BaseUrl.Trim().TrimEnd('/');

        if (request.Id is null)
        {
            var created = new ArrInstance
            {
                Name = request.Name.Trim(),
                Kind = request.Kind,
                BaseUrl = baseUrl,
                ApiKey = request.ApiKey!.Trim(),
                Enabled = request.Enabled,
            };
            await repository.AddAsync(created, ct);
            return ArrInstanceMapper.ToResponse(created);
        }

        var existing = await repository.GetByIdAsync(request.Id.Value, ct);
        if (existing is null)
            return null;

        existing.Name = request.Name.Trim();
        existing.Kind = request.Kind;
        existing.BaseUrl = baseUrl;
        existing.Enabled = request.Enabled;

        // Blank means unchanged — the UI never receives the real key back, so it cannot echo it.
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
            existing.ApiKey = request.ApiKey.Trim();

        await repository.UpdateAsync(existing, ct);
        return ArrInstanceMapper.ToResponse(existing);
    }
}

public class DeleteArrInstanceHandler(IArrInstanceRepository repository)
{
    public Task HandleAsync(Guid id, CancellationToken ct = default) => repository.DeleteAsync(id, ct);
}

internal static class ArrInstanceMapper
{
    /// <summary>
    /// Maps for display without a resolver — the key is masked, and no resolution state is reported.
    /// Used where the caller only just wrote the record and has nothing to probe.
    /// </summary>
    public static ArrInstanceResponse ToResponse(ArrInstance i) => new(
        Id: i.Id,
        Name: i.Name,
        Kind: i.Kind,
        BaseUrl: i.BaseUrl,
        ApiKeyMasked: Mask(i.ApiKey),
        Enabled: i.Enabled,
        LastTestedAt: i.LastTestedAt,
        LastTestOk: i.LastTestOk,
        LastTestMessage: i.LastTestMessage,
        ApiKeyIsReference: SecretReference.IsReference(i.ApiKey));

    /// <summary>
    /// Maps for display, additionally reporting whether a reference currently resolves — so the settings
    /// page can say "env:SONARR_API_KEY · not set" at a glance rather than only on a connection test.
    /// </summary>
    public static ArrInstanceResponse ToResponse(ArrInstance i, ISecretResolver secrets)
    {
        var response = ToResponse(i);
        if (!response.ApiKeyIsReference)
            return response;

        var resolved = secrets.Resolve(i.ApiKey);
        return response with
        {
            ApiKeyProblem = resolved.Origin == SecretOrigin.Unresolved ? resolved.Problem : null,
        };
    }

    /// <summary>
    /// Shows only the last four characters — enough to tell two keys apart when checking which one is
    /// configured, without disclosing anything usable.
    /// </summary>
    /// <remarks>
    /// A <b>reference is not a credential</b>, so it is shown verbatim: the operator needs to see which
    /// variable or file is wired, and masking it would hide exactly the thing they came to check.
    /// </remarks>
    internal static string Mask(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return string.Empty;

        if (SecretReference.IsReference(apiKey))
            return apiKey.Trim();

        // Too short to reveal any of it and still be a mask.
        return apiKey.Length <= 4 ? "••••" : "••••" + apiKey[^4..];
    }
}
