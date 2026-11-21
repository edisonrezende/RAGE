import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

export const cx = (...c) => c.filter(Boolean).join(" ");

export function Icon({ name, size = 20, className = "", fill = false, style }) {
  return (
    <span className={cx("material-symbols-outlined", fill && "icon-fill", className)} style={{ fontSize: size, width: size, height: size, ...style }} aria-hidden="true">
      {name}
    </span>
  );
}

const BTN = {
  base: "inline-flex shrink-0 items-center justify-center gap-1.5 whitespace-nowrap font-medium select-none transition-[background-color,box-shadow,color,transform,border-color,filter] duration-150 ease-out disabled:pointer-events-none disabled:opacity-40 active:translate-y-px focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--border-focus)] rounded-[var(--radius-sm)] border",
  primary: "border-transparent bg-[var(--bg-inverse)] text-[var(--text-inverse)] shadow-[var(--shadow-ink)] hover:opacity-90 dark:bg-white dark:text-[#111]",
  brand: "border-transparent brand-bg text-white shadow-[0_6px_18px_-6px_#ff5a1f99] hover:brightness-110",
  secondary: "border-[var(--border)] bg-[var(--bg-surface)] text-[var(--text-primary)] shadow-[var(--shadow-control-flat)] hover:bg-[var(--bg-subtle)] hover:border-[var(--border-default)]",
  ghost: "border-transparent bg-transparent text-[var(--text-secondary)] hover:bg-[var(--bg-subtle)] hover:text-[var(--text-primary)]",
  danger: "border-transparent bg-[var(--color-danger)] text-white hover:opacity-90",
  sm: "h-7 px-2.5 text-[13px]",
  md: "h-8 px-3 text-[13px]",
  lg: "h-10 px-4 text-sm",
  xl: "h-12 px-6 text-[15px] rounded-[var(--radius-md)]",
};

export function Button({ variant = "secondary", size = "md", icon, iconRight, loading, children, className, ...p }) {
  return (
    <button className={cx(BTN.base, BTN[variant], BTN[size], className)} disabled={loading || p.disabled} {...p}>
      {loading ? <Spinner size={size === "sm" ? 14 : 16} /> : icon && <Icon name={icon} size={size === "sm" ? 16 : size === "xl" ? 22 : 18} />}
      {children}
      {iconRight && <Icon name={iconRight} size={16} className="opacity-70" />}
    </button>
  );
}

export function IconButton({ icon, label, size = 20, className, active, ...p }) {
  return (
    <button
      aria-label={label}
      title={label}
      className={cx(
        "inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-[var(--text-secondary)] transition-colors hover:bg-[var(--bg-layer-2)] hover:text-[var(--text-primary)] disabled:opacity-40",
        active && "bg-[var(--bg-subtle)] text-[var(--text-primary)]",
        className,
      )}
      {...p}
    >
      <Icon name={icon} size={size} />
    </button>
  );
}

export function Spinner({ size = 16, className }) {
  return <span className={cx("inline-block animate-spin-slow rounded-full border-2 border-current border-t-transparent", className)} style={{ width: size, height: size }} />;
}

export function Chip({ children, tone = "neutral", className, dot, icon }) {
  const tones = {
    neutral: "bg-[var(--bg-layer-2)] text-[var(--text-secondary)]",
    info: "bg-[var(--chip-info-bg)] text-[var(--chip-info-text)]",
    warning: "bg-[var(--color-warning-soft)] text-[var(--color-warning)]",
    success: "bg-[var(--color-success-soft)] text-[var(--color-success)]",
    danger: "bg-[var(--color-danger-soft)] text-[var(--color-danger)]",
    brand: "bg-[var(--brand-primary-soft)] text-[var(--brand-primary-strong)]",
  };
  return (
    <span className={cx("inline-flex h-5 shrink-0 items-center gap-1 rounded-full px-2 text-[11px] font-semibold whitespace-nowrap", tones[tone], className)}>
      {dot && <span className="size-1.5 rounded-full bg-current" />}
      {icon && <Icon name={icon} size={13} />}
      {children}
    </span>
  );
}

