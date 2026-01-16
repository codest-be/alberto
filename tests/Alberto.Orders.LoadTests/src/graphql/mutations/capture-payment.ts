export const CAPTURE_PAYMENT_MUTATION = `
  mutation CapturePayment($input: CapturePaymentInput!) {
    capturePayment(input: $input) {
      success
    }
  }
`;

export interface CapturePaymentInput {
  paymentId: string;
  amount?: number;
}

export interface CapturePaymentResult {
  capturePayment: {
    success: boolean;
  };
}
