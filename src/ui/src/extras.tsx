// Add-ons: optional parts of the interface that only some copies of the source carry.
// Each add-on is a folder under ./private with an index.tsx whose default export is an Extra.
// When the folder is not there, nothing is loaded and the app is simply without those parts.
import type { ComponentType } from "react";

/** A place on a page where an add-on can show a panel. */
export type ExtraSlot = "overview" | "lighting";

export type Extra = {
  /** Panels to show, by the place they belong. */
  panels?: Partial<Record<ExtraSlot, ComponentType>>;
  /** Plain names for the add-on's modules (module id to name), for messages about them. */
  moduleNames?: Record<string, string>;
  /** Lets an add-on keep its own part of a lighting scene. */
  scenes?: {
    saved?: (name: string) => Promise<unknown>;
    /** May reject with an Error whose message can be shown to the user as it is. */
    apply?: (name: string) => Promise<unknown>;
    deleted?: (name: string) => Promise<unknown>;
  };
  /**
   * Answers for the development-only fake backend (see mock.ts).
   * Returns undefined for a method that is not this add-on's.
   */
  mock?: (method: string, params: any) => unknown;
};

const found = import.meta.glob<{ default: Extra }>("./private/*/index.tsx", {
  eager: true,
});

const extras: Extra[] = Object.keys(found)
  .sort()
  .map((path) => found[path]!.default);

/** Every add-on's panel for one place on a page. Shows nothing when there are none. */
export function ExtraPanels({ slot }: { slot: ExtraSlot }) {
  return (
    <>
      {extras.map((extra, i) => {
        const Panel = extra.panels?.[slot];
        return Panel ? <Panel key={i} /> : null;
      })}
    </>
  );
}

export const extraScenes = {
  /** A scene was saved: add-ons save their part too. Their trouble is not the user's problem here. */
  saved(name: string): void {
    for (const extra of extras)
      void extra.scenes?.saved?.(name).catch(() => undefined);
  },
  /** A scene is being applied: add-ons put their part back. Rejects with a message for the user. */
  async apply(name: string): Promise<void> {
    for (const extra of extras) await extra.scenes?.apply?.(name);
  },
  /** A scene was deleted: add-ons drop their part too. */
  deleted(name: string): void {
    for (const extra of extras)
      void extra.scenes?.deleted?.(name).catch(() => undefined);
  },
};

/** The fake backend's answer from an add-on, or undefined when no add-on knows the method. */
export function extrasMock(method: string, params: unknown): unknown {
  for (const extra of extras) {
    const answer = extra.mock?.(method, params);
    if (answer !== undefined) return answer;
  }
  return undefined;
}

/** An add-on's plain name for one of its modules, or undefined when no add-on knows it. */
export function extraModuleName(module: string): string | undefined {
  for (const extra of extras) {
    const name = extra.moduleNames?.[module];
    if (name) return name;
  }
  return undefined;
}
