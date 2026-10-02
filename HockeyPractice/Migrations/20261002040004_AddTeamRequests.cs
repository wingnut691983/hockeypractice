using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TeamRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FirstName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    LastName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Association = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    TeamName = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HandledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TeamRequests_Status_CreatedUtc",
                table: "TeamRequests",
                columns: new[] { "Status", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamRequests");
        }
    }
}
