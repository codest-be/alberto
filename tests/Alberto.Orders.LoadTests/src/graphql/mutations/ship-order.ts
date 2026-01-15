export const SHIP_ORDER_MUTATION = `
  mutation ShipOrder($input: ShipOrderInput!) {
    shipOrder(input: $input) {
      success
    }
  }
`;

export interface ShipOrderInput {
  orderId: string;
  trackingNumber: string;
  carrier: string;
}

export interface ShipOrderResult {
  shipOrder: {
    success: boolean;
  };
}
