import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useEvent, useHistory, useRpc, useTelemetry, getPrimaryGpu } from "../lib/store.js";
import * as f from "../lib/format.js";
import { AreaChart, Sparkline, TickRing, zoneColor } from "../components/Charts.jsx";
import { Button, Chip, Icon, Panel, Segmented, Skeleton, StatusDot, cx } from "../components/ui.jsx";

const RANGES = [{ value: 60, label: "1 MIN" }, { value: 300, label: "5 MIN" }, { value: 900, label: "15 MIN" }];
const METRICS = {
  temp: { label: "Temperatura", cpu: "cpuTemp", gpu: "gpuTemp", fmt: (v) => f.temp(v), color: { cpu: "var(--chart-cpu)", gpu: "var(--chart-gpu)" } },
  load: { label: "Carga", cpu: "cpuLoad", gpu: "gpuLoad", fmt: (v) => f.pct(v), min: 0, max: 100, color: { cpu: "var(--chart-cpu)", gpu: "var(--chart-gpu)" } },
  power: { label: "Consumo", cpu: "cpuPower", gpu: "gpuPower", fmt: (v) => f.watts(v), color: { cpu: "var(--chart-cpu)", gpu: "var(--chart-gpu)" } },
  clock: { label: "Clock", cpu: "cpuClock", gpu: null, fmt: (v) => f.mhz(v), color: { cpu: "var(--chart-cpu)" } },
};

