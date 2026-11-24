import { useRef, useState } from "react";
import type { CurvePoint } from "../types";
import { addPoint, evaluateCurve, movePoint, removePoint } from "../curve";
import { toUnit, unitLabel, useTempUnit } from "../units";

const W = 640;
const H = 240;
const PAD = { left: 40, right: 14, top: 12, bottom: 28 };

/**
 * Drag points to shape a temperature → speed curve. Arrow keys move the focused point
 * (Shift = 5 steps). The curve is kept valid at every step, so it can always be applied.
 */
export function CurveEditor(props: {
  points: CurvePoint[];
  onChange: (points: CurvePoint[]) => void;
  range: { min: number; max: number };
  minDuty: number;
  maxPoints?: number | null;
  currentTempC?: number | null;
  sourceLabel: string;
  disabled?: boolean;
}) {
  const { points, range, minDuty } = props;
  const unit = useTempUnit();
  const deg = (c: number) => Math.round(toUnit(c, unit));
  const svgRef = useRef<SVGSVGElement>(null);
  const [active, setActive] = useState<number | null>(null);
  const dragging = useRef<number | null>(null);

  const innerW = W - PAD.left - PAD.right;
  const innerH = H - PAD.top - PAD.bottom;
  const x = (t: number) => PAD.left + ((t - range.min) / (range.max - range.min)) * innerW;
  const y = (d: number) => PAD.top + (1 - d / 100) * innerH;

  const toValue = (clientX: number, clientY: number) => {
    const svg = svgRef.current;
    if (!svg) return null;
    const r = svg.getBoundingClientRect();
    const sx = ((clientX - r.left) / r.width) * W;
    const sy = ((clientY - r.top) / r.height) * H;
    const t = range.min + ((sx - PAD.left) / innerW) * (range.max - range.min);
    const d = (1 - (sy - PAD.top) / innerH) * 100;
    return { t, d };
  };

  const onPointerMove = (e: React.PointerEvent) => {
    if (dragging.current === null || props.disabled) return;
    const v = toValue(e.clientX, e.clientY);
    if (!v) return;
    props.onChange(movePoint(points, dragging.current, v.t, v.d, minDuty, range));
  };

  const onKey = (i: number, e: React.KeyboardEvent) => {
    if (props.disabled) return;
    const step = e.shiftKey ? 5 : 1;
    const p = points[i]!;
    let t = p.tempC;
    let d = p.dutyPercent;
    if (e.key === "ArrowUp") d += step;
    else if (e.key === "ArrowDown") d -= step;
    else if (e.key === "ArrowRight") t += step;
    else if (e.key === "ArrowLeft") t -= step;
    else if ((e.key === "Delete" || e.key === "Backspace") && points.length > 2) {
      e.preventDefault();
      props.onChange(removePoint(points, i));
      setActive(null);
      return;
    } else return;
    e.preventDefault();
    props.onChange(movePoint(points, i, t, d, minDuty, range));
  };

  const linePath = points.map((p, i) => `${i === 0 ? "M" : "L"}${x(p.tempC)},${y(p.dutyPercent)}`).join(" ");
  const first = points[0];
  const last = points[points.length - 1];
  const areaPath =
    first && last
      ? `M${x(range.min)},${y(first.dutyPercent)} L${x(first.tempC)},${y(first.dutyPercent)} ${points
          .map((p) => `L${x(p.tempC)},${y(p.dutyPercent)}`)
          .join(" ")} L${x(range.max)},${y(last.dutyPercent)} L${x(range.max)},${y(0)} L${x(range.min)},${y(0)} Z`
      : "";

  const tempTicks: number[] = [];
  const tickStep = range.max - range.min > 50 ? 10 : 5;
  for (let t = Math.ceil(range.min / tickStep) * tickStep; t <= range.max; t += tickStep) tempTicks.push(t);

  const now = props.currentTempC ?? null;
  const nowDuty = now === null ? null : evaluateCurve(points, now);
  const selected = active !== null ? points[active] : null;
  const canAdd = points.length < Math.min(8, props.maxPoints ?? 8);

  return (
    <div className="stack">
      <svg
        ref={svgRef}
        className="curve"
        viewBox={`0 0 ${W} ${H}`}
        role="group"
        aria-label={`Speed curve following ${props.sourceLabel}`}
        onPointerMove={onPointerMove}
        onPointerUp={() => (dragging.current = null)}
        onPointerLeave={() => (dragging.current = null)}
      >
        {[0, 25, 50, 75, 100].map((d) => (
          <g key={`d${d}`}>
            <line className="grid-line" x1={PAD.left} x2={W - PAD.right} y1={y(d)} y2={y(d)} />
            <text className="axis-text" x={PAD.left - 8} y={y(d) + 4} textAnchor="end">
              {d}%
            </text>
          </g>
        ))}
        {tempTicks.map((t) => (
          <text key={`t${t}`} className="axis-text" x={x(t)} y={H - 8} textAnchor="middle">
            {deg(t)}°
          </text>
        ))}
        {minDuty > 0 && (
          <rect className="forbidden" x={PAD.left} width={innerW} y={y(minDuty)} height={y(0) - y(minDuty)}>
            <title>{`Below ${minDuty}% is not allowed for this ${props.sourceLabel === "liquid temperature" ? "channel" : "header"}`}</title>
          </rect>
        )}
        <path className="area" d={areaPath} />
        <path className="line" d={linePath} />
        {now !== null && now >= range.min && now <= range.max && (
          <g>
            <line className="now-line" x1={x(now)} x2={x(now)} y1={PAD.top} y2={y(0)} />
            <text className="now-text" x={x(now) + 5} y={PAD.top + 11}>
              now {deg(now)}°
            </text>
          </g>
        )}
        {points.map((p, i) => (
          <circle
            key={i}
            className={`point ${active === i ? "active" : ""}`}
            cx={x(p.tempC)}
            cy={y(p.dutyPercent)}
            r={7}
            tabIndex={props.disabled ? -1 : 0}
            role="slider"
            aria-label={`Point ${i + 1}`}
            aria-valuetext={`${deg(p.tempC)} degrees, ${p.dutyPercent} percent`}
            aria-valuenow={p.dutyPercent}
            aria-valuemin={minDuty}
            aria-valuemax={100}
            onFocus={() => setActive(i)}
            onKeyDown={(e) => onKey(i, e)}
            onPointerDown={(e) => {
              if (props.disabled) return;
              (e.target as Element).setPointerCapture?.(e.pointerId);
              dragging.current = i;
              setActive(i);
            }}
            onDoubleClick={() => {
              if (!props.disabled && points.length > 2) {
                props.onChange(removePoint(points, i));
                setActive(null);
              }
            }}
          />
        ))}
      </svg>
      <div className="spread">
        <span className="curve-readout">
          {selected
            ? `Point ${active! + 1}: ${selected.dutyPercent}% at ${deg(selected.tempC)} ${unitLabel(unit)}`
            : nowDuty !== null
              ? `Right now: ${nowDuty}% at ${deg(now!)} ${unitLabel(unit)} ${props.sourceLabel}`
              : `Follows ${props.sourceLabel}. Drag a point, or focus it and use the arrow keys.`}
        </span>
        <span className="row">
          <button className="btn small" disabled={props.disabled || !canAdd} onClick={() => props.onChange(addPoint(points))}>
            Add point
          </button>
          <button
            className="btn small"
            disabled={props.disabled || active === null || points.length <= 2}
            onClick={() => {
              if (active !== null) {
                props.onChange(removePoint(points, active));
                setActive(null);
              }
            }}
          >
            Remove point
          </button>
        </span>
      </div>
    </div>
  );
}
