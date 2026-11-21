import { useNavigate } from "react-router-dom";
import { TOOLS, ALL_PAGES } from "../nav.js";
import { Icon } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

const MORE = ["/verificacao", "/limpeza", "/registro", "/inicializacao", "/ajustes", "/internet", "/disco", "/antivirus", "/seguranca", "/resgate", "/benchmark", "/stress"];

function Tile({ p, onClick }) {
  return (
    <button onClick={onClick} className="panel panel-hover group flex items-start gap-3 p-4 text-left">
      <span className="flex size-10 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-[var(--brand-primary-soft)] text-[var(--brand)] transition-colors group-hover:brand-bg group-hover:text-white"><Icon name={p.icon} size={21} /></span>
      <div className="min-w-0">
        <p className="text-[13.5px] font-semibold">{p.title}</p>
        <p className="mt-0.5 text-[12px] leading-snug text-[var(--text-tertiary)]">{p.desc || p.subtitle}</p>
      </div>
    </button>
  );
}

export default function Toolbox() {
  const nav = useNavigate();
  return (
    <div className="space-y-6">
      <PageIntro icon="handyman" title="Caixa de ferramentas">Utilitários para cuidar do PC além da limpeza do dia a dia.</PageIntro>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">{TOOLS.map((p) => <Tile key={p.to} p={p} onClick={() => nav(p.to)} />)}</div>
      <div>
        <p className="mb-3 text-[11px] font-bold tracking-[0.08em] text-[var(--text-tertiary)] uppercase">Também no RAGE!</p>
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">{ALL_PAGES.filter((p) => MORE.includes(p.to)).map((p) => <Tile key={p.to} p={p} onClick={() => nav(p.to)} />)}</div>
      </div>
    </div>
  );
}
