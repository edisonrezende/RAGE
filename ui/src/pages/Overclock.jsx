import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useAction, useHistory, useRpc, useTelemetry, getPrimaryGpu } from "../lib/store.js";
import { coolDown, isMuted, rageRev, setMuted, shiftClick } from "../lib/sound.js";
import * as f from "../lib/format.js";
import { TickRing, zoneColor } from "../components/Charts.jsx";
import { Button, Chip, ErrorBox, Icon, IconButton, KV, Panel, Progress, Segmented, Skeleton, Toggle, cx } from "../components/ui.jsx";
import { LogoMark } from "../components/Shell.jsx";

/* ============================================================
   Peças de base: relógio, escala do clock e botão de segurar
   ============================================================ */

function useElapsed(since) {
  const [, tick] = useState(0);
  useEffect(() => { if (!since) return; const t = setInterval(() => tick((x) => x + 1), 1000); return () => clearInterval(t); }, [since]);
  return since ? Math.max(0, (Date.now() - new Date(since).getTime()) / 1000) : 0;
}

const clockFmt = (sec) => {
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = Math.floor(sec % 60);
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
};

function useRevScale(s, h) {
  // Rotação máxima: o maior clock já visto (guardado por PC) ou 1,5x o clock base.
  const maxSeen = useMemo(() => {
    let stored = 0;
    try { stored = +localStorage.getItem("rage.cpuMax") || 0; } catch { /* */ }
    const seen = Math.max(stored, ...h.cpuClock.filter((v) => v != null));
    try { if (seen > stored) localStorage.setItem("rage.cpuMax", String(Math.round(seen))); } catch { /* */ }
    return seen;
  }, [h.cpuClock.length]); // eslint-disable-line react-hooks/exhaustive-deps
  const base = s?.cpu?.baseClock || 2000;
  const max = Math.max(maxSeen * 1.03, base * 1.5);
  return { base, max };
}

function useHold(onDone, ms = 1100) {
  const [p, setP] = useState(0);
  const [holding, setHolding] = useState(false);
  const raf = useRef(0), start = useRef(0), fired = useRef(false), timer = useRef(0);
  const stop = useCallback(() => {
    cancelAnimationFrame(raf.current);
    clearTimeout(timer.current);
    setHolding(false);
    if (!fired.current) {
      const from = performance.now(), p0 = start.current ? Math.min(1, (from - start.current) / ms) : 0;
      const back = (t) => { const k = Math.max(0, p0 - (t - from) / 260); setP(k); if (k > 0) raf.current = requestAnimationFrame(back); };
      raf.current = requestAnimationFrame(back);
    }
    start.current = 0;
  }, [ms]);
  const begin = useCallback(() => {
    if (holding) return;
    fired.current = false;
    setHolding(true);
    cancelAnimationFrame(raf.current);
    start.current = performance.now();
    // O disparo é por relógio (não depende do rAF); o rAF só anima o preenchimento.
    timer.current = setTimeout(() => {
      cancelAnimationFrame(raf.current);
      fired.current = true; setHolding(false); start.current = 0; setP(1);
      onDone();
      setTimeout(() => setP(0), 400);
    }, ms);
    const step = () => {
      if (!start.current) return;
      setP(Math.min(1, (performance.now() - start.current) / ms));
      raf.current = requestAnimationFrame(step);
    };
    raf.current = requestAnimationFrame(step);
  }, [holding, ms, onDone]);
  useEffect(() => () => { cancelAnimationFrame(raf.current); clearTimeout(timer.current); }, []);
  const bind = {
    onPointerDown: (e) => { if (e.button === 0) { e.currentTarget.setPointerCapture?.(e.pointerId); begin(); } },
    onPointerUp: stop, onPointerCancel: stop, onLostPointerCapture: () => holding && stop(),
    onKeyDown: (e) => { if ((e.key === " " || e.key === "Enter") && !e.repeat) { e.preventDefault(); begin(); } },
    onKeyUp: (e) => { if (e.key === " " || e.key === "Enter") stop(); },
  };
  return { p, holding, bind };
}

/* ============================================================
   Câmbio: a grade em H escolhe a marcha
   ============================================================ */

// Posição de cada marcha na grade, em % da área (x, y). O ponto morto fica no meio da barra.
const GATE = { stock: [50, 50], street: [26, 25], track: [26, 75], rage: [74, 25] };
const GATE_PATH = "M26,25 V75 M26,50 H74 M74,50 V25";
const GATE_LABEL = {
  stock: { left: "50%", top: "calc(50% + 28px)", transform: "translateX(-50%)" },
  street: { right: "calc(74% + 30px)", top: "25%", transform: "translateY(-50%)" },
  track: { right: "calc(74% + 30px)", top: "75%", transform: "translateY(-50%)" },
  rage: { left: "calc(74% + 30px)", top: "25%", transform: "translateY(-50%)" },
};

/** Caminho da alavanca entre duas marchas, sempre passando pela barra do ponto morto. */
function gateRoute(a, b) {
  const pts = [a];
  if (a[0] !== b[0]) {
    if (a[1] !== 50) pts.push([a[0], 50]);
    if (b[1] !== 50) pts.push([b[0], 50]);
  }
  pts.push(b);
  return pts;
}

