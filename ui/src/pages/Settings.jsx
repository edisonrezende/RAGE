import { useEffect, useState } from "react";
import { useLocation } from "react-router-dom";
import { useApp } from "../App.jsx";
import { call } from "../lib/rage.js";
import { useAction, useTelemetry } from "../lib/store.js";
import * as f from "../lib/format.js";
import { Button, Chip, Icon, KV, LogView, Panel, Segmented, Toggle, cx } from "../components/ui.jsx";
import { Logo } from "../components/Shell.jsx";

function Row({ title, desc, children }) {
  return (
    <div className="flex items-center gap-4 py-3">
      <div className="min-w-0 flex-1"><p className="text-[13px] font-semibold">{title}</p>{desc && <p className="text-[12px] text-[var(--text-tertiary)]">{desc}</p>}</div>
      {children}
    </div>
  );
}

function Num({ value, onChange, min, max, suffix }) {
  return (
    <div className="flex items-center gap-2">
      <input type="range" className="range w-40" min={min} max={max} value={value ?? min} onChange={(e) => onChange(+e.target.value)} />
      <span className="w-14 text-right text-[13px] font-semibold tabular">{value}{suffix}</span>
    </div>
  );
}

function SensorsPanel() {
  const { info, reloadInfo, toast } = useApp();
  const s = useTelemetry();
  const a = useAction();
  const st = info?.sensors;
  const cpuOk = s?.cpu?.temp != null && s?.cpu?.tempSource === "sensor";
  const install = async () => {
    try { await a.run("app.installSensorDriver"); toast("Driver de sensores instalado", "success"); reloadInfo(); }
    catch (e) { if (!e.canceled) toast(e.message, "danger"); }
  };
  return (
    <Panel title="Sensores de hardware" icon="device_thermostat" className="scroll-mt-4" subtitle="Temperatura e consumo reais da CPU">
      <div id="sensores" className="flex flex-wrap items-center gap-4 rounded-[var(--radius-md)] bg-[var(--bg-subtle)] p-4">
        <span className={cx("flex size-11 items-center justify-center rounded-full", cpuOk ? "bg-[var(--color-success-soft)] text-[var(--color-success)]" : "bg-[var(--color-warning-soft)] text-[var(--color-warning)]")}><Icon name={cpuOk ? "check_circle" : "thermostat"} size={24} /></span>
        <div className="min-w-0 flex-1">
          <p className="text-[13.5px] font-semibold">{cpuOk ? `Sensores ativos · CPU a ${f.temp(s.cpu.temp)}` : st?.installed ? "Driver instalado" : "Driver de sensores não instalado"}</p>
          <p className="text-[12px] text-[var(--text-tertiary)]">
            {cpuOk ? `PawnIO ${st?.version || ""} — temperatura por núcleo e consumo em watts liberados.`
              : st?.installed ? (info?.isAdmin ? "Aguardando a primeira leitura…" : "Abra o RAGE! como administrador para ler a CPU.")
              : "O Windows não entrega a temperatura real do processador. O PawnIO é um driver gratuito, assinado e de código aberto (usado pelo LibreHardwareMonitor e FanControl). Instala uma vez por PC."}
          </p>
        </div>
        {!st?.installed && (info?.isAdmin
          ? <Button variant="brand" icon="download" loading={a.running} onClick={install}>Instalar PawnIO</Button>
          : <Button icon="admin_panel_settings" onClick={() => call("app.elevate")}>Abrir como admin</Button>)}
      </div>
      {(a.running || a.lines.length > 0) && <LogView lines={a.lines} className="mt-3 max-h-40" empty="Baixando pelo winget…" />}
      <p className="mt-3 text-[11.5px] text-[var(--text-tertiary)]">GPU, disco e bateria funcionam sem driver. O RAGE! nunca usa o antigo WinRing0 (bloqueado pelo Defender).</p>
    </Panel>
  );
}

