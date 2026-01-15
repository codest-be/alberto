import { Options } from 'k6/options';
import { stressProfile } from '../config/load-profiles';
import { mixedWorkloadScenario } from '../scenarios/mixed-workload';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

export const options: Options = {
  ...stressProfile,
  tags: {
    testType: 'stress',
  },
};

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();

  console.log('='.repeat(60));
  console.log('STRESS TEST - Find Breaking Point');
  console.log('='.repeat(60));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log('');
  console.log('WARNING: This test may cause degraded performance!');
  console.log('');
  console.log('Profile:');
  console.log('  - 10 VUs for 2m (baseline)');
  console.log('  - 25 VUs for 2m (normal)');
  console.log('  - 50 VUs for 2m (high)');
  console.log('  - 75 VUs for 2m (stress)');
  console.log('  - 100 VUs for 7m (maximum)');
  console.log('  - Ramp down over 2m');
  console.log('  - Total duration: ~17m');
  console.log('');
  console.log('Relaxed thresholds:');
  console.log('  - p99 latency < 2000ms');
  console.log('  - Error rate < 5%');
  console.log('='.repeat(60));

  resetTenantIndex();
}

export default function (): void {
  mixedWorkloadScenario();
}

export function teardown(): void {
  console.log('='.repeat(60));
  console.log('STRESS TEST COMPLETE');
  console.log('='.repeat(60));
}