function GearGate({ stages, sel, active, engaged, onSelect }) {
  const group = useRef(null), knob = useRef(null), prev = useRef(sel);
  useLayoutEffect(() => {
    const from = GATE[prev.current], to = GATE[sel];
    prev.current = sel;
    if (!from || !to || from === to || !knob.current?.animate) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    const pts = gateRoute(from, to);
    knob.current.animate(pts.map(([x, y]) => ({ left: `${x}%`, top: `${y}%` })), { duration: 150 + 110 * (pts.length - 1), easing: "cubic-bezier(.22,1,.36,1)" });
  }, [sel]);

  const order = stages.map((st) => st.id);
  const pick = (id) => { if (id !== sel) { shiftClick(); onSelect(id); } };
  const onKey = (e) => {
    const d = e.key === "ArrowRight" || e.key === "ArrowUp" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowDown" ? -1 : 0;
    if (!d) return;
    e.preventDefault();
    const next = order[Math.max(0, Math.min(order.length - 1, order.indexOf(sel) + d))];
    pick(next);
    group.current?.querySelector(`[data-gear="${next}"]`)?.focus();
  };
  const selDef = stages.find((st) => st.id === sel);
  const [kx, ky] = GATE[sel] || GATE.stock;
  const [ax, ay] = GATE[active] || GATE.stock;

  return (
    <div ref={group} role="radiogroup" aria-label="Marcha" onKeyDown={onKey} className="gate h-[236px] min-w-0">
      <svg className="absolute inset-0 size-full" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
        <path d={GATE_PATH} className="gate-edge" />
        <path d={GATE_PATH} className="gate-slot" />
        <path d={GATE_PATH} className="gate-guide" />
        {engaged && active !== "stock" && <path d={`M50,50 H${ax} V${ay}`} className="gate-trail" />}
      </svg>
      {stages.filter((st) => GATE[st.id]).map((st) => {
        const [x, y] = GATE[st.id];
        const on = sel === st.id, cur = active === st.id;
        return (
          <button key={st.id} role="radio" aria-checked={on} tabIndex={on ? 0 : -1} data-gear={st.id} data-current={cur && engaged}
            aria-label={`${st.name}, ${st.tag}${cur ? " (marcha ativa)" : ""}`} onClick={() => pick(st.id)}
            className="gate-node" style={{ left: `${x}%`, top: `${y}%` }}>
            <span className="gate-dot">{st.id === "rage" ? <LogoMark size={15} /> : st.glyph}</span>
          </button>
        );
      })}
      {stages.filter((st) => GATE_LABEL[st.id]).map((st) => (
        <span key={st.id} aria-hidden="true" style={GATE_LABEL[st.id]}
          className={cx("pointer-events-none absolute flex items-center gap-1.5 text-[12px] whitespace-nowrap transition-colors duration-150",
            sel === st.id ? "font-semibold text-[var(--text-primary)]" : "font-medium text-[var(--text-tertiary)]")}>
          {active === st.id && engaged && <span className="size-1.5 rounded-full bg-[var(--brand)]" />}
          {st.name}
        </span>
      ))}
      <span ref={knob} className="gate-knob" style={{ left: `${kx}%`, top: `${ky}%` }} aria-hidden="true">
        {sel === "rage" ? <LogoMark size={17} /> : selDef?.glyph}
      </span>
      <p className="absolute right-3.5 bottom-3 flex items-center gap-1 text-[11px] text-[var(--text-tertiary)]">
        <Icon name="keyboard_arrow_left" size={14} /><Icon name="keyboard_arrow_right" size={14} />trocam a marcha
      </p>
    </div>
  );
}

const STATS = ["Desempenho", "Temperatura", "Bateria", "Ruído"];

/** Cinco segmentos: valor da marcha escolhida, com o que sobe (laranja) e o que cai (contorno) em relação à ativa. */
function StatRow({ label, value, base }) {
  const d = value - base;
  return (
    <div className="grid grid-cols-[88px_1fr_24px] items-center gap-3">
      <span className="text-[12px] text-[var(--text-secondary)]">{label}</span>
      <div className="flex gap-1" role="img" aria-label={`${label}: ${value} de 5${d ? `, ${d > 0 ? "+" : "−"}${Math.abs(d)} em relação à marcha ativa` : ""}`}>
        {Array.from({ length: 5 }, (_, j) => (
          <span key={j} className="seg" data-s={j < Math.min(value, base) ? "on" : j < value ? "up" : j < base ? "down" : "off"} />
        ))}
      </div>
      <span className={cx("text-right text-[11.5px] font-semibold tabular", d > 0 ? "text-[var(--brand-primary-strong)]" : "text-[var(--text-tertiary)]")}>
        {d ? `${d > 0 ? "+" : "−"}${Math.abs(d)}` : ""}
      </span>
    </div>
  );
}

function GearCard({ tune, sel, setSel }) {
  const [muted, setMutedState] = useState(isMuted);
  const active = tune.active ? tune.stage : "stock";
  const selDef = tune.stages.find((x) => x.id === sel) || tune.stages[0];
  const activeDef = tune.stages.find((x) => x.id === active) || tune.stages[0];
  const toggleMute = () => { setMuted(!muted); setMutedState(!muted); };

  return (
    <section className="panel flex min-w-0 flex-col p-5 [grid-area:gear]">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="heading text-[16px] leading-tight font-semibold">Câmbio de preparação</h2>
          <p className="mt-1 max-w-[60ch] text-[12.5px] text-[var(--text-tertiary)]">Escolha a marcha e segure o botão. Fica envenenado até você voltar ao original, mesmo depois de reiniciar.</p>
        </div>
        <button onClick={toggleMute} aria-pressed={!muted}
          className="inline-flex h-7 shrink-0 items-center gap-1.5 rounded-full border border-[var(--panel-border)] px-2.5 text-[11.5px] font-semibold text-[var(--text-tertiary)] transition-colors hover:text-[var(--text-primary)]">
          <Icon name={muted ? "volume_off" : "volume_up"} size={15} />{muted ? "Som desligado" : "Som do motor"}
        </button>
      </div>

      <div className="mt-4 grid flex-1 gap-5 md:grid-cols-[minmax(0,1.08fr)_minmax(0,1fr)]">
        <GearGate stages={tune.stages} sel={sel} active={active} engaged={tune.active} onSelect={setSel} />

        <div className="flex min-w-0 flex-col">
          <div className="flex items-center gap-2">
            <h3 className="heading text-[20px] leading-tight font-semibold">{selDef.name}</h3>
            <span className="text-[13px] text-[var(--text-tertiary)]">{selDef.tag}</span>
            <span className="flex-1" />
            {sel === active && <Chip tone={tune.active ? "brand" : "neutral"} dot>{tune.active ? "Ativa" : "Atual"}</Chip>}
          </div>
          <p className="mt-1.5 text-[13px] leading-relaxed text-[var(--text-secondary)]">{selDef.desc}</p>
          {selDef.id === "rage" && (
            <p className="mt-2 flex items-center gap-1.5 text-[12px] text-[var(--color-warning)]"><Icon name="warning" size={15} />Esquenta mais e drena a bateria. Para jogar na tomada.</p>
          )}
          <div className="mt-4 grid gap-2.5">
            {STATS.map((label, k) => <StatRow key={label} label={label} value={selDef.stats[k]} base={activeDef.stats[k]} />)}
          </div>
          <p className="mt-auto pt-4 text-[12px] text-[var(--text-tertiary)]">
            {sel === active ? "Esta é a marcha em uso agora." : `Em laranja, o que muda em relação a ${activeDef.name}.`}
          </p>
        </div>
      </div>
    </section>
  );
}

