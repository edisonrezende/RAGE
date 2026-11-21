import { useMemo, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useAction } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, Checkbox, Confirm, Empty, IconButton, Panel, Progress, Segmented } from "../../components/ui.jsx";
import { FolderBar, useFolder } from "./FileTools.jsx";

export default function LargeFiles() {
  const { toast } = useApp();
  const [folder, setFolder] = useFolder("large");
  const [min, setMin] = useState(500);
  const a = useAction();
  const [list, setList] = useState(null);
  const [sel, setSel] = useState(new Set());
  const [confirm, setConfirm] = useState(false);
  const scan = async () => {
    setList(null); setSel(new Set());
    try { setList(await a.run("files.large", { root: folder, minSize: min * 1048576, limit: 300 })); } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  const recycle = async () => {
    setConfirm(false);
    try {
      const r = await call("files.recycle", { paths: [...sel] });
      toast(`${r?.ok ?? sel.size} arquivos enviados para a Lixeira`, "success");
      setList((l) => l.filter((x) => !sel.has(x.path))); setSel(new Set());
    } catch (e) { toast(e.message, "danger"); }
  };
  const selSize = useMemo(() => (list || []).filter((x) => sel.has(x.path)).reduce((s, x) => s + x.size, 0), [list, sel]);
  return (
    <div className="space-y-4">
      <FolderBar folder={folder} setFolder={setFolder} disabled={a.running}>
        <Segmented value={min} onChange={setMin} options={[100, 500, 1024, 4096].map((v) => ({ value: v, label: v >= 1024 ? `${v / 1024} GB+` : `${v} MB+` }))} />
        {a.running ? <Button icon="stop" onClick={a.cancel}>Parar</Button> : <Button variant="brand" icon="search" onClick={scan}>Procurar</Button>}
      </FolderBar>
      {a.running && <Panel><p className="mb-2 truncate text-[12.5px] text-[var(--text-tertiary)]">{f.n(a.progress?.scanned)} arquivos verificados · {a.progress?.current}</p><Progress indeterminate /></Panel>}
      {list && (
        <Panel padded={false} title={`${list.length} arquivos · ${f.bytes(list.reduce((s, x) => s + x.size, 0))}`} actions={sel.size > 0 && <Button size="sm" variant="danger" icon="delete" onClick={() => setConfirm(true)}>Lixeira ({f.bytes(selSize)})</Button>}>
          {list.length === 0 ? <Empty icon="folder_zip" title="Nenhum arquivo grande aqui" /> : (
            <div className="scroll-area max-h-[calc(100vh-300px)] divide-y divide-[var(--border)] overflow-y-auto">
              {list.map((x) => (
                <div key={x.path} className="flex items-center gap-3 px-4 py-2 hover:bg-[var(--bg-subtle)]">
                  <Checkbox checked={sel.has(x.path)} onChange={(v) => setSel((s) => { const n = new Set(s); v ? n.add(x.path) : n.delete(x.path); return n; })} />
                  <div className="min-w-0 flex-1"><p className="truncate text-[13px] font-medium">{x.path.split("\\").pop()}</p><p className="mono truncate text-[11px] text-[var(--text-tertiary)]" title={x.path}>{x.path}</p></div>
                  <span className="text-[11.5px] text-[var(--text-tertiary)]">{f.dateShort(x.modified)}</span>
                  <span className="w-20 text-right text-[13px] font-semibold tabular">{f.bytes(x.size)}</span>
                  <IconButton icon="folder_open" label="Abrir local" onClick={() => call("app.reveal", { path: x.path })} />
                </div>
              ))}
            </div>
          )}
        </Panel>
      )}
      <Confirm open={confirm} title={`Enviar ${sel.size} arquivos para a Lixeira?`} tone="danger" icon="delete" confirmLabel="Enviar para a Lixeira" onClose={() => setConfirm(false)} onConfirm={recycle}>Você ainda pode recuperar pela Lixeira até esvaziá-la.</Confirm>
    </div>
  );
}
