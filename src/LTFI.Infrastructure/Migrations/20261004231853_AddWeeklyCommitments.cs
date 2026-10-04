using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LTFI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyCommitments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeeklyCommitments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CheckInId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyCommitments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyCommitments_Reflections_CheckInId",
                        column: x => x.CheckInId,
                        principalTable: "Reflections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WeeklyCommitments_Tasks_LinkedTaskId",
                        column: x => x.LinkedTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyCommitments_CheckInId",
                table: "WeeklyCommitments",
                column: "CheckInId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyCommitments_LinkedTaskId",
                table: "WeeklyCommitments",
                column: "LinkedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyCommitments_WeekStart",
                table: "WeeklyCommitments",
                column: "WeekStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyCommitments");
        }
    }
}
