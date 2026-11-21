import { useEffect, useState } from "react";
import { useApp } from "../../App.jsx";
import { call } from "../../lib/rage.js";
import { useRpc } from "../../lib/store.js";
import * as f from "../../lib/format.js";
import { Button, Icon, Panel, Segmented, cx } from "../../components/ui.jsx";
import { PageIntro } from "../../components/Shell.jsx";

const ACTIONS = [{ value: "shutdown", label: "Desligar", icon: "power_settings_new" }, { value: "restart", label: "Reiniciar", icon: "restart_alt" }, { value: "hibernate", label: "Hibernar", icon: "bedtime" }, { value: "sleep", label: "Suspender", icon: "dark_mode" }];

export default function Schedule() {
  const { toast } = useApp();
  const { data, setData } = useRpc("schedule.get");
  const [action, setAction] = useState("shutdown");
  const [minutes, setMinutes] = useState(60);
  const [, tick] = useState(0);
  useEffect(() => { if (!data?.at) return; const t = setInterval(() => tick((x) => x + 1), 1000); return () => clearInterval(t); }, [data]);
  const set = async () => { try { await call("schedule.set", { action, minutes }); setData({ action, at: new Date(Date.now() + minutes * 60000).toISOString() }); toast("Agendado", "success"); } catch (e) { toast(e.message, "danger"); } };
  const cancel = async () => { try { await call("schedule.cancel"); setData(null); toast("Agendamento cancelado"); } catch (e) { toast(e.message, "danger"); } };
  const left = data?.at ? (new Date(data.at).getTime() - Date.now()) / 1000 : 0;
  const act = ACTIONS.find((a) => a.value === (data?.action || action));
  return (
    <div className="mx-auto max-w-3xl space-y-4">
      <PageIntro icon="schedule" title="Desligamento agendado">Deixe o download terminando ou a partida rolando e o PC desliga, reinicia ou hiberna sozinho na hora certa.</PageIntro>
      {data?.at ? (
        <Panel>
          <div className="flex flex-col items-center py-6 text-center">
            <span className="brand-bg flex size-16 items-center justify-center rounded-full text-white glow"><Icon name={act.icon} size={32} /></span>
            <p className="heading mt-4 text-[40px] font-semibold tabular">{f.duration(Math.max(0, left))}</p>
            <p className="text-[13px] text-[var(--text-secondary)]">para {act.label.toLowerCase()} · às {f.clock(data.at)}</p>
            <Button className="mt-5" icon="close" onClick={cancel}>Cancelar agendamento</Button>
          </div>
        </Panel>
      ) : (
        <Panel>
          <p className="mb-2 text-[12.5px] font-semibold">O que fazer</p>
          <div className="grid grid-cols-2 gap-2 md:grid-cols-4">
            {ACTIONS.map((a) => (
              <button key={a.value} onClick={() => setAction(a.value)} className={cx("flex flex-col items-center gap-1.5 rounded-[var(--radius-md)] border py-4 transition-colors", action === a.value ? "border-[var(--brand)] bg-[var(--brand-primary-soft)] text-[var(--brand)]" : "border-[var(--border)] text-[var(--text-secondary)] hover:border-[var(--border-strong)]")}>
                <Icon name={a.icon} size={24} /><span className="text-[13px] font-semibold">{a.label}</span>
              </button>
            ))}
          </div>
          <p className="mt-5 mb-2 text-[12.5px] font-semibold">Daqui a</p>
          <Segmented value={minutes} onChange={setMinutes} options={[15, 30, 60, 90, 120, 180].map((m) => ({ value: m, label: m < 60 ? `${m} min` : `${m / 60} h` }))} />
          <div className="mt-3 flex items-center gap-3">
            <input type="range" className="range flex-1" min={5} max={480} step={5} value={minutes} onChange={(e) => setMinutes(+e.target.value)} />
            <span className="w-20 text-right text-[13px] font-semibold tabular">{f.duration(minutes * 60)}</span>
          </div>
          <Button variant="brand" size="lg" className="mt-5 w-full" icon="schedule" onClick={set}>Agendar para as {f.clock(Date.now() + minutes * 60000)}</Button>
        </Panel>
      )}
    </div>
  );
}
