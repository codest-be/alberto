using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alberto.Orders.Platform.Migrations
{
    /// <inheritdoc />
    public partial class InitialOrdersProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");

            migrationBuilder.CreateTable(
                name: "order_summaries",
                schema: "orders",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocumentId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TrackingNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Carrier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ShippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_summaries", x => new { x.TenantId, x.DocumentId });
                });

            migrationBuilder.CreateTable(
                name: "order_line_items",
                schema: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OrderDocumentId = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_line_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_line_items_order_summaries_TenantId_OrderDocumentId",
                        columns: x => new { x.TenantId, x.OrderDocumentId },
                        principalSchema: "orders",
                        principalTable: "order_summaries",
                        principalColumns: new[] { "TenantId", "DocumentId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_line_items_tenant_product",
                schema: "orders",
                table: "order_line_items",
                columns: new[] { "TenantId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_order_line_items_TenantId_OrderDocumentId",
                schema: "orders",
                table: "order_line_items",
                columns: new[] { "TenantId", "OrderDocumentId" });

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_created",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_customer",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_orderid",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_status",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "ix_order_summaries_tenant_updated",
                schema: "orders",
                table: "order_summaries",
                columns: new[] { "TenantId", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_line_items",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "order_summaries",
                schema: "orders");
        }
    }
}
