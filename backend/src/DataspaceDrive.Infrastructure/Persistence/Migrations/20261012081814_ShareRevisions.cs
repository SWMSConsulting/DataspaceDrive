using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LineageId",
                table: "shares",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "shares",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceFileIds",
                table: "shares",
                type: "character varying(20000)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFolderIds",
                table: "shares",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersedesShareId",
                table: "shares",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LineageId",
                table: "incoming_offers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "incoming_offers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "incoming_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_shares_LineageId",
                table: "shares",
                column: "LineageId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_offers_PartnerId_LineageId",
                table: "incoming_offers",
                columns: new[] { "PartnerId", "LineageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_shares_LineageId",
                table: "shares");

            migrationBuilder.DropIndex(
                name: "IX_incoming_offers_PartnerId_LineageId",
                table: "incoming_offers");

            migrationBuilder.DropColumn(
                name: "LineageId",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "SourceFileIds",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "SourceFolderIds",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "SupersedesShareId",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "LineageId",
                table: "incoming_offers");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "incoming_offers");

            migrationBuilder.DropColumn(
                name: "SourceKey",
                table: "incoming_items");
        }
    }
}
