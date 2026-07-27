using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchWaitPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchWaitMode",
                table: "Settings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // "ReturnFast", not "": the column is parsed as an enum, so an empty string would throw on
                // read for any row inserted without an explicit value.
                defaultValue: "ReturnFast");

            migrationBuilder.AddColumn<int>(
                name: "SearchWaitSeconds",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 8); // matches the entity default; 0 would be an invalid wait

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "SearchWaitMode", "SearchWaitSeconds" },
                values: new object[] { "ReturnFast", 8 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchWaitMode",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SearchWaitSeconds",
                table: "Settings");
        }
    }
}
