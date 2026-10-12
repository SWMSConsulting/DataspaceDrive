using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailNotificationsOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Die Spalte kam mit false für bestehende Nutzer; Benachrichtigungen sind aber standardmäßig eingeschaltet.
            migrationBuilder.Sql("UPDATE users SET \"EmailNotifications\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
