import { useState, useEffect } from "react";
import { useQuery, useMutation, useSubscription } from "urql";
import { toast } from "sonner";
import { graphql, type ResultOf } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { PageHeader } from "@/components/ui/page-header";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  Tooltip,
  TooltipTrigger,
  TooltipContent,
} from "@/components/ui/tooltip";

// ---------------------------------------------------------------------------
// GraphQL documents
// ---------------------------------------------------------------------------

const RebuildStatesQuery = graphql(`
  query RebuildStates($processorId: String, $module: String) {
    adminRebuildStates(processorId: $processorId, module: $module) {
      processorId
      projectionType
      activeVersion
      rebuildingVersion
      status
      requestedAction
      replayedPosition
      targetPosition
      startedAt
      completedAt
    }
  }
`);

const GlobalPositionQuery = graphql(`
  query GlobalPosition($module: String) {
    adminGlobalPosition(module: $module)
  }
`);

const StartRebuildMutation = graphql(`
  mutation StartRebuild(
    $processorId: String!
    $projectionType: String!
    $targetPosition: Long!
    $operatorId: String
    $module: String
  ) {
    adminStartRebuild(
      processorId: $processorId
      projectionType: $projectionType
      targetPosition: $targetPosition
      operatorId: $operatorId
      module: $module
    ) {
      activeVersion
      rebuildingVersion
      targetPosition
    }
  }
`);

const PromoteRebuildMutation = graphql(`
  mutation PromoteRebuild(
    $processorId: String!
    $force: Boolean!
    $operatorId: String
    $module: String
  ) {
    adminPromoteRebuild(
      processorId: $processorId
      force: $force
      operatorId: $operatorId
      module: $module
    ) {
      processorId
      status
    }
  }
`);

const AbortRebuildMutation = graphql(`
  mutation AbortRebuild($processorId: String!, $operatorId: String, $module: String) {
    adminAbortRebuild(
      processorId: $processorId
      operatorId: $operatorId
      module: $module
    ) {
      processorId
      status
    }
  }
`);

const RebuildUpdatedSub = graphql(`
  subscription RebuildUpdated($module: String) {
    onAdminRebuildUpdated(module: $module) {
      processorId
      action
      status
    }
  }
`);

// ---------------------------------------------------------------------------
// Types (inferred, never hand-written)
// ---------------------------------------------------------------------------

type RebuildRow = ResultOf<typeof RebuildStatesQuery>["adminRebuildStates"][number];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const OPERATOR_ID = "admin";

function relativeTime(dateStr: string | null | undefined): string {
  if (!dateStr) return "—";
  const diff = Date.now() - new Date(dateStr).getTime();
  const abs = Math.abs(diff);
  const seconds = Math.floor(abs / 1000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);
  if (days > 0) return `${days}d ago`;
  if (hours > 0) return `${hours}h ago`;
  if (minutes > 0) return `${minutes}m ago`;
  return `${seconds}s ago`;
}

function absoluteTime(dateStr: string | null | undefined): string {
  if (!dateStr) return "—";
  return new Date(dateStr).toLocaleString();
}

function formatPosition(pos: number | null | undefined): string {
  if (pos === null || pos === undefined) return "—";
  return pos.toLocaleString();
}

type StatusTone = "default" | "secondary" | "info" | "success" | "danger" | "warning";

function statusBadgeVariant(status: string): StatusTone {
  switch (status) {
    case "rebuilding":
      return "info";
    case "ready":
      return "success";
    case "completed":
      return "success";
    case "aborted":
      return "danger";
    case "idle":
    default:
      return "secondary";
  }
}

function actionBadgeVariant(action: string | null | undefined): StatusTone {
  if (!action) return "secondary";
  switch (action) {
    case "promote":
      return "success";
    case "force-promote":
      return "warning";
    case "abort":
      return "danger";
    default:
      return "secondary";
  }
}

// ---------------------------------------------------------------------------
// Progress bar
// ---------------------------------------------------------------------------

interface ProgressBarProps {
  replayed: number | null | undefined;
  target: number | null | undefined;
  status: string;
}

