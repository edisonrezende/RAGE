# RAGE! — contrato das funções (RPC)

A interface (React, WebView2) chama `rage.call("nome", {params})`. No C#, cada função é um método **estático** marcado com
`[Rpc("nome")]` (ver `app/Core/Rpc.cs`). Os parâmetros são ligados **pelo nome** a partir do JSON (case-insensitive);
parâmetros do tipo `RpcContext` / `CancellationToken` são injetados. O retorno é serializado em JSON **camelCase**
(Newtonsoft). Erros para o usuário: `throw new RpcException("mensagem em PT-BR")`.

Progresso de operações longas: `ctx.Progress(obj)` (a interface recebe em tempo real). Cancelamento: `ctx.Token`.

Utilitários prontos em `app/Core`: `Proc.Run/RunAsync/Ps/PsJson` (processos e PowerShell, saída OEM/UTF-8),
`Reg` (registro com caminhos "HKLM\\..." e backup `.reg`: `Reg.AppendExport` + `Reg.SaveBackup`), `Paths` (pasta
portátil `data\`: `Paths.Backups`, `Paths.File("x.json")`), `Json.Load/Save`, `Log`, `Activity.Add(kind, icon, title, detail, value)`
(feed "Eventos recentes"), `Fmt.Bytes`, `Native` (P/Invokes comuns), `RestorePoint.Create`, `Settings.Current`.
`Program.IsAdmin` indica se está elevado.

**Todos os textos visíveis ao usuário em português do Brasil, com acentos.** Datas: `DateTime` (vira ISO no JSON).
Tamanhos sempre em **bytes** (number). Percentuais 0–100.

---

## 1. Verificação inteligente (orquestrador — já implementado em `app/Core/Care.cs`)

Cada categoria implementa `ICareScanner` (descoberta automática por reflexão, construtor sem parâmetros):

```csharp
string Category; string Title; string Description; string Icon; int Order; bool DefaultOn;
List<CareItem> Scan(RpcContext ctx);
List<FixResult> Fix(List<CareItem> items, RpcContext ctx);
```

`CareItem`: `id` (único e estável, prefixado pela categoria, ex. `junk.wintemp`), `category`, `group`, `title`, `detail`,
`size` (bytes), `count`, `selected` (seleção padrão), `risk` (`safe`|`caution`), `note` (aviso), `current`, `recommended`,
`needsReboot`, `fixable`, `action` (URI p/ correção manual, ex. `ms-settings:...`), `status` (`ok`|`warn`|`bad`),
`paths` (até 5 exemplos), `Tag` (interno, não serializado).

`FixResult`: `id`, `ok`, `message`, `freed` (bytes), `needsReboot`, `backup` (arquivo gerado).

Categorias (Order) e dono:

| Order | category | Título | DefaultOn | Dono |
|---|---|---|---|---|
| 10 | `junk` | Arquivos inúteis | sim | Care |
| 20 | `privacy` | Rastros de privacidade | sim | Care |
| 30 | `shortcuts` | Atalhos inválidos | sim | Care |
| 40 | `registry` | Registro do Windows | sim | Care |
| 50 | `startup` | Inicialização | sim | Tools |
| 60 | `tweaks` | Otimização do sistema | sim | Care |
| 70 | `internet` | Otimização da internet | sim | Care |
| 80 | `security` | Reforço de segurança | sim | Care |
| 90 | `disk` | Otimização de disco | sim | Care |
| 100 | `updates` | Programas desatualizados | não | Tools |
| 110 | `antivirus` | Ameaças (Microsoft Defender) | não | Tools |

RPC do orquestrador: `care.categories` → `[{id,title,description,icon,defaultOn,order}]`;
`care.scan {categories?: string[]}` → `{at, categories:[{id,title,icon,items:CareItem[],error,size,count,issues,durationMs}]}`
(progresso `{phase:"scan"|"scanned", category, title, index, total, issues?, size?}`; também é usado pelas páginas de detalhe
com uma única categoria); `care.fix {ids: string[]}` → `{ok, failed, freed, restorePoint, needsReboot, backups[], details: FixResult[]}`
(progresso `{phase:"restore"|"fix", category, title, index, total}` e o que os scanners enviarem).

---

## 2. Módulo Care (`app/Care/**`)

### 2.1 `junk` — Arquivos inúteis
Grupos (`group`): **Windows**, **Navegadores**, **Apps e jogos**, **Sistema**. Cada item = uma regra com tamanho total e contagem.
Ignorar arquivos em uso (pular sem erro). Não seguir junctions/symlinks. Regras mínimas:
- Windows: temporários do usuário (%TEMP%), temporários do Windows (C:\Windows\Temp), cache do Windows Update
  (SoftwareDistribution\Download), cache de Otimização de Entrega, relatórios de erro (WER), despejos de memória
  (Minidump, MEMORY.DMP, %LOCALAPPDATA%\CrashDumps), cache de miniaturas (thumbcache_*.db), logs do Windows (CBS/DISM *.log antigos),
  Prefetch (**selected=false**, `note`: deixa a inicialização dos apps mais lenta por um tempo), Lixeira (SHQueryRecycleBin / SHEmptyRecycleBin).
- Navegadores (cache, NÃO histórico/cookies): Chrome, Edge, Brave, Opera, Opera GX, Vivaldi (todos os perfis: Cache, Code Cache, GPUCache, ShaderCache),
  Firefox (cache2). Avisar no `note` quando o navegador estiver aberto (itens em uso são pulados).
- Apps e jogos: Discord (Cache, Code Cache, GPUCache), Spotify (Data/Storage cache), Microsoft Teams cache, Steam (htmlcache, appcache\httpcache, logs, dumps),
  Epic Games Launcher (webcache*), Battle.net cache, cache de shaders DirectX (%LOCALAPPDATA%\D3DSCache), NVIDIA (DXCache, GLCache, NV_Cache),
  AMD (DxCache, DxcCache, GLCache, VkCache). **Caches de shader: selected=false**, `risk=caution`, `note`: "Os jogos recompilam os shaders na
  próxima abertura (pode haver travadinhas no início)".
- Sistema: arquivos de log de instalação antigos, cache de ícones? (opcional), `Windows.old` só informativo (fixable=false) se existir.
Fix: apagar arquivos da regra (Lixeira via API). `freed` = bytes realmente apagados.

### 2.2 `privacy` — Rastros de privacidade
Grupos: **Windows** (documentos recentes, Jump Lists, histórico do Executar (RunMRU), caminhos digitados no Explorer (TypedPaths),
histórico da área de transferência (opcional), arquivos recentes do Office (opcional)) e **Navegadores** (histórico e cookies por navegador —
**selected=false**, `risk=caution`, `note` para cookies: "Você será desconectado dos sites"). Senhas salvas: NÃO oferecer.
Itens de navegador com o navegador aberto: `note` pedindo para fechar.

### 2.3 `shortcuts` — Atalhos inválidos
Atalhos `.lnk` (Área de Trabalho do usuário e pública, Menu Iniciar do usuário e de todos, Inicialização rápida/barra de tarefas fixada)
cujo destino é um arquivo local inexistente. Ignorar alvos em unidades removíveis/rede ausentes, URLs e itens de shell. Um item por atalho
(`detail` = destino). Fix: **mover** para `Paths.Backups\atalhos\<data>\` (mantendo estrutura) — `backup` = pasta.

### 2.4 `registry` — Registro (conservador)
Grupos: **Desinstaladores inválidos** (Uninstall sem UninstallString válido E sem InstallLocation existente; ignorar SystemComponent=1 e entradas
do Windows), **Caminhos de aplicativos** (App Paths apontando p/ exe inexistente), **DLLs compartilhadas ausentes** (SharedDLLs),
**Cache MUI** (MuiCache com exe inexistente), **Inicialização inválida** (Run/RunOnce apontando p/ arquivo inexistente),
**Fontes ausentes** (HKLM Fonts com arquivo inexistente), **Extensões de arquivo órfãs** (opcional, conservador).
`detail` = chave\valor. Fix: exportar TUDO que será removido para um `.reg` via `Reg.AppendExport`/`Reg.SaveBackup` (um arquivo por correção),
depois remover. `backup` = arquivo `.reg`.

### 2.5 `tweaks` — Otimização do sistema (+ RPCs próprias)
Ajustes reversíveis. Cada ajuste: `id` (ex. `tw.gamemode`), `category` (`gaming`|`system`|`internet`|`privacy`), título, descrição,
`recommended` (bool), `needsReboot`, `risk`. Guardar o valor anterior em `Paths.File("tweaks-backup.json")` antes de aplicar; `revert`
restaura o anterior (ou o padrão do Windows). Lista mínima:
- gaming: Modo de Jogo ligado; Game DVR/gravação em segundo plano desligada; Agendamento de GPU acelerado por hardware (HwSchMode=2, reboot);
  Otimizações para jogos em janela (DirectXUserGlobalSettings SwapEffectUpgradeEnable=1); Prioridade de jogos no MMCSS (SystemResponsiveness=10,
  Tasks\Games GPU Priority=8, Priority=6, Scheduling Category=High, SFIO Priority=High); Aceleração do mouse desligada (MouseSpeed=0,
  MouseThreshold1/2=0, aplicar na hora com SystemParametersInfo SPI_SETMOUSE); Prioridade do primeiro plano (Win32PrioritySeparation=0x26, não recomendado por padrão);
  Desativar limitação de energia (PowerThrottlingOff=1, **não** recomendado em notebook na bateria).
- system: Atraso do menu (MenuShowDelay=100); Encerrar apps travados mais rápido (AutoEndTasks=1, HungAppTimeout=3000, WaitToKillAppTimeout=5000);
  Desligamento mais rápido de serviços (WaitToKillServiceTimeout=2000); Desativar nomes curtos 8.3 do NTFS (fsutil 8dot3name set 1);
  Hibernação desligada (powercfg /h off — **não** recomendado; `detail` com o tamanho do hiberfil.sys, que é liberado);
  Mostrar extensões de arquivos (HideFileExt=0).
- privacy: Telemetria (DiagTrack desativado, AllowTelemetry=0 nas políticas); Dicas, sugestões e anúncios do Windows (ContentDeliveryManager
  SubscribedContent-338388/338389/353694/353696, SystemPaneSuggestionsEnabled, SilentInstalledAppsEnabled, SoftLandingEnabled = 0);
  ID de publicidade (AdvertisingInfo Enabled=0); Recomendações no Menu Iniciar (Start_IrisRecommendations=0); Bing na pesquisa do Menu Iniciar
  (DisableSearchBoxSuggestions=1 em HKCU\Software\Policies\Microsoft\Windows\Explorer).
- internet (categoria de Care `internet`, ver 2.6).

RPCs: `tweaks.list {category?: "gaming"|"system"|"privacy"|"internet"}` →
`[{id, category, title, desc, applied: bool, recommended: bool, needsReboot, risk, valueText?}]`;
`tweaks.apply {ids}` / `tweaks.revert {ids}` → `{results: FixResult[], items: (lista atualizada)}`.
Scanner `tweaks` (Care): itens = ajustes `recommended && !applied` das categorias gaming/system/privacy (current/recommended preenchidos). Fix = apply.

### 2.6 `internet` — Otimização da internet
Itens (reversíveis, mesmos mecanismos dos ajustes, `category=internet`): Ajuste automático da janela TCP = normal; Limitação de rede para
multimídia (NetworkThrottlingIndex=0xFFFFFFFF); Algoritmo de Nagle desligado nas interfaces (TcpAckFrequency=1, TCPNoDelay=1 — `note`: só ajuda em
alguns jogos); Cache DNS (limpar); ECN/RSS ativos. Scanner `internet`: itens recomendados ainda não aplicados. RPC via `tweaks.*` com `category: "internet"`.

### 2.7 `security` — Reforço de segurança
Cada verificação vira item com `status` ok/warn/bad (itens ok também retornam com `fixable=false`, para a interface mostrar o checklist):
Proteção em tempo real do Defender; assinaturas do Defender atualizadas (≤ 3 dias); Firewall ativo em todos os perfis; Controle de Conta de Usuário
(EnableLUA=1); SmartScreen; Execução automática de mídias removíveis desativada (NoDriveTypeAutoRun=0xFF); SMBv1 desativado;
Área de Trabalho Remota desativada (informativo se ligada); Conta Convidado desativada; Windows Update instalado nos últimos 30 dias (informativo,
`action: "ms-settings:windowsupdate"`). Se houver antivírus de terceiros (SecurityCenter2 AntiVirusProduct), não acusar o Defender desligado.
Fix aplica a correção quando possível.

### 2.8 `disk` — Otimização de disco (+ RPCs próprias)
Scanner: um item por volume fixo que precisa de otimização (SSD: TRIM com mais de 7 dias ou desconhecido; HDD: fragmentação > 5%).
Fix = `Optimize-Volume -ReTrim` (SSD) / `-Defrag` (HDD), com progresso.
RPCs: `disk.volumes` → `[{letter, label, media:"SSD"|"HDD"|"Desconhecido", bus, total, free, lastOptimized?: date, fragmentation?: number, action:"trim"|"defrag"|null, needed: bool}]`;
`disk.optimize {letter}` → progresso `{line}` → `{ok, message}`.

### 2.9 `health.summary` (Care) — checklist "Saúde do PC" da Visão geral (rápido, < 1,5 s, sem PowerShell lento)
→ `[{id, title, detail, status: "ok"|"warn"|"bad", action?: {label, route?: "/rota-da-interface", rpc?: "nome", uri?: "ms-settings:..."}}]`.
Itens: sensores da CPU disponíveis (usar `Rage.Monitor.Sensors.DriverStatus()`; rota `/configuracoes`), Proteção do Defender (registro/WMI rápido),
Modo de Jogo ligado, Gravação em segundo plano (Game DVR) desligada, espaço livre no disco do sistema (> 15% ok, > 8% warn), uso de RAM,
reinicialização pendente (chaves RebootPending/RebootRequired), ponto de restauração recente (opcional se rápido).

---

## 3. Módulo Tools (`app/Tools/**`)

### 3.1 Inicialização — `startup.*` + scanner `startup`
`startup.list` → `{items: StartupItem[], boot?: {lastSec, history: [{at, sec}]}}`. Fontes: HKCU/HKLM Run (64 e 32 bits), pastas Inicializar (usuário e comum),
tarefas agendadas com gatilho de logon/inicialização que **não** são da Microsoft (COM Schedule.Service), serviços de terceiros com início automático.
`StartupItem` = `{id, name, publisher, command, path, location, type:"registry"|"folder"|"task"|"service", enabled, impact:"alto"|"médio"|"baixo"|null,
recommendation:"desativar"|"manter"|null, reason?, icon?: dataUrl}`. Habilitar/desabilitar registro e pasta pelo mesmo mecanismo do Gerenciador de
Tarefas (`Explorer\StartupApproved\Run|Run32|StartupFolder`, binário 02/03 + FILETIME); tarefa: Enabled; serviço: Automatic ↔ Manual.
`startup.set {id, enabled}` → item atualizado; `startup.remove {id}` (só registry/folder, com backup); `startup.reveal {id}`.
Boot: Event Log `Microsoft-Windows-Diagnostics-Performance/Operational` ID 100 (BootTime em ms) — últimos 10 (precisa de admin; senão omitir).
Scanner `startup` (Order 50): itens com `recommendation=desativar` e ativos (updaters, launchers, apps de nuvem, mensageiros) — fix = desativar.

### 3.2 Processos — `proc.*`
`proc.list` → `[{pid, name, title, path, publisher, cpu, mem, gpu, threads, priority, isSystem, icon?}]` (cpu % via delta de TotalProcessorTime entre
chamadas, normalizado por núcleos; gpu % via PDH `\GPU Engine(*)\Utilization Percentage` instâncias `pid_X`; ícones em cache).
`proc.kill {pid, tree?}`; `proc.priority {pid, priority:"idle"|"belowNormal"|"normal"|"aboveNormal"|"high"}`; `proc.reveal {pid}`.
`proc.top {by:"mem"|"cpu"|"gpu", limit}` → `[{name, exe, pids:[], count, mem, cpu, gpu, self, icon?}]`: apps que mais usam o recurso, somando os
processos de cada programa e sem os do sistema (usado pelo mini monitor; `self` marca o próprio RAGE!, que não pode ser finalizado).

### 3.3 Programas instalados — `apps.*`
`apps.list` → `[{id, name, publisher, version, size, installDate, icon?, location, source:"win32"|"store", canUninstall}]` (Uninstall HKLM/HKCU/WOW6432,
sem SystemComponent/ParentKeyName/atualizações; Store via `Get-AppxPackage` sem frameworks/sistema).
`apps.uninstall {id}` → executa o desinstalador (interativo; aguarda) → `{ok, leftovers:[{path, size}]}` (pastas remanescentes em Program Files,
ProgramData, AppData com nome do app/fabricante). `apps.removeLeftovers {paths}` → Lixeira.

### 3.4 Atualizador — `updates.*` + scanner `updates`
`updates.list` → `[{id, name, version, available, source}]` (parse de `winget upgrade --include-unknown --accept-source-agreements --disable-interactivity`,
saída localizada: usar a linha de traços para achar as colunas). `updates.install {ids}` → progresso `{id, line}` → `[{id, ok, message}]`
(`winget upgrade --id X -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity`).
Scanner `updates` (Order 100, DefaultOn=false).

### 3.5 Drivers — `drivers.*`
`drivers.list` → `{items:[{name, class, manufacturer, provider, version, date, inf, deviceId, old: bool, problem?: string}], problems: number}` (Win32_PnPSignedDriver;
problemas via Win32_PnPEntity ConfigManagerErrorCode ≠ 0; `old` = data > 3 anos e provedor ≠ Microsoft).
`drivers.backup {folder}` → progresso `{line}` → `{ok, count, folder}` (`pnputil /export-driver * <pasta>`).

### 3.6 Arquivos — `files.*`
`files.large {root, minSize, limit}` → progresso `{scanned, current}` → `[{path, size, modified, ext}]`;
`files.duplicates {root, minSize}` → progresso → `[{hash, size, files:[{path, modified}]}]` (agrupa por tamanho, depois hash parcial e completo);
`files.empty {root}` → `[path]`; `files.recycle {paths}` → `{ok, failed}` (Lixeira, `Microsoft.VisualBasic.FileIO`);
`files.shred {paths, passes}` → progresso → `{ok, failed}` (sobrescreve com aleatório, renomeia, apaga). Nunca tocar em pastas do sistema.

### 3.6b Organizador da área de trabalho — `desktop.*` (`app/Tools/Desktop.cs`)
Lê e posiciona os ícones pela visão de pasta do Explorer (`IShellWindows` → `IFolderView2`), numa thread STA.
`desktop.scan` → `{desktop, grid:{cols, rows, cellW, cellH, iconSize, autoArrange, snapToGrid}, items:[…], backup:{at, count}|null, admin}`.
Cada item: `id` (nome de análise do shell, estável), `name`, `path` (null para Lixeira etc.), `kind` (`app`|`file`|`folder`|`shortcut`|`system`),
`isDir`, `ext`, `target`, `vendor`, `vendorKey` (fornecedor normalizado: "adobe"), `color` (#hex da cor dominante do ícone), `hue` (0–360),
`sat`, `lum`, `neutral` (ícone sem cor), `icon` (PNG 48 px em data URL), `x`/`y` (pixels), `col`/`row` (célula atual), `size`, `modified`, `common` (Área de Trabalho pública).
`desktop.apply {items:[{id, col, row}]}` → `{moved, total, autoArrangeWasOn, backup}`: guarda o arranjo atual em `data\backups\area-de-trabalho\arranjo-*.json`,
desliga a organização automática se estiver ligada e posiciona. **O arranjo é calculado na interface** (`ui/src/lib/deskLayout.js`), não no C#.
`desktop.restore` → `{moved, backup}`: volta ao último arranjo guardado (e religa a organização automática se era o caso).
Arquivos soltos vão para a Lixeira por `files.recycle`.

### 3.7 Informações do sistema — `sysinfo.*`
`sysinfo.get` → `{os:{name, version, build, installDate, uptimeSec, user, machine}, cpu:{name, cores, threads, baseMHz, socket, l2, l3, virtualization},
gpus:[{name, driver, driverDate, vram, resolution, refresh}], ram:{total, modules:[{slot, size, speed, manufacturer, part, type}]},
board:{manufacturer, product, bios, biosDate}, disks:[{model, size, media, bus, health?}], network:[{name, type, mac, ip, speed}],
battery?:{design, full, wearPct, chemistry}}`. `sysinfo.export` → texto do relatório (a interface salva com `app.saveText`).

### 3.8 Reparos do Windows — `fix.*`
`fix.list` → `[{id, title, desc, icon, minutes, needsReboot, admin}]`; `fix.run {id}` → progresso `{line}` → `{ok, exitCode, message}`.
Itens: sfc /scannow (saída UTF-16), DISM CheckHealth/ScanHealth/RestoreHealth, limpeza do repositório de componentes (DISM /StartComponentCleanup),
redefinir rede (winsock reset, int ip reset, flushdns), limpar cache DNS, reconstruir cache de ícones, reiniciar o Explorer, redefinir Windows Update
(para serviços, renomeia SoftwareDistribution/catroot2, religa), redefinir Microsoft Store (wsreset), verificar disco online (chkdsk C: /scan),
sincronizar relógio (w32tm /resync), limpar fila de impressão.

### 3.9 Resgate — `rescue.*`
`rescue.list` → `{points:[{seq, description, date, type}], backups:[{file, kind:"registro"|"atalhos"|"drivers"|"outro", date, size}]}`;
`rescue.createPoint {description}`; `rescue.restoreBackup {file}` (`.reg` → `reg import`; atalhos → mover de volta); `rescue.deleteBackup {file}`;
`rescue.openSystemRestore` (rstrui.exe).

### 3.10 Antivírus (Microsoft Defender) — `av.*` + scanner `antivirus`
`av.status` → `{available, enabled, realtime, tamper, signatureVersion, signatureAgeDays, engineVersion, lastQuickScan, lastFullScan, thirdParty?: string,
threats:[{id, name, severity, path, status, detected}]}`; `av.scan {type:"quick"|"full"|"custom", path?}` → progresso `{line}` → `{ok, threats}`
(MpCmdRun.exe); `av.update` → progresso; `av.remove` → remove ameaças ativas (Remove-MpThreat). Scanner `antivirus` (Order 110, DefaultOn=false):
verificação rápida.

### 3.11 Rede — `net.*`
`net.adapters` → `[{name, desc, type, status, ip, gateway, dns:[], mac, speedMbps, ssid?, signal?}]`;
`net.dnsBenchmark` → `[{id, name, primary, secondary, ms, current: bool}]` (tempo médio de resolução de alguns domínios, incluindo o DNS atual);
`net.setDns {adapter, preset:"auto"|"cloudflare"|"google"|"quad9"|"opendns"|"adguard"}`; `net.ping {host, count}` → `{min, avg, max, loss, jitter}`;
`net.flushDns`.

### 3.12 Benchmark e stress — `bench.*`, `stress.*`
`bench.run {tests?: ["cpuSingle","cpuMulti","memory","disk"]}` → progresso `{test, pct, label}` → `{id, at, scores:{cpuSingle, cpuMulti, memory (GB/s), diskRead (MB/s), diskWrite (MB/s)}, total, system:{cpu, gpu, ram}}`
(pontuação estável e reprodutível; CPU: mistura de inteiros, ponto flutuante e hash; total = combinação; salvar histórico em `Paths.File("bench.json")`);
`bench.history` → lista. `stress.start {minutes, threads?}`, `stress.stop`, `stress.status` → `{running, startedAt, minutes, threads}`
(carga máxima em todos os núcleos; a interface mostra temperatura/clock pela telemetria).

### 3.13 Agendar desligamento — `schedule.*`
`schedule.set {action:"shutdown"|"restart"|"hibernate"|"sleep", minutes}`; `schedule.cancel`; `schedule.get` → `{action, at} | null`.

---

## 4. Já implementado (núcleo)

`app.info`, `app.elevate`, `app.open {target}`, `app.reveal {path}`, `app.regedit {key}`, `app.pickFolder {title}`, `app.pickFiles {title, multi, filter}`,
`app.saveText {fileName, content, filter}`, `app.setTheme {theme}`, `app.show`, `app.hide`, `app.quit`, `app.notify`, `app.openDataFolder`, `app.installSensorDriver`,
`settings.get`, `settings.set {values}`, `activity.list {limit}`, `activity.clear`,
`monitor.snapshot`, `monitor.history {seconds}`, `monitor.sensors`, `fps.status`, `overlay.toggle {show}`, `overlay.applySettings`,
`overlay.setHotkey {hotkey}` (ex.: `"Ctrl+Shift+F10"`; nomes de tecla de `System.Windows.Forms.Keys`; erro se o Windows recusar a combinação, e o
atalho anterior continua valendo), `overlay.captureHotkey {on}` (solta o atalho enquanto a tela de configurações grava uma combinação nova),
`mini.state` → `{enabled, open, hideFullscreen}`, `mini.close`, `mini.setEnabled {on}`, `mini.resetPosition`,
`boost.status`, `boost.start {mode}`, `boost.stop`, `boost.config`, `boost.setApps {apps}`, `boost.setServices {services, reset}`,
`ram.clean {includeForeground}`, `ram.top`, `power.plans`, `power.setPlan {guid|"rage"}`, `power.setOverlay {mode}`, `power.setCpu {...}`,
`games.list`, `games.scan`, `games.add {exe}`, `games.update {id, autoBoost, highPerfGpu, name, hidden}`, `games.remove {id}`, `games.launch {id, boost}`,
`care.categories`, `care.scan`, `care.fix`, `care.last`.

Eventos enviados pelo host: `telemetry` (Snapshot a cada 1 s, só para as páginas que estão na tela), `activity`, `boost`, `game`, `overlay`,
`mini {open}`, `app.shown`.

### 4.1 Mini monitor (`app/Monitor/MiniMonitor.cs`)

Pílula flutuante (janela em camadas desenhada em GDI+, não rouba o foco) com RAM e CPU; arrastar reposiciona e grava `miniMonitorPos`, um clique
abre o painel. O painel é um segundo WebView2 no mesmo ambiente da janela principal, carregando a rota `#/mini` (`ui/src/pages/MiniPanel.jsx`),
e é descartado 2 minutos depois de fechado. A ponte (`Bridge`) aceita várias páginas: respostas voltam para quem chamou, eventos vão para todas.
Configurações: `miniMonitor` (liga/desliga), `miniMonitorHideFullscreen` (some em tela cheia sem bordas), `miniMonitorPos` (`"x,y"` em pixels).
Para testes: `--mini-open` já abre com o painel, `--overlay-show` já abre com o overlay.
