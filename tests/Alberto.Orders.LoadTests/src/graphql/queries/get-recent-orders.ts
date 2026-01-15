export const GET_RECENT_ORDERS_QUERY = `
  query GetRecentOrders($limit: Int!) {
    recentOrders(limit: $limit) {
      orderId
      customerId
      status
      total
      createdAt
      updatedAt
    }
  }
`;

export interface GetRecentOrdersVariables {
  limit: number;
}

export interface RecentOrder {
  orderId: string;
  customerId: string;
  status: string;
  total: number;
  createdAt: string;
  updatedAt: string | null;
}

export interface GetRecentOrdersResult {
  recentOrders: RecentOrder[];
}
