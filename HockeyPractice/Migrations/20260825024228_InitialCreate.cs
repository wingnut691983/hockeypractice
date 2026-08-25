using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HockeyPractice.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LogoFileName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PrimaryColor = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    AccentColor = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    ViewCodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    CoachCodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    PracticeDateLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CoachNotes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plans_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    JerseyNumber = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Players_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PracticePlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanLinks_Plans_PracticePlanId",
                        column: x => x.PracticePlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlanViews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PracticePlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ViewerKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstViewedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanViews_Plans_PracticePlanId",
                        column: x => x.PracticePlanId,
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanViews_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Subscribers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TeamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlayerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ConfirmToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UnsubToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConfirmedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscribers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscribers_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Subscribers_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanLinks_PracticePlanId",
                table: "PlanLinks",
                column: "PracticePlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_TeamId_PracticeDateLocal",
                table: "Plans",
                columns: new[] { "TeamId", "PracticeDateLocal" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanViews_PlayerId",
                table: "PlanViews",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_PlanViews_PracticePlanId_ViewerKey",
                table: "PlanViews",
                columns: new[] { "PracticePlanId", "ViewerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Players_TeamId",
                table: "Players",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscribers_ConfirmToken",
                table: "Subscribers",
                column: "ConfirmToken");

            migrationBuilder.CreateIndex(
                name: "IX_Subscribers_PlayerId",
                table: "Subscribers",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscribers_TeamId_Email",
                table: "Subscribers",
                columns: new[] { "TeamId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscribers_UnsubToken",
                table: "Subscribers",
                column: "UnsubToken");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Slug",
                table: "Teams",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanLinks");

            migrationBuilder.DropTable(
                name: "PlanViews");

            migrationBuilder.DropTable(
                name: "Subscribers");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "Teams");
        }
    }
}
