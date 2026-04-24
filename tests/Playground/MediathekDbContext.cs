using Mediathek.Models;
using Microsoft.EntityFrameworkCore;

namespace Mediathek.Data;

public class MediathekDbContext(DbContextOptions<MediathekDbContext> options)
    : DbContext(options)
{
    public DbSet<Broadcaster>     Broadcasters => Set<Broadcaster>();
    public DbSet<Show>            Shows        => Set<Show>();
    public DbSet<Episode>         Episodes     => Set<Episode>();
    public DbSet<EpisodeStream>   Streams      => Set<EpisodeStream>();
    public DbSet<EpisodeSubtitle> Subtitles    => Set<EpisodeSubtitle>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ── Broadcaster ──────────────────────────────────────────────────────
        b.Entity<Broadcaster>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
        });

        // ── Show ─────────────────────────────────────────────────────────────
        b.Entity<Show>(e =>
        {
            // A broadcaster can't have two shows with the same external ID
            e.HasIndex(x => new { x.BroadcasterId, x.ExternalId })
             .IsUnique()
             .HasFilter("[ExternalId] IS NOT NULL");

            e.HasIndex(x => x.Title);

            e.HasMany(s => s.Episodes)
             .WithOne(ep => ep.Show)
             .HasForeignKey(ep => ep.ShowId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Episode ──────────────────────────────────────────────────────────
        b.Entity<Episode>(e =>
        {
            // Prevents duplicate imports of the same broadcast
            e.HasIndex(x => new { x.ShowId, x.ExternalId })
             .IsUnique()
             .HasFilter("[ExternalId] IS NOT NULL");

            e.HasIndex(x => new { x.ShowId, x.AiredOn });

            e.HasMany(ep => ep.Streams)
             .WithOne(s => s.Episode)
             .HasForeignKey(s => s.EpisodeId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasMany(ep => ep.Subtitles)
             .WithOne(s => s.Episode)
             .HasForeignKey(s => s.EpisodeId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── EpisodeStream ────────────────────────────────────────────────────
        b.Entity<EpisodeStream>(e =>
        {
            e.HasIndex(x => new { x.EpisodeId, x.Quality, x.Language }).IsUnique();
        });

        // ── EpisodeSubtitle ──────────────────────────────────────────────────
        b.Entity<EpisodeSubtitle>(e =>
        {
            e.HasIndex(x => new { x.EpisodeId, x.Language }).IsUnique();
        });

        // ── Seed: Broadcasters ───────────────────────────────────────────────
        b.Entity<Broadcaster>().HasData(
            new Broadcaster { Id = 1,  Key = "ARD",          DisplayName = "Das Erste"     },
            new Broadcaster { Id = 2,  Key = "ZDF",          DisplayName = "ZDF"           },
            new Broadcaster { Id = 3,  Key = "ZDFneo",       DisplayName = "ZDFneo"        },
            new Broadcaster { Id = 4,  Key = "ZDFinfo",      DisplayName = "ZDFinfo"       },
            new Broadcaster { Id = 5,  Key = "ZDFtivi",      DisplayName = "ZDFtivi"       },
            new Broadcaster { Id = 6,  Key = "BR",           DisplayName = "BR"            },
            new Broadcaster { Id = 7,  Key = "HR",           DisplayName = "HR"            },
            new Broadcaster { Id = 8,  Key = "MDR",          DisplayName = "MDR"           },
            new Broadcaster { Id = 9,  Key = "NDR",          DisplayName = "NDR"           },
            new Broadcaster { Id = 10, Key = "RBB",          DisplayName = "RBB"           },
            new Broadcaster { Id = 11, Key = "SWR",          DisplayName = "SWR"           },
            new Broadcaster { Id = 12, Key = "WDR",          DisplayName = "WDR"           },
            new Broadcaster { Id = 13, Key = "SR",           DisplayName = "SR"            },
            new Broadcaster { Id = 14, Key = "ONE",          DisplayName = "ONE"           },
            new Broadcaster { Id = 15, Key = "ARDalpha",     DisplayName = "ARD alpha"     },
            new Broadcaster { Id = 16, Key = "tagesschau24", DisplayName = "tagesschau24"  },
            new Broadcaster { Id = 17, Key = "phoenix",      DisplayName = "phoenix"       },
            new Broadcaster { Id = 18, Key = "funk",         DisplayName = "funk"          },
            new Broadcaster { Id = 19, Key = "RB",           DisplayName = "Radio Bremen"  }
        );
    }
}
