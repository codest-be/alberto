import * as React from "react";
import { Slot } from "@radix-ui/react-slot";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

const buttonVariants = cva(
  "inline-flex items-center justify-center whitespace-nowrap rounded-base text-sm font-base ring-offset-background transition gap-2 [&_svg]:pointer-events-none [&_svg]:size-4 [&_svg]:shrink-0 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50",
  {
    variants: {
      variant: {
        default:
          "neo-pressable text-main-foreground bg-main border-2 border-border shadow-shadow",
        destructive:
          "neo-pressable bg-secondary-background text-destructive border-2 border-border shadow-shadow",
        outline:
          "neo-pressable bg-secondary-background text-foreground border-2 border-border shadow-shadow hover:bg-main hover:text-main-foreground",
        muted:
          "bg-secondary-background text-foreground/80 border-2 border-border/70 hover:border-border hover:text-foreground",
        noShadow:
          "text-main-foreground bg-main border-2 border-border hover:bg-main/80",
        ghost:
          "bg-transparent text-foreground border-2 border-transparent hover:border-border hover:bg-secondary-background",
        link: "text-foreground underline-offset-4 hover:underline focus-visible:underline",
      },
      // CurioStack's h-11 default, kept as-is. The one deviation is `sm`, which
      // is h-11 there too — a mobile touch-target decision that does not carry
      // over to a console whose tables put a button on every row.
      size: {
        default: "h-11 px-4 py-2",
        sm: "h-9 px-3",
        lg: "h-12 px-8",
        icon: "relative size-11",
        iconSm: "relative size-9",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  asChild?: boolean;
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, asChild = false, ...props }, ref) => {
    const Comp = asChild ? Slot : "button";
    return (
      <Comp
        className={cn(buttonVariants({ variant, size, className }))}
        ref={ref}
        {...props}
      />
    );
  }
);
Button.displayName = "Button";

// eslint-disable-next-line react-refresh/only-export-components
export { Button, buttonVariants };
