// Dados simulados para desenvolver a interface no navegador (sem o RAGE!.exe).
const GB = 1073741824;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const rnd = (a, b) => a + Math.random() * (b - a);
let t0 = Date.now();
let emitFn = null;
let boost = { active: false, mode: null, since: null, source: null, auto: false, game: null, actions: [], freed: 0 };
let cpuTemp = 52, gpuTemp = 48, cpuLoad = 18, gpuLoad = 4;
let stress = null;

function snapshot() {
  const s = stress ? 1 : 0;
  cpuLoad = Math.max(2, Math.min(100, s ? rnd(96, 100) : cpuLoad + rnd(-6, 6)));
  gpuLoad = Math.max(0, Math.min(100, gpuLoad + rnd(-4, 4)));
  cpuTemp += ((s ? 88 : 44 + cpuLoad * 0.35) - cpuTemp) * 0.15 + rnd(-0.6, 0.6);
  gpuTemp += (42 + gpuLoad * 0.4 - gpuTemp) * 0.1 + rnd(-0.3, 0.3);
  const used = 6.1 * GB + rnd(-0.1, 0.1) * GB;
  return {
    t: Date.now(),
    cpu: { name: "Intel Core i5-9300H @ 2.40GHz", cores: 4, threads: 8, load: +cpuLoad.toFixed(1), perCore: Array.from({ length: 8 }, () => +rnd(cpuLoad * 0.6, Math.min(100, cpuLoad * 1.4)).toFixed(1)), clock: Math.round(2400 * rnd(1.05, 1.6)), baseClock: 2400, temp: +cpuTemp.toFixed(1), tempSource: "sensor", power: +(8 + cpuLoad * 0.35).toFixed(1), coreTemps: [cpuTemp, cpuTemp - 2, cpuTemp + 1, cpuTemp - 1].map((v) => Math.round(v)) },
    gpus: [
      { id: "0x0_0xD26A", name: "NVIDIA GeForce GTX 1650", vendor: "nvidia", kind: "discrete", state: "active", load: +gpuLoad.toFixed(1), temp: +gpuTemp.toFixed(1), hotSpot: gpuTemp + 8, clock: gpuLoad > 10 ? 1560 : 300, memClock: 405, power: +(3 + gpuLoad * 0.5).toFixed(1), vramUsed: 0.6 * GB, vramTotal: 4 * GB, sharedUsed: 0.1 * GB, fan: null, maxTemp: 102 },
      { id: "0x0_0xC37E", name: "Intel(R) UHD Graphics 630", vendor: "intel", kind: "integrated", state: "active", load: +rnd(1, 6).toFixed(1), temp: null, vramUsed: 0, vramTotal: GB, sharedUsed: 0.3 * GB },
    ],
    ram: { total: 7.83 * GB, used, avail: 7.83 * GB - used, pct: +((used / (7.83 * GB)) * 100).toFixed(1), standby: 0.7 * GB, modified: 0.01 * GB, commitUsed: 15 * GB, commitLimit: 31 * GB, speed: 2667, type: "DDR4", modules: 2, slots: 2 },
    disks: [{ letter: "C:", label: "OS", fs: "NTFS", total: 459 * GB, free: 63.4 * GB, read: rnd(0, 4e6), write: rnd(0, 2e6), active: +rnd(0, 8).toFixed(1), model: "CL1-3D512-Q11 NVMe SSSTC 512GB", media: "SSD", bus: "NVMe", temp: 41, life: 92, system: true }],
    net: { down: rnd(2e3, 3e5), up: rnd(1e3, 4e4), ping: Math.round(rnd(18, 32)), adapter: "Wi-Fi", kind: "wifi", linkMbps: 180 },
    battery: { present: true, pct: 87, plugged: true, charging: true, rateW: 7.8, minutesLeft: null, wear: 24.6 },
    system: { processes: 301, threads: 4190, handles: 153927, uptime: 139359 + Math.round((Date.now() - t0) / 1000) },
    boost,
    game: boost.active ? { pid: 4120, name: "cs2", fps: Math.round(rnd(138, 150)), frameTime: 7.0, low1: 112, isGame: true } : null,
    tune: { active: tune.active, stage: tune.stage },
  };
}

const history = { t: [], cpuTemp: [], cpuLoad: [], cpuPower: [], cpuClock: [], gpuTemp: [], gpuLoad: [], gpuPower: [], ramPct: [], netDown: [], netUp: [], ping: [], fps: [], diskActive: [] };
function seedHistory() {
  const now = Date.now();
  for (let i = 900; i > 0; i--) {
    const s = snapshot();
    history.t.push(now - i * 1000);
    history.cpuTemp.push(s.cpu.temp); history.cpuLoad.push(s.cpu.load); history.cpuPower.push(s.cpu.power); history.cpuClock.push(s.cpu.clock);
    history.gpuTemp.push(s.gpus[0].temp); history.gpuLoad.push(s.gpus[0].load); history.gpuPower.push(s.gpus[0].power);
    history.ramPct.push(s.ram.pct); history.netDown.push(s.net.down); history.netUp.push(s.net.up); history.ping.push(s.net.ping); history.fps.push(null); history.diskActive.push(s.disks[0].active);
  }
}

export function mockStart(emit) {
  emitFn = emit;
  seedHistory();
  setInterval(() => emit("telemetry", snapshot()), 1000);
}

