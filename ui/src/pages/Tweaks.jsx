import { useMemo, useState } from "react";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useRpc } from "../lib/store.js";
import { Button, Chip, ErrorBox, Icon, Panel, Skeleton, Tabs, Toggle, cx } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

const CATS = [
  { value: "gaming", label: "Jogos", icon: "sports_esports", desc: "Menos latência e mais FPS: Game DVR, agendamento de GPU, mouse 1:1, prioridade de jogos." },
  { value: "system", label: "Sistema", icon: "computer", desc: "Windows mais ágil: menus, desligamento, NTFS e hibernação." },
  { value: "privacy", label: "Privacidade e anúncios", icon: "visibility_off", desc: "Menos telemetria, sem anúncios, sugestões e Bing no Menu Iniciar." },
  { value: "internet", label: "Internet", icon: "network_check", desc: "TCP, limitação de rede e Nagle." },
];

export default function Tweaks() {
  const { toast, info } = useApp();
  const [cat, setCat] = useState("gaming");
  const { data, setData, error, loading, reload } = useRpc("tweaks.list");
  const [busy, setBusy] = useState(null);
  const list = useMemo(() => (data || []).filter((t) => t.category === cat), [data, cat]);
  const pending = list.filter((t) => t.recommended && !t.applied && !t.oneShot);
  const count = (c) => (data || []).filter((t) => t.category === c && t.recommended && !t.applied && !t.oneShot).length;

  const apply = async (ids, on) => {
    setBusy(ids.length > 1 ? "all" : ids[0]);
    try {
      const r = await call(on ? "tweaks.apply" : "tweaks.revert", { ids });
      if (r?.items) setData(r.items);
      const fails = (r?.results || []).filter((x) => !x.ok);
      if (fails.length) toast(fails[0].message || "Alguns ajustes não puderam ser aplicados", "danger");
      else toast(on ? (ids.length > 1 ? `${ids.length} ajustes aplicados` : "Ajuste aplicado") : "Ajuste revertido", "success");
      if (on && (r?.results || []).some((x) => x.ok && x.needsReboot)) toast("Alguns ajustes só valem depois de reiniciar o PC", "warning");
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const cur = CATS.find((c) => c.value === cat);

  return (
    <div className="space-y-4">
      <PageIntro icon="tune" title="Ajustes do sistema">
        Cada ajuste mostra o que faz e pode ser desfeito a qualquer momento — o RAGE! guarda o valor original. Os marcados como recomendados são seguros para a maioria dos PCs.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      <div className="flex flex-wrap items-center gap-3">
        <Tabs size="md" value={cat} onChange={setCat} items={CATS.map((c) => ({ value: c.value, label: c.label, icon: c.icon, badge: count(c.value) || null }))} />
        {pending.length > 0 && <Button variant="brand" className="ml-auto" icon="auto_fix_high" loading={busy === "all"} disabled={!info?.isAdmin && cat !== "privacy"} onClick={() => apply(pending.map((t) => t.id), true)}>Aplicar {pending.length} recomendados</Button>}
      </div>
      <Panel padded={false} title={cur.label} subtitle={cur.desc} icon={cur.icon}>
        {loading && !data ? <div className="p-4"><Skeleton className="h-64" /></div> : (
          <div className="divide-y divide-[var(--border)]">
            {list.map((t) => (
              <div key={t.id} className="flex items-center gap-4 px-4 py-3">
                <span className={cx("flex size-9 shrink-0 items-center justify-center rounded-[var(--radius-md)]", t.applied ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : "bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]")}>
                  <Icon name={t.applied ? "check" : "tune"} size={18} />
                </span>
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-center gap-1.5 text-[13.5px] font-semibold">
                    {t.title}
                    {t.recommended && !t.applied && <Chip tone="brand">Recomendado</Chip>}
                    {t.risk === "caution" && <Chip tone="warning">Avançado</Chip>}
                    {t.needsReboot && <Chip>Requer reinício</Chip>}
                  </p>
                  <p className="text-[12.5px] text-[var(--text-tertiary)]">{t.desc}{t.valueText ? ` · ${t.valueText}` : ""}</p>
                  {t.note && <p className="text-[12px] text-[var(--color-warning)]">{t.note}</p>}
                </div>
                {busy === t.id ? <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" />
                  : t.oneShot ? <Button size="sm" onClick={() => apply([t.id], true)}>Executar</Button>
                  : <Toggle checked={t.applied} onChange={(v) => apply([t.id], v)} />}
              </div>
            ))}
            {list.length === 0 && <p className="px-4 py-8 text-center text-[13px] text-[var(--text-tertiary)]">Nenhum ajuste nesta categoria.</p>}
          </div>
        )}
      </Panel>
    </div>
  );
}
