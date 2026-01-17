using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alberto.Orders.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LineItemsAsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_line_items",
                schema: "orders");

            migrationBuilder.AddColumn<string>(
                name: "LineItems",
                schema: "orders",
                table: "order_summaries",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LineItems",
                schema: "orders",
                table: "order_summaries");

            migrationBuilder.CreateTable(
                name: "order_line_items",
                schema: "orders",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrderDocumentId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_line_items", x => new { x.TenantId, x.OrderDocumentId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_order_line_items_order_summaries_TenantId_OrderDocumentId",
                        columns: x => new { x.TenantId, x.OrderDocumentId },
                        principalSchema: "orders",
                        principalTable: "order_summaries",
                        principalColumns: new[] { "TenantId", "DocumentId" },
                        onDelete: ReferentialAction.Cascade);
                });
        }
    }
}
