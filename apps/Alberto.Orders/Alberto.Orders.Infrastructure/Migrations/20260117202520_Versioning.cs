using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alberto.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Versioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastProcessedPosition",
                schema: "orders",
                table: "order_summaries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Note: xmin is a PostgreSQL system column that already exists on all tables
            // We don't need to add it - EF Core reads from it via UseXminAsConcurrencyToken()
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProcessedPosition",
                schema: "orders",
                table: "order_summaries");

            // Note: xmin is a PostgreSQL system column - we don't drop it
        }
    }
}
