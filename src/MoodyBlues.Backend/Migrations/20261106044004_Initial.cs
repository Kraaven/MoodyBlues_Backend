using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Developers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Developers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scenes",
                columns: table => new
                {
                    DeveloperId = table.Column<string>(type: "text", nullable: false),
                    SceneId = table.Column<string>(type: "text", nullable: false),
                    LatestHash = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scenes", x => new { x.DeveloperId, x.SceneId });
                    table.ForeignKey(
                        name: "FK_Scenes_Developers_DeveloperId",
                        column: x => x.DeveloperId,
                        principalTable: "Developers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeveloperId = table.Column<string>(type: "text", nullable: false),
                    SceneId = table.Column<string>(type: "text", nullable: false),
                    SceneHash = table.Column<string>(type: "text", nullable: false),
                    ClientSessionId = table.Column<string>(type: "text", nullable: false),
                    StreamTokenHash = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastActivityUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedCleanly = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectionCount = table.Column<int>(type: "integer", nullable: false),
                    RawLogPath = table.Column<string>(type: "text", nullable: false),
                    RawLogSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReplayPath = table.Column<string>(type: "text", nullable: true),
                    ReplaySizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    ObjectCount = table.Column<int>(type: "integer", nullable: false),
                    SpawnCount = table.Column<int>(type: "integer", nullable: false),
                    RawEventCount = table.Column<long>(type: "bigint", nullable: false),
                    KeyframeCount = table.Column<long>(type: "bigint", nullable: false),
                    DeltaPromotions = table.Column<long>(type: "bigint", nullable: false),
                    HardClamps = table.Column<long>(type: "bigint", nullable: false),
                    DroppedEvents = table.Column<long>(type: "bigint", nullable: false),
                    CompilerVersion = table.Column<int>(type: "integer", nullable: false),
                    CompiledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
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

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DeveloperId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SceneRevisions",
                columns: table => new
                {
                    DeveloperId = table.Column<string>(type: "text", nullable: false),
                    SceneId = table.Column<string>(type: "text", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    RawGlbPath = table.Column<string>(type: "text", nullable: false),
                    RawSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    OptimizedGlbPath = table.Column<string>(type: "text", nullable: true),
                    OptimizedSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    RegistryPath = table.Column<string>(type: "text", nullable: true),
                    RegistryReady = table.Column<bool>(type: "boolean", nullable: false),
                    TrackedObjectCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SceneRevisions", x => new { x.DeveloperId, x.SceneId, x.Hash });
                    table.ForeignKey(
                        name: "FK_SceneRevisions_Scenes_DeveloperId_SceneId",
                        columns: x => new { x.DeveloperId, x.SceneId },
                        principalTable: "Scenes",
                        principalColumns: new[] { "DeveloperId", "SceneId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_DeveloperId",
                table: "Projects",
                column: "DeveloperId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_UserId",
                table: "Projects",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SceneRevisions_Status",
                table: "SceneRevisions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DeveloperId_ClientSessionId",
                table: "Sessions",
                columns: new[] { "DeveloperId", "ClientSessionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DeveloperId_SceneId",
                table: "Sessions",
                columns: new[] { "DeveloperId", "SceneId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status",
                table: "Sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_StreamTokenHash",
                table: "Sessions",
                column: "StreamTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "SceneRevisions");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Scenes");

            migrationBuilder.DropTable(
                name: "Developers");
        }
    }
}
