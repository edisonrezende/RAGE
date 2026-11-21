import { useEffect, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useApp } from "../App.jsx";
import { useAction, useRpc, useTelemetry, getPrimaryGpu } from "../lib/store.js";
import * as f from "../lib/format.js";
import { TickRing, zoneColor } from "../components/Charts.jsx";
import { Chip, Empty, Icon, Panel, Progress, Skeleton, cx } from "../components/ui.jsx";

const TESTS = [
  { id: "cpuSingle", label: "CPU · 1 núcleo", icon: "memory", unit: "pts", desc: "Velocidade de um núcleo (jogos dependem muito disso)" },
  { id: "cpuMulti", label: "CPU · todos os núcleos", icon: "grid_view", unit: "pts", desc: "Renderização, compilação, streaming" },
  { id: "memory", label: "Memória", icon: "memory_alt", unit: "GB/s", desc: "Largura de banda da RAM" },
  { id: "disk", label: "Disco", icon: "hard_drive", unit: "MB/s", desc: "Leitura e escrita sequencial" },
];

// A nota não tem zona de perigo: o anel inteiro vai do âmbar ao laranja.
const SCORE_COLOR = zoneColor(0, 1.01);

export default function Benchmark() {
  const { toast, boost } = useApp();
  const s = useTelemetry();
  const g = getPrimaryGpu(s);
  const { data: history, setData: setHistory, loading } = useRpc("bench.history");
  const { run, cancel, running, progress } = useAction();
  const [result, setResult] = useState(null);
  const [params, setParams] = useSearchParams();
  const started = useRef(false);

  const start = async () => {
    setResult(null);
    try {
      const r = await run("bench.run", { tests: TESTS.map((t) => t.id) });
      if (r) { setResult(r); setHistory((h) => [r, ...(h || [])]); toast(`RAGE! Bench: ${f.n(r.total)} pontos`, "success"); }
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  useEffect(() => {
    if (params.get("run") && !started.current) { started.current = true; setParams({}, { replace: true }); start(); }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const last = result || history?.[0];
  const best = (history || []).reduce((m, h) => Math.max(m, h.total || 0), 0);
  const curIdx = running ? TESTS.findIndex((t) => t.id === progress?.test) : -1;
  const overall = running ? ((Math.max(0, curIdx) + (progress?.pct || 0) / 100) / TESTS.length) * 100 : 0;
  const scoreOf = (r, id) => id === "disk" ? (r?.scores?.diskRead != null ? `${f.n(r.scores.diskRead)} / ${f.n(r.scores.diskWrite)}` : "—") : r?.scores?.[id] != null ? f.n(r.scores[id], id === "memory" ? 1 : 0) : "—";
  const vsBest = last ? Math.min(1, last.total / Math.max(best, last.total, 1)) : 0;

  return (
    <div className="space-y-4">
      <div className="grid gap-4 xl:grid-cols-3">
        <section className="panel flex min-w-0 flex-col items-center gap-6 p-6 sm:flex-row xl:col-span-2">
          <TickRing value={running ? overall / 100 : vsBest} size={204} colorAt={SCORE_COLOR}>
            {running ? (
              <>
                <span className="heading text-[40px] leading-none font-semibold tabular">{f.n(overall)}%</span>
                <span className="mt-1.5 max-w-[150px] truncate text-[11.5px] text-[var(--text-tertiary)]">{progress?.label || "Preparando…"}</span>
              </>
            ) : (
              <>
                <span className="heading text-[40px] leading-none font-semibold tabular">{last ? f.n(last.total) : "—"}</span>
                <span className="mt-1.5 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Pontos</span>
              </>
            )}
          </TickRing>
          <div className="min-w-0">
            <h2 className="heading text-[22px] leading-tight font-semibold">{running ? "Testando seu PC…" : last ? (result ? "Resultado do teste" : `Último teste ${f.rel(last.at)}`) : "Quanto o seu PC aguenta?"}</h2>
            <p className="mt-1.5 max-w-[62ch] text-[13px] leading-relaxed text-[var(--text-secondary)]">
              Mede CPU (um núcleo e todos), memória e disco com cargas fixas e reproduzíveis. Feche jogos e programas pesados para um resultado justo.
            </p>
            <div className="mt-4 flex flex-wrap gap-2">
              <Chip icon="memory">{s?.cpu?.name?.replace(/ @.*/, "") || "CPU"}</Chip>
              <Chip icon="developer_board">{g?.name || "GPU"}</Chip>
              <Chip icon="memory_alt">{f.gb(s?.ram?.total, 0)} {s?.ram?.type}</Chip>
              {best > 0 && <Chip tone="brand" icon="emoji_events">Recorde: {f.n(best)}</Chip>}
            </div>
            {last && best > 0 && !running && <p className="mt-3 text-[12px] text-[var(--text-tertiary)]">O anel mostra esta nota em relação ao seu recorde.</p>}
          </div>
        </section>

        <section className="hot-card flex min-w-0 flex-col p-5">
          <p className="text-[12.5px] font-semibold">{running ? "Testando agora" : last ? "Pronto para medir de novo" : "Pronto para medir"}</p>
          <p className="mt-3 flex items-baseline gap-2">
            <span className="heading text-[34px] leading-none font-semibold tabular">{running ? `${Math.max(0, curIdx) + 1} de ${TESTS.length}` : `${TESTS.length} testes`}</span>
            <span className="min-w-0 truncate text-[15px] text-[var(--hot-ink-2)]">{running ? TESTS[Math.max(0, curIdx)].label : "cerca de 1 min"}</span>
          </p>
          <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--hot-ink-2)]">
            {boost?.active ? "O Modo Turbo está ligado, então este resultado conta com ele." : "Ligue o Modo Turbo antes de rodar e compare a diferença."}
          </p>
          <div className="mt-4 grid gap-2">
            <div className="hot-pill" data-dark="true"><span>Recorde</span><b>{best > 0 ? `${f.n(best)} pts` : "ainda não tem"}</b></div>
            <div className="hot-pill"><span>Modo Turbo</span><span>{boost?.active ? "Ligado" : "Desligado"}</span></div>
          </div>
          <div className="mt-auto pt-5">
            {running
              ? <button onClick={cancel} className="hot-btn" style={{ "--p": overall / 100 }}><Icon name="stop" size={19} fill />Parar · {f.n(overall)}%</button>
              : <button onClick={start} className="hot-btn"><Icon name="play_arrow" size={20} fill />{last ? "Rodar de novo" : "Rodar benchmark"}</button>}
          </div>
        </section>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {TESTS.map((t, i) => {
          const state = running ? (i < curIdx ? "done" : i === curIdx ? "run" : "wait") : last ? "done" : "wait";
          return (
            <div key={t.id} className="panel p-4 transition-colors duration-200" style={state === "run" ? { borderColor: "var(--brand)" } : undefined}>
              <div className="flex items-center gap-2.5">
                <span className={cx("flex size-9 shrink-0 items-center justify-center rounded-[var(--radius-md)]", state === "run" ? "brand-bg text-white" : "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}><Icon name={t.icon} size={19} /></span>
                <div className="min-w-0"><p className="text-[13px] font-semibold">{t.label}</p><p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{t.desc}</p></div>
              </div>
              <p className="heading mt-4 text-[26px] font-semibold tabular">
                {state === "run" ? <span className="text-[var(--brand-primary-strong)]">{f.n(progress?.pct)}%</span> : running && state === "wait" ? <span className="text-[var(--text-muted)]">—</span> : scoreOf(last, t.id)}
                <span className="ml-1.5 text-[13px] font-semibold text-[var(--text-tertiary)]">{t.id === "disk" ? "MB/s (L/E)" : t.unit}</span>
              </p>
              {state === "run" && <Progress value={progress?.pct} className="mt-2" height={4} />}
            </div>
          );
        })}
      </div>

      <Panel title="Histórico" icon="history" subtitle="Compare antes e depois de otimizar" padded={false}>
        {loading ? <div className="p-4"><Skeleton className="h-24" /></div> : !history?.length ? <Empty icon="speed" title="Nenhum teste ainda">Rode o primeiro benchmark para começar o histórico.</Empty> : (
          <table className="w-full text-[13px]">
            <thead><tr className="text-left text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase"><th className="py-2.5 pl-4">Data</th><th>Total</th><th>1 núcleo</th><th>Todos</th><th>Memória</th><th>Disco L/E</th><th className="pr-4">vs. recorde</th></tr></thead>
            <tbody>
              {history.slice(0, 15).map((h) => (
                <tr key={h.id || h.at} className="border-t border-[var(--panel-border)]">
                  <td className="py-2.5 pl-4 text-[var(--text-secondary)]">{f.dateTime(h.at)}</td>
                  <td className="font-semibold tabular">{f.n(h.total)}</td>
                  <td className="tabular">{f.n(h.scores?.cpuSingle)}</td>
                  <td className="tabular">{f.n(h.scores?.cpuMulti)}</td>
                  <td className="tabular">{h.scores?.memory != null ? `${f.n(h.scores.memory, 1)} GB/s` : "—"}</td>
                  <td className="tabular">{h.scores?.diskRead != null ? `${f.n(h.scores.diskRead)} / ${f.n(h.scores.diskWrite)}` : "—"}</td>
                  <td className="pr-4"><div className="flex items-center gap-2"><div className="h-1.5 w-24 overflow-hidden rounded-full bg-[var(--seg-off)]"><div className="brand-bg h-full rounded-full" style={{ width: `${(h.total / best) * 100}%` }} /></div><span className="text-[11.5px] text-[var(--text-tertiary)] tabular">{f.pct((h.total / best) * 100)}</span></div></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>
    </div>
  );
}
