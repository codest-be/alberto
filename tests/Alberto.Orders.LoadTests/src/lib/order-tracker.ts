import { Counter, Trend } from 'k6/metrics';

/**
 * Custom metrics for order tracking.
 */
export const ordersCreated = new Counter('orders_created');
export const ordersConfirmed = new Counter('orders_confirmed');
export const ordersShipped = new Counter('orders_shipped');
export const ordersDelivered = new Counter('orders_delivered');
export const ordersCancelled = new Counter('orders_cancelled');
export const orderLifecycleDuration = new Trend('order_lifecycle_duration');

/**
 * Order status types.
 */
export type OrderStatus = 'draft' | 'confirmed' | 'shipped' | 'delivered' | 'cancelled';

/**
 * Tracked order state.
 */
export interface TrackedOrder {
  orderId: string;
  customerId: string;
  tenantId: string;
  createdAt: number;
  status: OrderStatus;
}

/**
 * Per-VU order storage.
 * Note: K6 VUs don't share memory, so each VU maintains its own list.
 */
let vuOrders: TrackedOrder[] = [];

/**
 * Track a newly created order.
 */
export function trackCreatedOrder(orderId: string, customerId: string, tenantId: string): TrackedOrder {
  const order: TrackedOrder = {
    orderId,
    customerId,
    tenantId,
    createdAt: Date.now(),
    status: 'draft',
  };
  vuOrders.push(order);
  ordersCreated.add(1);
  return order;
}

/**
 * Update order status and record metrics.
 */
export function updateOrderStatus(orderId: string, newStatus: OrderStatus): TrackedOrder | undefined {
  const order = vuOrders.find((o) => o.orderId === orderId);
  if (!order) {
    console.warn(`Order ${orderId} not found in tracker`);
    return undefined;
  }

  order.status = newStatus;

  switch (newStatus) {
    case 'confirmed':
      ordersConfirmed.add(1);
      break;
    case 'shipped':
      ordersShipped.add(1);
      break;
    case 'delivered':
      ordersDelivered.add(1);
      // Record full lifecycle duration
      orderLifecycleDuration.add(Date.now() - order.createdAt);
      break;
    case 'cancelled':
      ordersCancelled.add(1);
      break;
  }

  return order;
}

/**
 * Get a random order in a specific status.
 */
export function getOrderInStatus(status: OrderStatus): TrackedOrder | undefined {
  const matching = vuOrders.filter((o) => o.status === status);
  if (matching.length === 0) return undefined;
  return matching[Math.floor(Math.random() * matching.length)];
}

/**
 * Get the most recent draft order.
 */
export function getMostRecentDraftOrder(): TrackedOrder | undefined {
  const drafts = vuOrders.filter((o) => o.status === 'draft');
  return drafts.length > 0 ? drafts[drafts.length - 1] : undefined;
}

/**
 * Get a tracked order by ID.
 */
export function getTrackedOrder(orderId: string): TrackedOrder | undefined {
  return vuOrders.find((o) => o.orderId === orderId);
}

/**
 * Clear VU orders (useful in setup/teardown).
 */
export function clearVuOrders(): void {
  vuOrders = [];
}

/**
 * Get count of orders by status.
 */
export function getOrderCounts(): Record<OrderStatus, number> {
  const counts: Record<OrderStatus, number> = {
    draft: 0,
    confirmed: 0,
    shipped: 0,
    delivered: 0,
    cancelled: 0,
  };

  for (const order of vuOrders) {
    counts[order.status]++;
  }

  return counts;
}

/**
 * Get total number of tracked orders in this VU.
 */
export function getTotalTrackedOrders(): number {
  return vuOrders.length;
}
