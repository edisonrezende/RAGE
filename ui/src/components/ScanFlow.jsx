import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useAction } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Checkbox, Chip, Empty, Icon, cx } from "./ui.jsx";
import { TickRing, zoneColor } from "./Charts.jsx";

const WORK_COLOR = zoneColor(0, 1.01);

export const CATEGORY_ROUTE = { junk: "/limpeza", privacy: "/limpeza?tab=privacy", registry: "/registro", shortcuts: "/registro?tab=shortcuts", startup: "/inicializacao", tweaks: "/ajustes", internet: "/internet", security: "/seguranca", disk: "/disco", updates: "/ferramentas/atualizacoes", antivirus: "/antivirus" };

/** Resumo de uma categoria escaneada */
function catSummary(c, sel) {
  const items = c.items.filter((i) => i.fixable && i.status !== "ok");
  const chosen = items.filter((i) => sel.has(i.id));
  const size = chosen.reduce((a, i) => a + (i.size || 0), 0);
  return { items, chosen, size };
}

function ItemRow({ item, checked, onToggle }) {
  const fixable = item.fixable && item.status !== "ok";
  return (
    <div className={cx("group flex items-start gap-3 rounded-[var(--radius-sm)] px-3 py-2 hover:bg-[var(--bg-subtle)]", !fixable && "opacity-90")}>
      <div className="pt-0.5">
        {fixable ? <Checkbox checked={checked} onChange={onToggle} /> : (
          <span className={cx("flex size-[18px] items-center justify-center rounded-full", item.status === "ok" ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : item.status === "bad" ? "bg-[var(--color-danger-soft)] text-[var(--color-danger)]" : "bg-[var(--color-warning-soft)] text-[var(--color-warning)]")}>
            <Icon name={item.status === "ok" ? "check" : "priority_high"} size={13} />
          </span>
        )}
      </div>
      <div className="min-w-0 flex-1">
        <p className="flex flex-wrap items-center gap-1.5 text-[13px] font-medium">
          {item.title}
          {item.risk === "caution" && <Chip tone="warning">Atenção</Chip>}
          {item.needsReboot && <Chip>Reiniciar</Chip>}
        </p>
        {(item.detail || item.current) && (
          <p className="truncate text-[11.5px] text-[var(--text-tertiary)]" title={item.detail}>
            {item.current && item.recommended ? <>Atual: <b className="font-semibold text-[var(--text-secondary)]">{item.current}</b> → recomendado: <b className="font-semibold text-[var(--color-success)]">{item.recommended}</b>{item.detail ? ` · ${item.detail}` : ""}</> : item.detail}
          </p>
        )}
        {item.note && checked && <p className="mt-0.5 text-[11.5px] text-[var(--color-warning)]">{item.note}</p>}
        {item.note && !checked && fixable && <p className="mt-0.5 text-[11.5px] text-[var(--text-muted)]">{item.note}</p>}
      </div>
      <div className="shrink-0 text-right text-[12px] tabular">
        {item.size != null && item.size > 0 && <p className="font-semibold">{f.bytes(item.size)}</p>}
        {item.count != null && item.count > 0 && <p className="text-[var(--text-tertiary)]">{f.n(item.count)} {item.count === 1 ? "item" : "itens"}</p>}
        {!fixable && item.action && <button onClick={() => call("app.open", { target: item.action })} className="text-[11.5px] font-semibold text-[var(--text-link)] hover:underline">Abrir</button>}
      </div>
    </div>
  );
}