function HeroChart() {
  const s = useTelemetry();
  const h = useHistory();
  const { info } = useApp();
  const nav = useNavigate();
  const [range, setRange] = useState(300);
  const [src, setSrc] = useState("cpu");
  const cpuTempOk = s?.cpu?.temp != null;
  const [metric, setMetric] = useState(null);
  const m = metric || (src === "cpu" && !cpuTempOk ? "load" : "temp");
  const def = METRICS[m];
  const key = def[src] || def.cpu;
  const raw = h[key].slice(-range);
  const pad = Math.max(0, range - raw.length);
  const vals = pad ? [...Array(pad).fill(null), ...raw] : raw;
  const times = pad ? [...Array(pad).fill(null), ...h.t.slice(-range)] : h.t.slice(-range);
  const g = getPrimaryGpu(s);
  const current = src === "cpu"
    ? { temp: s?.cpu?.temp, load: s?.cpu?.load, power: s?.cpu?.power, clock: s?.cpu?.clock }[m]
    : { temp: g?.temp, load: g?.load, power: g?.power }[m];
  const first = vals.find((v) => v != null);
  const delta = current != null && first != null ? current - first : null;
  const rangeLabel = RANGES.find((r) => r.value === range).label.toLowerCase();
  const displayVals = m === "temp" ? vals.map(f.tempVal) : vals;

  return (
    <Panel padded={false} className="col-span-12 xl:col-span-8">
      <div className="flex flex-wrap items-start gap-4 px-5 pt-4">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <Segmented
              value={src}
              onChange={(v) => { setSrc(v); if (v === "gpu" && m === "clock") setMetric("temp"); }}
              options={[{ value: "cpu", label: "CPU" }, { value: "gpu", label: "GPU" }]}
            />
            <span className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">{def.label}</span>
          </div>
          <div className="mt-2 flex items-baseline gap-3">
            <span className="heading text-[40px] leading-none font-semibold tabular">
              {current == null ? "—" : m === "temp" ? f.n(f.tempVal(current), 1) : m === "load" ? f.n(current, 1) : m === "power" ? f.n(current, 1) : f.n(current / 1000, 2)}
            </span>
            <span className="text-[18px] font-semibold text-[var(--text-secondary)]">{m === "temp" ? f.tempUnit() : m === "load" ? "%" : m === "power" ? "W" : "GHz"}</span>
            {delta != null && Math.abs(delta) >= 0.1 && (
              <span className={cx("text-[12px] font-medium", delta > 0 ? "text-[var(--color-warning)]" : "text-[var(--color-success)]")}>
                {delta > 0 ? "+" : ""}{m === "temp" ? f.n(delta, 1) + " °" : m === "clock" ? f.n(delta, 0) + " MHz" : f.n(delta, 1)} em {rangeLabel}
              </span>
            )}
          </div>
          <p className="mt-1 truncate text-[12px] text-[var(--text-tertiary)]">{src === "cpu" ? s?.cpu?.name : g?.name}{src === "gpu" && g?.state === "sleep" ? " · em repouso para economizar energia" : ""}</p>
        </div>
        <div className="flex flex-col items-end gap-2">
          <Segmented value={range} onChange={setRange} options={RANGES} />
          <div className="flex gap-1">
            {Object.entries(METRICS).filter(([k, d]) => d[src] !== null && (src === "cpu" || k !== "clock")).map(([k, d]) => (
              <button key={k} onClick={() => setMetric(k)} className={cx("h-6 rounded-full border px-2.5 text-[11px] font-semibold transition-colors",
                m === k ? "border-transparent bg-white text-[#111]" : "border-[var(--border)] text-[var(--text-tertiary)] hover:text-[var(--text-primary)]")}>{d.label}</button>
            ))}
          </div>
        </div>
      </div>
      <div className="relative px-3 pt-2 pb-3">
        <AreaChart
          values={displayVals}
          times={times}
          height={232}
          color={def.color[src] || def.color.cpu}
          min={def.min}
          max={def.max}
          fmt={m === "temp" ? (v) => `${f.n(v, 0)}°` : m === "load" ? (v) => `${f.n(v, 0)}%` : m === "power" ? (v) => `${f.n(v, 0)} W` : (v) => f.n(v / 1000, 1)}
          fmtTime={(t) => f.rel(t)}
          xLabels={[{ at: 0, label: rangeLabel.replace(" ", " ") }, { at: 0.5, label: range === 60 ? "30 s" : range === 300 ? "2,5 min" : "7,5 min" }, { at: 1, label: "agora" }]}
        />
        {m === "temp" && src === "cpu" && !cpuTempOk && (
          <div className="absolute inset-0 flex items-center justify-center">
            <div className="flex max-w-sm flex-col items-center gap-2 rounded-[var(--radius-lg)] border border-[var(--border)] bg-[var(--bg-surface)]/95 p-5 text-center shadow-[var(--shadow-pop)]">
              <Icon name="device_thermostat" size={26} className="text-[var(--brand)]" />
              <p className="text-[13px] font-semibold">Temperatura da CPU indisponível</p>
              <p className="text-[12px] text-[var(--text-tertiary)]">
                {info?.isAdmin ? "O Windows não expõe a temperatura real do processador sem um driver. Instale o PawnIO (gratuito e assinado) para liberar temperatura e consumo." : "Abra o RAGE! como administrador e instale o driver de sensores PawnIO."}
              </p>
              <Button size="sm" variant="brand" icon="download" onClick={() => nav("/configuracoes#sensores")}>Liberar sensores</Button>
            </div>
          </div>
        )}
      </div>
    </Panel>
  );
}

