import { useState, useRef, useCallback, type ChangeEvent } from "react";
import { useQuery } from "urql";
import { graphql } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import type { ResultOf } from "@/lib/graphql";
import { PageHeader } from "@/components/ui/page-header";
import { DataTable } from "@/components/ui/data-table";
import type { ColumnDef } from "@/components/ui/data-table";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";

// ---------------------------------------------------------------------------
// GraphQL documents
// ---------------------------------------------------------------------------

const ProjectionTypesQuery = graphql(`
  query ProjectionTypes($module: String) {
    adminProjectionTypes(module: $module)
  }
`);

const ProjectionStatesQuery = graphql(`
  query ProjectionStates(
    $projectionType: String!
    $tenant: String
    $search: String
    $module: String
    $limit: Int! = 100
  ) {
    adminProjectionStates(
      projectionType: $projectionType
      tenant: $tenant
      search: $search
      module: $module
      limit: $limit
    ) {
      documentId
      tenantId
      updatedAt
    }
  }
`);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type ProjectionStateRow = ResultOf<
  typeof ProjectionStatesQuery
>["adminProjectionStates"][number];

// ---------------------------------------------------------------------------
// Small presentational helpers
// ---------------------------------------------------------------------------

function formatRelativeTime(iso: string): string {
  const date = new Date(iso);
  const diffSec = (date.getTime() - Date.now()) / 1000;
  const abs = Math.abs(diffSec);
  const rtf = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  if (abs < 60) return rtf.format(Math.round(diffSec), "second");
  if (abs < 3600) return rtf.format(Math.round(diffSec / 60), "minute");
  if (abs < 86400) return rtf.format(Math.round(diffSec / 3600), "hour");
  return rtf.format(Math.round(diffSec / 86400), "day");
}

function TimestampCell({ iso }: { iso: string }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="cursor-default tabular-nums">
          {formatRelativeTime(iso)}
        </span>
      </TooltipTrigger>
      <TooltipContent>{new Date(iso).toLocaleString()}</TooltipContent>
    </Tooltip>
  );
}

function TenantCell({ tenantId }: { tenantId: string | null }) {
  if (!tenantId) return <span className="text-muted-foreground">—</span>;
  if (tenantId === "*") {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <Badge variant="secondary" className="cursor-default">
            * cross-tenant
          </Badge>
        </TooltipTrigger>
        <TooltipContent>
          Cross-tenant aggregate — single blended document across all tenants.
          Not a wildcard — this specific document spans all tenants.
        </TooltipContent>
      </Tooltip>
    );
  }
  return <span className="font-mono text-xs">{tenantId}</span>;
}

// ---------------------------------------------------------------------------
// Column definitions (stable reference — defined outside the component)
// ---------------------------------------------------------------------------

