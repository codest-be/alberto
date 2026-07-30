import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { Provider } from "urql";
import { graphqlClient } from "@/lib/graphql-client";
import { ThemeProvider } from "@/components/ui/theme-provider";
import { AdminScopeProvider } from "@/lib/admin-scope";
import { reloadForStaleAssets } from "@/lib/reload-guard";
import { App } from "./App";

// Self-hosted via @fontsource — no CDN request, so the console keeps its
// typography on an air-gapped or otherwise locked-down operator network.
// Weights match the ones CurioStack.Web actually uses: body 500, headings 700,
// brand 800, and JetBrains Mono 400 for the positions, IDs and payloads that
// make up most of this UI's data.
import "@fontsource/space-grotesk/500.css";
import "@fontsource/space-grotesk/700.css";
import "@fontsource/bricolage-grotesque/800.css";
import "@fontsource/jetbrains-mono/400.css";

import "./index.css";

// Reload on stale Vite-hashed chunks after a deployment.
window.addEventListener("vite:preloadError", () => {
  reloadForStaleAssets();
});

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Provider value={graphqlClient}>
      <ThemeProvider defaultTheme="light">
        {/* Inside the urql Provider — the scope resolves the module list over GraphQL. */}
        <AdminScopeProvider>
          <App />
        </AdminScopeProvider>
      </ThemeProvider>
    </Provider>
  </StrictMode>
);
