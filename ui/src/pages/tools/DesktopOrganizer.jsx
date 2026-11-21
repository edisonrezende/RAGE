import { useEffect, useMemo, useRef, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useRpc } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { defaultRows, planDesktop } from "../../lib/deskLayout.js";
import { Button, Checkbox, Chip, Confirm, Empty, ErrorBox, Icon, IconButton, Panel, Segmented, Skeleton, Toggle, cx } from "../../components/ui.jsx";

const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const DAY = 864e5;
const AGES = [{ value: 0, label: "À mão" }, { value: 30, label: "1 mês" }, { value: 90, label: "3 meses" }, { value: 180, label: "6 meses" }, { value: 365, label: "1 ano" }];
const KIND_LABEL = { folder: "Pasta", shortcut: "Atalho" };

function ageText(date) {
  const days = Math.floor((Date.now() - new Date(date).getTime()) / DAY);
  if (!Number.isFinite(days)) return "—";
  if (days < 1) return "hoje";
  if (days < 30) return `há ${days} ${days === 1 ? "dia" : "dias"}`;
  if (days < 365) { const m = Math.floor(days / 30); return `há ${m} ${m === 1 ? "mês" : "meses"}`; }
  const y = Math.floor(days / 365);
  return `há ${y} ${y === 1 ? "ano" : "anos"}`;
}

/** Miniatura da tela: cada ícone desliza da posição atual para a posição proposta. */
function DesktopPreview({ items, grid, plan, view }) {
  const planned = view === "plan";
  return (
    <div className="desk-preview" style={{ aspectRatio: `${grid.cols * grid.cellW} / ${grid.rows * grid.cellH}` }} role="img"
      aria-label={planned ? "Prévia da área de trabalho organizada" : "Área de trabalho como está agora"}>
      <span className="desk-block" data-on={planned && plan.apps > 0} style={{ width: `${(plan.blockCols / grid.cols) * 100}%`, height: `${(plan.rows / grid.rows) * 100}%` }} />
      {items.map((it, i) => {
        const p = planned ? plan.byId.get(it.id) : null;
        const col = clamp(p ? p.col : it.col, 0, grid.cols - 1), row = clamp(p ? p.row : it.row, 0, grid.rows - 1);
        return (
          <span key={it.id} title={it.name} className="desk-icon"
            style={{ left: `${((col + 0.5) / grid.cols) * 100}%`, top: `${((row + 0.5) / grid.rows) * 100}%`, width: `${(100 / grid.cols) * 0.6}%`, transitionDelay: `${Math.min(i, 40) * 10}ms` }}>
            {it.icon ? <img src={it.icon} alt="" draggable={false} /> : <span className="desk-tile" style={{ background: it.color }}>{it.name.slice(0, 1)}</span>}
          </span>
        );
      })}
    </div>
  );
}

function Stepper({ value, min, max, onChange, unit }) {
  const btn = "flex size-7 items-center justify-center rounded-[var(--radius-sm)] text-[var(--text-secondary)] transition-colors hover:bg-[var(--bg-layer-2)] hover:text-[var(--text-primary)] disabled:opacity-35 disabled:pointer-events-none";
  return (
    <div className="inline-flex items-center gap-1 rounded-[var(--radius-md)] border border-[var(--panel-border)] p-0.5">
      <button className={btn} onClick={() => onChange(value - 1)} disabled={value <= min} aria-label="Diminuir"><Icon name="remove" size={16} /></button>
      <span className="min-w-[66px] text-center text-[12.5px] font-semibold tabular">{value} {unit}</span>
      <button className={btn} onClick={() => onChange(value + 1)} disabled={value >= max} aria-label="Aumentar"><Icon name="add" size={16} /></button>
    </div>
  );
}

