using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataspaceDrive.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "partners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Bpn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DspAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Did = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Protocol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProtocolAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolveError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SharingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Blocked = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastPolledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPollError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_partners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_shares_users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incoming_offers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderBpn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AssetId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OfferId = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SenderOrganization = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalSize = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StateChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TargetFolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NegotiationId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AgreementId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TransferProcessId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_offers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incoming_offers_partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientBpn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TokenVersion = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RetrievalCount = table.Column<int>(type: "integer", nullable: false),
                    LastRetrievalAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deliveries_partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deliveries_shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "share_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FileEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BlobKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_share_items_file_versions_FileVersionId",
                        column: x => x.FileVersionId,
                        principalTable: "file_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_share_items_shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "incoming_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OfferId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    FileEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incoming_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incoming_items_incoming_offers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "incoming_offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_retrievals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConsumerBpn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AgreementId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ItemKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Offset = table.Column<long>(type: "bigint", nullable: true),
                    Length = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_retrievals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_retrievals_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_tasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_tasks_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_PartnerId",
                table: "deliveries",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_ShareId",
                table: "deliveries",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_State_ExpiresAt",
                table: "deliveries",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_retrievals_DeliveryId_OccurredAt",
                table: "delivery_retrievals",
                columns: new[] { "DeliveryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_tasks_CompletedAt_NextAttemptAt",
                table: "delivery_tasks",
                columns: new[] { "CompletedAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_tasks_DeliveryId",
                table: "delivery_tasks",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_incoming_items_OfferId_ItemKey",
                table: "incoming_items",
                columns: new[] { "OfferId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incoming_offers_PartnerId_AssetId",
                table: "incoming_offers",
                columns: new[] { "PartnerId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incoming_offers_State",
                table: "incoming_offers",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_partners_Bpn",
                table: "partners",
                column: "Bpn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_share_items_FileVersionId",
                table: "share_items",
                column: "FileVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_share_items_ShareId_ItemKey",
                table: "share_items",
                columns: new[] { "ShareId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shares_CreatedBy",
                table: "shares",
                column: "CreatedBy");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_retrievals");

            migrationBuilder.DropTable(
                name: "delivery_tasks");

            migrationBuilder.DropTable(
                name: "incoming_items");

            migrationBuilder.DropTable(
                name: "share_items");

            migrationBuilder.DropTable(
                name: "deliveries");

            migrationBuilder.DropTable(
                name: "incoming_offers");

            migrationBuilder.DropTable(
                name: "shares");

            migrationBuilder.DropTable(
                name: "partners");
        }
    }
}
