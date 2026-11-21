import { useMemo, useState } from "react";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useRpc } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Chip, Confirm, Empty, ErrorBox, Icon, IconButton, Menu, Panel, Skeleton, Tabs, Toggle, cx } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

const TYPE_LABEL = { registry: "Registro", folder: "Pasta Inicializar", task: "Tarefa agendada", service: "Serviço" };

function BootCard({ boot }) {
  if (!boot?.lastSec) return null;
  const hist = (boot.history || []).slice(-10);
  const max = Math.max(...hist.map((h) => h.sec), boot.lastSec);
  return (
    <Panel className="xl:col-span-1" title="Tempo de inicialização" icon="timer" subtitle="Medido pelo próprio Windows">
      <p className="heading text-[34px] font-semibold tabular">{f.n(boot.lastSec, 1)} <span className="text-[15px] text-[var(--text-secondary)]">s</span></p>
      <p className="text-[12px] text-[var(--text-tertiary)]">último boot</p>
      {hist.length > 1 && (
        <div className="mt-4 flex h-16 items-end gap-1.5">
          {hist.map((h, i) => (
            <div key={i} title={`${f.dateShort(h.at)}: ${f.n(h.sec, 1)} s`} className="flex-1 rounded-t-[3px]" style={{ height: `${(h.sec / max) * 100}%`, background: i === hist.length - 1 ? "var(--brand-gradient)" : "var(--bg-layer-3)" }} />
          ))}
        </div>
      )}
    </Panel>
  );
}

export default function Startup() {
  const { toast, info } = useApp();
  const { data, setData, error, loading, reload } = useRpc("startup.list");
  const [tab, setTab] = useState("apps");
  const [busy, setBusy] = useState(null);
  const [confirm, setConfirm] = useState(null);
  const items = data?.items || [];
  const groups = { apps: items.filter((i) => i.type === "registry" || i.type === "folder"), task: items.filter((i) => i.type === "task"), service: items.filter((i) => i.type === "service") };
  const list = useMemo(() => [...(groups[tab] || [])].sort((a, b) => (b.enabled - a.enabled) || ((b.recommendation === "desativar") - (a.recommendation === "desativar")) || a.name.localeCompare(b.name)), [data, tab]); // eslint-disable-line react-hooks/exhaustive-deps
  const recommended = items.filter((i) => i.enabled && i.recommendation === "desativar");

  const setEnabled = async (item, enabled) => {
    setBusy(item.id);
    try {
      const r = await call("startup.set", { id: item.id, enabled });
      setData((d) => ({ ...d, items: d.items.map((x) => (x.id === item.id ? { ...x, ...(r || {}), enabled } : x)) }));
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const disableRecommended = async () => {
    setBusy("all");
    let ok = 0;
    for (const it of recommended) { try { await call("startup.set", { id: it.id, enabled: false }); ok++; } catch { /* */ } }
    toast(`${ok} programas não abrem mais com o Windows`, "success");
    await reload(true);
    setBusy(null);
  };
  const remove = async () => {
    const it = confirm; setConfirm(null);
    try { await call("startup.remove", { id: it.id }); toast(`${it.name} removido da inicialização (backup salvo)`, "success"); reload(true); } catch (e) { toast(e.message, "danger"); }
  };

  return (
    <div className="space-y-4">
      <PageIntro icon="rocket" title="O que abre junto com o Windows" actions={recommended.length > 0 && <Button variant="brand" icon="bolt" loading={busy === "all"} onClick={disableRecommended}>Desativar {recommended.length} recomendados</Button>}>
        Cada programa na inicialização deixa o boot mais lento e consome RAM o dia todo. Desativar não desinstala nada — dá para religar quando quiser.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      <div className="grid gap-4 xl:grid-cols-[300px_1fr]">
        <div className="space-y-4">
          <BootCard boot={data?.boot} />
          <Panel title="Resumo" icon="insights">
            {[["Ativos", items.filter((i) => i.enabled).length], ["Desativados", items.filter((i) => !i.enabled).length], ["Impacto alto", items.filter((i) => i.enabled && i.impact === "alto").length], ["Recomendados p/ desativar", recommended.length]].map(([l, v]) => (
              <div key={l} className="flex items-center justify-between py-1.5 text-[13px]"><span className="text-[var(--text-tertiary)]">{l}</span><span className="font-semibold tabular">{loading ? "…" : v}</span></div>
            ))}
            {!info?.isAdmin && <p className="mt-2 text-[11.5px] text-[var(--color-warning)]">Itens de todos os usuários, tarefas e serviços exigem administrador.</p>}
          </Panel>
        </div>
        <Panel padded={false} title={<Tabs value={tab} onChange={setTab} items={[{ value: "apps", label: "Programas", badge: groups.apps.length }, { value: "task", label: "Tarefas agendadas", badge: groups.task.length }, { value: "service", label: "Serviços", badge: groups.service.length }]} />} actions={<IconButton icon="refresh" label="Atualizar" onClick={() => reload()} />}>
          {loading && !data ? <div className="p-4"><Skeleton className="h-72" /></div> : list.length === 0 ? <Empty icon="rocket" title="Nada aqui">Nenhum item nesta categoria.</Empty> : (
            <div className="divide-y divide-[var(--border)]">
              {list.map((it) => (
                <div key={it.id} className={cx("flex items-center gap-3 px-4 py-2.5", !it.enabled && "opacity-60")}>
                  {it.icon ? <img src={it.icon} alt="" className="size-8 shrink-0 rounded-[var(--radius-sm)]" /> : <span className="flex size-8 shrink-0 items-center justify-center rounded-[var(--radius-sm)] bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]"><Icon name={it.type === "service" ? "settings_applications" : it.type === "task" ? "event_repeat" : "apps"} size={18} /></span>}
                  <div className="min-w-0 flex-1">
                    <p className="flex flex-wrap items-center gap-1.5 text-[13px] font-semibold">
                      {it.name}
                      {it.recommendation === "desativar" && it.enabled && <Chip tone="brand">Desativar</Chip>}
                      {it.impact && <Chip tone={it.impact === "alto" ? "danger" : it.impact === "médio" ? "warning" : "neutral"}>impacto {it.impact}</Chip>}
                    </p>
                    <p className="truncate text-[11.5px] text-[var(--text-tertiary)]" title={it.command}>{it.publisher || "Fabricante desconhecido"} · {it.location || TYPE_LABEL[it.type]}{it.reason ? ` · ${it.reason}` : ""}</p>
                  </div>
                  {busy === it.id ? <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" /> : <Toggle checked={it.enabled} onChange={(v) => setEnabled(it, v)} label={it.enabled ? "Desativar" : "Ativar"} />}
                  <Menu align="end" trigger={({ toggle }) => <IconButton icon="more_vert" label="Mais" onClick={toggle} />} items={[
                    { icon: "folder_open", label: "Abrir local do arquivo", onClick: () => call("startup.reveal", { id: it.id }).catch((e) => toast(e.message, "danger")) },
                    (it.type === "registry" || it.type === "folder") && { icon: "delete", label: "Remover da inicialização", danger: true, onClick: () => setConfirm(it) },
                  ]} />
                </div>
              ))}
            </div>
          )}
        </Panel>
      </div>
      <Confirm open={!!confirm} title={`Remover ${confirm?.name}?`} tone="danger" icon="delete" confirmLabel="Remover" onConfirm={remove} onClose={() => setConfirm(null)}>
        A entrada será removida da inicialização (o programa continua instalado). Um backup fica na Central de resgate.
      </Confirm>
    </div>
  );
}
