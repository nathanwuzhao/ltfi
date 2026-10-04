using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LTFI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPomodoroAndNsdr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PomodorosCompleted",
                table: "FocusSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PomodorosCompleted",
                table: "FocusSessions");
        }
    }
}
