import { useState } from "react";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useRpc } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Chip, Confirm, Empty, ErrorBox, Icon, IconButton, Panel, Skeleton } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

const KIND = { registro: ["account_tree", "Registro"], atalhos: ["link", "Atalhos"], drivers: ["developer_board", "Drivers"], outro: ["inventory_2", "Outro"] };

export default function Rescue() {
  const { toast, info } = useApp();
  const { data, setData, error, loading, reload } = useRpc("rescue.list");
  const [busy, setBusy] = useState(null);
  const [confirm, setConfirm] = useState(null);
  const create = async () => {
    setBusy("create");
    try { await call("rescue.createPoint", { description: "RAGE! - ponto manual" }); toast("Ponto de restauração criado", "success"); reload(true); } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const doConfirm = async () => {
    const c = confirm; setConfirm(null); setBusy(c.file);
    try {
      if (c.action === "restore") { await call("rescue.restoreBackup", { file: c.file }); toast("Backup restaurado", "success"); }
      else { await call("rescue.deleteBackup", { file: c.file }); toast("Backup apagado"); setData((d) => ({ ...d, backups: d.backups.filter((b) => b.file !== c.file) })); }
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  return (
    <div className="space-y-4">
      <PageIntro icon="restore" title="Central de resgate" actions={<><Button icon="history" onClick={() => call("app.open", { target: "rstrui.exe" }).catch((e) => toast(e.message, "danger"))}>Restauração do sistema</Button><Button variant="brand" icon="add" loading={busy === "create"} disabled={!info?.isAdmin} onClick={create}>Criar ponto agora</Button></>}>
        Tudo o que o RAGE! muda tem volta: pontos de restauração do Windows antes das correções e backups de cada entrada de registro e atalho removidos.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      <div className="grid gap-4 xl:grid-cols-2">
        <Panel title="Pontos de restauração" icon="settings_backup_restore" padded={false}>
          {loading && !data ? <div className="p-4"><Skeleton className="h-40" /></div> : !data?.points?.length ? <Empty icon="settings_backup_restore" title="Nenhum ponto de restauração">A Proteção do Sistema pode estar desligada. Crie um ponto agora.</Empty> : (
            <div className="divide-y divide-[var(--border)]">
              {data.points.map((p) => (
                <div key={p.seq} className="flex items-center gap-3 px-4 py-2.5">
                  <Icon name="restore" size={20} className="text-[var(--text-tertiary)]" />
                  <div className="min-w-0 flex-1"><p className="truncate text-[13px] font-semibold">{p.description}</p><p className="text-[11.5px] text-[var(--text-tertiary)]">{f.dateTime(p.date)} · {p.type}</p></div>
                  {p.description?.startsWith("RAGE!") && <Chip tone="brand">RAGE!</Chip>}
                </div>
              ))}
            </div>
          )}
        </Panel>
        <Panel title="Backups do RAGE!" icon="inventory_2" padded={false} actions={<IconButton icon="folder_open" label="Abrir pasta de dados" onClick={() => call("app.openDataFolder")} />}>
          {loading && !data ? <div className="p-4"><Skeleton className="h-40" /></div> : !data?.backups?.length ? <Empty icon="inventory_2" title="Nenhum backup ainda">Eles aparecem aqui quando você corrige o registro ou remove atalhos.</Empty> : (
            <div className="scroll-area max-h-[460px] divide-y divide-[var(--border)] overflow-y-auto">
              {data.backups.map((b) => {
                const [icon, label] = KIND[b.kind] || KIND.outro;
                return (
                  <div key={b.file} className="flex items-center gap-3 px-4 py-2.5">
                    <Icon name={icon} size={20} className="text-[var(--text-tertiary)]" />
                    <div className="min-w-0 flex-1"><p className="truncate text-[13px] font-semibold" title={b.file}>{b.file.split("\\").pop()}</p><p className="text-[11.5px] text-[var(--text-tertiary)]">{label} · {f.dateTime(b.date)} · {f.bytes(b.size)}</p></div>
                    <Button size="sm" loading={busy === b.file} onClick={() => setConfirm({ ...b, action: "restore" })}>Restaurar</Button>
                    <IconButton icon="delete" label="Apagar backup" onClick={() => setConfirm({ ...b, action: "delete" })} />
                  </div>
                );
              })}
            </div>
          )}
        </Panel>
      </div>
      <Confirm open={!!confirm} title={confirm?.action === "restore" ? "Restaurar este backup?" : "Apagar este backup?"} tone={confirm?.action === "restore" ? "brand" : "danger"} icon={confirm?.action === "restore" ? "restore" : "delete"} confirmLabel={confirm?.action === "restore" ? "Restaurar" : "Apagar"} onConfirm={doConfirm} onClose={() => setConfirm(null)}>
        {confirm?.action === "restore" ? "As entradas salvas voltam para o lugar de antes." : "Não dá para desfazer depois."}
      </Confirm>
    </div>
  );
}
