import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const orderCreationTime = new Trend('order_creation_time');
const orderPlacementTime = new Trend('order_placement_time');
const paymentCreationTime = new Trend('payment_creation_time');
const paymentProcessingTime = new Trend('payment_processing_time');
const orderShippingTime = new Trend('order_shipping_time');
const fullLifecycleTime = new Trend('full_lifecycle_time');
const lifecycleSuccessRate = new Rate('lifecycle_success_rate');
const lifecycleCompletions = new Counter('lifecycle_completions');

// Load profiles
const profiles = {
  smoke: {
    stages: [
      { duration: '1m', target: 10 },
    ],
    thresholds: {
      'http_req_duration': ['p(95)<1000', 'p(99)<2000'],
      'http_req_failed': ['rate<0.01'],
      'lifecycle_success_rate': ['rate>0.95'],
    },
  },
  load: {
    stages: [
      { duration: '2m', target: 50 },
      { duration: '5m', target: 100 },
      { duration: '2m', target: 50 },
      { duration: '1m', target: 0 },
    ],
    thresholds: {
      'http_req_duration': ['p(95)<1000', 'p(99)<2000'],
      'http_req_failed': ['rate<0.01'],
      'lifecycle_success_rate': ['rate>0.95'],
      'full_lifecycle_time': ['p(95)<3000'],
    },
  },
  stress: {
    stages: [
      { duration: '2m', target: 50 },   // Warm up
      { duration: '3m', target: 100 },  // Sustained load
      { duration: '3m', target: 150 },  // Push to limits
      { duration: '2m', target: 100 },  // Cool down
      { duration: '2m', target: 0 },    // Ramp down
    ],
    thresholds: {
      'http_req_duration': ['p(95)<2000', 'p(99)<5000'],
      'http_req_failed': ['rate<0.05'],
      'lifecycle_success_rate': ['rate>0.90'],
    },
  },
  breakpoint: {
    stages: [
      { duration: '2m', target: 50 },    // Baseline - establish normal performance
      { duration: '2m', target: 100 },   // 2x - should be comfortable with pooling
      { duration: '2m', target: 150 },   // 3x - within stress test limits
      { duration: '2m', target: 200 },   // 4x - previous breaking point (no pooling)
      { duration: '2m', target: 250 },   // 5x - pushing beyond old limits
      { duration: '2m', target: 300 },   // 6x - likely breaking point
      { duration: '2m', target: 0 },     // Ramp down and observe recovery
    ],
    thresholds: {
      'http_req_duration': ['p(95)<5000'],      // Very lenient - expect slowdowns
      'http_req_failed': ['rate<0.50'],         // Allow 50% failures - we want to break it
      'lifecycle_success_rate': ['rate>0.30'],  // Only 30% success needed - finding limits
    },
  },
};

// Get configuration from environment
const testProfile = __ENV.TEST_PROFILE || 'smoke';
const baseUrl = __ENV.BASE_URL || 'http://localhost:5000';

// Apply selected profile
const selectedProfile = profiles[testProfile as keyof typeof profiles] || profiles.smoke;

export const options = {
  stages: selectedProfile.stages,
  thresholds: selectedProfile.thresholds,
};

// Helper function to generate random data
function generateTestData() {
  const randomId = Math.floor(Math.random() * 1000000);
  return {
    amount: Math.floor(Math.random() * 90000) + 10000, // $100 - $1000
    customerId: `customer-${randomId}`,
    trackingNumber: `TRACK-${randomId}`,
  };
}

