export const REFUND_PAYMENT_MUTATION = `
  mutation RefundPayment($input: RefundPaymentInput!) {
    refundPayment(input: $input) {
      success
    }
  }
`;

export interface RefundPaymentInput {
  paymentId: string;
  amount: number;
  reason: string;
}

export interface RefundPaymentResult {
  refundPayment: {
    success: boolean;
  };
}
