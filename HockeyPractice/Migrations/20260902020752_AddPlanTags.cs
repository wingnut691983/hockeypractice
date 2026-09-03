using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PracticePlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanTags_Plans_PracticePlanId",
                        column: x => x.PracticePlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanTags_NormalizedName",
                table: "PlanTags",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_PlanTags_PracticePlanId_NormalizedName",
                table: "PlanTags",
                columns: new[] { "PracticePlanId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanTags");
        }
    }
}