const activity = [
  { id: "a1", at: new Date(Date.now() - 6e5).toISOString(), icon: "rocket_launch", title: "Modo Turbo: Jogo ativado", detail: "Jogo: cs2", value: "+1,2 GB", kind: "boost" },
  { id: "a2", at: new Date(Date.now() - 36e5).toISOString(), icon: "speed", title: "Benchmark concluído", detail: "RAGE! Bench · CPU multi", value: "3.849 pts", kind: "bench" },
  { id: "a3", at: new Date(Date.now() - 72e5).toISOString(), icon: "auto_fix_high", title: "38 itens corrigidos", detail: "Arquivos inúteis, Registro", value: "2,4 GB", kind: "clean" },
  { id: "a4", at: new Date(Date.now() - 864e5).toISOString(), icon: "memory", title: "Memória RAM liberada", detail: "42 processos otimizados", value: "820 MB", kind: "clean" },
];

let settings = {
  theme: "dark", closeToTray: true, startWithWindows: false, startMinimized: true, tempFahrenheit: false, restorePointBeforeFix: true, registryBackup: true,
  boostMode: "game", autoBoost: true, autoDetectGames: true, boostCleanRam: true, boostPowerPlan: true, boostGamePriority: true, boostFlushDns: false,
  boostStopServices: true, boostCloseApps: true, boostNotify: true, boostServices: null, boostApps: ["OneDrive", "Teams"],
  autoRamClean: false, autoRamThreshold: 85, tempAlerts: true, cpuTempAlert: 95, gpuTempAlert: 90,
  overlayEnabled: true, overlayPosition: "top-left", overlayAutoInGame: true, overlayHotkey: "Ctrl+Shift+F10", fpsCounter: true,
};

const careCats = [
  { id: "junk", title: "Arquivos inúteis", description: "Temporários, caches e lixo do sistema", icon: "delete_sweep", defaultOn: true, order: 10 },
  { id: "privacy", title: "Rastros de privacidade", description: "Histórico de uso e atividade", icon: "visibility_off", defaultOn: true, order: 20 },
  { id: "shortcuts", title: "Atalhos inválidos", description: "Atalhos que apontam para nada", icon: "link_off", defaultOn: true, order: 30 },
  { id: "registry", title: "Registro do Windows", description: "Entradas órfãs e inválidas", icon: "account_tree", defaultOn: true, order: 40 },
  { id: "startup", title: "Inicialização", description: "Programas que atrasam o boot", icon: "rocket", defaultOn: true, order: 50 },
  { id: "tweaks", title: "Otimização do sistema", description: "Ajustes de desempenho e jogos", icon: "tune", defaultOn: true, order: 60 },
  { id: "internet", title: "Otimização da internet", description: "TCP, DNS e latência", icon: "network_check", defaultOn: true, order: 70 },
  { id: "security", title: "Reforço de segurança", description: "Firewall, Defender, UAC", icon: "shield", defaultOn: true, order: 80 },
  { id: "disk", title: "Otimização de disco", description: "TRIM e desfragmentação", icon: "hard_drive", defaultOn: true, order: 90 },
  { id: "updates", title: "Programas desatualizados", description: "Atualizações via winget", icon: "system_update_alt", defaultOn: false, order: 100 },
  { id: "antivirus", title: "Ameaças", description: "Verificação rápida do Defender", icon: "coronavirus", defaultOn: false, order: 110 },
];

