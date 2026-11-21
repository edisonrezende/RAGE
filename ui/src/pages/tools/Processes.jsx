import { useEffect, useMemo, useRef, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import * as f from "../../lib/format.js";
import { Button, Chip, Confirm, ErrorBox, Icon, IconButton, Menu, Panel, Skeleton, Toggle, cx } from "../../components/ui.jsx";

const PRIOS = [["idle", "Baixa"], ["belowNormal", "Abaixo do normal"], ["normal", "Normal"], ["aboveNormal", "Acima do normal"], ["high", "Alta"]];
const COLS = [["name", "Nome"], ["cpu", "CPU"], ["mem", "Memória"], ["gpu", "GPU"]];

export default function Processes() {
  const { toast } = useApp();
  const [list, setList] = useState(null);
  const [error, setError] = useState(null);
  const [q, setQ] = useState("");
  const [sort, setSort] = useState(["cpu", -1]);
  const [showSystem, setShowSystem] = useState(false);
  const [paused, setPaused] = useState(false);
  const [kill, setKill] = useState(null);

  const icons = useRef(new Map());
  useEffect(() => {
    let alive = true;
    // Ícones só na 1ª chamada (e quando aparece um programa novo); depois o polling vem leve.
    const load = () => {
      if (paused) return;
      const withIcons = icons.current.size === 0;
      call("proc.list", { icons: withIcons }).then((r) => {
        if (!alive) return;
        let missing = false;
        for (const p of r) {
          const k = p.path || p.name;
          if (p.icon) icons.current.set(k, p.icon);
          else if (icons.current.has(k)) p.icon = icons.current.get(k);
          else if (p.path && !icons.current.has(k)) { missing = true; icons.current.set(k, null); }
        }
        if (missing && !withIcons) icons.current.clear();
        setList(r); setError(null);
      }).catch((e) => alive && setError(e.message));
    };
    load();
    const t = setInterval(load, 2500);
    return () => { alive = false; clearInterval(t); };
  }, [paused]);

  const rows = useMemo(() => {
    let l = (list || []).filter((p) => showSystem || !p.isSystem);
    if (q.trim()) { const s = q.toLowerCase(); l = l.filter((p) => p.name?.toLowerCase().includes(s) || p.title?.toLowerCase().includes(s) || String(p.pid) === s); }
    const [k, dir] = sort;
    return [...l].sort((a, b) => (k === "name" ? a.name.localeCompare(b.name) * -dir : ((a[k] || 0) - (b[k] || 0)) * dir));
  }, [list, q, sort, showSystem]);

  const doKill = async (tree) => {
    const p = kill; setKill(null);
    try { await call("proc.kill", { pid: p.pid, tree }); toast(`${p.name} encerrado`, "success"); setList((l) => l.filter((x) => x.pid !== p.pid)); } catch (e) { toast(e.message, "danger"); }
  };
  const prio = async (p, priority) => { try { await call("proc.priority", { pid: p.pid, priority }); toast(`Prioridade de ${p.name}: ${PRIOS.find((x) => x[0] === priority)[1]}`, "success"); } catch (e) { toast(e.message, "danger"); } };
  const totals = useMemo(() => (list || []).reduce((a, p) => ({ cpu: a.cpu + (p.cpu || 0), mem: a.mem + (p.mem || 0) }), { cpu: 0, mem: 0 }), [list]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex h-9 w-72 items-center gap-2 rounded-[var(--radius-md)] border border-[var(--border)] bg-[var(--bg-surface)] px-2.5">
          <Icon name="search" size={18} className="text-[var(--text-tertiary)]" />
          <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Buscar processo, janela ou PID…" className="min-w-0 flex-1 bg-transparent text-[13px] outline-none" />
        </div>
        <label className="flex items-center gap-2 text-[13px]"><Toggle size="sm" checked={showSystem} onChange={setShowSystem} />Processos do sistema</label>
        <span className="ml-auto text-[12.5px] text-[var(--text-tertiary)]">{list ? `${list.length} processos · CPU ${f.pct(totals.cpu)} · ${f.bytes(totals.mem)}` : ""}</span>
        <Button size="sm" icon={paused ? "play_arrow" : "pause"} onClick={() => setPaused((p) => !p)}>{paused ? "Retomar" : "Pausar"}</Button>
      </div>
      <ErrorBox error={error} />
      <Panel padded={false}>
        {!list ? <div className="p-4"><Skeleton className="h-96" /></div> : (
          <div className="scroll-area max-h-[calc(100vh-230px)] overflow-y-auto">
            <table className="w-full text-[13px]">
              <thead className="sticky top-0 z-10 bg-[var(--bg-surface)]">
                <tr className="text-left text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">
                  {COLS.map(([k, l]) => (
                    <th key={k} className={cx("cursor-pointer py-2.5 select-none hover:text-[var(--text-primary)]", k === "name" ? "pl-4" : "w-28")} onClick={() => setSort(([sk, d]) => [k, sk === k ? -d : k === "name" ? 1 : -1])}>
                      {l}{sort[0] === k && <Icon name={sort[1] < 0 ? "arrow_downward" : "arrow_upward"} size={13} className="ml-0.5 align-middle" />}
                    </th>
                  ))}
                  <th className="w-24">PID</th><th className="w-20" />
                </tr>
              </thead>
              <tbody>
                {rows.map((p) => (
                  <tr key={p.pid} className="group border-t border-[var(--border)] hover:bg-[var(--bg-subtle)]">
                    <td className="py-1.5 pl-4">
                      <div className="flex items-center gap-2.5">
                        {p.icon ? <img src={p.icon} alt="" className="size-5 shrink-0" /> : <Icon name={p.isSystem ? "settings" : "web_asset"} size={18} className="text-[var(--text-tertiary)]" />}
                        <div className="min-w-0">
                          <p className="truncate font-medium">{p.name}{p.isSystem && <Chip className="ml-1.5">sistema</Chip>}</p>
                          {p.title && <p className="max-w-[420px] truncate text-[11.5px] text-[var(--text-tertiary)]">{p.title}</p>}
                        </div>
                      </div>
                    </td>
                    <td className={cx("tabular", p.cpu > 25 && "font-semibold text-[var(--color-warning)]")}>{f.pct(p.cpu, 1)}</td>
                    <td className="tabular">{f.bytes(p.mem)}</td>
                    <td className="tabular">{p.gpu ? f.pct(p.gpu) : "—"}</td>
                    <td className="tabular text-[var(--text-tertiary)]">{p.pid}</td>
                    <td className="pr-3 text-right">
                      <div className="flex justify-end opacity-0 group-hover:opacity-100">
                        <IconButton icon="close" label="Encerrar" onClick={() => setKill(p)} />
                        <Menu align="end" width={220} trigger={({ toggle }) => <IconButton icon="more_vert" label="Mais" onClick={toggle} />} items={[
                          ...PRIOS.map(([k, l]) => ({ icon: "low_priority", label: `Prioridade: ${l}`, checked: p.priority === k, onClick: () => prio(p, k) })),
                          { divider: true },
                          { icon: "folder_open", label: "Abrir local do arquivo", disabled: !p.path, onClick: () => call("proc.reveal", { pid: p.pid }).catch((e) => toast(e.message, "danger")) },
                          { icon: "account_tree", label: "Encerrar árvore de processos", danger: true, onClick: () => setKill({ ...p, tree: true }) },
                        ]} />
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <Confirm open={!!kill} title={`Encerrar ${kill?.name}?`} tone="danger" icon="close" confirmLabel="Encerrar" onClose={() => setKill(null)} onConfirm={() => doKill(!!kill?.tree)}>
        {kill?.isSystem ? "É um processo do sistema — encerrar pode travar ou reiniciar o Windows." : "Trabalho não salvo nesse programa será perdido."}
      </Confirm>
    </div>
  );
}
