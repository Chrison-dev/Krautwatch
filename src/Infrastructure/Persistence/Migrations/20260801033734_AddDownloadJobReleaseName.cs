using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadJobReleaseName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleaseName",
                table: "DownloadJobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleaseName",
                table: "DownloadJobs");
        }
    }
}
