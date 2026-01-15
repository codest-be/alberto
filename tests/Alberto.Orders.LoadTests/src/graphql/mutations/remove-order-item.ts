export const REMOVE_ORDER_ITEM_MUTATION = `
  mutation RemoveOrderItem($orderId: UUID!, $productId: UUID!) {
    removeOrderItem(orderId: $orderId, productId: $productId) {
      success
    }
  }
`;

export interface RemoveOrderItemVariables {
  orderId: string;
  productId: string;
}

export interface RemoveOrderItemResult {
  removeOrderItem: {
    success: boolean;
  };
}
