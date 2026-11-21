import { useSearchParams } from "react-router-dom";
import { ScanFlow } from "../components/ScanFlow.jsx";
import { Tabs } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

export default function Cleaner() {
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") === "privacy" ? "privacy" : "junk";
  return (
    <div className="space-y-4">
      <PageIntro icon={tab === "junk" ? "delete_sweep" : "visibility_off"} title={tab === "junk" ? "Arquivos inúteis" : "Rastros de privacidade"}
        actions={<Tabs size="md" value={tab} onChange={(v) => setParams(v === "junk" ? {} : { tab: v })} items={[{ value: "junk", label: "Arquivos inúteis", icon: "delete_sweep" }, { value: "privacy", label: "Privacidade", icon: "visibility_off" }]} />}>
        {tab === "junk"
          ? "Temporários do Windows, caches de navegadores, apps e jogos, relatórios de erro e a Lixeira. Arquivos em uso são pulados automaticamente."
          : "Documentos recentes, listas de atalhos e históricos. Histórico e cookies dos navegadores vêm desmarcados — feche o navegador antes de limpar."}
      </PageIntro>
      <ScanFlow key={tab} categories={[tab]} autoScan fixLabel={tab === "junk" ? "Limpar selecionados" : "Apagar rastros"} emptyText={tab === "junk" ? "Nenhum lixo encontrado" : "Nenhum rastro encontrado"} />
    </div>
  );
}
