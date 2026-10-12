using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartnerNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotificationReceivedAt",
                table: "partners",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastNotifiedAt",
                table: "partners",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyAssetId",
                table: "partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyError",
                table: "partners",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyNegotiationId",
                table: "partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyTransferProcessId",
                table: "partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastNotificationReceivedAt",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "LastNotifiedAt",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "NotifyAssetId",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "NotifyError",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "NotifyNegotiationId",
                table: "partners");

            migrationBuilder.DropColumn(
                name: "NotifyTransferProcessId",
                table: "partners");
        }
    }
}
