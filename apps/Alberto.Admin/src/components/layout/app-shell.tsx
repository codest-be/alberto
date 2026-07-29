/**
 * AppShell — the root layout for the Alberto admin console.
 *
 * Structure:
 *   SidebarProvider
 *     Sidebar  (left, collapsible to icon rail)
 *       SidebarHeader  — brand + trigger
 *       SidebarContent — nav items
 *       SidebarFooter  — user / connection status
 *     SidebarInset     — page content rendered via <Outlet />
 *       top bar (mobile trigger + breadcrumb/title)
 *       <Outlet />
 */
import { useEffect, useState } from "react";
import { Outlet, useLocation, useNavigate } from "react-router";
import {
  AlertCircle,
  Archive,
  BookOpen,
  Cpu,
  Database,
  LayoutDashboard,
  RotateCcw,
  Users,
  Wifi,
  WifiOff,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";

import { cn } from "@/lib/utils";
import type { BffUser } from "@/lib/auth";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarNavItem,
  SidebarProvider,
  SidebarSeparator,
  SidebarTrigger,
} from "@/components/ui/sidebar";
import { ThemeToggle } from "@/components/layout/theme-toggle";
import { ScopeBar } from "@/components/layout/scope-bar";

interface NavItem {
  title: string;
  path: string;
  icon: LucideIcon;
}

const NAV_ITEMS: NavItem[] = [
  { title: "Dashboard",   path: "/",            icon: LayoutDashboard },
  { title: "Checkpoints", path: "/checkpoints", icon: Cpu },
  { title: "Dead Letters", path: "/dead-letters", icon: AlertCircle },
  { title: "Rebuilds",    path: "/rebuilds",    icon: RotateCcw },
  { title: "Tenant Leases", path: "/tenants",   icon: Users },
  { title: "Events",      path: "/events",      icon: Database },
  { title: "Projections", path: "/projections", icon: Archive },
  { title: "Audit Trail", path: "/audit",       icon: BookOpen },
];

interface ConnectionStatusProps {
  /** null = unknown / not yet measured. */
  connected: boolean | null;
}

function ConnectionStatus({ connected }: ConnectionStatusProps) {
  if (connected === null) return null;
  return (
    <div
      className={cn(
        "flex items-center gap-2 rounded-base border px-2 py-1 text-xs",
        connected
          ? "border-border bg-success/15 text-success"
          : "border-border bg-danger/15 text-danger"
      )}
      title={connected ? "GraphQL WebSocket connected" : "GraphQL WebSocket disconnected"}
    >
      {connected ? <Wifi className="size-3" /> : <WifiOff className="size-3" />}
      <span className="hidden sm:inline">{connected ? "Connected" : "Disconnected"}</span>
    </div>
  );
}

interface AppShellProps {
  user: BffUser | null;
}

export function AppShell({ user }: AppShellProps) {
  const location = useLocation();
  const navigate = useNavigate();
  const [wsConnected, setWsConnected] = useState<boolean | null>(null);

  // Track WebSocket connection health for the status indicator.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const check = () => {
      // graphql-ws uses document events via custom events if integrated, but
      // the simplest approach for now is to poll network status as a proxy.
      // A follow-up can wire wsClient.on("connected"/"closed") directly.
      setWsConnected(navigator.onLine);
    };

    check();
    window.addEventListener("online", check);
    window.addEventListener("offline", check);
    // Poll every 30s as a backstop.
    timer = setInterval(check, 30_000);

    return () => {
      clearInterval(timer);
      window.removeEventListener("online", check);
      window.removeEventListener("offline", check);
    };
  }, []);

  function isActive(path: string): boolean {
    if (path === "/") return location.pathname === "/";
    return location.pathname.startsWith(path);
  }

  return (
    <SidebarProvider>
      {/* Left sidebar */}
      <Sidebar collapsible="icon">
        <SidebarHeader className="p-3">
          {/* shrink-0 on the trigger is load-bearing: the collapsed rail is
              3.5rem, and without it flex squeezed the 36px button down to 20px
              — a squashed pill rather than a control. The brand mark is dropped
              entirely when collapsed for the same reason; there is no room for
              two things, and the toggle is the one you need. */}
          <div className="flex items-center gap-2">
            <SidebarTrigger className="shrink-0" />
            <span className="font-brand text-base truncate group-data-[collapsible=icon]:hidden">
              Alberto Admin
            </span>
          </div>
        </SidebarHeader>

        <SidebarContent className="px-0 py-2">
          <SidebarMenu>
            {NAV_ITEMS.map((item) => (
              <SidebarNavItem
                key={item.path}
                icon={item.icon}
                title={item.title}
                active={isActive(item.path)}
                onClick={() => navigate(item.path)}
              />
            ))}
          </SidebarMenu>
        </SidebarContent>

        <SidebarSeparator />

        <SidebarFooter className="p-3 gap-2">
          <div className="flex items-center justify-between gap-2 group-data-[collapsible=icon]:hidden">
            <ConnectionStatus connected={wsConnected} />
            {/* Light-first is the default, but an operator staring at a lag
                table at 3am should not have to be. */}
            <ThemeToggle className="size-9 shrink-0" />
          </div>
          {user && (
            <div className="truncate group-data-[collapsible=icon]:hidden">
              <p className="text-xs font-heading truncate text-sidebar-foreground">{user.name}</p>
              {user.email && (
                <p className="text-[11px] truncate text-muted-foreground">{user.email}</p>
              )}
            </div>
          )}
        </SidebarFooter>
      </Sidebar>

      {/* Main content area */}
      <SidebarInset>
        {/* Mobile top-bar */}
        <header className="flex items-center gap-3 border-b-2 border-border px-4 py-3 md:hidden">
          <SidebarTrigger />
          <span className="font-brand text-base">Alberto Admin</span>
          <div className="ml-auto">
            <ConnectionStatus connected={wsConnected} />
          </div>
        </header>

        {/* Scope — sits above the scroll container so the store and tenant a page's
            numbers belong to stay on screen while the page itself scrolls. */}
        <ScopeBar />

        {/* Page content */}
        <div className="flex-1 overflow-auto">
          <Outlet />
        </div>
      </SidebarInset>
    </SidebarProvider>
  );
}
