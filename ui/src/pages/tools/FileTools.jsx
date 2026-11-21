import { useState } from "react";
import { call } from "../../lib/rage.js";
import { Button, Icon } from "../../components/ui.jsx";

export function useFolder(key, def = "C:\\") {
  const [folder, setFolderState] = useState(() => { try { return localStorage.getItem("folder:" + key) || def; } catch { return def; } });
  const setFolder = (v) => { setFolderState(v); try { localStorage.setItem("folder:" + key, v); } catch { /* */ } };
  return [folder, setFolder];
}

export function FolderBar({ folder, setFolder, disabled, children }) {
  const pick = async () => { const p = await call("app.pickFolder", { title: "Escolha a pasta" }); if (p) setFolder(p); };
  return (
    <div className="panel flex flex-wrap items-center gap-3 px-4 py-3">
      <Icon name="folder" size={20} className="text-[var(--brand)]" />
      <div className="min-w-0 flex-1">
        <p className="text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Pasta</p>
        <p className="mono truncate text-[13px] font-medium" title={folder}>{folder}</p>
      </div>
      <Button icon="folder_open" onClick={pick} disabled={disabled}>Escolher</Button>
      {children}
    </div>
  );
}
