using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Converts the nine ISO-8601 text timestamp columns to native <c>timestamptz</c> (#50).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written rather than left as EF scaffolded it. The generated <c>AlterColumn</c> emits a bare
    /// <c>ALTER COLUMN … TYPE timestamptz</c>, which Postgres refuses for a text column:
    /// <c>column "BroadcastDate" cannot be cast automatically to type timestamp with time zone …
    /// You might need to specify "USING"</c>. The migration would fail on any database with the old
    /// schema — that is, every existing install.
    /// </para>
    /// <para>
    /// The <c>USING</c> cast is safe for the data actually present: values were written with
    /// <c>ToString("O")</c>, which Postgres parses natively, and every writer used UTC. A row holding
    /// anything else would fail the cast loudly here rather than sort wrongly forever, which is the
    /// right way round.
    /// </para>
    /// <para>
    /// The other ten <c>DateTimeOffset</c> columns in the model were already <c>timestamptz</c> — they
    /// never had converters. This migration ends that split.
    /// </para>
    /// </remarks>
    public partial class TimestampsAsNativeType : Migration
    {
        /// <summary>Table and column of every timestamp that was stored as text.</summary>
        private static readonly (string Table, string Column)[] Columns =
        [
            ("Episodes",     "BroadcastDate"),
            ("Episodes",     "AvailableUntil"),
            ("DownloadJobs", "CreatedAt"),
            ("DownloadJobs", "StartedAt"),
            ("DownloadJobs", "CompletedAt"),
            ("Proxies",      "CreatedAt"),
            ("Proxies",      "UpdatedAt"),
            ("Proxies",      "SourceLastChecked"),
            ("Proxies",      "LastProbedAt"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Columns)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}"
                    ALTER COLUMN "{column}" TYPE timestamp with time zone
                    USING NULLIF("{column}", '')::timestamp with time zone;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to the ISO-8601 shape DateTimeOffset.Parse expects. Microsecond precision rather
            // than the "O" format's 100-nanosecond ticks — that is all Postgres stores, so the extra
            // digits were always zeros.
            foreach (var (table, column) in Columns)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}"
                    ALTER COLUMN "{column}" TYPE text
                    USING to_char("{column}" AT TIME ZONE 'UTC',
                                  'YYYY-MM-DD"T"HH24:MI:SS.US"+00:00"');
                    """);
            }
        }
    }
}
