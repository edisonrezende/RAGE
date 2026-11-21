import { useEffect, useId, useState } from "react";
import { Link, NavLink, useLocation } from "react-router-dom";
import { NAV, SETTINGS_PAGE, pageFor } from "../nav.js";
import { useApp } from "../App.jsx";
import { useTelemetry, getPrimaryGpu } from "../lib/store.js";
import { call } from "../lib/rage.js";
import * as f from "../lib/format.js";
import { Icon, IconButton, cx, Spinner } from "./ui.jsx";

// Logo oficial do RAGE! (assets/rage-logo.svg)
const LOGO_PATH = "M759.9,741.9c-4.9,0.2-7.8,0.5-10.8,0.5c-45.3,0-90.6-0.3-135.9,0.2c-16.8,0.2-29.7-6.5-40-18.8c-48.5-57.6-96.7-115.4-145-173.1c-2.1-2.5-4.4-4.8-7.5-8.1c-10.9,12.9-21.1,24.9-31.3,37c-16,19-32,37.8-47.7,57c-4.2,5.2-6.6,4.4-10.5-0.3c-17.6-21.2-35.6-42-53.2-63.2c-17-20.4-16.5-45.6,1.2-67.1c15.5-18.9,31.5-37.5,47.2-56.3c2.7-3.2,5.4-6.5,8.5-10.3c-27.8-33.3-55.3-66.2-84.4-101c5.4-0.4,8.3-0.8,11.2-0.8c158,0,315.9,0,473.9-0.1c16.9,0,30.1,7,40.7,19.5c13.8,16.4,27.4,32.9,41.1,49.4c17,20.4,16.3,46.9-0.7,67.1c-37.3,44.2-74.2,88.7-111.3,133.1c-7.5,8.9-14.7,18-22.6,26.5c-5.5,5.9-5.3,10.3,0,16.4c24.5,28.6,48.5,57.6,72.7,86.4C756.5,737.1,757.4,738.6,759.9,741.9z M424.6,537.7c1.8,0.7,2.6,1.3,3.4,1.3c51.7,0.1,103.4,0.2,155.1-0.2c3.8,0,8.6-3,11.3-6.1c14.3-16.3,28.1-33.1,42.1-49.7c11.3-13.5,22.5-27.1,34.5-41.5c-2.5-0.5-3.6-0.9-4.7-0.9c-51.4,0-102.8-0.1-154.2,0.3c-3.6,0-8.1,2.7-10.6,5.5c-13.6,15.4-26.6,31.1-39.8,46.8C449.4,507.9,437.2,522.6,424.6,537.7z";

export function LogoMark({ size = 24, className, color = "currentColor" }) {
  return (
    <svg viewBox="160 300 760 480" width={size} height={size * (480 / 760)} className={className} aria-hidden="true">
      <path d={LOGO_PATH} fill={color} />
    </svg>
  );
}

export function Logo({ size = 30 }) {
  const id = useId().replace(/:/g, "");
  return (
    <span className="relative inline-flex shrink-0 overflow-hidden rounded-[22%] shadow-[0_4px_14px_-4px_#ff5a1fb0]" style={{ width: size, height: size }} title="RAGE!">
      <svg viewBox="0 0 1080 1080" width={size} height={size} aria-hidden="true">
        <defs>
          <linearGradient id={`lg${id}`} gradientUnits="userSpaceOnUse" x1="1156.0625" y1="971.3716" x2="-76.0624" y2="108.6284">
            <stop offset="0" stopColor="#FF7F07" />
            <stop offset="1" stopColor="#FF3C32" />
          </linearGradient>
        </defs>
        <rect width="1080" height="1080" fill={`url(#lg${id})`} />
        <path d={LOGO_PATH} fill="#fff" />
      </svg>
    </span>
  );
}

function SideLink({ item, end }) {
  return (
    <NavLink
      to={item.to}
      end={end}
      className={({ isActive }) =>
        cx(
          "group relative flex h-8 items-center gap-2.5 rounded-[var(--radius-sm)] px-2.5 text-[13px] font-medium transition-colors",
          isActive ? "bg-[var(--chrome-surface-active)] text-[var(--chrome-text)]" : "text-[var(--chrome-text-muted)] hover:bg-[var(--chrome-surface)] hover:text-[var(--chrome-text)]",
        )
      }
    >
      {({ isActive }) => (
        <>
          {isActive && <span className="brand-bg absolute top-1.5 bottom-1.5 left-0 w-[3px] rounded-r-full" />}
          <Icon name={item.icon} size={18} className={isActive ? "text-[var(--brand-bright)]" : ""} />
          <span className="truncate">{item.label}</span>
        </>
      )}
    </NavLink>
  );
}

