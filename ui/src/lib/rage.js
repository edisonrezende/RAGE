// Ponte com o RAGE!.exe (WebView2). No navegador (npm run dev) usa dados simulados.
import { mockCall, mockStart } from "./mock.js";

const host = typeof window !== "undefined" ? window.chrome?.webview : null;
export const isHost = !!host;

const pending = new Map();
const listeners = new Map();
let seq = 0;

function emit(event, data) {
  const set = listeners.get(event);
  if (set) for (const fn of [...set]) { try { fn(data); } catch (e) { console.error(e); } }
}

if (host) {
  host.addEventListener("message", (e) => {
    const m = e.data;
    if (!m) return;
    if (m.event) return emit(m.event, m.data);
    const p = pending.get(m.id);
    if (!p) return;
    if (m.progress !== undefined) return p.onProgress?.(m.progress);
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result);
    else {
      const err = new Error(m.error || "Erro desconhecido");
      err.canceled = !!m.canceled;
      p.reject(err);
    }
  });
} else {
  mockStart(emit);
}

/** Chama uma função do host. opts: { onProgress, signal } */
export function call(method, params = {}, opts = {}) {
  if (!host) return mockCall(method, params, opts);
  const id = `r${++seq}`;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject, onProgress: opts.onProgress });
    if (opts.signal) {
      if (opts.signal.aborted) { host.postMessage({ cancel: id }); }
      opts.signal.addEventListener("abort", () => host.postMessage({ cancel: id }), { once: true });
    }
    host.postMessage({ id, method, params });
  });
}

/** Assina um evento do host (telemetry, activity, boost, game, overlay). Retorna a função para cancelar. */
export function on(event, fn) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event).add(fn);
  return () => listeners.get(event)?.delete(fn);
}

export const rage = { call, on, isHost };
export default rage;
