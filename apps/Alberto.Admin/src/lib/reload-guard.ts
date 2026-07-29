// Reload the page if a stale-chunk error occurred, with a time-boxed
// cooldown so a permanently-broken chunk can't pin the app in a reload loop.
const RELOAD_COOLDOWN_MS = 10_000;
const LAST_RELOAD_KEY = "alberto-admin:last-stale-reload";

export function reloadForStaleAssets(): void {
  const last = Number(sessionStorage.getItem(LAST_RELOAD_KEY) ?? "0");
  if (Date.now() - last < RELOAD_COOLDOWN_MS) return;
  sessionStorage.setItem(LAST_RELOAD_KEY, String(Date.now()));
  window.location.reload();
}
