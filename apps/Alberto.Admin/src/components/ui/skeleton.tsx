import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

function Skeleton({ className, ...props }: ComponentProps<"div">) {
  return (
    <div
      className={cn(
        "skeleton-fade-in animate-pulse rounded-base bg-secondary-background border-2 border-border",
        className
      )}
      {...props}
    />
  );
}

export { Skeleton };
