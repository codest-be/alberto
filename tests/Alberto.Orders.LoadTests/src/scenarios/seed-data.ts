import { executeMutation, getData, isSuccess } from '../lib/graphql-client';
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
import { eventsSeeded, seedingDuration } from '../lib/metrics';

/**
 * Fast seeding scenario for pre-populating the database.
 *
 * This scenario creates complete order lifecycles as quickly as possible:
 * - No sleeps between operations
 * - Full lifecycle per iteration (Create → Confirm → Ship → Deliver)
 * - 4 events per iteration
 *
 * Used to populate the database before throughput testing to simulate
 * production conditions where streams have existing events.
 *
 * Target: 100,000 orders (10,000 per tenant × 10 tenants) = 400,000 events
 */
export function seedOrderScenario(): void {
  const tenantId = getNextTenant();
  const createInput = generateCreateOrderInput();
  const iterationStart = Date.now();

  // Event 1: Create order
  const createResponse = executeMutation<CreateOrderResult>(
    CREATE_ORDER_MUTATION,
    { input: createInput },
    { tenantId, tags: { scenario: 'seeding', step: 'create' } }
  );

  const createData = getData(createResponse);
  if (!createData?.createOrder?.orderId) {
    return; // Failed, skip this iteration
  }

  eventsSeeded.add(1);
  const orderId = createData.createOrder.orderId;

  // Event 2: Confirm order
  const confirmResponse = executeMutation<ConfirmOrderResult>(
    CONFIRM_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'seeding', step: 'confirm' } }
  );

  if (!isSuccess(confirmResponse)) {
    seedingDuration.add(Date.now() - iterationStart);
    return;
  }

  eventsSeeded.add(1);

  // Event 3: Ship order
  const shipResponse = executeMutation<ShipOrderResult>(
    SHIP_ORDER_MUTATION,
    {
      input: {
        orderId,
        trackingNumber: `TRK${Date.now()}${Math.random().toString(36).substring(7)}`,
        carrier: 'FedEx',
      },
    },
    { tenantId, tags: { scenario: 'seeding', step: 'ship' } }
  );

  if (!isSuccess(shipResponse)) {
    seedingDuration.add(Date.now() - iterationStart);
    return;
  }

  eventsSeeded.add(1);

  // Event 4: Deliver order
  const deliverResponse = executeMutation<DeliverOrderResult>(
    DELIVER_ORDER_MUTATION,
    { orderId },
    { tenantId, tags: { scenario: 'seeding', step: 'deliver' } }
  );

  if (isSuccess(deliverResponse)) {
    eventsSeeded.add(1);
  }

  seedingDuration.add(Date.now() - iterationStart);
}
