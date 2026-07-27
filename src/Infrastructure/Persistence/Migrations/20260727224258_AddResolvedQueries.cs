using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krautwatch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResolvedQueries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResolvedQueries",
                columns: table => new
                {
                    Query = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    LastAttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    ProvidersTried = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolvedQueries", x => x.Query);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResolvedQueries");
        }
    }
}
