import { useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useAction } from "../../lib/store.js";
import { Button, Checkbox, Empty, Panel, Progress } from "../../components/ui.jsx";
import { FolderBar, useFolder } from "./FileTools.jsx";

export default function EmptyFolders() {
  const { toast } = useApp();
  const [folder, setFolder] = useFolder("empty", "C:\\Users");
  const a = useAction();
  const [list, setList] = useState(null);
  const [sel, setSel] = useState(new Set());
  const scan = async () => {
    setList(null);
    try { const r = await a.run("files.empty", { root: folder }); setList(r); setSel(new Set(r)); } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  const remove = async () => {
    try { const r = await call("files.recycle", { paths: [...sel] }); toast(`${r?.ok ?? sel.size} pastas removidas`, "success"); setList((l) => l.filter((p) => !sel.has(p))); setSel(new Set()); } catch (e) { toast(e.message, "danger"); }
  };
  return (
    <div className="space-y-4">
      <FolderBar folder={folder} setFolder={setFolder} disabled={a.running}>
        {a.running ? <Button icon="stop" onClick={a.cancel}>Parar</Button> : <Button variant="brand" icon="search" onClick={scan}>Procurar</Button>}
      </FolderBar>
      {a.running && <Panel><Progress indeterminate /></Panel>}
      {list && (
        <Panel padded={false} title={`${list.length} pastas vazias`} actions={sel.size > 0 && <Button size="sm" variant="danger" icon="delete" onClick={remove}>Remover {sel.size}</Button>}>
          {list.length === 0 ? <Empty icon="folder_off" title="Nenhuma pasta vazia" /> : (
            <div className="scroll-area max-h-[calc(100vh-280px)] divide-y divide-[var(--border)] overflow-y-auto">
              {list.map((p) => (
                <label key={p} className="flex cursor-pointer items-center gap-3 px-4 py-2 hover:bg-[var(--bg-subtle)]">
                  <Checkbox checked={sel.has(p)} onChange={(v) => setSel((s) => { const n = new Set(s); v ? n.add(p) : n.delete(p); return n; })} />
                  <span className="mono truncate text-[12px]" title={p}>{p}</span>
                </label>
              ))}
            </div>
          )}
        </Panel>
      )}
    </div>
  );
}
