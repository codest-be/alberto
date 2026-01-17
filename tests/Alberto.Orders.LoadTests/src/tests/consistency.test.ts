import { Options } from 'k6/options';
import { projectionConsistencyScenario, projectionStalenessScenario } from '../scenarios/projection-consistency';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

/**
 * Consistency test profile: Validates projection consistency after mutations.
 * Purpose: Verify that async projections are correctly flushed (IFlushable fix).
 *
 * This test creates orders and verifies that projections reflect the changes
 * within an acceptable time window. It specifically targets:
 * - Fix 1: IFlushable implementation for projection persistence
 * - Fix 5: EfStateStore optimization for reduced DB queries
 */
export const options: Options = {
  scenarios: {
    consistency: {
      executor: 'constant-vus',
      vus: 5,
      duration: '2m',
      exec: 'consistencyCheck',
    },
    staleness: {
      executor: 'constant-vus',
      vus: 2,
      duration: '2m',
      exec: 'stalenessCheck',
      startTime: '10s', // Start slightly after consistency checks
    },
  },
  thresholds: {
    // Critical: No projection consistency failures allowed
    'projection_consistency_failures': ['count<1'],
    // Projection should be consistent within 600ms (p95) - includes 400ms wait + processing
    'projection_latency_ms': ['p(95)<700', 'p(99)<900'],
    // Standard HTTP checks
    'http_req_failed': ['rate<0.01'],
    'http_req_duration': ['p(95)<1000'],
    // GraphQL error rate should be near zero
    'graphql_error_rate': ['rate<0.01'],
  },
  tags: {
    testType: 'consistency',
  },
};

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();

  console.log('='.repeat(60));
  console.log('CONSISTENCY TEST - Projection Validation');
  console.log('='.repeat(60));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log(`Duration: 2m, VUs: 7 (5 consistency + 2 staleness)`);
  console.log('');
  console.log('Verifies:');
  console.log('  - IFlushable: Projections persist correctly after mutations');
  console.log('  - EfStateStore: Consistent reads without stale data');
  console.log('='.repeat(60));

  resetTenantIndex();
}

/**
 * Main consistency check scenario.
 * Creates orders and verifies projections reflect the changes.
 */
export function consistencyCheck(): void {
  projectionConsistencyScenario();
}

/**
 * Staleness detection scenario.
 * Queries projections multiple times to detect stale reads.
 */
export function stalenessCheck(): void {
  projectionStalenessScenario();
}

export function teardown(): void {
  console.log('='.repeat(60));
  console.log('CONSISTENCY TEST COMPLETE');
  console.log('');
  console.log('Check results:');
  console.log('  - projection_consistency_failures should be 0');
  console.log('  - projection_latency_ms p95 should be under 700ms');
  console.log('='.repeat(60));
}
