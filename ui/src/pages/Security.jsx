import { useNavigate } from "react-router-dom";
import { ScanFlow } from "../components/ScanFlow.jsx";
import { Button } from "../components/ui.jsx";
import { PageIntro } from "../components/Shell.jsx";

export default function Security() {
  const nav = useNavigate();
  return (
    <div className="space-y-4">
      <PageIntro icon="verified_user" title="Reforço de segurança" actions={<Button icon="coronavirus" onClick={() => nav("/antivirus")}>Antivírus</Button>}>
        Confere as proteções do Windows (Defender, Firewall, UAC, SmartScreen, execução automática de pendrives, SMBv1…) e corrige o que estiver desligado.
      </PageIntro>
      <ScanFlow categories={["security"]} autoScan compact fixLabel="Reforçar segurança" emptyText="Seu Windows está protegido" />
    </div>
  );
}
