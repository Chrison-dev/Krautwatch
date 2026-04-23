using MediathekNext.Domain.Entities;
using MediathekNext.Domain.Enums;
using Microsoft.EntityFrameworkCore;
// using TickerQ.Utilities.EntityFramework.Configurations; // TODO: Add proper TickerQ reference

namespace MediathekNext.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Show> Shows => Set<Show>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeStream> EpisodeStreams => Set<EpisodeStream>();
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();

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
        // AppSettings — singleton row pattern (Id always = 1)
        // --------------------------------------------------------
        modelBuilder.Entity<AppSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DownloadDirectory).IsRequired().HasMaxLength(500);
            e.HasData(new AppSettings
            {
                Id = 1,
                DownloadDirectory = "/downloads",
                MaxConcurrentDownloads = 2,
                CatalogRefreshIntervalHours = 6,
                CatalogProviderKey = "mediathekview"
            });
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