function LooseFiles({ data, onChanged }) {
  const { toast } = useApp();
  const files = useMemo(() => data.items.filter((i) => i.path && (i.kind === "file" || i.kind === "folder" || i.kind === "shortcut"))
    .sort((a, b) => new Date(a.modified || 0) - new Date(b.modified || 0)), [data]);
  const [age, setAge] = useState(0);
  const [sel, setSel] = useState(() => new Set());
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const locked = (x) => x.common && !data.admin;

  const pickAge = (days) => {
    setAge(days);
    // Só arquivos entram na marcação automática; pastas e atalhos ficam por conta de quem usa.
    setSel(new Set(days ? files.filter((x) => x.kind === "file" && !locked(x) && x.modified && Date.now() - new Date(x.modified).getTime() > days * DAY).map((x) => x.path) : []));
  };
  const toggle = (path, on) => setSel((s) => { const n = new Set(s); on ? n.add(path) : n.delete(path); return n; });
  const chosen = files.filter((x) => sel.has(x.path));
  const size = chosen.reduce((s, x) => s + (x.size || 0), 0);

  const recycle = async () => {
    setConfirm(false); setBusy(true);
    try {
      const r = await call("files.recycle", { paths: chosen.map((x) => x.path) });
      const ok = r?.ok ?? chosen.length;
      toast(r?.failed ? `${ok} na Lixeira, ${r.failed} não puderam ser movidos` : `${f.plural(ok, "item enviado", "itens enviados")} para a Lixeira`, r?.failed ? "warning" : "success");
      setSel(new Set()); setAge(0);
      await onChanged();
    } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };

  return (
    <Panel padded={false} title="Arquivos soltos" icon="draft"
      subtitle="Tudo o que não é atalho de programa. Nada é apagado de vez: vai para a Lixeira."
      actions={chosen.length > 0 && <Button size="sm" variant="danger" icon="delete" loading={busy} onClick={() => setConfirm(true)}>Lixeira · {chosen.length}{size > 0 ? ` · ${f.bytes(size)}` : ""}</Button>}>
      {files.length === 0 ? (
        <Empty icon="task_alt" title="Nenhum arquivo solto">Na área de trabalho só há atalhos de programas e itens do Windows.</Empty>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-3 border-t border-[var(--panel-border)] px-4 py-3">
            <span className="text-[12.5px] text-[var(--text-secondary)]">Marcar arquivos sem mudança há mais de</span>
            <Segmented value={age} onChange={pickAge} options={AGES} />
            <span className="ml-auto text-[12px] text-[var(--text-tertiary)]">{f.plural(files.length, "item", "itens")} na área de trabalho</span>
          </div>
          <div className="scroll-area max-h-[420px] overflow-y-auto">
            {files.map((x) => (
              <label key={x.path} className={cx("flex items-center gap-3 border-t border-[var(--panel-border)] px-4 py-2 transition-colors", locked(x) ? "opacity-60" : "cursor-pointer hover:bg-[var(--bg-subtle)]")}>
                <Checkbox checked={sel.has(x.path)} disabled={locked(x)} onChange={(v) => toggle(x.path, v)} />
                {x.icon ? <img src={x.icon} alt="" className="size-6 shrink-0" draggable={false} /> : <Icon name={x.isDir ? "folder" : "draft"} size={22} className="text-[var(--text-tertiary)]" />}
                <div className="min-w-0 flex-1">
                  <p className="truncate text-[13px] font-medium">{x.name}</p>
                  <p className="truncate text-[11.5px] text-[var(--text-tertiary)]">
                    {KIND_LABEL[x.kind] || (x.ext ? x.ext.toUpperCase() : "Arquivo")}{locked(x) ? " · de todos os usuários, precisa de administrador" : ""}
                  </p>
                </div>
                <span className="w-24 text-right text-[12px] text-[var(--text-secondary)]" title={x.modified ? f.dateTime(x.modified) : undefined}>{x.modified ? ageText(x.modified) : "—"}</span>
                <span className="w-20 text-right text-[13px] font-semibold tabular">{x.size != null ? f.bytes(x.size) : "—"}</span>
                <IconButton icon="folder_open" label="Abrir local" onClick={(e) => { e.preventDefault(); call("app.reveal", { path: x.path }); }} />
              </label>
            ))}
          </div>
        </>
      )}
      <Confirm open={confirm} title={`Enviar ${f.plural(chosen.length, "item", "itens")} para a Lixeira?`} tone="danger" icon="delete" confirmLabel="Enviar para a Lixeira"
        onClose={() => setConfirm(false)} onConfirm={recycle}>
        {chosen.some((x) => x.isDir) ? "Há pastas na seleção: elas vão com tudo o que têm dentro. " : ""}Dá para recuperar pela Lixeira até ela ser esvaziada.
      </Confirm>
    </Panel>
  );
}