function BenchCard() {
  const s = useTelemetry();
  const nav = useNavigate();
  const { data } = useRpc("bench.history");
  const hist = useHistory();
  const last = data?.[0];
  const g = getPrimaryGpu(s);
  const game = s?.game;
  if (game?.fps != null) {
    return (
      <Panel className="col-span-12 xl:col-span-4" title="Jogando agora" icon="sports_esports" actions={<Chip tone="brand" dot>AO VIVO</Chip>}>
        <p className="truncate text-[13px] font-semibold text-[var(--text-secondary)]">{game.name}</p>
        <div className="mt-2 flex items-baseline gap-2">
          <span className="heading brand-text text-[52px] leading-none font-bold tabular">{f.n(game.fps)}</span>
          <span className="text-[16px] font-semibold text-[var(--text-secondary)]">FPS</span>
        </div>
        <div className="mt-4 grid grid-cols-3 gap-2 text-center">
          {[["1% low", f.n(game.low1)], ["Frametime", game.frameTime != null ? `${f.n(game.frameTime, 1)} ms` : "—"], ["GPU", f.pct(g?.load)]].map(([l, v]) => (
            <div key={l} className="rounded-[var(--radius-md)] bg-[var(--bg-subtle)] py-2"><p className="text-[11px] text-[var(--text-tertiary)]">{l}</p><p className="font-semibold tabular">{v}</p></div>
          ))}
        </div>
        <Sparkline values={hist.fps.slice(-120)} color="var(--brand)" height={54} className="mt-4" />
      </Panel>
    );
  }
  return (
    <Panel className="col-span-12 xl:col-span-4" padded={false}>
      <div className="flex h-full flex-col px-5 pt-4 pb-4">
        <p className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Desempenho · RAGE! Bench</p>
        <div className="mt-2 flex items-baseline gap-2">
          <span className="heading text-[40px] leading-none font-semibold tabular">{last ? f.n(last.total) : "—"}</span>
          <span className="text-[14px] font-semibold text-[var(--text-secondary)]">pts</span>
        </div>
        <p className="mt-1 text-[12px] text-[var(--text-tertiary)]">{last ? `Último teste ${f.rel(last.at)}` : "Ainda sem testes — descubra a nota do seu PC"}</p>
        <div className="mt-4 divide-y divide-[var(--border)] text-[13px]">
          {[["CPU · 1 núcleo", last?.scores?.cpuSingle, "pts"], ["CPU · todos os núcleos", last?.scores?.cpuMulti, "pts"], ["Memória", last?.scores?.memory, "GB/s"], ["Disco · leitura", last?.scores?.diskRead, "MB/s"]].map(([l, v, u]) => (
            <div key={l} className="flex items-center justify-between py-2"><span className="text-[var(--text-tertiary)]">{l}</span><span className="font-semibold tabular">{v != null ? `${f.n(v, u === "GB/s" ? 1 : 0)} ${u}` : "—"}</span></div>
          ))}
        </div>
        <div className="mt-3 space-y-1.5">
          <div className="flex items-center justify-between rounded-[var(--radius-sm)] bg-[var(--bg-subtle)] px-3 py-1.5 text-[12px]"><span className="text-[var(--text-tertiary)]">CPU</span><span className="truncate pl-3 font-semibold">{s?.cpu?.name?.replace(/ @.*/, "") || "—"}</span></div>
          <div className="flex items-center justify-between rounded-[var(--radius-sm)] bg-[var(--bg-subtle)] px-3 py-1.5 text-[12px]"><span className="text-[var(--text-tertiary)]">GPU</span><span className="truncate pl-3 font-semibold">{g?.name?.replace(/NVIDIA GeForce |AMD Radeon /, "") || "—"}</span></div>
        </div>
        <Button variant="primary" className="mt-auto w-full translate-y-1" size="lg" icon="play_arrow" onClick={() => nav("/benchmark?run=1")}>Rodar benchmark</Button>
      </div>
    </Panel>
  );
}

