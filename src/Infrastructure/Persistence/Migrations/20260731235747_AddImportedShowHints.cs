using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedShowHints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportedShowHints",
                columns: table => new
                {
                    TvdbId = table.Column<int>(type: "integer", nullable: false),
                    NormalizedTopic = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Topic = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedShowHints", x => new { x.TvdbId, x.NormalizedTopic });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedShowHints_Source",
                table: "ImportedShowHints",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_ImportedShowHints_TvdbId",
                table: "ImportedShowHints",
                column: "TvdbId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportedShowHints");
        }
    }
}
