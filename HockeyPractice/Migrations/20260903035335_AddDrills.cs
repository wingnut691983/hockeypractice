using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class AddDrills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Drills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    DiagramFileName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    DiagramBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    VideoUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CopiedFromDrillId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drills_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DrillTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DrillId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DrillTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrillTags_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanDrills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PracticePlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    DrillId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanDrills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanDrills_Drills_DrillId",
                        column: x => x.DrillId,
                        principalTable: "Drills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlanDrills_Plans_PracticePlanId",
                        column: x => x.PracticePlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Drills_TeamId_IsArchived",
                table: "Drills",
                columns: new[] { "TeamId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_DrillTags_DrillId_NormalizedName",
                table: "DrillTags",
                columns: new[] { "DrillId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DrillTags_NormalizedName",
                table: "DrillTags",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_PlanDrills_DrillId",
                table: "PlanDrills",
                column: "DrillId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanDrills_PracticePlanId_SortOrder",
                table: "PlanDrills",
                columns: new[] { "PracticePlanId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrillTags");

            migrationBuilder.DropTable(
                name: "PlanDrills");

            migrationBuilder.DropTable(
                name: "Drills");
        }
    }
}
