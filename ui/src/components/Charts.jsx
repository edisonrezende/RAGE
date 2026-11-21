import { useEffect, useId, useMemo, useRef, useState } from "react";
import { cx } from "./ui.jsx";

function useWidth() {
  const ref = useRef(null);
  const [w, setW] = useState(0);
  useEffect(() => {
    if (!ref.current) return;
    const ro = new ResizeObserver(([e]) => setW(Math.round(e.contentRect.width)));
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, []);
  return [ref, w];
}

/** Caminho suave (Catmull-Rom → Bézier) ignorando buracos (null). */
function smoothPath(pts) {
  if (pts.length < 2) return "";
  let d = `M${pts[0][0]},${pts[0][1]}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i - 1] || pts[i], p1 = pts[i], p2 = pts[i + 1], p3 = pts[i + 2] || p2;
    const t = 0.18;
    const c1x = p1[0] + (p2[0] - p0[0]) * t, c1y = p1[1] + (p2[1] - p0[1]) * t;
    const c2x = p2[0] - (p3[0] - p1[0]) * t, c2y = p2[1] - (p3[1] - p1[1]) * t;
    d += ` C${c1x},${c1y} ${c2x},${c2y} ${p2[0]},${p2[1]}`;
  }
  return d;
}

function segments(values, x, y) {
  const segs = [];
  let cur = [];
  values.forEach((v, i) => {
    if (v == null || Number.isNaN(v)) { if (cur.length) segs.push(cur); cur = []; }
    else cur.push([x(i), y(v)]);
  });
  if (cur.length) segs.push(cur);
  return segs;
}

/**
 * Gráfico de área com grade, rótulos e dica ao passar o mouse.
 * values: number[] (null = sem dado). fmt: (v) => string. times: timestamps (opcional, p/ dica).
 */
export function AreaChart({ values, times, height = 200, color = "var(--chart-cpu)", min, max, fmt = (v) => v, fmtTime, ticks = 3, className, xLabels, refLine, smooth = true }) {
  const [ref, w] = useWidth();
  const gid = useId().replace(/:/g, "");
  const [hover, setHover] = useState(null);
  const padL = 40, padR = 10, padT = 10, padB = xLabels ? 22 : 8;
  const W = Math.max(0, w), H = height;
  const clean = values.filter((v) => v != null && !Number.isNaN(v));
  let lo = min ?? (clean.length ? Math.min(...clean) : 0);
  let hi = max ?? (clean.length ? Math.max(...clean) : 1);
  if (min == null || max == null) {
    const span = Math.max(1, hi - lo);
    if (min == null) lo = Math.max(0, Math.floor((lo - span * 0.25) / 5) * 5);
    if (max == null) hi = Math.ceil((hi + span * 0.25) / 5) * 5;
  }
  if (hi <= lo) hi = lo + 1;
  const n = Math.max(2, values.length);
  const x = (i) => padL + (i / (n - 1)) * (W - padL - padR);
  const y = (v) => padT + (1 - (v - lo) / (hi - lo)) * (H - padT - padB);
  const segs = useMemo(() => (W ? segments(values, x, y) : []), [values, W, lo, hi]); // eslint-disable-line react-hooks/exhaustive-deps
  const grid = Array.from({ length: ticks }, (_, i) => lo + ((hi - lo) * i) / (ticks - 1));
  const last = [...values].reverse().find((v) => v != null);
  const lastIdx = values.lastIndexOf(last);

  const onMove = (e) => {
    const r = e.currentTarget.getBoundingClientRect();
    const i = Math.round(((e.clientX - r.left - padL) / (W - padL - padR)) * (n - 1));
    if (i >= 0 && i < values.length && values[i] != null) setHover(i); else setHover(null);
  };

  return (
    <div ref={ref} className={cx("relative w-full select-none", className)} style={{ height: H }}>
      {W > 0 && (
        <svg width={W} height={H} onMouseMove={onMove} onMouseLeave={() => setHover(null)} className="overflow-visible">
          <defs>
            <linearGradient id={`g${gid}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.42" />
              <stop offset="100%" stopColor={color} stopOpacity="0" />
            </linearGradient>
          </defs>
          {grid.map((g, i) => (
            <g key={i}>
              <line x1={padL} x2={W - padR} y1={y(g)} y2={y(g)} stroke="var(--chart-grid)" strokeDasharray={i === 0 ? "" : "3 4"} />
              <text x={padL - 8} y={y(g) + 3.5} textAnchor="end" fontSize="10.5" fill="var(--text-tertiary)" className="tabular">{fmt(g)}</text>
            </g>
          ))}
          {refLine != null && refLine > lo && refLine < hi && (
            <g>
              <line x1={padL} x2={W - padR} y1={y(refLine)} y2={y(refLine)} stroke="var(--color-danger)" strokeOpacity=".6" strokeDasharray="4 4" />
            </g>
          )}
          {segs.map((s, i) => {
            const line = smooth ? smoothPath(s) : "M" + s.map((p) => p.join(",")).join(" L");
            const area = `${line} L${s.at(-1)[0]},${H - padB} L${s[0][0]},${H - padB} Z`;
            return (
              <g key={i}>
                <path d={area} fill={`url(#g${gid})`} />
                <path d={line} fill="none" stroke={color} strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />
              </g>
            );
          })}
          {last != null && hover == null && (
            <g>
              <circle cx={x(lastIdx)} cy={y(last)} r="7" fill={color} opacity=".18" />
              <circle cx={x(lastIdx)} cy={y(last)} r="3.5" fill={color} stroke="var(--bg-surface)" strokeWidth="1.5" />
            </g>
          )}
          {xLabels && xLabels.map((l, i) => (
            <text key={i} x={padL + (l.at) * (W - padL - padR)} y={H - 5} textAnchor={l.at === 0 ? "start" : l.at === 1 ? "end" : "middle"} fontSize="10.5" fill="var(--text-tertiary)">{l.label}</text>
          ))}
          {hover != null && (
            <g>
              <line x1={x(hover)} x2={x(hover)} y1={padT} y2={H - padB} stroke="var(--text-tertiary)" strokeOpacity=".5" />
              <circle cx={x(hover)} cy={y(values[hover])} r="4" fill={color} stroke="var(--bg-surface)" strokeWidth="2" />
            </g>
          )}
        </svg>
      )}
      {hover != null && (
        <div
          className="pointer-events-none absolute z-10 -translate-x-1/2 rounded-[var(--radius-sm)] border border-[var(--border)] bg-[var(--bg-surface)] px-2 py-1 text-[11.5px] shadow-[var(--shadow-pop)]"
          style={{ left: Math.min(Math.max(x(hover), 60), W - 60), top: Math.max(0, y(values[hover]) - 44) }}
        >
          <div className="font-semibold tabular">{fmt(values[hover])}</div>
          {times?.[hover] && <div className="text-[var(--text-tertiary)]">{fmtTime ? fmtTime(times[hover]) : new Date(times[hover]).toLocaleTimeString("pt-BR")}</div>}
        </div>
      )}
    </div>
  );
}

