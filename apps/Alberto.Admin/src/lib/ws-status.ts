/**
 * Live WebSocket connection state for the GraphQL subscription transport.
 *
 * Pages must not infer liveness from urql's `result.fetching`: for a subscription
 * that flag drops to false as soon as a payload is delivered, so a perfectly
 * healthy stream reports itself as disconnected the moment it does its job.
 * The authority on whether the socket is up is the graphql-ws client itself.
 */
import { useSyncExternalStore } from "react";
import { wsClient } from "./graphql-client";

export type WsStatus = "connecting" | "connected" | "closed";

// The client is created lazily, so nothing is connected until the first
// subscription mounts. "connecting" is the honest starting point.
let current: WsStatus = "connecting";
const listeners = new Set<() => void>();

function set(next: WsStatus) {
  if (current === next) return;
  current = next;
  for (const listener of listeners) listener();
}

wsClient.on("connecting", () => set("connecting"));
wsClient.on("connected", () => set("connected"));
wsClient.on("closed", () => set("closed"));

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => {
    listeners.delete(onStoreChange);
  };
}

/** Subscribes to the real transport state; re-renders on every transition. */
export function useWsStatus(): WsStatus {
  return useSyncExternalStore(subscribe, () => current);
}