export default function SettingsPage() {
  const { settings: s, updateSettings: set, theme, setTheme, info, toast } = useApp();
  const { hash } = useLocation();
  const [hotkey, setHotkey] = useState("");
  useEffect(() => { setHotkey(s?.overlayHotkey || ""); }, [s?.overlayHotkey]);
  useEffect(() => { if (hash.includes("sensores")) setTimeout(() => document.getElementById("sensores")?.scrollIntoView({ behavior: "smooth", block: "center" }), 200); }, [hash]);
  if (!s) return null;
  const saveHotkey = async () => { await set({ overlayHotkey: hotkey }); call("overlay.applySettings").catch(() => {}); toast("Atalho do overlay salvo", "success"); };

  return (
    <div className="grid gap-4 xl:grid-cols-2">
      <div className="space-y-4">
        <SensorsPanel />
        <Panel title="Geral" icon="tune" bodyClass="divide-y divide-[var(--border)] !pt-0">
          <Row title="Iniciar com o Windows" desc="Abre minimizado na bandeja, já como administrador (sem pedir permissão).">
            <Toggle checked={s.startWithWindows} disabled={!info?.isAdmin} onChange={(v) => set({ startWithWindows: v })} />
          </Row>
          <Row title="Atalho na Área de Trabalho" desc="Abre o RAGE! direto como administrador, sem a pergunta do Windows.">
            <Button size="md" icon="shortcut" disabled={!info?.isAdmin} onClick={() => call("app.desktopShortcut", { noUac: true }).then(() => toast("Atalho RAGE! criado na Área de Trabalho", "success")).catch((e) => toast(e.message, "danger"))}>Criar atalho</Button>
          </Row>
          <Row title="Fechar para a bandeja" desc="O X esconde a janela e o RAGE! continua monitorando e pronto para o Turbo.">
            <Toggle checked={s.closeToTray} onChange={(v) => set({ closeToTray: v })} />
          </Row>
          <Row title="Tema"><Segmented value={theme} onChange={setTheme} options={[{ value: "dark", label: "Escuro", icon: "dark_mode" }, { value: "light", label: "Claro", icon: "light_mode" }]} /></Row>
          <Row title="Unidade de temperatura"><Segmented value={s.tempFahrenheit ? "f" : "c"} onChange={(v) => set({ tempFahrenheit: v === "f" })} options={[{ value: "c", label: "°C" }, { value: "f", label: "°F" }]} /></Row>
        </Panel>
        <Panel title="Proteção" icon="shield" bodyClass="divide-y divide-[var(--border)] !pt-0">
          <Row title="Ponto de restauração antes de corrigir" desc="Antes de mexer no registro, ajustes e segurança.">
            <Toggle checked={s.restorePointBeforeFix} onChange={(v) => set({ restorePointBeforeFix: v })} />
          </Row>
          <Row title="Alertas de temperatura" desc="Notificação quando passar do limite.">
            <Toggle checked={s.tempAlerts} onChange={(v) => set({ tempAlerts: v })} />
          </Row>
          {s.tempAlerts && <>
            <Row title="Limite da CPU"><Num value={s.cpuTempAlert} min={70} max={105} suffix=" °C" onChange={(v) => set({ cpuTempAlert: v })} /></Row>
            <Row title="Limite da GPU"><Num value={s.gpuTempAlert} min={65} max={100} suffix=" °C" onChange={(v) => set({ gpuTempAlert: v })} /></Row>
          </>}
        </Panel>
      </div>
      <div className="space-y-4">
        <Panel title="Memória automática" icon="memory" bodyClass="divide-y divide-[var(--border)] !pt-0">
          <Row title="Liberar RAM automaticamente" desc="Quando o uso passar do limite (no máximo a cada 5 minutos).">
            <Toggle checked={s.autoRamClean} onChange={(v) => set({ autoRamClean: v })} />
          </Row>
          {s.autoRamClean && <Row title="Limite de uso"><Num value={s.autoRamThreshold} min={60} max={95} suffix="%" onChange={(v) => set({ autoRamThreshold: v })} /></Row>}
        </Panel>
        <Panel title="Overlay de jogo" icon="picture_in_picture" bodyClass="divide-y divide-[var(--border)] !pt-0">
          <Row title="Mostrar ao abrir um jogo"><Toggle checked={s.overlayEnabled && s.overlayAutoInGame} onChange={(v) => set({ overlayEnabled: v, overlayAutoInGame: v }).then(() => call("overlay.applySettings"))} /></Row>
          <Row title="Atalho para mostrar/esconder" desc="Ex.: Ctrl+Shift+F10, Alt+F12">
            <div className="flex gap-2"><input className="input mono w-40" value={hotkey} onChange={(e) => setHotkey(e.target.value)} /><Button size="md" onClick={saveHotkey} disabled={hotkey === s.overlayHotkey}>Salvar</Button></div>
          </Row>
        </Panel>
        <Panel title="Sobre" icon="info">
          <div className="mb-3 flex items-center gap-3">
            <Logo size={44} />
            <div>
              <p className="heading text-[20px] font-extrabold">RAGE<span className="brand-text">!</span> <span className="text-[13px] font-medium text-[var(--text-tertiary)]">v{info?.version}</span></p>
              <p className="text-[12px] text-[var(--text-tertiary)]">Monitor, limpeza, otimização e Modo Turbo para jogos. Todas as funções liberadas.</p>
            </div>
          </div>
          <KV label="Windows" value={info ? `${info.os} (${info.osBuild})` : "—"} />
          <KV label="Computador" value={info ? `${info.machine} · ${info.user}` : "—"} />
          <KV label="Modo" value={info?.portable ? "Portátil (dados na pasta do app)" : "Instalado"} />
          <KV label="Permissão" value={info?.isAdmin ? <Chip tone="success" dot>Administrador</Chip> : <Chip tone="warning" dot>Limitado</Chip>} />
          <KV label="WebView2" value={info?.webview} />
          <KV label="Ligado há" value={f.duration(info?.uptimeSec)} />
          <div className="mt-3 flex flex-wrap gap-2">
            <Button size="sm" icon="folder_open" onClick={() => call("app.openDataFolder")}>Pasta de dados</Button>
            <Button size="sm" variant="ghost" icon="logout" onClick={() => call("app.quit")}>Sair do RAGE!</Button>
          </div>
          <p className="mt-4 text-[11px] leading-relaxed text-[var(--text-muted)]">
            Usa LibreHardwareMonitorLib (MPL-2.0), driver PawnIO, Microsoft WebView2 e Newtonsoft.Json (MIT). Antivírus: Microsoft Defender. Atualizações de programas: winget.
          </p>
        </Panel>
      </div>
    </div>
  );
}
