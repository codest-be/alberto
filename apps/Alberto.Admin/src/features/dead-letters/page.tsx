import { useState, useEffect, useMemo } from "react";
import { useQuery, useMutation, useSubscription } from "urql";
import { toast } from "sonner";
import { graphql, type ResultOf } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { PageHeader } from "@/components/ui/page-header";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import { StatCard } from "@/components/ui/stat-card";
import { Skeleton } from "@/components/ui/skeleton";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
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

const DeadLettersQuery = graphql(`
  query DeadLetters(
    $processorId: String
    $eventType: String
    $tenant: String
    $module: String
    $limit: Int!
  ) {
    adminDeadLetters(
      processorId: $processorId
      eventType: $eventType
      tenant: $tenant
      module: $module
      limit: $limit
    ) {
      id
      processorId
      eventType
      globalPosition
      errorMessage
      failedAt
      tenantId
    }
  }
`);

const DeadLetterCountQuery = graphql(`
  query DeadLetterCount($module: String) {
    adminDeadLetterCount(module: $module)
  }
`);

const RetryByRewindMutation = graphql(`
  mutation RetryByRewind($processorId: String!, $operatorId: String, $module: String) {
    adminRetryByRewind(processorId: $processorId, operatorId: $operatorId, module: $module) {
      processorId
      rewindPosition
      deletedCount
    }
  }
`);

const ClearForProcessorMutation = graphql(`
  mutation ClearForProcessor($processorId: String!, $operatorId: String, $module: String) {
    adminClearDeadLettersForProcessor(processorId: $processorId, operatorId: $operatorId, module: $module) {
      deletedCount
    }
  }
`);

const ClearAllMutation = graphql(`
  mutation ClearAll($operatorId: String, $module: String) {
    adminClearAllDeadLetters(operatorId: $operatorId, module: $module) {
      deletedCount
    }
  }
`);

const DeadLettersChangedSub = graphql(`
  subscription DeadLettersChanged($module: String) {
    onAdminDeadLettersChanged(module: $module) {
      deletedCount
    }
  }
`);

// ---------------------------------------------------------------------------
// Types (inferred, never hand-written)
// ---------------------------------------------------------------------------

type DeadLetterRow =
  ResultOf<typeof DeadLettersQuery>["adminDeadLetters"][number];

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

interface RetryDialogProps {
  processorId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: () => void;
  loading: boolean;
}