/* ============================================================
   Ação: segurar para envenenar, ou voltar ao original
   ============================================================ */

function ActionCard({ tune, sel, onEngage, onDisengage }) {
  const { info } = useApp();
  const active = tune.active ? tune.stage : "stock";
  const selDef = tune.stages.find((x) => x.id === sel) || tune.stages[0];
  const running = tune.active && sel === active;
  const toStock = sel === "stock";
  const hold = useHold(() => onEngage(sel));
  const elapsed = useElapsed(tune.active ? tune.since : null);
  const total = tune.parts.length;
  const willInstall = tune.parts.filter((p) => p.minStage <= selDef.level).length;
  const installed = tune.parts.filter((p) => p.installed).length;
  const needsAdmin = !info?.isAdmin;

  // Voltar ao original (ou já estar nele) é o estado calmo: cartão neutro.
  if (toStock) {
    return (
      <section className="panel flex min-w-0 flex-col p-5 [grid-area:action]">
        <p className="text-[12.5px] font-semibold text-[var(--text-secondary)]">{tune.active ? "Voltar ao original" : "Sem preparação"}</p>
        <p className="mt-3 flex items-baseline gap-2">
          <span className="heading text-[34px] leading-none font-semibold">{selDef.name}</span>
          <span className="text-[15px] text-[var(--text-tertiary)]">{selDef.tag}</span>
        </p>
        <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--text-secondary)]">
          {tune.active ? `${f.plural(installed, "peça sai", "peças saem")} e o Windows volta a ser como era.` : "Escolha uma marcha no câmbio para preparar o PC."}
        </p>
        <div className="mt-auto pt-5">
          {needsAdmin && tune.active ? <AdminNote /> : (
            <Button size="xl" className="w-full !rounded-full" icon="settings_backup_restore" onClick={onDisengage} disabled={!tune.active}>
              {tune.active ? "Voltar ao original" : "Já está original"}
            </Button>
          )}
        </div>
      </section>
    );
  }

  return (
    <section className="hot-card flex min-w-0 flex-col p-5 [grid-area:action]">
      <p className="text-[12.5px] font-semibold">{running ? "Envenenado há" : tune.active ? "Trocar de marcha" : "Pronto para envenenar"}</p>
      {running ? (
        <p className="heading mt-3 text-[34px] leading-none font-semibold tabular">{clockFmt(elapsed)}</p>
      ) : (
        <p className="mt-3 flex items-baseline gap-2">
          <span className="heading text-[34px] leading-none font-semibold">{selDef.name}</span>
          <span className="text-[15px] text-[var(--hot-ink-2)]">{selDef.tag}</span>
        </p>
      )}
      <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--hot-ink-2)]">
        {running
          ? `${selDef.name}${selDef.id !== "rage" ? ` · ${selDef.tag}` : ""}, no plano de energia ${tune.plan || "atual"}.`
          : "As peças entram de uma vez e saem quando você voltar ao original."}
      </p>

      <div className="mt-4 grid gap-2">
        <div className="hot-pill" data-dark="true">
          <span>Peças</span>
          <b>{running ? `${installed} instaladas` : `${willInstall} de ${total}`}</b>
        </div>
        <div className="hot-pill">
          <span>{selDef.id === "rage" ? "Use na tomada" : "Tudo reversível"}</span>
          <Icon name={selDef.id === "rage" ? "power" : "settings_backup_restore"} size={16} />
        </div>
      </div>

      <div className="mt-auto pt-5">
        {needsAdmin ? <AdminNote hot /> : running ? (
          <button onClick={onDisengage} className="hot-btn"><Icon name="settings_backup_restore" size={19} />Voltar ao original</button>
        ) : (
          <button {...hold.bind} aria-label={`Segure para envenenar: ${selDef.name}`} className="hot-btn touch-none select-none" style={{ "--p": hold.p }}>
            <Icon name="local_fire_department" size={19} fill />
            {hold.holding ? `Segurando… ${Math.round(hold.p * 100)}%` : tune.active ? "Segure para trocar" : "Segure para envenenar"}
          </button>
        )}
      </div>
    </section>
  );
}

function AdminNote({ hot }) {
  return (
    <div>
      <p className={cx("text-[12.5px] leading-relaxed", hot ? "text-[var(--hot-ink)]" : "text-[var(--color-warning)]")}>Envenenar mexe no plano de energia do Windows: abra o RAGE! como administrador.</p>
      {hot
        ? <button onClick={() => call("app.elevate")} className="hot-btn mt-3"><Icon name="admin_panel_settings" size={19} />Abrir como administrador</button>
        : <Button size="xl" className="mt-3 w-full !rounded-full" icon="admin_panel_settings" onClick={() => call("app.elevate")}>Abrir como administrador</Button>}
    </div>
  );
}

/* ============================================================
   Clock da CPU e sensores
   ============================================================ */

