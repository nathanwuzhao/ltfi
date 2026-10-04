using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LTFI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalList",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExternalRemovedAt",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSource",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ExternalSource_ExternalId",
                table: "Tasks",
                columns: new[] { "ExternalSource", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_ExternalSource_ExternalId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ExternalList",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ExternalRemovedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ExternalSource",
                table: "Tasks");
        }
    }
}
