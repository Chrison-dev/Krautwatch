using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediathekNext.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DownloadDirectory = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MaxConcurrentDownloads = table.Column<int>(type: "INTEGER", nullable: false),
                    CatalogRefreshIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    CatalogProviderKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Shows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ChannelId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Shows_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    ShowId = table.Column<string>(type: "TEXT", nullable: false),
                    BroadcastDate = table.Column<string>(type: "TEXT", nullable: false),
                    Duration = table.Column<double>(type: "REAL", nullable: false),
                    AvailableUntil = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EpisodeStreams",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<string>(type: "TEXT", nullable: false),
                    Quality = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpisodeStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpisodeStreams_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DownloadJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpisodeId = table.Column<string>(type: "TEXT", nullable: false),
                    StreamUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Quality = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    // Phase metadata — populated as the TickerQ chain progresses
                    StreamType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    ContentLengthBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    TempPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    // Progress & results
                    ProgressPercent = table.Column<double>(type: "REAL", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OutputPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    // Timestamps
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadJobs_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // TickerQ tables
            migrationBuilder.CreateTable(
                name: "TimeTickerEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Function = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ExecutionTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Request = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Retries = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryIntervals = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LockHolder = table.Column<string>(type: "TEXT", nullable: true),
                    LockExpiry = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeTickerEntities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CronTickerEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Function = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Expression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LockHolder = table.Column<string>(type: "TEXT", nullable: true),
                    LockExpiry = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronTickerEntities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CronTickerOccurrenceEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CronTickerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CronTickerOccurrenceEntities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CronTickerOccurrenceEntities_CronTickerEntities_CronTickerId",
                        column: x => x.CronTickerId,
                        principalTable: "CronTickerEntities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Indexes — domain tables
            migrationBuilder.CreateIndex(name: "IX_Shows_ChannelId",         table: "Shows",         column: "ChannelId");
            migrationBuilder.CreateIndex(name: "IX_Episodes_ShowId",         table: "Episodes",       column: "ShowId");
            migrationBuilder.CreateIndex(name: "IX_Episodes_BroadcastDate",  table: "Episodes",       column: "BroadcastDate");
            migrationBuilder.CreateIndex(name: "IX_Episodes_ContentType",    table: "Episodes",       column: "ContentType");
            migrationBuilder.CreateIndex(name: "IX_EpisodeStreams_EpisodeId", table: "EpisodeStreams", column: "EpisodeId");
            migrationBuilder.CreateIndex(name: "IX_DownloadJobs_Status",     table: "DownloadJobs",   column: "Status");
            migrationBuilder.CreateIndex(name: "IX_DownloadJobs_CreatedAt",  table: "DownloadJobs",   column: "CreatedAt");

            // Indexes — TickerQ tables
            migrationBuilder.CreateIndex(name: "IX_TimeTickerEntities_Function",     table: "TimeTickerEntities",         column: "Function");
            migrationBuilder.CreateIndex(name: "IX_TimeTickerEntities_Status",       table: "TimeTickerEntities",         column: "Status");
            migrationBuilder.CreateIndex(name: "IX_CronTickerEntities_Function",     table: "CronTickerEntities",         column: "Function", unique: true);
            migrationBuilder.CreateIndex(name: "IX_CronTickerOccurrenceEntities_CronTickerId", table: "CronTickerOccurrenceEntities", column: "CronTickerId");

            // Seed default settings
            migrationBuilder.InsertData(
                table: "Settings",
                columns: ["Id", "DownloadDirectory", "MaxConcurrentDownloads", "CatalogRefreshIntervalHours", "CatalogProviderKey"],
                values: new object[] { 1, "/downloads", 2, 6, "mediathekview" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DownloadJobs");
            migrationBuilder.DropTable(name: "EpisodeStreams");
            migrationBuilder.DropTable(name: "Episodes");
            migrationBuilder.DropTable(name: "Shows");
            migrationBuilder.DropTable(name: "Channels");
            migrationBuilder.DropTable(name: "Settings");
            migrationBuilder.DropTable(name: "CronTickerOccurrenceEntities");
            migrationBuilder.DropTable(name: "CronTickerEntities");
            migrationBuilder.DropTable(name: "TimeTickerEntities");
        }
    }
}
