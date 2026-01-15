import { sleep } from 'k6';
import { executeMutation, isSuccess, getData } from '../lib/graphql-client';
import { getNextTenant } from '../config/tenants';
import {
  CREATE_ORDER_MUTATION,
  CreateOrderResult,
  ADD_ORDER_ITEM_MUTATION,
  AddOrderItemResult,
  CANCEL_ORDER_MUTATION,
  CancelOrderResult,
} from '../graphql';
import {
  generateCreateOrderInput,
  generateLineItem,
  generateCancellationReason,
} from '../lib/data-generators';
import { trackCreatedOrder, updateOrderStatus } from '../lib/order-tracker';

/**
 * Scenario: Create order, modify it, then cancel.
 * Tests the cancellation flow and order modification.
 */
export function orderWithCancellationScenario(): void {
  const tenantId = getNextTenant();

  // Step 1: Create order
  const createInput = generateCreateOrderInput();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'cancellation' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    console.error('Failed to create order for cancellation scenario');
    return;
  }

  const orderId = createData.createOrder.orderId;
  trackCreatedOrder(orderId, createInput.customerId, tenantId);

  sleep(0.3);

  // Step 2: Add another item
  const newItem = generateLineItem();
  const addItemResponse = executeMutation<AddOrderItemResult>(
    ADD_ORDER_ITEM_MUTATION,
    { input: { orderId, ...newItem } },
    { tenantId, tags: { scenario: 'cancellation', step: 'add-item' } }
  );

  if (!isSuccess(addItemResponse)) {
    console.warn(`Failed to add item to order ${orderId}`);
  }

  sleep(0.3);

  // Step 3: Cancel the order
  const reason = generateCancellationReason();
  const cancelResponse = executeMutation<CancelOrderResult>(
    CANCEL_ORDER_MUTATION,
    { input: { orderId, reason } },
    { tenantId, tags: { scenario: 'cancellation', step: 'cancel' } }
  );

  if (isSuccess(cancelResponse)) {
    updateOrderStatus(orderId, 'cancelled');
  } else {
    console.error(`Failed to cancel order ${orderId}`);
  }

  sleep(0.5);
}

/**
 * Scenario: Create and immediately cancel.
 * Tests quick cancellation path.
 */
export function quickCancelScenario(): void {
  const tenantId = getNextTenant();

  // Create order
  const createInput = generateCreateOrderInput();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'quick-cancel' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    return;
  }

  const orderId = createData.createOrder.orderId;
  trackCreatedOrder(orderId, createInput.customerId, tenantId);

  sleep(0.1);

  // Immediately cancel
  const reason = generateCancellationReason();
  const cancelResponse = executeMutation<CancelOrderResult>(
    CANCEL_ORDER_MUTATION,
    { input: { orderId, reason } },
    { tenantId, tags: { scenario: 'quick-cancel' } }
  );

  if (isSuccess(cancelResponse)) {
    updateOrderStatus(orderId, 'cancelled');
  }

  sleep(0.3);
}
