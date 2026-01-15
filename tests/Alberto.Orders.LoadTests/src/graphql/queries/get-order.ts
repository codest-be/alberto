export const GET_ORDER_QUERY = `
  query GetOrder($orderId: UUID!) {
    order(orderId: $orderId) {
      orderId
      customerId
      status
      total
      trackingNumber
      carrier
      cancellationReason
      lineItems {
        productId
        productName
        quantity
        unitPrice
        total
      }
      notes
      createdAt
      confirmedAt
      shippedAt
      deliveredAt
      cancelledAt
      updatedAt
    }
  }
`;

export interface GetOrderVariables {
  orderId: string;
}

export interface OrderLineItem {
  productId: string;
  productName: string;
  quantity: number;
  unitPrice: number;
  total: number;
}

export type OrderStatus = 'NONE' | 'DRAFT' | 'CONFIRMED' | 'SHIPPED' | 'DELIVERED' | 'CANCELLED';

export interface Order {
  orderId: string;
  customerId: string;
  status: OrderStatus;
  total: number;
  trackingNumber: string | null;
  carrier: string | null;
  cancellationReason: string | null;
  lineItems: OrderLineItem[];
  notes: string | null;
  createdAt: string;
  confirmedAt: string | null;
  shippedAt: string | null;
  deliveredAt: string | null;
  cancelledAt: string | null;
  updatedAt: string | null;
}

export interface GetOrderResult {
  order: Order | null;
}