function careItems(cat) {
  const mk = (i, o) => ({ id: `${cat}.${i}`, category: cat, selected: true, risk: "safe", fixable: true, ...o });
  switch (cat) {
    case "junk": return [
      mk("utemp", { group: "Windows", title: "Arquivos temporários do usuário", detail: "%TEMP%", size: 1.31 * GB, count: 4210 }),
      mk("wtemp", { group: "Windows", title: "Temporários do Windows", detail: "C:\\Windows\\Temp", size: 212e6, count: 88 }),
      mk("wu", { group: "Windows", title: "Cache do Windows Update", size: 845e6, count: 41 }),
      mk("thumbs", { group: "Windows", title: "Cache de miniaturas", size: 96e6, count: 12 }),
      mk("prefetch", { group: "Windows", title: "Prefetch", size: 41e6, count: 260, selected: false, note: "Os apps podem abrir um pouco mais devagar na primeira vez." }),
      mk("recycle", { group: "Windows", title: "Lixeira", size: 620e6, count: 33 }),
      mk("chrome", { group: "Navegadores", title: "Cache do Google Chrome", size: 512e6, count: 3100 }),
      mk("edge", { group: "Navegadores", title: "Cache do Microsoft Edge", size: 230e6, count: 1400 }),
      mk("discord", { group: "Apps e jogos", title: "Cache do Discord", size: 180e6, count: 950 }),
      mk("steam", { group: "Apps e jogos", title: "Cache da Steam", size: 140e6, count: 600 }),
      mk("nvshader", { group: "Apps e jogos", title: "Cache de shaders NVIDIA", size: 410e6, count: 300, selected: false, risk: "caution", note: "Os jogos recompilam os shaders na próxima abertura (pode haver travadinhas no início)." }),
    ];
    case "privacy": return [
      mk("recent", { group: "Windows", title: "Documentos recentes", count: 120 }),
      mk("jump", { group: "Windows", title: "Listas de atalhos (Jump Lists)", count: 46 }),
      mk("runmru", { group: "Windows", title: "Histórico do Executar", count: 9 }),
      mk("chromehist", { group: "Navegadores", title: "Histórico do Chrome", count: 5400, selected: false, risk: "caution" }),
      mk("chromecook", { group: "Navegadores", title: "Cookies do Chrome", count: 980, selected: false, risk: "caution", note: "Você será desconectado dos sites." }),
    ];
    case "shortcuts": return [mk("s1", { title: "Jogo Antigo.lnk", detail: "D:\\Games\\Antigo\\game.exe" }), mk("s2", { title: "Photoshop CS6.lnk", detail: "C:\\Program Files\\Adobe\\Photoshop.exe" })];
    case "registry": return [
      mk("r1", { group: "Desinstaladores inválidos", title: "WinRAR 5.0", detail: "HKLM\\...\\Uninstall\\WinRAR" }),
      mk("r2", { group: "Cache MUI", title: "setup_x64.exe", detail: "HKCU\\...\\MuiCache" }),
      mk("r3", { group: "Inicialização inválida", title: "OldUpdater", detail: "HKCU\\...\\Run\\OldUpdater" }),
    ];
    case "startup": return [mk("st1", { title: "Spotify", detail: "Abre junto com o Windows (impacto alto)" }), mk("st2", { title: "Microsoft Teams", detail: "Atualizador e chat" })];
    case "tweaks": return [
      mk("tw.gamedvr", { title: "Desligar gravação em segundo plano (Game DVR)", current: "Ligado", recommended: "Desligado" }),
      mk("tw.mmcss", { title: "Prioridade de jogos no agendador multimídia", current: "Padrão", recommended: "Jogos em prioridade alta" }),
      mk("tw.mouse", { title: "Desligar aceleração do mouse", current: "Ligado", recommended: "Desligado" }),
    ];
    case "internet": return [mk("net.throttle", { title: "Remover limitação de rede multimídia", current: "10", recommended: "Desativada" })];
    case "security": return [
      mk("sec.defender", { title: "Proteção em tempo real", status: "ok", fixable: false, detail: "Microsoft Defender ativo" }),
      mk("sec.firewall", { title: "Firewall do Windows", status: "ok", fixable: false }),
      mk("sec.autorun", { title: "Execução automática de pendrives", status: "warn", detail: "Ativada — risco de vírus por USB", current: "Ativada", recommended: "Desativada" }),
    ];
    case "disk": return [mk("disk.C", { title: "C: · SSD NVMe", detail: "Último TRIM há 12 dias", recommended: "Executar TRIM" })];
    default: return [];
  }
}