function ProgressBar({ replayed, target, status }: ProgressBarProps) {
  const isActive = status === "rebuilding";
  if (!isActive || replayed === null || replayed === undefined) {
    return <span className="text-muted-foreground text-xs">—</span>;
  }

  const pct =
    target !== null && target !== undefined && target > 0
      ? Math.min(100, (replayed / target) * 100)
      : 0;

  const label = `${formatPosition(replayed)} / ${formatPosition(target)}`;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <div className="w-36 space-y-1 cursor-default">
          <div className="h-2 rounded-full bg-secondary-background border border-border overflow-hidden">
            <div
              className="h-full bg-info transition-all duration-500"
              style={{ width: `${pct.toFixed(1)}%` }}
            />
          </div>
          <p className="font-mono text-xs text-muted-foreground">
            {pct.toFixed(1)}%
          </p>
        </div>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function TimestampCell({ value }: { value: string | null | undefined }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="cursor-default tabular-nums">{relativeTime(value)}</span>
      </TooltipTrigger>
      <TooltipContent>{absoluteTime(value)}</TooltipContent>
    </Tooltip>
  );
}

// ---------------------------------------------------------------------------
// Start rebuild dialog
// ---------------------------------------------------------------------------

interface StartRebuildDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  defaultPosition: number | null | undefined;
  loading: boolean;
  onConfirm: (processorId: string, projectionType: string, targetPosition: number) => void;
}

