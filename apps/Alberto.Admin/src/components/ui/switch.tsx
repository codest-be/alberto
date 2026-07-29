import * as React from "react";
import { cn } from "@/lib/utils";

interface SwitchProps
  extends Omit<React.ButtonHTMLAttributes<HTMLButtonElement>, "onChange"> {
  checked: boolean;
  onCheckedChange?: (checked: boolean) => void;
}

const Switch = React.forwardRef<HTMLButtonElement, SwitchProps>(
  ({ className, checked, onCheckedChange, disabled, onClick, ...props }, ref) => {
    return (
      <button
        ref={ref}
        type="button"
        role="switch"
        aria-checked={checked}
        data-slot="switch"
        data-state={checked ? "checked" : "unchecked"}
        disabled={disabled}
        className={cn(
          "group relative inline-flex h-6 w-11 shrink-0 cursor-pointer items-center rounded-base border-2 border-border shadow-shadow transition-[background-color,box-shadow,transform] duration-150 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50",
          checked ? "bg-main" : "bg-secondary-background",
          className,
        )}
        onClick={(event) => {
          onClick?.(event);
          if (!event.defaultPrevented) {
            onCheckedChange?.(!checked);
          }
        }}
        {...props}
      >
        <span
          data-slot="switch-thumb"
          className={cn(
            "pointer-events-none block size-4 rounded-base border-2 border-border bg-background transition-transform duration-150",
            checked ? "translate-x-[22px]" : "translate-x-0.5",
          )}
        />
      </button>
    );
  },
);
Switch.displayName = "Switch";

export { Switch };
