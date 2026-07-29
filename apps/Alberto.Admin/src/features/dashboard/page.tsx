import { useEffect } from "react";
import { Link } from "react-router";
import { useQuery, useSubscription } from "urql";
import { RefreshCw } from "lucide-react";

import { graphql, type ResultOf } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { StatCard } from "@/components/ui/stat-card";
import { PageHeader } from "@/components/ui/page-header";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import {
  Tooltip,
  TooltipTrigger,
  TooltipContent,
  TooltipProvider,
} from "@/components/ui/tooltip";

// ── GraphQL documents ─────────────────────────────────────────────────────────

const SystemInfoQuery = graphql(`
  query DashboardSystemInfo($module: String) {
    adminSystemInfo(module: $module) {
      globalPosition
      processorCount
      deadLetterCount
      lastEventAt
    }
  }
`);

const CheckpointsOverviewQuery = graphql(`
  query DashboardCheckpoints($module: String) {
    adminCheckpoints(module: $module) {
      processorId
      lastPosition
      updatedAt
    }
    adminGlobalPosition(module: $module)
  }
`);

const SystemUpdatedSubscription = graphql(`
  subscription DashboardSystemUpdated($module: String) {
    onAdminSystemUpdated(module: $module) {
      globalPosition
      processorCount
      deadLetterCount
      lastEventAt
    }
  }
`);

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Display a Long (number in TS, may be string at runtime) without parseInt. */
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

/**
 * Compute lag as BigInt so arithmetic is safe whether the runtime value is a
 * JS number or a string (HotChocolate may serialise Long as string).
 */
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

// ── Row type (derived from query, no hand-written types) ──────────────────────

type CheckpointRow = ResultOf<
  typeof CheckpointsOverviewQuery
>["adminCheckpoints"][0] & { lag: bigint | null };

// ── Component ─────────────────────────────────────────────────────────────────

