import { useState } from "react";
import { useQuery, useMutation } from "urql";
import { toast } from "sonner";
import { AlertTriangle, RefreshCw, Shield, Users } from "lucide-react";
import { graphql } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { PageHeader } from "@/components/ui/page-header";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

// ---------------------------------------------------------------------------
// GraphQL documents
// ---------------------------------------------------------------------------

const TenantLeasesQuery = graphql(`
  query TenantLeases($module: String) {
    adminTenantLeases(module: $module) {
      tenancyMode
      leases {
        tenantId
        consumerId
        replicaId
        expiresAt
      }
    }
  }
`);

const ProcessorsQuery = graphql(`
  query ProcessorsForTenants($module: String) {
    adminProcessors(module: $module) {
      processorId
    }
  }
`);

const ProcessorLeasesQuery = graphql(`
  query ProcessorLeases($processorId: String!, $module: String) {
    adminProcessorLeases(processorId: $processorId, module: $module) {
      consumerId
      replicaId
      expiresAt
    }
  }
`);

const ReleaseTenantLeasesMutation = graphql(`
  mutation ReleaseTenantLeases(
    $consumerId: String
    $operatorId: String
    $module: String
  ) {
    adminReleaseTenantLeases(
      consumerId: $consumerId
      operatorId: $operatorId
      module: $module
    ) {
      releasedCount
    }
  }
`);

// ---------------------------------------------------------------------------
// Time helpers
// ---------------------------------------------------------------------------

function formatAbsolute(dateStr: string): string {
  return new Date(dateStr).toLocaleString();
}

function formatRelative(dateStr: string): string {
  const diffMs = Date.now() - new Date(dateStr).getTime();
  const isFuture = diffMs < 0;
  const abs = Math.abs(diffMs);
  const s = Math.floor(abs / 1000);
  const m = Math.floor(s / 60);
  const h = Math.floor(m / 60);
  const d = Math.floor(h / 24);
  let label: string;
  if (s < 60) label = `${s}s`;
  else if (m < 60) label = `${m}m`;
  else if (h < 24) label = `${h}h`;
  else label = `${d}d`;
  return isFuture ? `in ${label}` : `${label} ago`;
}

type ExpiryState = "expired" | "critical" | "ok" | "none";

function expiryState(expiresAt: string | null | undefined): ExpiryState {
  if (!expiresAt) return "none";
  const diff = new Date(expiresAt).getTime() - Date.now();
  if (diff <= 0) return "expired";
  if (diff < 5 * 60 * 1000) return "critical";
  return "ok";
}

