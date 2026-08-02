using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodyBlues.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionEventLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventLogPath",
                table: "Sessions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EventLogSizeBytes",
                table: "Sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventLogPath",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "EventLogSizeBytes",
                table: "Sessions");
        }
    }
}