// Main test scenario: Complete order lifecycle
export default function () {
  const testData = generateTestData();
  const lifecycleStartTime = Date.now();
  let lifecycleSuccess = false;

  try {
    // Step 1: Create Order
    const createOrderStart = Date.now();
    const createOrderRes = http.post(
      `${baseUrl}/orders`,
      JSON.stringify({
        amount: testData.amount,
        customerId: testData.customerId,
      }),
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'CreateOrder' },
      }
    );

    orderCreationTime.add(Date.now() - createOrderStart);

    const createOrderSuccess = check(createOrderRes, {
      'create order status is 200': (r) => r.status === 200,
      'create order has orderId': (r) => {
        try {
          const orderId = JSON.parse(r.body as string);
          return typeof orderId === 'string' && orderId.length > 0;
        } catch {
          return false;
        }
      },
    });

    if (!createOrderSuccess) {
      console.error(`Failed to create order: ${createOrderRes.status} - ${createOrderRes.body}`);
      return;
    }

    const orderId = JSON.parse(createOrderRes.body as string);
    sleep(0.5);

    // Step 2: Place Order
    const placeOrderStart = Date.now();
    const placeOrderRes = http.post(
      `${baseUrl}/orders/${orderId}/place`,
      null,
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'PlaceOrder' },
      }
    );

    orderPlacementTime.add(Date.now() - placeOrderStart);

    const placeOrderSuccess = check(placeOrderRes, {
      'place order status is 200': (r) => r.status === 200,
    });

    if (!placeOrderSuccess) {
      console.error(`Failed to place order ${orderId}: ${placeOrderRes.status} - ${placeOrderRes.body}`);
      return;
    }

    sleep(0.5);

    // Step 3: Create Payment
    const createPaymentStart = Date.now();
    const createPaymentRes = http.post(
      `${baseUrl}/payments`,
      JSON.stringify({
        orderId: orderId,
        amount: testData.amount,
      }),
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'CreatePayment' },
      }
    );

    paymentCreationTime.add(Date.now() - createPaymentStart);

    const createPaymentSuccess = check(createPaymentRes, {
      'create payment status is 200': (r) => r.status === 200,
      'create payment has paymentId': (r) => {
        try {
          const paymentId = JSON.parse(r.body as string);
          return typeof paymentId === 'string' && paymentId.length > 0;
        } catch {
          return false;
        }
      },
    });

    if (!createPaymentSuccess) {
      console.error(`Failed to create payment: ${createPaymentRes.status} - ${createPaymentRes.body}`);
      return;
    }

    const paymentId = JSON.parse(createPaymentRes.body as string);
    sleep(0.5);

    // Step 4: Process Payment
    const processPaymentStart = Date.now();
    const processPaymentRes = http.post(
      `${baseUrl}/payments/${paymentId}/process`,
      null,
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'ProcessPayment' },
      }
    );

    paymentProcessingTime.add(Date.now() - processPaymentStart);

    const processPaymentSuccess = check(processPaymentRes, {
      'process payment status is 200': (r) => r.status === 200,
    });

    if (!processPaymentSuccess) {
      console.error(`Failed to process payment ${paymentId}: ${processPaymentRes.status} - ${processPaymentRes.body}`);
      return;
    }

    sleep(0.5);

    // Step 5: Ship Order
    const shipOrderStart = Date.now();
    const shipOrderRes = http.post(
      `${baseUrl}/orders/${orderId}/ship`,
      JSON.stringify({
        trackingNumber: testData.trackingNumber,
      }),
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { name: 'ShipOrder' },
      }
    );

    orderShippingTime.add(Date.now() - shipOrderStart);

    const shipOrderSuccess = check(shipOrderRes, {
      'ship order status is 200': (r) => r.status === 200,
    });

    if (!shipOrderSuccess) {
      console.error(`Failed to ship order ${orderId}: ${shipOrderRes.status} - ${shipOrderRes.body}`);
      return;
    }

    // Success! Full lifecycle completed
    lifecycleSuccess = true;
    lifecycleCompletions.add(1);
    fullLifecycleTime.add(Date.now() - lifecycleStartTime);

  } catch (error) {
    console.error(`Lifecycle error: ${error}`);
  } finally {
    lifecycleSuccessRate.add(lifecycleSuccess);
    sleep(1);
  }
}
