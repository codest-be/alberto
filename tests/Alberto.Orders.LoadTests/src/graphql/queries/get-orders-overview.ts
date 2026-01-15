export const GET_ORDERS_OVERVIEW_QUERY = `
  query GetOrdersOverview {
    ordersOverview {
      totalOrders
      draftOrders
      confirmedOrders
      shippedOrders
      deliveredOrders
      cancelledOrders
      totalRevenue
      averageOrderValue
      lastOrderAt
      updatedAt
    }
  }
`;

export interface OrdersOverview {
  totalOrders: number;
  draftOrders: number;
  confirmedOrders: number;
  shippedOrders: number;
  deliveredOrders: number;
  cancelledOrders: number;
  totalRevenue: number;
  averageOrderValue: number;
  lastOrderAt: string | null;
  updatedAt: string;
}

export interface GetOrdersOverviewResult {
  ordersOverview: OrdersOverview | null;
}
