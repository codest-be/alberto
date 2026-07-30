import { useState, useEffect } from "react";
import { Link } from "react-router";
import { useQuery, useMutation, useSubscription } from "urql";
import { toast } from "sonner";
import { RefreshCw, RotateCcw, Pencil } from "lucide-react";

import { graphql, type ResultOf } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { useAuth } from "@/lib/auth";
import { PageHeader } from "@/components/ui/page-header";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
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
  TooltipProvider,
} from "@/components/ui/tooltip";

// ── GraphQL documents ─────────────────────────────────────────────────────────

const CheckpointsQuery = graphql(`
  query AdminCheckpointsList($module: String) {
    adminCheckpoints(module: $module) {
      processorId
      lastPosition
      updatedAt
    }
    adminGlobalPosition(module: $module)
  }
`);

const SetCheckpointMutation = graphql(`
  mutation AdminSetCheckpoint($processorId: String!, $position: Long!, $operatorId: String, $module: String) {
    adminSetCheckpoint(processorId: $processorId, position: $position, operatorId: $operatorId, module: $module) {
      processorId
      success
      message
    }
  }
`);

const ResetCheckpointMutation = graphql(`
  mutation AdminResetCheckpoint($processorId: String!, $operatorId: String, $module: String) {
    adminResetCheckpoint(processorId: $processorId, operatorId: $operatorId, module: $module) {
      processorId
      success
      message
    }
  }
`);

const CheckpointUpdatedSubscription = graphql(`
  subscription CheckpointUpdated($module: String) {
    onAdminCheckpointUpdated(module: $module) {
      processorId
      success
      message
    }
  }
`);

// ── Helpers ───────────────────────────────────────────────────────────────────

function formatLong(value: number | null | undefined): string {
  if (value == null) return "—";
  return String(value);
}

function formatRelativeTime(dateStr: string | null | undefined): string {
  if (!dateStr) return "Never";
  const diffMs = new Date(dateStr).getTime() - Date.now();
  const diffSecs = Math.round(diffMs / 1000);
  const fmt = new Intl.RelativeTimeFormat("en", { numeric: "auto" });
  const absSecs = Math.abs(diffSecs);
  if (absSecs < 60) return fmt.format(diffSecs, "second");
  const diffMins = Math.round(diffSecs / 60);
  if (Math.abs(diffMins) < 60) return fmt.format(diffMins, "minute");
  const diffHours = Math.round(diffMins / 60);
  if (Math.abs(diffHours) < 24) return fmt.format(diffHours, "hour");
  return fmt.format(Math.round(diffHours / 24), "day");
}

function formatAbsolute(dateStr: string | null | undefined): string {
  if (!dateStr) return "Never";
  return new Date(dateStr).toLocaleString();
}

function computeLag(
  globalPos: number | null | undefined,
  lastPos: number,
): bigint | null {
  if (globalPos == null) return null;
  try {
    const lag = BigInt(String(globalPos)) - BigInt(String(lastPos));
    return lag < 0n ? 0n : lag;
  } catch {
    return null;
  }
}

function formatLag(lag: bigint | null): string {
  if (lag == null) return "—";
  return lag.toLocaleString();
}

type LagVariant = "success" | "warning" | "danger" | "secondary";
function lagVariant(lag: bigint | null): LagVariant {
  if (lag == null) return "secondary";
  if (lag <= 100n) return "success";
  if (lag < 5000n) return "warning";
  return "danger";
}

function isValidPosition(s: string): boolean {
  return /^\d+$/.test(s.trim()) && s.trim().length > 0;
}

// ── Types ─────────────────────────────────────────────────────────────────────

type CheckpointRow = ResultOf<typeof CheckpointsQuery>["adminCheckpoints"][0] & {
  lag: bigint | null;
};

type DialogState =
  | { kind: "none" }
  | { kind: "set"; row: CheckpointRow; posInput: string }
  | { kind: "reset"; row: CheckpointRow };

// ── Component ─────────────────────────────────────────────────────────────────

