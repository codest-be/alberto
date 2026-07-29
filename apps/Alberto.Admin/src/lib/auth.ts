/**
 * Auth boot — calls GET /bff/user on mount.
 *
 * The BFF ships anonymous: no identity provider is registered until someone wires
 * one into AdminBffAuthentication. That is why there are three outcomes here and
 * not two. "Anonymous" and "signed out" look identical from the SPA's side — no
 * user — but they call for opposite responses, so the BFF distinguishes them and
 * this module keeps them apart:
 *
 *   200 { authenticationRequired: false }  → anonymous, render the app
 *   200 { authenticationRequired: true, … } → signed in, render the app
 *   401                                     → signed out, render the sign-in prompt
 *
 * Collapsing the first into the third would put a Sign in button in front of an
 * operator with nowhere to sign in to — /bff/login is not even mapped in that mode.
 */
import { useCallback, useEffect, useState } from "react";

export interface BffUser {
  name: string;
  email?: string;
  claims?: Record<string, string>;
}

export type AuthState =
  | { status: "loading" }
  | { status: "authenticated"; user: BffUser }
  /** No identity provider configured — the admin surface is open. */
  | { status: "anonymous" }
  | { status: "unauthenticated" };

/** Shape returned by GET /bff/user — see Alberto.Admin.Bff/Program.cs. */
interface BffUserResponse {
  authenticationRequired: boolean;
  name: string | null;
  claims: Array<{ type: string; value: string }>;
}

type AuthProbe =
  | { kind: "authenticated"; user: BffUser }
  | { kind: "anonymous" }
  | { kind: "unauthenticated" };

// Whether the BFF has an identity provider in front of it, as last observed by a
// /bff/user probe. Read by the GraphQL client, which has to decide what a 401 from
// the API means and cannot subscribe to React state to find out.
//
// Starts false — the anonymous default — so nothing redirects to a /bff/login that
// may not be mapped before the first probe lands. The app renders a skeleton until
// then, so there is no window in which that matters.
let authenticationRequired = false;

/** True when an identity provider is configured, so signing in is a real option. */
export function isAuthenticationRequired(): boolean {
  return authenticationRequired;
}

async function fetchBffUser(): Promise<AuthProbe> {
  try {
    const res = await fetch("/bff/user", { credentials: "include" });
    if (res.status === 401) {
      // Only a configured provider answers 401 here, so this is also the moment
      // the GraphQL client learns that signing in is possible.
      authenticationRequired = true;
      return { kind: "unauthenticated" };
    }
    if (!res.ok) throw new Error(`/bff/user returned ${res.status}`);

    // The BFF returns an envelope { authenticationRequired, name, claims: [...] },
    // not a bare claims array.
    const body = (await res.json()) as BffUserResponse;
    authenticationRequired = body.authenticationRequired;

    if (!body.authenticationRequired) return { kind: "anonymous" };

    // Index each claim under its full type and its last URI segment. Cookie claims
    // arrive as WS-Federation URIs (".../identity/claims/name"), whereas an OIDC
    // handler would emit short names ("preferred_username"); indexing both means
    // swapping in a real identity provider needs no change here.
    const claimsMap: Record<string, string> = {};
    for (const c of body.claims ?? []) {
      claimsMap[c.type] = c.value;
      const shortName = c.type.split("/").pop();
      if (shortName && !(shortName in claimsMap)) claimsMap[shortName] = c.value;
    }

    const name =
      body.name ??
      claimsMap["name"] ??
      claimsMap["preferred_username"] ??
      claimsMap["nameidentifier"] ??
      claimsMap["sub"] ??
      "Operator";

    return {
      kind: "authenticated",
      user: { name, email: claimsMap["email"], claims: claimsMap },
    };
  } catch {
    // A probe that could not complete is treated as signed out rather than open:
    // the sign-in prompt is a dead end at worst, whereas assuming anonymous would
    // render the whole operator surface on the strength of a failed request.
    return { kind: "unauthenticated" };
  }
}

export function useAuth(): { state: AuthState; refresh: () => void } {
  const [state, setState] = useState<AuthState>({ status: "loading" });

  const refresh = useCallback(() => {
    setState({ status: "loading" });
    void fetchBffUser().then((probe) => {
      setState(
        probe.kind === "authenticated"
          ? { status: "authenticated", user: probe.user }
          : probe.kind === "anonymous"
            ? { status: "anonymous" }
            : { status: "unauthenticated" },
      );
    });
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh]);

  return { state, refresh };
}