function Group({ group }) {
  const { pathname } = useLocation();
  const key = "nav:" + group.group;
  const containsActive = group.items.some((i) => (i.to === "/" ? pathname === "/" : pathname.startsWith(i.to)));
  const [open, setOpen] = useState(() => {
    try { const v = localStorage.getItem(key); return v == null ? containsActive : v === "1"; } catch { return containsActive; }
  });
  useEffect(() => { if (containsActive && !open) setOpen(true); }, [containsActive]); // eslint-disable-line react-hooks/exhaustive-deps
  const toggle = () => setOpen((o) => { try { localStorage.setItem(key, o ? "0" : "1"); } catch { /* */ } return !o; });
  const items = group.group === "RAGE! FERRAMENTAS" ? group.items.slice(0, 1).concat(group.items.filter((i) => pathname === i.to && i.to !== "/ferramentas")) : group.items;
  return (
    <div className="mt-3">
      <button onClick={toggle} className="flex h-7 w-full items-center gap-2 rounded-[var(--radius-sm)] px-2.5 text-left text-[10.5px] font-bold tracking-[0.08em] text-[var(--chrome-text-muted)] hover:text-[var(--chrome-text)]">
        <span className="flex-1">{group.group}</span>
        <Icon name="expand_more" size={16} className={cx("transition-transform duration-200", !open && "-rotate-90")} />
      </button>
      {open && <div className="mt-0.5 flex flex-col gap-0.5">{items.map((i) => <SideLink key={i.to} item={i} end={i.to === "/ferramentas"} />)}</div>}
    </div>
  );
}

function MiniStats() {
  const s = useTelemetry();
  const { boost } = useApp();
  const g = getPrimaryGpu(s);
  const row = (label, a, b, hot) => (
    <div className="flex items-center gap-2 text-[11.5px]">
      <span className="w-9 font-semibold text-[var(--chrome-text-muted)]">{label}</span>
      <span className={cx("flex-1 font-semibold tabular", hot ? "text-[#ff8a5c]" : "text-[var(--chrome-text)]")}>{a}</span>
      <span className="tabular text-[var(--chrome-text-muted)]">{b}</span>
    </div>
  );
  return (
    <div className="rounded-[var(--radius-md)] border border-[var(--chrome-border)] bg-[var(--chrome-surface)] px-3 py-2.5">
      <div className="flex flex-col gap-1">
        {row("CPU", f.temp(s?.cpu?.temp, 1), f.pct(s?.cpu?.load), s?.cpu?.temp >= 85)}
        {row("GPU", g?.state === "sleep" ? "em repouso" : f.temp(g?.temp, 0), f.pct(g?.load), g?.temp >= 85)}
        {row("RAM", f.gb(s?.ram?.used), f.pct(s?.ram?.pct))}
        <div className="flex items-center gap-2 text-[11.5px]">
          <span className="w-9 font-semibold text-[var(--chrome-text-muted)]">TURBO</span>
          <span className={cx("flex items-center gap-1.5 font-semibold", boost?.active ? "text-[#ff8a5c]" : "text-[var(--chrome-text-muted)]")}>
            <span className={cx("size-1.5 rounded-full", boost?.active ? "bg-[#ff6a2b] animate-breathe" : "bg-[#555962]")} />
            {boost?.active ? (boost.mode === "game" ? "jogo" : boost.mode === "work" ? "trabalho" : "economia") : "parado"}
          </span>
        </div>
        <Link to="/overclock" className="flex items-center gap-2 text-[11.5px]">
          <span className="w-9 font-semibold text-[var(--chrome-text-muted)]">OC</span>
          <span className={cx("flex items-center gap-1.5 font-semibold", s?.tune?.active ? "text-[#ff8a5c]" : "text-[var(--chrome-text-muted)]")}>
            {s?.tune?.active && <Icon name="local_fire_department" size={13} fill />}
            {s?.tune?.active ? ({ street: "Stage 1", track: "Stage 2", rage: "RAGE!" }[s.tune.stage] || "envenenado") : "original"}
          </span>
        </Link>
      </div>
    </div>
  );
}

