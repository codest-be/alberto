export const ADD_ORDER_ITEM_MUTATION = `
  mutation AddOrderItem($input: AddOrderItemInput!) {
    addOrderItem(input: $input) {
      success
    }
  }
`;

export interface AddOrderItemInput {
  orderId: string;
  productId: string;
  productName: string;
  quantity: number;
  unitPrice: number;
}

export interface AddOrderItemResult {
  addOrderItem: {
    success: boolean;
  };
}