export function Sparkline({ values, color = "var(--chart-cpu)", height = 32, min = 0, max, className, fill = true }) {
  const [ref, w] = useWidth();
  const gid = useId().replace(/:/g, "");
  const clean = values.filter((v) => v != null);
  const hi = max ?? Math.max(1, ...clean) * 1.1;
  const n = Math.max(2, values.length);
  const x = (i) => (i / (n - 1)) * w;
  const y = (v) => 2 + (1 - (v - min) / (hi - min || 1)) * (height - 4);
  const segs = w ? segments(values, x, y) : [];
  return (
    <div ref={ref} className={cx("w-full", className)} style={{ height }}>
      {w > 0 && (
        <svg width={w} height={height}>
          <defs>
            <linearGradient id={`s${gid}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor={color} stopOpacity=".35" /><stop offset="1" stopColor={color} stopOpacity="0" /></linearGradient>
          </defs>
          {segs.map((s, i) => {
            const line = smoothPath(s);
            return (
              <g key={i}>
                {fill && <path d={`${line} L${s.at(-1)[0]},${height} L${s[0][0]},${height} Z`} fill={`url(#s${gid})`} />}
                <path d={line} fill="none" stroke={color} strokeWidth="1.6" />
              </g>
            );
          })}
        </svg>
      )}
    </div>
  );
}

/** Anel de progresso (gauge). */
export function Ring({ value, size = 120, stroke = 10, color = "var(--brand)", track = "var(--bg-layer-2)", children, gradient }) {
  const gid = useId().replace(/:/g, "");
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const v = Math.max(0, Math.min(100, value ?? 0));
  return (
    <div className="relative inline-flex items-center justify-center" style={{ width: size, height: size }}>
      <svg width={size} height={size} className="-rotate-90">
        {gradient && (
          <defs>
            <linearGradient id={`r${gid}`} x1="0" y1="0" x2="1" y2="1"><stop offset="0" stopColor="#ff2e3b" /><stop offset="1" stopColor="#ff8a00" /></linearGradient>
          </defs>
        )}
        <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={track} strokeWidth={stroke} />
        <circle
          cx={size / 2} cy={size / 2} r={r} fill="none" stroke={gradient ? `url(#r${gid})` : color} strokeWidth={stroke} strokeLinecap="round"
          strokeDasharray={c} strokeDashoffset={c * (1 - v / 100)} style={{ transition: "stroke-dashoffset .6s var(--ease-emphasized)" }}
        />
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">{children}</div>
    </div>
  );
}

