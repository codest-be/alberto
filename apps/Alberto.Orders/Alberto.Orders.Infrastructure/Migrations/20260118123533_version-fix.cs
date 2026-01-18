using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alberto.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class versionfix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_order_summaries_tenant_orderid",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_orderid_version",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "OrderId", "RebuildVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_order_summaries_tenant_orderid_version",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_orderid",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "OrderId" },
                unique: true);
        }
    }
}
