import { Options } from 'k6/options';
import { throughputScenario, extendedThroughputScenario, burstWriteScenario } from '../scenarios/throughput-focused';
import { seedOrderScenario } from '../scenarios/seed-data';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

/**
 * Throughput Test with Pre-Seeded Data
 *
 * This test measures maximum sustainable events/second against a database
 * that has been pre-populated with existing data, simulating production
 * conditions where:
 * - Streams have existing events (order history)
 * - DCB consistency checks query inverted indexes with more entries
 * - The database has more data to manage (indexes, WAL, etc.)
 *
 * Phases:
 * 1. Seeding: Create orders via parallel seeding scenario
 * 2. Throughput (~12.5m): Measure events/sec with populated database
 *
 * The seeding phase uses high parallelism (50 VUs) with shared-iterations
 * to populate the database as quickly as possible before measuring throughput.
 *
 * IMPORTANT: Set SEED_WAIT_TIME based on expected seeding duration.
 * Default is 10m. If seeding completes faster, throughput waits for the full duration.
 * If seeding takes longer, increase SEED_WAIT_TIME accordingly.
 *
 * Scenarios available for throughput phase (select via SCENARIO env var):
 * - "standard" (default): Create + Confirm (2 events per iteration)
 * - "extended": Create + Confirm + Ship + Deliver (4 events per iteration)
 * - "burst": Create only (1 event per iteration, fastest)
 *
 * Environment variables:
 *   SKIP_SEEDING=true     - Skip seeding (for re-runs against already seeded database)
 *   SEED_ITERATIONS=N     - Number of orders to create during seeding (default: 100000)
 *   SEED_VUS=N            - Number of VUs for seeding (default: 50)
 *   SEED_WAIT_TIME=Xm     - Time to wait before starting throughput (default: 10m)
 *   SCENARIO=type         - Throughput scenario: standard, extended, or burst
 *
 * Example:
 *   k6 run --env SCENARIO=burst --env SEED_WAIT_TIME=5m dist/throughput.test.js
 */

// Configuration
const SEED_ITERATIONS = parseInt(__ENV.SEED_ITERATIONS || '100000', 10);
const SEED_VUS = parseInt(__ENV.SEED_VUS || '50', 10);
const SEED_WAIT_TIME = __ENV.SEED_WAIT_TIME || '10m';
const SKIP_SEEDING = __ENV.SKIP_SEEDING === 'true';

// Build scenarios dynamically based on SKIP_SEEDING
const scenarios: Options['scenarios'] = {};

if (!SKIP_SEEDING) {
  scenarios.seeding = {
    executor: 'shared-iterations',
    vus: SEED_VUS,
    iterations: SEED_ITERATIONS,
    maxDuration: '60m', // Safety limit only - scenario ends when iterations complete
    gracefulStop: '0s', // End immediately when all iterations complete
    exec: 'seedData',
    tags: { phase: 'seeding' },
  };

  scenarios.throughput = {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '30s', target: 10 },   // Warm up
      { duration: '2m', target: 50 },    // Low load
      { duration: '2m', target: 100 },   // Medium load
      { duration: '2m', target: 150 },   // High load
      { duration: '3m', target: 200 },   // Push to saturation
      { duration: '2m', target: 200 },   // Sustain max
      { duration: '1m', target: 0 },     // Ramp down
    ],
    startTime: SEED_WAIT_TIME,
    exec: 'default',
    tags: { phase: 'throughput' },
  };
} else {
  scenarios.throughput = {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '30s', target: 10 },   // Warm up
      { duration: '2m', target: 50 },    // Low load
      { duration: '2m', target: 100 },   // Medium load
      { duration: '2m', target: 150 },   // High load
      { duration: '3m', target: 200 },   // Push to saturation
      { duration: '2m', target: 200 },   // Sustain max
      { duration: '1m', target: 0 },     // Ramp down
    ],
    startTime: '0s',
    exec: 'default',
    tags: { phase: 'throughput' },
  };
}

export const options: Options = {
  scenarios,
  thresholds: {
    // Seeding phase thresholds
    'http_req_failed{phase:seeding}': SKIP_SEEDING ? [] : ['rate<0.10'],
    'events_seeded_total': SKIP_SEEDING ? [] : ['count>350000'],

    // Throughput phase thresholds
    'http_req_failed{phase:throughput}': ['rate<0.05'],
    'http_req_duration{phase:throughput}': ['p(95)<2000'],
    'events_created_total': ['count>1000'],
    'throughput_iterations_total': ['count>100'],
  },
  tags: {
    testType: 'throughput-seeded',
  },
};