export function Tabs({ items, value, onChange, className, size = "sm" }) {
  return (
    <div role="tablist" className={cx("scrollbar-hide inline-flex max-w-full gap-0.5 overflow-x-auto rounded-[var(--radius-md)] border border-[var(--border-subtle)] bg-[var(--bg-base)] p-0.5", className)}>
      {items.map((it) => {
        const active = it.value === value;
        return (
          <button
            key={it.value}
            role="tab"
            aria-selected={active}
            disabled={it.disabled}
            onClick={() => onChange?.(it.value)}
            className={cx(
              "inline-flex items-center gap-1.5 whitespace-nowrap rounded-[var(--radius-sm)] px-2.5 transition-[color,background,box-shadow] duration-150 disabled:opacity-40",
              size === "sm" ? "h-7 text-[12px]" : "h-8 text-[13px]",
              active ? "bg-[var(--bg-layer-1)] font-semibold text-[var(--text-primary)] shadow-[var(--shadow-elevate-sm)] dark:bg-[var(--bg-layer-3)]" : "font-medium text-[var(--text-tertiary)] hover:text-[var(--text-secondary)]",
            )}
          >
            {it.icon && <Icon name={it.icon} size={16} />}
            {it.label}
            {it.badge != null && <span className="rounded-full bg-[var(--bg-layer-2)] px-1.5 text-[10px] tabular dark:bg-[var(--bg-base)]">{it.badge}</span>}
          </button>
        );
      })}
    </div>
  );
}

export function Toggle({ checked, onChange, disabled, size = "md", label }) {
  const w = size === "sm" ? 28 : 34, h = size === "sm" ? 16 : 20, k = h - 4;
  return (
    <button
      role="switch"
      aria-checked={!!checked}
      aria-label={label}
      disabled={disabled}
      onClick={(e) => { e.stopPropagation(); onChange?.(!checked); }}
      className={cx("relative shrink-0 rounded-full transition-colors duration-200 disabled:opacity-40", checked ? "brand-bg" : "bg-[var(--bg-layer-3)] dark:bg-[#3a3a40]")}
      style={{ width: w, height: h }}
    >
      <span className="absolute top-[2px] rounded-full bg-white shadow transition-transform duration-200" style={{ width: k, height: k, left: 2, transform: `translateX(${checked ? w - k - 4 : 0}px)` }} />
    </button>
  );
}

export function Checkbox({ checked, indeterminate, onChange, disabled, className }) {
  return (
    <button
      role="checkbox"
      aria-checked={indeterminate ? "mixed" : !!checked}
      disabled={disabled}
      onClick={(e) => { e.stopPropagation(); onChange?.(!checked); }}
      className={cx(
        "inline-flex size-[18px] shrink-0 items-center justify-center rounded-[5px] border transition-colors disabled:opacity-40",
        checked || indeterminate ? "border-transparent brand-bg text-white" : "border-[var(--border-strong)] bg-transparent hover:border-[var(--text-secondary)]",
        className,
      )}
    >
      {indeterminate ? <Icon name="remove" size={14} /> : checked ? <Icon name="check" size={14} className="font-bold" /> : null}
    </button>
  );
}

export function Progress({ value, className, tone = "brand", height = 6, indeterminate }) {
  const color = tone === "brand" ? "var(--brand-gradient)" : tone === "success" ? "var(--color-success)" : tone === "warning" ? "var(--color-warning)" : tone === "danger" ? "var(--color-danger)" : tone;
  return (
    <div className={cx("w-full overflow-hidden rounded-full bg-[var(--bg-layer-2)] dark:bg-[var(--bg-layer-3)]", className)} style={{ height }}>
      <div
        className={cx("h-full rounded-full transition-[width] duration-500 ease-out", indeterminate && "indeterminate")}
        style={{ width: indeterminate ? "100%" : `${Math.max(0, Math.min(100, value || 0))}%`, background: color }}
      />
    </div>
  );
}

