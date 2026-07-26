using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SeriesType",
                table: "Shows",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Existing rows are dated Mediathek content — default to the Daily regime, not "".
                defaultValue: "Daily");

            migrationBuilder.AddColumn<int>(
                name: "TvdbId",
                table: "Shows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AbsoluteEpisodeNumber",
                table: "Episodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "Episodes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "Episodes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_ShowId_SeasonNumber_EpisodeNumber",
                table: "Episodes",
                columns: new[] { "ShowId", "SeasonNumber", "EpisodeNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Episodes_ShowId_SeasonNumber_EpisodeNumber",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "SeriesType",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "TvdbId",
                table: "Shows");

            migrationBuilder.DropColumn(
                name: "AbsoluteEpisodeNumber",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "Episodes");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "Episodes");
        }
    }
}
