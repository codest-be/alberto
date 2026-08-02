/**
 * Multi-tenant configuration for load tests.
 * Uses predefined tenant IDs with round-robin distribution.
 */

// Predefined tenant IDs - configure these for your environment
// Using 10 tenants to stress test multi-tenant scenarios and tenant cache eviction (Fix 4)
//
// These must match TenantContext's rule, ^[a-z][a-z0-9_]{0,62}$ — underscores, never hyphens.
// The same allowlist governs schema names, so the API rejects anything outside it with
// INVALID_TENANT before a request reaches a resolver. An earlier hyphenated set here failed
// every request in every scenario.
const DEFAULT_TENANT_IDS = [
  'tenant_loadtest_001',
  'tenant_loadtest_002',
  'tenant_loadtest_003',
  'tenant_loadtest_004',
  'tenant_loadtest_005',
  'tenant_loadtest_006',
  'tenant_loadtest_007',
  'tenant_loadtest_008',
  'tenant_loadtest_009',
  'tenant_loadtest_010',
];

// Allow override via environment variable (comma-separated)
function getTenantIds(): string[] {
  const envTenants = __ENV.TENANT_IDS;
  if (envTenants) {
    return envTenants.split(',').map((t) => t.trim()).filter((t) => t.length > 0);
  }
  return DEFAULT_TENANT_IDS;
}

const TENANT_IDS = getTenantIds();

// Per-VU tenant index for round-robin distribution
let tenantIndex = 0;

/**
 * Get the next tenant ID using round-robin distribution.
 * Each call returns the next tenant in the list.
 */
export function getNextTenant(): string {
  const tenant = TENANT_IDS[tenantIndex % TENANT_IDS.length];
  tenantIndex++;
  return tenant;
}

/**
 * Get a specific tenant by index (useful for VU-based assignment).
 */
export function getTenantForVu(vuId: number): string {
  return TENANT_IDS[vuId % TENANT_IDS.length];
}

/**
 * Get all configured tenant IDs.
 */
export function getAllTenants(): string[] {
  return [...TENANT_IDS];
}

/**
 * Get the current tenant count.
 */
export function getTenantCount(): number {
  return TENANT_IDS.length;
}

/**
 * Reset the round-robin index (useful for test setup).
 */
export function resetTenantIndex(): void {
  tenantIndex = 0;
}
