using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartnerAutoAccept : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoAccept",
                table: "partners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "AutoAcceptFolderId",
                table: "partners",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AutoAcceptUserId",
                table: "partners",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoAccept",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "AutoAcceptFolderId",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "AutoAcceptUserId",
                table: "partners");
        }
    }
}
