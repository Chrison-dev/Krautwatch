# DR-001 — Catalog Provider Abstraction

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-03-08 |
| **Deciders** | Architect |

## Context

The application needs to source its catalog of shows and episodes from German public TV channels. Multiple data sources exist:
- **MediathekView filmliste** — community-maintained, available now, no API key required
- **ZDF API** — official, developer portal access requested but pending
- **ARD API** — undocumented but stable JSON API, to be investigated

Binding the application to any single source would make future migration expensive and fragile.

## Decision

Implement a `ICatalogProvider` abstraction in the Domain layer. All catalog data access goes through this interface. Concrete implementations live exclusively in the Infrastructure layer.

```csharp
public interface ICatalogProvider
{
    string ProviderName { get; }
    Task<IReadOnlyList<Episode>> FetchCatalogAsync(CancellationToken ct);
    Task<Episode?> GetEpisodeDetailAsync(string episodeId, CancellationToken ct);
}
```

Ship MVP with `MediathekViewProvider`. Add `ZdfApiProvider` and `ArdApiProvider` as access is granted.

Providers are selected by configuration key — switching requires no code changes.

## Consequences

- ✅ Domain layer has zero knowledge of MediathekView or any specific API
- ✅ Swapping providers is a config change
- ✅ Multiple providers can be registered and used per channel
- ⚠️ MediathekView filmliste is ~150MB compressed — must be parsed and indexed incrementally
