export const AUTHORIZE_PAYMENT_MUTATION = `
  mutation AuthorizePayment($paymentId: UUID!, $authorizationCode: String!) {
    authorizePayment(paymentId: $paymentId, authorizationCode: $authorizationCode) {
      success
    }
  }
`;

export interface AuthorizePaymentResult {
  authorizePayment: {
    success: boolean;
  };
}
