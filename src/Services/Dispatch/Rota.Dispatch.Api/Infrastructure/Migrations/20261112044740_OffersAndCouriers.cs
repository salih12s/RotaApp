using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rota.Dispatch.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OffersAndCouriers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dropoff_address",
                table: "dispatch_jobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "dropoff_lat",
                table: "dispatch_jobs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "dropoff_lon",
                table: "dispatch_jobs",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "pickup_address",
                table: "dispatch_jobs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "couriers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    current_offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shift_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_couriers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    courier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    distance_m = table.Column<int>(type: "integer", nullable: false),
                    eta_seconds = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    responded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_offers", x => x.id);
                    table.ForeignKey(
                        name: "fk_offers_couriers_courier_id",
                        column: x => x.courier_id,
                        principalTable: "couriers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_offers_dispatch_jobs_order_id",
                        column: x => x.order_id,
                        principalTable: "dispatch_jobs",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_couriers_state",
                table: "couriers",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_offers_pending_expiry",
                table: "offers",
                column: "expires_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_offers_one_pending_per_courier",
                table: "offers",
                column: "courier_id",
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_offers_one_pending_per_order",
                table: "offers",
                column: "order_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropTable(
                name: "couriers");

            migrationBuilder.DropColumn(
                name: "dropoff_address",
                table: "dispatch_jobs");

            migrationBuilder.DropColumn(
                name: "dropoff_lat",
                table: "dispatch_jobs");

            migrationBuilder.DropColumn(
                name: "dropoff_lon",
                table: "dispatch_jobs");

            migrationBuilder.DropColumn(
                name: "pickup_address",
                table: "dispatch_jobs");
        }
    }
}