function RetryDialog({ processorId, open, onOpenChange, onConfirm, loading }: RetryDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Retry via rewind — {processorId}</DialogTitle>
          <DialogDescription asChild>
            <div className="space-y-2 text-sm text-muted-foreground">
              <p>
                This rewinds the checkpoint for <strong className="text-foreground">{processorId}</strong> to
                before its earliest dead-letter entry, then clears all dead letters for that processor.
              </p>
              <p>
                The failed events will be <strong className="text-foreground">re-processed</strong> from
                the rewound position. This is the recommended recovery path.
              </p>
            </div>
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button onClick={onConfirm} disabled={loading}>
            {loading ? "Retrying…" : "Rewind & retry"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface ClearProcessorDialogProps {
  processorId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: () => void;
  loading: boolean;
}

function ClearProcessorDialog({
  processorId,
  open,
  onOpenChange,
  onConfirm,
  loading,
}: ClearProcessorDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Clear dead letters — {processorId}</DialogTitle>
          <DialogDescription asChild>
            <div className="space-y-2 text-sm text-muted-foreground">
              <p>
                This permanently removes all dead-letter entries for{" "}
                <strong className="text-foreground">{processorId}</strong>.
              </p>
              <p className="font-semibold text-danger">
                The entries are gone for good. The events will not be reprocessed.
                This does not fix the underlying problem.
              </p>
            </div>
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={onConfirm} disabled={loading}>
            {loading ? "Clearing…" : "Clear entries"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface ClearAllDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onConfirm: () => void;
  loading: boolean;
}

function ClearAllDialog({ open, onOpenChange, onConfirm, loading }: ClearAllDialogProps) {
  const [confirmText, setConfirmText] = useState("");
  const required = "CLEAR ALL";
  const canConfirm = confirmText === required;

  // Reset text when dialog closes
  useEffect(() => {
    if (!open) setConfirmText("");
  }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Clear ALL dead letters across every processor</DialogTitle>
          <DialogDescription asChild>
            <div className="space-y-3 text-sm text-muted-foreground">
              <p>
                This is the most destructive control on this page. It permanently
                removes every dead-letter entry across all processors in the system.
              </p>
              <p className="font-semibold text-danger">
                All entries are gone for good. No events will be reprocessed.
                This does not fix any underlying problems.
              </p>
              <div className="pt-1">
                <label className="block font-mono text-[0.65rem] tracking-[0.14em] uppercase mb-1.5">
                  Type <span className="font-mono text-foreground">{required}</span> to confirm
                </label>
                <Input
                  value={confirmText}
                  onChange={(e) => setConfirmText(e.target.value)}
                  placeholder={required}
                  autoComplete="off"
                />
              </div>
            </div>
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button
            variant="destructive"
            onClick={onConfirm}
            disabled={!canConfirm || loading}
          >
            {loading ? "Clearing…" : "Clear all entries"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface DetailSheetProps {
  row: DeadLetterRow | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onRetry: (row: DeadLetterRow) => void;
  onClear: (row: DeadLetterRow) => void;
}

function DetailSheet({ row, open, onOpenChange, onRetry, onClear }: DetailSheetProps) {
  if (!row) return null;
  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent side="right" className="w-full sm:max-w-xl overflow-y-auto">
        <SheetHeader className="mb-4">
          <SheetTitle className="font-mono text-sm break-all">{row.processorId}</SheetTitle>
          <SheetDescription>
            Dead-letter entry — {row.eventType ?? "(no event type)"}
          </SheetDescription>
        </SheetHeader>

        <div className="space-y-4 text-sm">
          <div className="grid grid-cols-2 gap-3">
            <div>
              <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                Event type
              </p>
              <p className="font-mono break-all">{row.eventType ?? "—"}</p>
            </div>
            <div>
              <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                Global position
              </p>
              <p className="font-mono">{formatPosition(row.globalPosition)}</p>
            </div>
            <div>
              <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                Failed at
              </p>
              <p>{absoluteTime(row.failedAt)}</p>
            </div>
            <div>
              <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                Tenant
              </p>
              <p className="font-mono">{row.tenantId ?? "—"}</p>
            </div>
            <div className="col-span-2">
              <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                Entry ID
              </p>
              <p className="font-mono text-xs break-all text-muted-foreground">{row.id}</p>
            </div>
          </div>

          <div>
            <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-2">
              Error / Exception
            </p>
            <pre className="whitespace-pre-wrap break-all rounded-base border-2 border-border bg-secondary-background p-3 text-xs leading-relaxed font-mono max-h-96 overflow-y-auto">
              {row.errorMessage ?? "(no error message recorded)"}
            </pre>
          </div>

          <div className="flex gap-2 pt-2">
            <Button
              size="sm"
              onClick={() => {
                onRetry(row);
                onOpenChange(false);
              }}
            >
              Rewind &amp; retry
            </Button>
            <Button
              size="sm"
              variant="destructive"
              onClick={() => {
                onClear(row);
                onOpenChange(false);
              }}
            >
              Clear entries
            </Button>
          </div>
        </div>
      </SheetContent>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------
// Row actions
// ---------------------------------------------------------------------------

interface RowActionsProps {
  row: DeadLetterRow;
  onViewDetail: (row: DeadLetterRow) => void;
  onRetry: (row: DeadLetterRow) => void;
  onClear: (row: DeadLetterRow) => void;
}

function RowActions({ row, onViewDetail, onRetry, onClear }: RowActionsProps) {
  return (
    <div className="flex items-center justify-end gap-1">
      <Button size="sm" variant="ghost" onClick={() => onViewDetail(row)}>
        Details
      </Button>
      <Button size="sm" variant="outline" onClick={() => onRetry(row)}>
        Retry
      </Button>
      <Button size="sm" variant="destructive" onClick={() => onClear(row)}>
        Clear
      </Button>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Breakdown summary
// ---------------------------------------------------------------------------

function BreakdownSummary({ rows }: { rows: DeadLetterRow[] }) {
  const { byEventType, byProcessor } = useMemo(() => {
    const eventTypeCounts = new Map<string, number>();
    const processorCounts = new Map<string, number>();
    for (const row of rows) {
      const et = row.eventType ?? "(none)";
      eventTypeCounts.set(et, (eventTypeCounts.get(et) ?? 0) + 1);
      processorCounts.set(row.processorId, (processorCounts.get(row.processorId) ?? 0) + 1);
    }
    return {
      byEventType: [...eventTypeCounts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5),
      byProcessor: [...processorCounts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5),
    };
  }, [rows]);

  if (rows.length === 0) return null;

  return (
    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mb-4">
      <div className="rounded-base border-2 border-border p-4 bg-card">
        <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-3">
          By event type (top {byEventType.length})
        </p>
        <ul className="space-y-1.5">
          {byEventType.map(([type, count]) => (
            <li key={type} className="flex items-center justify-between gap-2">
              <span className="font-mono text-xs truncate">{type}</span>
              <Badge variant={count > 1 ? "danger" : "secondary"}>{count}</Badge>
            </li>
          ))}
        </ul>
      </div>
      <div className="rounded-base border-2 border-border p-4 bg-card">
        <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-3">
          By processor (top {byProcessor.length})
        </p>
        <ul className="space-y-1.5">
          {byProcessor.map(([id, count]) => (
            <li key={id} className="flex items-center justify-between gap-2">
              <span className="font-mono text-xs truncate">{id}</span>
              <Badge variant={count > 1 ? "danger" : "secondary"}>{count}</Badge>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main page component
// ---------------------------------------------------------------------------

export function DeadLettersPage() {
  // Filter state (committed on Apply)
  const [processorIdInput, setProcessorIdInput] = useState("");
  const [eventTypeInput, setEventTypeInput] = useState("");
  const [limitInput, setLimitInput] = useState("50");

  // Committed filter values (drive the query)
  const [processorIdFilter, setProcessorIdFilter] = useState<string | null>(null);
  const [eventTypeFilter, setEventTypeFilter] = useState<string | null>(null);
  const [limit, setLimit] = useState(50);

  // UI state
  const [detailRow, setDetailRow] = useState<DeadLetterRow | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  const [retryTarget, setRetryTarget] = useState<DeadLetterRow | null>(null);
  const [retryOpen, setRetryOpen] = useState(false);

  const [clearTarget, setClearTarget] = useState<DeadLetterRow | null>(null);
  const [clearOpen, setClearOpen] = useState(false);

  const [clearAllOpen, setClearAllOpen] = useState(false);

  // The tenant filter is a server-side argument, not a filter over the returned
  // page: this query is capped at `limit`, so filtering the rows after they arrive
  // would hide a quiet tenant's failures behind a noisy one's.
  const { module, tenant } = useAdminScope();

  // Queries
  const [deadLettersResult, reExecuteDeadLetters] = useQuery({
    query: DeadLettersQuery,
    variables: {
      processorId: processorIdFilter,
      eventType: eventTypeFilter,
      tenant,
      module,
      limit,
    },
  });

  const [countResult, reExecuteCount] = useQuery({
    query: DeadLetterCountQuery,
    variables: { module },
  });

  // Mutations
  const [retryResult, executeRetry] = useMutation(RetryByRewindMutation);
  const [clearProcessorResult, executeClearProcessor] = useMutation(ClearForProcessorMutation);
  const [clearAllResult, executeClearAll] = useMutation(ClearAllMutation);

  // Subscription — triggers re-query on any dead-letter change
  const [subResult] = useSubscription({
    query: DeadLettersChangedSub,
    variables: { module },
  });
  useEffect(() => {
    if (subResult.data) {
      reExecuteDeadLetters({ requestPolicy: "network-only" });
      reExecuteCount({ requestPolicy: "network-only" });
    }
  }, [subResult.data, reExecuteDeadLetters, reExecuteCount]);

  // Apply filters
  function applyFilters() {
    setProcessorIdFilter(processorIdInput.trim() || null);
    setEventTypeFilter(eventTypeInput.trim() || null);
    setLimit(Number(limitInput));
  }

  function clearFilters() {
    setProcessorIdInput("");
    setEventTypeInput("");
    setLimitInput("50");
    setProcessorIdFilter(null);
    setEventTypeFilter(null);
    setLimit(50);
  }

  // Handlers
  function handleViewDetail(row: DeadLetterRow) {
    setDetailRow(row);
    setDetailOpen(true);
  }

  function handleRetryClick(row: DeadLetterRow) {
    setRetryTarget(row);
    setRetryOpen(true);
  }

  function handleClearClick(row: DeadLetterRow) {
    setClearTarget(row);
    setClearOpen(true);
  }

  async function handleRetryConfirm() {
    if (!retryTarget) return;
    const result = await executeRetry({
      processorId: retryTarget.processorId,
      operatorId: OPERATOR_ID,
      module,
    });
    setRetryOpen(false);
    if (result.error) {
      toast.error(`Retry failed: ${result.error.message}`);
    } else {
      const deleted = result.data?.adminRetryByRewind.deletedCount ?? 0;
      const rewind = result.data?.adminRetryByRewind.rewindPosition;
      toast.success(
        `Rewound ${retryTarget.processorId} to position ${rewind !== null && rewind !== undefined ? rewind.toLocaleString() : "—"}, cleared ${deleted} dead letter${deleted !== 1 ? "s" : ""}.`
      );
      reExecuteDeadLetters({ requestPolicy: "network-only" });
      reExecuteCount({ requestPolicy: "network-only" });
    }
    setRetryTarget(null);
  }

  async function handleClearProcessorConfirm() {
    if (!clearTarget) return;
    const result = await executeClearProcessor({
      processorId: clearTarget.processorId,
      operatorId: OPERATOR_ID,
      module,
    });
    setClearOpen(false);
    if (result.error) {
      toast.error(`Clear failed: ${result.error.message}`);
    } else {
      const deleted = result.data?.adminClearDeadLettersForProcessor.deletedCount ?? 0;
      toast.success(`Cleared ${deleted} dead letter${deleted !== 1 ? "s" : ""} for ${clearTarget.processorId}.`);
      reExecuteDeadLetters({ requestPolicy: "network-only" });
      reExecuteCount({ requestPolicy: "network-only" });
    }
    setClearTarget(null);
  }

  async function handleClearAllConfirm() {
    const result = await executeClearAll({ operatorId: OPERATOR_ID, module });
    setClearAllOpen(false);
    if (result.error) {
      toast.error(`Clear all failed: ${result.error.message}`);
    } else {
      const deleted = result.data?.adminClearAllDeadLetters.deletedCount ?? 0;
      toast.success(`Cleared ${deleted} dead letter${deleted !== 1 ? "s" : ""} across all processors.`);
      reExecuteDeadLetters({ requestPolicy: "network-only" });
      reExecuteCount({ requestPolicy: "network-only" });
    }
  }

  // Column definitions
  const columns: ColumnDef<DeadLetterRow>[] = [
    {
      key: "processorId",
      header: "Processor",
      cell: (row) => (
        <span className="font-mono text-xs">{row.processorId}</span>
      ),
    },
    {
      key: "eventType",
      header: "Event type",
      cell: (row) =>
        row.eventType ? (
          <Badge variant="secondary" className="font-mono text-xs">
            {row.eventType}
          </Badge>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
    {
      key: "globalPosition",
      header: "Position",
      cell: (row) => (
        <span className="font-mono tabular-nums">{formatPosition(row.globalPosition)}</span>
      ),
      className: "tabular-nums",
    },
    {
      key: "failedAt",
      header: "Failed at",
      cell: (row) => <TimestampCell value={row.failedAt} />,
    },
    {
      key: "tenantId",
      header: "Tenant",
      cell: (row) =>
        row.tenantId ? (
          <span className="font-mono text-xs text-muted-foreground">{row.tenantId}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
    },
  ];

  const rows = deadLettersResult.data?.adminDeadLetters;
  const totalCount = countResult.data?.adminDeadLetterCount;
  const isLoading = deadLettersResult.fetching;
  const isError = Boolean(deadLettersResult.error);

  return (
    <div className="p-6">
      <PageHeader
        title="Dead Letters"
        eyebrow="Error Queue"
        description="Failed events that could not be processed. Retry-by-rewind re-processes them; clearing discards them permanently."
      >
        <Button
          variant="destructive"
          size="sm"
          onClick={() => setClearAllOpen(true)}
        >
          Clear all
        </Button>
      </PageHeader>

      {/* Headline stat */}
      <div className="mb-6">
        {countResult.fetching ? (
          <Skeleton className="h-20 w-40" />
        ) : (
          <StatCard
            label="Total dead letters"
            value={totalCount?.toLocaleString() ?? "—"}
            tone={
              totalCount === undefined
                ? "neutral"
                : totalCount === 0
                  ? "success"
                  : totalCount > 10
                    ? "danger"
                    : "warning"
            }
          />
        )}
      </div>

      {/* Filter bar */}
      <div className="mb-4 flex flex-wrap items-end gap-3">
        <div className="flex flex-col gap-1 min-w-[180px]">
          <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
            Processor ID
          </label>
          <Input
            placeholder="Filter by processor…"
            value={processorIdInput}
            onChange={(e) => setProcessorIdInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && applyFilters()}
            className="h-8 text-xs"
          />
        </div>
        <div className="flex flex-col gap-1 min-w-[180px]">
          <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
            Event type
          </label>
          <Input
            placeholder="Filter by event type…"
            value={eventTypeInput}
            onChange={(e) => setEventTypeInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && applyFilters()}
            className="h-8 text-xs"
          />
        </div>
        <div className="flex flex-col gap-1 w-24">
          <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
            Limit
          </label>
          <Select value={limitInput} onValueChange={setLimitInput}>
            <SelectTrigger className="h-8 text-xs">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="25">25</SelectItem>
              <SelectItem value="50">50</SelectItem>
              <SelectItem value="100">100</SelectItem>
              <SelectItem value="200">200</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div className="flex gap-2 pb-0.5">
          <Button size="sm" onClick={applyFilters}>
            Apply
          </Button>
          {(processorIdFilter || eventTypeFilter) && (
            <Button size="sm" variant="ghost" onClick={clearFilters}>
              Clear
            </Button>
          )}
        </div>
      </div>

      {/* Error state */}
      {isError && !isLoading && (
        <div className="mb-4 flex items-center gap-3 rounded-base border-2 border-border bg-danger/5 p-4 text-sm">
          <span className="text-danger font-semibold">Failed to load dead letters.</span>
          <Button
            size="sm"
            variant="outline"
            onClick={() => reExecuteDeadLetters({ requestPolicy: "network-only" })}
          >
            Retry
          </Button>
        </div>
      )}

      {/* Breakdown summary */}
      {rows && rows.length > 0 && <BreakdownSummary rows={rows} />}

      {/* Table */}
      <DataTable
        columns={columns}
        data={rows}
        rowKey={(row) => row.id}
        loading={isLoading}
        emptyMessage="No dead letters — all processors are healthy."
        rowAction={(row) => (
          <RowActions
            row={row}
            onViewDetail={handleViewDetail}
            onRetry={handleRetryClick}
            onClear={handleClearClick}
          />
        )}
      />

      {/* Detail sheet */}
      <DetailSheet
        row={detailRow}
        open={detailOpen}
        onOpenChange={setDetailOpen}
        onRetry={handleRetryClick}
        onClear={handleClearClick}
      />

      {/* Retry dialog */}
      <RetryDialog
        processorId={retryTarget?.processorId ?? ""}
        open={retryOpen}
        onOpenChange={setRetryOpen}
        onConfirm={handleRetryConfirm}
        loading={retryResult.fetching}
      />

      {/* Clear processor dialog */}
      <ClearProcessorDialog
        processorId={clearTarget?.processorId ?? ""}
        open={clearOpen}
        onOpenChange={setClearOpen}
        onConfirm={handleClearProcessorConfirm}
        loading={clearProcessorResult.fetching}
      />

      {/* Clear all dialog */}
      <ClearAllDialog
        open={clearAllOpen}
        onOpenChange={setClearAllOpen}
        onConfirm={handleClearAllConfirm}
        loading={clearAllResult.fetching}
      />
    </div>
  );
}
