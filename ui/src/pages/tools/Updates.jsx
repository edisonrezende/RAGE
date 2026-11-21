import { useState } from "react";
import { useApp } from "../../App.jsx";
import { useAction, useRpc } from "../../lib/store.js";
import { Button, Checkbox, Chip, Empty, ErrorBox, Icon, LogView, Panel, Skeleton } from "../../components/ui.jsx";
import { PageIntro } from "../../components/Shell.jsx";

export default function Updates() {
  const { toast, info } = useApp();
  const { data, error, loading, reload } = useRpc("updates.list");
  const [sel, setSel] = useState(null);
  const a = useAction();
  const [results, setResults] = useState({});
  const list = data || [];
  const selected = sel ?? new Set(list.map((x) => x.id));
  const toggle = (id, v) => setSel(() => { const n = new Set(selected); v ? n.add(id) : n.delete(id); return n; });

  const install = async (ids) => {
    try {
      const r = await a.run("updates.install", { ids });
      const map = {}; (r || []).forEach((x) => (map[x.id] = x));
      setResults((s) => ({ ...s, ...map }));
      const ok = (r || []).filter((x) => x.ok).length;
      toast(`${ok} de ${ids.length} programas atualizados`, ok === ids.length ? "success" : "warning");
      reload(true);
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  const current = a.progress?.id;

  return (
    <div className="space-y-4">
      <PageIntro icon="system_update_alt" title="Atualizar programas" actions={<><Button icon="refresh" onClick={() => reload()} disabled={a.running}>Procurar de novo</Button>{list.length > 0 && <Button variant="brand" icon="download" loading={a.running} disabled={!selected.size} onClick={() => install([...selected])}>Atualizar {selected.size}</Button>}</>}>
        Usa o winget, o gerenciador de pacotes oficial do Windows: baixa direto do fabricante, sem instaladores com propaganda.{!info?.isAdmin && " Alguns programas pedem administrador."}
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      <Panel padded={false}>
        {loading ? <div className="flex items-center gap-3 p-6 text-[13px] text-[var(--text-tertiary)]"><span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" />Consultando o winget… isso pode levar alguns segundos.</div>
          : list.length === 0 ? <Empty icon="verified" title="Tudo atualizado">Nenhuma atualização disponível pelo winget.</Empty> : (
            <div className="divide-y divide-[var(--border)]">
              {list.map((u) => {
                const r = results[u.id];
                return (
                  <div key={u.id} className="flex items-center gap-3 px-4 py-2.5">
                    <Checkbox checked={selected.has(u.id)} onChange={(v) => toggle(u.id, v)} disabled={a.running} />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-[13px] font-semibold">{u.name}</p>
                      <p className="mono truncate text-[11px] text-[var(--text-tertiary)]">{u.id}</p>
                    </div>
                    <span className="text-[12.5px] tabular text-[var(--text-tertiary)]">{u.version}</span>
                    <Icon name="arrow_forward" size={15} className="text-[var(--text-muted)]" />
                    <span className="w-24 text-[12.5px] font-semibold tabular text-[var(--color-success)]">{u.available}</span>
                    {current === u.id && a.running ? <Chip tone="brand" dot>atualizando</Chip> : r ? <Chip tone={r.ok ? "success" : "danger"}>{r.ok ? "atualizado" : "falhou"}</Chip> : <Button size="sm" onClick={() => install([u.id])} disabled={a.running}>Atualizar</Button>}
                  </div>
                );
              })}
            </div>
          )}
      </Panel>
      {(a.running || a.lines.length > 0) && <Panel title="Saída do winget" icon="terminal"><LogView lines={a.lines} /></Panel>}
    </div>
  );
}
