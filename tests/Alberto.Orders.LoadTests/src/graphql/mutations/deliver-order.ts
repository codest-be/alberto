export const DELIVER_ORDER_MUTATION = `
  mutation DeliverOrder($orderId: UUID!) {
    deliverOrder(orderId: $orderId) {
      success
    }
  }
`;

export interface DeliverOrderVariables {
  orderId: string;
}

export interface DeliverOrderResult {
  deliverOrder: {
    success: boolean;
  };
}