function ComponentTable() {
  const s = useTelemetry();
  const h = useHistory();
  const nav = useNavigate();
  if (!s) return <Panel className="col-span-12"><Skeleton className="h-56" /></Panel>;
  const rows = [];
  rows.push({ icon: "memory", name: "Processador", sub: s.cpu?.name, temp: s.cpu?.temp, load: s.cpu?.load, clock: f.mhz(s.cpu?.clock), use: f.watts(s.cpu?.power), spark: h.cpuLoad, color: "var(--chart-cpu)", to: "/stress" });
  for (const g of s.gpus || []) {
    rows.push({
      icon: g.kind === "discrete" ? "developer_board" : "videogame_asset", name: g.kind === "discrete" ? "Placa de vídeo" : "Vídeo integrado", sub: g.name,
      temp: g.state === "sleep" ? null : g.temp, sleep: g.state === "sleep", load: g.load, clock: g.clock ? f.mhz(g.clock) : "—",
      use: g.power != null ? f.watts(g.power) : g.vramTotal ? `${f.gb(g.vramUsed)} / ${f.gb(g.vramTotal, 0)}` : f.gb(g.sharedUsed),
      spark: g.kind === "discrete" ? h.gpuLoad : null, color: "var(--chart-gpu)", to: "/overclock",
    });
  }
  rows.push({ icon: "memory_alt", name: "Memória RAM", sub: `${f.gb(s.ram?.total, 0)} ${s.ram?.type || ""} ${s.ram?.speed ? `${s.ram.speed} MHz` : ""}`.trim(), temp: null, load: s.ram?.pct, clock: s.ram?.speed ? `${s.ram.speed} MHz` : "—", use: f.gb(s.ram?.used), spark: h.ramPct, color: "var(--chart-ram)", to: "/turbo" });
  for (const d of s.disks || []) {
    rows.push({ icon: d.removable ? "usb" : "hard_drive", name: `Disco ${d.letter}`, sub: d.model || d.label, temp: d.temp, load: d.active, clock: d.media ? `${d.media}${d.bus ? " " + d.bus : ""}` : "—", use: `${f.gb(d.free)} livres`, spark: d.system ? h.diskActive : null, color: "var(--chart-disk)", to: "/disco", life: d.life });
  }
  rows.push({ icon: s.net?.kind === "wifi" ? "wifi" : s.net?.kind === "offline" ? "wifi_off" : "lan", name: "Rede", sub: s.net?.adapter ? `${s.net.adapter}${s.net.linkMbps ? ` · ${s.net.linkMbps} Mbps` : ""}` : "Sem conexão", temp: null, load: null, clock: s.net?.ping != null ? `ping ${f.ms(s.net.ping)}` : "—", use: `↓ ${f.bits(s.net?.down)}  ↑ ${f.bits(s.net?.up)}`, spark: h.netDown, color: "var(--chart-net)", to: "/internet" });
  if (s.battery?.present) {
    rows.push({ icon: s.battery.charging ? "battery_charging_full" : "battery_5_bar", name: "Bateria", sub: s.battery.plugged ? (s.battery.charging ? "Carregando" : "Na tomada") : s.battery.minutesLeft ? `${f.duration(s.battery.minutesLeft * 60)} restantes` : "Na bateria", temp: null, load: s.battery.pct, clock: s.battery.rateW ? f.watts(Math.abs(s.battery.rateW)) : "—", use: s.battery.wear != null ? `desgaste ${f.pct(s.battery.wear)}` : "—", to: "/overclock" });
  }
  const tempClass = (t) => (t == null ? "text-[var(--text-muted)]" : t >= 90 ? "text-[var(--color-danger)]" : t >= 80 ? "text-[var(--color-warning)]" : "");
  return (
    <Panel className="col-span-12" padded={false}>
      <table className="w-full text-[13px]">
        <thead>
          <tr className="text-left text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">
            <th className="py-3 pl-5 font-bold">Componente</th>
            <th className="w-[13%] font-bold">Temperatura</th>
            <th className="w-[19%] font-bold">Carga</th>
            <th className="w-[13%] font-bold">Clock</th>
            <th className="w-[18%] font-bold">Consumo / uso</th>
            <th className="w-10" />
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} onClick={() => nav(r.to)} className="group cursor-pointer border-t border-[var(--border)] transition-colors hover:bg-[var(--bg-subtle)]">
              <td className="py-2.5 pl-5">
                <div className="flex items-center gap-3">
                  <span className="flex size-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--bg-layer-2)] text-[var(--text-secondary)]"><Icon name={r.icon} size={18} /></span>
                  <div className="min-w-0">
                    <p className="font-semibold">{r.name}</p>
                    <p className="max-w-[300px] truncate text-[11.5px] text-[var(--text-tertiary)]">{r.sub}</p>
                  </div>
                </div>
              </td>
              <td className={cx("font-semibold tabular", tempClass(r.temp))}>{r.sleep ? <span className="text-[12px] font-medium text-[var(--text-tertiary)]">repouso</span> : f.temp(r.temp)}</td>
              <td>
                <div className="flex items-center gap-2.5 pr-4">
                  <span className="w-11 font-semibold tabular">{f.pct(r.load)}</span>
                  {r.spark ? <Sparkline values={r.spark.slice(-90)} color={r.color} height={24} className="max-w-[110px]" /> : null}
                </div>
              </td>
              <td className="font-medium tabular text-[var(--text-secondary)]">{r.clock}</td>
              <td className="font-medium tabular text-[var(--text-secondary)]">{r.use}</td>
              <td className="pr-4 text-right"><Icon name="arrow_forward" size={17} className="text-[var(--text-muted)] transition-transform group-hover:translate-x-0.5 group-hover:text-[var(--text-primary)]" /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </Panel>
  );
}

