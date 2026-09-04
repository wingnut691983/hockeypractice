using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <summary>
    /// One copy per source drill, per team. This is what makes sharing safe to press twice, and
    /// what holds when two managers share the same drill at the same moment.
    /// </summary>
    public partial class UniqueDrillProvenance : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nothing in production violates this today (checked: zero groups), but a unique index
            // that fails to create takes the whole startup migration with it, and the app is built
            // to keep serving on a migration failure rather than crash — so the failure would be a
            // quiet one found much later. Older copies of the database, or one restored from a
            // backup taken elsewhere, cost nothing to defend against.
            //
            // Clears the duplicate's provenance rather than deleting the drill. These are real
            // drills a coach may have edited since; severing the "copied from" link loses only the
            // record of where it came from, and the worst that follows is one extra copy on a
            // future share. Deleting would lose the drill itself.
            migrationBuilder.Sql("""
                UPDATE Drills SET CopiedFromDrillId = NULL
                WHERE CopiedFromDrillId IS NOT NULL
                  AND Id NOT IN (
                    SELECT MIN(Id) FROM Drills
                    WHERE CopiedFromDrillId IS NOT NULL
                    GROUP BY TeamId, CopiedFromDrillId
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Drills_TeamId_CopiedFromDrillId",
                table: "Drills",
                columns: new[] { "TeamId", "CopiedFromDrillId" },
                unique: true,
                filter: "[CopiedFromDrillId] IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Drills_TeamId_CopiedFromDrillId",
                table: "Drills");
        }
    }
}
