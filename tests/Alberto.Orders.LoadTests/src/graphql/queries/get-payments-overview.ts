export const GET_PAYMENTS_OVERVIEW_QUERY = `
  query GetPaymentsOverview {
    paymentsOverview {
      totalPayments
      authorizedPayments
      capturedPayments
      failedPayments
      refundedPayments
      totalCapturedAmount
      totalRefundedAmount
    }
  }
`;

export interface GetPaymentsOverviewResult {
  paymentsOverview: {
    totalPayments: number;
    authorizedPayments: number;
    capturedPayments: number;
    failedPayments: number;
    refundedPayments: number;
    totalCapturedAmount: number;
    totalRefundedAmount: number;
  } | null;
}
