using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoynaPay.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "merchants");

            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "api_credentials",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secret_cipher = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    key_ring_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_otp_challenges",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    msisdn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_otp_challenges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_tokens",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    replaced_by_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_tokens", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_ref = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    charged_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    grace_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    customer_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    customer_email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    customer_msisdn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    redirect_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    callback_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    msisdn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    support_msisdn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    actor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    from = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    customer_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    msisdn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    charged_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    fields_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    digit = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    call_attempts = table.Column<int>(type: "integer", nullable: false),
                    paid_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    trx_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    courier = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    tracking_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    callback_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    shipped_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    claimed_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rate_limit_hits",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_limit_hits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "request_nonces",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    nonce = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_nonces", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    calls = table.Column<bool>(type: "boolean", nullable: false),
                    payments = table.Column<bool>(type: "boolean", nullable: false),
                    courier = table.Column<bool>(type: "boolean", nullable: false),
                    plan = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                schema: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    secret_cipher = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    key_ring_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    last_failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_delivered_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_actions",
                schema: "orders",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    result = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_actions", x => new { x.merchant_id, x.order_id, x.action_id });
                });

            migrationBuilder.CreateTable(
                name: "workflow_sessions",
                schema: "orders",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    step = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    complete = table.Column<bool>(type: "boolean", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    history = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_sessions", x => new { x.merchant_id, x.order_id, x.name });
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_credentials_key_id",
                schema: "merchants",
                table: "api_credentials",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_otp_challenges_msisdn_created_at",
                schema: "merchants",
                table: "app_otp_challenges",
                columns: new[] { "msisdn", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_app_tokens_merchant_id_expires_at",
                schema: "merchants",
                table: "app_tokens",
                columns: new[] { "merchant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_app_tokens_token_hash_kind",
                schema: "merchants",
                table: "app_tokens",
                columns: new[] { "token_hash", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_order_ref",
                schema: "payments",
                table: "invoices",
                columns: new[] { "tenant_id", "order_ref" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_merchants_msisdn",
                schema: "merchants",
                table: "merchants",
                column: "msisdn",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_order_events_order_id_at",
                schema: "orders",
                table: "order_events",
                columns: new[] { "order_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_reference",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "reference" },
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_status_created_at",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "status", "created_at" });

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

            migrationBuilder.CreateIndex(
                name: "ix_rate_limit_hits_key_at",
                schema: "merchants",
                table: "rate_limit_hits",
                columns: new[] { "key", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_request_nonces_expires_at",
                schema: "merchants",
                table: "request_nonces",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_request_nonces_key_id_nonce",
                schema: "merchants",
                table: "request_nonces",
                columns: new[] { "key_id", "nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tenant_id",
                schema: "merchants",
                table: "subscriptions",
                column: "tenant_id",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_tenant_id",
                schema: "merchants",
                table: "webhook_endpoints",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_sessions_name_complete_lease_until",
                schema: "orders",
                table: "workflow_sessions",
                columns: new[] { "name", "complete", "lease_until" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_credentials",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "app_otp_challenges",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "app_tokens",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "payments");

            migrationBuilder.DropTable(
                name: "merchants",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "order_events",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "rate_limit_hits",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "request_nonces",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "webhook_endpoints",
                schema: "merchants");

            migrationBuilder.DropTable(
                name: "workflow_actions",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "workflow_sessions",
                schema: "orders");
        }
    }
}
