import { useState, useEffect, type ChangeEvent } from "react";
import { useSubscription } from "urql";
import { graphql } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import type { ResultOf } from "@/lib/graphql";
import { useWsStatus } from "@/lib/ws-status";
import { PageHeader } from "@/components/ui/page-header";
import { DataTable } from "@/components/ui/data-table";
import type { ColumnDef } from "@/components/ui/data-table";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";

// ---------------------------------------------------------------------------
// GraphQL document
// ---------------------------------------------------------------------------

const AuditSubscription = graphql(`
  subscription AuditEvents($module: String) {
    onAdminAuditEvent(module: $module) {
      eventType
      operatorId
      description
      timestamp
    }
  }
`);

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Maximum number of entries retained in the in-memory buffer. */
const BUFFER_CAP = 300;

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type AuditEventData = ResultOf<
  typeof AuditSubscription
>["onAdminAuditEvent"];

/** An audit entry with a client-generated stable key for React. */
type AuditEntry = AuditEventData & { _key: string };

// ---------------------------------------------------------------------------
// Helpers
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

// ---------------------------------------------------------------------------
// Column definitions
// ---------------------------------------------------------------------------

const COLUMNS: ColumnDef<AuditEntry>[] = [
  {
    key: "eventType",
    header: "Event Type",
    cell: (row) => (
      <Badge variant="secondary" className="font-mono text-xs">
        {row.eventType}
      </Badge>
    ),
  },
  {
    key: "operatorId",
    header: "Operator",
    cell: (row) => (
      <span className="font-mono text-xs">{row.operatorId}</span>
    ),
  },
  {
    key: "description",
    header: "Description",
    cell: (row) => (
      <span className="text-sm text-foreground">{row.description}</span>
    ),
    className: "max-w-[420px]",
  },
  {
    key: "timestamp",
    header: "Time",
    cell: (row) => <TimestampCell iso={row.timestamp} />,
  },
];

// ---------------------------------------------------------------------------
// Connection status badge
// ---------------------------------------------------------------------------

type ConnectionStatus = "connecting" | "connected" | "error" | "disconnected";

function ConnectionStatusBadge({ status }: { status: ConnectionStatus }) {
  const map: Record<
    ConnectionStatus,
    { label: string; variant: "success" | "warning" | "danger" | "secondary" }
  > = {
    connected: { label: "Live", variant: "success" },
    connecting: { label: "Connecting…", variant: "warning" },
    error: { label: "Stream error", variant: "danger" },
    disconnected: { label: "Disconnected", variant: "secondary" },
  };
  const { label, variant } = map[status];
  return (
    <Badge variant={variant} className="shrink-0">
      {status === "connected" && (
        <span className="mr-1 inline-block size-1.5 rounded-full bg-success-foreground animate-pulse" />
      )}
      {label}
    </Badge>
  );
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export function AuditPage() {
  // The audit stream is per module — the server publishes to a module-scoped
  // topic, so switching stores switches which operators' actions arrive.
  const { module } = useAdminScope();

  // Accumulated buffer — newest first
  const [entries, setEntries] = useState<AuditEntry[]>([]);

  // Client-side filters (the buffer is the full universe of data)
  const [operatorFilter, setOperatorFilter] = useState("");
  const [eventTypeFilter, setEventTypeFilter] = useState("");

  // ---------------------------------------------------------------------------
  // Subscription
  // ---------------------------------------------------------------------------

  const [result] = useSubscription({
    query: AuditSubscription,
    variables: { module },
  });
  const wsStatus = useWsStatus();

  // The buffer is history, not a query result, so nothing clears it on its own.
  // Left alone it would keep one store's entries on screen under another
  // store's heading — an audit log that attributes actions to the wrong system.
  useEffect(() => {
    setEntries([]);
  }, [module]);

  // Accumulate incoming events into the buffer as they arrive
  useEffect(() => {
    const event = result.data?.onAdminAuditEvent;
    if (!event) return;
    setEntries((prev) => {
      const entry: AuditEntry = { ...event, _key: crypto.randomUUID() };
      return [entry, ...prev].slice(0, BUFFER_CAP);
    });
  }, [result.data]);

  // ---------------------------------------------------------------------------
  // Derived state
  // ---------------------------------------------------------------------------

  // Liveness comes from the WebSocket transport, not from result.fetching —
  // urql clears that flag as soon as a payload arrives, which made a healthy
  // stream label itself "Disconnected" the instant it delivered its first event.
  const status: ConnectionStatus = result.error
    ? "error"
    : wsStatus === "connected"
      ? "connected"
      : wsStatus === "connecting"
        ? "connecting"
        : "disconnected";

  const filteredEntries = entries.filter((e) => {
    if (
      operatorFilter &&
      !e.operatorId.toLowerCase().includes(operatorFilter.toLowerCase())
    )
      return false;
    if (
      eventTypeFilter &&
      !e.eventType.toLowerCase().includes(eventTypeFilter.toLowerCase())
    )
      return false;
    return true;
  });

  const handleOperatorChange = (ev: ChangeEvent<HTMLInputElement>) =>
    setOperatorFilter(ev.target.value);
  const handleEventTypeChange = (ev: ChangeEvent<HTMLInputElement>) =>
    setEventTypeFilter(ev.target.value);

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  return (
    <TooltipProvider>
      <div className="p-6">
        <PageHeader
          title="Audit"
          eyebrow="Admin Audit Trail"
          description="Live stream of admin actions. History before this page was opened is not retained — audit events are never persisted."
        >
          <ConnectionStatusBadge status={status} />
        </PageHeader>

        {/* ------------------------------------------------------------------ */}
        {/* Buffer cap notice + filters                                         */}
        {/* ------------------------------------------------------------------ */}
        <div className="mb-4 flex flex-wrap gap-3 items-end justify-between">
          <div className="flex flex-wrap gap-3 items-end">
            <div className="flex flex-col gap-1 min-w-[180px]">
              <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
                Filter by Operator
              </label>
              <Input
                placeholder="Operator ID…"
                value={operatorFilter}
                onChange={handleOperatorChange}
              />
            </div>

            <div className="flex flex-col gap-1 min-w-[180px]">
              <label className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
                Filter by Event Type
              </label>
              <Input
                placeholder="Event type…"
                value={eventTypeFilter}
                onChange={handleEventTypeChange}
              />
            </div>
          </div>

          <p className="text-xs text-muted-foreground self-end">
            Buffer capped at {BUFFER_CAP} entries. Oldest entries are discarded
            first.
          </p>
        </div>

        {/* ------------------------------------------------------------------ */}
        {/* Error state                                                         */}
        {/* ------------------------------------------------------------------ */}
        {result.error && (
          <div className="mb-3 rounded-base border-2 border-border bg-destructive/10 px-3 py-2 text-sm text-destructive">
            Subscription error: {result.error.message}. Urql will attempt to
            reconnect automatically.
          </div>
        )}

        {/* ------------------------------------------------------------------ */}
        {/* Table                                                               */}
        {/* ------------------------------------------------------------------ */}
        <DataTable
          columns={COLUMNS}
          data={filteredEntries}
          rowKey={(row) => row._key}
          loading={false}
          emptyMessage={
            entries.length === 0
              ? status === "connecting"
                ? "Connecting to audit stream…"
                : "No admin actions have occurred since this page was opened. This stream shows actions in real time — history before the page was opened is not retained."
              : "No entries match the current filters."
          }
        />
      </div>
    </TooltipProvider>
  );
}
