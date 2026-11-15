using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class SessionSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                table: "Sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "Sessions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_ShareToken",
                table: "Sessions",
                column: "ShareToken",
                unique: true,
                filter: "\"ShareToken\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_ShareToken",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ShareToken",
                table: "Sessions");
        }
    }
}
