/**
 * SLA thresholds for load tests.
 * These define the performance criteria that must be met.
 */

export const thresholds = {
  // Overall HTTP metrics
  http_req_duration: [
    'p(50)<200',   // 50% of requests under 200ms
    'p(95)<500',   // 95% of requests under 500ms
    'p(99)<1000',  // 99% of requests under 1s
  ],
  http_req_failed: ['rate<0.01'], // Error rate < 1%

  // Mutation-specific thresholds (tagged metrics)
  'http_req_duration{operation:CreateOrder}': ['p(95)<300'],
  'http_req_duration{operation:ConfirmOrder}': ['p(95)<250'],
  'http_req_duration{operation:ShipOrder}': ['p(95)<250'],
  'http_req_duration{operation:DeliverOrder}': ['p(95)<250'],
  'http_req_duration{operation:AddOrderItem}': ['p(95)<250'],
  'http_req_duration{operation:RemoveOrderItem}': ['p(95)<250'],
  'http_req_duration{operation:CancelOrder}': ['p(95)<250'],

  // Query-specific thresholds
  'http_req_duration{operation:GetOrder}': ['p(95)<150'],
  'http_req_duration{operation:GetRecentOrders}': ['p(95)<300'],
  'http_req_duration{operation:GetOrdersOverview}': ['p(95)<200'],

  // Custom metrics
  order_lifecycle_duration: ['p(95)<3000'], // Full lifecycle under 3s (includes ~2s of intentional sleeps)
  orders_created: ['count>0'], // At least some orders created
};

// Relaxed thresholds for stress testing
export const stressThresholds = {
  ...thresholds,
  http_req_duration: ['p(99)<2000'], // Relaxed for stress
  http_req_failed: ['rate<0.05'],    // Allow 5% errors under stress
};

// Relaxed thresholds for spike testing
export const spikeThresholds = {
  ...thresholds,
  http_req_failed: ['rate<0.10'], // Allow 10% errors during spike
};

// Throughput test thresholds - focused on measuring max throughput
// Relaxed latency requirements to find ceiling
export const throughputThresholds = {
  http_req_failed: ['rate<0.05'],           // Allow 5% errors at max throughput
  http_req_duration: ['p(95)<2000'],         // Relaxed latency for throughput focus
  events_created_total: ['count>1000'],      // Ensure meaningful event volume
  throughput_iterations_total: ['count>100'], // Ensure test ran enough iterations
};
