using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkEditedFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WasEditedByCoach",
                table: "PlanLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WasEditedByCoach",
                table: "PlanLinks");
        }
    }
}
