using System.Text.Json;
using System.Text.Json.Serialization;
using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Domain.Interfaces;

namespace Krautwatch.Application.Settings;

// ══════════════════════════════════════════════════════════════
// DTOs
// ══════════════════════════════════════════════════════════════

/// <summary>
/// One show↔TVDB mapping, shaped for the settings page.
/// </summary>
/// <param name="Contested">
/// True when another of our shows also claims this TVDB id. These are the rows worth an operator's
/// attention: an uncontested mapping is either obvious or already settled.
/// </param>
public record ShowMappingResponse(
    int TvdbId,
    string ShowId,
    string ShowTitle,
    string ChannelId,
    MappingProvenance Provenance,
    int PickCount,
    string? Evidence,
    DateTimeOffset? LastPickedAt,
    bool Contested)
{
    /// <summary>How many more grabs before this mapping decides itself. Null once it no longer matters.</summary>
    public int? PicksUntilSettled =>
        !Contested || Provenance == MappingProvenance.OperatorConfirmed
            ? null
            : Math.Max(0, ShowMapping.AutoSelectAfterPicks - PickCount);
}

/// <summary>The portable file format for exported/imported mappings.</summary>
/// <remarks>
/// Show titles and channels travel purely for human readability — the identity is
/// <see cref="TvdbId"/> plus <see cref="ShowId"/>, and our show ids are derived from the broadcaster's
/// own identifiers, so they are stable across installs.
/// </remarks>
public record ShowMappingExport(
    int TvdbId,
    string ShowId,
    string? ShowTitle,
    string? ChannelId,
    MappingProvenance Provenance,
    int PickCount);

public record ImportResult(int Applied, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// Serialisation for the portable mapping file.
/// </summary>
/// <remarks>
/// Enums are written as names, not numbers. The file is meant to be shared, hand-edited and re-imported,
/// and a bare <c>1</c> is both unreadable and silently wrong if the enum is ever reordered. Reading is
/// case-insensitive so a hand-edited file is forgiving.
/// </remarks>
public static class ShowMappingFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

// ══════════════════════════════════════════════════════════════
// Handlers
// ══════════════════════════════════════════════════════════════

public class GetShowMappingsHandler(IShowMappingRepository mappings)
{
    /// <remarks>Contested groups first — those are the ones an operator can usefully act on.</remarks>
    public async Task<IReadOnlyList<ShowMappingResponse>> HandleAsync(CancellationToken ct = default)
    {
        var all = await mappings.GetAllAsync(ct);
        var contested = all.GroupBy(m => m.TvdbId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        return all
            .Select(m => new ShowMappingResponse(
                m.TvdbId,
                m.ShowId,
                m.Show?.Title ?? m.ShowId,
                m.Show?.ChannelId ?? string.Empty,
                m.Provenance,
                m.PickCount,
                m.Evidence,
                m.LastPickedAt,
                contested.Contains(m.TvdbId)))
            .OrderByDescending(m => m.Contested)
            .ThenBy(m => m.ShowTitle, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(m => m.PickCount)
            .ToList();
    }
}

/// <summary>
/// Pins a mapping as the operator's answer for a TVDB id.
/// </summary>
/// <remarks>
/// Competing mappings for the same id are deleted rather than left pinned-behind. An override exists
/// because the automatic answer was wrong, so leaving the rejected candidates in place would keep showing
/// them as open questions and let their pick counts keep climbing.
/// </remarks>
public class ConfirmShowMappingHandler(IShowMappingRepository mappings)
{
    public async Task HandleAsync(int tvdbId, string showId, CancellationToken ct = default)
    {
        foreach (var competing in await mappings.GetByTvdbIdAsync(tvdbId, ct))
        {
            if (!string.Equals(competing.ShowId, showId, StringComparison.Ordinal))
                await mappings.DeleteAsync(competing.TvdbId, competing.ShowId, ct);
        }

        await mappings.UpsertAsync(
            new ShowMapping
            {
                TvdbId = tvdbId,
                ShowId = showId,
                Provenance = MappingProvenance.OperatorConfirmed,
                Evidence = "confirmed by the operator",
            },
            ct);
    }
}

public class DeleteShowMappingHandler(IShowMappingRepository mappings)
{
    public Task HandleAsync(int tvdbId, string showId, CancellationToken ct = default) =>
        mappings.DeleteAsync(tvdbId, showId, ct);
}

public class ExportShowMappingsHandler(IShowMappingRepository mappings)
{
    public async Task<IReadOnlyList<ShowMappingExport>> HandleAsync(CancellationToken ct = default) =>
        (await mappings.GetAllAsync(ct))
            .Select(m => new ShowMappingExport(
                m.TvdbId, m.ShowId, m.Show?.Title, m.Show?.ChannelId, m.Provenance, m.PickCount))
            .OrderBy(m => m.TvdbId)
            .ThenBy(m => m.ShowId, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Applies mappings from an export file.
/// </summary>
/// <remarks>
/// An operator override already present is never overwritten by an import: the local decision was made
/// against this catalog by this operator, and a file from elsewhere is weaker evidence than that.
/// Everything else is applied with <see cref="MappingProvenance.Imported"/> — someone vouched for it, but
/// not here.
/// </remarks>
public class ImportShowMappingsHandler(
    IShowMappingRepository mappings,
    IEpisodeRepository episodes)
{
    public async Task<ImportResult> HandleAsync(
        IReadOnlyList<ShowMappingExport> incoming,
        CancellationToken ct = default)
    {
        var known = (await episodes.GetShowsAsync(ct: ct))
            .Select(row => row.Show.Id)
            .ToHashSet(StringComparer.Ordinal);

        var applied = 0;
        var skipped = 0;
        var notes = new List<string>();

        foreach (var entry in incoming)
        {
            if (entry.TvdbId <= 0 || string.IsNullOrWhiteSpace(entry.ShowId))
            {
                skipped++;
                continue;
            }

            // A mapping to a show we have never crawled would be unreachable — nothing could ever match it,
            // and it would sit in the UI looking like a configured mapping that silently does nothing.
            if (!known.Contains(entry.ShowId))
            {
                skipped++;
                notes.Add($"{entry.ShowTitle ?? entry.ShowId}: no such show in this catalog");
                continue;
            }

            var existing = await mappings.GetByShowIdAsync(entry.ShowId, ct);
            if (existing is { IsPinned: true })
            {
                skipped++;
                notes.Add($"{entry.ShowTitle ?? entry.ShowId}: kept your confirmed mapping");
                continue;
            }

            await mappings.UpsertAsync(
                new ShowMapping
                {
                    TvdbId = entry.TvdbId,
                    ShowId = entry.ShowId,
                    Provenance = MappingProvenance.Imported,
                    Evidence = "imported",
                    PickCount = entry.PickCount,
                },
                ct);
            applied++;
        }

        return new ImportResult(applied, skipped, notes);
    }
}
