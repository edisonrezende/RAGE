const nf = (d = 0) => new Intl.NumberFormat("pt-BR", { minimumFractionDigits: d, maximumFractionDigits: d });
const cache = {};
const num = (v, d = 0) => (cache[d] ||= nf(d)).format(v);

export const isNum = (v) => typeof v === "number" && Number.isFinite(v);

export function bytes(b, d) {
  if (!isNum(b)) return "—";
  const u = ["B", "KB", "MB", "GB", "TB"];
  let i = 0;
  let v = Math.abs(b);
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  const digits = d ?? (i === 0 ? 0 : v >= 100 ? 0 : 1);
  return `${num(Math.sign(b) * v, digits)} ${u[i]}`;
}

export const gb = (b, d = 1) => (isNum(b) ? `${num(b / 1073741824, d)} GB` : "—");

/** Taxa em bytes/s -> "1,2 MB/s" */
export function rate(bps) {
  if (!isNum(bps)) return "—";
  return `${bytes(bps, bps < 1024 ? 0 : 1)}/s`;
}

/** Taxa de rede em bits -> "12,4 Mbps" */
export function bits(bps) {
  if (!isNum(bps)) return "—";
  const b = bps * 8;
  if (b >= 1e9) return `${num(b / 1e9, 1)} Gbps`;
  if (b >= 1e6) return `${num(b / 1e6, 1)} Mbps`;
  if (b >= 1e3) return `${num(b / 1e3, 0)} Kbps`;
  return `${num(b, 0)} bps`;
}

let fahrenheit = false;
export const setFahrenheit = (v) => { fahrenheit = !!v; };
export function temp(c, d = 0) {
  if (!isNum(c)) return "—";
  return fahrenheit ? `${num(c * 9 / 5 + 32, d)} °F` : `${num(c, d)} °C`;
}
export const tempVal = (c) => (isNum(c) ? (fahrenheit ? c * 9 / 5 + 32 : c) : null);
export const tempUnit = () => (fahrenheit ? "°F" : "°C");

export const pct = (v, d = 0) => (isNum(v) ? `${num(v, d)}%` : "—");
export const n = (v, d = 0) => (isNum(v) ? num(v, d) : "—");
export function mhz(v) {
  if (!isNum(v) || v <= 0) return "—";
  return v >= 1000 ? `${num(v / 1000, 2)} GHz` : `${num(v, 0)} MHz`;
}
export const watts = (v) => (isNum(v) ? `${num(v, v < 10 ? 1 : 0)} W` : "—");
export const ms = (v) => (isNum(v) ? `${num(v, 0)} ms` : "—");

export function duration(sec) {
  if (!isNum(sec)) return "—";
  sec = Math.max(0, Math.round(sec));
  const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}min`;
  if (m) return `${m}min${s && m < 10 ? ` ${s}s` : ""}`;
  return `${s}s`;
}

export function playTime(sec) {
  if (!isNum(sec) || sec < 60) return "Nunca jogado";
  const h = sec / 3600;
  return h >= 1 ? `${num(h, h < 10 ? 1 : 0)} h jogadas` : `${Math.round(sec / 60)} min jogados`;
}

export function rel(date) {
  if (!date) return "—";
  const t = new Date(date).getTime();
  if (!t) return "—";
  const s = (Date.now() - t) / 1000;
  if (s < 45) return "agora";
  if (s < 3600) return `há ${Math.round(s / 60)} min`;
  if (s < 86400) return `há ${Math.round(s / 3600)} h`;
  if (s < 86400 * 7) return `há ${Math.round(s / 86400)} d`;
  return dateShort(date);
}

export function dateShort(date) {
  if (!date) return "—";
  const d = new Date(date);
  if (isNaN(d)) return "—";
  return d.toLocaleDateString("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" });
}
export function dateTime(date) {
  if (!date) return "—";
  const d = new Date(date);
  if (isNaN(d)) return "—";
  return d.toLocaleString("pt-BR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}
export function clock(date) {
  const d = new Date(date);
  return isNaN(d) ? "—" : d.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" });
}

export const plural = (count, one, many) => `${n(count)} ${count === 1 ? one : many}`;
