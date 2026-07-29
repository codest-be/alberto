import { cn } from "@/lib/utils";

interface PageHeaderProps {
  title: string;
  eyebrow?: string;
  subtitle?: string;
  description?: string;
  children?: React.ReactNode;
  className?: string;
}

export function PageHeader({
  title,
  eyebrow,
  subtitle,
  description,
  children,
  className,
}: PageHeaderProps) {
  return (
    <div
      className={cn(
        "mb-4 flex flex-col gap-3 border-b-2 border-border pb-4 sm:mb-8 sm:pb-6 sm:flex-row sm:items-start sm:justify-between sm:gap-4",
        className,
      )}
    >
      <div className="min-w-0">
        {eyebrow && (
          <p className="font-mono text-[0.65rem] tracking-[0.14em] uppercase text-muted-foreground mb-1.5">
            {eyebrow}
          </p>
        )}
        <h1 className="font-brand text-2xl sm:text-4xl md:text-5xl tracking-tight leading-none">
          {title}
          {subtitle && (
            <>
              {" "}<em className="hidden sm:inline font-brand font-medium italic text-muted-foreground">&mdash; {subtitle}</em>
            </>
          )}
        </h1>
        {description && (
          <p className="hidden sm:block mt-2 max-w-[60ch] text-sm text-muted-foreground">{description}</p>
        )}
      </div>
      {children && (
        <div className="flex shrink-0 items-center gap-2">{children}</div>
      )}
    </div>
  );
}
