using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoynaPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CallRetryAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_call_attempt_at",
                schema: "orders",
                table: "orders",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_call_attempt_at",
                schema: "orders",
                table: "orders",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_status_next_call_attempt_at",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "status", "next_call_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_orders_tenant_id_status_next_call_attempt_at",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "last_call_attempt_at",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "next_call_attempt_at",
                schema: "orders",
                table: "orders");
        }
    }
}
