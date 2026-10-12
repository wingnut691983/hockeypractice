using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddReadPathIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Players_TeamId",
                table: "Players");

            migrationBuilder.CreateIndex(
                name: "IX_Players_TeamId_IsActive",
                table: "Players",
                columns: new[] { "TeamId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_TeamId_PdfKey",
                table: "Plans",
                columns: new[] { "TeamId", "PdfKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Drills_CopiedFromDrillId",
                table: "Drills",
                column: "CopiedFromDrillId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Players_TeamId_IsActive",
                table: "Players");

            migrationBuilder.DropIndex(
                name: "IX_Plans_TeamId_PdfKey",
                table: "Plans");

            migrationBuilder.DropIndex(
                name: "IX_Drills_CopiedFromDrillId",
                table: "Drills");

            migrationBuilder.CreateIndex(
                name: "IX_Players_TeamId",
                table: "Players",
                column: "TeamId");
        }
    }
}
