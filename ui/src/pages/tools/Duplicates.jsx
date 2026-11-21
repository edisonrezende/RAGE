import { useMemo, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useAction } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, Checkbox, Confirm, Empty, IconButton, Panel, Progress, Segmented } from "../../components/ui.jsx";
import { FolderBar, useFolder } from "./FileTools.jsx";

export default function Duplicates() {
  const { toast } = useApp();
  const [folder, setFolder] = useFolder("dupes", "C:\\Users");
  const [min, setMin] = useState(1);
  const a = useAction();
  const [groups, setGroups] = useState(null);
  const [sel, setSel] = useState(new Set());
  const [confirm, setConfirm] = useState(false);

  const scan = async () => {
    setGroups(null); setSel(new Set());
    try {
      const r = await a.run("files.duplicates", { root: folder, minSize: min * 1048576 });
      setGroups(r);
      // pré-seleciona todas as cópias, menos a mais antiga de cada grupo
      const s = new Set();
      for (const g of r || []) [...g.files].sort((x, y) => new Date(x.modified) - new Date(y.modified)).slice(1).forEach((x) => s.add(x.path));
      setSel(s);
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  const wasted = useMemo(() => (groups || []).reduce((s, g) => s + g.size * (g.files.length - 1), 0), [groups]);
  const selSize = useMemo(() => (groups || []).reduce((s, g) => s + g.files.filter((x) => sel.has(x.path)).length * g.size, 0), [groups, sel]);
  const recycle = async () => {
    setConfirm(false);
    try {
      await call("files.recycle", { paths: [...sel] });
      toast(`${sel.size} cópias enviadas para a Lixeira`, "success");
      setGroups((gs) => gs.map((g) => ({ ...g, files: g.files.filter((x) => !sel.has(x.path)) })).filter((g) => g.files.length > 1)); setSel(new Set());
    } catch (e) { toast(e.message, "danger"); }
  };
  const allChosen = (g) => g.files.every((x) => sel.has(x.path));

  return (
    <div className="space-y-4">
      <FolderBar folder={folder} setFolder={setFolder} disabled={a.running}>
        <Segmented value={min} onChange={setMin} options={[[0, "Todos"], [1, "1 MB+"], [10, "10 MB+"], [100, "100 MB+"]].map(([v, l]) => ({ value: v, label: l }))} />
        {a.running ? <Button icon="stop" onClick={a.cancel}>Parar</Button> : <Button variant="brand" icon="search" onClick={scan}>Procurar</Button>}
      </FolderBar>
      {a.running && <Panel><p className="mb-2 truncate text-[12.5px] text-[var(--text-tertiary)]">{a.progress?.label || `${f.n(a.progress?.scanned)} arquivos`} {a.progress?.current ? `· ${a.progress.current}` : ""}</p><Progress indeterminate /></Panel>}
      {groups && (
        <Panel padded={false} title={`${groups.length} grupos de arquivos idênticos · ${f.bytes(wasted)} repetidos`} subtitle="A cópia mais antiga de cada grupo fica desmarcada" actions={sel.size > 0 && <Button size="sm" variant="danger" icon="delete" onClick={() => setConfirm(true)}>Lixeira ({f.bytes(selSize)})</Button>}>
          {groups.length === 0 ? <Empty icon="file_copy" title="Nenhum arquivo duplicado" /> : (
            <div className="scroll-area max-h-[calc(100vh-300px)] overflow-y-auto">
              {groups.map((g) => (
                <div key={g.hash} className="border-t border-[var(--border)] px-4 py-2.5">
                  <p className="mb-1 text-[12px] font-semibold text-[var(--text-secondary)]">{g.files.length} cópias · {f.bytes(g.size)} cada</p>
                  {g.files.map((x) => (
                    <div key={x.path} className="flex items-center gap-3 py-1">
                      <Checkbox checked={sel.has(x.path)} onChange={(v) => setSel((s) => { const n = new Set(s); v ? n.add(x.path) : n.delete(x.path); return n; })} />
                      <span className="mono min-w-0 flex-1 truncate text-[12px]" title={x.path}>{x.path}</span>
                      <span className="text-[11.5px] text-[var(--text-tertiary)]">{f.dateShort(x.modified)}</span>
                      <IconButton icon="folder_open" label="Abrir local" onClick={() => call("app.reveal", { path: x.path })} />
                    </div>
                  ))}
                  {allChosen(g) && <p className="text-[11.5px] text-[var(--color-warning)]">Todas as cópias deste grupo estão marcadas — nenhuma vai sobrar.</p>}
                </div>
              ))}
            </div>
          )}
        </Panel>
      )}
      <Confirm open={confirm} title={`Enviar ${sel.size} cópias para a Lixeira?`} tone="danger" icon="delete" confirmLabel="Enviar para a Lixeira" onClose={() => setConfirm(false)} onConfirm={recycle}>Você ainda pode recuperar pela Lixeira.</Confirm>
    </div>
  );
}