// Track start times for each phase
let seedingStartTime: number;
let throughputStartTime: number;
let seedingIterationsCompleted = 0;

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();
  const scenarioType = __ENV.SCENARIO || 'standard';

  console.log('='.repeat(70));
  console.log('THROUGHPUT TEST WITH PRE-SEEDED DATA');
  console.log('='.repeat(70));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log(`Scenario: ${scenarioType}`);
  console.log('');

  if (!SKIP_SEEDING) {
    console.log('PHASE 1: SEEDING');
    console.log('-'.repeat(70));
    console.log(`  Target: ${SEED_ITERATIONS.toLocaleString()} orders (${(SEED_ITERATIONS * 4).toLocaleString()} events)`);
    console.log(`  VUs: ${SEED_VUS}`);
    console.log(`  Wait time before throughput: ${SEED_WAIT_TIME}`);
    console.log('');
    console.log('  TIP: Adjust SEED_WAIT_TIME to match expected seeding duration');
    console.log('       k6 run --env SEED_WAIT_TIME=5m dist/throughput.test.js');
    console.log('');
  } else {
    console.log('SEEDING SKIPPED (SKIP_SEEDING=true)');
    console.log('');
  }

  console.log('PHASE 2: THROUGHPUT MEASUREMENT');
  console.log('-'.repeat(70));
  console.log('Scenario Details:');
  switch (scenarioType) {
    case 'burst':
      console.log('  - Burst write: Create orders only (1 event/iteration)');
      console.log('  - Fastest scenario for pure event creation');
      break;
    case 'extended':
      console.log('  - Extended lifecycle: Create -> Confirm -> Ship -> Deliver');
      console.log('  - 4 events per iteration');
      break;
    default:
      console.log('  - Standard: Create -> Confirm (2 events/iteration)');
      break;
  }
  console.log('');
  console.log('Profile (~12.5m):');
  console.log('  - 10 VUs for 30s (warm up)');
  console.log('  - 50 VUs for 2m (low load)');
  console.log('  - 100 VUs for 2m (medium load)');
  console.log('  - 150 VUs for 2m (high load)');
  console.log('  - 200 VUs for 3m (saturation)');
  console.log('  - 200 VUs for 2m (sustain)');
  console.log('  - Ramp down over 1m');
  console.log('');
  console.log('Key Metrics to Watch:');
  console.log('  - events_seeded_total: Events created during seeding');
  console.log('  - events_created_total: Events created during throughput test');
  console.log('  - event_creation_duration_ms: Latency per event');
  console.log('  - http_req_failed{phase:throughput}: Error rate (target < 5%)');
  console.log('');
  console.log('This test simulates production conditions where streams have');
  console.log('existing events and indexes contain historical data.');
  console.log('='.repeat(70));

  seedingStartTime = Date.now();
  resetTenantIndex();
}

/**
 * Seeding function - creates full order lifecycles.
 * Called by the seeding scenario.
 */
export function seedData(): void {
  seedOrderScenario();

  seedingIterationsCompleted++;

  // Log progress every 10000 iterations
  if (seedingIterationsCompleted % 10000 === 0) {
    const elapsedMin = (Date.now() - seedingStartTime) / 1000 / 60;
    console.log(`Seeding: ${seedingIterationsCompleted.toLocaleString()} / ${SEED_ITERATIONS.toLocaleString()} (${elapsedMin.toFixed(1)}m elapsed)`);
  }
}

/**
 * Default throughput function - runs selected scenario.
 * Called by the throughput scenario.
 */
export default function (): void {
  // Track throughput phase start on first iteration
  if (!throughputStartTime) {
    throughputStartTime = Date.now();
    if (!SKIP_SEEDING) {
      const seedingDurationMs = throughputStartTime - seedingStartTime;
      console.log(`Starting throughput measurement phase (${(seedingDurationMs / 1000 / 60).toFixed(1)}m after test start)`);
    }
  }

  const scenario = __ENV.SCENARIO || 'standard';

  switch (scenario) {
    case 'burst':
      burstWriteScenario();
      break;
    case 'extended':
      extendedThroughputScenario();
      break;
    default:
      throughputScenario();
      break;
  }
}

export function teardown(): void {
  const totalDurationMs = Date.now() - seedingStartTime;
  const totalDurationSec = totalDurationMs / 1000;

  console.log('='.repeat(70));
  console.log('THROUGHPUT TEST COMPLETE');
  console.log('='.repeat(70));
  console.log(`Total test duration: ${(totalDurationSec / 60).toFixed(1)} minutes`);
  console.log('');
  console.log('Check the K6 summary for:');
  if (!SKIP_SEEDING) {
    console.log('  SEEDING PHASE:');
    console.log('    - events_seeded_total: Total events created during seeding');
    console.log('    - seeding_duration_ms: Time per order lifecycle');
    console.log('');
  }
  console.log('  THROUGHPUT PHASE:');
  console.log('    - events_created_total: Events created during throughput test');
  console.log('    - event_creation_duration_ms p95: Event latency');
  console.log('    - http_req_failed{phase:throughput}: Error percentage');
  console.log('');
  console.log('Compare events_created_total / throughput_duration with empty');
  console.log('database results to see impact of pre-seeded data.');
  console.log('='.repeat(70));
}