export function Panel({ title, subtitle, icon, actions, children, className, bodyClass, padded = true }) {
  return (
    <section className={cx("panel flex min-w-0 flex-col", className)}>
      {(title || actions) && (
        <header className="flex min-h-12 items-center gap-2.5 px-4 pt-3 pb-1">
          {icon && <Icon name={icon} size={18} className="text-[var(--text-tertiary)]" />}
          <div className="min-w-0 flex-1">
            {title && <h3 className="truncate text-[13px] font-semibold text-[var(--text-primary)]">{title}</h3>}
            {subtitle && <p className="truncate text-[12px] text-[var(--text-tertiary)]">{subtitle}</p>}
          </div>
          {actions}
        </header>
      )}
      <div className={cx("min-h-0 flex-1", padded && "px-4 pb-4 pt-2", bodyClass)}>{children}</div>
    </section>
  );
}

export function Label({ children, className }) {
  return <p className={cx("text-[11px] font-semibold tracking-[0.06em] text-[var(--text-tertiary)] uppercase", className)}>{children}</p>;
}

export function Empty({ icon = "inbox", title, children, action, className }) {
  return (
    <div className={cx("flex flex-col items-center justify-center gap-2 px-6 py-10 text-center", className)}>
      <span className="mb-1 flex size-12 items-center justify-center rounded-full bg-[var(--bg-layer-2)] text-[var(--text-tertiary)]"><Icon name={icon} size={24} /></span>
      {title && <p className="text-sm font-semibold">{title}</p>}
      {children && <div className="max-w-md text-[13px] text-[var(--text-tertiary)]">{children}</div>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}

export function Skeleton({ className, style }) {
  return <div className={cx("skeleton", className)} style={style} />;
}

export function ErrorBox({ error, onRetry }) {
  if (!error) return null;
  return (
    <div className="flex items-center gap-3 rounded-[var(--radius-md)] border border-[var(--color-danger-soft)] bg-[var(--color-danger-soft)] px-3 py-2.5 text-[13px] text-[var(--color-danger)]">
      <Icon name="error" size={18} />
      <span className="flex-1">{error}</span>
      {onRetry && <Button size="sm" variant="ghost" onClick={onRetry}>Tentar de novo</Button>}
    </div>
  );
}

export function Modal({ open, onClose, children, width = 520, className, dismissable = true }) {
  useEffect(() => {
    if (!open || !dismissable) return;
    const onKey = (e) => e.key === "Escape" && onClose?.();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose, dismissable]);
  if (!open) return null;
  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-[var(--bg-overlay)] p-4 backdrop-blur-[2px]" onMouseDown={(e) => dismissable && e.target === e.currentTarget && onClose?.()}>
      <div role="dialog" aria-modal="true" className={cx("animate-pop max-h-[calc(100vh-2rem)] w-full overflow-hidden rounded-[var(--radius-pop)] border border-[var(--border)] bg-[var(--bg-surface)] shadow-[var(--shadow-pop)]", className)} style={{ maxWidth: width }}>
        {children}
      </div>
    </div>,
    document.body,
  );
}

export function Confirm({ open, title, children, confirmLabel = "Confirmar", tone = "brand", icon = "help", onConfirm, onClose, busy }) {
  return (
    <Modal open={open} onClose={onClose} width={440}>
      <div className="p-5">
        <div className="flex gap-3">
          <span className={cx("flex size-10 shrink-0 items-center justify-center rounded-full", tone === "danger" ? "bg-[var(--color-danger-soft)] text-[var(--color-danger)]" : "bg-[var(--brand-primary-soft)] text-[var(--brand)]")}>
            <Icon name={icon} size={22} />
          </span>
          <div className="min-w-0 flex-1">
            <h3 className="heading text-base font-semibold">{title}</h3>
            <div className="mt-1.5 text-[13px] leading-relaxed text-[var(--text-secondary)]">{children}</div>
          </div>
        </div>
        <div className="mt-5 flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancelar</Button>
          <Button variant={tone === "danger" ? "danger" : "brand"} onClick={onConfirm} loading={busy}>{confirmLabel}</Button>
        </div>
      </div>
    </Modal>
  );
}

export function Menu({ trigger, items, align = "start", width = 210, className }) {
  const [open, setOpen] = useState(false);
  const ref = useRef(null);
  useEffect(() => {
    if (!open) return;
    const close = (e) => !ref.current?.contains(e.target) && setOpen(false);
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);
  return (
    <div ref={ref} className={cx("relative inline-flex", className)}>
      {trigger({ open, toggle: () => setOpen((o) => !o) })}
      {open && (
        <div className={cx("animate-pop absolute top-full z-40 mt-1 rounded-[var(--radius-md)] border border-[var(--border)] bg-[var(--bg-surface)] p-1 shadow-[var(--shadow-pop)]", align === "end" ? "right-0" : "left-0")} style={{ width }}>
          {items.filter(Boolean).map((it, i) =>
            it.divider ? <div key={i} className="my-1 h-px bg-[var(--border)]" /> : (
              <button
                key={i}
                disabled={it.disabled}
                onClick={(e) => { e.stopPropagation(); setOpen(false); it.onClick?.(); }}
                className={cx("flex h-8 w-full items-center gap-2 rounded-[var(--radius-sm)] px-2 text-left text-[13px] hover:bg-[var(--bg-subtle)] disabled:opacity-40", it.danger ? "text-[var(--color-danger)]" : "text-[var(--text-primary)]")}
              >
                {it.icon && <Icon name={it.icon} size={17} className={it.danger ? "" : "text-[var(--text-tertiary)]"} />}
                <span className="flex-1 truncate">{it.label}</span>
                {it.checked && <Icon name="check" size={16} />}
              </button>
            ),
          )}
        </div>
      )}
    </div>
  );
}

export function InfoTip({ text, size = 16 }) {
  return <span title={text} className="inline-flex cursor-help text-[var(--text-muted)]"><Icon name="info" size={size} /></span>;
}

export function Segmented({ options, value, onChange, className }) {
  return (
    <div className={cx("inline-flex rounded-[var(--radius-md)] border border-[var(--border)] bg-[var(--bg-base)] p-0.5", className)}>
      {options.map((o) => (
        <button
          key={o.value}
          onClick={() => onChange(o.value)}
          className={cx("inline-flex h-7 items-center gap-1.5 rounded-[var(--radius-sm)] px-3 text-[12px] font-semibold transition-colors",
            value === o.value ? "bg-white text-[#111] shadow-[var(--shadow-1)]" : "text-[var(--text-tertiary)] hover:text-[var(--text-primary)]")}
        >
          {o.icon && <Icon name={o.icon} size={15} />}
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function StatusDot({ status, className }) {
  const c = status === "ok" ? "bg-[var(--color-success)]" : status === "warn" ? "bg-[var(--color-warning)]" : status === "bad" ? "bg-[var(--color-danger)]" : "bg-[var(--text-muted)]";
  return <span className={cx("inline-block size-2 shrink-0 rounded-full", c, className)} />;
}

export function KV({ label, value, mono, className }) {
  return (
    <div className={cx("flex items-baseline justify-between gap-3 py-1.5 text-[13px]", className)}>
      <span className="shrink-0 text-[var(--text-tertiary)]">{label}</span>
      <span className={cx("min-w-0 truncate text-right font-medium text-[var(--text-primary)]", mono && "mono text-[12px]")} title={typeof value === "string" ? value : undefined}>{value ?? "—"}</span>
    </div>
  );
}

/** Console de saída (sfc, winget, DISM...) */
export function LogView({ lines, className, empty = "Aguardando saída…" }) {
  const ref = useRef(null);
  useEffect(() => { if (ref.current) ref.current.scrollTop = ref.current.scrollHeight; }, [lines]);
  return (
    <div ref={ref} data-selectable className={cx("scroll-area mono max-h-64 overflow-auto rounded-[var(--radius-md)] border border-[var(--border)] bg-[#0a0a0c] p-3 text-[11.5px] leading-relaxed text-[#c9ced8]", className)}>
      {lines?.length ? lines.map((l, i) => <div key={i} className="whitespace-pre-wrap break-all">{l || " "}</div>) : <span className="text-[#5d626d]">{empty}</span>}
    </div>
  );
}
