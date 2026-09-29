using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoynaPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Devices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_device_pairing_tokens",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_device_pairing_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_devices",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    app_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    push_token = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    permission_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    battery_percent = table.Column<int>(type: "integer", nullable: true),
                    network_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_devices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_device_pairing_tokens_expires_at",
                schema: "merchants",
                table: "app_device_pairing_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_app_device_pairing_tokens_token_hash",
                schema: "merchants",
                table: "app_device_pairing_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_devices_id_device_token_hash",
                schema: "merchants",
                table: "app_devices",
                columns: new[] { "id", "device_token_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_app_devices_merchant_id_last_heartbeat_at",
                schema: "merchants",
                table: "app_devices",
                columns: new[] { "merchant_id", "last_heartbeat_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_device_pairing_tokens",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "app_devices",
                schema: "merchants");
        }
    }
}
