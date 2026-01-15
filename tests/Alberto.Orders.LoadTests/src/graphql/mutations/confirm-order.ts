export const CONFIRM_ORDER_MUTATION = `
  mutation ConfirmOrder($orderId: UUID!) {
    confirmOrder(orderId: $orderId) {
      success
    }
  }
`;

export interface ConfirmOrderVariables {
  orderId: string;
}

export interface ConfirmOrderResult {
  confirmOrder: {
    success: boolean;
  };
}
