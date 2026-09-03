using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class DropSingleDrillDiagram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Safe ONLY because AddDrillDiagrams ran first and copied every one of these values
            // into the DrillDiagrams table. EF flags this as possible data loss and it is right
            // to: run this without that backfill and every diagram already uploaded is orphaned,
            // with its file left on the volume and nothing pointing at it. The two migrations are
            // deliberately separate so each shows up as its own line in the startup log — this
            // app catches migration failures and keeps serving, so a silent one is invisible.
            migrationBuilder.DropColumn(
                name: "DiagramBytes",
                table: "Drills");

            migrationBuilder.DropColumn(
                name: "DiagramFileName",
                table: "Drills");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DiagramBytes",
                table: "Drills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DiagramFileName",
                table: "Drills",
                type: "TEXT",
                maxLength: 120,
                nullable: true);
        }
    }
}