const COLUMNS: ColumnDef<ProjectionStateRow>[] = [
  {
    key: "documentId",
    header: "Document ID",
    cell: (row) => (
      <span className="font-mono text-xs break-all">{row.documentId}</span>
    ),
  },
  {
    key: "tenantId",
    header: "Tenant",
    cell: (row) => <TenantCell tenantId={row.tenantId ?? null} />,
  },
  {
    key: "updatedAt",
    header: "Last Updated",
    cell: (row) =>
      row.updatedAt ? (
        <TimestampCell iso={row.updatedAt} />
      ) : (
        <span className="text-muted-foreground">—</span>
      ),
  },
];

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function ProjectionsPage() {
  // Projection types are declared per module, so the type list itself changes
  // with the store. A scope tenant, when one is picked, overrides the free-text
  // box below: the box can still reach a tenant that holds no lease, but it
  // must not silently contradict the scope shown at the top of the screen.
  const { module, tenant: scopeTenant, isMultiTenant } = useAdminScope();

  // Type selector
  const [selectedType, setSelectedType] = useState<string | undefined>(
    undefined
  );

  // Filter inputs — two pairs: immediate (bound to input) and debounced (sent to query)
  const [tenantInput, setTenantInput] = useState("");
  const [searchInput, setSearchInput] = useState("");
  const [tenantFilter, setTenantFilter] = useState<string | undefined>(
    undefined
  );
  const [searchFilter, setSearchFilter] = useState<string | undefined>(
    undefined
  );

  // Detail sheet
  const [selectedRow, setSelectedRow] = useState<ProjectionStateRow | null>(
    null
  );

  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // ---------------------------------------------------------------------------
  // Queries
  // ---------------------------------------------------------------------------

  const [typesResult, refetchTypes] = useQuery({
    query: ProjectionTypesQuery,
    variables: { module },
  });

  const effectiveTenant = scopeTenant ?? tenantFilter ?? undefined;

  const [statesResult, refetchStates] = useQuery({
    query: ProjectionStatesQuery,
    variables: {
      projectionType: selectedType ?? "",
      tenant: effectiveTenant,
      search: searchFilter || undefined,
      module,
      limit: 100,
    },
    // Do not run the query until a type is selected — projectionType is non-nullable
    pause: !selectedType,
  });

  // ---------------------------------------------------------------------------
  // Handlers
  // ---------------------------------------------------------------------------

  const handleTypeChange = useCallback((value: string) => {
    setSelectedType(value);
    // Reset filters when type changes
    setTenantInput("");
    setSearchInput("");
    setTenantFilter(undefined);
    setSearchFilter(undefined);
  }, []);

  const scheduleDebounce = useCallback(
    (tenant: string, search: string) => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => {
        setTenantFilter(tenant || undefined);
        setSearchFilter(search || undefined);
      }, 400);
    },
    []
  );

  const handleTenantChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value;
      setTenantInput(value);
      scheduleDebounce(value, searchInput);
    },
    [scheduleDebounce, searchInput]
  );

  const handleSearchChange = useCallback(
    (e: ChangeEvent<HTMLInputElement>) => {
      const value = e.target.value;
      setSearchInput(value);
      scheduleDebounce(tenantInput, value);
    },
    [scheduleDebounce, tenantInput]
  );

  // ---------------------------------------------------------------------------
  // Derived state
  // ---------------------------------------------------------------------------

  const projectionTypes = typesResult.data?.adminProjectionTypes ?? [];
  const statesData = statesResult.error
    ? undefined
    : statesResult.data?.adminProjectionStates;

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  return (
    <TooltipProvider>
      <div className="p-6">
        <PageHeader
          title="Projections"
          eyebrow="Read Models"
          description="Inspect projection state documents built from the event log."
        />

        {/* ------------------------------------------------------------------ */}
        {/* Controls                                                            */}
        {/* ------------------------------------------------------------------ */}
        <div className="mb-4 flex flex-wrap gap-3 items-end">
          {/* Type selector */}
          <div className="flex flex-col gap-1 min-w-[260px]">
            <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
              Projection Type
            </label>

            {typesResult.fetching && !typesResult.data ? (
              <Skeleton className="h-9 w-full" />
            ) : typesResult.error ? (
              <div className="flex items-center gap-2">
                <span className="text-sm text-destructive">
                  Failed to load types.
                </span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    refetchTypes({ requestPolicy: "network-only" })
                  }
                >
                  Retry
                </Button>
              </div>
            ) : (
              <Select
                value={selectedType ?? ""}
                onValueChange={handleTypeChange}
              >
                <SelectTrigger>
                  <SelectValue placeholder="Select a projection type…" />
                </SelectTrigger>
                <SelectContent>
                  {projectionTypes.length === 0 ? (
                    <SelectItem value="__none__" disabled>
                      No projection types found
                    </SelectItem>
                  ) : (
                    projectionTypes.map((t) => (
                      <SelectItem key={t} value={t}>
                        {t}
                      </SelectItem>
                    ))
                  )}
                </SelectContent>
              </Select>
            )}
          </div>

          {/* Filters — only visible once a type is selected */}
          {selectedType && (
            <>
              {/* Hidden on a single-tenant store, where there is no tenant
                  column to filter on, and pinned when the scope bar already
                  names a tenant — two tenant filters that disagree is worse
                  than one you cannot reach from here. */}
              {isMultiTenant && (
                <div className="flex flex-col gap-1 min-w-[160px]">
                  <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
                    Tenant
                  </label>
                  <Input
                    placeholder={
                      scopeTenant ? scopeTenant : "Filter by tenant…"
                    }
                    value={scopeTenant ?? tenantInput}
                    onChange={handleTenantChange}
                    disabled={!!scopeTenant}
                  />
                </div>
              )}

              <div className="flex flex-col gap-1 min-w-[200px]">
                <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
                  Search
                </label>
                <Input
                  placeholder="Search document IDs…"
                  value={searchInput}
                  onChange={handleSearchChange}
                />
              </div>

              <Button
                variant="outline"
                size="sm"
                className="self-end"
                onClick={() => refetchStates({ requestPolicy: "network-only" })}
              >
                Refresh
              </Button>
            </>
          )}
        </div>

        {/* ------------------------------------------------------------------ */}
        {/* No type selected — explicit initial state                          */}
        {/* ------------------------------------------------------------------ */}
        {!selectedType && !typesResult.fetching && !typesResult.error && (
          <div className="rounded-base border-2 border-border px-4 py-12 text-center text-sm text-muted-foreground">
            Select a projection type above to load its state documents.
          </div>
        )}

        {/* ------------------------------------------------------------------ */}
        {/* States table                                                        */}
        {/* ------------------------------------------------------------------ */}
        {selectedType && (
          <>
            {statesResult.error && (
              <div className="mb-3 flex items-center gap-2 rounded-base border-2 border-border bg-destructive/10 px-3 py-2 text-sm text-destructive">
                <span>Failed to load projection states.</span>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() =>
                    refetchStates({ requestPolicy: "network-only" })
                  }
                >
                  Retry
                </Button>
              </div>
            )}

            <DataTable
              columns={COLUMNS}
              data={statesData}
              rowKey={(row) => `${row.tenantId ?? "_"}:${row.documentId}`}
              loading={statesResult.fetching && !statesResult.data}
              emptyMessage="No projection documents match the current filters."
              rowAction={(row) => (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setSelectedRow(row)}
                >
                  View
                </Button>
              )}
            />
          </>
        )}

        {/* ------------------------------------------------------------------ */}
        {/* Detail sheet                                                        */}
        {/* ------------------------------------------------------------------ */}
        <Sheet
          open={selectedRow !== null}
          onOpenChange={(open) => {
            if (!open) setSelectedRow(null);
          }}
        >
          <SheetContent side="right">
            <SheetHeader className="mb-4">
              <SheetTitle>Projection Document</SheetTitle>
              <SheetDescription>
                Metadata for this state entry. The document body is not exposed
                via the current admin API — only the identifier, tenant, and
                last-updated timestamp are available here.
              </SheetDescription>
            </SheetHeader>
            {selectedRow && (
              <pre className="rounded-base border-2 border-border bg-secondary-background p-4 text-xs font-mono overflow-auto whitespace-pre-wrap break-all leading-relaxed">
                {JSON.stringify(
                  {
                    documentId: selectedRow.documentId,
                    tenantId: selectedRow.tenantId ?? null,
                    ...(selectedRow.tenantId === "*"
                      ? {
                          tenantNote:
                            "Cross-tenant aggregate — single document blended across all tenants.",
                        }
                      : {}),
                    updatedAt: selectedRow.updatedAt ?? null,
                  },
                  null,
                  2
                )}
              </pre>
            )}
          </SheetContent>
        </Sheet>
      </div>
    </TooltipProvider>
  );
}
