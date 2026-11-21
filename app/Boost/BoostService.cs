using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading.Tasks;
using Rage.Core;

namespace Rage.Boost
{
    /// <summary>Modo Turbo: Trabalho, Jogo e Economia. Tudo que é alterado é desfeito ao desligar.</summary>
    public static class BoostService
    {
        public sealed class KnownService
        {
            public string Name; public string Title; public string Desc; public bool Game; public bool Work;
        }

        // Serviços que podem ser pausados com segurança durante uma sessão (são religados ao desligar o Turbo).
        public static readonly List<KnownService> KnownServices = new List<KnownService>
        {
            new KnownService { Name = "Spooler", Title = "Spooler de Impressão", Desc = "Fila de impressão. Ninguém imprime no meio da partida.", Game = true, Work = false },
            new KnownService { Name = "WSearch", Title = "Windows Search", Desc = "Indexação de arquivos em segundo plano (usa disco e CPU).", Game = true, Work = false },
            new KnownService { Name = "SysMain", Title = "SysMain (Superfetch)", Desc = "Pré-carrega apps na RAM. Pausar libera memória e disco.", Game = true, Work = false },
            new KnownService { Name = "DiagTrack", Title = "Telemetria do Windows", Desc = "Envio de dados de diagnóstico para a Microsoft.", Game = true, Work = true },
            new KnownService { Name = "dmwappushservice", Title = "Roteamento de mensagens WAP", Desc = "Usado pela telemetria.", Game = true, Work = true },
            new KnownService { Name = "wuauserv", Title = "Windows Update", Desc = "Evita downloads de atualização durante o jogo.", Game = true, Work = false },
            new KnownService { Name = "UsoSvc", Title = "Orquestrador de atualizações", Desc = "Agenda instalações do Windows Update.", Game = true, Work = false },
            new KnownService { Name = "WerSvc", Title = "Relatório de Erros do Windows", Desc = "Coleta e envia relatórios de falhas.", Game = true, Work = true },
            new KnownService { Name = "PcaSvc", Title = "Assistente de Compatibilidade", Desc = "Monitora programas antigos.", Game = true, Work = false },
            new KnownService { Name = "MapsBroker", Title = "Mapas baixados", Desc = "Atualiza mapas offline.", Game = true, Work = true },
            new KnownService { Name = "lfsvc", Title = "Geolocalização", Desc = "Localização do dispositivo.", Game = true, Work = false },
            new KnownService { Name = "TrkWks", Title = "Rastreamento de links distribuídos", Desc = "Mantém atalhos de rede atualizados.", Game = true, Work = true },
            new KnownService { Name = "Fax", Title = "Fax", Desc = "Serviço de fax.", Game = true, Work = true },
            new KnownService { Name = "RetailDemo", Title = "Modo de demonstração", Desc = "Modo de loja.", Game = true, Work = true },
            new KnownService { Name = "WMPNetworkSvc", Title = "Compartilhamento do Media Player", Desc = "Compartilha mídia na rede.", Game = true, Work = true },
            new KnownService { Name = "PhoneSvc", Title = "Serviço de telefonia", Desc = "Gerencia estado de telefonia.", Game = true, Work = false },
            new KnownService { Name = "TabletInputService", Title = "Teclado virtual e escrita à mão", Desc = "Só pause se não usar tela sensível ao toque.", Game = false, Work = false },
            new KnownService { Name = "BITS", Title = "Transferência inteligente (BITS)", Desc = "Downloads em segundo plano do Windows e de apps.", Game = false, Work = false },
        };