function StartRebuildDialog({
  open,
  onOpenChange,
  defaultPosition,
  loading,
  onConfirm,
}: StartRebuildDialogProps) {
  const [processorId, setProcessorId] = useState("");
  const [projectionType, setProjectionType] = useState("");
  const [targetPositionStr, setTargetPositionStr] = useState("");

  // Sync defaults when dialog opens
  useEffect(() => {
    if (open) {
      setProcessorId("");
      setProjectionType("");
      setTargetPositionStr(
        defaultPosition !== null && defaultPosition !== undefined
          ? String(Math.floor(defaultPosition))
          : ""
      );
    }
  }, [open, defaultPosition]);

  // Auto-fill projectionType from processorId when blank
  function handleProcessorIdChange(value: string) {
    setProcessorId(value);
    if (!projectionType || projectionType === processorId) {
      setProjectionType(value);
    }
  }

  const targetPositionNum = Number(targetPositionStr);
  const canSubmit =
    processorId.trim().length > 0 &&
    projectionType.trim().length > 0 &&
    targetPositionStr.trim().length > 0 &&
    !isNaN(targetPositionNum) &&
    targetPositionNum >= 0;

  function handleConfirm() {
    if (!canSubmit) return;
    onConfirm(processorId.trim(), projectionType.trim(), targetPositionNum);
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Start projection rebuild</DialogTitle>
          <DialogDescription>
            Starts a zero-downtime rebuild. A shadow copy of the projection state is
            replayed from position 0 up to the target position while the live copy
            keeps serving reads. Promote when ready.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4 py-2">
          <div className="space-y-1.5">
            <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
              Processor ID
            </label>
            <Input
              placeholder="e.g. orders-read-model"
              value={processorId}
              onChange={(e) => handleProcessorIdChange(e.target.value)}
              autoFocus
            />
          </div>

          <div className="space-y-1.5">
            <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
              Projection type
            </label>
            <Input
              placeholder="Defaults to processor ID"
              value={projectionType}
              onChange={(e) => setProjectionType(e.target.value)}
            />
            <p className="text-xs text-muted-foreground">
              The CLR projection type name. Leave matching the processor ID if unsure.
            </p>
          </div>

          <div className="space-y-1.5">
            <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
              Target position
            </label>
            <Input
              placeholder="Current head position"
              value={targetPositionStr}
              onChange={(e) => setTargetPositionStr(e.target.value)}
              className="font-mono"
            />
            {defaultPosition !== null && defaultPosition !== undefined && (
              <p className="text-xs text-muted-foreground">
                Current head:{" "}
                <button
                  type="button"
                  className="font-mono underline underline-offset-2"
                  onClick={() => setTargetPositionStr(String(Math.floor(defaultPosition)))}
                >
                  {formatPosition(defaultPosition)}
                </button>
              </p>
            )}
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button onClick={handleConfirm} disabled={!canSubmit || loading}>
            {loading ? "Starting…" : "Start rebuild"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------
// Promote dialog
// ---------------------------------------------------------------------------

interface PromoteDialogProps {
  processorId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  loading: boolean;
  onConfirm: (force: boolean) => void;
}

function PromoteDialog({ processorId, open, onOpenChange, loading, onConfirm }: PromoteDialogProps) {
  const [force, setForce] = useState(false);

  useEffect(() => {
    if (!open) setForce(false);
  }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Promote rebuild — {processorId}</DialogTitle>
          <DialogDescription asChild>
            <div className="space-y-2 text-sm text-muted-foreground">
              <p>
                Swaps the shadow copy into the active slot in a single transaction. The
                live copy will be discarded after a brief reclaim grace period.
              </p>
              {force && (
                <p className="rounded-base border-2 border-border bg-warning/10 p-2 text-warning font-semibold">
                  Force-promote requests promotion before the original target position is
                  reached. The promoted version may be behind the live projection.
                </p>
              )}
            </div>
          </DialogDescription>
        </DialogHeader>

        <div className="flex items-center gap-2 py-1">
          <Checkbox
            id="force-promote"
            checked={force}
            onCheckedChange={(checked) => setForce(checked === true)}
          />
          <label htmlFor="force-promote" className="text-sm cursor-pointer select-none">
            Force-promote (before target reached)
          </label>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button
            variant={force ? "destructive" : "default"}
            onClick={() => onConfirm(force)}
            disabled={loading}
          >
            {loading ? "Promoting…" : force ? "Force-promote" : "Promote"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------
// Abort dialog
// ---------------------------------------------------------------------------

interface AbortDialogProps {
  processorId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  loading: boolean;
  onConfirm: () => void;
}

function AbortDialog({ processorId, open, onOpenChange, loading, onConfirm }: AbortDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Abort rebuild — {processorId}</DialogTitle>
          <DialogDescription asChild>
            <div className="space-y-2 text-sm text-muted-foreground">
              <p>
                Marks the in-flight rebuild as aborted. The live projection is not
                affected — it continues serving reads normally.
              </p>
              <p>
                The shadow loop only learns of the abort on its next poll, so a few
                writes may still land after this request. A background sweep will
                reclaim the partial shadow state once the grace period elapses.
              </p>
            </div>
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button variant="outline" onClick={onConfirm} disabled={loading}>
            {loading ? "Aborting…" : "Abort rebuild"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ---------------------------------------------------------------------------
// Row actions
// ---------------------------------------------------------------------------

interface RowActionsProps {
  row: RebuildRow;
  onPromote: (row: RebuildRow) => void;
  onAbort: (row: RebuildRow) => void;
}

function RowActions({ row, onPromote, onAbort }: RowActionsProps) {
  const canPromote = row.status === "ready" || row.status === "rebuilding";
  const canAbort = row.status === "rebuilding" || row.status === "ready";

  if (!canPromote && !canAbort) return null;

  return (
    <div className="flex items-center justify-end gap-1">
      {canPromote && (
        <Button size="sm" variant="outline" onClick={() => onPromote(row)}>
          Promote
        </Button>
      )}
      {canAbort && (
        <Button size="sm" variant="ghost" onClick={() => onAbort(row)}>
          Abort
        </Button>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main page component
// ---------------------------------------------------------------------------

export function RebuildsPage() {
  // A rebuild rewrites one store's projection state against that store's own
  // log, so the module travels with every read and every mutation here. Getting
  // it wrong would promote a version in the store you are not looking at.
  const { module } = useAdminScope();

  // Filter
  const [processorIdInput, setProcessorIdInput] = useState("");
  const [processorIdFilter, setProcessorIdFilter] = useState<string | null>(null);

  // Dialog state
  const [startOpen, setStartOpen] = useState(false);
  const [promoteTarget, setPromoteTarget] = useState<RebuildRow | null>(null);
  const [promoteOpen, setPromoteOpen] = useState(false);
  const [abortTarget, setAbortTarget] = useState<RebuildRow | null>(null);
  const [abortOpen, setAbortOpen] = useState(false);

  // Queries
  const [rebuildsResult, reExecuteRebuilds] = useQuery({
    query: RebuildStatesQuery,
    variables: { processorId: processorIdFilter, module },
  });

  const [globalPosResult] = useQuery({
    query: GlobalPositionQuery,
    variables: { module },
  });

  // Mutations
  const [startResult, executeStart] = useMutation(StartRebuildMutation);
  const [promoteResult, executePromote] = useMutation(PromoteRebuildMutation);
  const [abortResult, executeAbort] = useMutation(AbortRebuildMutation);

  // Subscription — triggers re-query on any rebuild lifecycle event
  const [subResult] = useSubscription({
    query: RebuildUpdatedSub,
    variables: { module },
  });
  useEffect(() => {
    if (subResult.data) {
      reExecuteRebuilds({ requestPolicy: "network-only" });
    }
  }, [subResult.data, reExecuteRebuilds]);

  // Apply filter
  function applyFilter() {
    setProcessorIdFilter(processorIdInput.trim() || null);
  }

  // Handlers
  function handlePromoteClick(row: RebuildRow) {
    setPromoteTarget(row);
    setPromoteOpen(true);
  }

  function handleAbortClick(row: RebuildRow) {
    setAbortTarget(row);
    setAbortOpen(true);
  }

  async function handleStartConfirm(
    processorId: string,
    projectionType: string,
    targetPosition: number
  ) {
    const result = await executeStart({
      processorId,
      projectionType,
      targetPosition,
      operatorId: OPERATOR_ID,
      module,
    });
    setStartOpen(false);
    if (result.error) {
      toast.error(`Start rebuild failed: ${result.error.message}`);
    } else {
      const d = result.data?.adminStartRebuild;
      toast.success(
        `Rebuild started for ${processorId}. Rebuilding version ${d?.rebuildingVersion}, target ${formatPosition(d?.targetPosition)}.`
      );
      reExecuteRebuilds({ requestPolicy: "network-only" });
    }
  }

  async function handlePromoteConfirm(force: boolean) {
    if (!promoteTarget) return;
    const result = await executePromote({
      processorId: promoteTarget.processorId,
      force,
      operatorId: OPERATOR_ID,
      module,
    });
    setPromoteOpen(false);
    if (result.error) {
      toast.error(`Promote failed: ${result.error.message}`);
    } else {
      toast.success(
        `${force ? "Force-promoted" : "Promoted"} ${promoteTarget.processorId}. Status: ${result.data?.adminPromoteRebuild.status ?? "unknown"}.`
      );
      reExecuteRebuilds({ requestPolicy: "network-only" });
    }
    setPromoteTarget(null);
  }

  async function handleAbortConfirm() {
    if (!abortTarget) return;
    const result = await executeAbort({
      processorId: abortTarget.processorId,
      operatorId: OPERATOR_ID,
      module,
    });
    setAbortOpen(false);
    if (result.error) {
      toast.error(`Abort failed: ${result.error.message}`);
    } else {
      toast.success(
        `Abort requested for ${abortTarget.processorId}. The shadow loop will stop on its next poll.`
      );
      reExecuteRebuilds({ requestPolicy: "network-only" });
    }
    setAbortTarget(null);
  }

  // Column definitions
  const columns: ColumnDef<RebuildRow>[] = [
    {
      key: "processorId",
      header: "Processor",
      cell: (row) => (
        <span className="font-mono text-xs">{row.processorId}</span>
      ),
    },
    {
      key: "projectionType",
      header: "Projection type",
      cell: (row) => (
        <span className="font-mono text-xs text-muted-foreground">{row.projectionType}</span>
      ),
    },
    {
      key: "status",
      header: "Status",
      cell: (row) => (
        <div className="flex items-center gap-2">
          <Badge variant={statusBadgeVariant(row.status)}>{row.status}</Badge>
          {row.requestedAction && (
            <Badge variant={actionBadgeVariant(row.requestedAction)} className="text-xs">
              ↦ {row.requestedAction}
            </Badge>
          )}
        </div>
      ),
    },
    {
      key: "versions",
      header: "Version (active / rebuilding)",
      cell: (row) => (
        <span className="font-mono text-xs tabular-nums">
          {row.activeVersion}
          {row.rebuildingVersion !== null && row.rebuildingVersion !== undefined && (
            <span className="text-muted-foreground"> / {row.rebuildingVersion}</span>
          )}
        </span>
      ),
    },
    {
      key: "progress",
      header: "Progress",
      cell: (row) => (
        <ProgressBar
          replayed={row.replayedPosition}
          target={row.targetPosition}
          status={row.status}
        />
      ),
    },
    {
      key: "startedAt",
      header: "Started",
      cell: (row) => <TimestampCell value={row.startedAt} />,
    },
    {
      key: "completedAt",
      header: "Completed",
      cell: (row) => <TimestampCell value={row.completedAt} />,
    },
  ];

  const rows = rebuildsResult.data?.adminRebuildStates;
  const isLoading = rebuildsResult.fetching;
  const isError = Boolean(rebuildsResult.error);
  const globalPosition = globalPosResult.data?.adminGlobalPosition;

  return (
    <div className="p-6">
      <PageHeader
        title="Rebuilds"
        eyebrow="Projection Rebuilds"
        description="Zero-downtime projection rebuilds. A shadow copy replays under its own checkpoint while the live copy keeps serving reads, then the two are swapped in one transaction."
      >
        <Button size="sm" onClick={() => setStartOpen(true)}>
          Start rebuild
        </Button>
      </PageHeader>

      {/* Filter */}
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1 min-w-[220px]">
          <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
            Processor ID
          </label>
          <Input
            placeholder="Filter by processor…"
            value={processorIdInput}
            onChange={(e) => setProcessorIdInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && applyFilter()}
            className="h-8 text-xs"
          />
        </div>
        <div className="flex gap-2 pb-0.5">
          <Button size="sm" onClick={applyFilter}>
            Apply
          </Button>
          {processorIdFilter && (
            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setProcessorIdInput("");
                setProcessorIdFilter(null);
              }}
            >
              Clear
            </Button>
          )}
        </div>

        {/* Global position indicator */}
        {globalPosition !== null && globalPosition !== undefined && (
          <div className="ml-auto flex items-center gap-1.5 text-xs text-muted-foreground">
            <span className="font-mono text-[0.65rem] tracking-[0.14em] uppercase">Head:</span>
            <span className="font-mono">{formatPosition(globalPosition)}</span>
          </div>
        )}
      </div>

      {/* Error state */}
      {isError && !isLoading && (
        <div className="mb-4 flex items-center gap-3 rounded-base border-2 border-border bg-danger/5 p-4 text-sm">
          <span className="text-danger font-semibold">Failed to load rebuild states.</span>
          <Button
            size="sm"
            variant="outline"
            onClick={() => reExecuteRebuilds({ requestPolicy: "network-only" })}
          >
            Retry
          </Button>
        </div>
      )}

      {/* In-flight summary */}
      {rows && rows.some((r) => r.status === "rebuilding") && (
        <div className="mb-4 rounded-base border-2 border-border bg-info/5 p-3 text-xs text-info font-semibold">
          Rebuilds in progress — positions update live. Promote once the progress
          bar reaches 100% and status transitions to{" "}
          <span className="font-mono">ready</span>.
        </div>
      )}

      {/* Table */}
      <DataTable
        columns={columns}
        data={rows}
        rowKey={(row) => row.processorId}
        loading={isLoading}
        emptyMessage="No rebuild records. Start a rebuild to begin a zero-downtime projection swap."
        rowAction={(row) => (
          <RowActions row={row} onPromote={handlePromoteClick} onAbort={handleAbortClick} />
        )}
      />

      {/* Start rebuild dialog */}
      <StartRebuildDialog
        open={startOpen}
        onOpenChange={setStartOpen}
        defaultPosition={globalPosition}
        loading={startResult.fetching}
        onConfirm={handleStartConfirm}
      />

      {/* Promote dialog */}
      <PromoteDialog
        processorId={promoteTarget?.processorId ?? ""}
        open={promoteOpen}
        onOpenChange={setPromoteOpen}
        loading={promoteResult.fetching}
        onConfirm={handlePromoteConfirm}
      />

      {/* Abort dialog */}
      <AbortDialog
        processorId={abortTarget?.processorId ?? ""}
        open={abortOpen}
        onOpenChange={setAbortOpen}
        loading={abortResult.fetching}
        onConfirm={handleAbortConfirm}
      />
    </div>
  );
}
