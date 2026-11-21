import { useEffect, useState } from "react";
import { useApp } from "../App.jsx";
import { useRpc } from "../lib/store.js";
import { ScanFlow } from "../components/ScanFlow.jsx";
import { Icon, Skeleton, Toggle, cx, Button } from "../components/ui.jsx";
import { DialButton } from "../components/Charts.jsx";

export default function SmartScan() {
  const { data: cats } = useRpc("care.categories");
  const { info, settings, updateSettings } = useApp();
  const [picked, setPicked] = useState(null);
  useEffect(() => {
    if (!cats || picked) return;
    let saved = null;
    try { saved = JSON.parse(localStorage.getItem("smartscan.cats") || "null"); } catch { /* */ }
    setPicked(new Set(saved?.length ? saved.filter((id) => cats.some((c) => c.id === id)) : cats.filter((c) => c.defaultOn).map((c) => c.id)));
  }, [cats, picked]);
  const toggle = (id, v) => setPicked((p) => {
    const n = new Set(p); v ? n.add(id) : n.delete(id);
    try { localStorage.setItem("smartscan.cats", JSON.stringify([...n])); } catch { /* */ }
    return n;
  });
  if (!cats || !picked) return <Skeleton className="h-96" />;
  const meta = cats.filter((c) => picked.has(c.id));

  return (
    <ScanFlow
      categories={[...picked]}
      meta={meta}
      fixLabel="Corrigir tudo"
      idle={(scan) => (
        <div className="grid gap-4 xl:grid-cols-[1fr_380px]">
          <section className="panel relative flex flex-col items-center justify-center overflow-hidden px-6 py-12 text-center">
            <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(420px_circle_at_50%_35%,#ff8a3a1c,transparent_65%)]" />
            <DialButton size={224} n={60} tickLen={13} tone="hot" icon="health_and_safety" label="Verificar" sub={`${picked.size} ${picked.size === 1 ? "categoria" : "categorias"}`} onClick={scan} disabled={!picked.size} className="relative" />
            <h2 className="heading mt-8 text-[22px] font-semibold">Um clique para limpar e acelerar o PC</h2>
            <p className="mt-1.5 max-w-lg text-[13px] text-[var(--text-secondary)]">Encontra lixo, rastros, entradas inválidas, ajustes de desempenho e brechas de segurança. Nada é alterado sem você revisar antes.</p>
            <label className="mt-5 flex items-center gap-2.5 text-[13px]">
              <Toggle checked={settings?.restorePointBeforeFix} onChange={(v) => updateSettings({ restorePointBeforeFix: v })} />
              Criar ponto de restauração antes de corrigir registro e ajustes
            </label>
            {!info?.isAdmin && <p className="mt-3 text-[12px] text-[var(--color-warning)]">Sem administrador parte da verificação fica de fora.</p>}
          </section>
          <section className="panel p-2">
            <p className="px-3 pt-3 pb-2 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">O que verificar</p>
            {cats.map((c) => (
              <label key={c.id} className="flex cursor-pointer items-center gap-3 rounded-[var(--radius-sm)] px-3 py-2 hover:bg-[var(--bg-subtle)]">
                <span className={cx("flex size-8 items-center justify-center rounded-[var(--radius-sm)]", picked.has(c.id) ? "bg-[var(--brand-primary-soft)] text-[var(--brand)]" : "bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]")}><Icon name={c.icon} size={18} /></span>
                <div className="min-w-0 flex-1">
                  <p className="text-[13px] font-semibold">{c.title}</p>
                  <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{c.description}</p>
                </div>
                <Toggle size="sm" checked={picked.has(c.id)} onChange={(v) => toggle(c.id, v)} />
              </label>
            ))}
            <div className="flex gap-2 px-3 pt-2 pb-2">
              <Button size="sm" variant="ghost" onClick={() => cats.forEach((c) => toggle(c.id, true))}>Marcar todas</Button>
              <Button size="sm" variant="ghost" onClick={() => cats.forEach((c) => toggle(c.id, c.defaultOn))}>Padrão</Button>
            </div>
          </section>
        </div>
      )}
    />
  );
}
