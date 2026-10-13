using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddDrillDiagramSketch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDemo",
                table: "Teams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // The demo team was chosen by a hard-coded slug until now. Carry that choice over
            // once, here; from now on the flag goes with the row and the slug means nothing.
            migrationBuilder.Sql("UPDATE \"Teams\" SET \"IsDemo\" = 1 WHERE \"Slug\" = 'demoteam';");

            migrationBuilder.AddColumn<string>(
                name: "AltText",
                table: "DrillDiagrams",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DrillDiagramSketches",
                columns: table => new
                {
                    DrillDiagramId = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrillDiagramSketches", x => x.DrillDiagramId);
                    table.ForeignKey(
                        name: "FK_DrillDiagramSketches_DrillDiagrams_DrillDiagramId",
                        column: x => x.DrillDiagramId,
                        principalTable: "DrillDiagrams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrillDiagramSketches");

            migrationBuilder.DropColumn(
                name: "IsDemo",
                table: "Teams");

            migrationBuilder.DropColumn(
                name: "AltText",
                table: "DrillDiagrams");
        }
    }
}
