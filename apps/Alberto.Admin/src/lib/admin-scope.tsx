/**
 * AdminScope — which event store the console is looking at, and, when that store
 * is multi-tenant, which tenant's rows it is showing.
 *
 * A host can run several Alberto modules in one process; the Orders example runs
 * `orders` and `payments`. They are separate event stores with separate schemas,
 * so a position, a checkpoint or a dead-letter count from one says nothing about
 * the other. That is why this is a switcher and not a merged view: there is no
 * honest way to add two event logs together, and a console that tried would be
 * showing a number that describes nothing.
 *
 * Every page reads `module` from here and passes it as the `module` variable on
 * its queries, mutations and subscriptions. Passing `null` is meaningful — the
 * server answers for its default module — so the console does not have to wait
 * for the module list before it can render anything.
 */
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";
import { useQuery } from "urql";

import { graphql, type ResultOf } from "@/lib/graphql";

// ── GraphQL documents ─────────────────────────────────────────────────────────

const AdminModulesQuery = graphql(`
  query AdminModules {
    adminModules {
      key
      schema
      isDefault
      tenancyMode
    }
  }
`);

const ScopeTenantsQuery = graphql(`
  query AdminScopeTenants($module: String) {
    adminTenants(module: $module)
  }
`);

export type AdminModule = ResultOf<typeof AdminModulesQuery>["adminModules"][0];

// ── Context ───────────────────────────────────────────────────────────────────

export interface AdminScope {
  /** Every module the server will answer for. Empty until the list loads. */
  modules: AdminModule[];
  /**
   * The selected module key, or `null` to let the server pick its default.
   * Pass this straight through as the `module` variable — `null` is a valid value.
   */
  module: string | null;
  /** The descriptor for whatever `module` resolves to, or null before the list loads. */
  current: AdminModule | null;
  selectModule: (key: string) => void;

  /** Whether the current module's schema carries tenant identity. */
  isMultiTenant: boolean;
  /** The selected tenant, or `null` for every tenant. Always null on a single-tenant module. */
  tenant: string | null;
  /**
   * Every tenant the current module's store has seen an event for.
   *
   * Read from the store's tenant catalog rather than its lease table: a lease
   * exists only while a consumer is actively processing a tenant, so a filter
   * built from leases would be empty on an idle store — which is exactly when
   * an operator is most likely to be looking.
   */
  tenants: string[];
  selectTenant: (tenant: string | null) => void;

  /** True while the module list is still in flight. */
  loading: boolean;
}

const AdminScopeContext = createContext<AdminScope | null>(null);

const MODULE_STORAGE_KEY = "alberto-admin.module";
const tenantStorageKey = (module: string | null) =>
  `alberto-admin.tenant.${module ?? "(default)"}`;

function readStored(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Private-browsing modes throw on localStorage access. A console that cannot
    // remember the last selection is fine; one that white-screens is not.
    return null;
  }
}

function writeStored(key: string, value: string | null): void {
  try {
    if (value === null) window.localStorage.removeItem(key);
    else window.localStorage.setItem(key, value);
  } catch {
    // See readStored.
  }
}

export function AdminScopeProvider({ children }: { children: React.ReactNode }) {
  const [module, setModule] = useState<string | null>(() =>
    readStored(MODULE_STORAGE_KEY)
  );
  const [tenant, setTenant] = useState<string | null>(() =>
    readStored(tenantStorageKey(readStored(MODULE_STORAGE_KEY)))
  );

  const [modulesResult] = useQuery({ query: AdminModulesQuery });
  const modules = useMemo(
    () => modulesResult.data?.adminModules ?? [],
    [modulesResult.data]
  );

  // A stored key for a module the server no longer registers would pin the whole
  // console to an error. Fall back to the default rather than stranding it.
  useEffect(() => {
    if (modules.length === 0 || module === null) return;
    if (modules.some((m) => m.key === module)) return;
    setModule(null);
    writeStored(MODULE_STORAGE_KEY, null);
  }, [modules, module]);

  const current = useMemo(
    () =>
      modules.find((m) => m.key === module) ??
      modules.find((m) => m.isDefault) ??
      null,
    [modules, module]
  );

  const isMultiTenant = current?.tenancyMode === "MULTI_TENANT";

  const [tenantsResult] = useQuery({
    query: ScopeTenantsQuery,
    variables: { module },
    pause: !isMultiTenant,
  });

  const tenants = useMemo(
    () => tenantsResult.data?.adminTenants ?? [],
    [tenantsResult.data]
  );

  const selectModule = useCallback(
    (key: string) => {
      setModule(key);
      writeStored(MODULE_STORAGE_KEY, key);
      // Tenant IDs belong to a module's own store. Carrying a selection across
      // would filter one store's rows by another store's tenant and show an
      // empty page that looks like a healthy, quiet system.
      const restored = readStored(tenantStorageKey(key));
      setTenant(restored);
    },
    []
  );

  const selectTenant = useCallback(
    (next: string | null) => {
      setTenant(next);
      writeStored(tenantStorageKey(module), next);
    },
    [module]
  );

  // A single-tenant module has no tenant column, and asking the server to filter
  // on one is an error rather than an empty result.
  const effectiveTenant = isMultiTenant ? tenant : null;

  const value = useMemo<AdminScope>(
    () => ({
      modules,
      module,
      current,
      selectModule,
      isMultiTenant,
      tenant: effectiveTenant,
      tenants,
      selectTenant,
      loading: modulesResult.fetching && modules.length === 0,
    }),
    [
      modules,
      module,
      current,
      selectModule,
      isMultiTenant,
      effectiveTenant,
      tenants,
      selectTenant,
      modulesResult.fetching,
    ]
  );

  return (
    <AdminScopeContext.Provider value={value}>
      {children}
    </AdminScopeContext.Provider>
  );
}

/** Reads the current module/tenant scope. Throws outside {@link AdminScopeProvider}. */
export function useAdminScope(): AdminScope {
  const scope = useContext(AdminScopeContext);
  if (scope === null) {
    throw new Error("useAdminScope must be used within an AdminScopeProvider");
  }
  return scope;
}