        // Apps comuns em segundo plano que podem ser fechados sem prejuízo
        public static readonly Dictionary<string, string> KnownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OneDrive"] = "Microsoft OneDrive", ["Teams"] = "Microsoft Teams (clássico)", ["ms-teams"] = "Microsoft Teams",
            ["Skype"] = "Skype", ["GoogleDriveFS"] = "Google Drive", ["Dropbox"] = "Dropbox", ["AdobeCollabSync"] = "Adobe Acrobat (sincronização)",
            ["CCXProcess"] = "Adobe Creative Cloud", ["Creative Cloud"] = "Adobe Creative Cloud", ["AdobeIPCBroker"] = "Adobe IPC",
            ["PhoneExperienceHost"] = "Vincular ao Celular", ["YourPhone"] = "Vincular ao Celular", ["Widgets"] = "Widgets do Windows",
            ["msedge"] = "Microsoft Edge", ["chrome"] = "Google Chrome", ["firefox"] = "Firefox", ["opera"] = "Opera", ["brave"] = "Brave",
            ["Spotify"] = "Spotify", ["WhatsApp"] = "WhatsApp", ["Telegram"] = "Telegram", ["Zoom"] = "Zoom", ["slack"] = "Slack",
            ["EpicGamesLauncher"] = "Epic Games Launcher", ["Battle.net"] = "Battle.net", ["EADesktop"] = "EA app", ["upc"] = "Ubisoft Connect",
            ["iCloudServices"] = "iCloud", ["Lightshot"] = "Lightshot", ["AnyDesk"] = "AnyDesk", ["TeamViewer"] = "TeamViewer",
        };
        static readonly HashSet<string> RecommendedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "OneDrive", "Teams", "ms-teams", "Skype", "GoogleDriveFS", "Dropbox", "AdobeCollabSync", "CCXProcess", "Creative Cloud", "AdobeIPCBroker", "PhoneExperienceHost", "YourPhone", "Widgets", "iCloudServices" };

        static readonly object Gate = new object();
        public static bool Active { get; private set; }
        public static string Mode { get; private set; }
        public static DateTime Since { get; private set; }
        public static string Source { get; private set; }
        public static bool AutoStarted { get; private set; }
        public static int GamePid { get; private set; }
        public static string GameName { get; private set; }
        static Guid prevScheme;
        static Guid? prevOverlay;
        static readonly List<string> stoppedServices = new List<string>();
        static readonly List<string> closedApps = new List<string>();
        static readonly List<string> actions = new List<string>();
        static double freedRam;

        public static object Status() => new
        {
            active = Active, mode = Mode, since = Active ? Since : (DateTime?)null, source = Source, auto = AutoStarted,
            game = GamePid > 0 ? new { pid = GamePid, name = GameName } : null,
            actions = Active ? actions.ToList() : new List<string>(),
            freed = freedRam,
        };

        public static List<string> ServicesFor(string mode)
        {
            var custom = Settings.Current.BoostServices;
            if (custom != null) return custom;
            return KnownServices.Where(s => mode == "game" ? s.Game : s.Work).Select(s => s.Name).ToList();
        }

        public static object Start(string mode, int? gamePid, string source)
        {
            lock (Gate)
            {
                if (Active) Stop("troca de modo", true);
                mode = mode == "work" || mode == "economy" ? mode : "game";
                var st = Settings.Current;
                actions.Clear(); stoppedServices.Clear(); closedApps.Clear(); freedRam = 0;
                Active = true; Mode = mode; Since = DateTime.Now; Source = source; AutoStarted = source != null && source.StartsWith("auto");
                GamePid = gamePid ?? 0;
                GameName = null;
                if (GamePid > 0) { try { using (var p = Process.GetProcessById(GamePid)) GameName = p.ProcessName; } catch { GamePid = 0; } }

                // 1) Energia
                if (st.BoostPowerPlan && Program.IsAdmin)
                {
                    try
                    {
                        prevScheme = Power.ActiveScheme();
                        prevOverlay = Power.Overlay();
                        if (mode == "economy")
                        {
                            if (Power.SetOverlay(Power.OverlayBestEfficiency)) actions.Add("Modo de energia: melhor eficiência");
                            else if (Power.SetScheme(Power.PowerSaver)) actions.Add("Plano de energia: economia");
                        }
                        else
                        {
                            var plan = mode == "game" ? Power.EnsureRagePlan() : Power.HighPerformance;
                            if (Power.SetScheme(plan)) actions.Add(mode == "game" ? "Plano de energia RAGE! Turbo ativado" : "Plano de energia: alto desempenho");
                            else if (Power.SetScheme(Power.HighPerformance)) actions.Add("Plano de energia: alto desempenho");
                            if (Power.SetOverlay(Power.OverlayBestPerformance)) actions.Add("Modo de energia: melhor desempenho");
                        }
                    }
                    catch (Exception ex) { Log.Warn("Boost energia: " + ex.Message); }
                }

                // 2) Serviços
                if (st.BoostStopServices && Program.IsAdmin)
                {
                    var names = ServicesFor(mode);
                    Parallel.ForEach(names, new ParallelOptions { MaxDegreeOfParallelism = 4 }, n =>
                    {
                        try
                        {
                            using (var sc = new ServiceController(n))
                            {
                                if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop) return;
                                sc.Stop();
                                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                                lock (stoppedServices) stoppedServices.Add(n);
                            }
                        }
                        catch (Exception ex) { Log.Warn("Parar serviço " + n + ": " + ex.Message); }
                    });
                    if (stoppedServices.Count > 0) actions.Add(stoppedServices.Count + " serviços pausados");
                }

                // 3) Apps em segundo plano
                if (st.BoostCloseApps && st.BoostApps != null && st.BoostApps.Count > 0)
                {
                    var fg = (int)Native.ForegroundPid();
                    foreach (var name in st.BoostApps.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        foreach (var p in Process.GetProcessesByName(name))
                        {
                            try
                            {
                                if (p.Id == GamePid || p.Id == fg) continue;
                                string path = null; try { path = p.MainModule?.FileName; } catch { }
                                if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                                if (!p.WaitForExit(1500)) p.Kill();
                                if (path != null && !closedApps.Contains(path, StringComparer.OrdinalIgnoreCase)) closedApps.Add(path);
                            }
                            catch { }
                            finally { p.Dispose(); }
                        }
                    }
                    if (closedApps.Count > 0) actions.Add(closedApps.Count + " apps em segundo plano fechados");
                }

                // 4) RAM
                if (st.BoostCleanRam)
                {
                    var r = RamCleaner.Clean(false, GamePid > 0 ? GamePid : (int?)null);
                    freedRam = r.Freed;
                    actions.Add("RAM liberada: " + r.FreedText);
                }

                // 5) Prioridade do jogo e sem limitação de energia (EcoQoS)
                if (GamePid > 0 && st.BoostGamePriority && mode == "game") ApplyGamePriority(GamePid);

                // 6) DNS
                if (st.BoostFlushDns) { try { DnsFlushResolverCache(); actions.Add("Cache DNS limpo"); } catch { } }

                var title = mode == "game" ? "Modo Turbo: Jogo" : mode == "work" ? "Modo Turbo: Trabalho" : "Modo Economia";
                Activity.Add("boost", "rocket_launch", title + " ativado", GameName != null ? "Jogo: " + GameName : source, freedRam > 0 ? "+" + Fmt.Bytes(freedRam) : null);
                Log.Info("Boost ligado: " + mode + " (" + source + ") " + string.Join("; ", actions));
                if (st.BoostNotify && AutoStarted) MainForm.Instance?.Notify(title + " ativado", (GameName != null ? GameName + " · " : "") + string.Join(" · ", actions.Take(3)));
                Bridge.Emit("boost", Status());
                return Status();
            }
        }

        public static void ApplyGamePriority(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    p.PriorityClass = ProcessPriorityClass.High;
                    DisablePowerThrottling(p.Handle);
                }
                if (!actions.Contains("Jogo com prioridade alta")) actions.Add("Jogo com prioridade alta");
            }
            catch (Exception ex) { Log.Warn("Prioridade do jogo: " + ex.Message); }
        }

        public static void AttachGame(int pid)
        {
            lock (Gate)
            {
                if (!Active || GamePid == pid) return;
                GamePid = pid;
                try { using (var p = Process.GetProcessById(pid)) GameName = p.ProcessName; } catch { }
                if (Settings.Current.BoostGamePriority && Mode == "game") ApplyGamePriority(pid);
                Bridge.Emit("boost", Status());
            }
        }

        public static object Stop(string source, bool silent = false)
        {
            lock (Gate)
            {
                if (!Active) return Status();
                var restored = new List<string>();
                try
                {
                    if (prevScheme != Guid.Empty) { Power.SetScheme(prevScheme); restored.Add("plano de energia"); }
                    if (prevOverlay.HasValue) Power.SetOverlay(prevOverlay.Value);
                }
                catch { }
                foreach (var n in stoppedServices.ToList())
                {
                    try { using (var sc = new ServiceController(n)) if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); }
                    catch (Exception ex) { Log.Warn("Religar serviço " + n + ": " + ex.Message); }
                }
                if (stoppedServices.Count > 0) restored.Add(stoppedServices.Count + " serviços");
                if (GamePid > 0) { try { using (var p = Process.GetProcessById(GamePid)) p.PriorityClass = ProcessPriorityClass.Normal; } catch { } }
                foreach (var path in closedApps.ToList())
                {
                    try { if (File.Exists(path) && Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)).Length == 0) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) }); }
                    catch { }
                }
                if (closedApps.Count > 0) restored.Add(closedApps.Count + " apps reabertos");
                var dur = DateTime.Now - Since;
                var mode = Mode;
                Active = false; Mode = null; GamePid = 0; GameName = null; AutoStarted = false;
                prevScheme = Guid.Empty; prevOverlay = null; stoppedServices.Clear(); closedApps.Clear();
                if (!silent)
                {
                    Activity.Add("boost", "power_settings_new", "Modo Turbo desligado", "Duração: " + (dur.TotalHours >= 1 ? ((int)dur.TotalHours) + "h " : "") + dur.Minutes + "min" + (restored.Count > 0 ? " · restaurado: " + string.Join(", ", restored) : ""));
                    Log.Info("Boost desligado (" + source + ")");
                }
                Bridge.Emit("boost", Status());
                return Status();
            }
        }

        // ---------- P/Invoke ----------
        [DllImport("dnsapi.dll")] static extern bool DnsFlushResolverCache();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessInformation(IntPtr h, int cls, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [StructLayout(LayoutKind.Sequential)] struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        static void DisablePowerThrottling(IntPtr h)
        {
            var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 0 }; // EXECUTION_SPEED desligado
            try { SetProcessInformation(h, 4, ref s, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE))); } catch { }
        }

        public static bool FlushDns() { try { return DnsFlushResolverCache(); } catch { return false; } }

        // ---------- RPC ----------
        [Rpc("boost.status")]
        public static object StatusRpc() => Status();

        [Rpc("boost.start")]
        public static object StartRpc(string mode)
        {
            var fg = (int)Native.ForegroundPid();
            var game = GameWatcher.RunningGamePid();
            return Start(mode ?? Settings.Current.BoostMode, game ?? (int?)null, "manual");
        }

        [Rpc("boost.stop")]
        public static object StopRpc() => Stop("manual");

        [Rpc("boost.config")]
        public static object Config()
        {
            var st = Settings.Current;
            var selected = new HashSet<string>(ServicesFor(st.BoostMode), StringComparer.OrdinalIgnoreCase);
            var services = KnownServices.Select(k =>
            {
                string status = "ausente", startType = null;
                try { using (var sc = new ServiceController(k.Name)) { status = sc.Status == ServiceControllerStatus.Running ? "rodando" : "parado"; startType = sc.StartType.ToString(); } } catch { }
                return new { name = k.Name, title = k.Title, desc = k.Desc, recommended = k.Game, status, startType, selected = selected.Contains(k.Name) };
            }).Where(s => s.status != "ausente").ToList();

            var running = new Dictionary<string, (int count, double mem, string title, bool window)>(StringComparer.OrdinalIgnoreCase);
            var self = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.SessionId != self || p.Id == 0) continue;
                    var n = p.ProcessName;
                    if (n.Equals("RAGE", StringComparison.OrdinalIgnoreCase) || n.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                    var known = KnownApps.TryGetValue(n, out var t);
                    var hasWin = p.MainWindowHandle != IntPtr.Zero;
                    if (!known && !hasWin) continue;
                    if (IsSystemProcess(n)) continue;
                    (int count, double mem, string title, bool window) e = running.TryGetValue(n, out var prev) ? prev : (0, 0d, t ?? (hasWin ? SafeTitle(p) : n), false);
                    running[n] = (e.count + 1, e.mem + p.WorkingSet64, e.title, e.window || hasWin);
                }
                catch { }
                finally { p.Dispose(); }
            }
            var apps = running.Select(kv => new
            {
                name = kv.Key,
                title = KnownApps.TryGetValue(kv.Key, out var t) ? t : kv.Value.title,
                processes = kv.Value.count,
                memory = kv.Value.mem,
                recommended = RecommendedApps.Contains(kv.Key),
                selected = st.BoostApps.Contains(kv.Key, StringComparer.OrdinalIgnoreCase),
            }).OrderByDescending(a => a.selected).ThenByDescending(a => a.recommended).ThenByDescending(a => a.memory).ToList();
            // Apps marcados que não estão rodando agora
            foreach (var n in st.BoostApps.Where(n => !running.ContainsKey(n)))
                apps.Add(new { name = n, title = KnownApps.TryGetValue(n, out var t) ? t : n, processes = 0, memory = 0d, recommended = RecommendedApps.Contains(n), selected = true });

            return new { services, apps, settings = st, status = Status(), admin = Program.IsAdmin };
        }

        static string SafeTitle(Process p) { try { var t = p.MainWindowTitle; return string.IsNullOrWhiteSpace(t) ? p.ProcessName : (t.Length > 60 ? t.Substring(0, 60) + "…" : t); } catch { return p.ProcessName; } }

        static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "LockApp", "dwm", "csrss", "winlogon", "sihost", "taskhostw", "ctfmon", "RuntimeBroker", "conhost", "WindowsTerminal", "OpenConsole", "Taskmgr", "mmc", "powershell", "cmd", "pwsh" };
        static bool IsSystemProcess(string n) => SystemNames.Contains(n);

        [Rpc("boost.setApps")]
        public static object SetApps(List<string> apps)
        {
            Settings.Current.BoostApps = apps ?? new List<string>();
            Settings.Save();
            return Config();
        }

        [Rpc("boost.setServices")]
        public static object SetServices(List<string> services, bool reset = false)
        {
            Settings.Current.BoostServices = reset ? null : services;
            Settings.Save();
            return Config();
        }
    }
}
