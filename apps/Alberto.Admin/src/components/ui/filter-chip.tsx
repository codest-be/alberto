import type { ComponentType, ReactNode } from "react";
import { X } from "lucide-react";
import { cn } from "@/lib/utils";

export interface FilterChipProps {
  active: boolean;
  onClick: () => void;
  Icon?: ComponentType<{ className?: string }>;
  children: ReactNode;
  className?: string;
}

/**
 * Toggleable filter chip used in flat filter rows.
 * For multi-segment exclusive choices that should look joined, use `<SegmentedControl>` instead.
 */
export function FilterChip({ active, onClick, Icon, children, className }: FilterChipProps) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border-2 border-border px-3 py-1.5 text-[13px] font-base whitespace-nowrap cursor-pointer transition-colors",
        "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        active ? "bg-main text-main-foreground" : "bg-background text-foreground hover:bg-muted",
        className,
      )}
    >
      {Icon && <Icon className="size-3.5" aria-hidden="true" />}
      {children}
    </button>
  );
}

export interface RemovableFilterChipProps {
  onRemove: () => void;
  /** Optional small color/icon swatch shown before the label. */
  swatch?: ReactNode;
  children: ReactNode;
  className?: string;
}

/**
 * Removable badge representing one active filter.
 * Click to remove the filter.
 */
export function RemovableFilterChip({
  onRemove,
  swatch,
  children,
  className,
}: RemovableFilterChipProps) {
  return (
    <button
      type="button"
      onClick={onRemove}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border-[1.5px] border-border bg-secondary-background px-2 py-[3px] pl-[7px] font-mono text-[10px] font-semibold uppercase tracking-[0.06em] cursor-pointer transition-colors hover:bg-muted",
        "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        className,
      )}
    >
      {swatch}
      <span className="normal-case tracking-normal">{children}</span>
      <X aria-hidden="true" className="size-3 text-muted-foreground" />
      <span className="sr-only">Remove filter</span>
    </button>
  );
}
