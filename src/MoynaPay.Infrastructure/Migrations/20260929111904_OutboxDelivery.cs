using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoynaPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_next_attempt_at",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_order_id_created_at",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.AddColumn<string>(
                name: "claimed_by",
                schema: "orders",
                table: "outbox_messages",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_until",
                schema: "orders",
                table: "outbox_messages",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_dead",
                schema: "orders",
                table: "outbox_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_claimed_by",
                schema: "orders",
                table: "outbox_messages",
                column: "claimed_by",
                filter: "claimed_by is not null");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_next_attempt_at",
                schema: "orders",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "delivered_at is null and is_dead = false");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_order_id_created_at",
                schema: "orders",
                table: "outbox_messages",
                columns: new[] { "order_id", "created_at" },
                filter: "delivered_at is null and is_dead = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_claimed_by",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_next_attempt_at",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_order_id_created_at",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claimed_by",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "claimed_until",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "is_dead",
                schema: "orders",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_next_attempt_at",
                schema: "orders",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "delivered_at is null");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_order_id_created_at",
                schema: "orders",
                table: "outbox_messages",
                columns: new[] { "order_id", "created_at" },
                filter: "delivered_at is null");
        }
    }
}
