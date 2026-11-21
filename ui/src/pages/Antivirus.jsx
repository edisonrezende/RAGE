import { useState } from "react";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useAction, useRpc } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Chip, Confirm, Empty, ErrorBox, Icon, KV, LogView, Panel, Skeleton, cx } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

export default function Antivirus() {
  const { toast, info } = useApp();
  const { data: st, error, loading, reload } = useRpc("av.status");
  const scanA = useAction();
  const [kind, setKind] = useState(null);
  const [lastScan, setLastScan] = useState(null);
  const [confirmFull, setConfirmFull] = useState(false);

  const scan = async (type, path) => {
    setKind(type); setLastScan(null);
    try { const r = await scanA.run("av.scan", { type, path }); setLastScan(r); toast(r?.threats?.length ? `${r.threats.length} ameaças encontradas` : "Nenhuma ameaça encontrada", r?.threats?.length ? "danger" : "success"); reload(true); }
    catch (e) { if (!e.canceled) toast(e.message, "danger"); }
    setKind(null);
  };
  const custom = async () => { const p = await call("app.pickFolder", { title: "Pasta para verificar" }); if (p) scan("custom", p); };
  const update = async () => { setKind("update"); try { await scanA.run("av.update"); toast("Definições de vírus atualizadas", "success"); reload(true); } catch (e) { if (!e.canceled) toast(e.message, "danger"); } setKind(null); };
  const remove = async () => { try { await call("av.remove"); toast("Ameaças removidas", "success"); reload(true); } catch (e) { toast(e.message, "danger"); } };

  const protectedOk = st?.enabled && st?.realtime;
  const threats = st?.threats || [];

  return (
    <div className="space-y-4">
      <PageIntro icon="coronavirus" title="Antivírus">
        O RAGE! usa o Microsoft Defender — o antivírus que já vem no Windows, sem pesar o PC nem cobrar assinatura — e coloca tudo num lugar só.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      {loading && !st ? <Skeleton className="h-48" /> : st && (
        <section className={cx("panel relative overflow-hidden p-6", protectedOk ? "" : "border-[var(--color-danger)]")}>
          <div className={cx("pointer-events-none absolute inset-0", protectedOk ? "bg-[radial-gradient(500px_circle_at_10%_40%,#31c47a1c,transparent_60%)]" : "bg-[radial-gradient(500px_circle_at_10%_40%,#ef44441c,transparent_60%)]")} />
          <div className="relative flex flex-wrap items-center gap-6">
            <span className={cx("flex size-20 items-center justify-center rounded-full", protectedOk ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : "bg-[var(--color-danger-soft)] text-[var(--color-danger)]")}>
              <Icon name={protectedOk ? "verified_user" : "gpp_bad"} size={44} fill />
            </span>
            <div className="min-w-0 flex-1">
              <h2 className="heading text-[22px] font-semibold">{st.thirdParty ? `Protegido por ${st.thirdParty}` : protectedOk ? "Seu PC está protegido" : "Proteção desligada"}</h2>
              <p className="text-[13px] text-[var(--text-secondary)]">
                {st.thirdParty ? "Outro antivírus está ativo — o Defender fica em modo passivo." : protectedOk ? `Proteção em tempo real ativa · definições ${st.signatureAgeDays === 0 ? "de hoje" : `de ${st.signatureAgeDays} dias atrás`}` : "Ative a proteção em tempo real em Segurança do Windows."}
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <Chip tone={st.realtime ? "success" : "danger"} dot>Tempo real {st.realtime ? "ativo" : "desligado"}</Chip>
                <Chip tone={st.tamper ? "success" : "warning"} dot>Proteção contra violação {st.tamper ? "ativa" : "desligada"}</Chip>
                <Chip tone={st.signatureAgeDays <= 3 ? "success" : "warning"} dot>Definições {st.signatureVersion || "—"}</Chip>
              </div>
            </div>
            <div className="flex flex-col gap-2">
              <Button variant="ghost" icon="open_in_new" onClick={() => call("app.open", { target: "windowsdefender:" })}>Segurança do Windows</Button>
              <Button icon="sync" loading={kind === "update"} disabled={!!kind || !info?.isAdmin} onClick={update}>Atualizar definições</Button>
            </div>
          </div>
        </section>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        {[
          { type: "quick", icon: "bolt", title: "Verificação rápida", desc: "Locais onde os vírus costumam se esconder. 2 a 10 minutos.", last: st?.lastQuickScan },
          { type: "full", icon: "travel_explore", title: "Verificação completa", desc: "Todos os arquivos e programas. Pode levar mais de uma hora.", last: st?.lastFullScan },
          { type: "custom", icon: "folder_open", title: "Verificar pasta", desc: "Escolha uma pasta — por exemplo, Downloads ou um pendrive." },
        ].map((c) => (
          <Panel key={c.type}>
            <div className="flex items-center gap-3">
              <span className={cx("flex size-10 items-center justify-center rounded-[var(--radius-md)]", kind === c.type ? "brand-bg text-white" : "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}><Icon name={c.icon} size={21} /></span>
              <div className="min-w-0"><p className="text-[14px] font-semibold">{c.title}</p><p className="text-[12px] text-[var(--text-tertiary)]">{c.last ? `Última: ${f.rel(c.last)}` : c.type === "custom" ? "Sob demanda" : "Nunca executada"}</p></div>
            </div>
            <p className="mt-3 text-[12.5px] text-[var(--text-secondary)]">{c.desc}</p>
            {kind === c.type ? <Button className="mt-4 w-full" icon="stop" onClick={scanA.cancel}>Cancelar</Button> : (
              <Button className="mt-4 w-full" variant={c.type === "quick" ? "brand" : "secondary"} icon="play_arrow" disabled={!!kind || !info?.isAdmin} onClick={() => (c.type === "custom" ? custom() : c.type === "full" ? setConfirmFull(true) : scan("quick"))}>Verificar</Button>
            )}
          </Panel>
        ))}
      </div>

      {(scanA.running || scanA.lines.length > 0) && (
        <Panel title={scanA.running ? "Verificando…" : "Saída da verificação"} icon="terminal">
          <LogView lines={scanA.lines} empty="Iniciando o Microsoft Defender…" />
        </Panel>
      )}

      <Panel title="Ameaças" icon="bug_report" actions={threats.some((t) => t.status !== "removida") && <Button size="sm" variant="danger" icon="delete" onClick={remove}>Remover ameaças</Button>}>
        {threats.length === 0 && !lastScan?.threats?.length ? <Empty icon="verified" title="Nenhuma ameaça registrada">O Defender não encontrou nada nas últimas verificações.</Empty> : (
          <div className="divide-y divide-[var(--border)]">
            {[...(lastScan?.threats || []), ...threats].map((t, i) => (
              <div key={t.id || i} className="flex items-center gap-3 py-2.5">
                <Icon name="bug_report" size={20} className="text-[var(--color-danger)]" />
                <div className="min-w-0 flex-1"><p className="text-[13px] font-semibold">{t.name}</p><p className="truncate text-[11.5px] text-[var(--text-tertiary)]">{t.path}</p></div>
                <Chip tone={t.severity === "Grave" || t.severity === "Alta" ? "danger" : "warning"}>{t.severity || "—"}</Chip>
                <span className="text-[11.5px] text-[var(--text-tertiary)]">{t.status} · {f.rel(t.detected)}</span>
              </div>
            ))}
          </div>
        )}
      </Panel>
      <Confirm open={confirmFull} title="Verificação completa" icon="travel_explore" confirmLabel="Começar" onClose={() => setConfirmFull(false)} onConfirm={() => { setConfirmFull(false); scan("full"); }}>
        Pode levar mais de uma hora e deixar o PC mais lento enquanto roda. Você pode continuar usando o computador normalmente.
      </Confirm>
    </div>
  );
}
