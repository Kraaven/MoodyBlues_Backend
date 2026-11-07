using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class SceneVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SkippedMeshCount",
                table: "SceneRevisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SceneRevisions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Number the existing versions of each scene 1, 2, ... in upload order, before the
            // unique index is created.
            migrationBuilder.Sql("""
                UPDATE "SceneRevisions" AS r
                SET "Version" = numbered.v
                FROM (
                    SELECT "DeveloperId", "SceneId", "Hash",
                           ROW_NUMBER() OVER (PARTITION BY "DeveloperId", "SceneId" ORDER BY "CreatedAtUtc", "Hash") AS v
                    FROM "SceneRevisions"
                ) AS numbered
                WHERE r."DeveloperId" = numbered."DeveloperId"
                  AND r."SceneId" = numbered."SceneId"
                  AND r."Hash" = numbered."Hash";
                """);

            // Sessions that failed only because their scene had not arrived within 24 hours now
            // wait for it instead (status 5 = WaitingForScene); the time limit no longer exists.
            migrationBuilder.Sql("""
                UPDATE "Sessions"
                SET "Status" = 5, "ErrorMessage" = NULL
                WHERE "Status" = 4
                  AND "ErrorMessage" LIKE 'The scene this session was recorded in was never uploaded%';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SceneRevisions_DeveloperId_SceneId_Version",
                table: "SceneRevisions",
                columns: new[] { "DeveloperId", "SceneId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SceneRevisions_DeveloperId_SceneId_Version",
                table: "SceneRevisions");

            migrationBuilder.DropColumn(
                name: "SkippedMeshCount",
                table: "SceneRevisions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SceneRevisions");
        }
    }
}
