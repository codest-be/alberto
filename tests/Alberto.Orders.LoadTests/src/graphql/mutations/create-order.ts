export const CREATE_ORDER_MUTATION = `
  mutation CreateOrder($input: CreateOrderInput!) {
    createOrder(input: $input) {
      orderId
    }
  }
`;

export interface OrderItemInput {
  productId: string;
  productName: string;
  quantity: number;
  unitPrice: number;
}

export interface CreateOrderInput {
  customerId: string;
  lineItems: OrderItemInput[];
  notes?: string;
}

export interface CreateOrderResult {
  createOrder: {
    orderId: string;
  };
}
