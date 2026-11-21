import { useCallback, useEffect, useRef, useState } from "react";
import { call, on } from "../lib/rage.js";
import { getPrimaryGpu, startTelemetry, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { setFahrenheit } from "../lib/format.js";
import { TickRing, zoneColor } from "../components/Charts.jsx";
import { Icon, Skeleton, cx } from "../components/ui.jsx";
import { Logo } from "../components/Shell.jsx";

// Painel do mini monitor: abre numa janela própria, presa à pílula flutuante (rota #/mini).

const ZONE = { ram: zoneColor(0.75, 0.9), cpu: zoneColor(0.7, 0.92), gpu: zoneColor(0.7, 0.92), disk: zoneColor(0.8, 0.93) };
const TABS = [
  { id: "ram", label: "RAM", by: "mem", title: "Quem mais usa memória" },
  { id: "cpu", label: "CPU", by: "cpu", title: "Quem mais usa o processador" },
  { id: "gpu", label: "GPU", by: "gpu", title: "Quem mais usa a placa de vídeo" },
  { id: "disk", label: "Disco", by: null, title: "Unidades" },
];
const rowKey = (r) => `${r.exe}|${r.name}`;

function AppIcon({ row }) {
  if (row.icon) return <img src={row.icon} alt="" className="size-5 object-contain" draggable={false} />;
  return <span className="flex size-5 items-center justify-center rounded-[5px] bg-[var(--bg-layer-3)] text-[10px] font-bold text-[var(--text-secondary)]">{(row.name || "?")[0].toUpperCase()}</span>;
}

function Check({ on: checked, disabled }) {
  return (
    <span className={cx("flex size-[18px] items-center justify-center rounded-[5px] border transition-colors",
      disabled ? "border-[var(--border)] opacity-40" : checked ? "border-transparent bg-[#ffb054] text-[#1d1006]" : "border-[var(--border-strong)]")}>
      {checked && <Icon name="check" size={14} />}
    </span>
  );
}

function ProcessList({ tab, rows, selected, toggle }) {
  if (!rows) return <div className="grid gap-1.5 px-2 pt-1">{Array.from({ length: 6 }, (_, i) => <Skeleton key={i} className="h-7" />)}</div>;
  if (!rows.length) return <p className="px-3 pt-6 text-center text-[12.5px] text-[var(--text-tertiary)]">Nenhum app usando {tab === "gpu" ? "a placa de vídeo" : "isso"} agora.</p>;
  const value = (r) => (tab === "ram" ? f.bytes(r.mem) : tab === "cpu" ? f.pct(r.cpu, 1) : f.pct(r.gpu, 0));
  return rows.map((r) => {
    const k = rowKey(r);
    return (
      <button key={k} type="button" className="mini-row" role="checkbox" aria-checked={selected.has(k)} disabled={r.self} onClick={() => toggle(k)}
        title={r.self ? "O próprio RAGE!" : r.count > 1 ? `${r.name} · ${r.count} processos` : r.name}>
        <Check on={selected.has(k)} disabled={r.self} />
        <AppIcon row={r} />
        <span className="truncate">{r.name}{r.count > 1 && <span className="text-[var(--text-tertiary)]"> · {r.count}</span>}</span>
        <span className="font-semibold tabular text-[var(--text-secondary)]">{value(r)}</span>
      </button>
    );
  });
}

function DiskList({ disks }) {
  if (!disks?.length) return <p className="px-3 pt-6 text-center text-[12.5px] text-[var(--text-tertiary)]">Nenhuma unidade encontrada.</p>;
  return (
    <div className="grid gap-3 px-2 pt-1">
      {disks.map((d) => {
        const used = d.total ? ((d.total - d.free) / d.total) * 100 : 0;
        const tone = used >= 93 ? "var(--color-danger)" : used >= 80 ? "var(--color-warning)" : "var(--seg-on)";
        return (
          <div key={d.letter}>
            <div className="flex items-baseline gap-2 text-[12.5px]">
              <span className="font-semibold">{d.letter}</span>
              <span className="min-w-0 flex-1 truncate text-[var(--text-tertiary)]">{d.label}{d.media ? ` · ${d.media}` : ""}</span>
              <span className="font-semibold tabular">{f.gb(d.free, 0)} livres</span>
            </div>
            <div className="mt-1.5 h-1 overflow-hidden rounded-full bg-[var(--seg-off)]"><div className="h-full rounded-full transition-[width] duration-500" style={{ width: `${used}%`, background: tone }} /></div>
            <div className="mt-1 flex justify-between text-[11px] text-[var(--text-tertiary)] tabular">
              <span>{f.pct(used)} ocupado{d.temp != null ? ` · ${f.temp(d.temp)}` : ""}</span>
              <span>lê {f.rate(d.read)} · grava {f.rate(d.write)}</span>
            </div>
          </div>
        );
      })}
    </div>
  );
}

export default function MiniPanel() {
  const s = useTelemetry();
  const [tab, setTab] = useState("ram");
  const [rows, setRows] = useState(null);
  const [selected, setSelected] = useState(() => new Set());
  const [open, setOpen] = useState(true);
  const [busy, setBusy] = useState(null); // "clean" | "turbo" | "kill"
  const [confirm, setConfirm] = useState(false);
  const [note, setNote] = useState(null);
  const noteTimer = useRef(null);
  const def = TABS.find((t) => t.id === tab);

  const say = useCallback((text, tone = "ok") => {
    setNote({ text, tone });
    clearTimeout(noteTimer.current);
    noteTimer.current = setTimeout(() => setNote(null), 4000);
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = "dark"; // a pílula é sempre escura; o painel acompanha
    document.title = "RAGE! mini monitor";
    startTelemetry();
    call("settings.get").then((st) => setFahrenheit(st?.tempFahrenheit)).catch(() => {});
    const offs = [on("mini", (m) => setOpen(!!m?.open))];
    const onKey = (e) => { if (e.key === "Escape") call("mini.close").catch(() => {}); };
    window.addEventListener("keydown", onKey);
    return () => { offs.forEach((off) => off()); window.removeEventListener("keydown", onKey); clearTimeout(noteTimer.current); };
  }, []);

  // Lista de processos: só consulta com o painel aberto, a cada 2,5 s.
  const load = useCallback(() => {
    if (!def.by) return Promise.resolve();
    return call("proc.top", { by: def.by, limit: 8 }).then(setRows).catch(() => setRows((r) => r || []));
  }, [def.by]);
  useEffect(() => {
    setRows(null); setConfirm(false);
    if (!open || !def.by) return;
    load();
    const t = setInterval(() => { if (document.visibilityState !== "hidden") load(); }, 2500);
    return () => clearInterval(t);
  }, [open, def.by, load]);

  const toggle = (k) => { setConfirm(false); setSelected((cur) => { const n = new Set(cur); n.has(k) ? n.delete(k) : n.add(k); return n; }); };
  const chosen = (rows || []).filter((r) => selected.has(rowKey(r)) && !r.self);

  const clean = async () => {
    setBusy("clean");
    try { const r = await call("ram.clean"); say(`${r.freedText || f.bytes(r.freed)} de RAM liberados`); load(); } catch (e) { say(e.message, "bad"); }
    setBusy(null);
  };
  const turbo = async () => {
    setBusy("turbo");
    try {
      if (s?.boost?.active) { await call("boost.stop"); say("Modo Turbo desligado"); }
      else { const st = await call("settings.get"); await call("boost.start", { mode: st?.boostMode || "game" }); say("Modo Turbo ligado"); }
    } catch (e) { say(e.message, "bad"); }
    setBusy(null);
  };
  const kill = async () => {
    if (!confirm) { setConfirm(true); return; }
    setConfirm(false); setBusy("kill");
    let ok = 0, lastError = null;
    for (const r of chosen) {
      let any = false;
      for (const pid of r.pids || []) {
        try { await call("proc.kill", { pid }); any = true; } catch (e) { if (!/já foi encerrado/.test(e.message)) lastError = e.message; else any = true; }
      }
      if (any) ok++;
    }
    setSelected(new Set());
    await load();
    setBusy(null);
    if (ok) say(`${ok} ${ok === 1 ? "app finalizado" : "apps finalizados"}`); else if (lastError) say(lastError, "bad");
  };

  const g = getPrimaryGpu(s);
  const sys = s?.disks?.find((d) => d.system) || s?.disks?.[0];
  const pct = {
    ram: s?.ram?.pct,
    cpu: s?.cpu?.load,
    gpu: g ? (g.state === "sleep" ? 0 : g.load) : null,
    disk: sys?.total ? ((sys.total - sys.free) / sys.total) * 100 : null,
  };
  const aside = {
    ram: s?.ram ? `${f.gb(s.ram.used)} de ${f.gb(s.ram.total, 0)}` : "",
    cpu: [s?.cpu?.temp != null && f.temp(s.cpu.temp), s?.cpu?.clock && f.mhz(s.cpu.clock)].filter(Boolean).join(" · "),
    gpu: !g ? "" : g.state === "sleep" ? "em repouso" : [g.temp != null && f.temp(g.temp), g.vramTotal ? `${f.gb(g.vramUsed)} de ${f.gb(g.vramTotal, 0)}` : null].filter(Boolean).join(" · "),
    disk: sys?.active != null ? `${f.pct(sys.active)} de atividade` : "",
  }[tab];
  const boostOn = !!s?.boost?.active;

  return (
    <div className="mini">
      <header className="flex h-[46px] shrink-0 items-center gap-2.5 pr-2 pl-3.5">
        <Logo size={22} />
        <h1 className="heading min-w-0 flex-1 truncate text-[13px] font-semibold">Monitor de desempenho</h1>
        <button type="button" title="Abrir o RAGE!" aria-label="Abrir o RAGE!" onClick={() => call("app.show").catch(() => {})}
          className="inline-flex size-8 items-center justify-center rounded-full text-[var(--text-tertiary)] transition-colors hover:bg-[#ffffff12] hover:text-[var(--text-primary)]"><Icon name="open_in_new" size={17} /></button>
        <button type="button" title="Recolher (Esc)" aria-label="Recolher o painel" onClick={() => call("mini.close").catch(() => {})}
          className="inline-flex size-8 items-center justify-center rounded-full text-[var(--text-tertiary)] transition-colors hover:bg-[#ffffff12] hover:text-[var(--text-primary)]"><Icon name="close" size={18} /></button>
      </header>

      <div role="tablist" aria-label="Componente" className="flex shrink-0 gap-1 px-2.5">
        {TABS.map((t) => (
          <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} className="mini-tab" onClick={() => setTab(t.id)}>
            <TickRing value={(pct[t.id] || 0) / 100} size={58} n={22} tickLen={6} tickWidth={2.4} colorAt={ZONE[t.id]}>
              <span className="text-[12.5px] leading-none font-semibold tracking-normal tabular text-[var(--text-primary)] normal-case">{pct[t.id] == null ? "—" : f.pct(pct[t.id])}</span>
            </TickRing>
            {t.label}
          </button>
        ))}
      </div>

      <div className="mt-2.5 flex shrink-0 items-baseline gap-3 px-4 pb-1.5">
        <p className="text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">{def.title}</p>
        <p className="min-w-0 flex-1 truncate text-right text-[11.5px] text-[var(--text-tertiary)] tabular">{aside}</p>
      </div>

      <div className="scroll-area min-h-0 flex-1 overflow-y-auto px-2 pb-1">
        {tab === "disk" ? <DiskList disks={s?.disks} /> : <ProcessList tab={tab} rows={rows} selected={selected} toggle={toggle} />}
      </div>

      <footer className="flex h-[54px] shrink-0 items-center gap-2 border-t border-[var(--panel-border)] px-3">
        <button type="button" className="mini-foot-btn" title="Limpar memória RAM" aria-label="Limpar memória RAM" onClick={clean} disabled={!!busy}>
          <Icon name={busy === "clean" ? "progress_activity" : "mop"} size={18} className={busy === "clean" ? "animate-spin-slow" : ""} />
        </button>
        <button type="button" className="mini-foot-btn" data-on={boostOn} title={boostOn ? "Desligar o Modo Turbo" : "Ligar o Modo Turbo"} aria-label="Modo Turbo" aria-pressed={boostOn} onClick={turbo} disabled={!!busy}>
          <Icon name={busy === "turbo" ? "progress_activity" : "rocket_launch"} size={18} fill={boostOn} className={busy === "turbo" ? "animate-spin-slow" : ""} />
        </button>
        <p className={cx("min-w-0 flex-1 truncate text-[11.5px]", note?.tone === "bad" ? "text-[var(--color-warning)]" : "text-[var(--text-secondary)]")} role="status">{note?.text}</p>
        {tab !== "disk" && (
          <button type="button" onClick={kill} disabled={!chosen.length || !!busy}
            className={cx("inline-flex h-[34px] shrink-0 items-center gap-1.5 rounded-full px-3.5 text-[12.5px] font-semibold transition-colors disabled:opacity-40",
              confirm ? "bg-[var(--color-danger)] text-white" : "bg-[var(--text-primary)] text-[#111] hover:bg-white")}>
            {busy === "kill" && <Icon name="progress_activity" size={15} className="animate-spin-slow" />}
            {confirm ? `Confirmar (${chosen.length})` : chosen.length ? `Finalizar (${chosen.length})` : "Finalizar tarefa"}
          </button>
        )}
      </footer>
    </div>
  );
}