function ClockCard() {
  const s = useTelemetry();
  const h = useHistory();
  const nav = useNavigate();
  const { base, max } = useRevScale(s, h);
  const clock = s?.cpu?.clock;
  const boost = clock && base ? (clock / base - 1) * 100 : null;
  const colorAt = useMemo(() => zoneColor(base / max), [base, max]);
  const legend = [["var(--tick-base)", `Base ${f.n(base / 1000, 1)}`], ["#ff8a3a", "Turbo"], ["var(--color-danger)", `Limite ${f.n(max / 1000, 1)}`]];
  const stats = [
    ["Turbo", boost != null ? `${boost >= 0 ? "+" : "−"}${f.n(Math.abs(boost))}%` : "—"],
    ["Núcleos", s?.cpu?.cores ? `${s.cpu.cores}/${s.cpu.threads}` : "—"],
    ["Consumo", f.watts(s?.cpu?.power)],
  ];
  return (
    <Panel className="[grid-area:clock]" title="Clock da CPU" icon="speed"
      actions={<IconButton icon="arrow_outward" size={18} label="Testar no Stress test" onClick={() => nav("/stress")} />}>
      <div className="flex h-full flex-col items-center">
        <TickRing value={clock ? clock / max : 0} colorAt={colorAt}>
          <span className="heading text-[40px] leading-none font-semibold tabular">{clock ? f.n(clock / 1000, 2) : "—"}</span>
          <span className="mt-1.5 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">GHz</span>
        </TickRing>
        <div className="-mt-4 flex flex-wrap items-center justify-center gap-x-4 gap-y-1 text-[11.5px] text-[var(--text-secondary)]">
          {legend.map(([c, l]) => <span key={l} className="flex items-center gap-1.5"><span className="size-2 rounded-[3px]" style={{ background: c }} />{l}</span>)}
        </div>
        <div className="mt-auto grid w-full grid-cols-3 pt-5 text-center">
          {stats.map(([l, v], i) => (
            <div key={l} className={cx(i > 0 && "border-l border-[var(--panel-border)]")}>
              <p className="text-[15px] font-semibold tabular">{v}</p>
              <p className="mt-0.5 text-[11.5px] text-[var(--text-tertiary)]">{l}</p>
            </div>
          ))}
        </div>
      </div>
    </Panel>
  );
}

function SensorRow({ icon, name, sub, value, pct, warn = 101, danger = 101 }) {
  const v = Math.max(0, Math.min(100, pct ?? 0));
  const hot = pct != null && pct >= danger ? "danger" : pct != null && pct >= warn ? "warning" : null;
  return (
    <div>
      <div className="flex items-center gap-3">
        <span className="flex size-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--bg-layer-2)] text-[var(--text-secondary)]"><Icon name={icon} size={18} /></span>
        <div className="min-w-0 flex-1">
          <p className="text-[13px] font-semibold">{name}</p>
          <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{sub}</p>
        </div>
        <span className={cx("text-[15px] font-semibold tabular", hot === "danger" ? "text-[var(--color-danger)]" : hot === "warning" ? "text-[var(--color-warning)]" : "")}>{value}</span>
      </div>
      <div className="mt-2 ml-11 h-1 overflow-hidden rounded-full bg-[var(--seg-off)]">
        <div className="h-full rounded-full transition-[width] duration-500" style={{ width: `${v}%`, background: hot ? `var(--color-${hot})` : "var(--seg-on)" }} />
      </div>
    </div>
  );
}

function SensorsCard({ tune }) {
  const s = useTelemetry();
  const nav = useNavigate();
  const g = getPrimaryGpu(s);
  const asleep = g?.state === "sleep";
  const gpuName = g?.name?.replace(/NVIDIA GeForce |AMD Radeon |Intel\(R\) /, "");
  const noCpuTemp = s && s.cpu?.temp == null;
  return (
    <Panel className="[grid-area:sensors]" title="Temperatura e carga" icon="device_thermostat" subtitle={`Plano de energia: ${tune.plan || "—"}`}>
      <div className="flex h-full flex-col justify-between gap-4">
        <SensorRow icon="memory" name="Processador" sub={noCpuTemp ? "Sem leitura de temperatura" : s?.cpu?.name?.replace(/ @.*/, "")} value={f.temp(s?.cpu?.temp)} pct={s?.cpu?.temp} warn={80} danger={90} />
        <SensorRow icon="developer_board" name="Placa de vídeo" sub={gpuName || "—"} value={asleep ? "repouso" : f.temp(g?.temp)} pct={asleep ? null : g?.temp} warn={80} danger={90} />
        <SensorRow icon="monitoring" name="Carga da CPU" sub={s?.cpu?.threads ? `${s.cpu.threads} threads` : "—"} value={f.pct(s?.cpu?.load)} pct={s?.cpu?.load} />
        <SensorRow icon="videogame_asset" name="Carga da GPU" sub={g ? [g.clock ? f.mhz(g.clock) : null, g.power != null ? f.watts(g.power) : null].filter(Boolean).join(" · ") || "—" : "—"} value={f.pct(g?.load)} pct={g?.load} />
        {noCpuTemp && <Button size="sm" variant="ghost" icon="download" className="self-start" onClick={() => nav("/configuracoes#sensores")}>Instalar o driver de sensores</Button>}
      </div>
    </Panel>
  );
}

/* ============================================================
   Dinamômetro: mede antes e depois com o RAGE! Bench (CPU)
   ============================================================ */

function loadDyno() { try { return JSON.parse(localStorage.getItem("rage.dyno") || "{}"); } catch { return {}; } }

