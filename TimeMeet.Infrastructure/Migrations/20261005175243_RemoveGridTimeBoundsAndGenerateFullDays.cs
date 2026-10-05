using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeMeet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveGridTimeBoundsAndGenerateFullDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GridFrom",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "GridTo",
                table: "Meetings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "GridFrom",
                table: "Meetings",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "GridTo",
                table: "Meetings",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));
        }
    }
}
