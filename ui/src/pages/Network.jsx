import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { lastN, useHistory, useRpc, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { AreaChart } from "../components/Charts.jsx";
import { Button, Chip, ErrorBox, Icon, KV, Panel, Skeleton, Toggle, cx } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

function PingTest() {
  const { toast } = useApp();
  const [host, setHost] = useState("1.1.1.1");
  const [r, setR] = useState(null);
  const [busy, setBusy] = useState(false);
  const go = async (h = host) => {
    setBusy(true); setR(null);
    try { setR(await call("net.ping", { host: h, count: 20 })); } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  const quality = !r ? null : r.loss > 2 || r.avg > 120 ? ["danger", "Ruim para jogos online"] : r.avg > 60 || r.jitter > 15 ? ["warning", "Razoável"] : ["success", "Ótimo para jogar"];
  return (
    <Panel title="Teste de ping" icon="network_ping" subtitle="20 pacotes: latência, variação (jitter) e perda">
      <div className="flex gap-2">
        <input className="input" value={host} onChange={(e) => setHost(e.target.value)} placeholder="Endereço ou IP" onKeyDown={(e) => e.key === "Enter" && go()} />
        <Button variant="brand" icon="play_arrow" loading={busy} onClick={() => go()}>Testar</Button>
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5">
        {[["Cloudflare", "1.1.1.1"], ["Google", "8.8.8.8"], ["Steam", "store.steampowered.com"], ["Riot", "riotgames.com"], ["Epic", "epicgames.com"]].map(([l, h]) => (
          <button key={h} onClick={() => { setHost(h); go(h); }} className="h-6 rounded-full border border-[var(--border)] px-2.5 text-[11.5px] font-semibold text-[var(--text-tertiary)] hover:text-[var(--text-primary)]">{l}</button>
        ))}
      </div>
      {r && (
        <div className="mt-4">
          <div className="grid grid-cols-5 gap-2 text-center">
            {[["Mínimo", f.ms(r.min)], ["Médio", f.ms(r.avg)], ["Máximo", f.ms(r.max)], ["Jitter", f.ms(r.jitter)], ["Perda", f.pct(r.loss)]].map(([l, v]) => (
              <div key={l} className="rounded-[var(--radius-md)] bg-[var(--bg-subtle)] py-2"><p className="text-[11px] text-[var(--text-tertiary)]">{l}</p><p className="font-semibold tabular">{v}</p></div>
            ))}
          </div>
          <Chip tone={quality[0]} className="mt-3" dot>{quality[1]}</Chip>
        </div>
      )}
    </Panel>
  );
}

function DnsPanel({ adapters }) {
  const { toast, info } = useApp();
  const [res, setRes] = useState(null);
  const [busy, setBusy] = useState(null);
  const adapter = adapters?.find((a) => a.gateway) || adapters?.[0];
  const bench = async () => { setBusy("bench"); try { setRes(await call("net.dnsBenchmark")); } catch (e) { toast(e.message, "danger"); } setBusy(null); };
  const use = async (preset) => {
    setBusy(preset);
    try { await call("net.setDns", { adapter: adapter?.name, preset }); toast(preset === "auto" ? "DNS automático restaurado" : "DNS alterado", "success"); } catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  const best = res ? [...res].filter((x) => x.ms != null).sort((a, b) => a.ms - b.ms)[0] : null;
  return (
    <Panel title="DNS mais rápido" icon="dns" subtitle={adapter ? `Adaptador: ${adapter.name} · DNS atual ${adapter.dns?.join(", ") || "automático"}` : "Testa e troca o servidor de DNS"} actions={<Button size="sm" icon="speed" loading={busy === "bench"} onClick={bench}>Testar DNS</Button>}>
      {!res ? (
        <p className="py-6 text-center text-[13px] text-[var(--text-tertiary)]">Um DNS rápido deixa sites, lojas e o login dos jogos mais ágeis. Clique em Testar DNS para comparar.</p>
      ) : (
        <div className="space-y-1.5">
          {[...res].sort((a, b) => (a.ms ?? 9999) - (b.ms ?? 9999)).map((d) => (
            <div key={d.id} className={cx("flex items-center gap-3 rounded-[var(--radius-md)] border px-3 py-2", d === best ? "border-[var(--brand)] bg-[var(--brand-primary-soft)]" : "border-[var(--border)]")}>
              <div className="min-w-0 flex-1">
                <p className="flex items-center gap-1.5 text-[13px] font-semibold">{d.name}{d.current && <Chip>atual</Chip>}{d === best && <Chip tone="brand">mais rápido</Chip>}</p>
                <p className="mono text-[11px] text-[var(--text-tertiary)]">{[d.primary, d.secondary].filter(Boolean).join(" · ")}</p>
              </div>
              <span className="w-16 text-right text-[13px] font-semibold tabular">{d.ms != null ? f.ms(d.ms) : "falhou"}</span>
              {!d.current && d.id !== "current" && <Button size="sm" loading={busy === d.id} disabled={!info?.isAdmin} onClick={() => use(d.id)}>Usar</Button>}
            </div>
          ))}
          <Button size="sm" variant="ghost" className="mt-1" loading={busy === "auto"} disabled={!info?.isAdmin} onClick={() => use("auto")}>Voltar ao DNS automático do provedor</Button>
        </div>
      )}
    </Panel>
  );
}

function NetTweaks() {
  const { toast } = useApp();
  const { data, setData } = useRpc("tweaks.list", { category: "internet" });
  const [busy, setBusy] = useState(null);
  const set = async (t, on) => {
    setBusy(t.id);
    try { const r = await call(on ? "tweaks.apply" : "tweaks.revert", { ids: [t.id] }); setData((r?.items || []).filter((x) => x.category === "internet")); const fail = r?.results?.find((x) => !x.ok); if (fail) toast(fail.message, "danger"); }
    catch (e) { toast(e.message, "danger"); }
    setBusy(null);
  };
  return (
    <Panel title="Ajustes de rede" icon="tune" bodyClass="!px-2">
      {!data ? <Skeleton className="m-2 h-32" /> : data.map((t) => (
        <div key={t.id} className="flex items-center gap-3 rounded-[var(--radius-sm)] px-2 py-2.5 hover:bg-[var(--bg-subtle)]">
          <div className="min-w-0 flex-1">
            <p className="flex items-center gap-1.5 text-[13px] font-semibold">{t.title}{t.recommended && !t.applied && <Chip tone="brand">Recomendado</Chip>}</p>
            <p className="text-[12px] text-[var(--text-tertiary)]">{t.desc}</p>
          </div>
          {busy === t.id ? <span className="size-4 animate-spin-slow rounded-full border-2 border-current border-t-transparent" />
            : t.oneShot ? <Button size="sm" onClick={() => set(t, true)}>Executar</Button>
            : <Toggle checked={t.applied} onChange={(v) => set(t, v)} />}
        </div>
      ))}
    </Panel>
  );
}

export default function Network() {
  const s = useTelemetry();
  const h = useHistory();
  const nav = useNavigate();
  const { toast } = useApp();
  const { data: adapters, error, reload } = useRpc("net.adapters");
  const flush = async () => { try { await call("net.flushDns"); toast("Cache de DNS limpo", "success"); } catch (e) { toast(e.message, "danger"); } };
  return (
    <div className="space-y-4">
      <PageIntro icon="network_check" title="Internet e latência" actions={<><Button icon="cleaning_services" onClick={flush}>Limpar cache DNS</Button><Button variant="ghost" icon="build" onClick={() => nav("/ferramentas/reparos")}>Reparar rede</Button></>}>
        Ping ao vivo, teste de latência para jogos, DNS mais rápido e ajustes de TCP.
      </PageIntro>
      <ErrorBox error={error} onRetry={reload} />
      <div className="grid gap-4 xl:grid-cols-3">
        <Panel className="xl:col-span-2" title="Ping ao vivo" icon="monitor_heart" subtitle="Até 1.1.1.1, a cada 3 segundos" actions={<Chip tone={s?.net?.ping == null ? "danger" : s.net.ping > 80 ? "warning" : "success"} dot>{s?.net?.ping != null ? f.ms(s.net.ping) : "sem resposta"}</Chip>}>
          <AreaChart values={lastN(h.ping, 300)} times={lastN(h.t, 300)} height={170} color="var(--chart-net)" min={0} fmt={(v) => `${f.n(v)} ms`} smooth={false} />
          <div className="mt-3 grid grid-cols-3 gap-2 text-center">
            {[["Download agora", f.bits(s?.net?.down)], ["Upload agora", f.bits(s?.net?.up)], ["Conexão", s?.net?.adapter ? `${s.net.kind === "wifi" ? "Wi-Fi" : "Cabo"} · ${s.net.linkMbps || "?"} Mbps` : "offline"]].map(([l, v]) => (
              <div key={l} className="rounded-[var(--radius-md)] bg-[var(--bg-subtle)] py-2"><p className="text-[11px] text-[var(--text-tertiary)]">{l}</p><p className="text-[13px] font-semibold tabular">{v}</p></div>
            ))}
          </div>
        </Panel>
        <PingTest />
      </div>
      <div className="grid gap-4 xl:grid-cols-2">
        <DnsPanel adapters={adapters} />
        <NetTweaks />
      </div>
      <Panel title="Adaptadores de rede" icon="lan">
        {!adapters ? <Skeleton className="h-24" /> : (
          <div className="grid gap-3 lg:grid-cols-2">
            {adapters.filter((a) => !a.virtual).map((a) => (
              <div key={a.name} className="rounded-[var(--radius-md)] border border-[var(--border)] p-3">
                <p className="flex items-center gap-2 text-[13px] font-semibold"><Icon name={a.type?.toLowerCase().includes("wi") ? "wifi" : "lan"} size={18} />{a.name}<Chip tone={a.status?.toLowerCase().startsWith("con") || a.status === "Up" ? "success" : "neutral"} dot>{a.status}</Chip></p>
                <p className="mb-1 truncate text-[11.5px] text-[var(--text-tertiary)]">{a.desc}</p>
                <KV label="IP" value={a.ip} mono />
                <KV label="Gateway" value={a.gateway} mono />
                <KV label="DNS" value={a.dns?.join(", ") || "automático"} mono />
                <KV label="Velocidade" value={a.speedMbps ? `${a.speedMbps} Mbps` : "—"} />
                {a.ssid && <KV label="Rede Wi-Fi" value={`${a.ssid}${a.signal ? ` · sinal ${a.signal}%` : ""}`} />}
                <KV label="MAC" value={a.mac} mono />
              </div>
            ))}
          </div>
        )}
      </Panel>
    </div>
  );
}
