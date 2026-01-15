export const CANCEL_ORDER_MUTATION = `
  mutation CancelOrder($input: CancelOrderInput!) {
    cancelOrder(input: $input) {
      success
    }
  }
`;

export interface CancelOrderInput {
  orderId: string;
  reason: string;
}

export interface CancelOrderResult {
  cancelOrder: {
    success: boolean;
  };
}
