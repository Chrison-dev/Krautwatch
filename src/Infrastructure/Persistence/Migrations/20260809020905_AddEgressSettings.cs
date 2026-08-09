using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEgressSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EgressProxyListEnabled",
                table: "Settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EgressProxyListMaxCandidates",
                table: "Settings",
                type: "integer",
                nullable: false,
                // 5, not 0: existing rows take the column default, and 0 candidates would mean Mode B
                // silently offered nothing if it were ever switched on.
                defaultValue: 5);

            migrationBuilder.AddColumn<string>(
                name: "EgressProxyUrl",
                table: "Settings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "EgressProxyListEnabled", "EgressProxyListMaxCandidates", "EgressProxyUrl" },
                values: new object[] { false, 5, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EgressProxyListEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "EgressProxyListMaxCandidates",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "EgressProxyUrl",
                table: "Settings");
        }
    }
}
