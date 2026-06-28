using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LTFI.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyProjectFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DesiredOutcome",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "Projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DesiredOutcome",
                table: "Projects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "Projects",
                type: "INTEGER",
                nullable: true);
        }
    }
}
