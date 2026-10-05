using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeMeet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MigrateToSinglePhaseVoting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Availabilities_TimeSlots_TimeSlotId",
                table: "Availabilities");

            migrationBuilder.DropForeignKey(
                name: "FK_Meetings_TimeSlots_SelectedTimeSlotId",
                table: "Meetings");

            migrationBuilder.DropTable(
                name: "TimeSlots");

            migrationBuilder.DropIndex(
                name: "IX_Meetings_SelectedTimeSlotId",
                table: "Meetings");

            migrationBuilder.DropIndex(
                name: "IX_Availabilities_TimeSlotId",
                table: "Availabilities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Availability_ExactlyOneTarget",
                table: "Availabilities");

            migrationBuilder.DropColumn(
                name: "SelectedTimeSlotId",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "TimeSlotId",
                table: "Availabilities");

            migrationBuilder.DropColumn(
                name: "Phase",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "Deadline",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "FinalDeadline",
                table: "Meetings");

            migrationBuilder.AddColumn<int>(
                name: "RetentionMode",
                table: "Meetings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SelectedStartTime",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SelectedEndTime",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasNoSuitableTime",
                table: "Participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOrganizer",
                table: "Participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletionReason",
                table: "Meetings",
                type: "integer",
                nullable: true);


            migrationBuilder.AlterColumn<Guid>(
                name: "GridCellId",
                table: "Availabilities",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Availabilities_ParticipantId_GridCellId",
                table: "Availabilities",
                columns: new[] { "ParticipantId", "GridCellId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Availabilities_ParticipantId_GridCellId",
                table: "Availabilities");

            migrationBuilder.DropColumn(
                name: "HasNoSuitableTime",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsOrganizer",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "Meetings");


            migrationBuilder.DropColumn(
                name: "SelectedStartTime",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "SelectedEndTime",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "RetentionMode",
                table: "Meetings");

            migrationBuilder.AddColumn<int>(
                name: "Phase",
                table: "Meetings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Deadline",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinalDeadline",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedTimeSlotId",
                table: "Meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "GridCellId",
                table: "Availabilities",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TimeSlotId",
                table: "Availabilities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TimeSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceGridCellId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeSlots_GridCells_SourceGridCellId",
                        column: x => x.SourceGridCellId,
                        principalTable: "GridCells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TimeSlots_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Meetings_SelectedTimeSlotId",
                table: "Meetings",
                column: "SelectedTimeSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_Availabilities_TimeSlotId",
                table: "Availabilities",
                column: "TimeSlotId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Availability_ExactlyOneTarget",
                table: "Availabilities",
                sql: "(\"GridCellId\" IS NOT NULL AND \"TimeSlotId\" IS NULL) OR (\"GridCellId\" IS NULL AND \"TimeSlotId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_MeetingId",
                table: "TimeSlots",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSlots_SourceGridCellId",
                table: "TimeSlots",
                column: "SourceGridCellId");

            migrationBuilder.AddForeignKey(
                name: "FK_Availabilities_TimeSlots_TimeSlotId",
                table: "Availabilities",
                column: "TimeSlotId",
                principalTable: "TimeSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Meetings_TimeSlots_SelectedTimeSlotId",
                table: "Meetings",
                column: "SelectedTimeSlotId",
                principalTable: "TimeSlots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
