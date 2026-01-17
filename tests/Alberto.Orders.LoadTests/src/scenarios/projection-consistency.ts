import { sleep, check } from 'k6';
import { executeMutation, executeQuery, isSuccess, getData } from '../lib/graphql-client';
import { getNextTenant } from '../config/tenants';
import {
  CREATE_ORDER_MUTATION,
  CreateOrderResult,
  CONFIRM_ORDER_MUTATION,
  ConfirmOrderResult,
  GET_ORDER_QUERY,
  GetOrderResult,
  GET_ORDERS_OVERVIEW_QUERY,
  GetOrdersOverviewResult,
} from '../graphql';
import { generateCreateOrderInput } from '../lib/data-generators';
import { projectionLatency, projectionConsistencyFailures } from '../lib/metrics';

/**
 * Projection consistency scenario.
 * Validates that projections are flushed correctly after mutations (verifies IFlushable fix).
 *
 * Flow:
 * 1. Query ordersOverview to get baseline counts
 * 2. Create a new order with line items
 * 3. Confirm the order
 * 4. Wait briefly for async processing
 * 5. Query ordersOverview again
 * 6. Assert that counts increased appropriately
 * 7. Query the individual order to verify it exists
 */
export function projectionConsistencyScenario(): void {
  const startTime = Date.now();
  const tenantId = getNextTenant();

  // Step 1: Get baseline counts from ordersOverview projection
  const beforeResponse = executeQuery<GetOrdersOverviewResult>(
    GET_ORDERS_OVERVIEW_QUERY,
    undefined,
    { tenantId, tags: { scenario: 'consistency', step: 'before' }, checkResponse: false }
  );

  const beforeData = getData(beforeResponse);
  const beforeTotalOrders = beforeData?.ordersOverview?.totalOrders ?? 0;
  const beforeDraftOrders = beforeData?.ordersOverview?.draftOrders ?? 0;
  const beforeConfirmedOrders = beforeData?.ordersOverview?.confirmedOrders ?? 0;

  // Step 2: Create a new order
  const createInput = generateCreateOrderInput();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'consistency', step: 'create' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    console.error('Consistency check failed: Could not create order');
    projectionConsistencyFailures.add(1);
    return;
  }

  const orderId = createData.createOrder.orderId;

  // Step 3: Confirm the order
  sleep(0.1); // Small pause before confirming
  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'consistency', step: 'confirm' } }
  );

  if (!isSuccess(confirmResponse)) {
    console.error(`Consistency check failed: Could not confirm order ${orderId}`);
    projectionConsistencyFailures.add(1);
    return;
  }

  // Step 4: Wait for async processing (projection flush)
  // With 250ms polling interval + processing time + flush, we need ~400ms
  // The IFlushable fix ensures projections are flushed after each batch
  sleep(0.4);

  // Step 5: Query ordersOverview again to verify projection updated
  const afterResponse = executeQuery<GetOrdersOverviewResult>(
    GET_ORDERS_OVERVIEW_QUERY,
    undefined,
    { tenantId, tags: { scenario: 'consistency', step: 'after' }, checkResponse: false }
  );

  const afterData = getData(afterResponse);
  const afterTotalOrders = afterData?.ordersOverview?.totalOrders ?? 0;
  const afterConfirmedOrders = afterData?.ordersOverview?.confirmedOrders ?? 0;

  // Record projection latency (time from mutation to consistent read)
  const latency = Date.now() - startTime;
  projectionLatency.add(latency);

  // Step 6: Assert projection consistency
  const projectionChecks = check(
    { beforeTotalOrders, afterTotalOrders, beforeConfirmedOrders, afterConfirmedOrders },
    {
      'projection updated: totalOrders increased': (r) =>
        r.afterTotalOrders > r.beforeTotalOrders,
      'projection updated: confirmedOrders increased': (r) =>
        r.afterConfirmedOrders > r.beforeConfirmedOrders,
    }
  );

  if (!projectionChecks) {
    console.error(
      `Projection consistency failure: totalOrders ${beforeTotalOrders} -> ${afterTotalOrders}, ` +
        `confirmedOrders ${beforeConfirmedOrders} -> ${afterConfirmedOrders}`
    );
    projectionConsistencyFailures.add(1);
  }

  // Step 7: Verify individual order exists with correct status
  const orderResponse = executeQuery<GetOrderResult>(
    GET_ORDER_QUERY,
    { orderId },
    { tenantId, tags: { scenario: 'consistency', step: 'verify-order' }, checkResponse: false }
  );

  const orderData = getData(orderResponse);
  const orderCheck = check(orderData, {
    'order exists': (r) => r?.order !== null && r?.order !== undefined,
    'order has correct status': (r) => r?.order?.status === 'CONFIRMED',
    'order has correct customer': (r) => r?.order?.customerId === createInput.customerId,
    'order has line items': (r) =>
      r?.order?.lineItems !== undefined && r?.order?.lineItems.length === createInput.lineItems.length,
  });

  if (!orderCheck) {
    console.error(`Order verification failed for ${orderId}: ${JSON.stringify(orderData?.order)}`);
    projectionConsistencyFailures.add(1);
  }

  sleep(0.5);
}

/**
 * Projection staleness check - queries the same projection multiple times
 * to detect any inconsistencies or stale reads.
 */
export function projectionStalenessScenario(): void {
  const tenantId = getNextTenant();

  // Query overview 3 times in succession
  const responses: (GetOrdersOverviewResult | undefined)[] = [];

  for (let i = 0; i < 3; i++) {
    const response = executeQuery<GetOrdersOverviewResult>(
      GET_ORDERS_OVERVIEW_QUERY,
      undefined,
      { tenantId, tags: { scenario: 'staleness', iteration: i.toString() }, checkResponse: false }
    );
    responses.push(getData(response));
    sleep(0.05);
  }

  // All reads should return consistent data (no stale reads)
  const totalOrdersValues = responses.map((r) => r?.ordersOverview?.totalOrders ?? -1);
  const allConsistent = totalOrdersValues.every((v) => v === totalOrdersValues[0]);

  check({ consistent: allConsistent, values: totalOrdersValues }, {
    'no stale reads: consecutive queries return same totalOrders': (r) => r.consistent,
  });

  if (!allConsistent) {
    console.warn(`Staleness detected: totalOrders varied across reads: ${JSON.stringify(totalOrdersValues)}`);
  }

  sleep(0.3);
}
