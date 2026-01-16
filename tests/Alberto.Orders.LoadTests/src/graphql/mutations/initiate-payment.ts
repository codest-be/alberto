export const INITIATE_PAYMENT_MUTATION = `
  mutation InitiatePayment($input: InitiatePaymentInput!) {
    initiatePayment(input: $input) {
      paymentId
    }
  }
`;

export interface InitiatePaymentInput {
  orderId: string;
  amount: number;
  currency: string;
  paymentMethod: string;
}

export interface InitiatePaymentResult {
  initiatePayment: {
    paymentId: string;
  };
}