function CategoryBlock({ cat, sel, setSel, defaultOpen }) {
  const [open, setOpen] = useState(defaultOpen);
  const nav = useNavigate();
  const { items, chosen, size } = catSummary(cat, sel);
  const all = items.length > 0 && chosen.length === items.length;
  const some = chosen.length > 0 && !all;
  const toggleAll = (v) => setSel((s) => { const n = new Set(s); for (const i of items) v ? n.add(i.id) : n.delete(i.id); return n; });
  const groups = useMemo(() => {
    const m = new Map();
    for (const i of cat.items) { const g = i.group || ""; if (!m.has(g)) m.set(g, []); m.get(g).push(i); }
    return [...m.entries()];
  }, [cat]);
  const totalSize = cat.items.reduce((a, i) => a + (i.size || 0), 0);
  return (
    <div className="panel overflow-hidden">
      <div className="flex cursor-pointer items-center gap-3 px-4 py-3 hover:bg-[var(--bg-subtle)]" onClick={() => setOpen((o) => !o)}>
        {items.length > 0 ? <Checkbox checked={all} indeterminate={some} onChange={toggleAll} /> : <span className="flex size-[18px] items-center justify-center rounded-full bg-[var(--color-success-soft)] text-[var(--color-success)]"><Icon name="check" size={13} /></span>}
        <span className={cx("flex size-8 items-center justify-center rounded-[var(--radius-sm)]", items.length ? "bg-[var(--brand-primary-soft)] text-[var(--brand)]" : "bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]")}><Icon name={cat.icon || "check"} size={18} /></span>
        <div className="min-w-0 flex-1">
          <p className="text-[13.5px] font-semibold">{cat.title}</p>
          <p className="text-[12px] text-[var(--text-tertiary)]">
            {cat.error ? <span className="text-[var(--color-danger)]">Erro: {cat.error}</span> : items.length === 0 ? "Nada a corrigir" : `${chosen.length} de ${items.length} selecionados${totalSize > 0 ? ` · ${f.bytes(size)} de ${f.bytes(totalSize)}` : ""}`}
          </p>
        </div>
        {items.length > 0 && totalSize > 0 && <span className="heading text-[15px] font-semibold tabular">{f.bytes(totalSize)}</span>}
        {items.length > 0 && totalSize === 0 && <Chip tone="warning">{items.length} {items.length === 1 ? "item" : "itens"}</Chip>}
        {CATEGORY_ROUTE[cat.id] && <button onClick={(e) => { e.stopPropagation(); nav(CATEGORY_ROUTE[cat.id]); }} className="hidden text-[12px] font-semibold text-[var(--text-tertiary)] hover:text-[var(--text-primary)] md:block">Detalhes</button>}
        <Icon name="expand_more" size={20} className={cx("text-[var(--text-tertiary)] transition-transform", open && "rotate-180")} />
      </div>
      {open && cat.items.length > 0 && (
        <div className="scroll-area max-h-[420px] overflow-y-auto border-t border-[var(--border)] px-1 py-1.5">
          {groups.map(([g, list]) => (
            <div key={g}>
              {g && <p className="px-3 pt-2 pb-1 text-[10.5px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">{g}</p>}
              {list.map((it) => <ItemRow key={it.id} item={it} checked={sel.has(it.id)} onToggle={(v) => setSel((s) => { const n = new Set(s); v ? n.add(it.id) : n.delete(it.id); return n; })} />)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Fluxo completo: verificar → revisar → corrigir.
 * categories: ids das categorias (ou null = padrão do orquestrador). meta: [{id,title,icon}] para mostrar o progresso.
 */
export function ScanFlow({ categories, meta = [], autoScan = false, idle, emptyText = "Tudo limpo por aqui!", fixLabel = "Corrigir selecionados", onDone, compact }) {
  const { toast, info } = useApp();
  const scanA = useAction();
  const fixA = useAction();
  const [result, setResult] = useState(null);
  const [sel, setSel] = useState(new Set());
  const [done, setDone] = useState(null);
  const [scanned, setScanned] = useState({});
  const [scanStage, setScanStage] = useState(null);
  const [fixStage, setFixStage] = useState(null);
  const started = useRef(false);
  // Eventos {phase:"item"} trazem o item atual; as etapas (scan/scanned/fix/restore) controlam a barra.
  useEffect(() => { const p = scanA.progress; if (p && p.phase !== "item") setScanStage(p); }, [scanA.progress]);
  useEffect(() => { const p = fixA.progress; if (p && p.phase !== "item" && p.line == null) setFixStage(p); }, [fixA.progress]);

  const scan = async () => {
    setDone(null); setResult(null); setScanned({}); setScanStage(null);
    try {
      const r = await scanA.run("care.scan", { categories });
      setResult(r);
      setSel(new Set(r.categories.flatMap((c) => c.items.filter((i) => i.fixable && i.status !== "ok" && i.selected).map((i) => i.id))));
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  useEffect(() => { if (scanA.progress?.phase === "scanned") setScanned((s) => ({ ...s, [scanA.progress.category]: scanA.progress })); }, [scanA.progress]);
  useEffect(() => { if (autoScan && !started.current) { started.current = true; scan(); } }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const fix = async () => {
    setFixStage(null);
    try {
      const r = await fixA.run("care.fix", { ids: [...sel] });
      setDone(r);
      onDone?.(r);
      toast(r.freed > 0 ? `${f.bytes(r.freed)} liberados` : `${r.ok} itens corrigidos`, "success");
    } catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };

  const cats = result?.categories || [];
  const fixable = cats.flatMap((c) => c.items.filter((i) => i.fixable && i.status !== "ok"));
  const chosen = fixable.filter((i) => sel.has(i.id));
  const chosenSize = chosen.reduce((a, i) => a + (i.size || 0), 0);
  const totalSize = fixable.reduce((a, i) => a + (i.size || 0), 0);

  // ---------- Concluído ----------
  if (done) {
    return (
      <div className="panel animate-fade flex flex-col items-center px-6 py-10 text-center">
        <span className="flex size-16 items-center justify-center rounded-full bg-[var(--color-success-soft)] text-[var(--color-success)]"><Icon name="check_circle" size={36} fill /></span>
        <h3 className="heading mt-4 text-[22px] font-semibold">{done.freed > 0 ? `${f.bytes(done.freed)} liberados` : `${done.ok} ${done.ok === 1 ? "item corrigido" : "itens corrigidos"}`}</h3>
        <p className="mt-1 text-[13px] text-[var(--text-secondary)]">
          {done.ok} {done.ok === 1 ? "correção aplicada" : "correções aplicadas"}{done.failed ? ` · ${done.failed} não puderam ser corrigidos (arquivos em uso ou sem permissão)` : ""}
        </p>
        <div className="mt-3 flex flex-wrap justify-center gap-2">
          {done.restorePoint && <Chip tone="success" icon="restore">Ponto de restauração criado</Chip>}
          {done.backups?.length > 0 && <Chip tone="info" icon="inventory_2">Backup salvo na Central de resgate</Chip>}
          {done.needsReboot && <Chip tone="warning" icon="restart_alt">Reinicie o PC para concluir</Chip>}
        </div>
        <Button className="mt-6" icon="refresh" onClick={scan}>Verificar de novo</Button>
      </div>
    );
  }

  // ---------- Corrigindo ----------
  if (fixA.running) {
    const p = fixStage;
    const cur = fixA.progress;
    const detail = cur?.phase === "item" ? cur.title : cur?.line;
    return (
      <div className="panel flex flex-col items-center px-6 py-12 text-center">
        <TickRing value={p?.total ? (p.index + 0.5) / p.total : 0.04} size={132} n={44} tickLen={11} tickWidth={3} colorAt={WORK_COLOR}>
          <Icon name="auto_fix_high" size={30} className="text-[var(--brand-bright)]" />
          <span className="mt-1 text-[12px] font-semibold tabular text-[var(--text-secondary)]">{p?.total ? f.pct(((p.index + 0.5) / p.total) * 100) : "…"}</span>
        </TickRing>
        <h3 className="heading mt-4 text-[18px] font-semibold">{p?.phase === "restore" ? "Criando ponto de restauração…" : p?.title ? `Corrigindo: ${p.title}` : "Corrigindo…"}</h3>
        {detail && <p className="mt-3 max-w-xl truncate text-[12px] text-[var(--text-tertiary)]">{detail}</p>}
      </div>
    );
  }

  // ---------- Verificando ----------
  if (scanA.running) {
    const p = scanStage;
    const scanPct = p?.total ? ((p.index + (p.phase === "scanned" ? 1 : 0.4)) / p.total) * 100 : 0;
    return (
      <div className="panel px-6 py-8">
        <div className="flex flex-col items-center text-center">
          <TickRing value={scanPct / 100 || 0.04} size={132} n={44} tickLen={11} tickWidth={3} colorAt={WORK_COLOR}>
            <Icon name="search" size={28} className="text-[var(--brand-bright)]" />
            <span className="mt-1 text-[12px] font-semibold tabular text-[var(--text-secondary)]">{f.pct(scanPct)}</span>
          </TickRing>
          <h3 className="heading mt-4 text-[18px] font-semibold">Verificando{p?.title ? `: ${p.title}` : "…"}</h3>
          {scanA.progress?.phase === "item" && scanA.progress.title && <p className="mt-2 max-w-xl truncate text-[12px] text-[var(--text-tertiary)]">{scanA.progress.title}</p>}
        </div>
        {meta.length > 1 && (
          <div className="mx-auto mt-6 grid max-w-3xl gap-2 sm:grid-cols-2">
            {meta.map((m) => {
              const sc = scanned[m.id];
              const cur = p?.category === m.id && p?.phase === "scan";
              return (
                <div key={m.id} className="flex items-center gap-2.5 rounded-[var(--radius-md)] border border-[var(--border)] px-3 py-2">
                  <Icon name={m.icon} size={18} className={cur ? "text-[var(--brand)]" : "text-[var(--text-tertiary)]"} />
                  <span className="flex-1 truncate text-[12.5px] font-medium">{m.title}</span>
                  {sc ? (sc.issues ? <Chip tone="warning">{sc.size > 0 ? f.bytes(sc.size) : sc.issues}</Chip> : <Icon name="check" size={16} className="text-[var(--color-success)]" />) : cur ? <span className="size-3.5 animate-spin-slow rounded-full border-2 border-[var(--brand)] border-t-transparent" /> : <span className="text-[11px] text-[var(--text-muted)]">aguardando</span>}
                </div>
              );
            })}
          </div>
        )}
        <div className="mt-6 flex justify-center"><Button variant="ghost" onClick={scanA.cancel}>Cancelar</Button></div>
      </div>
    );
  }

  // ---------- Ocioso ----------
  if (!result) return idle ? idle(scan) : (
    <div className="panel"><Empty icon="search" title="Pronto para verificar" action={<Button variant="brand" icon="search" onClick={scan}>Verificar agora</Button>}>{!info?.isAdmin && "Dica: abra o RAGE! como administrador para verificar e limpar tudo."}</Empty></div>
  );

  // ---------- Resultados ----------
  return (
    <div className="space-y-3">
      <div className="panel flex flex-wrap items-center gap-4 px-5 py-4">
        <div className="min-w-0 flex-1">
          {fixable.length === 0 ? (
            <><p className="heading text-[18px] font-semibold text-[var(--color-success)]">{emptyText}</p><p className="text-[12.5px] text-[var(--text-tertiary)]">Nenhum problema encontrado.</p></>
          ) : (
            <>
              <p className="heading text-[20px] font-semibold">
                {totalSize > 0 ? <>{f.bytes(totalSize)} <span className="text-[14px] font-medium text-[var(--text-secondary)]">podem ser liberados</span></> : <>{fixable.length} <span className="text-[14px] font-medium text-[var(--text-secondary)]">{fixable.length === 1 ? "item para corrigir" : "itens para corrigir"}</span></>}
              </p>
              <p className="text-[12.5px] text-[var(--text-tertiary)]">{chosen.length} selecionados{chosenSize > 0 ? ` · ${f.bytes(chosenSize)}` : ""} · verificado {f.rel(result.at)}</p>
            </>
          )}
        </div>
        <Button icon="refresh" onClick={scan}>Verificar de novo</Button>
        {fixable.length > 0 && <Button variant="brand" size="lg" icon="auto_fix_high" disabled={!chosen.length} onClick={fix}>{fixLabel}</Button>}
      </div>
      {cats.map((c) => <CategoryBlock key={c.id} cat={c} sel={sel} setSel={setSel} defaultOpen={compact || cats.length === 1} />)}
    </div>
  );
}
