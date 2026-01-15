import { Options } from 'k6/options';
import { smokeProfile } from '../config/load-profiles';
import { orderLifecycleScenario } from '../scenarios/order-lifecycle';
import { readHeavyScenario } from '../scenarios/read-heavy';
import { getAllTenants, resetTenantIndex } from '../config/tenants';
import { getEnvironment } from '../config/environments';

export const options: Options = {
  ...smokeProfile,
  tags: {
    testType: 'smoke',
  },
};

export function setup(): void {
  const env = getEnvironment();
  const tenants = getAllTenants();

  console.log('='.repeat(60));
  console.log('SMOKE TEST - Quick API Validation');
  console.log('='.repeat(60));
  console.log(`Environment: ${__ENV.ENV || 'local'}`);
  console.log(`Base URL: ${env.baseUrl}`);
  console.log(`Tenants: ${tenants.join(', ')}`);
  console.log(`Duration: 30s, VUs: 1`);
  console.log('='.repeat(60));

  resetTenantIndex();
}

export default function (): void {
  // Alternate between lifecycle and read scenarios
  if (__ITER % 2 === 0) {
    orderLifecycleScenario();
  } else {
    readHeavyScenario();
  }
}

export function teardown(): void {
  console.log('='.repeat(60));
  console.log('SMOKE TEST COMPLETE');
  console.log('='.repeat(60));
}
