using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class ShareSettingsAndUnityName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShareSettingsJson",
                table: "Sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnityName",
                table: "Scenes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShareDefaultsJson",
                table: "Projects",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShareSettingsJson",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "UnityName",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "ShareDefaultsJson",
                table: "Projects");
        }
    }
}
