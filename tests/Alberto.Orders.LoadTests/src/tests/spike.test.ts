import { Options } from 'k6/options';
import { spikeProfile } from '../config/load-profiles';
import { mixedWorkloadScenario } from '../scenarios/mixed-workload';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

export const options: Options = {
  ...spikeProfile,
  tags: {
    testType: 'spike',
  },
};

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();

  console.log('='.repeat(60));
  console.log('SPIKE TEST - Sudden Traffic Burst');
  console.log('='.repeat(60));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log('');
  console.log('Profile:');
  console.log('  - 5 VUs for 30s (normal)');
  console.log('  - SPIKE to 100 VUs over 10s');
  console.log('  - Hold at 100 VUs for 1m');
  console.log('  - Drop to 5 VUs over 10s');
  console.log('  - Recovery period: 1m at 5 VUs');
  console.log('  - Ramp down over 30s');
  console.log('  - Total duration: ~4m');
  console.log('');
  console.log('Relaxed thresholds during spike:');
  console.log('  - Error rate < 10%');
  console.log('='.repeat(60));

  resetTenantIndex();
}

export default function (): void {
  mixedWorkloadScenario();
}

export function teardown(): void {
  console.log('='.repeat(60));
  console.log('SPIKE TEST COMPLETE');
  console.log('='.repeat(60));
}
