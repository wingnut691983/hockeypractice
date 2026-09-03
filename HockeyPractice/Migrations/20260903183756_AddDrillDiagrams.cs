using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddDrillDiagrams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DrillDiagrams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DrillId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrillDiagrams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillDiagrams_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DrillDiagrams_DrillId",
                table: "DrillDiagrams",
                column: "DrillId");

            // Carry every existing diagram across BEFORE the next migration drops the columns it
            // lives in. The files on disk don't move — a drill's directory already holds them and
            // the filename is all that's needed to find one again — so this is purely the row
            // that points at them. Without it, dropping the columns would orphan every diagram
            // already uploaded while leaving the files sitting on the volume.
            migrationBuilder.Sql(@"
                INSERT INTO DrillDiagrams (DrillId, FileName, Bytes)
                SELECT Id, DiagramFileName, DiagramBytes
                FROM Drills
                WHERE DiagramFileName IS NOT NULL AND DiagramFileName <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrillDiagrams");
        }
    }
}
