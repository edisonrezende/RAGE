import { useEffect, useRef, useState } from "react";
import { call } from "../lib/rage.js";
import { Icon, cx } from "./ui.jsx";

// No WebView2 (Chromium no Windows) o keyCode é o próprio código de tecla virtual do Windows,
// então o nome gerado aqui é o mesmo que o host entende (System.Windows.Forms.Keys).
const NAMED = {
  32: "Space", 33: "PageUp", 34: "PageDown", 35: "End", 36: "Home", 37: "Left", 38: "Up", 39: "Right", 40: "Down",
  45: "Insert", 46: "Delete", 19: "Pause", 145: "Scroll", 106: "Multiply", 107: "Add", 109: "Subtract", 110: "Decimal", 111: "Divide",
};
// Pontuação fica de fora: o símbolo de cada uma muda conforme o layout do teclado (ABNT2, US...).
const LABEL = {
  Space: "Espaço", PageUp: "PgUp", PageDown: "PgDn", Left: "←", Up: "↑", Right: "→", Down: "↓", Insert: "Ins", Delete: "Del", Scroll: "ScrLk",
  Multiply: "Num *", Add: "Num +", Subtract: "Num -", Decimal: "Num ,", Divide: "Num /",
};

function keyName(code) {
  if (code >= 65 && code <= 90) return String.fromCharCode(code);
  if (code >= 48 && code <= 57) return `D${code - 48}`;
  if (code >= 96 && code <= 105) return `NumPad${code - 96}`;
  if (code >= 112 && code <= 135) return `F${code - 111}`;
  return NAMED[code] || null;
}

export function keyLabel(part) {
  if (/^D\d$/.test(part)) return part.slice(1);
  if (/^NumPad\d$/.test(part)) return `Num ${part.slice(6)}`;
  return LABEL[part] || part;
}

export function Keys({ combo, className }) {
  const parts = (combo || "").split("+").map((p) => p.trim()).filter(Boolean);
  return (
    <span className={cx("inline-flex items-center gap-1", className)}>
      {parts.map((p, i) => <kbd key={i} className="keycap">{keyLabel(p)}</kbd>)}
    </span>
  );
}

/**
 * Grava um atalho global pelas teclas: clique, segure Ctrl/Alt/Shift e aperte a tecla.
 * onSave(combo) deve rejeitar com a mensagem do host se o Windows recusar a combinação.
 */
export function HotkeyInput({ value, onSave, defaultValue = "Ctrl+Shift+F10" }) {
  const [rec, setRec] = useState(false);
  const [held, setHeld] = useState([]);
  const [hint, setHint] = useState(null);
  const [saving, setSaving] = useState(false);
  const ref = useRef(null);
  const recRef = useRef(false);

  const stop = () => {
    if (!recRef.current) return;
    recRef.current = false;
    setRec(false); setHeld([]); setHint(null);
    call("overlay.captureHotkey", { on: false }).catch(() => {});
  };
  const start = () => {
    recRef.current = true;
    setRec(true); setHint(null); setHeld([]);
    // solta o atalho atual para a própria combinação poder ser lida de novo
    call("overlay.captureHotkey", { on: true }).catch(() => {});
  };
  useEffect(() => () => { if (recRef.current) call("overlay.captureHotkey", { on: false }).catch(() => {}); }, []);

  const save = async (combo) => {
    recRef.current = false; // o host religa o atalho ao gravar
    setRec(false); setHeld([]); setSaving(true);
    try { await onSave(combo); setHint(null); }
    catch (e) { setHint(e.message); call("overlay.captureHotkey", { on: false }).catch(() => {}); }
    setSaving(false);
    ref.current?.blur();
  };

  const mods = (e) => [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift"].filter(Boolean);
  const onKeyDown = (e) => {
    if (!rec) { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); start(); } return; }
    e.preventDefault(); e.stopPropagation();
    if (e.key === "Escape") { stop(); ref.current?.blur(); return; }
    const m = mods(e);
    setHeld(m);
    if (["Control", "Alt", "Shift", "Meta", "AltGraph"].includes(e.key)) return;
    const name = keyName(e.keyCode);
    if (!name) { setHint("Essa tecla não serve para atalho. Use letras, números, F1 a F12, setas ou o teclado numérico."); return; }
    const fn = /^F\d+$/.test(name);
    if (!e.ctrlKey && !e.altKey && !fn) { setHint("Segure Ctrl ou Alt junto com a tecla (as teclas F1 a F12 podem ir sozinhas)."); return; }
    save([...m, name].join("+"));
  };

  return (
    <div className="flex flex-col items-end gap-1.5">
      <div className="flex items-center gap-1.5">
        <button
          ref={ref} type="button" className="hotkey" data-rec={rec}
          onClick={() => (rec ? null : start())}
          onKeyDown={onKeyDown}
          onKeyUp={(e) => rec && setHeld(mods(e))}
          onBlur={stop}
          aria-label={rec ? "Gravando atalho: pressione a combinação" : `Atalho atual: ${value}. Clique para trocar`}
        >
          {rec ? (
            <>
              <span className="size-2 shrink-0 rounded-full bg-[var(--brand)] animate-breathe" />
              {held.length ? <><Keys combo={held.join("+")} /><span className="text-[var(--text-tertiary)]">+ …</span></> : <span className="text-[var(--text-secondary)]">Pressione as teclas…</span>}
            </>
          ) : (
            <>
              <Keys combo={value} />
              <span className="flex-1" />
              <Icon name={saving ? "progress_activity" : "keyboard"} size={16} className={cx("text-[var(--text-tertiary)]", saving && "animate-spin-slow")} />
            </>
          )}
        </button>
        {value !== defaultValue && !rec && (
          <button type="button" title={`Voltar ao padrão (${defaultValue.replaceAll("+", " + ")})`} aria-label="Voltar ao atalho padrão" onClick={() => save(defaultValue)}
            className="inline-flex size-9 items-center justify-center rounded-[var(--radius-sm)] text-[var(--text-tertiary)] transition-colors hover:bg-[var(--bg-subtle)] hover:text-[var(--text-primary)]">
            <Icon name="restart_alt" size={18} />
          </button>
        )}
      </div>
      {rec && !hint && <p className="text-[11.5px] text-[var(--text-tertiary)]">Esc cancela</p>}
      {hint && <p className="max-w-[300px] text-right text-[11.5px] text-[var(--color-warning)]" role="alert">{hint}</p>}
    </div>
  );
}
