using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rota.Tracking.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // docs/04-data-model.md "Tracking". Written by hand: EF cannot express partitioned tables.
            // The primary key must contain the partition key (recorded_at).
            migrationBuilder.Sql(
                """
                CREATE TABLE location_points (
                    courier_id   uuid             NOT NULL,
                    seq          bigint           NOT NULL,
                    lat          double precision NOT NULL,
                    lon          double precision NOT NULL,
                    accuracy_m   real             NULL,
                    recorded_at  timestamptz      NOT NULL,
                    received_at  timestamptz      NOT NULL,
                    CONSTRAINT pk_location_points PRIMARY KEY (courier_id, seq, recorded_at)
                ) PARTITION BY RANGE (recorded_at);
                """);

            // Landing table for COPY (which cannot do ON CONFLICT). UNLOGGED: no write-ahead log, so
            // faster; it only ever holds a batch for the length of one transaction, so losing it costs nothing.
            migrationBuilder.Sql("CREATE UNLOGGED TABLE location_points_staging (LIKE location_points INCLUDING DEFAULTS);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS location_points_staging;");

            // Dropping the parent drops its partitions with it.
            migrationBuilder.Sql("DROP TABLE IF EXISTS location_points;");
        }
    }
}
