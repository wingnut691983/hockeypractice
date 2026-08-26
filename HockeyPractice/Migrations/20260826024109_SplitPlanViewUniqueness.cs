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
