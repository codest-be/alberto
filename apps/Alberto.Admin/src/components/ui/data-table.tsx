/**
 * DataTable — column-definition wrapper over the CurioStack table primitives.
 *
 * Accepts a column definition array and row data. Renders a loading skeleton,
 * an empty state, and an optional per-row action slot.
 * Feature pages own filtering/pagination; this component only handles display.
 *
 * The markup deliberately goes through `./table` rather than raw <table> tags:
 * that is where the ink borders, the full-weight row rules and the amber header
 * band live, and hand-rolling the elements here is exactly how this console
 * drifted away from the rest of the design in the first place.
 */
import { cn } from "@/lib/utils";
import { Skeleton } from "./skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "./table";

export interface ColumnDef<TRow> {
  /** Unique key — used as React key and aria-label fallback. */
  key: string;
  /** Column header label. */
  header: string;
  /** Render the cell. Return a React node. */
  cell: (row: TRow) => React.ReactNode;
  /** Optional additional class names for the cell. */
  className?: string;
}

export interface DataTableProps<TRow> {
  columns: ColumnDef<TRow>[];
  data: TRow[] | undefined;
  /** Unique key extractor for each row. */
  rowKey: (row: TRow) => string;
  /** Optional per-row action rendered in a trailing column. */
  rowAction?: (row: TRow) => React.ReactNode;
  /** Shown while data is undefined / loading. Defaults to 5 skeleton rows. */
  loading?: boolean;
  skeletonRows?: number;
  /** Shown when data is an empty array. */
  emptyMessage?: string;
  className?: string;
}

export function DataTable<TRow>({
  columns,
  data,
  rowKey,
  rowAction,
  loading = false,
  skeletonRows = 5,
  emptyMessage = "No data",
  className,
}: DataTableProps<TRow>) {
  const isLoading = loading || data === undefined;
  const isEmpty = !isLoading && data?.length === 0;
  const columnCount = columns.length + (rowAction ? 1 : 0);

  return (
    <div className={cn("rounded-base shadow-shadow", className)}>
      <Table className="rounded-base overflow-hidden">
        <TableHeader>
          {/* bg-main is what TableHead's text-main-foreground is written
              against — the amber band is the header, not a decoration on it. */}
          <TableRow className="bg-main">
            {columns.map((col) => (
              <TableHead key={col.key}>{col.header}</TableHead>
            ))}
            {rowAction && <TableHead className="text-right">Actions</TableHead>}
          </TableRow>
        </TableHeader>
        <TableBody>
          {isLoading &&
            Array.from({ length: skeletonRows }, (_, i) => (
              <TableRow key={i}>
                {columns.map((col) => (
                  <TableCell key={col.key}>
                    <Skeleton className="h-4 w-full" />
                  </TableCell>
                ))}
                {rowAction && (
                  <TableCell className="text-right">
                    <Skeleton className="ml-auto h-4 w-16" />
                  </TableCell>
                )}
              </TableRow>
            ))}

          {isEmpty && (
            <TableRow>
              <TableCell
                colSpan={columnCount}
                className="py-12 text-center text-muted-foreground"
              >
                {emptyMessage}
              </TableCell>
            </TableRow>
          )}

          {!isLoading &&
            data?.map((row) => (
              <TableRow
                key={rowKey(row)}
                className="hover:bg-secondary-background"
              >
                {columns.map((col) => (
                  <TableCell key={col.key} className={cn("text-foreground", col.className)}>
                    {col.cell(row)}
                  </TableCell>
                ))}
                {rowAction && (
                  <TableCell className="text-right">{rowAction(row)}</TableCell>
                )}
              </TableRow>
            ))}
        </TableBody>
      </Table>
    </div>
  );
}
