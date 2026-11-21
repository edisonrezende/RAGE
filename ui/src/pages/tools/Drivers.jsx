import { useMemo, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useAction, useRpc } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, Chip, ErrorBox, Icon, LogView, Panel, Skeleton, Tabs } from "../../components/ui.jsx";
import { PageIntro } from "../../components/Shell.jsx";

export default function Drivers() {
  const { toast, info } = useApp();
  const { data, error, loading, reload } = useRpc("drivers.list");
  const [tab, setTab] = useState("attention");
  const [q, setQ] = useState("");
  const a = useAction();
  const items = data?.items || [];
  const list = useMemo(() => {
    let l = tab === "attention" ? items.filter((d) => d.problem || d.old) : items;
    if (q.trim()) l = l.filter((d) => `${d.name} ${d.provider} ${d.class}`.toLowerCase().includes(q.toLowerCase()));
    return [...l].sort((x, y) => (!!y.problem - !!x.problem) || (y.old - x.old) || x.name.localeCompare(y.name));
  }, [items, tab, q]);
  const backup = async () => {
    const folder = await call("app.pickFolder", { title: "Onde salvar o backup dos drivers?" });
    if (!folder) return;
    try { const r = await a.run("drivers.backup", { folder }); toast(`${r?.count ?? ""} drivers salvos em ${r?.folder || folder}`, "success"); } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  return (
    <div className="space-y-4">
      <PageIntro icon="developer_board" title="Drivers" actions={<><Button icon="system_update" onClick={() => call("app.open", { target: "ms-settings:windowsupdate-optionalupdates" })}>Atualizações opcionais</Button><Button variant="brand" icon="save" loading={a.running} disabled={!info?.isAdmin} onClick={backup}>Backup de drivers</Button></>}>
        Lista os drivers instalados, mostra dispositivos com erro e os muito antigos. Atualize pelo Windows Update (atualizações opcionais) ou pelo site do fabricante — para placa de vídeo, use o app da NVIDIA/AMD/Intel.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      {(a.running || a.lines.length > 0) && <LogView lines={a.lines} className="max-h-40" />}
      <div className="flex flex-wrap items-center gap-3">
        <Tabs size="md" value={tab} onChange={setTab} items={[{ value: "attention", label: "Precisam de atenção", badge: items.filter((d) => d.problem || d.old).length }, { value: "all", label: "Todos", badge: items.length }]} />
        <input className="input ml-auto w-64" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Buscar driver…" />
      </div>
      <Panel padded={false}>
        {loading && !data ? <div className="p-4"><Skeleton className="h-80" /></div> : list.length === 0 ? (
          <p className="py-10 text-center text-[13px] text-[var(--text-tertiary)]">{tab === "attention" ? "Nenhum driver com problema ou muito antigo." : "Nada encontrado."}</p>
        ) : (
          <div className="divide-y divide-[var(--border)]">
            {list.map((d, i) => (
              <div key={d.deviceId || i} className="flex items-center gap-3 px-4 py-2.5">
                <Icon name={d.problem ? "error" : d.class === "Display" ? "monitor" : d.class === "Net" ? "lan" : d.class === "AudioEndpoint" || d.class === "MEDIA" ? "volume_up" : "memory"} size={19} className={d.problem ? "text-[var(--color-danger)]" : "text-[var(--text-tertiary)]"} />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13px] font-semibold">{d.name}</p>
                  <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{d.problem || `${d.provider || d.manufacturer || "—"} · ${d.class || ""}`}</p>
                </div>
                {d.old && <Chip tone="warning">antigo</Chip>}
                {d.problem && <Chip tone="danger">com erro</Chip>}
                <span className="w-32 text-right text-[12px] tabular text-[var(--text-secondary)]">{d.version}</span>
                <span className="w-24 text-right text-[12px] tabular text-[var(--text-tertiary)]">{f.dateShort(d.date)}</span>
              </div>
            ))}
          </div>
        )}
      </Panel>
    </div>
  );
}