const handlers = {
  "app.info": () => ({ name: "RAGE!", version: "1.0.0", isAdmin: true, portable: true, dataDir: "C:\\RAGE!\\data", os: "Windows 11 Home Single Language 25H2", osBuild: "26200.7171", machine: "MEU-PC", user: "Voce", uptimeSec: 139359, webview: "142.0", sensors: { installed: false, version: null, admin: true, cpuTemp: false, ready: true } }),
  "settings.get": () => settings,
  "settings.set": ({ values }) => (settings = { ...settings, ...values }),
  "app.setTheme": () => true,
  "activity.list": () => activity,
  "activity.clear": () => true,
  "monitor.history": () => history,
  "monitor.snapshot": () => snapshot(),
  "boost.status": () => boost,
  "boost.start": async ({ mode }) => {
    await wait(1400);
    boost = { active: true, mode: mode || "game", since: new Date().toISOString(), source: "manual", auto: false, game: null, freed: 1.2 * GB, actions: ["Plano de energia RAGE! Turbo ativado", "Modo de energia: melhor desempenho", "9 serviços pausados", "2 apps em segundo plano fechados", "RAM liberada: 1,2 GB"] };
    emitFn?.("boost", boost);
    return boost;
  },
  "boost.stop": async () => { await wait(800); boost = { ...boost, active: false, mode: null, actions: [] }; emitFn?.("boost", boost); return boost; },
  "boost.config": () => ({
    admin: true, settings, status: boost,
    services: [
      { name: "Spooler", title: "Spooler de Impressão", desc: "Fila de impressão. Ninguém imprime no meio da partida.", recommended: true, status: "rodando", selected: true },
      { name: "WSearch", title: "Windows Search", desc: "Indexação de arquivos em segundo plano (usa disco e CPU).", recommended: true, status: "rodando", selected: true },
      { name: "SysMain", title: "SysMain (Superfetch)", desc: "Pré-carrega apps na RAM.", recommended: true, status: "rodando", selected: true },
      { name: "DiagTrack", title: "Telemetria do Windows", desc: "Envio de dados de diagnóstico para a Microsoft.", recommended: true, status: "rodando", selected: true },
      { name: "wuauserv", title: "Windows Update", desc: "Evita downloads durante o jogo.", recommended: true, status: "parado", selected: true },
      { name: "BITS", title: "Transferência inteligente (BITS)", desc: "Downloads em segundo plano.", recommended: false, status: "rodando", selected: false },
    ],
    apps: [
      { name: "OneDrive", title: "Microsoft OneDrive", processes: 1, memory: 120e6, recommended: true, selected: true },
      { name: "ms-teams", title: "Microsoft Teams", processes: 6, memory: 640e6, recommended: true, selected: true },
      { name: "chrome", title: "Google Chrome", processes: 24, memory: 1.9 * GB, recommended: false, selected: false },
      { name: "Spotify", title: "Spotify", processes: 5, memory: 310e6, recommended: false, selected: false },
    ],
  }),
  "boost.setApps": ({ apps }) => { settings.boostApps = apps; return handlers["boost.config"](); },
  "boost.setServices": ({ services, reset }) => { settings.boostServices = reset ? null : services; return handlers["boost.config"](); },
  "ram.clean": async () => { await wait(1200); return { freed: 912e6, freedText: "870 MB", trimmed: 64, pctBefore: 78, pctAfter: 66 }; },
  "ram.top": () => [{ pid: 1, name: "chrome", ws: 1.1 * GB }, { pid: 2, name: "Code", ws: 0.7 * GB }, { pid: 3, name: "ms-teams", ws: 0.45 * GB }],
  "fps.status": () => ({ running: true, admin: true, current: null, apps: [] }),
  "overlay.toggle": ({ show }) => ({ visible: !!show }),
  "overlay.applySettings": () => true,
  "power.plans": () => ({ plans: [{ guid: "381b4222-f694-41f0-9685-ff5bb260df2e", name: "Equilibrado", active: true }, { guid: "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", name: "Alto desempenho", active: false }, { guid: "rage-x", name: "RAGE! Turbo", active: false }], active: "381b4222-f694-41f0-9685-ff5bb260df2e", overlay: "00000000-0000-0000-0000-000000000000", overlayName: "Equilibrado", cpu: { minAc: 5, maxAc: 100, boostAc: 2, minDc: 5, maxDc: 100, boostDc: 1 } }),
  "power.setPlan": () => handlers["power.plans"](),
  "power.setOverlay": () => handlers["power.plans"](),
  "power.setCpu": () => handlers["power.plans"](),
  "games.list": () => [
    { id: "steam-730", name: "Counter-Strike 2", source: "steam", folder: "C:\\Steam\\steamapps\\common\\Counter-Strike Global Offensive", exe: "C:\\...\\cs2.exe", autoBoost: true, highPerfGpu: true, playSeconds: 412000, lastPlayed: new Date(Date.now() - 864e5).toISOString(), running: false, exists: true },
    { id: "epic-fn", name: "Fortnite", source: "epic", folder: "C:\\Epic\\Fortnite", autoBoost: true, highPerfGpu: false, playSeconds: 98000, lastPlayed: new Date(Date.now() - 5 * 864e5).toISOString(), running: false, exists: true },
    { id: "riot-val", name: "VALORANT", source: "riot", folder: "C:\\Riot Games\\VALORANT", autoBoost: true, highPerfGpu: true, playSeconds: 0, running: false, exists: true },
    { id: "xbox-forza", name: "Forza Horizon 5", source: "xbox", folder: "C:\\XboxGames\\Forza Horizon 5\\Content", autoBoost: false, highPerfGpu: false, playSeconds: 12000, running: false, exists: true },
  ],
  "games.scan": async () => { await wait(900); return handlers["games.list"](); },
  "games.update": () => handlers["games.list"](),
  "games.add": () => handlers["games.list"](),
  "games.remove": () => handlers["games.list"](),
  "games.launch": () => true,
  "care.categories": () => careCats,
  "care.scan": async ({ categories }, { onProgress }) => {
    const cats = careCats.filter((c) => (categories?.length ? categories.includes(c.id) : c.defaultOn));
    const out = [];
    for (let i = 0; i < cats.length; i++) {
      const c = cats[i];
      onProgress?.({ phase: "scan", category: c.id, title: c.title, index: i, total: cats.length });
      await wait(450 + Math.random() * 500);
      const items = careItems(c.id);
      const size = items.reduce((a, x) => a + (x.size || 0), 0);
      const issues = items.filter((x) => x.fixable && x.status !== "ok").length;
      onProgress?.({ phase: "scanned", category: c.id, title: c.title, index: i, total: cats.length, issues, size });
      out.push({ id: c.id, title: c.title, icon: c.icon, items, size, count: items.length, issues, durationMs: 500 });
    }
    return { at: new Date().toISOString(), categories: out };
  },
  "care.fix": async ({ ids }, { onProgress }) => {
    for (let i = 0; i < 4; i++) { onProgress?.({ phase: "fix", title: "Corrigindo", index: i, total: 4 }); await wait(500); }
    return { ok: ids.length, failed: 0, freed: 3.4 * GB, restorePoint: true, needsReboot: false, backups: [], details: ids.map((id) => ({ id, ok: true })) };
  },
  "health.summary": () => [
    { id: "sensors", title: "Sensores da CPU", detail: "Instale o driver PawnIO para ver a temperatura real", status: "warn", action: { label: "Instalar", route: "/configuracoes" } },
    { id: "defender", title: "Microsoft Defender", detail: "Proteção em tempo real ativa", status: "ok" },
    { id: "gamemode", title: "Modo de Jogo do Windows", detail: "Ligado", status: "ok" },
    { id: "gamedvr", title: "Gravação em segundo plano", detail: "Game DVR ligado — pode custar FPS", status: "warn", action: { label: "Desligar", route: "/ajustes" } },
    { id: "disk", title: "Espaço no disco C:", detail: "63 GB livres (14%)", status: "warn", action: { label: "Limpar", route: "/limpeza" } },
    { id: "reboot", title: "Reinicialização pendente", detail: "Nenhuma", status: "ok" },
  ],
  "tweaks.list": ({ category }) => [
    { id: "tw.gamemode", category: "gaming", title: "Modo de Jogo do Windows", desc: "Prioriza o jogo e evita atualizações durante a partida.", applied: true, recommended: true, needsReboot: false, risk: "safe" },
    { id: "tw.gamedvr", category: "gaming", title: "Desligar gravação em segundo plano", desc: "O Game DVR grava o tempo todo e pode custar FPS.", applied: false, recommended: true, needsReboot: false, risk: "safe" },
    { id: "tw.hags", category: "gaming", title: "Agendamento de GPU acelerado por hardware", desc: "Reduz latência em GPUs recentes.", applied: false, recommended: true, needsReboot: true, risk: "safe" },
    { id: "tw.mouse", category: "gaming", title: "Desligar aceleração do mouse", desc: "Mira consistente: o movimento do cursor fica 1:1.", applied: false, recommended: true, needsReboot: false, risk: "safe" },
    { id: "tw.menu", category: "system", title: "Menus mais rápidos", desc: "Atraso dos menus de 400 ms para 100 ms.", applied: true, recommended: true, needsReboot: false, risk: "safe" },
    { id: "tw.hiber", category: "system", title: "Desligar hibernação", desc: "Libera 6,3 GB do hiberfil.sys.", applied: false, recommended: false, needsReboot: false, risk: "caution" },
    { id: "tw.ads", category: "privacy", title: "Desligar anúncios e sugestões do Windows", desc: "Remove dicas e apps sugeridos.", applied: false, recommended: true, needsReboot: false, risk: "safe" },
    { id: "net.nagle", category: "internet", title: "Desligar algoritmo de Nagle", desc: "Pode reduzir a latência em alguns jogos online.", applied: false, recommended: false, needsReboot: false, risk: "safe" },
  ].filter((x) => !category || x.category === category),
  "tweaks.apply": ({ ids }) => ({ results: ids.map((id) => ({ id, ok: true })), items: handlers["tweaks.list"]({}) }),
  "tweaks.revert": ({ ids }) => ({ results: ids.map((id) => ({ id, ok: true })), items: handlers["tweaks.list"]({}) }),
  "disk.volumes": () => [{ letter: "C:", label: "OS", media: "SSD", bus: "NVMe", total: 459 * GB, free: 63 * GB, lastOptimized: new Date(Date.now() - 12 * 864e5).toISOString(), action: "trim", needed: true }],
  "disk.optimize": async (_, { onProgress }) => { for (const l of ["Otimizando C:...", "TRIM concluído"]) { onProgress?.({ line: l }); await wait(700); } return { ok: true, message: "Concluído" }; },
  "startup.list": () => ({
    boot: { lastSec: 31.4, history: [{ at: new Date(Date.now() - 864e5).toISOString(), sec: 33.1 }, { at: new Date().toISOString(), sec: 31.4 }] },
    items: [
      { id: "1", name: "Spotify", publisher: "Spotify AB", command: "Spotify.exe --autostart", location: "HKCU Run", type: "registry", enabled: true, impact: "alto", recommendation: "desativar", reason: "Não precisa abrir com o Windows" },
      { id: "2", name: "Microsoft Teams", publisher: "Microsoft Corporation", command: "ms-teams.exe", location: "HKCU Run", type: "registry", enabled: true, impact: "alto", recommendation: "desativar" },
      { id: "3", name: "Realtek HD Audio", publisher: "Realtek", command: "RtkAudUService64.exe", location: "HKLM Run", type: "registry", enabled: true, impact: "baixo", recommendation: "manter", reason: "Driver de áudio" },
      { id: "4", name: "Steam", publisher: "Valve", command: "steam.exe -silent", location: "HKCU Run", type: "registry", enabled: false, impact: "médio", recommendation: "desativar" },
      { id: "5", name: "Google Updater", publisher: "Google LLC", command: "updater.exe --wake", location: "Tarefa agendada", type: "task", enabled: true, impact: "baixo", recommendation: "manter" },
    ],
  }),
  "startup.set": ({ id, enabled }) => ({ id, enabled }),
  "proc.list": () => [
    { pid: 1200, name: "chrome", title: "YouTube — Google Chrome", path: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe", publisher: "Google LLC", cpu: 6.2, mem: 1.1 * GB, gpu: 3, threads: 42, priority: "normal", isSystem: false },
    { pid: 3010, name: "Code", title: "VS Code", publisher: "Microsoft", cpu: 1.4, mem: 0.7 * GB, gpu: 0, threads: 30, priority: "normal", isSystem: false },
    { pid: 4, name: "System", title: "", publisher: "", cpu: 0.4, mem: 0.1e6, gpu: 0, threads: 280, priority: "normal", isSystem: true },
  ],
  "apps.list": () => [
    { id: "a", name: "Google Chrome", publisher: "Google LLC", version: "131.0", size: 640e6, installDate: "2026-01-10", source: "win32", canUninstall: true },
    { id: "b", name: "Spotify", publisher: "Spotify AB", version: "1.2.50", size: 380e6, installDate: "2025-11-03", source: "win32", canUninstall: true },
  ],
  "updates.list": async () => { await wait(1500); return [{ id: "Google.Chrome", name: "Google Chrome", version: "130.0", available: "131.0", source: "winget" }, { id: "7zip.7zip", name: "7-Zip", version: "23.01", available: "24.08", source: "winget" }]; },
  "drivers.list": () => ({ problems: 0, items: [{ name: "NVIDIA GeForce GTX 1650", class: "Display", provider: "NVIDIA", version: "32.0.16.1074", date: "2025-08-01", old: false }, { name: "Intel(R) UHD Graphics 630", class: "Display", provider: "Intel Corporation", version: "30.0.101.1338", date: "2021-12-01", old: true }] }),
  "sysinfo.get": () => ({ os: { name: "Windows 11 Home Single Language", version: "25H2", build: "26200.7171", user: "Voce", machine: "MEU-PC", uptimeSec: 139359 }, cpu: { name: "Intel Core i5-9300H", cores: 4, threads: 8, baseMHz: 2400 }, gpus: [{ name: "NVIDIA GeForce GTX 1650", vram: 4 * GB, driver: "32.0.16.1074" }], ram: { total: 8 * GB, modules: [{ slot: "DIMM A", size: 4 * GB, speed: 2667, manufacturer: "SK Hynix", type: "DDR4" }, { slot: "DIMM B", size: 4 * GB, speed: 2667, manufacturer: "SK Hynix", type: "DDR4" }] }, board: { manufacturer: "Dell Inc.", product: "G3 3590", bios: "1.21.0" }, disks: [{ model: "CL1-3D512-Q11 NVMe SSSTC 512GB", size: 512e9, media: "SSD", bus: "NVMe" }], network: [{ name: "Wi-Fi", type: "Wi-Fi", ip: "192.168.0.12", speed: 180e6 }] }),
  "fix.list": () => [
    { id: "sfc", title: "Verificar arquivos do sistema (SFC)", desc: "Repara arquivos corrompidos do Windows.", icon: "verified", minutes: 15, needsReboot: false, admin: true },
    { id: "dism", title: "Restaurar imagem do Windows (DISM)", desc: "Repara o repositório de componentes.", icon: "healing", minutes: 20, needsReboot: false, admin: true },
    { id: "net", title: "Redefinir rede", desc: "Winsock, TCP/IP e DNS.", icon: "lan", minutes: 1, needsReboot: true, admin: true },
  ],
  "rescue.list": () => ({ points: [{ seq: 41, description: "RAGE! - antes das correções", date: new Date(Date.now() - 72e5).toISOString(), type: "Configuração" }], backups: [{ file: "C:\\RAGE!\\data\\backups\\registro\\2026-11-20_registro.reg", kind: "registro", date: new Date().toISOString(), size: 12400 }] }),
  "av.status": () => ({ available: true, enabled: true, realtime: true, tamper: true, signatureVersion: "1.421.112.0", signatureAgeDays: 0, engineVersion: "1.1.25010.1", lastQuickScan: new Date(Date.now() - 2 * 864e5).toISOString(), lastFullScan: null, threats: [] }),
  "net.adapters": () => [{ name: "Wi-Fi", desc: "Intel Wireless-AC 9560", type: "Wi-Fi", status: "Conectado", ip: "192.168.0.12", gateway: "192.168.0.1", dns: ["192.168.0.1"], mac: "AA-BB-CC-DD-EE-FF", speedMbps: 180, ssid: "Casa", signal: 78 }],
  "net.dnsBenchmark": async () => { await wait(1600); return [{ id: "cloudflare", name: "Cloudflare", primary: "1.1.1.1", secondary: "1.0.0.1", ms: 14 }, { id: "google", name: "Google", primary: "8.8.8.8", secondary: "8.8.4.4", ms: 22 }, { id: "current", name: "Atual (roteador)", primary: "192.168.0.1", ms: 38, current: true }]; },
  "net.ping": async () => { await wait(1200); return { min: 18, avg: 22, max: 31, loss: 0, jitter: 3 }; },
  "bench.history": () => [{ id: "b1", at: new Date(Date.now() - 36e5).toISOString(), total: 3849, scores: { cpuSingle: 416, cpuMulti: 1890, memory: 18.2, diskRead: 2100, diskWrite: 1300 } }],
  "bench.run": async (_, { onProgress }) => {
    for (const [test, label] of [["cpuSingle", "CPU · 1 núcleo"], ["cpuMulti", "CPU · todos os núcleos"], ["memory", "Memória"], ["disk", "Disco"]]) {
      for (let p = 0; p <= 100; p += 20) { onProgress?.({ test, pct: p, label }); await wait(250); }
    }
    return { id: "b2", at: new Date().toISOString(), total: 3912, scores: { cpuSingle: 421, cpuMulti: 1912, memory: 18.6, diskRead: 2140, diskWrite: 1320 } };
  },
  "stress.start": ({ minutes }) => { stress = { startedAt: Date.now(), minutes }; return { running: true }; },
  "stress.stop": () => { stress = null; return { running: false }; },
  "stress.status": () => ({ running: !!stress, startedAt: stress ? new Date(stress.startedAt).toISOString() : null, minutes: stress?.minutes, threads: 8 }),
  "schedule.get": () => null,
  "monitor.sensors": () => ({ driver: { installed: false }, hardware: [] }),
  "desktop.scan": () => deskScan(),
  "desktop.apply": async ({ items }) => {
    await wait(700);
    desk.backup = { at: new Date().toISOString(), saved: desk.items.map((i) => ({ id: i.id, col: i.col, row: i.row })) };
    const to = new Map(items.map((p) => [p.id, p]));
    for (const i of desk.items) { const p = to.get(i.id); if (p) { i.col = p.col; i.row = p.row; } }
    return { moved: items.length, total: desk.items.length, autoArrangeWasOn: false, backup: { at: desk.backup.at, count: desk.backup.saved.length } };
  },
  "desktop.restore": async () => {
    await wait(500);
    const old = new Map(desk.backup.saved.map((p) => [p.id, p]));
    for (const i of desk.items) { const p = old.get(i.id); if (p) { i.col = p.col; i.row = p.row; } }
    desk.backup = null;
    return { moved: old.size, backup: null };
  },
  "files.recycle": async ({ paths }) => { await wait(500); desk.items = desk.items.filter((i) => !paths.includes(i.path)); return { ok: paths.length, failed: 0, freed: 0, errors: [] }; },
  "app.reveal": () => true,
  "tune.status": () => tuneStatus(),
  "tune.engage": async ({ stage }, { onProgress }) => {
    const level = TUNE_STAGES.find((s) => s.id === stage).level;
    const parts = TUNE_PARTS.filter((p) => p.minStage <= level);
    for (const p of parts) {
      onProgress?.({ part: p.id, name: p.name, status: "install" });
      await wait(260 + Math.random() * 380);
      const fail = p.id === "mode" && level >= 2 && false;
      onProgress?.({ part: p.id, name: p.name, status: fail ? "fail" : "ok" });
    }
    tune = { active: true, stage, since: new Date().toISOString(), installed: parts.map((p) => p.id) };
    return tuneStatus();
  },
  "tune.disengage": async (_, { onProgress }) => {
    onProgress?.({ part: "restore", name: "Voltando ao original", status: "install" });
    await wait(900);
    tune = { active: false, stage: "stock", since: null, installed: [] };
    return tuneStatus();
  },
};

const TUNE_STAGES = [
  { level: 0, id: "stock", name: "Original", tag: "De fábrica", glyph: "N", stats: [2, 2, 5, 2], desc: "Como o Windows vem: equilibrado, frio e econômico." },
  { level: 1, id: "street", name: "Stage 1", tag: "Rua", glyph: "1", stats: [3, 3, 4, 3], desc: "Turbo agressivo e modo de jogo. Mais resposta no dia a dia sem esquentar muito." },
  { level: 2, id: "track", name: "Stage 2", tag: "Pista", glyph: "2", stats: [4, 4, 2, 4], desc: "Remap RAGE! Turbo: CPU sempre no topo e todos os núcleos acordados na tomada." },
  { level: 3, id: "rage", name: "RAGE!", tag: "Envenenado", glyph: "R", stats: [5, 5, 1, 5], desc: "Tudo no máximo, inclusive na bateria, sem limitador e com prioridade de pista para os jogos." },
];
const TUNE_PARTS = [
  { id: "turbo", minStage: 1, icon: "speed", name: "Turbo do processador", real: "Turbo Boost em modo agressivo: a CPU sobe o clock antes e segura lá em cima por mais tempo." },
  { id: "mode", minStage: 1, icon: "bolt", name: "Modo pista", real: "Modo de energia do Windows em desempenho (o controle de Configurações › Sistema › Energia)." },
  { id: "nitro", minStage: 1, icon: "local_fire_department", name: "Nitro", real: "Modo de Jogo do Windows ligado e gravação em segundo plano (Game DVR) desligada." },
  { id: "map", minStage: 2, icon: "tune", name: "Remap RAGE! Turbo", real: "Plano de energia RAGE! Turbo, feito a partir do Desempenho Máximo, sem economia de PCIe e USB." },
  { id: "inject", minStage: 2, icon: "trending_up", name: "Injeção direta", real: "Na tomada o processador fica sempre no clock máximo (estado mínimo 100%): zero atraso para acelerar." },
  { id: "exhaust", minStage: 2, icon: "grid_view", name: "Escape aberto", real: "Todos os núcleos acordados: o Windows para de “estacionar” núcleos para economizar." },
  { id: "ecu", minStage: 3, icon: "flag", name: "Prioridade de pista", real: "Jogos em prioridade alta no agendador multimídia (MMCSS) e prioridade máxima ao app em primeiro plano." },
  { id: "limiter", minStage: 3, icon: "block", name: "Sem limitador", real: "Desliga a limitação de energia (Power Throttling) dos apps. Vale depois de reiniciar." },
  { id: "battery", minStage: 3, icon: "battery_charging_full", name: "Bateria liberada", real: "O mesmo desempenho fora da tomada. A bateria acaba bem mais rápido." },
  { id: "air", minStage: 3, icon: "air", name: "Filtro de ar", real: "Libera a memória RAM no momento em que o PC é envenenado." },
];
let tune = { active: false, stage: "stock", since: null, installed: [] };
function tuneStatus() {
  const level = TUNE_STAGES.find((s) => s.id === tune.stage)?.level || 0;
  return {
    active: tune.active, stage: tune.stage, level, since: tune.since, admin: true, stages: TUNE_STAGES,
    parts: TUNE_PARTS.map((p) => ({ ...p, installed: tune.active && tune.installed.includes(p.id) })),
    plan: tune.active && level >= 2 ? "RAGE! Turbo" : "Equilibrado",
  };
}

// ---------- Área de trabalho simulada (bagunçada de propósito) ----------
function hsl(hex) {
  const n = parseInt(hex.slice(1), 16), r = (n >> 16) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min, l = (max + min) / 2;
  if (d < 0.02) return { hue: 0, sat: 0, lum: l, neutral: true };
  let h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  h = (h * 60 + 360) % 360;
  const sat = d / (1 - Math.abs(2 * l - 1));
  return { hue: +h.toFixed(1), sat: +sat.toFixed(3), lum: +l.toFixed(3), neutral: sat < 0.12 };
}
const DESK_GRID = { cols: 20, rows: 8, cellW: 95, cellH: 122, iconSize: 48, autoArrange: false, snapToGrid: true };
const DESK_APPS = [
  ["Photoshop", "Adobe Inc.", "#31a8ff"], ["Illustrator", "Adobe Inc.", "#ff9a00"], ["Premiere Pro", "Adobe Inc.", "#9999ff"], ["After Effects", "Adobe Inc.", "#9a8cff"],
  ["Google Chrome", "Google LLC", "#fbbc05"], ["Discord", "Discord Inc.", "#5865f2"], ["Telegram", "Telegram FZ-LLC", "#229ed9"], ["Steam", "Valve Corporation", "#1b2838"],
  ["Counter-Strike 2", "Valve Corporation", "#f29d2a"], ["Spotify", "Spotify AB", "#1ed760"], ["VirtualDJ", "Atomix Productions", "#e3141b"], ["AnyDesk", "AnyDesk Software GmbH", "#ef443b"],
  ["Opera GX", "Opera Norway AS", "#fa1e4e"], ["Word", "Microsoft Corporation", "#2b579a"], ["Excel", "Microsoft Corporation", "#217346"], ["PowerPoint", "Microsoft Corporation", "#d24726"],
  ["VS Code", "Microsoft Corporation", "#23a9f2"], ["Minecraft Launcher", null, "#52a435"], ["OBS Studio", "OBS Project", "#302e31"], ["Claude", "Anthropic PBC", "#d97757"],
  ["RAGE!", "RAGE!", "#ff5a1f"], ["FxSound", "FxSound LLC", "#2c2c2c"], ["Figma", "Figma, Inc.", "#a259ff"], ["WhatsApp", "WhatsApp LLC", "#25d366"],
];
const DESK_FILES = [
  ["setup_driver_2024.exe", 310, 184e6], ["Captura de tela (12).png", 6, 1.4e6], ["boleto_marco.pdf", 240, 220e3], ["orcamento-final-v3.xlsx", 96, 48e3], ["video_evento.mp4", 410, 1.9e9],
  ["notas.txt", 2, 3e3], ["logo-antiga.psd", 520, 86e6], ["planilha clientes.csv", 31, 610e3], ["contrato.docx", 150, 92e3], ["winrar-x64.exe", 700, 3.6e6], ["foto perfil.jpg", 12, 2.2e6],
];
const vendorKeyOf = (v) => (v ? v.toLowerCase().replace(/[^a-z0-9 ]/g, " ").split(" ").filter((w) => w && !["inc", "llc", "corporation", "ab", "gmbh", "as", "fz", "pbc", "software"].includes(w))[0] || null : null);
function deskSeed() {
  const cells = [];
  for (let c = 0; c < DESK_GRID.cols; c++) for (let r = 0; r < DESK_GRID.rows; r++) cells.push([c, r]);
  for (let i = cells.length - 1; i > 0; i--) { const j = (i * 7919 + 13) % (i + 1); [cells[i], cells[j]] = [cells[j], cells[i]]; }
  let k = 0;
  const at = () => { const [col, row] = cells[k++]; return { col, row, x: col * DESK_GRID.cellW + 21, y: row * DESK_GRID.cellH + 2 }; };
  const user = "C:\\Users\\Voce\\Desktop\\";
  const items = DESK_APPS.map(([name, vendor, color]) => ({ id: user + name + ".lnk", name, path: user + name + ".lnk", kind: "app", isDir: false, ext: "lnk", target: null, vendor, vendorKey: vendorKeyOf(vendor), color, ...hsl(color), icon: null, size: 1500, modified: new Date(Date.now() - 4e9).toISOString(), common: false, ...at() }));
  for (const [name, days, size] of DESK_FILES) items.push({ id: user + name, name, path: user + name, kind: "file", isDir: false, ext: name.split(".").pop().toLowerCase(), target: null, vendor: null, vendorKey: null, color: "#8a8f98", hue: 0, sat: 0, lum: 0.55, neutral: true, icon: null, size, modified: new Date(Date.now() - days * 864e5).toISOString(), common: false, ...at() });
  for (const [name, days] of [["Estudos", 52], ["Projetos antigos", 380]]) items.push({ id: user + name, name, path: user + name, kind: "folder", isDir: true, ext: null, target: null, vendor: null, vendorKey: null, color: "#fcd059", ...hsl("#fcd059"), icon: null, size: 3.4 * GB, modified: new Date(Date.now() - days * 864e5).toISOString(), common: false, ...at() });
  items.push({ id: "::{645FF040-5081-101B-9F08-00AA002F954E}", name: "Lixeira", path: null, kind: "system", isDir: false, ext: null, target: null, vendor: null, vendorKey: null, color: "#128ddb", ...hsl("#128ddb"), icon: null, size: null, modified: null, common: false, ...at() });
  return items;
}
const desk = { items: deskSeed(), backup: null };
function deskScan() {
  return { desktop: "C:\\Users\\Voce\\Desktop", grid: DESK_GRID, items: desk.items.map((i) => ({ ...i })), backup: desk.backup ? { at: desk.backup.at, count: desk.backup.saved.length } : null, admin: true };
}

export async function mockCall(method, params = {}, opts = {}) {
  await wait(120 + Math.random() * 200);
  const h = handlers[method];
  if (!h) { console.warn("[mock] sem simulação para", method, params); return null; }
  return h(params, opts);
}
