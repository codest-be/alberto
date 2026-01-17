/**
 * Multi-tenant configuration for load tests.
 * Uses predefined tenant IDs with round-robin distribution.
 */

// Predefined tenant IDs - configure these for your environment
// Using 10 tenants to stress test multi-tenant scenarios and tenant cache eviction (Fix 4)
const DEFAULT_TENANT_IDS = [
  'tenant-loadtest-001',
  'tenant-loadtest-002',
  'tenant-loadtest-003',
  'tenant-loadtest-004',
  'tenant-loadtest-005',
  'tenant-loadtest-006',
  'tenant-loadtest-007',
  'tenant-loadtest-008',
  'tenant-loadtest-009',
  'tenant-loadtest-010',
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
