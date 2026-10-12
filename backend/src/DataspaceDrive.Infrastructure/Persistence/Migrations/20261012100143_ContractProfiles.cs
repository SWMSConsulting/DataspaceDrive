using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractProfile",
                table: "shares",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "membership");

            migrationBuilder.AddColumn<string>(
                name: "ContractProfile",
                table: "incoming_offers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "membership");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractProfile",
                table: "shares");

            migrationBuilder.DropColumn(
                name: "ContractProfile",
                table: "incoming_offers");
        }
    }
}
