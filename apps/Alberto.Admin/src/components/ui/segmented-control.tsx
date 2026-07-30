import type { ComponentType } from "react";
import { cn } from "@/lib/utils";

export interface SegmentedControlOption<T extends string> {
  value: T;
  /** Used for aria-label / title; also rendered as text when `showLabels` is true. */
  label: string;
  Icon?: ComponentType<{ className?: string }>;
}

export interface SegmentedControlProps<T extends string> {
  value: T;
  onChange: (next: T) => void;
  options: SegmentedControlOption<T>[];
  ariaLabel: string;
  /** Render text labels alongside icons. Default false (icon-only buttons). */
  showLabels?: boolean;
  /** When true, segments grow to fill the parent's width (flex-1). */
  fill?: boolean;
  className?: string;
}

export function SegmentedControl<T extends string>({
  value,
  onChange,
  options,
  ariaLabel,
  showLabels = false,
  fill = false,
  className,
}: SegmentedControlProps<T>) {
  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      className={cn(
        "inline-flex h-10 self-start overflow-hidden rounded-base border-2 border-border bg-background",
        className,
      )}
    >
      {options.map(({ value: optValue, label, Icon }, idx) => {
        const active = value === optValue;
        return (
          <button
            key={optValue}
            type="button"
            role="radio"
            aria-checked={active}
            aria-label={showLabels ? undefined : label}
            title={label}
            onClick={() => onChange(optValue)}
            className={cn(
              "flex items-center justify-center gap-1.5 px-3 transition-colors cursor-pointer",
              "focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
              fill && "flex-1",
              idx > 0 && "border-l-2 border-border",
              active
                ? "bg-main text-main-foreground"
                : "bg-transparent text-muted-foreground hover:text-foreground",
            )}
          >
            {Icon && <Icon className="size-4" />}
            {showLabels && (
              <span className="font-heading text-sm leading-none">{label}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}
