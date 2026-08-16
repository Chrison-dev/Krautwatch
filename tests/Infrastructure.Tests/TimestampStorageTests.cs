using Krautwatch.Domain.Entities;
using Krautwatch.Domain.Enums;
using Krautwatch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Xunit;

namespace Krautwatch.Infrastructure.Tests;

/// <summary>
/// Timestamps are stored as native <c>timestamptz</c> rather than ISO-8601 text (#50). These cover the
/// two ways that change can go wrong: the migration failing on data that already exists, and Npgsql
/// rejecting the non-UTC offsets the broadcasters hand us.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TimestampStorageTests(PostgresFixture postgres)
{
    /// <summary>The migration immediately before the conversion — the state an existing install is in.</summary>
    private const string PreviousMigration = "AddEpisodeSubtitleUrl";

    [Fact]
    public async Task Migration_converts_existing_ISO_8601_text_rows_to_real_timestamps()
    {
        var options = postgres.CreateUnmigratedDatabase();
        await using var db = new AppDbContext(options);

        // The old schema, with the timestamp columns still text.
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

        // A row exactly as the removed converters would have written it: ToString("O"), UTC, and a
        // null in the nullable column — the shape the USING cast has to survive.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Proxies"
                ("Id", "Host", "Port", "Protocol", "Source",
                 "UpTime", "Speed", "ResponseTime", "Latency",
                 "CreatedAt", "UpdatedAt", "SourceLastChecked", "LastProbedAt")
            VALUES
                ('1.2.3.4:8080', '1.2.3.4', 8080, 'http', 'test',
                 99.5, 10, 120, 0.12,
                 '2026-08-09T04:05:06.7080900+00:00', '2026-08-09T04:05:06.7080900+00:00',
                 NULL, NULL);
            """,
            TestContext.Current.CancellationToken);

        // The conversion itself. Without the USING cast in the migration, Postgres refuses this
        // outright: "column cannot be cast automatically to type timestamp with time zone".
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var columnType = await ColumnTypeAsync(db, "Proxies", "CreatedAt");
        columnType.ShouldBe("timestamp with time zone");

        // And the instant survived the cast rather than being reinterpreted.
        var proxy = await db.Proxies.SingleAsync(TestContext.Current.CancellationToken);
        proxy.CreatedAt.ShouldBe(new DateTimeOffset(2026, 8, 9, 4, 5, 6, 708, TimeSpan.Zero)
            .AddTicks(900));
        proxy.LastProbedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_conversion_rolls_back_to_text_an_older_build_can_still_read()
    {
        var options = postgres.CreateUnmigratedDatabase();
        await using var db = new AppDbContext(options);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Proxies"
                ("Id", "Host", "Port", "Protocol", "Source",
                 "UpTime", "Speed", "ResponseTime", "Latency", "CreatedAt", "UpdatedAt")
            VALUES
                ('5.6.7.8:3128', '5.6.7.8', 3128, 'http', 'test',
                 50, 1, 500, 0.5, timestamptz '2026-08-09 04:05:06.708+00',
                 timestamptz '2026-08-09 04:05:06.708+00');
            """,
            TestContext.Current.CancellationToken);

        // Downgrading is what an operator reaches for when an upgrade goes wrong, so the Down has to
        // leave text the previous build's DateTimeOffset.Parse can actually read back.
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);

        (await ColumnTypeAsync(db, "Proxies", "CreatedAt")).ShouldBe("text");

        var text = await ScalarAsync(db, """select "CreatedAt" from "Proxies" """);
        DateTimeOffset.Parse(text).ShouldBe(
            new DateTimeOffset(2026, 8, 9, 4, 5, 6, 708, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_broadcast_date_with_a_German_offset_round_trips_as_the_same_instant()
    {
        await using var db = new AppDbContext(await postgres.CreateDatabaseAsync());

        // What DateTimeOffset.Parse gives us for an ARD/ZDF air date in summer: 20:15 local, +02:00.
        // Npgsql will not write a non-zero offset to timestamptz at all, so without the normalizing
        // convention this throws rather than storing anything.
        var berlinEvening = new DateTimeOffset(2026, 7, 1, 20, 15, 0, TimeSpan.FromHours(2));
        db.Add(Episode("ep-berlin", berlinEvening));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();

        var stored = await db.Episodes.SingleAsync(e => e.Id == "ep-berlin",
            TestContext.Current.CancellationToken);

        stored.BroadcastDate.ShouldBe(berlinEvening);                       // same instant…
        stored.BroadcastDate.Offset.ShouldBe(TimeSpan.Zero);                // …normalized to UTC
        stored.BroadcastDate.Hour.ShouldBe(18);
    }

    [Fact]
    public async Task Ordering_follows_the_instant_even_when_the_offsets_differ()
    {
        await using var db = new AppDbContext(await postgres.CreateDatabaseAsync());

        // 21:00+02:00 is 19:00Z — *earlier* than 20:00Z, but its ISO-8601 text sorts later. That is
        // precisely the silent mis-ordering the text columns were one careless offset away from, and
        // it decided both the release feed's recency and the download queue's FIFO claim.
        var earlierInstant = new DateTimeOffset(2026, 7, 1, 21, 0, 0, TimeSpan.FromHours(2));
        var laterInstant = new DateTimeOffset(2026, 7, 1, 20, 0, 0, TimeSpan.Zero);

        db.AddRange(Episode("ep-earlier", earlierInstant), Episode("ep-later", laterInstant));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();

        var newestFirst = await db.Episodes
            .OrderByDescending(e => e.BroadcastDate)
            .Select(e => e.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        newestFirst.ShouldBe(["ep-later", "ep-earlier"]);
    }

    private static Episode Episode(string id, DateTimeOffset broadcastDate) => new()
    {
        Id = id,
        Title = id,
        BroadcastDate = broadcastDate,
        Duration = TimeSpan.FromMinutes(45),
        ContentType = ContentType.Episode,
        ShowId = $"show-{id}",
        Show = new Show
        {
            Id = $"show-{id}",
            Title = $"Show {id}",
            ChannelId = $"channel-{id}",
            Channel = new Channel { Id = $"channel-{id}", Name = "ARD", ProviderKey = "ard" },
        },
    };

    private static async Task<string> ScalarAsync(AppDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> ColumnTypeAsync(AppDbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            select data_type from information_schema.columns
            where table_name = @table and column_name = @column
            """;

        var tableParam = command.CreateParameter();
        tableParam.ParameterName = "table";
        tableParam.Value = table;
        command.Parameters.Add(tableParam);

        var columnParam = command.CreateParameter();
        columnParam.ParameterName = "column";
        columnParam.Value = column;
        command.Parameters.Add(columnParam);

        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
