using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSceneProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GlbPath",
                table: "Scenes",
                newName: "RawGlbPath");

            migrationBuilder.AddColumn<string>(
                name: "OptimizedGlbPath",
                table: "Scenes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OptimizedSizeBytes",
                table: "Scenes",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProcessingStatus",
                table: "Scenes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RawSizeBytes",
                table: "Scenes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptimizedGlbPath",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "OptimizedSizeBytes",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "ProcessingStatus",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "RawSizeBytes",
                table: "Scenes");

            migrationBuilder.RenameColumn(
                name: "RawGlbPath",
                table: "Scenes",
                newName: "GlbPath");
        }
    }
}
