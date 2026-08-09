using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Microsoft.EntityFrameworkCore;
// using TickerQ.Utilities.EntityFramework.Configurations; // TODO: Add proper TickerQ reference

namespace Krautwatch.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeStream> EpisodeStreams => Set<EpisodeStream>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<Proxy> Proxies => Set<Proxy>();
    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<ArrInstance> ArrInstances => Set<ArrInstance>();
    public DbSet<ResolvedQuery> ResolvedQueries => Set<ResolvedQuery>();
    public DbSet<ShowMapping> ShowMappings => Set<ShowMapping>();
    public DbSet<ImportedShowHint> ImportedShowHints => Set<ImportedShowHint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --------------------------------------------------------
        // Channel
        // --------------------------------------------------------
        modelBuilder.Entity<Channel>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.ProviderKey).IsRequired().HasMaxLength(50);
        });

        // --------------------------------------------------------
        // Show
        // --------------------------------------------------------
        modelBuilder.Entity<Show>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).IsRequired().HasMaxLength(500);

            e.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasMany(x => x.Episodes)
                .WithOne(x => x.Show)
                .HasForeignKey(x => x.ShowId)
                .OnDelete(DeleteBehavior.Cascade);

            // Sonarr model — matching regime (stored as text like the other enums)
            e.Property(x => x.SeriesType)
                .HasConversion(v => v.ToString(), v => Enum.Parse<SeriesType>(v))
                .HasMaxLength(20);
        });

        // --------------------------------------------------------
        // Episode
        // --------------------------------------------------------
        modelBuilder.Entity<Episode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).IsRequired().HasMaxLength(500);
            e.Property(x => x.Description).HasMaxLength(5000);

            // Store as ISO 8601 TEXT to avoid EF Core 10 DateTimeOffset/SQLite REAL ambiguity
            // See: DR-007 notes on EF Core 10 breaking changes
            e.Property(x => x.BroadcastDate)
                .HasConversion(
                    v => v.ToString("O"),
                    v => DateTimeOffset.Parse(v));

            e.Property(x => x.AvailableUntil)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString("O") : null,
                    v => v != null ? DateTimeOffset.Parse(v) : (DateTimeOffset?)null);

            e.Property(x => x.Duration)
                .HasConversion(
                    v => v.TotalSeconds,
                    v => TimeSpan.FromSeconds(v));

            e.HasOne(x => x.Show)
                .WithMany(x => x.Episodes)
                .HasForeignKey(x => x.ShowId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.ContentType)
                .HasConversion(v => v.ToString(), v => Enum.Parse<ContentType>(v));

            e.HasIndex(x => x.ShowId);
            e.HasIndex(x => x.BroadcastDate);
            e.HasIndex(x => x.ContentType);
            // Newznab season/episode lookups (Standard series)
            e.HasIndex(x => new { x.ShowId, x.SeasonNumber, x.EpisodeNumber });
        });

        // --------------------------------------------------------
        // EpisodeStream
        // --------------------------------------------------------
        modelBuilder.Entity<EpisodeStream>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Url).IsRequired().HasMaxLength(2000);
            e.Property(x => x.Format).IsRequired().HasMaxLength(10);
            e.Property(x => x.Quality)
                .HasConversion(v => v.ToString(), v => Enum.Parse<VideoQuality>(v));

            e.HasOne<Episode>()
                .WithMany(x => x.Streams)
                .HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.EpisodeId);
        });

        // --------------------------------------------------------
        // DownloadJob
        // --------------------------------------------------------
        modelBuilder.Entity<DownloadJob>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.StreamUrl).IsRequired().HasMaxLength(2000);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.Property(x => x.OutputPath).HasMaxLength(1000);

            e.Property(x => x.Quality)
                .HasConversion(v => v.ToString(), v => Enum.Parse<VideoQuality>(v));

            e.Property(x => x.Status)
                .HasConversion(v => v.ToString(), v => Enum.Parse<DownloadStatus>(v));

            e.Property(x => x.CreatedAt)
                .HasConversion(
                    v => v.ToString("O"),
                    v => DateTimeOffset.Parse(v));

            e.Property(x => x.StartedAt)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString("O") : null,
                    v => v != null ? DateTimeOffset.Parse(v) : (DateTimeOffset?)null);

            e.Property(x => x.CompletedAt)
                .HasConversion(
                    v => v.HasValue ? v.Value.ToString("O") : null,
                    v => v != null ? DateTimeOffset.Parse(v) : (DateTimeOffset?)null);

            e.HasOne(x => x.Episode)
                .WithMany()
                .HasForeignKey(x => x.EpisodeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);

            // Phase-tracking columns
            e.Property(x => x.StreamType).HasMaxLength(10);
            e.Property(x => x.TempPath).HasMaxLength(1000);
        });

        // --------------------------------------------------------
        // Proxy — cached public egress-proxy candidates (Mode B, #45)
        // --------------------------------------------------------
        modelBuilder.Entity<Proxy>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever().HasMaxLength(100); // "host:port"
            e.Property(x => x.Host).IsRequired().HasMaxLength(100);
            e.Property(x => x.Protocol).IsRequired().HasMaxLength(10);
            e.Property(x => x.Source).IsRequired().HasMaxLength(50);
            e.Property(x => x.Country).HasMaxLength(10);
            e.Property(x => x.AnonymityLevel).HasMaxLength(20);
            e.Property(x => x.VerifiedEgressCountry).HasMaxLength(10);

            foreach (var ts in new[] { nameof(Proxy.SourceLastChecked), nameof(Proxy.LastProbedAt) })
                e.Property<DateTimeOffset?>(ts).HasConversion(
                    v => v.HasValue ? v.Value.ToString("O") : null,
                    v => v != null ? DateTimeOffset.Parse(v) : (DateTimeOffset?)null);

            foreach (var ts in new[] { nameof(Proxy.CreatedAt), nameof(Proxy.UpdatedAt) })
                e.Property<DateTimeOffset>(ts).HasConversion(v => v.ToString("O"), v => DateTimeOffset.Parse(v));

            e.Ignore(x => x.Url); // computed from Protocol/Host/Port
            e.HasIndex(x => x.Country);
        });

        // --------------------------------------------------------
        // AppSettings — singleton row pattern (Id always = 1)
        // --------------------------------------------------------
        modelBuilder.Entity<AppSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DownloadDirectory).IsRequired().HasMaxLength(500);

            // Text like the other enums, so the column stays readable in the database.
            e.Property(x => x.SearchWaitMode)
                .HasConversion(v => v.ToString(), v => Enum.Parse<SearchWaitMode>(v))
                .HasMaxLength(20);
            e.HasData(new AppSettings
            {
                Id = 1,
                DownloadDirectory = "/downloads",
                MaxConcurrentDownloads = 2,
                CatalogRefreshIntervalHours = 6,
            });
        });

        // --------------------------------------------------------
        // AdminAccount — the single local admin (Auth:Provider = local).
        // Deliberately NOT seeded: no default credentials ever ship. Absence of this row is what
        // triggers first-run setup, and the fixed singleton key means two concurrent setup posts
        // cannot both insert.
        // --------------------------------------------------------
        modelBuilder.Entity<AdminAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Username).IsRequired().HasMaxLength(100);
            e.Property(x => x.PasswordHash).IsRequired().HasMaxLength(500);
        });

        // --------------------------------------------------------
        // ArrInstance — configured Sonarr/Radarr instances we call OUTBOUND (#4).
        // BaseUrl is uniquely indexed: #5 bootstraps instances from env vars and matches them by base
        // URL, so duplicates have to be impossible at the schema level rather than by convention.
        // --------------------------------------------------------
        modelBuilder.Entity<ArrInstance>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.BaseUrl).IsRequired().HasMaxLength(500);
            e.Property(x => x.ApiKey).IsRequired().HasMaxLength(200);
            e.Property(x => x.LastTestMessage).HasMaxLength(500);

            // Stored as text like the other enums, so the column stays readable in the database.
            e.Property(x => x.Kind)
                .HasConversion(v => v.ToString(), v => Enum.Parse<ArrKind>(v))
                .HasMaxLength(20);

            e.HasIndex(x => x.BaseUrl).IsUnique();
        });

        // --------------------------------------------------------
        // ShowMapping — our show ↔ TheTVDB series id (PR 3a).
        // Composite key, because the relationship is many-of-ours to one TVDB id: three of our shows are
        // really tvdb 255986 (extra 3 on ARD plus two ZDF variants). Deliberately its own table and NOT a
        // column on Show — the crawl upsert marks existing rows Modified and rewrites every column, so a
        // mapping stored on Show would be wiped by the next crawl.
        // --------------------------------------------------------
        modelBuilder.Entity<ShowMapping>(e =>
        {
            e.HasKey(x => new { x.TvdbId, x.ShowId });
            e.Property(x => x.ShowId).HasMaxLength(500);
            e.Property(x => x.Evidence).HasMaxLength(500);

            e.Property(x => x.Provenance)
                .HasConversion(v => v.ToString(), v => Enum.Parse<MappingProvenance>(v))
                .HasMaxLength(20);

            e.HasOne(x => x.Show)
                .WithMany()
                .HasForeignKey(x => x.ShowId)
                .OnDelete(DeleteBehavior.Cascade);

            // The hot path is "Sonarr asked about this id — which of our shows is it?".
            e.HasIndex(x => x.TvdbId);

            // Counts votes from grabs; never null, so an increment needs no null handling.
            e.Property(x => x.PickCount).HasDefaultValue(0);

            // A show maps to at most one series: two ids for one show would make the release we emit
            // ambiguous, and there would be no way to pick between them at query time.
            e.HasIndex(x => x.ShowId).IsUnique();
        });

        // --------------------------------------------------------
        // ImportedShowHint — curated topic↔tvdbId pairs from a third-party set (RundfunkArr).
        // No foreign key to Shows on purpose: most of an imported set names shows this instance has never
        // crawled, and a hint is useful precisely because it can arrive before the show does.
        // --------------------------------------------------------
        modelBuilder.Entity<ImportedShowHint>(e =>
        {
            e.HasKey(x => new { x.TvdbId, x.NormalizedTopic });
            e.Property(x => x.NormalizedTopic).HasMaxLength(500);
            e.Property(x => x.Topic).IsRequired().HasMaxLength(500);
            e.Property(x => x.Source).IsRequired().HasMaxLength(50);

            // Read path is "any curated names for this id?"; the source index backs re-import and clearing.
            e.HasIndex(x => x.TvdbId);
            e.HasIndex(x => x.Source);
        });

        // --------------------------------------------------------
        // ResolvedQuery — the on-demand search resolution cache (#58).
        // Keyed on the normalised query itself; no surrogate id, because the query IS the identity.
        // --------------------------------------------------------
        modelBuilder.Entity<ResolvedQuery>(e =>
        {
            e.HasKey(x => x.Query);
            e.Property(x => x.Query).HasMaxLength(300);
            e.Property(x => x.ProvidersTried).HasMaxLength(200);
        });

        // --------------------------------------------------------
        // TickerQ — job scheduler tables (TimeTickers, CronTickers, etc.)
        // UseModelCustomizerForMigrations() is the alternative but we use
        // explicit config here for full visibility at design-time.
        // --------------------------------------------------------
        // TickerQ entity configurations (commented out until TickerQ is properly integrated)
        /*
        modelBuilder.ApplyConfiguration(new TimeTickerConfigurations());
        modelBuilder.ApplyConfiguration(new CronTickerConfigurations());
        modelBuilder.ApplyConfiguration(new CronTickerOccurrenceConfigurations());
        */
    }
}
