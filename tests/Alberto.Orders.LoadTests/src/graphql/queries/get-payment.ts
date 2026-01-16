export const GET_PAYMENT_QUERY = `
  query GetPayment($paymentId: UUID!) {
    payment(paymentId: $paymentId) {
      paymentId
      orderId
      amount
      currency
      paymentMethod
      status
      authorizationCode
      errorCode
      errorMessage
      refundedAmount
      createdAt
      authorizedAt
      capturedAt
      refundedAt
    }
  }
`;

export interface GetPaymentResult {
  payment: {
    paymentId: string;
    orderId: string;
    amount: number;
    currency: string;
    paymentMethod: string;
    status: string;
    authorizationCode: string | null;
    errorCode: string | null;
    errorMessage: string | null;
    refundedAmount: number | null;
    createdAt: string;
    authorizedAt: string | null;
    capturedAt: string | null;
    refundedAt: string | null;
  } | null;
}
