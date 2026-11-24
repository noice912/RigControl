import { useEffect, useId, useRef, useState, type ReactNode } from "react";

export function Switch(props: {
  checked: boolean;
  onChange: (value: boolean) => void;
  label?: ReactNode;
  disabled?: boolean;
}) {
  return (
    <label className="switch">
      <input
        type="checkbox"
        role="switch"
        checked={props.checked}
        disabled={props.disabled}
        onChange={(e) => props.onChange(e.target.checked)}
      />
      <span className="track" aria-hidden="true" />
      {props.label && <span>{props.label}</span>}
    </label>
  );
}

export function Segmented<T extends string | number>(props: {
  value: T;
  options: { value: T; label: string; disabled?: boolean }[];
  onChange: (value: T) => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <div className="segmented" role="group" aria-label={props.label}>
      {props.options.map((o) => (
        <button
          key={String(o.value)}
          type="button"
          aria-pressed={o.value === props.value}
          disabled={props.disabled || o.disabled}
          onClick={() => props.onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

/**
 * Range slider. `onChange` fires while dragging; `onCommit` fires once when released
 * (use it for anything that talks to hardware).
 */
export function Slider(props: {
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange?: (value: number) => void;
  onCommit?: (value: number) => void;
  format?: (value: number) => string;
  label: string;
  disabled?: boolean;
}) {
  const [local, setLocal] = useState(props.value);
  const dragging = useRef(false);

  useEffect(() => {
    if (!dragging.current) setLocal(props.value);
  }, [props.value]);

  const span = props.max - props.min || 1;
  const fill = `${((local - props.min) / span) * 100}%`;
  const commit = () => {
    if (!dragging.current) return;
    dragging.current = false;
    props.onCommit?.(local);
  };

  return (
    <div className="slider">
      <input
        type="range"
        aria-label={props.label}
        min={props.min}
        max={props.max}
        step={props.step ?? 1}
        value={local}
        disabled={props.disabled}
        style={{ ["--fill" as string]: fill }}
        onChange={(e) => {
          dragging.current = true;
          const v = Number(e.target.value);
          setLocal(v);
          props.onChange?.(v);
        }}
        onPointerUp={commit}
        onKeyUp={commit}
        onBlur={commit}
      />
      <output>{props.format ? props.format(local) : local}</output>
    </div>
  );
}

export function Notice(props: {
  tone?: "info" | "warn" | "error";
  title: ReactNode;
  children?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <div className={`notice ${props.tone && props.tone !== "info" ? props.tone : ""}`} role={props.tone === "error" ? "alert" : undefined}>
      <div className="notice-body">
        <div className="notice-title">{props.title}</div>
        {props.children && <div className="notice-text">{props.children}</div>}
      </div>
      {props.actions && <div className="notice-actions">{props.actions}</div>}
    </div>
  );
}

export function Dialog(props: {
  title: ReactNode;
  children: ReactNode;
  actions: ReactNode;
  onClose?: () => void;
}) {
  const titleId = useId();
  const ref = useRef<HTMLDivElement>(null);
  // Keep the latest onClose without re-running the effect (the page re-renders every second
  // with live readings; re-running would steal focus back to the first button).
  const onCloseRef = useRef(props.onClose);
  onCloseRef.current = props.onClose;

  useEffect(() => {
    const el = ref.current;
    const first = el?.querySelector<HTMLElement>("button, input, select");
    first?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && onCloseRef.current) {
        e.preventDefault();
        onCloseRef.current();
      }
      if (e.key === "Tab" && el) {
        const items = [...el.querySelectorAll<HTMLElement>("button, input, select, [tabindex]")].filter((x) => !x.hasAttribute("disabled"));
        if (items.length === 0) return;
        const firstItem = items[0]!;
        const lastItem = items[items.length - 1]!;
        if (e.shiftKey && document.activeElement === firstItem) {
          e.preventDefault();
          lastItem.focus();
        } else if (!e.shiftKey && document.activeElement === lastItem) {
          e.preventDefault();
          firstItem.focus();
        }
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  return (
    <div className="backdrop" onMouseDown={(e) => e.target === e.currentTarget && props.onClose?.()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref}>
        <h2 id={titleId}>{props.title}</h2>
        <div className="notice-text">{props.children}</div>
        <div className="dialog-actions">{props.actions}</div>
      </div>
    </div>
  );
}

/** A confirm dialog driven by a promise: `const ok = await confirm({...})`. */
export function useConfirm() {
  const [state, setState] = useState<null | {
    title: string;
    body: ReactNode;
    confirmLabel: string;
    danger?: boolean;
    resolve: (ok: boolean) => void;
  }>(null);

  const confirm = (opts: { title: string; body: ReactNode; confirmLabel: string; danger?: boolean }) =>
    new Promise<boolean>((resolve) => setState({ ...opts, resolve }));

  const close = (ok: boolean) => {
    state?.resolve(ok);
    setState(null);
  };

  const element = state ? (
    <Dialog
      title={state.title}
      onClose={() => close(false)}
      actions={
        <>
          <button className="btn" onClick={() => close(false)}>
            Cancel
          </button>
          <button className={`btn ${state.danger ? "danger" : "primary"}`} onClick={() => close(true)}>
            {state.confirmLabel}
          </button>
        </>
      }
    >
      {state.body}
    </Dialog>
  ) : null;

  return { confirm, element };
}

export function Meter(props: { label: string; value: string; unit?: string; fraction: number | null; detail?: string }) {
  return (
    <div>
      <div className="meter-label">
        <span>{props.label}</span>
        {props.detail && <span className="faint">{props.detail}</span>}
      </div>
      <div className="meter-value">
        {props.value}
        {props.unit && <small>{props.unit}</small>}
      </div>
      <div className="bar">
        <div style={{ width: `${Math.round((props.fraction ?? 0) * 100)}%` }} />
      </div>
    </div>
  );
}

export function Loading({ what }: { what: string }) {
  return <div className="empty">Reading {what}…</div>;
}
