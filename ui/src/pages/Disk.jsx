import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { useAction, useRpc, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Ring } from "../components/Charts.jsx";
import { Button, Chip, ErrorBox, Icon, KV, LogView, Panel, Skeleton } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

function VolumeCard({ v, live }) {
  const { toast, info } = useApp();
  const a = useAction();
  const [done, setDone] = useState(null);
  const used = v.total ? ((v.total - v.free) / v.total) * 100 : 0;
  const run = async () => {
    setDone(null);
    try { const r = await a.run("disk.optimize", { letter: v.letter }); setDone(r); toast(r?.message || "Otimização concluída", r?.ok === false ? "danger" : "success"); }
    catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  return (
    <Panel>
      <div className="flex items-start gap-4">
        <Ring value={used} size={92} stroke={8} color={used > 90 ? "var(--color-danger)" : used > 80 ? "var(--color-warning)" : "var(--chart-disk)"}>
          <span className="text-[16px] font-semibold tabular">{f.pct(used)}</span>
        </Ring>
        <div className="min-w-0 flex-1">
          <p className="flex flex-wrap items-center gap-2 text-[15px] font-semibold">{v.letter} {v.label && <span className="text-[13px] font-medium text-[var(--text-tertiary)]">{v.label}</span>}<Chip>{v.media}{v.bus ? ` · ${v.bus}` : ""}</Chip></p>
          <p className="mt-0.5 text-[12.5px] text-[var(--text-secondary)]">{f.gb(v.free)} livres de {f.gb(v.total, 0)}</p>
          {live?.model && <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{live.model}</p>}
          <div className="mt-2 flex flex-wrap gap-1.5">
            {live?.temp != null && <Chip tone={live.temp > 65 ? "warning" : "neutral"} icon="device_thermostat">{f.temp(live.temp)}</Chip>}
            {live?.life != null && <Chip tone={live.life < 20 ? "danger" : live.life < 50 ? "warning" : "success"} icon="favorite">vida útil {f.pct(live.life)}</Chip>}
            {live?.active != null && <Chip icon="swap_vert">atividade {f.pct(live.active)}</Chip>}
          </div>
        </div>
      </div>
      <div className="mt-4 border-t border-[var(--border)] pt-3">
        <KV label="Última otimização" value={v.lastOptimized ? f.rel(v.lastOptimized) : "desconhecida"} />
        {v.fragmentation != null && <KV label="Fragmentação" value={f.pct(v.fragmentation)} />}
        <KV label="Recomendação" value={v.needed ? (v.action === "trim" ? "Executar TRIM" : "Desfragmentar") : "Nada a fazer"} />
      </div>
      {a.running || a.lines.length ? <LogView lines={a.lines} className="mt-3 max-h-32" empty="Otimizando…" /> : null}
      {v.action && (
        <Button className="mt-3 w-full" variant={v.needed ? "brand" : "secondary"} icon={v.action === "trim" ? "bolt" : "grid_view"} loading={a.running} disabled={!info?.isAdmin} onClick={run}>
          {v.action === "trim" ? "Otimizar SSD (TRIM)" : "Desfragmentar HD"}
        </Button>
      )}
      {done && <p className="mt-2 text-[12px] text-[var(--color-success)]">{done.message}</p>}
    </Panel>
  );
}

export default function Disk() {
  const s = useTelemetry();
  const nav = useNavigate();
  const { data, error, loading, reload } = useRpc("disk.volumes");
  return (
    <div className="space-y-4">
      <PageIntro icon="hard_drive" title="Discos" actions={<><Button icon="delete_sweep" onClick={() => nav("/limpeza")}>Liberar espaço</Button><Button variant="ghost" icon="folder_zip" onClick={() => nav("/ferramentas/arquivos-grandes")}>Arquivos grandes</Button></>}>
        SSDs ganham com o TRIM (mantém a velocidade de escrita); HDs ganham com a desfragmentação. Temperatura e vida útil vêm do SMART do próprio disco.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      {loading && !data ? <div className="grid gap-4 lg:grid-cols-2"><Skeleton className="h-64" /><Skeleton className="h-64" /></div> : (
        <div className="grid gap-4 lg:grid-cols-2 2xl:grid-cols-3">
          {(data || []).map((v) => <VolumeCard key={v.letter} v={v} live={s?.disks?.find((d) => d.letter === v.letter)} />)}
        </div>
      )}
      {!loading && !data?.length && !error && <Panel><p className="py-6 text-center text-[13px] text-[var(--text-tertiary)]">Nenhum disco encontrado.</p></Panel>}
    </div>
  );
}
