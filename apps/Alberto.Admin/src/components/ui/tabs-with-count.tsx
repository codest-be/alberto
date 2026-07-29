import { cn } from "@/lib/utils";

export interface TabWithCountOption<T extends string> {
  value: T;
  label: string;
  count: number;
}

export interface TabsWithCountProps<T extends string> {
  value: T;
  onChange: (next: T) => void;
  options: TabWithCountOption<T>[];
  ariaLabel: string;
  className?: string;
}

export function TabsWithCount<T extends string>({
  value,
  onChange,
  options,
  ariaLabel,
  className,
}: TabsWithCountProps<T>) {
  return (
    <div
      role="tablist"
      aria-label={ariaLabel}
      className={cn(
        "flex items-center self-end overflow-hidden rounded-base border-2 border-border shadow-shadow",
        className
      )}
    >
      {options.map(({ value: optValue, label, count }) => {
        const active = value === optValue;
        return (
          <button
            key={optValue}
            type="button"
            role="tab"
            aria-selected={active}
            onClick={() => onChange(optValue)}
            className={cn(
              "flex items-center gap-1.5 px-4 py-2 font-semibold text-sm leading-none whitespace-nowrap border-r-2 border-border last:border-r-0 cursor-pointer transition-colors",
              active
                ? "bg-main text-main-foreground"
                : "bg-secondary-background text-muted-foreground hover:text-foreground"
            )}
          >
            <span>{label}</span>
            <span aria-hidden="true" className="opacity-60">·</span>
            <span className="tabular-nums">{count}</span>
          </button>
        );
      })}
    </div>
  );
}
