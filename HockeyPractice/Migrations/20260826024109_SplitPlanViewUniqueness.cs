using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class SplitPlanViewUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data predating this migration allowed the same player to accumulate more than one
            // PlanView row per plan (one per device/ViewerKey, back when ViewerKey was the sole
            // unique key). The new partial index below requires at most one row per
            // (PracticePlanId, PlayerId), so collapse those duplicates first — keep the earliest
            // row (lowest Id) per pair, which preserves each player's original first-viewed time.
            migrationBuilder.Sql(@"
                DELETE FROM PlanViews
                WHERE PlayerId IS NOT NULL
                  AND Id NOT IN (
                      SELECT MIN(Id) FROM PlanViews
                      WHERE PlayerId IS NOT NULL
                      GROUP BY PracticePlanId, PlayerId
                  );");

            migrationBuilder.DropIndex(
                name: "IX_PlanViews_PracticePlanId_ViewerKey",
                table: "PlanViews");

            migrationBuilder.CreateIndex(
                name: "IX_PlanViews_PracticePlanId_PlayerId",
                table: "PlanViews",
                columns: new[] { "PracticePlanId", "PlayerId" },
                unique: true,
                filter: "[PlayerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlanViews_PracticePlanId_ViewerKey",
                table: "PlanViews",
                columns: new[] { "PracticePlanId", "ViewerKey" },
                unique: true,
                filter: "[PlayerId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlanViews_PracticePlanId_PlayerId",
                table: "PlanViews");

            migrationBuilder.DropIndex(
                name: "IX_PlanViews_PracticePlanId_ViewerKey",
                table: "PlanViews");

            migrationBuilder.CreateIndex(
                name: "IX_PlanViews_PracticePlanId_ViewerKey",
                table: "PlanViews",
                columns: new[] { "PracticePlanId", "ViewerKey" },
                unique: true);
        }
    }
}
