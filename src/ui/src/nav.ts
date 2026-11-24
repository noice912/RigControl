// Small helpers so a component anywhere in the app can open a section or the first-time setup
// without every page having to pass callbacks down.
const NAVIGATE = "rigcontrol:navigate";
const FIRST_RUN = "rigcontrol:first-run";

/** Opens a section of the app by its route name (for example "health"). */
export function goTo(route: string) {
  window.dispatchEvent(new CustomEvent(NAVIGATE, { detail: route }));
}

export function onGoTo(handler: (route: string) => void): () => void {
  const fn = (e: Event) => handler(String((e as CustomEvent).detail));
  window.addEventListener(NAVIGATE, fn);
  return () => window.removeEventListener(NAVIGATE, fn);
}

/** Shows the first-time setup again, even when it has been finished before. */
export function openFirstRun() {
  window.dispatchEvent(new Event(FIRST_RUN));
}

export function onOpenFirstRun(handler: () => void): () => void {
  window.addEventListener(FIRST_RUN, handler);
  return () => window.removeEventListener(FIRST_RUN, handler);
}
