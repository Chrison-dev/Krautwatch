using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMappingPickCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastPickedAt",
                table: "ShowMappings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PickCount",
                table: "ShowMappings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPickedAt",
                table: "ShowMappings");

            migrationBuilder.DropColumn(
                name: "PickCount",
                table: "ShowMappings");
        }
    }
}