/** Anéis concêntricos (RAM / VRAM / Disco). rings: [{value, color}] */
export function Rings({ rings, size = 150, stroke = 9, gap = 5, children }) {
  return (
    <div className="relative inline-flex items-center justify-center" style={{ width: size, height: size }}>
      <svg width={size} height={size} className="-rotate-90">
        {rings.map((ring, i) => {
          const r = size / 2 - stroke / 2 - i * (stroke + gap);
          const c = 2 * Math.PI * r;
          const v = Math.max(0, Math.min(100, ring.value ?? 0));
          return (
            <g key={i}>
              <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="var(--bg-layer-2)" strokeWidth={stroke} />
              <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={ring.color} strokeWidth={stroke} strokeLinecap="round"
                strokeDasharray={c} strokeDashoffset={c * (1 - v / 100)} style={{ transition: "stroke-dashoffset .6s var(--ease-emphasized)" }} />
            </g>
          );
        })}
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">{children}</div>
    </div>
  );
}

/** Barras verticais (uso por núcleo). */
export function CoreBars({ values, height = 44, className }) {
  return (
    <div className={cx("flex items-end gap-[3px]", className)} style={{ height }}>
      {values.map((v, i) => (
        <div key={i} className="relative flex-1 overflow-hidden rounded-[3px] bg-[var(--bg-layer-2)]" style={{ height: "100%" }} title={`Núcleo ${i + 1}: ${Math.round(v)}%`}>
          <div className="absolute inset-x-0 bottom-0 rounded-[3px] transition-[height] duration-500" style={{ height: `${Math.max(3, v)}%`, background: v > 85 ? "var(--color-danger)" : "var(--brand-gradient)" }} />
        </div>
      ))}
    </div>
  );
}

/** Barra fina horizontal com cor por faixa */
export function Meter({ value, warn = 75, danger = 90, height = 5 }) {
  const v = Math.max(0, Math.min(100, value ?? 0));
  const color = v >= danger ? "var(--color-danger)" : v >= warn ? "var(--color-warning)" : "var(--brand-gradient)";
  return (
    <div className="w-full overflow-hidden rounded-full bg-[var(--bg-layer-2)]" style={{ height }}>
      <div className="h-full rounded-full transition-[width] duration-500" style={{ width: `${v}%`, background: color }} />
    </div>
  );
}

/** Cor de cada tick: neutro até baseFrac, âmbar daí até redFrac, vermelho depois. */
export const zoneColor = (baseFrac = 0, redFrac = 0.9) => (t) => {
  if (t >= redFrac) return "var(--color-danger)";
  if (t < baseFrac) return "var(--tick-base)";
  const k = Math.round(((t - baseFrac) / Math.max(0.01, redFrac - baseFrac)) * 100);
  return `color-mix(in oklab, #ff5a1f ${k}%, #ffb347)`;
};

/** Mostrador de ticks em arco. value: 0..1; colorAt(t) dá a cor do tick na posição t (0..1). */
export function TickRing({ value, n = 56, size = 196, sweep = 270, tickLen = 13, tickWidth = 3.2, colorAt, children, className }) {
  const lit = Math.round(Math.max(0, Math.min(1, value || 0)) * n);
  const ticks = useMemo(() => {
    const c = size / 2, r1 = c - 2, r0 = r1 - tickLen, start = 90 + (360 - sweep) / 2;
    return Array.from({ length: n }, (_, i) => {
      const a = ((start + (i / (n - 1)) * sweep) * Math.PI) / 180;
      return [c + r0 * Math.cos(a), c + r0 * Math.sin(a), c + r1 * Math.cos(a), c + r1 * Math.sin(a)];
    });
  }, [n, size, sweep, tickLen]);
  return (
    <div className={cx("relative shrink-0", className)} style={{ width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
        {ticks.map(([x1, y1, x2, y2], i) => (
          <line key={i} x1={x1} y1={y1} x2={x2} y2={y2} strokeWidth={tickWidth} strokeLinecap="round"
            stroke={i < lit ? colorAt(i / (n - 1)) : "var(--tick-off)"} style={{ transition: "stroke .2s var(--ease-default)" }} />
        ))}
      </svg>
      <div className="absolute inset-0 flex flex-col items-center justify-center">{children}</div>
    </div>
  );
}
