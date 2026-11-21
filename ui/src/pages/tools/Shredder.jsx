import { useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useAction } from "../../lib/store.js";
import { Button, Confirm, Empty, Icon, IconButton, Panel, Progress, Segmented } from "../../components/ui.jsx";
import { PageIntro } from "../../components/Shell.jsx";

export default function Shredder() {
  const { toast } = useApp();
  const [paths, setPaths] = useState([]);
  const [passes, setPasses] = useState(1);
  const [confirm, setConfirm] = useState(false);
  const a = useAction();
  const addFiles = async () => { const r = await call("app.pickFiles", { title: "Arquivos para triturar", multi: true }); if (r?.length) setPaths((p) => [...new Set([...p, ...r])]); };
  const addFolder = async () => { const r = await call("app.pickFolder", { title: "Pasta para triturar" }); if (r) setPaths((p) => [...new Set([...p, r])]); };
  const shred = async () => {
    setConfirm(false);
    try { const r = await a.run("files.shred", { paths, passes }); toast(`${r?.ok ?? 0} itens triturados${r?.failed ? ` · ${r.failed} falharam` : ""}`, r?.failed ? "warning" : "success"); setPaths([]); }
    catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  return (
    <div className="space-y-4">
      <PageIntro icon="content_cut" title="Triturador de arquivos" actions={<><Button icon="note_add" onClick={addFiles} disabled={a.running}>Adicionar arquivos</Button><Button icon="create_new_folder" onClick={addFolder} disabled={a.running}>Adicionar pasta</Button></>}>
        Sobrescreve o conteúdo com dados aleatórios antes de apagar — nenhum programa de recuperação consegue trazer de volta. Use para documentos sensíveis.
      </PageIntro>
      <Panel padded={false} title={`${paths.length} itens`} actions={<div className="flex items-center gap-2"><span className="text-[12px] text-[var(--text-tertiary)]">Passadas</span><Segmented value={passes} onChange={setPasses} options={[{ value: 1, label: "1 (rápido)" }, { value: 3, label: "3" }, { value: 7, label: "7 (paranoico)" }]} /></div>}>
        {paths.length === 0 ? <Empty icon="content_cut" title="Nada para triturar">Adicione arquivos ou pastas.</Empty> : (
          <div className="divide-y divide-[var(--border)]">
            {paths.map((p) => (
              <div key={p} className="flex items-center gap-3 px-4 py-2">
                <Icon name="description" size={18} className="text-[var(--text-tertiary)]" />
                <span className="mono min-w-0 flex-1 truncate text-[12px]" title={p}>{p}</span>
                <IconButton icon="close" label="Tirar da lista" onClick={() => setPaths((l) => l.filter((x) => x !== p))} disabled={a.running} />
              </div>
            ))}
          </div>
        )}
      </Panel>
      {a.running && <Panel><p className="mb-2 truncate text-[12.5px] text-[var(--text-tertiary)]">{a.progress?.current || "Triturando…"}</p><Progress value={a.progress?.pct} indeterminate={a.progress?.pct == null} /></Panel>}
      <div className="flex justify-end"><Button variant="danger" size="lg" icon="content_cut" disabled={!paths.length || a.running} onClick={() => setConfirm(true)}>Triturar para sempre</Button></div>
      <Confirm open={confirm} title="Triturar definitivamente?" tone="danger" icon="warning" confirmLabel="Triturar" onClose={() => setConfirm(false)} onConfirm={shred}>
        {paths.length} {paths.length === 1 ? "item será destruído" : "itens serão destruídos"} sem passar pela Lixeira. Não há como desfazer.
      </Confirm>
    </div>
  );
}
