import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useRpc, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Checkbox, Chip, Icon, Panel, Skeleton, Toggle, cx, ErrorBox, InfoTip } from "../components/ui.jsx";
import { DialButton } from "../components/Charts.jsx";

const MODES = [
  { id: "game", icon: "sports_esports", title: "Jogo", desc: "Máximo desempenho: plano RAGE! Turbo, serviços pausados, RAM livre e o jogo em prioridade alta." },
  { id: "work", icon: "work", title: "Trabalho", desc: "Alto desempenho para trabalhar: encerra o que não precisa e libera memória." },
  { id: "economy", icon: "eco", title: "Economia", desc: "Menor consumo de energia para a bateria durar mais, sem travar o PC." },
];

function useElapsed(since) {
  const [, tick] = useState(0);
  useEffect(() => { if (!since) return; const t = setInterval(() => tick((x) => x + 1), 1000); return () => clearInterval(t); }, [since]);
  return since ? (Date.now() - new Date(since).getTime()) / 1000 : 0;
}

function Hero({ config }) {
  const { boost, setBoost, settings, updateSettings, toast, info } = useApp();
  const [busy, setBusy] = useState(false);
  const mode = settings?.boostMode || "game";
  const active = boost?.active;
  const elapsed = useElapsed(active ? boost.since : null);
  const toggle = async () => {
    setBusy(true);
    try {
      const r = active ? await call("boost.stop") : await call("boost.start", { mode });
      setBoost(r);
      toast(active ? "Modo Turbo desligado — tudo foi restaurado" : `Modo Turbo ${MODES.find((m) => m.id === mode).title} ligado`, active ? "neutral" : "brand");
    } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  return (
    <section className="panel relative overflow-hidden">
      {active && <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(600px_circle_at_85%_30%,#ff5a1f26,transparent_60%)]" />}
      <div className="relative grid gap-6 p-6 lg:grid-cols-[1fr_300px]">
        <div>
          <p className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Escolha o modo</p>
          <div className="mt-3 grid gap-3 md:grid-cols-3">
            {MODES.map((m) => {
              const sel = mode === m.id;
              return (
                <button
                  key={m.id}
                  disabled={active || busy}
                  onClick={() => updateSettings({ boostMode: m.id })}
                  className={cx("group rounded-[var(--radius-lg)] border p-4 text-left transition-all disabled:cursor-not-allowed",
                    sel ? "border-[var(--brand)] bg-[var(--brand-primary-soft)]" : "border-[var(--border)] hover:border-[var(--border-strong)]", active && !sel && "opacity-40")}
                >
                  <div className="flex items-center gap-2.5">
                    <span className={cx("flex size-9 items-center justify-center rounded-[var(--radius-md)]", sel ? "brand-bg text-white" : "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}><Icon name={m.icon} size={20} /></span>
                    <span className="heading text-[15px] font-semibold">{m.title}</span>
                    {sel && <Icon name="check_circle" size={18} fill className="ml-auto text-[var(--brand)]" />}
                  </div>
                  <p className="mt-2.5 text-[12.5px] leading-snug text-[var(--text-secondary)]">{m.desc}</p>
                </button>
              );
            })}
          </div>
          <div className="mt-4 flex flex-wrap items-center gap-x-6 gap-y-2 text-[13px]">
            <label className="flex items-center gap-2.5">
              <Toggle checked={settings?.autoBoost} onChange={(v) => updateSettings({ autoBoost: v })} />
              <span>Ligar sozinho quando eu abrir um jogo</span>
            </label>
            <label className="flex items-center gap-2.5">
              <Toggle checked={settings?.autoDetectGames} onChange={(v) => updateSettings({ autoDetectGames: v })} />
              <span>Detectar jogos fora da biblioteca</span>
              <InfoTip text="Executáveis em tela cheia dentro de pastas de jogos (Steam, Epic, Xbox, Riot…) também contam como jogo." />
            </label>
          </div>
          {!info?.isAdmin && <p className="mt-3 text-[12px] text-[var(--color-warning)]">Sem administrador o Turbo só consegue liberar RAM e fechar apps. Abra o RAGE! como administrador para o efeito completo.</p>}
        </div>

        <div className="flex flex-col items-center justify-center gap-3 border-t border-[var(--border)] pt-6 lg:border-t-0 lg:border-l lg:pt-0 lg:pl-6">
          <DialButton
            size={184}
            icon="power_settings_new"
            label={busy ? (active ? "Desligando" : "Aplicando") : active ? "Ligado" : "Ligar"}
            state={busy ? "busy" : active ? "on" : "idle"}
            onClick={toggle}
            disabled={busy}
            aria-pressed={!!active}
            title={active ? "Desligar o Modo Turbo" : "Ligar o Modo Turbo"}
          />
          {active ? (
            <div className="text-center">
              <p className="text-[13px] font-semibold">{MODES.find((m) => m.id === boost.mode)?.title} · há {f.duration(elapsed)}</p>
              <p className="text-[12px] text-[var(--text-tertiary)]">{boost.auto ? "Ligado automaticamente" : "Ligado manualmente"}{boost.game ? ` · ${boost.game.name}` : ""}</p>
            </div>
          ) : (
            <p className="max-w-[240px] text-center text-[12px] text-[var(--text-tertiary)]">Tudo o que o Turbo mudar volta ao normal quando você desligar.</p>
          )}
        </div>
      </div>
      {active && boost.actions?.length > 0 && (
        <div className="relative flex flex-wrap gap-2 border-t border-[var(--border)] px-6 py-3.5">
          {boost.actions.map((a, i) => <Chip key={i} tone="brand" icon="check">{a}</Chip>)}
        </div>
      )}
    </section>
  );
}

function Options() {
  const { settings, updateSettings } = useApp();
  if (!settings) return <Skeleton className="h-64" />;
  const opts = [
    ["boostCleanRam", "memory", "Liberar memória RAM", "Esvazia a memória de apps em segundo plano e o cache em espera."],
    ["boostPowerPlan", "bolt", "Plano de energia de alto desempenho", "Ativa o plano RAGE! Turbo (CPU no máximo, sem estacionar núcleos)."],
    ["boostGamePriority", "trending_up", "Prioridade alta para o jogo", "O jogo ganha prioridade de CPU e não sofre limitação de energia."],
    ["boostStopServices", "pause_circle", "Pausar serviços desnecessários", "Os serviços marcados ao lado são pausados e religados no final."],
    ["boostCloseApps", "close_fullscreen", "Fechar apps em segundo plano", "Os apps marcados são fechados e reabertos quando o Turbo desliga."],
    ["boostFlushDns", "dns", "Limpar cache de DNS", "Útil se o jogo estiver com problema para conectar."],
    ["boostNotify", "notifications", "Avisar quando ligar sozinho", "Mostra uma notificação ao ativar no início de um jogo."],
  ];
  return (
    <Panel title="O que o Turbo faz" icon="checklist" subtitle="Vale para os modos Jogo e Trabalho">
      <div className="divide-y divide-[var(--border)]">
        {opts.map(([k, icon, title, desc]) => (
          <label key={k} className="flex cursor-pointer items-center gap-3 py-2.5">
            <Icon name={icon} size={19} className="text-[var(--text-tertiary)]" />
            <div className="min-w-0 flex-1">
              <p className="text-[13px] font-semibold">{title}</p>
              <p className="text-[12px] text-[var(--text-tertiary)]">{desc}</p>
            </div>
            <Toggle checked={settings[k]} onChange={(v) => updateSettings({ [k]: v })} />
          </label>
        ))}
      </div>
    </Panel>
  );
}

function ServicesList({ cfg, setCfg }) {
  const { toast } = useApp();
  const selected = cfg.services.filter((s) => s.selected).map((s) => s.name);
  const set = async (name, on) => {
    const next = on ? [...selected, name] : selected.filter((n) => n !== name);
    try { setCfg(await call("boost.setServices", { services: next })); } catch (e) { toast(e.message, "danger"); }
  };
  const reset = async () => { try { setCfg(await call("boost.setServices", { reset: true })); toast("Lista recomendada restaurada"); } catch (e) { toast(e.message, "danger"); } };
  return (
    <Panel title="Serviços pausados no Turbo" icon="settings_applications" subtitle={`${selected.length} de ${cfg.services.length} marcados · religados ao desligar`} actions={<Button size="sm" variant="ghost" onClick={reset}>Recomendados</Button>} bodyClass="!px-2">
      <div className="scroll-area max-h-[360px] overflow-y-auto">
        {cfg.services.map((s) => (
          <label key={s.name} className="flex cursor-pointer items-center gap-3 rounded-[var(--radius-sm)] px-2 py-2 hover:bg-[var(--bg-subtle)]">
            <Checkbox checked={s.selected} onChange={(v) => set(s.name, v)} />
            <div className="min-w-0 flex-1">
              <p className="flex items-center gap-2 text-[13px] font-semibold">{s.title}{s.recommended && <Chip tone="brand">Recomendado</Chip>}</p>
              <p className="truncate text-[12px] text-[var(--text-tertiary)]">{s.desc}</p>
            </div>
            <Chip tone={s.status === "rodando" ? "success" : "neutral"} dot>{s.status}</Chip>
          </label>
        ))}
      </div>
    </Panel>
  );
}

function AppsList({ cfg, setCfg }) {
  const { toast } = useApp();
  const selected = cfg.apps.filter((a) => a.selected).map((a) => a.name);
  const set = async (name, on) => {
    const next = on ? [...selected, name] : selected.filter((n) => n !== name);
    try { setCfg(await call("boost.setApps", { apps: next })); } catch (e) { toast(e.message, "danger"); }
  };
  return (
    <Panel title="Apps fechados no Turbo" icon="apps" subtitle="Marque o que pode ser fechado durante o jogo" bodyClass="!px-2">
      {cfg.apps.length === 0 ? <p className="px-2 py-6 text-center text-[13px] text-[var(--text-tertiary)]">Nenhum app em segundo plano agora.</p> : (
        <div className="scroll-area max-h-[360px] overflow-y-auto">
          {cfg.apps.map((a) => (
            <label key={a.name} className="flex cursor-pointer items-center gap-3 rounded-[var(--radius-sm)] px-2 py-2 hover:bg-[var(--bg-subtle)]">
              <Checkbox checked={a.selected} onChange={(v) => set(a.name, v)} />
              <div className="min-w-0 flex-1">
                <p className="flex items-center gap-2 truncate text-[13px] font-semibold">{a.title}{a.recommended && <Chip tone="brand">Recomendado</Chip>}</p>
                <p className="truncate text-[12px] text-[var(--text-tertiary)]">{a.name}.exe{a.processes ? ` · ${a.processes} ${a.processes === 1 ? "processo" : "processos"}` : " · não está aberto"}</p>
              </div>
              {a.memory > 0 && <span className="text-[12px] font-semibold tabular text-[var(--text-secondary)]">{f.bytes(a.memory)}</span>}
            </label>
          ))}
        </div>
      )}
    </Panel>
  );
}

function RamCard() {
  const s = useTelemetry();
  const { toast } = useApp();
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState(null);
  const { data: top, reload } = useRpc("ram.top", { limit: 6 });
  const clean = async () => {
    setBusy(true);
    try { const r = await call("ram.clean"); setLast(r); toast(`${r.freedText || f.bytes(r.freed)} de RAM liberados`, "success"); reload(true); } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  return (
    <Panel title="Memória RAM" icon="memory_alt" subtitle={`${f.gb(s?.ram?.used)} de ${f.gb(s?.ram?.total, 0)} em uso`}>
      <div className="flex items-center gap-4">
        <div className="flex-1">
          <div className="h-2.5 overflow-hidden rounded-full bg-[var(--bg-layer-2)]"><div className="brand-bg h-full rounded-full transition-[width] duration-700" style={{ width: `${s?.ram?.pct || 0}%` }} /></div>
          <div className="mt-1.5 flex justify-between text-[11.5px] text-[var(--text-tertiary)]"><span>{f.pct(s?.ram?.pct)} em uso</span><span>em espera {f.gb(s?.ram?.standby)}</span></div>
        </div>
        <Button variant="brand" icon="mop" onClick={clean} loading={busy}>Liberar agora</Button>
      </div>
      {last && <p className="mt-3 rounded-[var(--radius-sm)] bg-[var(--color-success-soft)] px-3 py-2 text-[12.5px] text-[var(--color-success)]">Liberados {last.freedText || f.bytes(last.freed)} · {last.trimmed} processos otimizados{last.standbyPurged ? " · cache em espera limpo" : ""}</p>}
      <p className="mt-4 mb-1 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Quem mais usa memória</p>
      {(top || []).map((p) => (
        <div key={p.pid} className="flex items-center justify-between py-1 text-[12.5px]"><span className="truncate">{p.name}</span><span className="font-semibold tabular text-[var(--text-secondary)]">{f.bytes(p.ws)}</span></div>
      ))}
    </Panel>
  );
}

export default function Turbo() {
  const { data: cfg, setData: setCfg, error, reload } = useRpc("boost.config");
  const nav = useNavigate();
  return (
    <div className="space-y-4">
      <Hero config={cfg} />
      <ErrorBox error={error} onRetry={reload} />
      <div className="grid gap-4 xl:grid-cols-3">
        <Options />
        {cfg ? <ServicesList cfg={cfg} setCfg={setCfg} /> : <Skeleton className="h-80" />}
        {cfg ? <AppsList cfg={cfg} setCfg={setCfg} /> : <Skeleton className="h-80" />}
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <RamCard />
        <Panel title="Mais desempenho" icon="bolt">
          <div className="grid gap-2">
            {[["stadia_controller", "Biblioteca de jogos", "Turbo automático por jogo, GPU dedicada e overlay de FPS", "/jogos"], ["bolt", "Overclock & energia", "Boost do processador, plano de energia e modo de energia", "/overclock"], ["tune", "Ajustes para jogos", "Game DVR, agendamento de GPU, aceleração do mouse…", "/ajustes"], ["rocket", "Inicialização", "Menos programas abrindo com o Windows", "/inicializacao"]].map(([icon, t, d, to]) => (
              <button key={to} onClick={() => nav(to)} className="flex items-center gap-3 rounded-[var(--radius-md)] border border-[var(--border)] px-3 py-2.5 text-left transition-colors hover:border-[var(--border-strong)] hover:bg-[var(--bg-subtle)]">
                <Icon name={icon} size={20} className="text-[var(--brand)]" />
                <div className="min-w-0 flex-1"><p className="text-[13px] font-semibold">{t}</p><p className="truncate text-[12px] text-[var(--text-tertiary)]">{d}</p></div>
                <Icon name="chevron_right" size={18} className="text-[var(--text-muted)]" />
              </button>
            ))}
          </div>
        </Panel>
      </div>
    </div>
  );
}
