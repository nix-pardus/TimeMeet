using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TimeMeet.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParticipantPasswordHash",
                table: "Participants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerPasswordHash",
                table: "Meetings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ParticipantPasswordHash",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OwnerPasswordHash",
                table: "Meetings");
        }
    }
}