const RAM_COLOR = zoneColor(0.75, 0.9);

function UseBar({ label, value, detail }) {
  const v = Math.max(0, Math.min(100, value ?? 0));
  const tone = value == null ? "transparent" : v >= 90 ? "var(--color-danger)" : v >= 75 ? "var(--color-warning)" : "var(--seg-on)";
  return (
    <div>
      <div className="flex items-baseline gap-3 text-[12.5px]">
        <span className="text-[var(--text-secondary)]">{label}</span>
        <span className="min-w-0 flex-1 truncate text-right text-[12px] text-[var(--text-tertiary)]">{detail}</span>
        <span className="w-10 text-right font-semibold tabular">{f.pct(value)}</span>
      </div>
      <div className="mt-1.5 h-1 overflow-hidden rounded-full bg-[var(--seg-off)]"><div className="h-full rounded-full transition-[width] duration-500" style={{ width: `${v}%`, background: tone }} /></div>
    </div>
  );
}

function MemoryCard() {
  const s = useTelemetry();
  const { toast } = useApp();
  const [busy, setBusy] = useState(false);
  const { data: top, reload } = useRpc("ram.top", { limit: 5 });
  useEffect(() => { const t = setInterval(() => reload(true), 10000); return () => clearInterval(t); }, [reload]);
  const g = getPrimaryGpu(s);
  const sys = s?.disks?.find((d) => d.system) || s?.disks?.[0];
  const vramPct = g?.vramTotal ? (g.vramUsed / g.vramTotal) * 100 : null;
  const diskPct = sys?.total ? ((sys.total - sys.free) / sys.total) * 100 : null;
  const commitPct = s?.ram?.commitLimit ? (s.ram.commitUsed / s.ram.commitLimit) * 100 : null;
  const clean = async () => {
    setBusy(true);
    try { const r = await call("ram.clean"); toast(`${r.freedText || f.bytes(r.freed)} de RAM liberados`, "success"); reload(true); } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  const list = (top || []).slice(0, 5);
  const biggest = Math.max(1, ...list.map((p) => p.ws || 0));
  return (
    <Panel className="col-span-12 lg:col-span-6 xl:col-span-4" padded={false}>
      <div className="flex h-full flex-col px-5 pt-4 pb-4">
        <p className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Memória e disco</p>
        <div className="mt-4 flex items-center gap-5">
          <TickRing value={(s?.ram?.pct || 0) / 100} size={124} n={40} tickLen={10} tickWidth={3} colorAt={RAM_COLOR}>
            <span className="heading text-[24px] leading-none font-semibold tabular">{f.pct(s?.ram?.pct)}</span>
            <span className="mt-1 text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">RAM</span>
          </TickRing>
          <div className="min-w-0 flex-1">
            <p className="heading text-[30px] leading-none font-semibold tabular">{f.gb(s?.ram?.used)}</p>
            <p className="mt-1.5 text-[12px] text-[var(--text-tertiary)]">em uso, de {f.gb(s?.ram?.total, 0)} instalados</p>
            <div className="mt-3 grid grid-cols-2 gap-x-4 text-[12px]">
              <div><p className="font-semibold tabular">{f.gb(s?.ram?.avail)}</p><p className="text-[var(--text-tertiary)]">livres</p></div>
              <div><p className="font-semibold tabular">{f.gb(s?.ram?.standby)}</p><p className="text-[var(--text-tertiary)]">em espera</p></div>
            </div>
          </div>
        </div>

        <div className="mt-5 grid gap-3">
          <UseBar label="VRAM" value={vramPct} detail={g?.vramTotal ? `${f.gb(g.vramUsed)} de ${f.gb(g.vramTotal, 0)}` : "sem memória dedicada"} />
          <UseBar label={`Disco ${sys?.letter || ""}`} value={diskPct} detail={sys ? `${f.gb(sys.free, 0)} livres` : "—"} />
          <UseBar label="Memória virtual" value={commitPct} detail={s?.ram?.commitLimit ? `${f.gb(s.ram.commitUsed, 0)} de ${f.gb(s.ram.commitLimit, 0)}` : "—"} />
        </div>

        <div className="mt-5 flex-1">
          <p className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Quem mais usa memória</p>
          <div className="mt-2.5 grid gap-2">
            {!top ? <Skeleton className="h-24" /> : list.length === 0 ? <p className="text-[12.5px] text-[var(--text-tertiary)]">Sem dados dos processos agora.</p> : list.map((p) => (
              <div key={p.pid} className="grid grid-cols-[minmax(0,1fr)_64px_62px] items-center gap-3 text-[12.5px]">
                <span className="truncate">{p.name}</span>
                <span className="h-1 overflow-hidden rounded-full bg-[var(--seg-off)]"><span className="block h-full rounded-full bg-[var(--chart-ram)] transition-[width] duration-500" style={{ width: `${((p.ws || 0) / biggest) * 100}%` }} /></span>
                <span className="text-right font-semibold tabular text-[var(--text-secondary)]">{f.bytes(p.ws)}</span>
              </div>
            ))}
          </div>
        </div>

        <Button className="mt-4 w-full" icon="mop" onClick={clean} loading={busy}>Limpar memória RAM</Button>
      </div>
    </Panel>
  );
}

const KIND_ICON_BG = { boost: "brand-bg text-white", clean: "bg-[var(--color-success-soft)] text-[var(--color-success)]", bench: "bg-[var(--chip-info-bg)] text-[var(--chip-info-text)]", warn: "bg-[var(--color-warning-soft)] text-[var(--color-warning)]" };

function ActivityCard() {
  const { data, setData } = useRpc("activity.list", { limit: 30 });
  useEvent("activity", (a) => setData((d) => [a, ...(d || [])].slice(0, 30)));
  return (
    <Panel className="col-span-12 lg:col-span-6 xl:col-span-4" title="Eventos recentes" icon="history" bodyClass="!pt-0">
      {!data ? <Skeleton className="h-48" /> : data.length === 0 ? (
        <p className="py-10 text-center text-[13px] text-[var(--text-tertiary)]">Nada por aqui ainda. Rode uma verificação ou ligue o Turbo.</p>
      ) : (
        <div className="scroll-area -mx-1 max-h-[244px] overflow-y-auto px-1">
          {data.slice(0, 20).map((a) => (
            <div key={a.id} className="flex items-center gap-3 border-b border-[var(--border)] py-2.5 last:border-0">
              <span className={cx("flex size-8 shrink-0 items-center justify-center rounded-full", KIND_ICON_BG[a.kind] || "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}><Icon name={a.icon || "bolt"} size={17} /></span>
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] font-semibold">{a.title}</p>
                {a.detail && <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{a.detail}</p>}
              </div>
              <div className="shrink-0 text-right">
                {a.value && <p className="text-[12.5px] font-semibold tabular">{a.value}</p>}
                <p className="text-[11px] text-[var(--text-tertiary)]">{f.rel(a.at)}</p>
              </div>
            </div>
          ))}
        </div>
      )}
    </Panel>
  );
}

function HealthCard() {
  const nav = useNavigate();
  const { toast } = useApp();
  const { data, error, reload } = useRpc("health.summary");
  const bad = (data || []).filter((x) => x.status !== "ok").length;
  const act = async (a) => {
    if (a.route) nav(a.route);
    else if (a.uri) call("app.open", { target: a.uri });
    else if (a.rpc) { try { await call(a.rpc); toast("Feito!", "success"); reload(); } catch (e) { toast(e.message, "danger"); } }
  };
  return (
    <Panel
      className="col-span-12 xl:col-span-4"
      title="Saúde do PC"
      icon="ecg_heart"
      actions={data && <Chip tone={bad ? "warning" : "success"}>{bad ? `${bad} ${bad === 1 ? "ponto de atenção" : "pontos de atenção"}` : "Tudo certo"}</Chip>}
      bodyClass="!pt-0"
    >
      {error ? <p className="py-6 text-[13px] text-[var(--text-tertiary)]">Não foi possível verificar agora.</p> : !data ? <Skeleton className="h-48" /> : (
        <div className="space-y-0.5">
          {data.map((x) => (
            <div key={x.id} className="flex items-center gap-3 rounded-[var(--radius-sm)] py-2">
              <span className={cx("flex size-6 shrink-0 items-center justify-center rounded-full", x.status === "ok" ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : x.status === "warn" ? "bg-[var(--color-warning-soft)] text-[var(--color-warning)]" : "bg-[var(--color-danger-soft)] text-[var(--color-danger)]")}>
                <Icon name={x.status === "ok" ? "check" : x.status === "warn" ? "priority_high" : "close"} size={15} />
              </span>
              <div className="min-w-0 flex-1">
                <p className="truncate text-[13px] font-semibold">{x.title}</p>
                {x.detail && <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{x.detail}</p>}
              </div>
              {x.action && x.status !== "ok" && <Button size="sm" variant="ghost" onClick={() => act(x.action)}>{x.action.label}</Button>}
            </div>
          ))}
          <Button variant="brand" className="mt-3 w-full" icon="health_and_safety" onClick={() => nav("/verificacao")}>Verificação inteligente</Button>
        </div>
      )}
    </Panel>
  );
}

function TurboBanner() {
  const { boost, setBoost, toast } = useApp();
  const nav = useNavigate();
  const [busy, setBusy] = useState(false);
  if (!boost?.active) return null;
  const stop = async () => { setBusy(true); try { setBoost(await call("boost.stop")); toast("Modo Turbo desligado — tudo restaurado"); } catch (e) { toast(e.message, "danger"); } setBusy(false); };
  return (
    <div className="col-span-12 flex items-center gap-4 overflow-hidden rounded-[var(--radius-lg)] border border-[#ff6a2b55] bg-[linear-gradient(90deg,#ff2e3b22,#ff8a0012)] px-4 py-3">
      <span className="brand-bg flex size-9 items-center justify-center rounded-full text-white glow"><Icon name="rocket_launch" size={19} fill /></span>
      <div className="min-w-0 flex-1">
        <p className="text-[13.5px] font-semibold">Modo Turbo {boost.mode === "game" ? "Jogo" : boost.mode === "work" ? "Trabalho" : "Economia"} ativo{boost.game ? ` · ${boost.game.name}` : ""}</p>
        <p className="truncate text-[12px] text-[var(--text-secondary)]">{boost.actions?.join(" · ") || "Aplicando…"}</p>
      </div>
      <Button size="sm" variant="ghost" onClick={() => nav("/turbo")}>Detalhes</Button>
      <Button size="sm" onClick={stop} loading={busy} icon="power_settings_new">Desligar</Button>
    </div>
  );
}

export default function Overview() {
  return (
    <div className="grid grid-cols-12 gap-4">
      <TurboBanner />
      <HeroChart />
      <BenchCard />
      <ComponentTable />
      <MemoryCard />
      <ActivityCard />
      <HealthCard />
    </div>
  );
}
