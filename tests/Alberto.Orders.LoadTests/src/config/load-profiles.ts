import { Options } from 'k6/options';
import { thresholds, stressThresholds, spikeThresholds } from './thresholds';

/**
 * Smoke test profile: Quick validation with minimal load.
 * Purpose: Verify the API is functional before running longer tests.
 */
export const smokeProfile: Options = {
  vus: 1,
  duration: '30s',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<1000'],
  },
};

/**
 * Load test profile: Normal expected load.
 * Purpose: Validate system behavior under typical traffic patterns.
 */
export const loadProfile: Options = {
  stages: [
    { duration: '1m', target: 10 },   // Ramp up to 10 users
    { duration: '5m', target: 10 },   // Stay at 10 users
    { duration: '1m', target: 20 },   // Ramp up to 20 users
    { duration: '5m', target: 20 },   // Stay at 20 users
    { duration: '2m', target: 0 },    // Ramp down
  ],
  thresholds,
};

/**
 * Stress test profile: Find the breaking point.
 * Purpose: Determine system capacity and identify failure modes.
 */
export const stressProfile: Options = {
  stages: [
    { duration: '2m', target: 10 },   // Below normal
    { duration: '2m', target: 25 },   // Normal load
    { duration: '2m', target: 50 },   // Around breaking point
    { duration: '2m', target: 75 },   // Beyond breaking point
    { duration: '2m', target: 100 },  // Well beyond
    { duration: '5m', target: 100 },  // Stay at max
    { duration: '2m', target: 0 },    // Recovery
  ],
  thresholds: stressThresholds,
};

/**
 * Spike test profile: Sudden traffic burst.
 * Purpose: Test system behavior under sudden load changes.
 */
export const spikeProfile: Options = {
  stages: [
    { duration: '30s', target: 5 },   // Normal traffic
    { duration: '10s', target: 100 }, // Spike!
    { duration: '1m', target: 100 },  // Stay at spike
    { duration: '10s', target: 5 },   // Back to normal
    { duration: '1m', target: 5 },    // Recovery period
    { duration: '30s', target: 0 },   // Ramp down
  ],
  thresholds: spikeThresholds,
};
