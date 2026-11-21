import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { call, on } from "./rage.js";

// ---------- Telemetria (último snapshot + 15 min de histórico) ----------
const MAX = 900;
const SERIES = ["cpuTemp", "cpuLoad", "cpuPower", "cpuClock", "gpuTemp", "gpuLoad", "gpuPower", "ramPct", "netDown", "netUp", "ping", "fps", "diskActive"];
const state = { snap: null, hist: Object.fromEntries([["t", []], ...SERIES.map((s) => [s, []])]), version: 0 };
const subs = new Set();
const notify = () => { state.version++; subs.forEach((f) => f()); };

function primaryGpu(s) { return s?.gpus?.find((g) => g.kind === "discrete") || s?.gpus?.[0]; }

function push(s) {
  const g = primaryGpu(s);
  const vals = {
    cpuTemp: s.cpu?.temp, cpuLoad: s.cpu?.load, cpuPower: s.cpu?.power, cpuClock: s.cpu?.clock,
    gpuTemp: g?.temp, gpuLoad: g?.load, gpuPower: g?.power, ramPct: s.ram?.pct,
    netDown: s.net?.down, netUp: s.net?.up, ping: s.net?.ping, fps: s.game?.fps, diskActive: s.disks?.find((d) => d.system)?.active,
  };
  const h = state.hist;
  h.t.push(s.t);
  for (const k of SERIES) h[k].push(vals[k] ?? null);
  if (h.t.length > MAX) for (const k of ["t", ...SERIES]) h[k].splice(0, h[k].length - MAX);
}

let started = false;
export function startTelemetry() {
  if (started) return;
  started = true;
  call("monitor.history", { seconds: MAX }).then((h) => {
    if (!h?.t) return;
    const merged = { t: [...h.t] };
    for (const k of SERIES) merged[k] = [...(h[k] || [])];
    // mantém o que chegou enquanto o histórico carregava
    const cut = merged.t.at(-1) || 0;
    const idx = state.hist.t.findIndex((t) => t > cut);
    if (idx >= 0) { merged.t.push(...state.hist.t.slice(idx)); for (const k of SERIES) merged[k].push(...state.hist[k].slice(idx)); }
    state.hist = merged;
    notify();
  }).catch(() => {});
  call("monitor.snapshot").then((s) => { if (s && !state.snap) { state.snap = s; notify(); } }).catch(() => {});
  on("telemetry", (s) => { state.snap = s; push(s); notify(); });
}

const subscribe = (f) => { subs.add(f); return () => subs.delete(f); };
export function useTelemetry() {
  return useSyncExternalStore(subscribe, () => state.snap);
}
export function useHistory() {
  useSyncExternalStore(subscribe, () => state.version);
  return state.hist;
}
export const getPrimaryGpu = primaryGpu;

/** Últimos n valores, completando à esquerda com null (o gráfico mostra só o trecho com dados). */
export function lastN(arr, n) {
  const r = (arr || []).slice(-n);
  return r.length < n ? [...Array(n - r.length).fill(null), ...r] : r;
}

// ---------- Chamadas ----------
/** Carrega dados de uma função do host. */
export function useRpc(method, params, deps = []) {
  const [data, setData] = useState(undefined);
  const [error, setError] = useState(null);
  const [loading, setLoading] = useState(true);
  const key = JSON.stringify(params ?? {});
  const alive = useRef(true);
  const load = useCallback((silent) => {
    if (!silent) setLoading(true);
    setError(null);
    return call(method, params ?? {})
      .then((r) => { if (alive.current) setData(r); return r; })
      .catch((e) => { if (alive.current) setError(e.message); })
      .finally(() => { if (alive.current) setLoading(false); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [method, key]);
  useEffect(() => { alive.current = true; load(); return () => { alive.current = false; }; }, [load, ...deps]);
  return { data, setData, error, loading, reload: load };
}

/** Executa uma ação longa com progresso e cancelamento. */
export function useAction() {
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState(null);
  const [lines, setLines] = useState([]);
  const ctrl = useRef(null);
  const run = useCallback(async (method, params, { keepLines } = {}) => {
    ctrl.current = new AbortController();
    setRunning(true);
    setProgress(null);
    if (!keepLines) setLines([]);
    try {
      return await call(method, params ?? {}, {
        signal: ctrl.current.signal,
        onProgress: (p) => {
          setProgress(p);
          if (p?.line != null) setLines((l) => (l.length > 600 ? [...l.slice(-500), p.line] : [...l, p.line]));
        },
      });
    } finally {
      setRunning(false);
    }
  }, []);
  const cancel = useCallback(() => ctrl.current?.abort(), []);
  return { run, cancel, running, progress, lines, setLines };
}

/** Assina um evento do host enquanto o componente estiver montado. */
export function useEvent(event, fn) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => on(event, (d) => ref.current(d)), [event]);
}
