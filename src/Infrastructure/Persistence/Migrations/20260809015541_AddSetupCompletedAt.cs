using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SetupCompletedAt",
                table: "Settings",
                type: "timestamp with time zone",
                nullable: true);

            // An instance that already has an administrator was set up before this wizard existed, so
            // mark it done. Without this every upgrading operator is bounced into first-run setup on a
            // working install — and "admin exists, not yet stamped" is exactly the state the wizard
            // treats as resume-me, so it cannot be told apart at runtime. It has to be settled here.
            //
            // CURRENT_TIMESTAMP rather than NOW(): the provider is swappable (postgres default, mssql
            // supported), and this is the spelling both accept.
            migrationBuilder.Sql(
                """
                UPDATE "Settings"
                SET "SetupCompletedAt" = CURRENT_TIMESTAMP
                WHERE "SetupCompletedAt" IS NULL
                  AND EXISTS (SELECT 1 FROM "AdminAccounts");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SetupCompletedAt",
                table: "Settings");
        }
    }
}
