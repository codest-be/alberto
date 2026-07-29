import { Component, Suspense, type ReactNode } from "react";
import { reloadForStaleAssets } from "@/lib/reload-guard";

function isChunkLoadError(error: unknown): boolean {
  if (!error) return false;
  const message = error instanceof Error ? error.message : String(error);
  return (
    /Loading chunk [\d]+ failed/i.test(message) ||
    /Failed to fetch dynamically imported module/i.test(message) ||
    /Importing a module script failed/i.test(message) ||
    /ChunkLoadError/i.test(message)
  );
}

interface State {
  error: unknown | null;
}

class ChunkErrorBoundary extends Component<
  { children: ReactNode; fallback: ReactNode },
  State
> {
  state: State = { error: null };

  static getDerivedStateFromError(error: unknown): State {
    return { error };
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    if (isChunkLoadError(error)) {
      reloadForStaleAssets();
      return this.props.fallback;
    }
    throw error;
  }
}

interface LazyRouteBoundaryProps {
  fallback: ReactNode;
  children: ReactNode;
}

export function LazyRouteBoundary({ fallback, children }: LazyRouteBoundaryProps) {
  const delayed = <div className="skeleton-fade-in">{fallback}</div>;
  return (
    <ChunkErrorBoundary fallback={delayed}>
      <Suspense fallback={delayed}>{children}</Suspense>
    </ChunkErrorBoundary>
  );
}
