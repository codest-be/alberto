/**
 * ScopeBar — the strip that answers "what am I looking at?".
 *
 * Two controls, both narrowing what every page below them shows:
 *
 *  - Module: which event store. Always visible, even with one module registered,
 *    because a console that only names the store when there are two of them
 *    teaches operators that the numbers are unqualified.
 *  - Tenant: which tenant's rows, and only on a module whose schema carries
 *    tenant identity. On a single-tenant module the control is replaced by a
 *    plain statement that there are no tenants, rather than being hidden — the
 *    absence of a filter should not be something you have to infer.
 */
import { Boxes, Users } from "lucide-react";

import { cn } from "@/lib/utils";
import { useAdminScope } from "@/lib/admin-scope";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

/** Radix Select has no value for "no selection", so the all-tenants row needs its own. */
const ALL_TENANTS = "__all__";

function ScopeLabel({ children }: { children: React.ReactNode }) {
  return (
    <span className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground shrink-0">
      {children}
    </span>
  );
}

export function ScopeBar({ className }: { className?: string }) {
  const {
    modules,
    current,
    selectModule,
    isMultiTenant,
    tenant,
    tenants,
    selectTenant,
    loading,
  } = useAdminScope();

  return (
    <div
      className={cn(
        "flex flex-wrap items-center gap-x-6 gap-y-3 border-b-2 border-border bg-background px-4 py-2.5",
        className
      )}
    >
      {/* Module */}
      <div className="flex items-center gap-2">
        <Boxes className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <ScopeLabel>Store</ScopeLabel>
        {loading ? (
          <Skeleton className="h-9 w-40" />
        ) : (
          <Select value={current?.key ?? ""} onValueChange={selectModule}>
            <SelectTrigger className="h-9 min-w-[10rem]" aria-label="Event store">
              <SelectValue placeholder="Select a module…" />
            </SelectTrigger>
            <SelectContent>
              {modules.map((m) => (
                <SelectItem key={m.key} value={m.key}>
                  <span className="font-mono">{m.key}</span>
                  {/* The schema is what an operator would check in psql, so it is
                      worth showing — but only when it differs from the key, which
                      it usually does not. */}
                  {m.schema !== m.key && (
                    <span className="ml-2 text-muted-foreground">{m.schema}</span>
                  )}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </div>

      {/* Tenant */}
      <div className="flex items-center gap-2">
        <Users className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        <ScopeLabel>Tenant</ScopeLabel>
        {loading || !current ? (
          // Also covers the failed-to-load case. Without `!current` the bar would
          // fall through to the single-tenant sentence below and state, as fact,
          // something it has no answer for.
          <Skeleton className="h-9 w-40" />
        ) : isMultiTenant ? (
          <Select
            value={tenant ?? ALL_TENANTS}
            onValueChange={(v) => selectTenant(v === ALL_TENANTS ? null : v)}
          >
            <SelectTrigger className="h-9 min-w-[12rem]" aria-label="Tenant filter">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL_TENANTS}>All tenants</SelectItem>
              {tenants.map((t) => (
                <SelectItem key={t} value={t}>
                  <span className="font-mono">{t}</span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        ) : (
          <span className="text-xs text-muted-foreground">
            Single-tenant store — no tenant column
          </span>
        )}
      </div>
    </div>
  );
}
