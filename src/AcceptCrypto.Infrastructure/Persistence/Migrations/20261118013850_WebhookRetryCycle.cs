using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcceptCrypto.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookRetryCycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retry_cycle_started_at",
                table: "outbox_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing events: their retry cycle started when they were created.
            migrationBuilder.Sql("UPDATE outbox_events SET retry_cycle_started_at = created_at;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "retry_cycle_started_at",
                table: "outbox_events");
        }
    }
}
