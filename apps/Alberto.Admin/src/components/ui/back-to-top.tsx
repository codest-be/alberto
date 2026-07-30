import { useEffect, useRef, useState } from "react";
import { ArrowUp } from "lucide-react";
import { cn } from "@/lib/utils";

function findScrollParent(): Element | null {
  return (
    document.querySelector("main > div[class*='overflow-y-auto']") ??
    document.querySelector("main")
  );
}

interface BackToTopProps {
  /** Pixels of scroll before the button appears. Defaults to 600. */
  threshold?: number;
  className?: string;
}

export function BackToTop({ threshold = 600, className }: BackToTopProps) {
  const scrollParentRef = useRef<Element | null>(null);
  const [visible, setVisible] = useState(false);
  const rafRef = useRef(0);

  useEffect(() => {
    const scrollParent = findScrollParent();
    scrollParentRef.current = scrollParent;
    if (!scrollParent) return;

    const update = () => {
      setVisible(scrollParent.scrollTop > threshold);
    };

    const handleScroll = () => {
      if (rafRef.current) return;
      rafRef.current = requestAnimationFrame(() => {
        rafRef.current = 0;
        update();
      });
    };

    update();
    scrollParent.addEventListener("scroll", handleScroll, { passive: true });
    return () => {
      scrollParent.removeEventListener("scroll", handleScroll);
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
    };
  }, [threshold]);

  const scrollToTop = () => {
    scrollParentRef.current?.scrollTo({ top: 0, behavior: "smooth" });
  };

  return (
    <button
      type="button"
      onClick={scrollToTop}
      aria-label="Back to top"
      aria-hidden={!visible}
      tabIndex={visible ? 0 : -1}
      className={cn(
        "neo-pressable fixed bottom-6 right-6 z-40 size-11 rounded-full border-2 border-border bg-background shadow-shadow transition-opacity duration-200 ease-out hover:bg-main hover:text-main-foreground focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        visible
          ? "pointer-events-auto opacity-100"
          : "pointer-events-none opacity-0",
        className,
      )}
    >
      <ArrowUp className="mx-auto size-4" aria-hidden="true" />
    </button>
  );
}
