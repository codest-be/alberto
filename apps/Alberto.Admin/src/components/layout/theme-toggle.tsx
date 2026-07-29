import { Moon, Sun } from "lucide-react";
import { useTheme } from "@/components/ui/theme-provider";
import { cn } from "@/lib/utils";

interface ThemeToggleProps {
  className?: string;
}

export function ThemeToggle({ className }: ThemeToggleProps) {
  const { theme, setTheme } = useTheme();

  const isDark =
    theme === "dark" ||
    (theme === "system" &&
      window.matchMedia("(prefers-color-scheme: dark)").matches);

  function handleToggle() {
    setTheme(isDark ? "light" : "dark");
  }

  return (
    <button
      onClick={handleToggle}
      className={cn(
        "neo-pressable inline-flex size-11 items-center justify-center rounded-base border-2 border-border bg-secondary-background shadow-nav focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2",
        className,
      )}
      aria-label={isDark ? "Switch to light mode" : "Switch to dark mode"}
    >
      {isDark ? (
        <Sun className="size-5 stroke-foreground" aria-hidden="true" />
      ) : (
        <Moon className="size-5 stroke-foreground" aria-hidden="true" />
      )}
    </button>
  );
}
