import { lazy } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router";
import { LazyRouteBoundary } from "@/components/lazy-route-boundary";
import { Toaster } from "@/components/ui/sonner";
import { AppShell } from "@/components/layout/app-shell";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/lib/auth";

// ---------------------------------------------------------------------------
// Lazy-loaded feature pages
// ---------------------------------------------------------------------------

const DashboardPage = lazy(() =>
  import("@/features/dashboard/page").then((m) => ({ default: m.DashboardPage }))
);
const CheckpointsPage = lazy(() =>
  import("@/features/checkpoints/page").then((m) => ({ default: m.CheckpointsPage }))
);
const DeadLettersPage = lazy(() =>
  import("@/features/dead-letters/page").then((m) => ({ default: m.DeadLettersPage }))
);
const RebuildsPage = lazy(() =>
  import("@/features/rebuilds/page").then((m) => ({ default: m.RebuildsPage }))
);
const TenantsPage = lazy(() =>
  import("@/features/tenants/page").then((m) => ({ default: m.TenantsPage }))
);
const EventsPage = lazy(() =>
  import("@/features/events/page").then((m) => ({ default: m.EventsPage }))
);
const ProjectionsPage = lazy(() =>
  import("@/features/projections/page").then((m) => ({ default: m.ProjectionsPage }))
);
const AuditPage = lazy(() =>
  import("@/features/audit/page").then((m) => ({ default: m.AuditPage }))
);

// ---------------------------------------------------------------------------
// Skeleton fallback shown while a lazy page chunk loads
// ---------------------------------------------------------------------------

function PageSkeleton() {
  return (
    <div className="p-6 space-y-4">
      <Skeleton className="h-10 w-64" />
      <Skeleton className="h-4 w-96" />
      <Skeleton className="h-64 w-full" />
    </div>
  );
}

function LazyPage({ children }: { children: React.ReactNode }) {
  return (
    <LazyRouteBoundary fallback={<PageSkeleton />}>{children}</LazyRouteBoundary>
  );
}

// ---------------------------------------------------------------------------
// Sign-in prompt shown when the BFF reports no session
// ---------------------------------------------------------------------------

function SignInPrompt() {
  const returnUrl = `${window.location.pathname}${window.location.search}`;

  return (
    <div className="flex min-h-svh items-center justify-center p-6">
      <div className="w-full max-w-sm space-y-4 text-center">
        <h1 className="font-brand text-3xl">Alberto Admin</h1>
        <p className="text-muted-foreground text-sm">
          You are not signed in. Sign in to inspect and operate the event store.
        </p>
        <Button asChild className="w-full">
          <a href={`/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`}>
            Sign in
          </a>
        </Button>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Root app component
// ---------------------------------------------------------------------------

export function App() {
  const { state } = useAuth();

  // Show a minimal loading state while the BFF user check resolves.
  if (state.status === "loading") {
    return <PageSkeleton />;
  }

  // Unauthenticated — show a sign-in prompt rather than redirecting automatically.
  //
  // An automatic redirect here is a trap: /bff/login redirects straight back, so
  // anything that leaves the session unresolved (a failed sign-in, or a bug reading
  // the /bff/user response) becomes an infinite navigation loop that hides its own
  // cause. Requiring a click keeps the failure visible and bounded.
  if (state.status === "unauthenticated") {
    return <SignInPrompt />;
  }

  // Anonymous — no identity provider is configured, so there is no one to name.
  // AppShell simply omits the identity block rather than inventing a placeholder
  // operator, which keeps the sidebar honest about who is acting.
  const user = state.status === "authenticated" ? state.user : null;

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell user={user} />}>
          <Route
            index
            element={
              <LazyPage>
                <DashboardPage />
              </LazyPage>
            }
          />
          <Route
            path="/checkpoints"
            element={
              <LazyPage>
                <CheckpointsPage />
              </LazyPage>
            }
          />
          <Route
            path="/dead-letters"
            element={
              <LazyPage>
                <DeadLettersPage />
              </LazyPage>
            }
          />
          <Route
            path="/rebuilds"
            element={
              <LazyPage>
                <RebuildsPage />
              </LazyPage>
            }
          />
          <Route
            path="/tenants"
            element={
              <LazyPage>
                <TenantsPage />
              </LazyPage>
            }
          />
          <Route
            path="/events"
            element={
              <LazyPage>
                <EventsPage />
              </LazyPage>
            }
          />
          <Route
            path="/projections"
            element={
              <LazyPage>
                <ProjectionsPage />
              </LazyPage>
            }
          />
          <Route
            path="/audit"
            element={
              <LazyPage>
                <AuditPage />
              </LazyPage>
            }
          />
          {/* Fallback — redirect unknown paths to dashboard */}
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
      <Toaster />
    </BrowserRouter>
  );
}
