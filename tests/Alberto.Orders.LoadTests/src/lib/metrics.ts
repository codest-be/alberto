import { Counter, Rate, Trend, Gauge } from 'k6/metrics';

/**
 * Request-level metrics.
 */
export const graphqlRequests = new Counter('graphql_requests_total');
export const graphqlErrors = new Counter('graphql_errors_total');
export const graphqlErrorRate = new Rate('graphql_error_rate');

/**
 * Operation-specific duration trends.
 */
export const createOrderDuration = new Trend('create_order_duration', true);
export const confirmOrderDuration = new Trend('confirm_order_duration', true);
export const shipOrderDuration = new Trend('ship_order_duration', true);
export const deliverOrderDuration = new Trend('deliver_order_duration', true);
export const getOrderDuration = new Trend('get_order_duration', true);
export const getRecentOrdersDuration = new Trend('get_recent_orders_duration', true);
export const getOrdersOverviewDuration = new Trend('get_orders_overview_duration', true);

/**
 * Business metrics.
 */
export const activeOrders = new Gauge('active_orders');

/**
 * Projection consistency metrics.
 * Used to verify async processing and IFlushable implementation.
 */
export const projectionLatency = new Trend('projection_latency_ms', true);
export const projectionConsistencyFailures = new Counter('projection_consistency_failures');

/**
 * Throughput metrics.
 * Used to measure maximum sustainable event throughput.
 */
export const eventsCreated = new Counter('events_created_total');
export const eventCreationDuration = new Trend('event_creation_duration_ms', true);
export const ordersPerSecond = new Gauge('orders_per_second');
export const eventsPerSecond = new Gauge('events_per_second');
export const throughputIterations = new Counter('throughput_iterations_total');

/**
 * Seeding metrics.
 * Used to track database pre-population before throughput tests.
 */
export const eventsSeeded = new Counter('events_seeded_total');
export const seedingDuration = new Trend('seeding_duration_ms', true);

/**
 * Record a GraphQL operation result.
 */
export function recordOperation(success: boolean, duration: number, operationType: string): void {
  graphqlRequests.add(1);

  if (!success) {
    graphqlErrors.add(1);
    graphqlErrorRate.add(1);
  } else {
    graphqlErrorRate.add(0);
  }

  // Record to operation-specific metric
  switch (operationType) {
    case 'CreateOrder':
      createOrderDuration.add(duration);
      break;
    case 'ConfirmOrder':
      confirmOrderDuration.add(duration);
      break;
    case 'ShipOrder':
      shipOrderDuration.add(duration);
      break;
    case 'DeliverOrder':
      deliverOrderDuration.add(duration);
      break;
    case 'GetOrder':
      getOrderDuration.add(duration);
      break;
    case 'GetRecentOrders':
      getRecentOrdersDuration.add(duration);
      break;
    case 'GetOrdersOverview':
      getOrdersOverviewDuration.add(duration);
      break;
  }
}
