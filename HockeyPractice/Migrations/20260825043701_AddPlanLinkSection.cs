using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanLinkSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "PlanLinks",
                type: "TEXT",
                maxLength: 140,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Section",
                table: "PlanLinks");
        }
    }
}
