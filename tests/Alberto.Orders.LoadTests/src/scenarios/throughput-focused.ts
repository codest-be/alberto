import { executeMutation, isSuccess, getData } from '../lib/graphql-client';
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
} from '../graphql';
import { generateCreateOrderInput } from '../lib/data-generators';
import {
  eventsCreated,
  eventCreationDuration,
  throughputIterations,
} from '../lib/metrics';

/**
 * High-throughput event creation scenario.
 *
 * This scenario is optimized for maximum event throughput by:
 * - Removing all intentional sleeps
 * - Using minimal operations per iteration (create + confirm = 2 events)
 * - Tracking event counts and durations for throughput analysis
 *
 * Each iteration produces 2 events:
 * 1. OrderCreated
 * 2. OrderConfirmed
 */
export function throughputScenario(): void {
  const tenantId = getNextTenant();
  const createInput = generateCreateOrderInput();

  // Event 1: Create order
  const createStart = Date.now();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'throughput', step: 'create' } }
  );
  const createDuration = Date.now() - createStart;

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    return; // Failed, don't count
  }

  eventsCreated.add(1);
  eventCreationDuration.add(createDuration);

  const orderId = createData.createOrder.orderId;

  // Event 2: Confirm order
  const confirmStart = Date.now();
  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'throughput', step: 'confirm' } }
  );
  const confirmDuration = Date.now() - confirmStart;

  if (isSuccess(confirmResponse)) {
    eventsCreated.add(1);
    eventCreationDuration.add(confirmDuration);
  }

  throughputIterations.add(1);
}

/**
 * Extended throughput scenario with full order lifecycle.
 *
 * This scenario creates more events per iteration for higher event volume:
 * 1. OrderCreated
 * 2. OrderConfirmed
 * 3. OrderShipped
 * 4. OrderDelivered
 *
 * Still minimal sleeps, but more events per VU iteration (4 events).
 */
export function extendedThroughputScenario(): void {
  const tenantId = getNextTenant();
  const createInput = generateCreateOrderInput();

  // Event 1: Create order
  const createStart = Date.now();
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'throughput-extended', step: 'create' } }
  );
  const createDuration = Date.now() - createStart;

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    return;
  }

  eventsCreated.add(1);
  eventCreationDuration.add(createDuration);

  const orderId = createData.createOrder.orderId;

  // Event 2: Confirm order
  const confirmStart = Date.now();
  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'throughput-extended', step: 'confirm' } }
  );
  const confirmDuration = Date.now() - confirmStart;

  if (!isSuccess(confirmResponse)) {
    throughputIterations.add(1);
    return;
  }

  eventsCreated.add(1);
  eventCreationDuration.add(confirmDuration);

  // Event 3: Ship order
  const shipStart = Date.now();
  const shipResponse = executeMutation<ShipOrderResult>(
    SHIP_ORDER_MUTATION,
    {
      input: {
        orderId,
        trackingNumber: `TRK${Date.now()}`,
        carrier: 'FedEx',
      },
    },
    { tenantId, tags: { scenario: 'throughput-extended', step: 'ship' } }
  );
  const shipDuration = Date.now() - shipStart;

  if (!isSuccess(shipResponse)) {
    throughputIterations.add(1);
    return;
  }

  eventsCreated.add(1);
  eventCreationDuration.add(shipDuration);

  // Event 4: Deliver order
  const deliverStart = Date.now();
  const deliverResponse = executeMutation<DeliverOrderResult>(
    DELIVER_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'throughput-extended', step: 'deliver' } }
  );
  const deliverDuration = Date.now() - deliverStart;

  if (isSuccess(deliverResponse)) {
    eventsCreated.add(1);
    eventCreationDuration.add(deliverDuration);
  }

  throughputIterations.add(1);
}

/**
 * Burst write scenario - create only, no state transitions.
 *
 * This is the fastest scenario for pure event creation:
 * - Only creates orders (1 event per iteration)
 * - No follow-up mutations
 * - Maximum events/second measurement
 */
export function burstWriteScenario(): void {
  const tenantId = getNextTenant();
  const createInput = generateCreateOrderInput();

  const start = Date.now();
  const response = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'burst-write', step: 'create' } }
  );
  const duration = Date.now() - start;

  if (getData(response)?.createOrder?.orderId) {
    eventsCreated.add(1);
    eventCreationDuration.add(duration);
    throughputIterations.add(1);
  }
}
