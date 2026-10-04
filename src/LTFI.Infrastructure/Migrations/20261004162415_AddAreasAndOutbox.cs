using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LTFI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAreasAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AreaId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalPendingSince",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsStanding",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "OutboxCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Op = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalUrl = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectAreas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectAreas_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_AreaId",
                table: "Tasks",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxCommands_ExternalUrl",
                table: "OutboxCommands",
                column: "ExternalUrl");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectAreas_ProjectId",
                table: "ProjectAreas",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_ProjectAreas_AreaId",
                table: "Tasks",
                column: "AreaId",
                principalTable: "ProjectAreas",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_ProjectAreas_AreaId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "OutboxCommands");

            migrationBuilder.DropTable(
                name: "ProjectAreas");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_AreaId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AreaId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ExternalPendingSince",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IsStanding",
                table: "Projects");
        }
    }
}