export function DashboardPage() {
  // Every figure on this page belongs to one event store. The module travels with
  // the queries rather than being applied after the fact, because two modules'
  // positions are unrelated counters and there is nothing to merge.
  const { module } = useAdminScope();

  // Queries
  const [sysQueryResult, reexecuteSys] = useQuery({
    query: SystemInfoQuery,
    variables: { module },
  });
  const [cpQueryResult, reexecuteCp] = useQuery({
    query: CheckpointsOverviewQuery,
    variables: { module },
  });

  // Subscription — keeps headline stats live without polling
  const [sysSubResult] = useSubscription({
    query: SystemUpdatedSubscription,
    variables: { module },
  });

  // Merge: subscription value wins once it fires (provides live updates)
  const systemInfo =
    sysSubResult.data?.onAdminSystemUpdated ??
    sysQueryResult.data?.adminSystemInfo;

  // A snapshot arriving means the store moved, so re-read the checkpoint table
  // too. Lag is measured against the global position, and without this the
  // headline cards go live while the table beneath them keeps comparing against
  // the position that was current when the page loaded — the worst kind of
  // staleness, because the page still looks fresh.
  useEffect(() => {
    if (!sysSubResult.data) return;
    reexecuteCp({ requestPolicy: "network-only" });
  }, [sysSubResult.data, reexecuteCp]);

  // Build rows sorted by lag descending (worst offenders first)
  const globalPos = cpQueryResult.data?.adminGlobalPosition;
  const checkpointRows: CheckpointRow[] = (
    cpQueryResult.data?.adminCheckpoints ?? []
  )
    .map((cp) => ({ ...cp, lag: computeLag(globalPos, cp.lastPosition) }))
    .sort((a, b) => {
      if (a.lag == null) return 1;
      if (b.lag == null) return -1;
      return a.lag > b.lag ? -1 : a.lag < b.lag ? 1 : 0;
    });

  const deadLetterCount =
    systemInfo?.deadLetterCount != null
      ? BigInt(String(systemInfo.deadLetterCount))
      : 0n;
  const hasDeadLetters = deadLetterCount > 0n;

  const sysLoading = sysQueryResult.fetching && !systemInfo;
  const hasError =
    (!!sysQueryResult.error && !systemInfo) ||
    (!!cpQueryResult.error && checkpointRows.length === 0);

  const lagColumns: ColumnDef<CheckpointRow>[] = [
    {
      key: "processorId",
      header: "Processor",
      cell: (row) => (
        <Link
          to="/checkpoints"
          className="font-mono text-xs text-foreground hover:underline"
        >
          {row.processorId}
        </Link>
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

  function handleRefresh() {
    reexecuteSys({ requestPolicy: "network-only" });
    reexecuteCp({ requestPolicy: "network-only" });
  }

  return (
    <TooltipProvider>
      <div className="p-6 space-y-6">
        <PageHeader
          title="Dashboard"
          eyebrow="System Status"
          description="Live overview of the event store health and processor lag."
        >
          <Button variant="outline" size="sm" onClick={handleRefresh}>
            <RefreshCw className="size-3.5" />
            Refresh
          </Button>
        </PageHeader>

        {/* Error banner */}
        {hasError && (
          <div className="flex items-center gap-3 rounded-base border-2 border-border bg-danger/10 px-4 py-3 text-sm text-danger">
            <span>Failed to load system data.</span>
            <Button variant="ghost" size="sm" onClick={handleRefresh}>
              Retry
            </Button>
          </div>
        )}

        {/* Headline stat cards */}
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
          {sysLoading ? (
            Array.from({ length: 4 }, (_, i) => (
              <Skeleton key={i} className="h-24 w-full" />
            ))
          ) : (
            <>
              <StatCard
                label="Global Position"
                value={
                  <span className="tabular-nums">
                    {formatLong(systemInfo?.globalPosition)}
                  </span>
                }
                tone="neutral"
              />

              <StatCard
                label="Active Processors"
                value={formatLong(systemInfo?.processorCount)}
                tone="neutral"
              />

              <StatCard
                label="Dead Letters"
                value={
                  hasDeadLetters ? (
                    <Link to="/dead-letters" className="hover:underline">
                      {formatLong(systemInfo?.deadLetterCount)}
                    </Link>
                  ) : (
                    formatLong(systemInfo?.deadLetterCount)
                  )
                }
                tone={hasDeadLetters ? "danger" : "success"}
                subtext={
                  hasDeadLetters ? (
                    <Link
                      to="/dead-letters"
                      className="underline text-danger hover:opacity-80"
                    >
                      View dead letters →
                    </Link>
                  ) : (
                    "All clear"
                  )
                }
              />

              <StatCard
                label="Last Event"
                value={
                  <Tooltip>
                    <TooltipTrigger asChild>
                      <span className="cursor-help">
                        {formatRelativeTime(systemInfo?.lastEventAt)}
                      </span>
                    </TooltipTrigger>
                    <TooltipContent>
                      {formatAbsolute(systemInfo?.lastEventAt)}
                    </TooltipContent>
                  </Tooltip>
                }
                tone="neutral"
              />
            </>
          )}
        </div>

        {/* Processor lag table */}
        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base tracking-tight">
                Processor Lag
              </h2>
              <p className="text-xs text-muted-foreground mt-0.5">
                Processors sorted by lag — worst offenders first.
              </p>
            </div>
            <Link
              to="/checkpoints"
              className="text-xs text-muted-foreground hover:underline shrink-0"
            >
              Manage checkpoints →
            </Link>
          </div>

          <DataTable
            columns={lagColumns}
            data={cpQueryResult.fetching ? undefined : checkpointRows}
            rowKey={(row) => row.processorId}
            loading={cpQueryResult.fetching}
            emptyMessage="No processors registered yet."
          />
        </div>
      </div>
    </TooltipProvider>
  );
}
