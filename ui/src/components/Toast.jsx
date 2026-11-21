import { createPortal } from "react-dom";
import { Icon, cx } from "./ui.jsx";

const ICON = { success: "check_circle", danger: "error", warning: "warning", brand: "rocket_launch", neutral: "info" };

export function ToastHost({ toasts }) {
  return createPortal(
    <div className="pointer-events-none fixed right-5 bottom-5 z-[70] flex w-[340px] flex-col gap-2">
      {toasts.map((t) => (
        <div key={t.id} className={cx("animate-pop pointer-events-auto flex items-start gap-2.5 rounded-[var(--radius-md)] border px-3.5 py-3 text-[13px] shadow-[var(--shadow-pop)]",
          "border-[var(--border)] bg-[var(--bg-surface)] text-[var(--text-primary)]")}>
          <Icon name={ICON[t.tone] || "info"} size={19} fill className={cx(
            t.tone === "success" && "text-[var(--color-success)]",
            t.tone === "danger" && "text-[var(--color-danger)]",
            t.tone === "warning" && "text-[var(--color-warning)]",
            t.tone === "brand" && "text-[var(--brand)]",
            t.tone === "neutral" && "text-[var(--text-tertiary)]",
          )} />
          <span className="flex-1 leading-snug">{t.message}</span>
        </div>
      ))}
    </div>,
    document.body,
  );
}
