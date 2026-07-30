import { useState } from "react";
import { cn } from "@/lib/utils";

interface CardEnterProps {
  index: number;
  className?: string;
  children: React.ReactNode;
}

/**
 * Wraps children in a div that plays the card-enter animation once on mount.
 * The delay is locked at mount time so index changes (e.g. after dismissals)
 * never restart the animation. Reduced motion is handled by CSS media query.
 */
export function CardEnter({ index, className, children }: CardEnterProps) {
  // Lazy initializer captures index at mount; subsequent index changes are ignored.
  const [delayMs] = useState(() => index * 30);
  const [done, setDone] = useState(false);

  return (
    <div
      className={cn(!done && "animate-card-enter", className)}
      style={!done ? { animationDelay: `${delayMs}ms` } : undefined}
      onAnimationEnd={() => setDone(true)}
    >
      {children}
    </div>
  );
}
