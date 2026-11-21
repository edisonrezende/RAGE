import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { HashRouter, Route, Routes } from "react-router-dom";
import { call, on } from "./lib/rage.js";
import { startTelemetry } from "./lib/store.js";
import { setFahrenheit } from "./lib/format.js";
import { Shell } from "./components/Shell.jsx";
import { ToastHost } from "./components/Toast.jsx";
import Overview from "./pages/Overview.jsx";
import Turbo from "./pages/Turbo.jsx";
import Games from "./pages/Games.jsx";
import Benchmark from "./pages/Benchmark.jsx";
import Stress from "./pages/Stress.jsx";
import Overclock from "./pages/Overclock.jsx";
import SmartScan from "./pages/SmartScan.jsx";
import Cleaner from "./pages/Cleaner.jsx";
import RegistryPage from "./pages/Registry.jsx";
import Startup from "./pages/Startup.jsx";
import Tweaks from "./pages/Tweaks.jsx";
import Network from "./pages/Network.jsx";
import Disk from "./pages/Disk.jsx";
import Antivirus from "./pages/Antivirus.jsx";
import Security from "./pages/Security.jsx";
import Rescue from "./pages/Rescue.jsx";
import Toolbox from "./pages/Toolbox.jsx";
import SettingsPage from "./pages/Settings.jsx";
import Processes from "./pages/tools/Processes.jsx";
import Programs from "./pages/tools/Programs.jsx";
import Updates from "./pages/tools/Updates.jsx";
import Drivers from "./pages/tools/Drivers.jsx";
import LargeFiles from "./pages/tools/LargeFiles.jsx";
import Duplicates from "./pages/tools/Duplicates.jsx";
import EmptyFolders from "./pages/tools/EmptyFolders.jsx";
import Shredder from "./pages/tools/Shredder.jsx";
import SystemInfo from "./pages/tools/SystemInfo.jsx";
import WinFix from "./pages/tools/WinFix.jsx";
import Schedule from "./pages/tools/Schedule.jsx";
import DesktopOrganizer from "./pages/tools/DesktopOrganizer.jsx";

const AppCtx = createContext(null);
export const useApp = () => useContext(AppCtx);

export default function App() {
  const [info, setInfo] = useState(null);
  const [settings, setSettings] = useState(null);
  const [theme, setThemeState] = useState(() => document.documentElement.dataset.theme || "dark");
  const [boost, setBoost] = useState(null);
  const [toasts, setToasts] = useState([]);

  const toast = useCallback((message, tone = "neutral") => {
    const id = Math.random().toString(36).slice(2);
    setToasts((t) => [...t, { id, message, tone }]);
    setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), 3800);
  }, []);

  const applyTheme = useCallback((t) => {
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem("theme", t); } catch { /* sem storage */ }
    setThemeState(t);
  }, []);

  const setTheme = useCallback((t) => { applyTheme(t); call("app.setTheme", { theme: t }).catch(() => {}); }, [applyTheme]);

  const updateSettings = useCallback(async (values) => {
    setSettings((s) => ({ ...s, ...values }));
    const next = await call("settings.set", { values });
    setSettings(next);
    setFahrenheit(next?.tempFahrenheit);
    return next;
  }, []);

  const reloadInfo = useCallback(() => call("app.info").then(setInfo).catch(() => {}), []);

  useEffect(() => {
    startTelemetry();
    reloadInfo();
    call("settings.get").then((s) => {
      setSettings(s);
      setFahrenheit(s?.tempFahrenheit);
      if (s?.theme && s.theme !== document.documentElement.dataset.theme) applyTheme(s.theme);
    }).catch(() => {});
    call("boost.status").then(setBoost).catch(() => {});
    const offs = [
      on("boost", setBoost),
      on("telemetry", (s) => s?.boost && setBoost((b) => (b?.active === s.boost.active && b?.mode === s.boost.mode ? b : s.boost))),
    ];
    return () => offs.forEach((f) => f());
  }, [applyTheme, reloadInfo]);

  const value = useMemo(() => ({ info, reloadInfo, settings, updateSettings, theme, setTheme, toast, boost, setBoost }), [info, reloadInfo, settings, updateSettings, theme, setTheme, toast, boost]);

  return (
    <AppCtx.Provider value={value}>
      <HashRouter>
        <Shell>
          <Routes>
            <Route path="/" element={<Overview />} />
            <Route path="/turbo" element={<Turbo />} />
            <Route path="/jogos" element={<Games />} />
            <Route path="/benchmark" element={<Benchmark />} />
            <Route path="/stress" element={<Stress />} />
            <Route path="/overclock" element={<Overclock />} />
            <Route path="/verificacao" element={<SmartScan />} />
            <Route path="/limpeza" element={<Cleaner />} />
            <Route path="/registro" element={<RegistryPage />} />
            <Route path="/inicializacao" element={<Startup />} />
            <Route path="/ajustes" element={<Tweaks />} />
            <Route path="/internet" element={<Network />} />
            <Route path="/disco" element={<Disk />} />
            <Route path="/antivirus" element={<Antivirus />} />
            <Route path="/seguranca" element={<Security />} />
            <Route path="/resgate" element={<Rescue />} />
            <Route path="/ferramentas" element={<Toolbox />} />
            <Route path="/ferramentas/processos" element={<Processes />} />
            <Route path="/ferramentas/programas" element={<Programs />} />
            <Route path="/ferramentas/atualizacoes" element={<Updates />} />
            <Route path="/ferramentas/drivers" element={<Drivers />} />
            <Route path="/ferramentas/arquivos-grandes" element={<LargeFiles />} />
            <Route path="/ferramentas/duplicados" element={<Duplicates />} />
            <Route path="/ferramentas/pastas-vazias" element={<EmptyFolders />} />
            <Route path="/ferramentas/triturador" element={<Shredder />} />
            <Route path="/ferramentas/sistema" element={<SystemInfo />} />
            <Route path="/ferramentas/reparos" element={<WinFix />} />
            <Route path="/ferramentas/desligamento" element={<Schedule />} />
            <Route path="/ferramentas/area-de-trabalho" element={<DesktopOrganizer />} />
            <Route path="/configuracoes" element={<SettingsPage />} />
            <Route path="*" element={<Overview />} />
          </Routes>
        </Shell>
      </HashRouter>
      <ToastHost toasts={toasts} />
    </AppCtx.Provider>
  );
}
