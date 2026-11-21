import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { lastN, useHistory, useRpc, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { AreaChart, CoreBars, TickRing } from "../components/Charts.jsx";
import { Button, Chip, Icon, Panel, cx } from "../components/ui.jsx";

const SAFETY = 98;
const DURATIONS = [1, 5, 10, 15, 30];

// Escala do anel de temperatura, em °C: neutro até 80, âmbar até 90, vermelho depois.
const T_MIN = 30, T_MAX = 105, T_WARN = 80, T_HOT = 90;
const tempColor = (t) => {
  const c = T_MIN + t * (T_MAX - T_MIN);
  return c >= T_HOT ? "var(--color-danger)" : c >= T_WARN ? "#ff8a3a" : "var(--tick-base)";
};

export default function Stress() {
  const { toast } = useApp();
  const nav = useNavigate();
  const s = useTelemetry();
  const h = useHistory();
  const { data: status, setData: setStatus } = useRpc("stress.status");
  const [minutes, setMinutes] = useState(5);
  const [stats, setStats] = useState(null);
  const [, tick] = useState(0);
  const running = status?.running;
  const startedAt = status?.startedAt ? new Date(status.startedAt).getTime() : null;
  const elapsed = running && startedAt ? (Date.now() - startedAt) / 1000 : 0;
  const total = (status?.minutes || minutes) * 60;
  const base = useRef(null);

  useEffect(() => { if (!running) return; const t = setInterval(() => tick((x) => x + 1), 1000); return () => clearInterval(t); }, [running]);

  // estatísticas da sessão + parada de segurança
  useEffect(() => {
    if (!running || !s) return;
    if (!base.current) base.current = { clock: s.cpu?.clock };
    setStats((st) => {
      const t = s.cpu?.temp, c = s.cpu?.clock;
      const n = { maxTemp: Math.max(st?.maxTemp ?? 0, t ?? 0), minClock: Math.min(st?.minClock ?? 99999, c ?? 99999), maxClock: Math.max(st?.maxClock ?? 0, c ?? 0), maxPower: Math.max(st?.maxPower ?? 0, s.cpu?.power ?? 0), samples: (st?.samples || 0) + 1 };
      return n;
    });
    if (s.cpu?.temp >= SAFETY) { stop(true); }
    if (elapsed >= total + 2) setStatus((x) => ({ ...x, running: false }));
  }, [s]); // eslint-disable-line react-hooks/exhaustive-deps

  const start = async () => {
    try { base.current = null; setStats(null); const r = await call("stress.start", { minutes }); setStatus({ ...r, running: true, startedAt: new Date().toISOString(), minutes }); toast(`Stress test de ${minutes} min iniciado`, "brand"); }
    catch (e) { toast(e.message, "danger"); }
  };
  const stop = async (safety) => {
    try { await call("stress.stop"); } catch { /* */ }
    setStatus((x) => ({ ...x, running: false }));
    if (safety === true) toast(`Parado por segurança: CPU chegou a ${SAFETY} °C`, "warning");
  };

  const throttling = stats && stats.maxClock > 0 && stats.minClock < stats.maxClock * 0.75 && stats.maxTemp >= 90;
  const verdict = !stats ? null : throttling ? { tone: "warning", icon: "warning", text: "Throttling térmico: o processador reduziu o clock por causa do calor. Limpe as ventoinhas, use uma base com cooler ou troque a pasta térmica." }
    : stats.maxTemp >= 90 ? { tone: "warning", icon: "device_thermostat", text: "Temperatura alta, mas o clock se manteve. Fique de olho na ventilação." }
    : { tone: "success", icon: "verified", text: "Estável: temperatura e clock sob controle." };
  const tempVals = lastN(h.cpuTemp, 300).map(f.tempVal);
  const temp = s?.cpu?.temp;
  const deg = (c) => `${f.n(f.tempVal(c))}°`;
  const session = [
    ["Temp. máx.", stats ? f.temp(stats.maxTemp) : "—"],
    ["Clock mín.", stats?.minClock < 99999 ? f.mhz(stats.minClock) : "—"],
    ["Clock máx.", stats?.maxClock ? f.mhz(stats.maxClock) : "—"],
    ["Consumo máx.", stats?.maxPower ? f.watts(stats.maxPower) : "—"],
  ];
  const legend = [["var(--tick-base)", "Normal"], ["#ff8a3a", `Quente ${deg(T_WARN)}`], ["var(--color-danger)", `Crítico ${deg(T_HOT)}`]];

  return (
    <div className="space-y-4">
      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        <section className="hot-card flex min-w-0 flex-col p-5">
          <p className="text-[12.5px] font-semibold">{running ? "Tempo restante" : "Pronto para começar"}</p>
          <p className="heading mt-3 text-[34px] leading-none font-semibold tabular">{running ? f.duration(Math.max(0, total - elapsed)) : `${minutes}:00`}</p>
          <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--hot-ink-2)]">
            Coloca todos os núcleos em 100% para ver até onde a temperatura sobe e se o processador perde velocidade (throttling).
          </p>
          <div role="radiogroup" aria-label="Duração" className={cx("mt-4 flex gap-1.5 transition-opacity", running && "pointer-events-none opacity-50")}>
            {DURATIONS.map((m) => (
              <button key={m} role="radio" aria-checked={minutes === m} data-on={minutes === m} disabled={running} onClick={() => setMinutes(m)} className="hot-chip">{m} min</button>
            ))}
          </div>
          <div className="hot-pill mt-2" data-dark="true"><span>Para sozinho em</span><b>{f.temp(SAFETY)}</b></div>
          <div className="mt-auto pt-5">
            {running
              ? <button onClick={() => stop()} className="hot-btn" style={{ "--p": Math.min(1, elapsed / total) }}><Icon name="stop" size={19} fill />Parar</button>
              : <button onClick={start} className="hot-btn"><Icon name="play_arrow" size={20} fill />Iniciar stress test</button>}
          </div>
        </section>

        <Panel title="Temperatura da CPU" icon="device_thermostat">
          <div className="flex h-full flex-col items-center">
            <TickRing value={temp != null ? (temp - T_MIN) / (T_MAX - T_MIN) : 0} colorAt={tempColor}>
              <span className={cx("heading text-[40px] leading-none font-semibold tabular", temp >= T_HOT ? "text-[var(--color-danger)]" : "")}>{temp != null ? f.n(f.tempVal(temp)) : "—"}</span>
              <span className="mt-1.5 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)]">{f.tempUnit()}</span>
            </TickRing>
            {temp == null ? (
              <div className="-mt-2 flex flex-col items-center gap-2 text-center">
                <p className="text-[12px] text-[var(--text-tertiary)]">Sem leitura de temperatura neste PC.</p>
                <Button size="sm" icon="download" onClick={() => nav("/configuracoes#sensores")}>Instalar o driver de sensores</Button>
              </div>
            ) : (
              <div className="-mt-4 flex flex-wrap items-center justify-center gap-x-4 gap-y-1 text-[11.5px] text-[var(--text-secondary)]">
                {legend.map(([c, l]) => <span key={l} className="flex items-center gap-1.5"><span className="size-2 rounded-[3px]" style={{ background: c }} />{l}</span>)}
              </div>
            )}
            <div className="mt-auto grid w-full grid-cols-2 gap-y-3 pt-5 text-center sm:grid-cols-4">
              {session.map(([l, v], i) => (
                <div key={l} className={cx(i > 0 && "sm:border-l sm:border-[var(--panel-border)]")}>
                  <p className="text-[14px] font-semibold tabular">{v}</p>
                  <p className="mt-0.5 text-[11px] text-[var(--text-tertiary)]">{l}</p>
                </div>
              ))}
            </div>
          </div>
        </Panel>

        <Panel className="lg:col-span-2 xl:col-span-1" title="Uso por núcleo" icon="grid_view" actions={<span className="text-[13px] font-semibold tabular">{f.pct(s?.cpu?.load)}</span>}>
          <div className="flex h-full flex-col">
            <CoreBars values={s?.cpu?.perCore || []} height={228} />
            <p className="mt-auto pt-3 text-[12px] text-[var(--text-tertiary)]">{s?.cpu?.cores ? `${s.cpu.cores} núcleos · ${s.cpu.threads} threads` : "—"}</p>
          </div>
        </Panel>
      </div>

      {verdict && !running && (
        <div className={cx("flex items-center gap-3 rounded-[var(--radius-xl)] px-4 py-3 text-[13px]", verdict.tone === "success" ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : "bg-[var(--color-warning-soft)] text-[var(--color-warning)]")}>
          <Icon name={verdict.icon} size={20} /><span>{verdict.text}</span>
        </div>
      )}

      <div className="grid gap-4 xl:grid-cols-3">
        <Panel className="xl:col-span-2" title="Temperatura nos últimos 5 min" icon="show_chart" actions={<Chip tone={temp >= 90 ? "danger" : temp >= 80 ? "warning" : "neutral"}>{f.temp(temp, 1)}</Chip>}>
          {temp == null ? <p className="py-10 text-center text-[13px] text-[var(--text-tertiary)]">Temperatura indisponível. Instale o driver de sensores em Configurações.</p> : (
            <AreaChart values={tempVals} times={lastN(h.t, 300)} height={190} min={30} max={f.tempVal(105)} refLine={f.tempVal(SAFETY)} fmt={(v) => `${f.n(v)}°`} />
          )}
        </Panel>
        <Panel title="Clock" icon="speed" actions={<span className="text-[13px] font-semibold tabular">{f.mhz(s?.cpu?.clock)}</span>}>
          <AreaChart values={lastN(h.cpuClock, 300)} height={190} color="var(--chart-gpu)" min={0} fmt={(v) => f.n(v / 1000, 1)} />
        </Panel>
      </div>
    </div>
  );
}
