using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alberto.Orders.Platform.Migrations
{
    /// <inheritdoc />
    public partial class versioncolumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_order_summaries",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.AddColumn<int>(
                name: "RebuildVersion",
                schema: "orders",
                table: "order_summaries",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddPrimaryKey(
                name: "PK_order_summaries",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "DocumentId", "RebuildVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_order_summaries",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.DropColumn(
                name: "RebuildVersion",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.AddPrimaryKey(
                name: "PK_order_summaries",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "DocumentId" });
        }
    }
}