export default function DesktopOrganizer() {
  const { toast } = useApp();
  const { data, error, reload } = useRpc("desktop.scan");
  const [view, setView] = useState("now");
  const [rows, setRows] = useState(null);
  const [groupVendors, setGroupVendors] = useState(true);
  const [busy, setBusy] = useState(null);
  const shown = useRef(false);

  // Na primeira leitura a prévia mostra a tela como está e, logo depois, os ícones deslizam para o arranjo proposto.
  useEffect(() => {
    if (!data || shown.current) return;
    const t = setTimeout(() => { shown.current = true; setView("plan"); }, 650);
    return () => clearTimeout(t);
  }, [data]);

  const plan = useMemo(() => (data ? planDesktop(data.items, data.grid, { rows: rows ?? undefined, groupVendors }) : null), [data, rows, groupVendors]);

  if (error) return <ErrorBox error={error} onRetry={reload} />;
  if (!data || !plan) {
    return (
      <div className="grid gap-4 xl:grid-cols-3">
        <Skeleton className="h-[420px] !rounded-[var(--radius-xl)] xl:col-span-2" />
        <Skeleton className="h-[420px] !rounded-[var(--radius-xl)]" />
      </div>
    );
  }

  const { grid } = data;
  const organized = plan.placements.every((p) => { const it = data.items.find((x) => x.id === p.id); return it && Math.round(it.col) === p.col && Math.round(it.row) === p.row; });

  const apply = async () => {
    setBusy("apply");
    try {
      const r = await call("desktop.apply", { items: plan.placements });
      toast(`${f.plural(r?.moved ?? plan.placements.length, "ícone organizado", "ícones organizados")}`, "brand");
      await reload(true);
      setView("plan");
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const restore = async () => {
    setBusy("restore");
    try {
      await call("desktop.restore");
      toast("Arranjo anterior restaurado", "success");
      await reload(true);
      setView("now");
    } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-4 xl:grid-cols-3">
        <section className="panel flex min-w-0 flex-col p-5 xl:col-span-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0">
              <h2 className="heading text-[16px] leading-tight font-semibold">Prévia da área de trabalho</h2>
              <p className="mt-1 text-[12.5px] text-[var(--text-tertiary)]">{f.plural(data.items.length, "ícone", "ícones")} numa grade de {grid.cols} × {grid.rows}. Nada muda no Windows até você mandar organizar.</p>
            </div>
            <Segmented value={view} onChange={setView} options={[{ value: "now", label: "Agora" }, { value: "plan", label: "Organizada" }]} />
          </div>
          <div className="mt-4"><DesktopPreview items={data.items} grid={grid} plan={plan} view={view} /></div>
          <div className="mt-4 flex flex-wrap items-center gap-x-5 gap-y-3 text-[12.5px]">
            <div className="flex items-center gap-2.5">
              <span className="text-[var(--text-secondary)]">Altura do bloco</span>
              <Stepper value={plan.rows} min={Math.min(2, grid.rows)} max={grid.rows} unit={plan.rows === 1 ? "linha" : "linhas"} onChange={(v) => { setRows(v); setView("plan"); }} />
              {rows != null && rows !== defaultRows(plan.apps, grid) && <button className="text-[12px] font-semibold text-[var(--text-tertiary)] underline underline-offset-4 hover:text-[var(--text-primary)]" onClick={() => setRows(null)}>Automática</button>}
            </div>
            <label className="flex cursor-pointer items-center gap-2.5">
              <Toggle checked={groupVendors} onChange={(v) => { setGroupVendors(v); setView("plan"); }} label="Manter apps do mesmo fornecedor juntos" />
              <span>Apps do mesmo fornecedor juntos</span>
            </label>
            <div className="flex flex-wrap items-center gap-1.5">
              {plan.vendors.map((v) => (
                <Chip key={v.key}><span className="size-2 rounded-full" style={{ background: v.color }} />{v.name} · {v.count}</Chip>
              ))}
            </div>
          </div>
        </section>

        {plan.apps === 0 ? (
          <section className="panel flex min-w-0 flex-col p-5">
            <p className="text-[12.5px] font-semibold text-[var(--text-secondary)]">Sem atalhos de programas</p>
            <p className="heading mt-3 text-[34px] leading-none font-semibold">Nada a agrupar</p>
            <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--text-secondary)]">O bloco em degradê é montado com atalhos de programas, e a área de trabalho não tem nenhum agora.</p>
          </section>
        ) : (
          <section className="hot-card flex min-w-0 flex-col p-5">
            <p className="text-[12.5px] font-semibold">{organized ? "Já está organizada" : "Pronto para organizar"}</p>
            <p className="mt-3 flex items-baseline gap-2">
              <span className="heading text-[34px] leading-none font-semibold tabular">{plan.apps} {plan.apps === 1 ? "app" : "apps"}</span>
              <span className="text-[15px] text-[var(--hot-ink-2)]">em {plan.blockCols} {plan.blockCols === 1 ? "coluna" : "colunas"}</span>
            </p>
            <p className="mt-3 text-[12.5px] leading-relaxed text-[var(--hot-ink-2)]">
              Os atalhos de programas viram um bloco em degradê de cor no canto de cima. Pastas, arquivos e a Lixeira descem para o canto de baixo, à direita.
            </p>
            <div className="mt-4 grid gap-2">
              <div className="hot-pill" data-dark="true"><span>Fornecedores juntos</span><b>{groupVendors && plan.vendors.length ? f.plural(plan.vendors.length, "grupo", "grupos") : "nenhum"}</b></div>
              <div className="hot-pill">
                <span>{grid.autoArrange ? "Organização automática" : "Arranjo de agora"}</span>
                <span>{grid.autoArrange ? "será desligada" : "fica guardado"}</span>
              </div>
            </div>
            {plan.unplaced > 0 && <p className="mt-3 text-[12px] text-[var(--hot-ink)]">{f.plural(plan.unplaced, "ícone não cabe", "ícones não cabem")} na tela e ficam onde estão.</p>}
            <div className="mt-auto pt-5">
              <button onClick={apply} disabled={!!busy || organized} className="hot-btn">
                {busy === "apply" ? <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" /> : <Icon name="auto_awesome_mosaic" size={19} fill />}
                {organized ? "Nada a mover" : "Organizar área de trabalho"}
              </button>
              {data.backup && (
                <button onClick={restore} disabled={!!busy} className="mt-3 block w-full text-center text-[12px] font-semibold underline underline-offset-4 disabled:opacity-50">
                  Desfazer a última organização ({f.dateTime(data.backup.at)})
                </button>
              )}
            </div>
          </section>
        )}
      </div>

      <LooseFiles data={data} onChanged={() => reload(true)} />
    </div>
  );
}
