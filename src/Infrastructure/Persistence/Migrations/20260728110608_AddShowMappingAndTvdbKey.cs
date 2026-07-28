using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShowMappingAndTvdbKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TvdbApiKey",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShowMappings",
                columns: table => new
                {
                    TvdbId = table.Column<int>(type: "integer", nullable: false),
                    ShowId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Provenance = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShowMappings", x => new { x.TvdbId, x.ShowId });
                    table.ForeignKey(
                        name: "FK_ShowMappings_Shows_ShowId",
                        column: x => x.ShowId,
                        principalTable: "Shows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: 1,
                column: "TvdbApiKey",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_ShowMappings_ShowId",
                table: "ShowMappings",
                column: "ShowId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShowMappings_TvdbId",
                table: "ShowMappings",
                column: "TvdbId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShowMappings");

            migrationBuilder.DropColumn(
                name: "TvdbApiKey",
                table: "Settings");
        }
    }
}
