using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Charging.Connectors.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "slots");

            migrationBuilder.CreateTable(
                name: "Connectors",
                schema: "slots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ConnectorType = table.Column<string>(type: "text", nullable: false),
                    City = table.Column<string>(type: "text", nullable: false),
                    ServiceDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PriceCents = table.Column<long>(type: "bigint", nullable: false),
                    SlotsTotal = table.Column<int>(type: "integer", nullable: false),
                    SlotsLeft = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connectors", x => x.Id);
                    table.CheckConstraint("CK_Connectors_Remaining", "\"SlotsLeft\" >= 0 AND \"SlotsLeft\" <= \"SlotsTotal\"");
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                schema: "slots",
                columns: table => new
                {
                    Consumer = table.Column<string>(type: "text", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => new { x.Consumer, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                schema: "slots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    RoutingKey = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SlotHolds",
                schema: "slots",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slots = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlotHolds", x => x.ReservationId);
                    table.ForeignKey(
                        name: "FK_SlotHolds_Connectors_ConnectorId",
                        column: x => x.ConnectorId,
                        principalSchema: "slots",
                        principalTable: "Connectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Connectors_Code",
                schema: "slots",
                table: "Connectors",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAt",
                schema: "slots",
                table: "OutboxMessages",
                column: "SentAt",
                filter: "\"SentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SlotHolds_ConnectorId",
                schema: "slots",
                table: "SlotHolds",
                column: "ConnectorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxMessages",
                schema: "slots");

            migrationBuilder.DropTable(
                name: "OutboxMessages",
                schema: "slots");

            migrationBuilder.DropTable(
                name: "SlotHolds",
                schema: "slots");

            migrationBuilder.DropTable(
                name: "Connectors",
                schema: "slots");
        }
    }
}
