using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class OptimizedGzipSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OptimizedGzipSizeBytes",
                table: "SceneRevisions",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OptimizedGzipSizeBytes",
                table: "SceneRevisions");
        }
    }
}