function ExpiryBadge({ expiresAt }: { expiresAt: string | null | undefined }) {
  const state = expiryState(expiresAt);
  if (state === "none") {
    return <Badge variant="secondary">No expiry</Badge>;
  }
  const variant =
    state === "expired" ? "danger" : state === "critical" ? "warning" : "success";
  const label =
    state === "expired"
      ? `Expired ${formatRelative(expiresAt!)}`
      : formatRelative(expiresAt!);
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>
          <Badge variant={variant}>{label}</Badge>
        </TooltipTrigger>
        <TooltipContent>{formatAbsolute(expiresAt!)}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

// ---------------------------------------------------------------------------
// AdminTenantLease row type (inferred from query)
// ---------------------------------------------------------------------------

type TenantLeaseRow = {
  tenantId: string;
  consumerId: string;
  replicaId: string | null;
  expiresAt: string | null;
};

type ProcessorLeaseRow = {
  consumerId: string;
  replicaId: string | null;
  expiresAt: string;
};

// ---------------------------------------------------------------------------
// Release confirmation dialog
// ---------------------------------------------------------------------------

interface ReleaseDialogProps {
  open: boolean;
  consumerId: string | null; // null = release all
  onConfirm: () => void;
  onCancel: () => void;
  pending: boolean;
}

function ReleaseDialog({
  open,
  consumerId,
  onConfirm,
  onCancel,
  pending,
}: ReleaseDialogProps) {
  return (
    <Dialog open={open} onOpenChange={(v) => !v && onCancel()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <AlertTriangle className="size-4 text-warning" />
            Release Tenant Leases
          </DialogTitle>
          <DialogDescription className="pt-2 text-sm text-foreground">
            {consumerId ? (
              <>
                This will release all tenant leases held by consumer{" "}
                <span className="font-mono font-semibold">{consumerId}</span>.
              </>
            ) : (
              <>This will release <strong>all tenant leases</strong> across every consumer.</>
            )}
          </DialogDescription>
          <p className="mt-2 text-sm text-muted-foreground">
            Releasing a lease allows another consumer to immediately acquire
            that tenant's work. Any in-flight processing under the released
            lease may be duplicated or re-processed.
          </p>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={onConfirm} disabled={pending}>
            {pending ? "Releasing…" : "Release leases"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------
// Processor leases panel
// ---------------------------------------------------------------------------

function ProcessorLeasesPanel() {
  const { module } = useAdminScope();
  const [selectedProcessor, setSelectedProcessor] = useState<string>("");

  const [{ data: processorsData, fetching: processorsFetching }] = useQuery({
    query: ProcessorsQuery,
    variables: { module },
  });

  const [{ data: leasesData, fetching: leasesFetching, error: leasesError }, reexecute] =
    useQuery({
      query: ProcessorLeasesQuery,
      variables: { processorId: selectedProcessor, module },
      pause: !selectedProcessor,
    });

  const processorLeaseColumns: ColumnDef<ProcessorLeaseRow>[] = [
    {
      key: "consumerId",
      header: "Consumer",
      cell: (row) => (
        <span className="font-mono text-xs">{row.consumerId}</span>
      ),
    },
    {
      key: "replicaId",
      header: "Replica",
      cell: (row) =>
        row.replicaId ? (
          <span className="font-mono text-xs">{row.replicaId}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
    {
      key: "expiresAt",
      header: "Expires",
      cell: (row) => <ExpiryBadge expiresAt={row.expiresAt} />,
    },
  ];

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Processor Leases</CardTitle>
        <p className="text-sm text-muted-foreground">
          Active leases held for a specific processor.
        </p>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex items-center gap-3">
          <div className="w-80">
            {processorsFetching ? (
              <Skeleton className="h-9 w-full" />
            ) : (
              <Select value={selectedProcessor} onValueChange={setSelectedProcessor}>
                <SelectTrigger>
                  <SelectValue placeholder="Select a processor…" />
                </SelectTrigger>
                <SelectContent>
                  {(processorsData?.adminProcessors ?? []).map((p) => (
                    <SelectItem key={p.processorId} value={p.processorId}>
                      {p.processorId}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </div>
          {selectedProcessor && (
            <Button
              variant="outline"
              size="icon"
              onClick={() => reexecute({ requestPolicy: "network-only" })}
              disabled={leasesFetching}
              title="Refresh"
            >
              <RefreshCw className={leasesFetching ? "animate-spin" : ""} />
            </Button>
          )}
        </div>

        {selectedProcessor && (
          <>
            {leasesError && (
              <p className="text-sm text-destructive">
                Failed to load leases: {leasesError.message}
              </p>
            )}
            <DataTable
              columns={processorLeaseColumns}
              data={leasesData?.adminProcessorLeases as ProcessorLeaseRow[] | undefined}
              rowKey={(row) => `${row.consumerId}:${row.replicaId ?? "none"}`}
              loading={leasesFetching}
              emptyMessage="No active leases for this processor."
            />
          </>
        )}
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Main page
// ---------------------------------------------------------------------------

export function TenantsPage() {
  // Leases are held per module: the same consumer can hold leases in both stores,
  // and releasing one store's leases says nothing about the other's.
  const { module } = useAdminScope();

  const [{ data, fetching, error }, reexecute] = useQuery({
    query: TenantLeasesQuery,
    variables: { module },
  });

  // Release dialog state
  const [dialogOpen, setDialogOpen] = useState(false);
  const [releaseTargetConsumerId, setReleaseTargetConsumerId] = useState<
    string | null
  >(null);

  const [{ fetching: releasing }, releaseMutation] = useMutation(
    ReleaseTenantLeasesMutation
  );

  function openReleaseDialog(consumerId: string | null) {
    setReleaseTargetConsumerId(consumerId);
    setDialogOpen(true);
  }

  async function handleRelease() {
    const result = await releaseMutation({
      consumerId: releaseTargetConsumerId,
      operatorId: "admin",
      module,
    });
    if (result.error) {
      toast.error("Failed to release leases", {
        description: result.error.message,
      });
    } else {
      const count = result.data?.adminReleaseTenantLeases.releasedCount ?? 0;
      toast.success(`Released ${count} lease${count !== 1 ? "s" : ""}`, {
        description: releaseTargetConsumerId
          ? `Consumer ${releaseTargetConsumerId} can now reacquire tenant work.`
          : "All consumers can now reacquire tenant work.",
      });
      reexecute({ requestPolicy: "network-only" });
    }
    setDialogOpen(false);
  }

  const tenantLeaseColumns: ColumnDef<TenantLeaseRow>[] = [
    {
      key: "tenantId",
      header: "Tenant",
      cell: (row) => (
        <span className="font-mono text-xs font-semibold">{row.tenantId}</span>
      ),
    },
    {
      key: "consumerId",
      header: "Consumer",
      cell: (row) => (
        <span className="font-mono text-xs">{row.consumerId}</span>
      ),
    },
    {
      key: "replicaId",
      header: "Replica",
      cell: (row) =>
        row.replicaId ? (
          <span className="font-mono text-xs">{row.replicaId}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
    {
      key: "expiresAt",
      header: "Expiry",
      cell: (row) => <ExpiryBadge expiresAt={row.expiresAt} />,
    },
  ];

  // Loading
  if (fetching && !data) {
    return (
      <div className="p-6 space-y-6">
        <PageHeader title="Tenant Leases" eyebrow="Tenant Management" />
        <div className="space-y-3">
          <Skeleton className="h-8 w-64" />
          <Skeleton className="h-48 w-full" />
        </div>
      </div>
    );
  }

  // Error
  if (error) {
    return (
      <div className="p-6 space-y-6">
        <PageHeader title="Tenant Leases" eyebrow="Tenant Management" />
        <Card className="max-w-lg">
          <CardContent className="pt-6">
            <p className="text-sm text-destructive font-semibold mb-1">
              Failed to load tenant lease inventory
            </p>
            <p className="text-xs text-muted-foreground mb-4">{error.message}</p>
            <Button
              variant="outline"
              size="sm"
              onClick={() => reexecute({ requestPolicy: "network-only" })}
            >
              <RefreshCw className="size-3" /> Retry
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  const inventory = data?.adminTenantLeases;

  // Single-tenant store — leases are not applicable
  if (inventory?.tenancyMode === "SINGLE_TENANT") {
    return (
      <div className="p-6 space-y-6">
        <PageHeader
          title="Tenant Leases"
          eyebrow="Tenant Management"
          description="Tenant lease management for multi-tenant stores."
        />
        <Card className="max-w-lg">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-base">
              <Shield className="size-4 text-muted-foreground" />
              Single-tenant store
            </CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-sm text-muted-foreground">
              This Alberto store was migrated in <strong>single-tenant</strong> mode.
              Tenant leases are a multi-tenant concept and are not present in this
              store. The tenancy mode is fixed at migration time and cannot be changed
              without re-creating the store.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  const leases = (inventory?.leases ?? []) as TenantLeaseRow[];

  return (
    <div className="p-6 space-y-6">
      <PageHeader
        title="Tenant Leases"
        eyebrow="Tenant Management"
        description="Inspect and release tenant leases across all consumers."
      >
        <Button
          variant="outline"
          size="sm"
          onClick={() => reexecute({ requestPolicy: "network-only" })}
          disabled={fetching}
        >
          <RefreshCw className={fetching ? "animate-spin" : ""} />
          Refresh
        </Button>
        <Button
          variant="destructive"
          size="sm"
          onClick={() => openReleaseDialog(null)}
        >
          <Users className="size-3" />
          Release all leases
        </Button>
      </PageHeader>

      {/* Mode badge */}
      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground">Store tenancy:</span>
        <Badge variant="info">Multi-tenant</Badge>
      </div>

      {/* Tenant leases table */}
      <DataTable
        columns={tenantLeaseColumns}
        data={leases}
        rowKey={(row) => row.tenantId}
        loading={fetching && !data}
        emptyMessage="No active tenant leases — the application may not have started yet or all leases have expired."
        rowAction={(row) => (
          <Button
            variant="outline"
            size="sm"
            onClick={() => openReleaseDialog(row.consumerId)}
          >
            Release
          </Button>
        )}
      />

      {/* Processor leases panel */}
      <ProcessorLeasesPanel />

      {/* Release confirmation dialog */}
      <ReleaseDialog
        open={dialogOpen}
        consumerId={releaseTargetConsumerId}
        onConfirm={handleRelease}
        onCancel={() => setDialogOpen(false)}
        pending={releasing}
      />
    </div>
  );
}
