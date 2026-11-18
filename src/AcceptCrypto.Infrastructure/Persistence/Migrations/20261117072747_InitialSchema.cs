using System;
using System.Numerics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AcceptCrypto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "invoice_derivation_index_seq",
                startValue: 0L,
                minValue: 0L,
                maxValue: 2147483647L);

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<BigInteger>(type: "numeric(78,0)", nullable: false),
                    external_order_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    deposit_address = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    derivation_index = table.Column<long>(type: "bigint", nullable: false),
                    public_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.CheckConstraint("ck_invoices_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_invoices_derivation_index", "derivation_index BETWEEN 0 AND 2147483647");
                });

            migrationBuilder.CreateTable(
                name: "scanner_checkpoints",
                columns: table => new
                {
                    chain_id = table.Column<long>(type: "bigint", nullable: false),
                    last_scanned_block = table.Column<long>(type: "bigint", nullable: false),
                    last_scanned_block_hash = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scanner_checkpoints", x => x.chain_id);
                });

            migrationBuilder.CreateTable(
                name: "unmatched_payments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    chain_id = table.Column<long>(type: "bigint", nullable: false),
                    tx_hash = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    log_index = table.Column<int>(type: "integer", nullable: false),
                    block_number = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    block_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    from_address = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    to_address = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    amount = table.Column<BigInteger>(type: "numeric(78,0)", nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unmatched_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.key);
                    table.ForeignKey(
                        name: "fk_idempotency_keys_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "observed_transfers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tx_hash = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    log_index = table.Column<int>(type: "integer", nullable: false),
                    from_address = table.Column<string>(type: "character varying(42)", maxLength: 42, nullable: false),
                    amount = table.Column<BigInteger>(type: "numeric(78,0)", nullable: false),
                    block_number = table.Column<long>(type: "bigint", nullable: false),
                    block_hash = table.Column<string>(type: "character varying(66)", maxLength: 66, nullable: false),
                    block_timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_on_time = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reverted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    chain_id = table.Column<long>(type: "bigint", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observed_transfers", x => x.id);
                    table.CheckConstraint("ck_observed_transfers_amount_positive", "amount > 0");
                    table.ForeignKey(
                        name: "fk_observed_transfers_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_outbox_events_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    outbox_event_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_delivery_attempts_outbox_events_outbox_event_id",
                        column: x => x.outbox_event_id,
                        principalTable: "outbox_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_invoice_id",
                table: "idempotency_keys",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_created_at_id",
                table: "invoices",
                columns: new[] { "created_at", "id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_deposit_address",
                table: "invoices",
                column: "deposit_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_derivation_index",
                table: "invoices",
                column: "derivation_index",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_idempotency_key",
                table: "invoices",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_public_token",
                table: "invoices",
                column: "public_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_status_expires_at",
                table: "invoices",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_observed_transfers_chain_id_tx_hash_log_index",
                table: "observed_transfers",
                columns: new[] { "chain_id", "tx_hash", "log_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_observed_transfers_invoice_id",
                table: "observed_transfers",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_observed_transfers_state_block_number",
                table: "observed_transfers",
                columns: new[] { "state", "block_number" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_event_id",
                table: "outbox_events",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_invoice_id",
                table: "outbox_events",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_status_next_attempt_at",
                table: "outbox_events",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_unmatched_payments_chain_id_tx_hash_log_index",
                table: "unmatched_payments",
                columns: new[] { "chain_id", "tx_hash", "log_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unmatched_payments_to_address",
                table: "unmatched_payments",
                column: "to_address");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_attempts_outbox_event_id",
                table: "webhook_delivery_attempts",
                column: "outbox_event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "observed_transfers");

            migrationBuilder.DropTable(
                name: "scanner_checkpoints");

            migrationBuilder.DropTable(
                name: "unmatched_payments");

            migrationBuilder.DropTable(
                name: "webhook_delivery_attempts");

            migrationBuilder.DropTable(
                name: "outbox_events");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropSequence(
                name: "invoice_derivation_index_seq");
        }
    }
}
