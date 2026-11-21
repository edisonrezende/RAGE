// Navegação e metadados das páginas
export const NAV = [
  { to: "/", icon: "monitoring", label: "Visão geral", title: "Visão geral", subtitle: "Monitor do seu PC em tempo real" },
  {
    group: "RAGE! JOGOS", icon: "sports_esports", items: [
      { to: "/turbo", icon: "rocket_launch", label: "Modo Turbo", title: "Modo Turbo", subtitle: "Aceleração para jogar, trabalhar ou economizar bateria" },
      { to: "/jogos", icon: "stadia_controller", label: "Biblioteca de jogos", title: "Biblioteca de jogos", subtitle: "Turbo automático, GPU dedicada, FPS e overlay" },
    ],
  },
  {
    group: "RAGE! DESEMPENHO", icon: "speed", items: [
      { to: "/benchmark", icon: "speed", label: "Benchmark", title: "RAGE! Bench", subtitle: "Pontuação de CPU, memória e disco" },
      { to: "/stress", icon: "local_fire_department", label: "Stress test", title: "Stress test", subtitle: "Carga máxima para testar temperatura e estabilidade" },
      { to: "/overclock", icon: "bolt", label: "Overclock", title: "Overclock", subtitle: "Prepare o PC como um carro: estágios, dinamômetro e ajuste fino" },
    ],
  },
  {
    group: "RAGE! LIMPEZA", icon: "cleaning_services", items: [
      { to: "/verificacao", icon: "health_and_safety", label: "Verificação inteligente", title: "Verificação inteligente", subtitle: "Limpa, corrige e otimiza tudo de uma vez" },
      { to: "/limpeza", icon: "delete_sweep", label: "Limpeza de lixo", title: "Limpeza de lixo", subtitle: "Arquivos inúteis e rastros de privacidade" },
      { to: "/registro", icon: "account_tree", label: "Registro e atalhos", title: "Registro e atalhos", subtitle: "Entradas inválidas com backup automático" },
    ],
  },
  {
    group: "RAGE! OTIMIZAÇÃO", icon: "tune", items: [
      { to: "/inicializacao", icon: "rocket", label: "Inicialização", title: "Inicialização", subtitle: "O que abre junto com o Windows" },
      { to: "/ajustes", icon: "tune", label: "Ajustes do sistema", title: "Ajustes do sistema", subtitle: "Jogos, desempenho e privacidade — tudo reversível" },
      { to: "/internet", icon: "network_check", label: "Internet", title: "Internet", subtitle: "Ping, DNS mais rápido e ajustes de rede" },
      { to: "/disco", icon: "hard_drive", label: "Disco", title: "Disco", subtitle: "Saúde, TRIM e desfragmentação" },
    ],
  },
  {
    group: "RAGE! PROTEÇÃO", icon: "shield", items: [
      { to: "/antivirus", icon: "coronavirus", label: "Antivírus", title: "Antivírus", subtitle: "Microsoft Defender sob o controle do RAGE!" },
      { to: "/seguranca", icon: "verified_user", label: "Reforço de segurança", title: "Reforço de segurança", subtitle: "Brechas comuns do Windows" },
      { to: "/resgate", icon: "restore", label: "Resgate", title: "Central de resgate", subtitle: "Pontos de restauração e backups" },
    ],
  },
  {
    group: "RAGE! FERRAMENTAS", icon: "handyman", items: [
      { to: "/ferramentas", icon: "apps", label: "Todas as ferramentas", title: "Ferramentas", subtitle: "Caixa de ferramentas do RAGE!" },
      { to: "/ferramentas/processos", icon: "memory", label: "Processos", title: "Processos", subtitle: "O que está rodando agora", tool: true, desc: "Veja CPU, RAM e GPU por app e encerre o que travar." },
      { to: "/ferramentas/programas", icon: "apps_outage", label: "Desinstalar programas", title: "Desinstalar programas", subtitle: "Remova programas e as sobras", tool: true, desc: "Desinstale programas e apps da Store e limpe as sobras." },
      { to: "/ferramentas/atualizacoes", icon: "system_update_alt", label: "Atualizar programas", title: "Atualizar programas", subtitle: "Atualizações via winget", tool: true, desc: "Atualize Chrome, Discord, 7-Zip e outros com um clique." },
      { to: "/ferramentas/drivers", icon: "developer_board", label: "Drivers", title: "Drivers", subtitle: "Versões, problemas e backup", tool: true, desc: "Encontre drivers antigos ou com erro e faça backup." },
      { to: "/ferramentas/area-de-trabalho", icon: "auto_awesome_mosaic", label: "Área de trabalho", title: "Organizador da área de trabalho", subtitle: "Atalhos em degradê de cor e arquivos soltos fora do caminho", tool: true, desc: "Arruma os atalhos por cor e fornecedor e limpa os arquivos soltos." },
      { to: "/ferramentas/arquivos-grandes", icon: "folder_zip", label: "Arquivos grandes", title: "Arquivos grandes", subtitle: "O que está ocupando espaço", tool: true, desc: "Ache os arquivos que mais ocupam o disco." },
      { to: "/ferramentas/duplicados", icon: "file_copy", label: "Arquivos duplicados", title: "Arquivos duplicados", subtitle: "Cópias idênticas", tool: true, desc: "Encontre cópias idênticas pelo conteúdo." },
      { to: "/ferramentas/pastas-vazias", icon: "folder_off", label: "Pastas vazias", title: "Pastas vazias", subtitle: "Pastas sem nada dentro", tool: true, desc: "Remova pastas vazias esquecidas." },
      { to: "/ferramentas/triturador", icon: "content_cut", label: "Triturador", title: "Triturador de arquivos", subtitle: "Apague sem chance de recuperação", tool: true, desc: "Apague arquivos sem chance de recuperação." },
      { to: "/ferramentas/sistema", icon: "info", label: "Informações do sistema", title: "Informações do sistema", subtitle: "Hardware e Windows em detalhe", tool: true, desc: "Hardware, Windows, BIOS e bateria em detalhe." },
      { to: "/ferramentas/reparos", icon: "build", label: "Reparos do Windows", title: "Reparos do Windows", subtitle: "SFC, DISM, rede e mais", tool: true, desc: "SFC, DISM, rede, Windows Update e Explorer." },
      { to: "/ferramentas/desligamento", icon: "schedule", label: "Desligamento agendado", title: "Desligamento agendado", subtitle: "Desligar, reiniciar ou hibernar depois", tool: true, desc: "Desligue o PC depois do download ou da partida." },
    ],
  },
];

export const ALL_PAGES = NAV.flatMap((n) => (n.items ? n.items : [n]));
export const TOOLS = ALL_PAGES.filter((p) => p.tool);
export const SETTINGS_PAGE = { to: "/configuracoes", icon: "settings", label: "Configurações", title: "Configurações", subtitle: "Preferências do RAGE!" };

export function pageFor(path) {
  if (path === SETTINGS_PAGE.to) return SETTINGS_PAGE;
  return ALL_PAGES.find((p) => p.to === path) || ALL_PAGES.filter((p) => p.to !== "/" && path.startsWith(p.to)).sort((a, b) => b.to.length - a.to.length)[0] || ALL_PAGES[0];
}
