using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackfillPhase3Defaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Die Spalten der Phase 3 kamen für bestehende Zeilen mit Platzhaltern. Ohne eigene Linie teilten sich alle
            // älteren Freigaben die leere GUID, und Revisionen verschiedener Freigaben wären beim Empfänger zusammengefallen.
            migrationBuilder.Sql("UPDATE shares SET \"LineageId\" = \"Id\", \"Revision\" = 1 WHERE \"LineageId\" = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("UPDATE shares SET \"Approval\" = 'NotRequired' WHERE \"Approval\" = '';");
            migrationBuilder.Sql("UPDATE partners SET \"AutoAccept\" = 'Off' WHERE \"AutoAccept\" = '';");
            migrationBuilder.Sql("UPDATE incoming_offers SET \"Revision\" = 1 WHERE \"Revision\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
