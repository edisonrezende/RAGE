import { useMemo, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useRpc } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, Checkbox, Chip, Confirm, Empty, ErrorBox, Icon, Modal, Panel, Skeleton, Tabs, cx } from "../../components/ui.jsx";

export default function Programs() {
  const { toast } = useApp();
  const { data, setData, error, loading, reload } = useRpc("apps.list");
  const [q, setQ] = useState("");
  const [tab, setTab] = useState("win32");
  const [sort, setSort] = useState("name");
  const [confirm, setConfirm] = useState(null);
  const [busy, setBusy] = useState(null);
  const [left, setLeft] = useState(null);
  const [leftSel, setLeftSel] = useState(new Set());

  const list = useMemo(() => {
    let l = (data || []).filter((a) => a.source === tab);
    if (q.trim()) l = l.filter((a) => `${a.name} ${a.publisher}`.toLowerCase().includes(q.toLowerCase()));
    return [...l].sort((a, b) => sort === "size" ? (b.size || 0) - (a.size || 0) : sort === "date" ? new Date(b.installDate || 0) - new Date(a.installDate || 0) : a.name.localeCompare(b.name));
  }, [data, tab, q, sort]);
  const total = list.reduce((a, x) => a + (x.size || 0), 0);

  const uninstall = async () => {
    const app = confirm; setConfirm(null); setBusy(app.id);
    try {
      const r = await call("apps.uninstall", { id: app.id });
      if (r?.ok === false) toast(`A desinstalação de ${app.name} não foi concluída`, "warning");
      else toast(`${app.name} desinstalado`, "success");
      if (r?.leftovers?.length) { setLeft({ app, items: r.leftovers }); setLeftSel(new Set(r.leftovers.map((x) => x.path))); }
      reload(true);
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const removeLeftovers = async () => {
    try { await call("apps.removeLeftovers", { paths: [...leftSel] }); toast("Sobras enviadas para a Lixeira", "success"); } catch (e) { toast(e.message, "danger"); }
    setLeft(null);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <Tabs size="md" value={tab} onChange={setTab} items={[{ value: "win32", label: "Programas", badge: data?.filter((a) => a.source === "win32").length }, { value: "store", label: "Apps da Microsoft Store", badge: data?.filter((a) => a.source === "store").length }]} />
        <div className="flex h-8 w-64 items-center gap-2 rounded-[var(--radius-md)] border border-[var(--border)] bg-[var(--bg-surface)] px-2.5">
          <Icon name="search" size={17} className="text-[var(--text-tertiary)]" />
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Buscar…" className="min-w-0 flex-1 bg-transparent text-[13px] outline-none" />
        </div>
        <Tabs value={sort} onChange={setSort} items={[{ value: "name", label: "Nome" }, { value: "size", label: "Tamanho" }, { value: "date", label: "Data" }]} />
        <span className="ml-auto text-[12.5px] text-[var(--text-tertiary)]">{list.length} itens · {f.bytes(total)}</span>
      </div>
      <ErrorBox error={error} onRetry={reload} />
      <Panel padded={false}>
        {loading && !data ? <div className="p-4"><Skeleton className="h-96" /></div> : list.length === 0 ? <Empty icon="apps" title="Nada encontrado" /> : (
          <div className="divide-y divide-[var(--border)]">
            {list.map((a) => (
              <div key={a.id} className="flex items-center gap-3 px-4 py-2.5 hover:bg-[var(--bg-subtle)]">
                {a.icon ? <img src={a.icon} alt="" className="size-8 shrink-0 rounded" /> : <span className="flex size-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]"><Icon name="apps" size={18} /></span>}
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13px] font-semibold">{a.name}</p>
                  <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{[a.publisher, a.version && `v${a.version}`, a.installDate && `instalado em ${f.dateShort(a.installDate)}`].filter(Boolean).join(" · ")}</p>
                </div>
                <span className="w-20 text-right text-[12.5px] font-semibold tabular text-[var(--text-secondary)]">{a.size ? f.bytes(a.size) : "—"}</span>
                <Button size="sm" icon="delete" loading={busy === a.id} disabled={!a.canUninstall || !!busy} onClick={() => setConfirm(a)}>Desinstalar</Button>
              </div>
            ))}
          </div>
        )}
      </Panel>
      <Confirm open={!!confirm} title={`Desinstalar ${confirm?.name}?`} tone="danger" icon="delete" confirmLabel="Desinstalar" onClose={() => setConfirm(null)} onConfirm={uninstall}>
        O desinstalador do próprio programa vai abrir. Depois o RAGE! procura pastas que sobraram.
      </Confirm>
      <Modal open={!!left} onClose={() => setLeft(null)} width={600}>
        <div className="p-5">
          <h3 className="heading text-base font-semibold">Sobras de {left?.app?.name}</h3>
          <p className="mt-1 text-[13px] text-[var(--text-secondary)]">Estas pastas ficaram para trás. Elas vão para a Lixeira (dá para recuperar).</p>
          <div className="scroll-area mt-3 max-h-72 overflow-y-auto rounded-[var(--radius-md)] border border-[var(--border)]">
            {left?.items.map((x) => (
              <label key={x.path} className="flex cursor-pointer items-center gap-3 border-b border-[var(--border)] px-3 py-2 last:border-0">
                <Checkbox checked={leftSel.has(x.path)} onChange={(v) => setLeftSel((s) => { const n = new Set(s); v ? n.add(x.path) : n.delete(x.path); return n; })} />
                <span className="mono min-w-0 flex-1 truncate text-[12px]" title={x.path}>{x.path}</span>
                <span className="text-[12px] tabular text-[var(--text-tertiary)]">{f.bytes(x.size)}</span>
              </label>
            ))}
          </div>
          <div className="mt-4 flex justify-end gap-2"><Button variant="ghost" onClick={() => setLeft(null)}>Manter</Button><Button variant="danger" icon="delete" disabled={!leftSel.size} onClick={removeLeftovers}>Remover selecionadas</Button></div>
        </div>
      </Modal>
    </div>
  );
}