export function CheckpointsPage() {
  const { state: authState } = useAuth();
  const operatorId =
    authState.status === "authenticated" ? authState.user.name : undefined;

  const [dialog, setDialog] = useState<DialogState>({ kind: "none" });
  const [isMutating, setIsMutating] = useState(false);

  // Checkpoints belong to one event store — a processor ID can exist in both
  // modules, at unrelated positions.
  const { module } = useAdminScope();

  // Queries
  const [queryResult, reexecute] = useQuery({
    query: CheckpointsQuery,
    variables: { module },
  });

  // Mutations
  const [, executeSet] = useMutation(SetCheckpointMutation);
  const [, executeReset] = useMutation(ResetCheckpointMutation);

  // Subscription — fires when any client performs a set/reset
  const [cpSubResult] = useSubscription({
    query: CheckpointUpdatedSubscription,
    variables: { module },
  });

  // Refetch list when the subscription reports an external change
  const cpSubData = cpSubResult.data;
  useEffect(() => {
    if (cpSubData != null) {
      reexecute({ requestPolicy: "network-only" });
    }
  }, [cpSubData, reexecute]);

  // Build rows
  const globalPos = queryResult.data?.adminGlobalPosition;
  const rows: CheckpointRow[] = (queryResult.data?.adminCheckpoints ?? []).map(
    (cp) => ({ ...cp, lag: computeLag(globalPos, cp.lastPosition) }),
  );

  // ── Mutation handlers ───────────────────────────────────────────────────────

  async function handleSetConfirm() {
    if (dialog.kind !== "set") return;
    if (!isValidPosition(dialog.posInput)) return;
    const { row, posInput } = dialog;
    setIsMutating(true);
    const result = await executeSet({
      processorId: row.processorId,
      position: Number(posInput.trim()),
      operatorId,
      module,
    });
    setIsMutating(false);
    if (result.error) {
      toast.error(`Failed: ${result.error.message}`);
    } else if (result.data?.adminSetCheckpoint.success) {
      toast.success(
        `Checkpoint for "${row.processorId}" moved to position ${posInput.trim()}.`,
      );
      setDialog({ kind: "none" });
      reexecute({ requestPolicy: "network-only" });
    } else {
      toast.error(
        result.data?.adminSetCheckpoint.message ?? "Server returned failure.",
      );
    }
  }

  async function handleResetConfirm() {
    if (dialog.kind !== "reset") return;
    const { row } = dialog;
    setIsMutating(true);
    const result = await executeReset({
      processorId: row.processorId,
      operatorId,
      module,
    });
    setIsMutating(false);
    if (result.error) {
      toast.error(`Failed: ${result.error.message}`);
    } else if (result.data?.adminResetCheckpoint.success) {
      toast.success(
        `Checkpoint for "${row.processorId}" has been deleted. It will replay from position 0.`,
      );
      setDialog({ kind: "none" });
      reexecute({ requestPolicy: "network-only" });
    } else {
      toast.error(
        result.data?.adminResetCheckpoint.message ?? "Server returned failure.",
      );
    }
  }

  // ── Column definitions ──────────────────────────────────────────────────────

  const columns: ColumnDef<CheckpointRow>[] = [
    {
      key: "processorId",
      header: "Processor",
      cell: (row) => (
        <span className="font-mono text-xs">{row.processorId}</span>
      ),
    },
    {
      key: "position",
      header: "Position",
      cell: (row) => (
        <span className="font-mono text-xs tabular-nums">
          {formatLong(row.lastPosition)}
        </span>
      ),
    },
    {
      key: "lag",
      header: "Lag",
      cell: (row) => (
        <Badge variant={lagVariant(row.lag)}>{formatLag(row.lag)}</Badge>
      ),
    },
    {
      key: "updatedAt",
      header: "Last Updated",
      cell: (row) => (
        <Tooltip>
          <TooltipTrigger asChild>
            <span className="cursor-help text-xs text-muted-foreground">
              {formatRelativeTime(row.updatedAt)}
            </span>
          </TooltipTrigger>
          <TooltipContent>{formatAbsolute(row.updatedAt)}</TooltipContent>
        </Tooltip>
      ),
    },
  ];

  const hasError = !!queryResult.error && rows.length === 0;

  // ── Render ──────────────────────────────────────────────────────────────────

  return (
    <TooltipProvider>
      <div className="p-6 space-y-6">
        <PageHeader
          title="Checkpoints"
          eyebrow="Event Processors"
          description="Processor positions in the event log and their lag behind the global position."
        >
          <Button
            variant="outline"
            size="sm"
            onClick={() => reexecute({ requestPolicy: "network-only" })}
          >
            <RefreshCw className="size-3.5" />
            Refresh
          </Button>
        </PageHeader>

        {/* Error state */}
        {hasError && (
          <div className="flex items-center gap-3 rounded-base border-2 border-border bg-danger/10 px-4 py-3 text-sm text-danger">
            <span>Failed to load checkpoints.</span>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => reexecute({ requestPolicy: "network-only" })}
            >
              Retry
            </Button>
          </div>
        )}

        {/* Global position context */}
        {!queryResult.fetching && globalPos != null && (
          <p className="text-xs text-muted-foreground">
            Global position:{" "}
            <span className="font-mono tabular-nums">{formatLong(globalPos)}</span>.
            Lag is how far each processor is behind.
          </p>
        )}

        <DataTable
          columns={columns}
          data={queryResult.fetching ? undefined : rows}
          rowKey={(row) => row.processorId}
          loading={queryResult.fetching}
          emptyMessage="No processor checkpoints found."
          rowAction={(row) => (
            <div className="flex items-center justify-end gap-2">
              <Button
                variant="outline"
                size="sm"
                onClick={() =>
                  setDialog({ kind: "set", row, posInput: "" })
                }
              >
                <Pencil className="size-3.5" />
                Set position
              </Button>
              <Button
                variant="destructive"
                size="sm"
                onClick={() => setDialog({ kind: "reset", row })}
              >
                <RotateCcw className="size-3.5" />
                Reset
              </Button>
            </div>
          )}
        />
      </div>

      {/* ── Set-position dialog ──────────────────────────────────────────────── */}
      <Dialog
        open={dialog.kind === "set"}
        onOpenChange={(open) => {
          if (!open) setDialog({ kind: "none" });
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Set checkpoint position</DialogTitle>
            <DialogDescription asChild>
              <div className="space-y-3 text-sm text-muted-foreground">
                <p>
                  You are about to move{" "}
                  <span className="font-mono font-semibold text-foreground">
                    {dialog.kind === "set" ? dialog.row.processorId : ""}
                  </span>{" "}
                  to a new position in the event log.
                </p>
                <div className="rounded-base border-2 border-border bg-warning/10 px-3 py-2 text-warning text-xs">
                  <strong>Warning:</strong> The processor will re-consume every
                  event after this position. If any handler is not idempotent,
                  it will produce duplicate side effects — sent emails, created
                  records, published messages.
                </div>
                <p className="text-xs">
                  Current position:{" "}
                  <span className="font-mono tabular-nums">
                    {dialog.kind === "set"
                      ? formatLong(dialog.row.lastPosition)
                      : ""}
                  </span>
                </p>
              </div>
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-1 py-2">
            <label className="text-xs font-semibold text-foreground">
              Target position
            </label>
            <Input
              type="text"
              inputMode="numeric"
              placeholder="e.g. 4200"
              value={dialog.kind === "set" ? dialog.posInput : ""}
              onChange={(e) => {
                if (dialog.kind === "set") {
                  setDialog({ ...dialog, posInput: e.target.value });
                }
              }}
            />
            {dialog.kind === "set" &&
              dialog.posInput !== "" &&
              !isValidPosition(dialog.posInput) && (
                <p className="text-xs text-danger">
                  Must be a non-negative integer.
                </p>
              )}
          </div>

          <DialogFooter>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setDialog({ kind: "none" })}
              disabled={isMutating}
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              size="sm"
              disabled={
                isMutating ||
                dialog.kind !== "set" ||
                !isValidPosition(dialog.posInput)
              }
              onClick={() => void handleSetConfirm()}
            >
              {isMutating ? "Setting…" : "Set position"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ── Reset dialog ─────────────────────────────────────────────────────── */}
      <Dialog
        open={dialog.kind === "reset"}
        onOpenChange={(open) => {
          if (!open) setDialog({ kind: "none" });
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Reset checkpoint</DialogTitle>
            <DialogDescription asChild>
              <div className="space-y-3 text-sm text-muted-foreground">
                <p>
                  You are about to delete the checkpoint for{" "}
                  <span className="font-mono font-semibold text-foreground">
                    {dialog.kind === "reset" ? dialog.row.processorId : ""}
                  </span>
                  .
                </p>
                <div className="rounded-base border-2 border-border bg-danger/10 px-3 py-2 text-danger text-xs space-y-1">
                  <p>
                    <strong>This deletes the checkpoint entirely.</strong> The
                    processor will replay from position 0, re-consuming every
                    event in the entire log. If any handler is not idempotent,
                    it will produce duplicate side effects — sent emails,
                    created records, published messages.
                  </p>
                </div>
                <div className="rounded-base border-2 border-border bg-secondary-background px-3 py-2 text-xs">
                  <strong>Safer for projections:</strong> A{" "}
                  <Link
                    to="/rebuilds"
                    className="underline text-foreground hover:opacity-80"
                    onClick={() => setDialog({ kind: "none" })}
                  >
                    zero-downtime rebuild
                  </Link>{" "}
                  replays into a shadow copy and swaps atomically, avoiding
                  downtime and the duplicate side-effect risk that a reset
                  causes.
                </div>
              </div>
            </DialogDescription>
          </DialogHeader>

          <DialogFooter>
            <Button
              variant="outline"
              size="sm"
              onClick={() => setDialog({ kind: "none" })}
              disabled={isMutating}
            >
              Cancel
            </Button>
            <Button
              variant="destructive"
              size="sm"
              disabled={isMutating}
              onClick={() => void handleResetConfirm()}
            >
              {isMutating ? "Resetting…" : "Delete checkpoint & replay from 0"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </TooltipProvider>
  );
}
