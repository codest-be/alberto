import { Client, fetchExchange, mapExchange, subscriptionExchange } from "urql";
import { cacheExchange } from "@urql/exchange-graphcache";
import { createClient as createWsClient } from "graphql-ws";
import { isAuthenticationRequired } from "@/lib/auth";

// HTTP endpoint — the BFF (apps/Alberto.Admin.Bff) strips /api and proxies to
// the Orders API GraphQL endpoint.
const HTTP_ENDPOINT = "/api/graphql";

// WebSocket endpoint — derived from window.location so it works in any env.
const wsProtocol = window.location.protocol === "https:" ? "wss:" : "ws:";
const WS_ENDPOINT = `${wsProtocol}//${window.location.host}/api/graphql`;

// Read the XSRF-TOKEN cookie set by the BFF and forward it as X-XSRF-TOKEN
// on every request so the BFF can validate it on mutations (double-submit cookie pattern).
function getXsrfToken(): string | undefined {
  const match = document.cookie.match(/(?:^|;\s*)XSRF-TOKEN=([^;]*)/);
  return match ? decodeURIComponent(match[1]) : undefined;
}

// Redirect to /bff/login on a 401 response from the GraphQL endpoint.
//
// Gated on an identity provider actually being configured. The admin BFF ships
// anonymous, and in that mode /bff/login is not mapped at all — redirecting there
// would answer a 401 with a 404 and lose the error that caused it. A 401 with no
// provider configured is an upstream problem, so it is left to surface as a query
// error on the page that asked for it.
let redirectingToLogin = false;

const sessionExpiredExchange = mapExchange({
  onResult(result) {
    if (result.error) {
      const status = (result.error as { response?: { status?: number } }).response?.status;
      if (status === 401 && isAuthenticationRequired() && !redirectingToLogin) {
        redirectingToLogin = true;
        window.location.href = `/bff/login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
      }
    }
  },
});

// Minimal graphcache — no complex optimistic updates for the admin console.
// Feature pages add keys / optimistic / updates as needed.
const graphcache = cacheExchange({});

const wsClient = createWsClient({
  url: WS_ENDPOINT,
  lazy: true,
  retryAttempts: Infinity,
  retryWait: async (retries) => {
    // Exponential backoff capped at 30s.
    await new Promise<void>((resolve) =>
      setTimeout(resolve, Math.min(1000 * 2 ** retries, 30_000)),
    );
  },
});

const wsExchange = subscriptionExchange({
  forwardSubscription(operation) {
    return {
      subscribe(sink) {
        const dispose = wsClient.subscribe(
          { ...operation, query: operation.query ?? "" },
          sink as Parameters<typeof wsClient.subscribe>[1],
        );
        return { unsubscribe: dispose };
      },
    };
  },
});

export const graphqlClient = new Client({
  url: HTTP_ENDPOINT,
  requestPolicy: "cache-and-network",
  preferGetMethod: false,
  exchanges: [graphcache, sessionExpiredExchange, wsExchange, fetchExchange],
  fetchOptions: () => {
    const headers: Record<string, string> = {
      "Content-Type": "application/json",
    };
    const xsrfToken = getXsrfToken();
    if (xsrfToken) {
      headers["X-XSRF-TOKEN"] = xsrfToken;
    }
    return {
      method: "POST",
      credentials: "include" as RequestCredentials,
      headers,
    };
  },
});

/** Expose the raw ws client so the app shell can subscribe to connection state. */
export { wsClient };
