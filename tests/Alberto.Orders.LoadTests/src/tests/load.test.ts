import { Options } from 'k6/options';
import { loadProfile } from '../config/load-profiles';
import { mixedWorkloadScenario } from '../scenarios/mixed-workload';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

export const options: Options = {
  ...loadProfile,
  tags: {
    testType: 'load',
  },
};

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();

  console.log('='.repeat(60));
  console.log('LOAD TEST - Normal Expected Load');
  console.log('='.repeat(60));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log('');
  console.log('Profile:');
  console.log('  - Ramp up to 10 VUs over 1m');
  console.log('  - Hold at 10 VUs for 5m');
  console.log('  - Ramp up to 20 VUs over 1m');
  console.log('  - Hold at 20 VUs for 5m');
  console.log('  - Ramp down over 2m');
  console.log('  - Total duration: ~14m');
  console.log('');
  console.log('Workload distribution:');
  console.log('  - 40% Read operations');
  console.log('  - 35% Complete order lifecycles');
  console.log('  - 15% Partial lifecycles');
  console.log('  - 10% Cancellations');
  console.log('='.repeat(60));

  resetTenantIndex();
}

export default function (): void {
  mixedWorkloadScenario();
}

export function teardown(): void {
  console.log('='.repeat(60));
  console.log('LOAD TEST COMPLETE');
  console.log('='.repeat(60));
}
