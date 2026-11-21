import { useNavigate, useSearchParams } from "react-router-dom";
import { ScanFlow } from "../components/ScanFlow.jsx";
import { Button, Tabs } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

export default function Registry() {
  const [params, setParams] = useSearchParams();
  const nav = useNavigate();
  const tab = params.get("tab") === "shortcuts" ? "shortcuts" : "registry";
  return (
    <div className="space-y-4">
      <PageIntro icon={tab === "registry" ? "account_tree" : "link_off"} title={tab === "registry" ? "Registro do Windows" : "Atalhos inválidos"}
        actions={<>
          <Tabs size="md" value={tab} onChange={(v) => setParams(v === "registry" ? {} : { tab: v })} items={[{ value: "registry", label: "Registro", icon: "account_tree" }, { value: "shortcuts", label: "Atalhos", icon: "link_off" }]} />
          <Button variant="ghost" icon="restore" onClick={() => nav("/resgate")}>Backups</Button>
        </>}>
        {tab === "registry"
          ? "Limpeza conservadora: só entradas que apontam para programas e arquivos que não existem mais. Tudo é salvo em um arquivo .reg antes — dá para desfazer pela Central de resgate."
          : "Atalhos da Área de Trabalho e do Menu Iniciar que apontam para programas removidos. Eles são movidos para a pasta de backup, não apagados."}
      </PageIntro>
      <ScanFlow key={tab} categories={[tab]} autoScan compact fixLabel={tab === "registry" ? "Corrigir com backup" : "Remover atalhos"} emptyText={tab === "registry" ? "Registro sem problemas" : "Nenhum atalho quebrado"} />
    </div>
  );
}
