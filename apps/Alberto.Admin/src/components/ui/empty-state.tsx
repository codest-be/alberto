import type { ReactNode } from "react";
import { AlertTriangle, RotateCw } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";

export interface EmptyStateProps {
  /** Decorative glyph rendered in a 72×72 outlined tile. Switches to the larger layout when present. */
  glyph?: ReactNode;
  title: ReactNode;
  description?: ReactNode;
  /** Primary action node (typically a `<Button>`). */
  action?: ReactNode;
  /** Footer line in muted mono caps. */
  footer?: ReactNode;
  /** ARIA live role. Defaults to `status` (polite); use `alert` for error panels. */
  role?: "status" | "alert";
  className?: string;
}

/**
 * Standard empty / no-results panel: dashed bordered card centered around a title,
 * optional glyph, description, action, and footer.
 */
export function EmptyState({
  glyph,
  title,
  description,
  action,
  footer,
  role = "status",
  className,
}: EmptyStateProps) {
  const large = !!glyph;
  return (
    <div
      role={role}
      className={cn(
        "flex flex-col items-center gap-2.5 rounded-base border-2 border-dashed border-border/40 bg-muted text-center",
        large ? "px-6 py-16" : "px-8 py-14",
        className,
      )}
    >
      {glyph && (
        <div className="mb-1.5 flex size-[72px] items-center justify-center rounded-base border-2 border-border bg-secondary-background font-brand text-[34px] font-extrabold shadow-shadow">
          {glyph}
        </div>
      )}
      <h3 className="m-0 font-brand text-2xl tracking-tight">{title}</h3>
      {description && (
        <p className="m-0 max-w-[42ch] text-sm text-muted-foreground">{description}</p>
      )}
      {action && <div className="mt-2">{action}</div>}
      {footer && (
        <p className="mt-1.5 font-mono text-[11px] tracking-[0.06em] text-muted-foreground/70">
          {footer}
        </p>
      )}
    </div>
  );
}

export interface ErrorStateProps {
  /** What failed to load, e.g. "your queue". Folded into the default copy. */
  title?: ReactNode;
  description?: ReactNode;
  /** Wires the "Try again" button; omit to hide it. */
  onRetry?: () => void;
  className?: string;
}

/**
 * Error counterpart to {@link EmptyState}: an `alert`-role panel for a failed
 * fetch, with a "Try again" action. Use when a query errors with no data on
 * screen — never as a stand-in for the empty state.
 */
export function ErrorState({
  title = "Couldn't load this",
  description = "Something went wrong reaching the server. Check your connection and try again.",
  onRetry,
  className,
}: ErrorStateProps) {
  return (
    <EmptyState
      role="alert"
      glyph={<AlertTriangle className="size-7" aria-hidden="true" />}
      title={title}
      description={description}
      action={
        onRetry && (
          <Button size="sm" variant="outline" className="gap-1.5" onClick={onRetry}>
            <RotateCw className="size-4" aria-hidden="true" />
            Try again
          </Button>
        )
      }
      className={className}
    />
  );
}
