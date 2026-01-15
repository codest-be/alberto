import { sleep } from 'k6';
import { executeQuery, getData } from '../lib/graphql-client';
import { getNextTenant } from '../config/tenants';
import { randomIntBetween } from '../lib/data-generators';
import {
  GET_ORDER_QUERY,
  GetOrderResult,
  GET_ORDERS_OVERVIEW_QUERY,
  GetOrdersOverviewResult,
  GET_RECENT_ORDERS_QUERY,
  GetRecentOrdersResult,
} from '../graphql';

// Store some order IDs from getRecentOrders for targeted queries
let knownOrderIds: string[] = [];

/**
 * Read-heavy scenario focusing on query operations.
 * Simulates dashboard/reporting usage patterns.
 */
export function readHeavyScenario(): void {
  const tenantId = getNextTenant();

  // Query 1: Get orders overview (aggregated stats)
  executeQuery<GetOrdersOverviewResult>(
    GET_ORDERS_OVERVIEW_QUERY,
    undefined,
    { tenantId, tags: { scenario: 'read-heavy', queryType: 'overview' } }
  );

  sleep(0.2);

  // Query 2: Get recent orders
  const limit = randomIntBetween(10, 50);
  const recentResponse = executeQuery<GetRecentOrdersResult>(
    GET_RECENT_ORDERS_QUERY,
    { limit },
    { tenantId, tags: { scenario: 'read-heavy', queryType: 'recent' } }
  );

  // Cache some order IDs for individual queries
  const recentData = getData(recentResponse);
  if (recentData?.recentOrders && recentData.recentOrders.length > 0) {
    knownOrderIds = recentData.recentOrders.map((o) => o.orderId).slice(0, 20);
  }

  sleep(0.2);

  // Query 3: Get individual orders (simulate drilling down)
  if (knownOrderIds.length > 0) {
    const selectedId = knownOrderIds[randomIntBetween(0, knownOrderIds.length - 1)];
    executeQuery<GetOrderResult>(
      GET_ORDER_QUERY,
      { orderId: selectedId },
      { tenantId, tags: { scenario: 'read-heavy', queryType: 'detail' } }
    );
  }

  sleep(0.5);
}

/**
 * Dashboard refresh pattern - simulates periodic dashboard updates.
 */
export function dashboardRefreshScenario(): void {
  const tenantId = getNextTenant();

  // Overview stats
  executeQuery<GetOrdersOverviewResult>(
    GET_ORDERS_OVERVIEW_QUERY,
    undefined,
    { tenantId, tags: { scenario: 'dashboard' } }
  );

  // Recent orders list
  executeQuery<GetRecentOrdersResult>(
    GET_RECENT_ORDERS_QUERY,
    { limit: 20 },
    { tenantId, tags: { scenario: 'dashboard' } }
  );

  sleep(5); // Dashboard refresh interval
}

/**
 * Order detail lookup pattern - simulates looking up specific orders.
 */
export function orderLookupScenario(orderIds: string[]): void {
  if (orderIds.length === 0) {
    console.warn('No order IDs provided for lookup scenario');
    return;
  }

  const tenantId = getNextTenant();
  const orderId = orderIds[randomIntBetween(0, orderIds.length - 1)];

  executeQuery<GetOrderResult>(
    GET_ORDER_QUERY,
    { orderId },
    { tenantId, tags: { scenario: 'order-lookup' } }
  );

  sleep(0.3);
}
