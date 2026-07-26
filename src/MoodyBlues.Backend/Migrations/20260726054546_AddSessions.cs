using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperId = table.Column<string>(type: "text", nullable: false),
                    SceneId = table.Column<string>(type: "text", nullable: false),
                    AnimationPath = table.Column<string>(type: "text", nullable: true),
                    AnimationSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    RawEventCount = table.Column<long>(type: "bigint", nullable: false),
                    KeyframeCount = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    ObjectCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sessions_Developers_DeveloperId",
                        column: x => x.DeveloperId,
                        principalTable: "Developers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DeveloperId_SceneId",
                table: "Sessions",
                columns: new[] { "DeveloperId", "SceneId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
