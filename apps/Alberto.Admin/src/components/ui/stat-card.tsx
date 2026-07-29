/**
 * StatCard — big-number metric card for operator dashboards.
 *
 * Accepts a label, value, optional delta/subtext, and a semantic status tone
 * (success | warning | danger | info | neutral).
 *
 * Use on any page that needs to surface a key metric at a glance.
 */
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

// The border stays full ink in every tone. Tinting it per status was how these
// cards lost their edge: a 40%-opacity border on paper is barely a line, so the
// "healthy" card read as unfinished rather than calm. Status is carried by the
// value colour alone — CurioStack puts no decoration on a card that a colour
// on the number cannot say, and four cards in a row with four different edge
// treatments is noise, not signal.
const statCardVariants = cva(
  "rounded-base border-2 border-border bg-background p-5 flex flex-col gap-1 shadow-shadow"
);

const valueVariants = cva(
  "font-mono text-3xl font-bold leading-tight tracking-tight tabular",
  {
    variants: {
      tone: {
        neutral: "text-foreground",
        success: "text-success",
        warning: "text-warning",
        danger: "text-danger",
        info: "text-info",
      },
    },
    defaultVariants: {
      tone: "neutral",
    },
  }
);

export interface StatCardProps extends VariantProps<typeof valueVariants> {
  label: string;
  value: React.ReactNode;
  /** Optional delta or secondary text shown below the value. */
  subtext?: React.ReactNode;
  className?: string;
}

export function StatCard({ label, value, subtext, tone, className }: StatCardProps) {
  return (
    <div className={cn(statCardVariants(), className)}>
      {/* Same eyebrow treatment as PageHeader — mono, tight caps. Keeping the
          two in step is what makes a card read as part of the page rather than
          a widget dropped onto it. */}
      <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground">
        {label}
      </p>
      <p className={valueVariants({ tone })}>{value}</p>
      {subtext && (
        <p className="text-xs text-muted-foreground mt-0.5">{subtext}</p>
      )}
    </div>
  );
}
