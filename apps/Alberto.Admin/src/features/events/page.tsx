import { useState, useEffect, useRef } from "react";
import { useQuery } from "urql";
import { ChevronDown, RefreshCw, Search, X } from "lucide-react";
import { graphql } from "@/lib/graphql";
import { useAdminScope } from "@/lib/admin-scope";
import { PageHeader } from "@/components/ui/page-header";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { DataTable, type ColumnDef } from "@/components/ui/data-table";
import { Input } from "@/components/ui/input";
import {
  Sheet,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetDescription,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { Card, CardContent } from "@/components/ui/card";

// ---------------------------------------------------------------------------
// GraphQL documents
// ---------------------------------------------------------------------------

const AdminEventsQuery = graphql(`
  query AdminEvents(
    $eventType: String
    $tag: String
    $tenant: String
    $module: String
    $afterPosition: Long!
    $limit: Int!
  ) {
    adminEvents(
      eventType: $eventType
      tag: $tag
      tenant: $tenant
      module: $module
      afterPosition: $afterPosition
      limit: $limit
    ) {
      globalPosition
      eventType
      tags
      createdAt
      tenantId
    }
  }
`);

const GlobalPositionQuery = graphql(`
  query AdminGlobalPosition($module: String) {
    adminGlobalPosition(module: $module)
  }
`);

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type EventRow = {
  globalPosition: number;
  eventType: string;
  tags: string | null;
  createdAt: string | null;
  tenantId: string | null;
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const LIMIT = 50;

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

function parseTags(tags: string | null): string[] {
  if (!tags) return [];
  return tags
    .split(",")
    .map((t) => t.trim())
    .filter(Boolean);
}

function formatPosition(pos: number): string {
  // pos is a Long (number). Use toLocaleString for readable display.
  return pos.toLocaleString();
}

// ---------------------------------------------------------------------------
// Sub-components
// ---------------------------------------------------------------------------

function TimestampCell({ value }: { value: string | null | undefined }) {
  if (!value) return <span className="text-muted-foreground">—</span>;
  return (
    <TooltipProvider>
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="cursor-default tabular-nums">{formatRelative(value)}</span>
        </TooltipTrigger>
        <TooltipContent>{formatAbsolute(value)}</TooltipContent>
      </Tooltip>
    </TooltipProvider>
  );
}

interface TagPillsProps {
  tags: string | null;
  activeTag: string;
  onTagClick: (tag: string) => void;
}

function TagPills({ tags, activeTag, onTagClick }: TagPillsProps) {
  const parts = parseTags(tags);
  if (parts.length === 0) {
    return <span className="text-muted-foreground text-xs">—</span>;
  }
  return (
    <div className="flex flex-wrap gap-1">
      {parts.map((tag) => (
        <button
          key={tag}
          type="button"
          onClick={() => onTagClick(tag)}
          className="cursor-pointer"
          title={`Filter by tag: ${tag}`}
        >
          <Badge
            variant={activeTag === tag ? "default" : "secondary"}
            className="text-[10px] hover:opacity-80 transition-opacity"
          >
            {tag}
          </Badge>
        </button>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Event detail sheet
// ---------------------------------------------------------------------------

interface EventDetailSheetProps {
  event: EventRow | null;
  onClose: () => void;
  activeTag: string;
  onTagClick: (tag: string) => void;
}

function EventDetailSheet({
  event,
  onClose,
  activeTag,
  onTagClick,
}: EventDetailSheetProps) {
  const tags = parseTags(event?.tags ?? null);

  return (
    <Sheet open={!!event} onOpenChange={(v) => !v && onClose()}>
      <SheetContent side="right" className="w-full sm:max-w-lg overflow-y-auto">
        <SheetHeader className="mb-6">
          <SheetTitle>Event Details</SheetTitle>
          <SheetDescription>
            Position {event ? formatPosition(event.globalPosition) : ""}
          </SheetDescription>
        </SheetHeader>

        {event && (
          <div className="space-y-4 text-sm">
            {/* Fields */}
            <div className="rounded-base border-2 border-border overflow-hidden">
              <table className="w-full">
                <tbody>
                  <tr className="border-b-2 border-border">
                    <td className="px-3 py-2 font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground bg-secondary-background w-32">
                      Position
                    </td>
                    <td className="px-3 py-2 font-mono tabular-nums">
                      {formatPosition(event.globalPosition)}
                    </td>
                  </tr>
                  <tr className="border-b-2 border-border">
                    <td className="px-3 py-2 font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground bg-secondary-background">
                      Event type
                    </td>
                    <td className="px-3 py-2 font-mono">{event.eventType}</td>
                  </tr>
                  <tr className="border-b-2 border-border">
                    <td className="px-3 py-2 font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground bg-secondary-background">
                      Tenant
                    </td>
                    <td className="px-3 py-2">
                      {event.tenantId ? (
                        <span className="font-mono">{event.tenantId}</span>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </td>
                  </tr>
                  <tr className="border-b-2 border-border">
                    <td className="px-3 py-2 font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground bg-secondary-background">
                      Timestamp
                    </td>
                    <td className="px-3 py-2">
                      {event.createdAt ? (
                        <span className="tabular-nums">{formatAbsolute(event.createdAt)}</span>
                      ) : (
                        <span className="text-muted-foreground">—</span>
                      )}
                    </td>
                  </tr>
                  <tr>
                    <td className="px-3 py-2 font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground bg-secondary-background align-top">
                      Tags
                    </td>
                    <td className="px-3 py-2">
                      {tags.length === 0 ? (
                        <span className="text-muted-foreground">—</span>
                      ) : (
                        <div className="flex flex-wrap gap-1">
                          {tags.map((tag) => (
                            <button
                              key={tag}
                              type="button"
                              onClick={() => {
                                onTagClick(tag);
                                onClose();
                              }}
                              title={`Filter by tag: ${tag}`}
                            >
                              <Badge
                                variant={activeTag === tag ? "default" : "secondary"}
                                className="text-[10px] hover:opacity-80 transition-opacity cursor-pointer"
                              >
                                {tag}
                              </Badge>
                            </button>
                          ))}
                        </div>
                      )}
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>

            {/* Raw tags string if present */}
            {event.tags && (
              <div>
                <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1">
                  Raw tags
                </p>
                <pre className="rounded-base border-2 border-border bg-secondary-background p-3 text-xs font-mono overflow-x-auto whitespace-pre-wrap break-all">
                  {event.tags}
                </pre>
              </div>
            )}

            <p className="text-xs text-muted-foreground">
              Note: event payload is not available through the admin API at this
              time. Use the event type and tags to correlate with your domain
              event definitions.
            </p>
          </div>
        )}
      </SheetContent>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------
// Head position indicator
// ---------------------------------------------------------------------------

function HeadPositionBadge() {
  const { module } = useAdminScope();
  const [{ data, fetching }] = useQuery({
    query: GlobalPositionQuery,
    variables: { module },
  });

  if (fetching) {
    return <Skeleton className="h-6 w-32" />;
  }

  const pos = data?.adminGlobalPosition;
  return (
    <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
      <span>Log head:</span>
      {pos != null ? (
        <Badge variant="secondary" className="tabular-nums font-mono">
          {formatPosition(pos)}
        </Badge>
      ) : (
        <Badge variant="secondary">No events</Badge>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Main page
// ---------------------------------------------------------------------------

export function EventsPage() {
  // The tenant filter is a query argument rather than a filter over the rows on
  // screen: this page reads the log 50 events at a time, so narrowing after the
  // fact would show one page's worth of a busy tenant and none of a quiet one.
  const { module, tenant } = useAdminScope();

  // Filter state
  const [filterEventType, setFilterEventType] = useState("");
  const [filterTag, setFilterTag] = useState("");

  // Pagination
  const [afterPosition, setAfterPosition] = useState<number>(0);
  const [allEvents, setAllEvents] = useState<EventRow[]>([]);

  // Track whether the upcoming data arrival should append or replace
  const appendOnNextDataRef = useRef(false);

  // Detail sheet
  const [selectedEvent, setSelectedEvent] = useState<EventRow | null>(null);

  const [{ data, fetching, error }, reexecute] = useQuery({
    query: AdminEventsQuery,
    variables: {
      eventType: filterEventType || null,
      tag: filterTag || null,
      tenant,
      module,
      afterPosition,
      limit: LIMIT,
    },
  });

  // Positions are per store and tenants are per store, so a page cursor means
  // nothing after either changes. Without this the accumulated list would keep
  // the previous scope's rows and resume paging from its position.
  useEffect(() => {
    appendOnNextDataRef.current = false;
    setAfterPosition(0);
    setAllEvents([]);
  }, [module, tenant]);

  // Accumulate events on data change
  useEffect(() => {
    if (!data?.adminEvents) return;
    const incoming = data.adminEvents as EventRow[];
    if (appendOnNextDataRef.current) {
      setAllEvents((prev) => [...prev, ...incoming]);
    } else {
      setAllEvents([...incoming]);
    }
    appendOnNextDataRef.current = false;
  }, [data]);

  function applyEventTypeFilter(value: string) {
    appendOnNextDataRef.current = false;
    setFilterEventType(value);
    setAfterPosition(0);
    setAllEvents([]);
  }

  function applyTagFilter(value: string) {
    appendOnNextDataRef.current = false;
    setFilterTag(value);
    setAfterPosition(0);
    setAllEvents([]);
  }

  function clearFilters() {
    appendOnNextDataRef.current = false;
    setFilterEventType("");
    setFilterTag("");
    setAfterPosition(0);
    setAllEvents([]);
  }

  function handleLoadMore() {
    const last = allEvents[allEvents.length - 1];
    if (!last) return;
    appendOnNextDataRef.current = true;
    setAfterPosition(last.globalPosition);
  }

  function handleRefresh() {
    appendOnNextDataRef.current = false;
    setAfterPosition(0);
    setAllEvents([]);
    reexecute({ requestPolicy: "network-only" });
  }

  const hasMore =
    !fetching &&
    !error &&
    (data?.adminEvents?.length ?? 0) === LIMIT;

  const hasActiveFilters = !!filterEventType || !!filterTag;

  const columns: ColumnDef<EventRow>[] = [
    {
      key: "globalPosition",
      header: "Position",
      cell: (row) => (
        <span className="font-mono tabular-nums text-xs text-muted-foreground">
          {formatPosition(row.globalPosition)}
        </span>
      ),
      className: "w-32",
    },
    {
      key: "eventType",
      header: "Event type",
      cell: (row) => (
        <button
          type="button"
          className="text-left hover:underline font-mono text-xs"
          onClick={() => applyEventTypeFilter(row.eventType)}
          title={`Filter by type: ${row.eventType}`}
        >
          {row.eventType}
        </button>
      ),
    },
    {
      key: "tags",
      header: "Tags",
      cell: (row) => (
        <TagPills
          tags={row.tags}
          activeTag={filterTag}
          onTagClick={applyTagFilter}
        />
      ),
    },
    {
      key: "tenantId",
      header: "Tenant",
      cell: (row) =>
        row.tenantId ? (
          <span className="font-mono text-xs">{row.tenantId}</span>
        ) : (
          <span className="text-muted-foreground">—</span>
        ),
      className: "w-36",
    },
    {
      key: "createdAt",
      header: "Timestamp",
      cell: (row) => <TimestampCell value={row.createdAt} />,
      className: "w-32",
    },
  ];

  return (
    <div className="p-6 space-y-6">
      <PageHeader
        title="Event Log"
        eyebrow="DCB Event Store"
        description="Browse the append-only event log. Filter by event type or tag — filters are server-side."
      >
        <HeadPositionBadge />
        <Button
          variant="outline"
          size="sm"
          onClick={handleRefresh}
          disabled={fetching}
        >
          <RefreshCw className={fetching ? "animate-spin" : ""} />
          Refresh
        </Button>
      </PageHeader>

      {/* Filter bar */}
      <div className="flex flex-wrap gap-3 items-center">
        <div className="relative w-52">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 size-3.5 text-muted-foreground pointer-events-none" />
          <Input
            className="pl-8"
            placeholder="Event type…"
            value={filterEventType}
            onChange={(e) => applyEventTypeFilter(e.target.value)}
          />
        </div>
        <div className="relative w-52">
          <Search className="absolute left-2.5 top-1/2 -translate-y-1/2 size-3.5 text-muted-foreground pointer-events-none" />
          <Input
            className="pl-8"
            placeholder="Tag filter…"
            value={filterTag}
            onChange={(e) => applyTagFilter(e.target.value)}
          />
        </div>
        {hasActiveFilters && (
          <Button variant="ghost" size="sm" onClick={clearFilters}>
            <X className="size-3" />
            Clear filters
          </Button>
        )}
        {hasActiveFilters && (
          <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
            {filterEventType && (
              <Badge variant="outline">type: {filterEventType}</Badge>
            )}
            {filterTag && <Badge variant="outline">tag: {filterTag}</Badge>}
          </div>
        )}
      </div>

      {/* Error */}
      {error && (
        <Card>
          <CardContent className="pt-6">
            <p className="text-sm text-destructive font-semibold mb-1">
              Failed to load events
            </p>
            <p className="text-xs text-muted-foreground mb-4">{error.message}</p>
            <Button variant="outline" size="sm" onClick={handleRefresh}>
              <RefreshCw className="size-3" /> Retry
            </Button>
          </CardContent>
        </Card>
      )}

      {/* Event table */}
      {!error && (
        <DataTable
          columns={columns}
          data={allEvents.length > 0 || !fetching ? allEvents : undefined}
          rowKey={(row) => String(row.globalPosition)}
          loading={fetching && allEvents.length === 0}
          emptyMessage={
            hasActiveFilters
              ? "No events match the current filters."
              : "No events in the log yet."
          }
          rowAction={(row) => (
            <Button
              variant="muted"
              size="sm"
              onClick={() => setSelectedEvent(row)}
            >
              Details
              <ChevronDown className="size-3 -rotate-90" />
            </Button>
          )}
        />
      )}

      {/* Load more */}
      {allEvents.length > 0 && (
        <div className="flex items-center justify-between pt-1">
          <span className="text-xs text-muted-foreground">
            Showing {allEvents.length.toLocaleString()} event
            {allEvents.length !== 1 ? "s" : ""}
            {afterPosition > 0 &&
              ` · after position ${formatPosition(afterPosition)}`}
          </span>
          {hasMore && (
            <Button
              variant="outline"
              size="sm"
              onClick={handleLoadMore}
              disabled={fetching}
            >
              {fetching ? (
                <RefreshCw className="size-3 animate-spin" />
              ) : null}
              Load {LIMIT} more
            </Button>
          )}
        </div>
      )}

      {/* Event detail sheet */}
      <EventDetailSheet
        event={selectedEvent}
        onClose={() => setSelectedEvent(null)}
        activeTag={filterTag}
        onTagClick={applyTagFilter}
      />
    </div>
  );
}
