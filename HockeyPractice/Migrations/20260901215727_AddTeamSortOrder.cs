using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Backfill so existing teams keep their current (alphabetical) order instead of all
            // landing on 0 — otherwise the first reorder click would silently jumble every team
            // that hadn't been touched yet.
            migrationBuilder.Sql(@"
                UPDATE Teams SET SortOrder = (
                    SELECT COUNT(*) FROM Teams t2
                    WHERE t2.Name < Teams.Name
                       OR (t2.Name = Teams.Name AND t2.Id < Teams.Id)
                );");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Teams");
        }
    }
}
