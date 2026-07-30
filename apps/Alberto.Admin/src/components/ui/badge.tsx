import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import * as React from "react";
import { cn } from "@/lib/utils";

const badgeVariants = cva(
  "inline-flex items-center justify-center rounded-[3px] border-2 border-border px-[9px] py-[3px] text-[11px] font-semibold tracking-[0.02em] w-fit whitespace-nowrap shrink-0 [&>svg]:size-3 gap-[5px] [&>svg]:pointer-events-none overflow-hidden",
  {
    variants: {
      variant: {
        default: "bg-main text-main-foreground",
        secondary: "bg-secondary-background text-foreground",
        destructive: "bg-destructive text-destructive-foreground",
        outline: "bg-transparent text-foreground",
        ghost: "bg-background text-foreground shadow-none",

        // CurioStack's decorative accents. Reach for these when the colour is a
        // label rather than a judgement — a tenant tag, a module name.
        mint: "bg-mint text-mint-foreground",
        coral: "bg-coral text-coral-foreground",
        lilac: "bg-lilac text-lilac-foreground",
        sky: "bg-sky text-sky-foreground",

        // Status variants, added for this console. These assert that something
        // is healthy, lagging or broken, so they keep the full ink border
        // rather than the softened one they carried before — a dead-letter
        // badge should read as hard as everything else on the page.
        success: "bg-success text-success-foreground",
        warning: "bg-warning text-warning-foreground",
        danger: "bg-danger text-danger-foreground",
        info: "bg-info text-info-foreground",
      },
    },
    defaultVariants: {
      variant: "default",
    },
  }
);

function Badge({
  className,
  variant,
  asChild = false,
  ...props
}: React.ComponentProps<"span"> &
  VariantProps<typeof badgeVariants> & {
    asChild?: boolean;
  }) {
  const Comp = asChild ? Slot : "span";
  return (
    <Comp className={cn(badgeVariants({ variant }), className)} {...props} />
  );
}

export { Badge, badgeVariants };
