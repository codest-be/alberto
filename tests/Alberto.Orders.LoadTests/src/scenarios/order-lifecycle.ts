import { sleep } from 'k6';
import { executeMutation, executeQuery, isSuccess, getData } from '../lib/graphql-client';
import { getNextTenant } from '../config/tenants';
import {
  CREATE_ORDER_MUTATION,
  CreateOrderResult,
  CONFIRM_ORDER_MUTATION,
  ConfirmOrderResult,
  SHIP_ORDER_MUTATION,
  ShipOrderResult,
  DELIVER_ORDER_MUTATION,
  DeliverOrderResult,
  GET_ORDER_QUERY,
  GetOrderResult,
} from '../graphql';
import {
  generateCreateOrderInput,
  generateShippingInfo,
} from '../lib/data-generators';
import {
  trackCreatedOrder,
  updateOrderStatus,
  orderLifecycleDuration,
} from '../lib/order-tracker';

/**
 * Complete order lifecycle scenario:
 * Create -> Verify -> Confirm -> Ship -> Deliver
 *
 * This simulates a happy path order flow.
 */
export function orderLifecycleScenario(): void {
  const startTime = Date.now();

  // Use a consistent tenant for the entire lifecycle
  const tenantId = getNextTenant();

  // Step 1: Create order
  const createInput = generateCreateOrderInput();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'lifecycle', step: 'create' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    console.error('Failed to create order');
    return;
  }

  const orderId = createData.createOrder.orderId;
  trackCreatedOrder(orderId, createInput.customerId, tenantId);

  sleep(0.5);

  // Step 2: Verify order was created (optional read)
  const getResponse = executeQuery<GetOrderResult>(
    GET_ORDER_QUERY,
    { orderId },
    { tenantId, tags: { scenario: 'lifecycle', step: 'verify-create' } }
  );

  const orderData = getData(getResponse);
  if (orderData?.order?.status !== 'DRAFT') {
    console.error(`Order ${orderId} not in DRAFT status, got: ${orderData?.order?.status}`);
    return;
  }

  sleep(0.5);

  // Step 3: Confirm order
  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'lifecycle', step: 'confirm' } }
  );

  if (!isSuccess(confirmResponse)) {
    console.error(`Failed to confirm order ${orderId}`);
    return;
  }

  updateOrderStatus(orderId, 'confirmed');
  sleep(0.5);

  // Step 4: Ship order
  const shippingInfo = generateShippingInfo();
  const shipResponse = executeMutation<ShipOrderResult>(
    SHIP_ORDER_MUTATION,
    { input: { orderId, ...shippingInfo } },
    { tenantId, tags: { scenario: 'lifecycle', step: 'ship' } }
  );

  if (!isSuccess(shipResponse)) {
    console.error(`Failed to ship order ${orderId}`);
    return;
  }

  updateOrderStatus(orderId, 'shipped');
  sleep(0.5);

  // Step 5: Deliver order
  const deliverResponse = executeMutation<DeliverOrderResult>(
    DELIVER_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'lifecycle', step: 'deliver' } }
  );

  if (!isSuccess(deliverResponse)) {
    console.error(`Failed to deliver order ${orderId}`);
    return;
  }

  updateOrderStatus(orderId, 'delivered');

  // Record total lifecycle duration
  const totalDuration = Date.now() - startTime;
  orderLifecycleDuration.add(totalDuration);

  sleep(1);
}

/**
 * Partial lifecycle - create and confirm only.
 * Useful for building up order inventory for other scenarios.
 */
export function createAndConfirmScenario(): string | null {
  const tenantId = getNextTenant();
  const createInput = generateCreateOrderInput();

  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'create-confirm' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    return null;
  }

  const orderId = createData.createOrder.orderId;
  trackCreatedOrder(orderId, createInput.customerId, tenantId);

  sleep(0.2);

  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'create-confirm' } }
  );

  if (isSuccess(confirmResponse)) {
    updateOrderStatus(orderId, 'confirmed');
    return orderId;
  }

  return null;
}
