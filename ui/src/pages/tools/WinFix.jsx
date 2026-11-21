import { useState } from "react";
import { useApp } from "../../App.jsx";
import { useAction, useRpc } from "../../lib/store.js";
import { Button, Chip, Confirm, ErrorBox, Icon, LogView, Panel, Skeleton, cx } from "../../components/ui.jsx";
import { PageIntro } from "../../components/Shell.jsx";

export default function WinFix() {
  const { toast, info } = useApp();
  const { data, error, loading, reload } = useRpc("fix.list");
  const a = useAction();
  const [cur, setCur] = useState(null);
  const [results, setResults] = useState({});
  const [confirm, setConfirm] = useState(null);
  const run = async (fx) => {
    setConfirm(null); setCur(fx);
    try {
      const r = await a.run("fix.run", { id: fx.id });
      setResults((s) => ({ ...s, [fx.id]: r }));
      toast(r?.message || (r?.ok ? "Concluído" : "Terminou com avisos"), r?.ok ? "success" : "warning");
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  return (
    <div className="space-y-4">
      <PageIntro icon="build" title="Reparos do Windows">
        Ferramentas oficiais do Windows (SFC, DISM, netsh…) num clique, com a saída ao vivo. Use quando algo estiver estranho: erros, rede caindo, Windows Update travado.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      {loading && !data ? <Skeleton className="h-72" /> : (
        <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-3">
          {(data || []).map((fx) => {
            const r = results[fx.id];
            const running = a.running && cur?.id === fx.id;
            return (
              <div key={fx.id} className={cx("panel flex flex-col p-4", running && "border-[var(--brand)]")}>
                <div className="flex items-start gap-3">
                  <span className={cx("flex size-10 shrink-0 items-center justify-center rounded-[var(--radius-md)]", running ? "brand-bg text-white" : "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}><Icon name={fx.icon || "build"} size={21} /></span>
                  <div className="min-w-0 flex-1">
                    <p className="text-[13.5px] font-semibold">{fx.title}</p>
                    <p className="text-[12px] text-[var(--text-tertiary)]">{fx.desc}</p>
                  </div>
                </div>
                <div className="mt-3 flex flex-wrap gap-1.5">
                  {fx.minutes != null && <Chip icon="schedule">{fx.minutes < 1 ? "segundos" : `~${fx.minutes} min`}</Chip>}
                  {fx.needsReboot && <Chip tone="warning">pede reinício</Chip>}
                  {r && <Chip tone={r.ok ? "success" : "warning"}>{r.ok ? "concluído" : "com avisos"}</Chip>}
                </div>
                <div className="mt-auto pt-4">
                  {running ? <Button className="w-full" icon="stop" onClick={a.cancel}>Cancelar</Button> : <Button className="w-full" icon="play_arrow" disabled={a.running || fx.available === false || (fx.admin && !info?.isAdmin)} onClick={() => (fx.needsReboot || fx.minutes >= 10 ? setConfirm(fx) : run(fx))}>Executar</Button>}
                </div>
              </div>
            );
          })}
        </div>
      )}
      {(a.running || a.lines.length > 0) && (
        <Panel title={a.running ? `Executando: ${cur?.title}` : `Saída: ${cur?.title || ""}`} icon="terminal"><LogView lines={a.lines} className="max-h-80" /></Panel>
      )}
      <Confirm open={!!confirm} title={confirm?.title} icon="build" confirmLabel="Executar" onClose={() => setConfirm(null)} onConfirm={() => run(confirm)}>
        {confirm?.desc} {confirm?.minutes >= 10 ? `Pode levar ${confirm.minutes} minutos ou mais.` : ""} {confirm?.needsReboot ? "Você vai precisar reiniciar o PC depois." : ""}
      </Confirm>
    </div>
  );
}
