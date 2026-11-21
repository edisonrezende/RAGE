import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useRpc } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, ErrorBox, KV, Panel, Skeleton } from "../../components/ui.jsx";

const v = (x) => (x == null || x === "" ? "—" : x);

export default function SystemInfo() {
  const { toast } = useApp();
  const { data: d, error, loading, reload } = useRpc("sysinfo.get");
  const exportTxt = async () => {
    try {
      const text = await call("sysinfo.export");
      const file = await call("app.saveText", { fileName: `RAGE-sistema-${new Date().toISOString().slice(0, 10)}.txt`, content: typeof text === "string" ? text : text?.text || JSON.stringify(text, null, 2) });
      if (file) toast("Relatório salvo", "success");
    } catch (e) { toast(e.message, "danger"); }
  };
  if (loading && !d) return <div className="grid gap-4 lg:grid-cols-2">{Array.from({ length: 4 }, (_, i) => <Skeleton key={i} className="h-56" />)}</div>;
  return (
    <div className="space-y-4">
      <ErrorBox error={error} onRetry={reload} />
      <div className="flex justify-end"><Button icon="save" onClick={exportTxt}>Exportar relatório</Button></div>
      {d && (
        <div className="grid gap-4 lg:grid-cols-2 2xl:grid-cols-3">
          <Panel title="Windows" icon="window">
            <KV label="Sistema" value={v(d.os?.name)} />
            <KV label="Versão" value={v(d.os?.version)} />
            <KV label="Compilação" value={v(d.os?.build)} />
            <KV label="Instalado em" value={f.dateShort(d.os?.installDate)} />
            <KV label="Ligado há" value={f.duration(d.os?.uptimeSec)} />
            <KV label="Computador" value={v(d.os?.machine)} />
            <KV label="Usuário" value={v(d.os?.user)} />
          </Panel>
          <Panel title="Processador" icon="memory">
            <KV label="Modelo" value={v(d.cpu?.name)} />
            <KV label="Núcleos / threads" value={`${v(d.cpu?.cores)} / ${v(d.cpu?.threads)}`} />
            <KV label="Clock base" value={f.mhz(d.cpu?.baseMHz)} />
            <KV label="Soquete" value={v(d.cpu?.socket)} />
            <KV label="Cache L2 / L3" value={`${d.cpu?.l2 ? f.bytes(d.cpu.l2) : "—"} / ${d.cpu?.l3 ? f.bytes(d.cpu.l3) : "—"}`} />
            <KV label="Virtualização" value={d.cpu?.virtualization == null ? "—" : d.cpu.virtualization ? "Ativada" : "Desativada"} />
          </Panel>
          <Panel title="Placa-mãe e BIOS" icon="developer_board">
            <KV label="Fabricante" value={v(d.board?.manufacturer)} />
            <KV label="Modelo" value={v(d.board?.product)} />
            <KV label="BIOS" value={v(d.board?.bios)} />
            <KV label="Data da BIOS" value={f.dateShort(d.board?.biosDate)} />
          </Panel>
          <Panel title="Memória RAM" icon="memory_alt" subtitle={`${f.gb(d.ram?.total, 0)} no total`}>
            {(d.ram?.modules || []).map((m, i) => (
              <div key={i} className="border-b border-[var(--border)] py-1.5 last:border-0">
                <KV label={v(m.slot)} value={`${f.gb(m.size, 0)} ${m.type || ""} ${m.speed ? `${m.speed} MHz` : ""}`} />
                <p className="text-right text-[11.5px] text-[var(--text-tertiary)]">{[m.manufacturer, m.part].filter(Boolean).join(" · ")}</p>
              </div>
            ))}
          </Panel>
          <Panel title="Vídeo" icon="monitor">
            {(d.gpus || []).map((g, i) => (
              <div key={i} className="border-b border-[var(--border)] py-1.5 last:border-0">
                <p className="text-[13px] font-semibold">{g.name}</p>
                <KV label="Memória" value={g.vram ? f.gb(g.vram, 0) : "—"} />
                <KV label="Driver" value={`${v(g.driver)}${g.driverDate ? ` (${f.dateShort(g.driverDate)})` : ""}`} />
                {g.resolution && <KV label="Tela" value={`${g.resolution}${g.refresh ? ` @ ${g.refresh} Hz` : ""}`} />}
              </div>
            ))}
          </Panel>
          <Panel title="Armazenamento" icon="hard_drive">
            {(d.disks || []).map((k, i) => (
              <div key={i} className="border-b border-[var(--border)] py-1.5 last:border-0">
                <p className="truncate text-[13px] font-semibold">{k.model}</p>
                <KV label="Tamanho" value={f.gb(k.size, 0)} />
                <KV label="Tipo" value={`${v(k.media)} ${k.bus || ""}`} />
                {k.health && <KV label="Saúde" value={k.health} />}
              </div>
            ))}
          </Panel>
          <Panel title="Rede" icon="lan">
            {(d.network || []).map((n, i) => (
              <div key={i} className="border-b border-[var(--border)] py-1.5 last:border-0">
                <p className="truncate text-[13px] font-semibold">{n.name}</p>
                <KV label="Tipo" value={v(n.type)} />
                <KV label="IP" value={v(n.ip)} mono />
                <KV label="MAC" value={v(n.mac)} mono />
                <KV label="Velocidade" value={n.speed ? `${f.n(n.speed)} Mbps` : "—"} />
              </div>
            ))}
          </Panel>
          {d.battery && (
            <Panel title="Bateria" icon="battery_full">
              <KV label="Capacidade original" value={d.battery.design ? `${f.n(d.battery.design / 1000, 1)} Wh` : "—"} />
              <KV label="Capacidade atual" value={d.battery.full ? `${f.n(d.battery.full / 1000, 1)} Wh` : "—"} />
              <KV label="Desgaste" value={f.pct(d.battery.wearPct)} />
              <KV label="Química" value={v(d.battery.chemistry)} />
            </Panel>
          )}
        </div>
      )}
    </div>
  );
}