function Dyno({ tune }) {
  const { toast, info } = useApp();
  const a = useAction();
  const [runs, setRuns] = useState(loadDyno);
  const current = tune.active ? tune.stage : "stock";
  const curDef = tune.stages.find((x) => x.id === current) || tune.stages[0];
  const run = async () => {
    try {
      const r = await a.run("bench.run", { tests: ["cpuSingle", "cpuMulti"] });
      if (!r?.scores) return;
      const next = { ...runs, [current]: { single: r.scores.cpuSingle, multi: r.scores.cpuMulti, at: r.at || new Date().toISOString() } };
      setRuns(next);
      try { localStorage.setItem("rage.dyno", JSON.stringify(next)); } catch { /* */ }
      const stock = next.stock;
      const gain = stock && current !== "stock" ? ((r.scores.cpuMulti / stock.multi) - 1) * 100 : null;
      toast(gain != null ? `Dinamômetro: ${gain >= 0 ? "+" : ""}${f.n(gain, 1)}% de potência sobre o original` : "Medição salva", "brand");
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  const measured = tune.stages.filter((st) => runs[st.id]);
  const top = Math.max(1, ...measured.map((st) => runs[st.id].multi));
  const stock = runs.stock;
  const cur = runs[current];
  const gainOf = (id) => (stock && id !== "stock" && runs[id] ? ((runs[id].multi / stock.multi) - 1) * 100 : null);
  const gain = gainOf(current);
  const signed = (v) => `${v >= 0 ? "+" : "−"}${f.n(Math.abs(v), 1)}%`;
  const pct = a.progress?.test === "cpuMulti" ? 50 + (a.progress.pct || 0) / 2 : (a.progress?.pct || 0) / 2;
  const note = a.running ? null
    : measured.length > 0 && !stock ? "Volte ao Original e rode uma vez para ter a base de comparação."
    : !info?.isAdmin ? "O dinamômetro funciona sem administrador; envenenar não."
    : null;

  return (
    <Panel className="[grid-area:dyno]" title="Dinamômetro" icon="monitoring" actions={<Chip>RAGE! Bench · CPU</Chip>}>
      <div className="flex h-full flex-col">
        <div className="flex flex-wrap items-baseline gap-x-2 gap-y-1">
          {a.running || cur ? (
            <>
              <span className="heading text-[36px] leading-none font-semibold tabular">{a.running ? f.n(pct) : f.n(cur.multi)}</span>
              <span className="text-[14px] font-semibold text-[var(--text-secondary)]">{a.running ? "%" : "pts"}</span>
            </>
          ) : <span className="heading text-[22px] leading-[36px] font-semibold">Sem medição</span>}
          {gain != null && !a.running && (
            <span className={cx("text-[12px] font-medium", gain >= 0 ? "text-[var(--color-success)]" : "text-[var(--color-danger)]")}>{signed(gain)} sobre o original</span>
          )}
        </div>
        <p className="mt-1.5 text-[12px] text-[var(--text-tertiary)]">
          {a.running ? `${a.progress?.label || "Aquecendo"} · medindo no ${curDef.name}`
            : cur ? `${curDef.name} com todos os núcleos · 1 núcleo: ${f.n(cur.single)} pts · ${f.rel(cur.at)}`
            : `Rode uma vez no ${curDef.name} para ter a nota desta marcha. Leva uns 20 segundos.`}
        </p>

        <div className="mt-4 grid min-h-[124px] flex-1 grid-cols-4 gap-2.5">
          {tune.stages.map((st) => {
            const r = runs[st.id], here = st.id === current, g = gainOf(st.id);
            const hgt = r ? Math.max(10, (r.multi / top) * 76) : 0;
            return (
              <div key={st.id} className="flex min-w-0 flex-col items-center gap-1.5">
                <div className="dyno-track" title={r ? `${st.name}: ${f.n(r.multi)} pts` : `${st.name}: sem medição`}>
                  {r && <div className="dyno-fill" data-here={here} style={{ height: `${hgt}%` }} />}
                  {r && <span className="dyno-tip" data-here={here} style={{ bottom: `calc(${hgt}% + 6px)` }}>{g != null ? signed(g) : st.id === "stock" ? "base" : f.n(r.multi)}</span>}
                </div>
                <span className={cx("max-w-full truncate text-[11.5px]", here ? "font-semibold text-[var(--text-primary)]" : "font-medium text-[var(--text-tertiary)]")}>{st.name}</span>
              </div>
            );
          })}
        </div>

        {a.running && <Progress value={pct} className="mt-4" height={4} />}
        {a.running
          ? <Button size="lg" className="mt-3 w-full" icon="stop" onClick={a.cancel}>Parar</Button>
          : <Button size="lg" className="mt-4 w-full" icon="speed" onClick={run}>Rodar no {curDef.name}</Button>}
        {note && <p className="mt-2.5 text-[12px] text-[var(--text-tertiary)]">{note}</p>}
      </div>
    </Panel>
  );
}

/* ============================================================
   Ficha técnica das peças
   ============================================================ */

function PartsSheet({ tune, sel }) {
  const selLevel = tune.stages.find((x) => x.id === sel)?.level ?? 0;
  const stageAt = (lvl) => tune.stages.find((x) => x.level === lvl);
  return (
    <Panel padded={false} title="Ficha técnica" icon="build" subtitle="Cada peça é um ajuste real do Windows. O nome é de oficina; o que ela faz está escrito embaixo.">
      <div className="mt-2 grid lg:grid-cols-2">
        {tune.parts.map((p, i) => {
          const preview = !p.installed && p.minStage <= selLevel && sel !== "stock";
          return (
            <div key={p.id} className={cx("flex items-start gap-3 border-t border-[var(--panel-border)] px-4 py-3.5 transition-colors duration-200",
              i % 2 === 0 && "lg:border-r", preview && "bg-[var(--brand-primary-soft)]")}>
              <span className={cx("flex size-9 shrink-0 items-center justify-center rounded-[10px]",
                p.installed ? "brand-bg text-white" : preview ? "bg-[var(--brand-primary-soft)] text-[var(--brand-primary-strong)]" : "bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]")}>
                <Icon name={p.icon} size={19} />
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                  <p className="text-[13.5px] font-semibold">{p.name}</p>
                  <span className="text-[11.5px] text-[var(--text-tertiary)]">a partir do {stageAt(p.minStage)?.name}</span>
                  <span className="flex-1" />
                  {p.installed ? <Chip tone="success" dot>Instalada</Chip> : preview ? <Chip tone="brand">Vai instalar</Chip> : null}
                </div>
                <p className="mt-1 text-[12.5px] leading-snug text-[var(--text-secondary)]">{p.real}</p>
              </div>
            </div>
          );
        })}
      </div>
    </Panel>
  );
}

/* ============================================================
   Sequência de envenenar (tela cheia)
   ============================================================ */

const SEQ_COLOR = zoneColor(0.4);

function RageSequence({ seq, onClose: onCloseProp }) {
  const [t, setT] = useState(0);
  const [leaving, setLeaving] = useState(false);
  const t0 = useRef(performance.now());
  const closeRef = useRef(onCloseProp);
  closeRef.current = onCloseProp;
  const onClose = useCallback(() => closeRef.current(), []);
  const disengage = seq.mode === "disengage";
  useEffect(() => {
    const id = setInterval(() => setT(performance.now() - t0.current), 33);
    return () => clearInterval(id);
  }, []);
  const slamAt = disengage ? 300 : 1150;
  const slam = t >= slamAt;
  const finished = seq.done && t >= (disengage ? 1300 : 2600);
  useEffect(() => {
    if (!finished || leaving) return;
    const hold = setTimeout(() => setLeaving(true), seq.error ? 99999 : 1300);
    return () => clearTimeout(hold);
  }, [finished, leaving, seq.error]);
  useEffect(() => { if (!leaving) return; const k = setTimeout(onClose, 520); return () => clearTimeout(k); }, [leaving, onClose]);
  useEffect(() => {
    const onKey = (e) => e.key === "Escape" && finished && setLeaving(true);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [finished]);

  const rev = disengage ? Math.max(0, 1 - t / 900) : Math.min(1, t / 1050);
  const rpm = disengage ? Math.round(3500 * rev + 900) : Math.round(900 + Math.min(1, t / 1100) ** 1.6 * 8100);
  const parts = seq.events.filter((e) => e.part !== "restore");
  const ok = parts.filter((e) => e.status === "ok").length;
  const stageName = `${seq.stage.name}${seq.stage.id !== "rage" ? ` · ${seq.stage.tag}` : ""}`;

  return createPortal(
    <div className="rage-overlay" data-theme="dark" data-leaving={leaving} onClick={() => finished && setLeaving(true)} role="dialog" aria-modal="true" aria-label={disengage ? "Voltando ao original" : "Envenenando o PC"}>
      {!disengage && <div className="rage-heat" />}
      <div className="rage-flash" data-go={slam && !disengage} />
      <div className="relative flex w-[min(520px,90vw)] flex-col items-center text-center">
        {!slam ? (
          <TickRing value={rev} n={64} size={236} tickLen={15} tickWidth={3.4} colorAt={SEQ_COLOR}>
            <span className="heading text-[44px] leading-none font-semibold tabular">{f.n(rpm)}</span>
            <span className="mt-2 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">rpm</span>
          </TickRing>
        ) : (
          <div className="flex flex-col items-center">
            {disengage ? (
              <>
                <span className="flex size-14 items-center justify-center rounded-full bg-[#ffffff12] text-[var(--text-primary)]"><Icon name="settings_backup_restore" size={28} /></span>
                <p className="heading mt-5 text-[30px] leading-tight font-semibold">{seq.done ? "De volta ao original" : "Voltando ao original"}</p>
              </>
            ) : (
              <div className="rage-slam flex items-center gap-4">
                <LogoMark size={76} color="#ff6a2b" />
                <span className="heading text-[64px] leading-none font-extrabold tracking-tight text-white">RAGE<span className="text-[var(--brand)]">!</span></span>
              </div>
            )}
            <p className={cx("mt-4 text-[15px] font-medium", seq.error ? "text-[var(--color-danger)]" : "text-[var(--brand-primary-strong)]")}>
              {seq.error ? "Não deu para envenenar" : disengage ? "Peças removidas, tudo como era" : finished ? `PC envenenado · ${stageName}` : `Instalando ${seq.stage.name}`}
            </p>
          </div>
        )}

        {slam && !disengage && parts.length > 0 && (
          <div className="mt-6 w-full max-w-[440px] overflow-hidden rounded-[14px] border border-[#ffffff14] bg-[#ffffff08] text-left">
            {parts.map((e) => (
              <div key={e.part} className="part-in flex items-center gap-3 border-t border-[#ffffff0f] px-4 py-2.5 first:border-t-0">
                {e.status === "install" ? <span className="size-4 shrink-0 animate-spin-slow rounded-full border-2 border-[var(--brand)] border-t-transparent" />
                  : <Icon name={e.status === "ok" ? "check" : "close"} size={18} className={e.status === "ok" ? "text-[var(--color-success)]" : "text-[var(--color-danger)]"} />}
                <span className="flex-1 text-[13.5px] font-medium">{e.name}</span>
                {e.message && <span className="truncate text-[11.5px] text-[var(--text-tertiary)]">{e.message}</span>}
              </div>
            ))}
          </div>
        )}
        {slam && !disengage && finished && !seq.error && <p className="mt-3 text-[12px] text-[var(--text-tertiary)]">{ok} de {parts.length} peças instaladas · clique para fechar</p>}
        {seq.error && finished && (
          <div className="mt-5 max-w-md text-[13px] text-[var(--text-secondary)]">
            <p>{seq.error}</p>
            <Button size="lg" className="mt-4" onClick={() => setLeaving(true)}>Fechar</Button>
          </div>
        )}
      </div>
    </div>,
    document.body,
  );
}

/* ============================================================
   Ajuste fino (avançado): os controles manuais de antes
   ============================================================ */

const BOOST = [{ value: 0, label: "Desligado" }, { value: 1, label: "Ligado" }, { value: 2, label: "Agressivo" }, { value: 3, label: "Eficiente" }, { value: 4, label: "Efic. agressivo" }];

function Slider({ label, value, onChange, min = 0, max = 100, suffix = "%" }) {
  return (
    <div>
      <div className="mb-1.5 flex justify-between text-[12.5px]"><span className="text-[var(--text-secondary)]">{label}</span><span className="font-semibold tabular">{value}{suffix}</span></div>
      <input type="range" className="range" min={min} max={max} value={value} onChange={(e) => onChange(+e.target.value)} aria-label={label} />
    </div>
  );
}

function PowerPanel() {
  const { toast, info } = useApp();
  const { data, setData, error, reload } = useRpc("power.plans");
  const [busy, setBusy] = useState(null);
  const setPlan = async (guid) => { setBusy(guid); try { setData(await call("power.setPlan", { guid })); toast("Plano de energia ativado", "success"); } catch (e) { toast(e.message, "danger"); } setBusy(null); };
  const setMode = async (mode) => { setBusy(mode); try { setData(await call("power.setOverlay", { mode })); } catch (e) { toast(e.message, "danger"); } setBusy(null); };
  const overlayVal = data?.overlay?.startsWith("ded574b5") ? "performance" : data?.overlay?.startsWith("961cc777") ? "efficiency" : "balanced";
  return (
    <Panel title="Plano de energia" icon="bolt">
      <ErrorBox error={error} onRetry={reload} />
      {!data ? <Skeleton className="h-40" /> : (
        <>
          <div className="space-y-1.5">
            {data.plans.map((p) => (
              <button key={p.guid} onClick={() => !p.active && setPlan(p.guid)} disabled={!info?.isAdmin} className={cx("flex w-full items-center gap-3 rounded-[var(--radius-md)] border px-3 py-2.5 text-left transition-colors disabled:opacity-60",
                p.active ? "border-[var(--brand)] bg-[var(--brand-primary-soft)]" : "border-[var(--border)] hover:border-[var(--border-strong)]")}>
                <span className={cx("flex size-4 items-center justify-center rounded-full border-2", p.active ? "border-[var(--brand)]" : "border-[var(--border-strong)]")}>{p.active && <span className="size-1.5 rounded-full bg-[var(--brand)]" />}</span>
                <span className="flex-1 text-[13px] font-semibold">{p.name}</span>
                {busy === p.guid && <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" />}
              </button>
            ))}
          </div>
          <div className="mt-4 border-t border-[var(--border)] pt-4">
            <p className="mb-2 text-[12.5px] font-semibold">Modo de energia do Windows</p>
            <Segmented value={overlayVal} onChange={setMode} options={[{ value: "efficiency", label: "Eficiência" }, { value: "balanced", label: "Equilibrado" }, { value: "performance", label: "Desempenho" }]} />
          </div>
        </>
      )}
    </Panel>
  );
}

function CpuPanel() {
  const { toast, info } = useApp();
  const { data, setData } = useRpc("power.plans");
  const [v, setV] = useState(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { if (data?.cpu) setV({ minAc: data.cpu.minAc ?? 5, maxAc: data.cpu.maxAc ?? 100, boostAc: data.cpu.boostAc ?? 2, minDc: data.cpu.minDc ?? 5, maxDc: data.cpu.maxDc ?? 100, boostDc: data.cpu.boostDc ?? 1 }); }, [data]);
  const apply = async () => { setBusy(true); try { setData(await call("power.setCpu", v)); toast("Ajuste do processador aplicado", "success"); } catch (e) { toast(e.message, "danger"); } setBusy(null); };
  return (
    <Panel title="Processador no plano ativo" icon="memory" subtitle="Dica: estado máximo em 99% desliga o Turbo Boost e deixa o notebook bem mais frio.">
      {!v ? <Skeleton className="h-48" /> : (
        <>
          <div className="grid gap-5 md:grid-cols-2">
            {[["Na tomada", "Ac", "power"], ["Na bateria", "Dc", "battery_5_bar"]].map(([t, k, icon]) => (
              <div key={k} className="space-y-4">
                <p className="flex items-center gap-1.5 text-[12.5px] font-semibold"><Icon name={icon} size={17} className="text-[var(--text-tertiary)]" />{t}</p>
                <Slider label="Estado mínimo" value={v["min" + k]} onChange={(x) => setV({ ...v, ["min" + k]: x })} />
                <Slider label="Estado máximo" value={v["max" + k]} onChange={(x) => setV({ ...v, ["max" + k]: x })} />
                <select className="input" value={v["boost" + k]} onChange={(e) => setV({ ...v, ["boost" + k]: +e.target.value })} aria-label={`Turbo Boost ${t}`}>
                  {BOOST.map((b) => <option key={b.value} value={b.value}>Turbo Boost: {b.label}</option>)}
                </select>
              </div>
            ))}
          </div>
          <Button variant="brand" className="mt-4" icon="check" onClick={apply} loading={busy} disabled={!info?.isAdmin}>Aplicar ao plano ativo</Button>
        </>
      )}
    </Panel>
  );
}

function GpuPanel() {
  const s = useTelemetry();
  const nav = useNavigate();
  return (
    <Panel title="Placa de vídeo" icon="developer_board">
      {(s?.gpus || []).map((g) => (
        <div key={g.id} className="mb-3 rounded-[var(--radius-md)] border border-[var(--border)] p-3 last:mb-0">
          <div className="flex items-center gap-2">
            <p className="flex-1 truncate text-[13px] font-semibold">{g.name}</p>
            <Chip tone={g.state === "sleep" ? "neutral" : "success"} dot>{g.state === "sleep" ? "em repouso" : g.kind === "discrete" ? "dedicada" : "integrada"}</Chip>
          </div>
          <div className="mt-2 grid grid-cols-2 gap-x-6">
            <KV label="Temperatura" value={g.state === "sleep" ? "—" : f.temp(g.temp)} />
            <KV label="Clock" value={f.mhz(g.clock)} />
            <KV label="Consumo" value={f.watts(g.power)} />
            <KV label="VRAM" value={g.vramTotal ? `${f.gb(g.vramUsed)} / ${f.gb(g.vramTotal, 0)}` : f.gb(g.sharedUsed)} />
          </div>
        </div>
      ))}
      <div className="mt-3 grid gap-2">
        <Button icon="stadia_controller" onClick={() => nav("/jogos")}>Usar a GPU dedicada nos jogos</Button>
        <Button variant="ghost" icon="open_in_new" onClick={() => call("app.open", { target: "ms-settings:display-advancedgraphics" })}>Gráficos do Windows</Button>
      </div>
      <p className="mt-3 text-[11.5px] text-[var(--text-tertiary)]">Clock e curva de tensão da placa de vídeo: MSI Afterburner ou o app do fabricante.</p>
    </Panel>
  );
}

function GamingTweaks() {
  const { toast } = useApp();
  const { data, setData, error } = useRpc("tweaks.list", { category: "gaming" });
  const [busy, setBusy] = useState(null);
  const set = async (t, on) => {
    setBusy(t.id);
    try {
      const r = await call(on ? "tweaks.apply" : "tweaks.revert", { ids: [t.id] });
      const res = r?.results?.[0];
      if (res && !res.ok) toast(res.message || "Não foi possível aplicar", "danger");
      setData((r?.items || []).filter((x) => x.category === "gaming"));
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  return (
    <Panel title="Ajustes do Windows para jogos" icon="sports_esports" bodyClass="!px-2">
      <ErrorBox error={error} />
      {!data ? <Skeleton className="m-2 h-48" /> : data.map((t) => (
        <div key={t.id} className="flex items-center gap-3 rounded-[var(--radius-sm)] px-2 py-2.5 hover:bg-[var(--bg-subtle)]">
          <div className="min-w-0 flex-1">
            <p className="flex flex-wrap items-center gap-1.5 text-[13px] font-semibold">{t.title}{t.needsReboot && <Chip>Reiniciar</Chip>}</p>
            <p className="text-[12px] text-[var(--text-tertiary)]">{t.desc}</p>
          </div>
          {busy === t.id ? <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" /> : <Toggle checked={t.applied} onChange={(v) => set(t, v)} label={t.title} />}
        </div>
      ))}
    </Panel>
  );
}

function FineTuning() {
  const [open, setOpen] = useState(() => { try { return localStorage.getItem("oc.fine") === "1"; } catch { return false; } });
  const toggle = () => setOpen((o) => { try { localStorage.setItem("oc.fine", o ? "0" : "1"); } catch { /* */ } return !o; });
  return (
    <section>
      <button onClick={toggle} aria-expanded={open} className="flex w-full items-center gap-3 rounded-[var(--radius-lg)] border border-[var(--border)] bg-[var(--bg-surface)] px-5 py-4 text-left hover:border-[var(--border-default)]">
        <Icon name="build" size={20} className="text-[var(--text-tertiary)]" />
        <div className="flex-1">
          <p className="text-[14px] font-semibold">Ajuste fino na mão</p>
          <p className="text-[12px] text-[var(--text-tertiary)]">Plano de energia, limites do processador, placa de vídeo e ajustes de jogos — um por um.</p>
        </div>
        <Icon name="expand_more" size={22} className={cx("text-[var(--text-tertiary)] transition-transform", open && "rotate-180")} />
      </button>
      {open && (
        <div className="animate-fade mt-4 grid gap-4">
          <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)]"><PowerPanel /><CpuPanel /></div>
          <div className="grid gap-4 xl:grid-cols-2"><GpuPanel /><GamingTweaks /></div>
        </div>
      )}
    </section>
  );
}

/* ============================================================ */

export default function Overclock() {
  const { toast } = useApp();
  const { data: tune, setData: setTune, error, reload } = useRpc("tune.status");
  const [sel, setSel] = useState(null);
  const [seq, setSeq] = useState(null);
  useEffect(() => {
    if (!tune || sel) return;
    setSel(tune.active ? tune.stage : "track");
  }, [tune, sel]);

  const onProgress = (p) => {
    if (!p?.part) return;
    setSeq((q) => (q ? { ...q, events: [...q.events.filter((e) => e.part !== p.part), p] } : q));
  };

  const engage = async (stage) => {
    const def = tune.stages.find((x) => x.id === stage);
    setSeq({ mode: "engage", stage: def, events: [], done: false, error: null });
    rageRev();
    setTimeout(() => {
      const el = document.querySelector("main");
      if (!el) return;
      el.classList.remove("shake"); void el.offsetWidth; el.classList.add("shake");
      setTimeout(() => el.classList.remove("shake"), 560);
    }, 1150);
    try {
      const r = await call("tune.engage", { stage }, { onProgress });
      setTune(r);
      setSeq((q) => ({ ...q, done: true }));
    } catch (e) {
      setSeq((q) => ({ ...q, done: true, error: e.message }));
    }
  };

  const disengage = async () => {
    setSeq({ mode: "disengage", stage: tune.stages[0], events: [], done: false, error: null });
    coolDown();
    try {
      const r = await call("tune.disengage", {}, { onProgress });
      setTune(r);
      setSel("track");
      setSeq((q) => ({ ...q, done: true }));
    } catch (e) {
      setSeq(null);
      toast(e.message, "danger");
    }
  };

  if (error) return <ErrorBox error={error} onRetry={reload} />;
  if (!tune || !sel) {
    return (
      <div className="oc-grid">
        <Skeleton className="h-[340px] !rounded-[var(--radius-xl)] [grid-area:gear]" />
        <Skeleton className="h-[340px] !rounded-[var(--radius-xl)] [grid-area:action]" />
        <Skeleton className="h-[360px] !rounded-[var(--radius-xl)] [grid-area:clock]" />
        <Skeleton className="h-[360px] !rounded-[var(--radius-xl)] [grid-area:sensors]" />
        <Skeleton className="h-[360px] !rounded-[var(--radius-xl)] [grid-area:dyno]" />
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="oc-grid">
        <GearCard tune={tune} sel={sel} setSel={setSel} />
        <ActionCard tune={tune} sel={sel} onEngage={engage} onDisengage={disengage} />
        <ClockCard />
        <SensorsCard tune={tune} />
        <Dyno tune={tune} />
      </div>
      <PartsSheet tune={tune} sel={sel} />
      <FineTuning />
      {seq && <RageSequence seq={seq} onClose={() => setSeq(null)} />}
    </div>
  );
}