function Sidebar() {
  const { info } = useApp();
  return (
    <aside className="relative flex h-full w-[248px] shrink-0 flex-col border-r border-[var(--chrome-border)]" style={{ background: "var(--chrome-bg)" }}>
      <div className="pointer-events-none absolute inset-0" style={{ background: "var(--chrome-flare)" }} />
      <div className="relative z-10 flex h-full min-h-0 flex-col">
        <Link to="/" className="flex h-14 shrink-0 items-center gap-2.5 px-4">
          <Logo />
          <span className="heading text-[19px] font-extrabold tracking-tight text-[var(--chrome-text)]">RAGE<span className="brand-text">!</span></span>
        </Link>
        <nav className="scrollbar-hide min-h-0 flex-1 overflow-y-auto px-2.5 pb-3">
          {NAV.map((n) => (n.items ? <Group key={n.group} group={n} /> : <div key={n.to} className="mt-1"><SideLink item={n} end /></div>))}
        </nav>
        <div className="shrink-0 space-y-2 px-2.5 pb-3">
          <SideLink item={SETTINGS_PAGE} />
          <MiniStats />
          <p className="px-1 text-[10.5px] text-[var(--chrome-text-muted)]">v{info?.version || "1.0.0"}{info?.portable ? " · portátil" : ""}</p>
        </div>
      </div>
    </aside>
  );
}

function TurboButton() {
  const { boost, setBoost, settings, toast } = useApp();
  const [busy, setBusy] = useState(false);
  const active = boost?.active;
  const click = async () => {
    setBusy(true);
    try {
      const r = active ? await call("boost.stop") : await call("boost.start", { mode: settings?.boostMode || "game" });
      setBoost(r);
      toast(active ? "Modo Turbo desligado — tudo restaurado" : "Modo Turbo ligado", active ? "neutral" : "brand");
    } catch (e) { toast(e.message, "danger"); }
    setBusy(false);
  };
  return (
    <button
      onClick={click}
      disabled={busy}
      title={active ? "Desligar o Modo Turbo" : "Ligar o Modo Turbo"}
      className={cx(
        "inline-flex h-8 items-center gap-1.5 rounded-full px-3.5 text-[12px] font-bold tracking-wide transition-all",
        active ? "brand-bg text-white glow" : "border border-[var(--border)] bg-[var(--bg-surface)] text-[var(--text-secondary)] hover:border-[var(--brand)] hover:text-[var(--brand)]",
      )}
    >
      {busy ? <Spinner size={14} /> : <Icon name="rocket_launch" size={16} fill={active} />}
      {active ? "TURBO LIGADO" : "TURBO"}
    </button>
  );
}

function Header() {
  const { pathname } = useLocation();
  const page = pageFor(pathname);
  const { theme, setTheme, info, toast } = useApp();
  useEffect(() => { document.title = page.to === "/" ? "RAGE!" : `${page.title} · RAGE!`; }, [page]);
  const elevate = () => call("app.elevate").catch((e) => toast(e.message, "danger"));
  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b border-[var(--border)] bg-[var(--bg-surface)] px-6">
      <div className="min-w-0 flex-1">
        <h1 className="heading truncate text-[16px] font-semibold leading-tight">{page.title}</h1>
        <p className="truncate text-[12px] text-[var(--text-tertiary)]">{page.subtitle}</p>
      </div>
      {info && !info.isAdmin && (
        <button onClick={elevate} className="inline-flex h-8 items-center gap-1.5 rounded-full bg-[var(--color-warning-soft)] px-3 text-[12px] font-semibold text-[var(--color-warning)] hover:brightness-110" title="Limpeza, serviços, sensores e FPS precisam de administrador">
          <Icon name="admin_panel_settings" size={16} /> Modo limitado · abrir como administrador
        </button>
      )}
      <TurboButton />
      <IconButton icon={theme === "dark" ? "dark_mode" : "light_mode"} label="Alternar tema" onClick={() => setTheme(theme === "dark" ? "light" : "dark")} />
    </header>
  );
}

export function Shell({ children }) {
  const { pathname } = useLocation();
  return (
    <div className="flex h-full overflow-hidden">
      <Sidebar />
      <div className="flex min-w-0 flex-1 flex-col bg-[var(--bg-base)]" style={{ backgroundImage: "var(--canvas-flare)" }}>
        <Header />
        <main key={pathname} className="scroll-area min-h-0 flex-1 overflow-y-auto">
          <div className="page-enter mx-auto w-full max-w-[1500px] px-6 pt-5 pb-10">{children}</div>
        </main>
      </div>
    </div>
  );
}

/** Cabeçalho de seção dentro das páginas */
export function PageIntro({ icon, title, children, actions, tone = "brand" }) {
  return (
    <div className="mb-5 flex flex-wrap items-center gap-4">
      {icon && (
        <span className={cx("flex size-11 shrink-0 items-center justify-center rounded-[var(--radius-lg)]", tone === "brand" ? "brand-bg text-white shadow-[0_8px_22px_-10px_#ff5a1f]" : "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]")}>
          <Icon name={icon} size={24} />
        </span>
      )}
      <div className="min-w-0 flex-1">
        {title && <h2 className="heading text-[20px] font-semibold">{title}</h2>}
        {children && <div className="mt-0.5 max-w-3xl text-[13px] text-[var(--text-secondary)]">{children}</div>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
}
