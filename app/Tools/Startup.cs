using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json;
using Rage.Core;

namespace Rage.Tools
{
    public sealed class StartupItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Command { get; set; }
        public string Path { get; set; }
        public string Location { get; set; }
        public string Type { get; set; }            // registry | folder | task | service
        public bool Enabled { get; set; }
        public string Impact { get; set; }          // alto | médio | baixo | null
        public string Recommendation { get; set; }  // desativar | manter | null
        public string Reason { get; set; }
        public string Icon { get; set; }
        [JsonIgnore] public string Group { get; set; }      // categoria interna (Atualizadores, Nuvem...)
        [JsonIgnore] public bool Caution { get; set; }      // desativar tem efeito colateral visível
        [JsonIgnore] public string Key { get; set; }        // chave do registro / pasta / caminho da tarefa / nome do serviço
        [JsonIgnore] public string ValueName { get; set; }  // valor do registro / arquivo / nome do serviço
        [JsonIgnore] public string ApprovedKey { get; set; }
        [JsonIgnore] public bool Machine { get; set; }      // HKLM / pasta comum / serviço (precisa de admin)
    }

    /// <summary>Programas que iniciam com o Windows: registro, pastas Inicializar, tarefas agendadas e serviços.</summary>
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        sealed class RegSource { public string Scope; public string Key; public string ApprovedKey; public bool Machine; }

        static readonly RegSource[] RegSources =
        {
            new RegSource { Scope = "HKCU", Key = @"HKCU\" + RunKey, ApprovedKey = @"HKCU\" + Approved + "Run", Machine = false },
            new RegSource { Scope = "HKLM", Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ApprovedKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", Machine = true },
            new RegSource { Scope = "HKLM32", Key = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", Machine = true },
        };

        static string UserStartup => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        static string CommonStartup => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

        // Serviços que o RAGE! passou para Manual (para continuarem na lista como desativados)
        sealed class SvcState { public string Name { get; set; } public bool Delayed { get; set; } public DateTime At { get; set; } }
        static string SvcStateFile => Paths.File("startup-services.json");

        // ---------- Listagem ----------
        public static List<StartupItem> Collect(bool icons = true)
        {
            var list = new List<StartupItem>();
            void Try(Action a, string what) { try { a(); } catch (Exception ex) { Log.Warn("Inicialização (" + what + "): " + ex.Message); } }
            Try(() => list.AddRange(FromRegistry()), "registro");
            Try(() => list.AddRange(FromFolder(UserStartup, false)), "pasta do usuário");
            Try(() => list.AddRange(FromFolder(CommonStartup, true)), "pasta comum");
            Try(() => list.AddRange(FromTasks()), "tarefas");
            Try(() => list.AddRange(FromServices()), "serviços");
            foreach (var it in list)
            {
                if (string.IsNullOrEmpty(it.Publisher)) it.Publisher = ToolsUtil.Publisher(it.Path);
                Classify(it);
                if (icons) it.Icon = ToolsUtil.IconData(it.Path);
            }
            list = list.OrderByDescending(x => x.Enabled).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            lock (CacheGate) { cache = list; cacheAt = DateTime.Now; }
            return list;
        }

        static bool ApprovedEnabled(string approvedKey, string valueName)
        {
            var v = Reg.Get(approvedKey, valueName) as byte[];
            if (v == null || v.Length == 0) return true;
            return (v[0] & 1) == 0; // 02/06 = ativado, 03/07 = desativado
        }

        static IEnumerable<StartupItem> FromRegistry()
        {
            foreach (var src in RegSources)
            {
                using (var k = Reg.Open(src.Key))
                {
                    if (k == null) continue;
                    foreach (var n in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(n)) continue;
                        string cmd;
                        try { cmd = Convert.ToString(k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames)); } catch { continue; }
                        if (string.IsNullOrWhiteSpace(cmd)) continue;
                        var exe = ToolsUtil.ExeFromCommand(cmd);
                        yield return new StartupItem
                        {
                            Id = "reg:" + src.Scope + ":" + n,
                            Name = NiceName(n, exe),
                            Command = cmd,
                            Path = exe,
                            Location = src.Key,
                            Type = "registry",
                            Enabled = ApprovedEnabled(src.ApprovedKey, n),
                            Key = src.Key, ValueName = n, ApprovedKey = src.ApprovedKey, Machine = src.Machine,
                        };
                    }
                }
            }
        }

        static IEnumerable<StartupItem> FromFolder(string dir, bool common)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
            var approved = (common ? @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder" : @"HKCU\" + Approved + "StartupFolder");
            foreach (var f in Directory.GetFiles(dir))
            {
                var fn = System.IO.Path.GetFileName(f);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string target = f, args = null;
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) ResolveShortcut(f, out target, out args);
                if (string.IsNullOrEmpty(target)) target = f;
                yield return new StartupItem
                {
                    Id = "folder:" + (common ? "common" : "user") + ":" + fn,
                    Name = NiceName(System.IO.Path.GetFileNameWithoutExtension(fn), target),
                    Command = string.IsNullOrEmpty(args) ? target : "\"" + target + "\" " + args,
                    Path = target,
                    Location = dir,
                    Type = "folder",
                    Enabled = ApprovedEnabled(approved, fn),
                    Key = dir, ValueName = fn, ApprovedKey = approved, Machine = common,
                };
            }
        }

        public static void ResolveShortcut(string lnk, out string target, out string args)
        {
            target = null; args = null;
            object shell = null, sc = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                dynamic d = shell;
                sc = d.CreateShortcut(lnk);
                dynamic s = sc;
                target = s.TargetPath as string;
                args = s.Arguments as string;
                if (string.IsNullOrWhiteSpace(target)) target = null;
            }
            catch { }
            finally { TaskSched.Release(sc); TaskSched.Release(shell); }
        }

        static IEnumerable<StartupItem> FromTasks()
        {
            var res = new List<StartupItem>();
            dynamic svc = TaskSched.Connect();
            try
            {
                void Walk(dynamic folder)
                {
                    string fpath = folder.Path;
                    if (fpath.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;
                    try
                    {
                        dynamic tasks = folder.GetTasks(1); // inclui ocultas
                        foreach (dynamic t in tasks)
                        {
                            try
                            {
                                var it = TaskItem(t);
                                if (it != null) res.Add(it);
                            }
                            catch { }
                            finally { TaskSched.Release(t); }
                        }
                        TaskSched.Release(tasks);
                    }
                    catch { }
                    try
                    {
                        dynamic subs = folder.GetFolders(0);
                        foreach (dynamic s in subs) { try { Walk(s); } catch { } finally { TaskSched.Release(s); } }
                        TaskSched.Release(subs);
                    }
                    catch { }
                }
                dynamic root = svc.GetFolder("\\");
                Walk(root);
                TaskSched.Release(root);
            }
            finally { TaskSched.Release(svc); }
            return res;
        }

        static StartupItem TaskItem(dynamic t)
        {
            dynamic def = t.Definition;
            bool atStart = false;
            foreach (dynamic tr in def.Triggers)
            {
                int type = tr.Type;
                if (type == 8 || type == 9) { bool en = true; try { en = tr.Enabled; } catch { } if (en) atStart = true; }
                TaskSched.Release(tr);
            }
            if (!atStart) return null;
            string exe = null, args = null;
            foreach (dynamic a in def.Actions)
            {
                int type = a.Type;
                if (type == 0 && exe == null) { exe = a.Path as string; args = a.Arguments as string; }
                TaskSched.Release(a);
            }
            if (string.IsNullOrWhiteSpace(exe)) return null;
            exe = exe.Trim().Trim('"');
            var full = ToolsUtil.ExeFromCommand("\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
            var pub = ToolsUtil.Publisher(full);
            if (ToolsUtil.IsMicrosoft(pub)) return null;
            string author = null;
            try { author = def.RegistrationInfo.Author as string; } catch { }
            if (ToolsUtil.IsMicrosoft(author) && (full ?? "").StartsWith(ToolsUtil.WinDir, StringComparison.OrdinalIgnoreCase)) return null;
            string path = t.Path, name = t.Name;
            bool enabled = t.Enabled;
            return new StartupItem
            {
                Id = "task:" + path,
                Name = NiceName(name, full, false),
                Publisher = pub,
                Command = "\"" + exe + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args),
                Path = full,
                Location = path,
                Type = "task",
                Enabled = enabled,
                Key = path, ValueName = name, Machine = true,
            };
        }

        static IEnumerable<StartupItem> FromServices()
        {
            var res = new List<StartupItem>();
            var mine = Json.Load(SvcStateFile, new List<SvcState>()).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            ServiceController[] all;
            try { all = ServiceController.GetServices(); } catch { return res; }
            foreach (var sc in all)
            {
                try
                {
                    var auto = sc.StartType == ServiceStartMode.Automatic;
                    var oursDisabled = !auto && sc.StartType == ServiceStartMode.Manual && mine.ContainsKey(sc.ServiceName);
                    if (!auto && !oursDisabled) continue;
                    var key = @"HKLM\SYSTEM\CurrentControlSet\Services\" + sc.ServiceName;
                    var image = Reg.GetStr(key, "ImagePath");
                    if (string.IsNullOrWhiteSpace(image)) continue;
                    var exe = ToolsUtil.ExeFromCommand(image);
                    if (exe == null) continue;
                    if (exe.StartsWith(ToolsUtil.WinDir, StringComparison.OrdinalIgnoreCase) && !oursDisabled) continue; // svchost e serviços do Windows
                    var pub = ToolsUtil.Publisher(exe);
                    if (ToolsUtil.IsMicrosoft(pub) && !oursDisabled) continue;
                    res.Add(new StartupItem
                    {
                        Id = "svc:" + sc.ServiceName,
                        Name = sc.DisplayName,
                        Publisher = pub,
                        Command = image,
                        Path = exe,
                        Location = "Serviço: " + sc.ServiceName,
                        Type = "service",
                        Enabled = auto,
                        Key = key, ValueName = sc.ServiceName, Machine = true,
                    });
                }
                catch { }
                finally { sc.Dispose(); }
            }
            return res;
        }

        /// <summary>Nome amigável: descrição do arquivo (como o Gerenciador de Tarefas) ou o nome do valor sem sufixos de GUID.</summary>
        static string NiceName(string raw, string exe, bool preferDescription = true)
        {
            var d = ToolsUtil.Description(exe);
            var r = raw == null ? null : Regex.Replace(raw, @"[_\s-]*[{(]?[0-9A-Fa-f]{8}-?[0-9A-Fa-f-]{8,}[})]?$|[_\s-]+[0-9A-Fa-f]{16,}$", "").Trim();
            if (string.IsNullOrWhiteSpace(r) || r.Length < 3) return d ?? raw ?? System.IO.Path.GetFileName(exe ?? "?");
            // Descrições genéricas de hospedeiros não servem como nome
            if (preferDescription && !string.IsNullOrEmpty(d) && !Regex.IsMatch(d, @"^(rundll|windows host|host process|windows command|windows powershell|wscript|microsoft ® windows based script)", RegexOptions.IgnoreCase) && d.Length <= 80) return d;
            return r;
        }

        // ---------- Classificação (impacto e recomendação) ----------
        sealed class Rule
        {
            public Regex Re; public string Rec; public string Group; public string Reason; public string Impact; public bool Caution;
            public Rule(string re, string rec, string group, string reason, string impact = null, bool caution = false)
            { Re = new Regex(re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); Rec = rec; Group = group; Reason = reason; Impact = impact; Caution = caution; }
        }

        static readonly Rule[] Rules =
        {
            new Rule(@"\brage\b|rage\.exe", "manter", "RAGE!", "O RAGE! inicia na bandeja para ligar o Modo Turbo e o monitor automaticamente."),
            new Rule(@"securityhealth|windows defender|msascui|avast|\bavg|kaspersky|\bavp(ui)?\b|\beset\b|egui|ekrn|norton|bitdefender|mcafee|malwarebytes|mbam|sophos|trend ?micro|webroot|panda ?(dome|security)|f-secure|comodo|360 ?total|avira|zonealarm|emsisoft|gdata|g data|antivirus|anti-virus|antimalware|ascavsvc",
                "manter", "Segurança", "Proteção do computador: mantenha ativo."),
            new Rule(@"usagetracking|usage ?tracking|telemetry|telemetria|\bceip\b|customer ?experience",
                "desativar", "Outros apps", "Coleta de dados de uso (telemetria) do fabricante: não é necessária para nada funcionar.", "baixo"),
            new Rule(@"update|updater|\bupd\b|jusched|adobearm|armsvc|supportassist|driver ?(&|and)? ?support ?assist|dsatray|softwareupdate|autoupdate|update ?checker|helperservice|crashpad|crash ?report",
                "desativar", "Atualizadores", "Atualizador em segundo plano: o programa continua procurando atualizações quando você abri-lo.", "baixo"),
            new Rule(@"realtek|rtkaud|rthdvcpl|rtkngui|nahimic|waves|maxxaudio|dolby|nvidia|nvcontainer|nvbackend|nvdisplay|radeon|\bamd\b|igfx|intel\(r\)|intelgraphics|igcc|synaptics|\belan\b|etdctrl|alps|logitech|\blogi|lghub|razer|synapse|icue|corsair|steelseries|armoury|asus|rog |msi ?center|dragon ?center|dell|alienware|lenovo|vantage|\bhp |hpsa|omen|acer|predator|bluetooth|touchpad|hotkey|thunderbolt|smart ?sound|conexant|\bidt\b|tobii|wacom|xbox ?game ?bar|gamebar|gameinput|xboxservice|xbox input",
                "manter", "Drivers e hardware", "Driver ou utilitário de hardware (áudio, vídeo, touchpad, teclado, periféricos): mantenha para tudo funcionar direito."),
            new Rule(@"edgeautolaunch|browser_?assistant|\\(msedge|chrome|opera|brave|firefox|vivaldi)\.exe",
                "desativar", "Outros apps", "Navegador abrindo junto com o Windows: ele abre rápido quando você precisar.", "médio"),
            new Rule(@"steam|epicgameslauncher|epic games|battle\.net|blizzard|eadesktop|ea desktop|\bea app|origin|ubisoft|\bupc\.exe|uplay|gog ?galaxy|galaxyclient|riot ?client|riotclient|rockstar|playnite|amazon games|itch|xboxapp|wargaming",
                "desativar", "Launchers de jogos", "Launcher de jogos: abra só quando for jogar (os jogos continuam funcionando normalmente).", "alto"),
            new Rule(@"onedrive|dropbox|googledrivefs|google ?drive|icloud|megasync|mega ?sync|box ?drive|pcloud|nextcloud|syncthing|sync ?center",
                "desativar", "Nuvem", "Sincronização na nuvem: os arquivos só serão sincronizados enquanto o app estiver aberto.", "alto", true),
            new Rule(@"discord|telegram|whatsapp|skype|\bteams\b|ms-teams|\bzoom\b|slack|signal|messenger|wechat|\bline\.exe|viber",
                "desativar", "Mensageiros", "Mensageiro: abre sozinho e fica em segundo plano. Você só recebe mensagens com o app aberto.", "alto", true),
            new Rule(@"spotify|ituneshelper|itunes ?helper|creative ?cloud|ccxprocess|adobe ?gc|acrobat|cortana|ccleaner|utorrent|bittorrent|qbittorrent|deluge|anydesk|teamviewer|lightshot|greenshot|sharex|\bobs\b|nvidia broadcast|figma|notion|evernote|gamebooster|driver ?booster|wise ?care|glary|avast ?cleanup",
                "desativar", "Outros apps", "Não precisa iniciar com o Windows: abra quando for usar.", null),
        };

        static readonly Rule AvRule = new Rule("$^", "manter", "Segurança", "Faz parte do antivírus instalado: mantenha ativo.");
        static List<string> avNames;

        /// <summary>Nomes dos antivírus registrados na Central de Segurança (para nunca recomendar desativá-los).</summary>
        static List<string> AvNames()
        {
            if (avNames != null) return avNames;
            var list = new List<string>();
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher(new System.Management.ManagementScope(@"\\.\root\SecurityCenter2"), new System.Management.ObjectQuery("SELECT displayName FROM AntiVirusProduct")))
                    foreach (System.Management.ManagementObject o in s.Get())
                        using (o)
                        {
                            var n = (o["displayName"] as string ?? "").Trim();
                            if (n.Length >= 4 && n.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) < 0) list.Add(n.ToLowerInvariant());
                        }
            }
            catch { }
            return avNames = list;
        }

        static void Classify(StartupItem it)
        {
            var hay = (it.Name + " | " + it.Path + " | " + it.Publisher + " | " + it.ValueName).ToLowerInvariant();
            if (it.Path != null && it.Path.StartsWith(Paths.AppDir, StringComparison.OrdinalIgnoreCase)) hay += " | rage.exe";
            Rule rule = AvNames().Any(n => hay.Contains(n)) ? AvRule : Rules.FirstOrDefault(r => r.Re.IsMatch(hay));
            if (rule != null)
            {
                it.Recommendation = rule.Rec;
                it.Reason = rule.Reason;
                it.Group = rule.Group;
                it.Caution = rule.Caution;
            }
            // Serviços de terceiros que não reconhecemos: manter (podem ser de drivers)
            if (rule == null && it.Type == "service") { it.Recommendation = null; it.Reason = "Serviço de terceiros. Desative só se souber para que serve."; }
            it.Impact = rule?.Impact ?? ImpactBySize(it);
        }

        static string ImpactBySize(StartupItem it)
        {
            try
            {
                if (string.IsNullOrEmpty(it.Path) || !File.Exists(it.Path)) return null;
                var len = new FileInfo(it.Path).Length;
                // Apps Electron/CEF costumam ter executáveis grandes
                if (len >= 40L * 1024 * 1024) return "alto";
                if (len >= 4L * 1024 * 1024) return "médio";
                if (it.Type == "service") return "médio";
                return "baixo";
            }
            catch { return null; }
        }

        // ---------- Tempo de inicialização ----------
        public static object Boot()
        {
            if (!Program.IsAdmin) return null;
            try
            {
                var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true };
                var hist = new List<object>();
                double? last = null;
                using (var r = new EventLogReader(q))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        using (var e = r.ReadEvent())
                        {
                            if (e == null) break;
                            var m = Regex.Match(e.ToXml(), @"<Data Name=['""]BootTime['""]>(\d+)</Data>");
                            if (!m.Success) continue;
                            var sec = Math.Round(long.Parse(m.Groups[1].Value) / 1000.0, 1);
                            if (last == null) last = sec;
                            hist.Add(new { at = e.TimeCreated, sec });
                        }
                    }
                }
                if (last == null) return null;
                hist.Reverse();
                return new { lastSec = last, history = hist };
            }
            catch (Exception ex) { Log.Warn("Tempo de boot: " + ex.Message); return null; }
        }

        // ---------- Alterações ----------
        static readonly object CacheGate = new object();
        static List<StartupItem> cache;
        static DateTime cacheAt;

        static StartupItem Find(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new RpcException("Item não informado.");
            List<StartupItem> list;
            lock (CacheGate) list = cache != null && (DateTime.Now - cacheAt).TotalSeconds < 120 ? cache : null;
            var it = list?.FirstOrDefault(x => x.Id == id);
            if (it == null)
            {
                list = Collect(false);
                it = list.FirstOrDefault(x => x.Id == id);
            }
            if (it == null) throw new RpcException("Item de inicialização não encontrado. Atualize a lista.");
            return it;
        }

        public static StartupItem SetEnabled(string id, bool enabled, bool refresh = true, bool activity = true)
        {
            var it = Find(id);
            if (it.Enabled == enabled) return refresh ? Refresh(id) : it;
            if (it.Machine) ToolsUtil.RequireAdmin("Alterar este item de inicialização");
            switch (it.Type)
            {
                case "registry":
                case "folder":
                    WriteApproved(it.ApprovedKey, it.ValueName, enabled);
                    break;
                case "task":
                    SetTask(it.Key, enabled);
                    break;
                case "service":
                    SetService(it.ValueName, enabled);
                    break;
            }
            it.Enabled = enabled;
            Log.Info("Inicialização: " + it.Name + " → " + (enabled ? "ativado" : "desativado"));
            if (activity) Activity.Add("info", "rocket_launch", (enabled ? "Inicialização ativada: " : "Inicialização desativada: ") + it.Name, it.Publisher);
            return refresh ? Refresh(id) : it;
        }

        static StartupItem Refresh(string id)
        {
            var it = Collect(false).FirstOrDefault(x => x.Id == id);
            if (it != null) it.Icon = ToolsUtil.IconData(it.Path);
            return it;
        }

        static void WriteApproved(string key, string name, bool enabled)
        {
            var data = new byte[12];
            if (enabled) data[0] = 2;
            else
            {
                data[0] = 3;
                Array.Copy(BitConverter.GetBytes(DateTime.Now.ToFileTime()), 0, data, 4, 8);
            }
            try { Reg.Set(key, name, data, RegistryValueKind.Binary); }
            catch (UnauthorizedAccessException) { throw new RpcException("Sem permissão para alterar este item. Abra o RAGE! como administrador."); }
        }

        static void SetTask(string path, bool enabled)
        {
            dynamic svc = TaskSched.Connect();
            try
            {
                var i = path.LastIndexOf('\\');
                dynamic folder = svc.GetFolder(i <= 0 ? "\\" : path.Substring(0, i));
                dynamic task = folder.GetTask(path.Substring(i + 1));
                task.Enabled = enabled;
                TaskSched.Release(task); TaskSched.Release(folder);
            }
            catch (UnauthorizedAccessException) { throw new RpcException("Sem permissão para alterar esta tarefa. Abra o RAGE! como administrador."); }
            catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new RpcException("Sem permissão para alterar esta tarefa. Abra o RAGE! como administrador."); }
            finally { TaskSched.Release(svc); }
        }

        // ChangeServiceConfig: Automático ↔ Manual
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string machine, string db, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr scm, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr svc, uint type, uint start, uint error, string bin, string group, IntPtr tag, string deps, string user, string pwd, string display);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool ChangeServiceConfig2(IntPtr svc, uint level, ref int info);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr h);
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF, SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3, SERVICE_CHANGE_CONFIG = 0x0002, SERVICE_QUERY_CONFIG = 0x0001, SC_MANAGER_CONNECT = 0x0001;
        const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

        static void SetService(string name, bool enabled)
        {
            var states = Json.Load(SvcStateFile, new List<SvcState>());
            var prev = states.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool delayed = (Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Services\" + name, "DelayedAutostart") ?? 0) == 1;
            var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new RpcException("Não foi possível abrir o gerenciador de serviços: " + Native.LastError());
            try
            {
                var h = OpenService(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
                if (h == IntPtr.Zero) throw new RpcException("Não foi possível abrir o serviço: " + Native.LastError());
                try
                {
                    if (!ChangeServiceConfig(h, SERVICE_NO_CHANGE, enabled ? SERVICE_AUTO_START : SERVICE_DEMAND_START, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, null))
                        throw new RpcException("Não foi possível alterar o serviço: " + Native.LastError());
                    if (enabled && prev != null && prev.Delayed)
                    {
                        int d = 1;
                        ChangeServiceConfig2(h, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref d);
                    }
                }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
            states.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!enabled) states.Add(new SvcState { Name = name, Delayed = delayed, At = DateTime.Now });
            try { Json.Save(SvcStateFile, states); } catch (Exception ex) { Log.Warn("Salvar estado de serviços: " + ex.Message); }
        }

        public static object Remove(string id)
        {
            var it = Find(id);
            if (it.Type != "registry" && it.Type != "folder") throw new RpcException("Só itens do registro e da pasta Inicializar podem ser removidos. Desative tarefas e serviços.");
            if (it.Machine) ToolsUtil.RequireAdmin("Remover este item de inicialização");
            string backup;
            if (it.Type == "registry")
            {
                var sb = new StringBuilder();
                Reg.AppendExport(sb, it.Key, it.ValueName);
                if (Reg.Get(it.ApprovedKey, it.ValueName) != null) Reg.AppendExport(sb, it.ApprovedKey, it.ValueName);
                backup = Reg.SaveBackup("inicializacao", sb);
                try
                {
                    Reg.DeleteValue(it.Key, it.ValueName);
                    try { Reg.DeleteValue(it.ApprovedKey, it.ValueName); } catch { }
                }
                catch (UnauthorizedAccessException) { throw new RpcException("Sem permissão para remover. Abra o RAGE! como administrador."); }
            }
            else
            {
                var src = System.IO.Path.Combine(it.Key, it.ValueName);
                var dir = Paths.Ensure(System.IO.Path.Combine(Paths.Backups, "atalhos", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_inicializacao"));
                var dest = System.IO.Path.Combine(dir, MirrorPath(src));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
                File.Move(src, dest);
                Rescue.WriteManifest(dir, new List<Rescue.MovedFile> { new Rescue.MovedFile { Original = src, Backup = dest } });
                try { Reg.DeleteValue(it.ApprovedKey, it.ValueName); } catch { }
                backup = dir;
            }
            lock (CacheGate) cache = null;
            Activity.Add("info", "rocket_launch", "Removido da inicialização: " + it.Name, "Backup em " + System.IO.Path.GetFileName(backup));
            return new { ok = true, backup };
        }

        /// <summary>"C:\Users\x\a.lnk" → "C\Users\x\a.lnk" (estrutura espelhada dentro do backup).</summary>
        public static string MirrorPath(string full)
        {
            var root = System.IO.Path.GetPathRoot(full) ?? "";
            var drive = root.TrimEnd('\\', ':');
            return System.IO.Path.Combine(drive.Length == 0 ? "_" : drive, full.Substring(root.Length));
        }

        // ---------- RPC ----------
        [Rpc("startup.list")]
        public static object ListRpc() => new { items = Collect(true), boot = Boot() };

        [Rpc("startup.set")]
        public static object SetRpc(string id, bool enabled) => SetEnabled(id, enabled);

        [Rpc("startup.remove")]
        public static object RemoveRpc(string id) => Remove(id);

        [Rpc("startup.reveal")]
        public static object RevealRpc(string id)
        {
            var it = Find(id);
            if (!string.IsNullOrEmpty(it.Path) && File.Exists(it.Path)) { ToolsUtil.Reveal(it.Path); return true; }
            switch (it.Type)
            {
                case "registry": Rage.App.AppRpc.OpenRegedit(it.Key); break;
                case "folder": ToolsUtil.Reveal(System.IO.Path.Combine(it.Key, it.ValueName)); break;
                case "task": Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true }); break;
                case "service": Process.Start(new ProcessStartInfo("services.msc") { UseShellExecute = true }); break;
            }
            return true;
        }
    }

    /// <summary>Verificação inteligente: programas que não precisam iniciar com o Windows.</summary>
    public sealed class StartupScanner : ICareScanner
    {
        public string Category => "startup";
        public string Title => "Inicialização";
        public string Description => "Programas que abrem sozinhos com o Windows e deixam a inicialização mais lenta.";
        public string Icon => "rocket_launch";
        public int Order => 50;
        public bool DefaultOn => true;

        public List<CareItem> Scan(RpcContext ctx)
        {
            var items = Startup.Collect(false);
            var res = new List<CareItem>();
            foreach (var it in items.Where(x => x.Enabled && x.Recommendation == "desativar"))
            {
                var needAdmin = it.Machine && !Program.IsAdmin;
                res.Add(new CareItem
                {
                    Id = "startup." + it.Id,
                    Category = "startup",
                    Group = it.Group ?? "Outros apps",
                    Title = it.Name,
                    Detail = (it.Publisher != null ? it.Publisher + " · " : "") + (it.Impact != null ? "impacto " + it.Impact : "impacto desconhecido"),
                    Selected = !it.Caution && !needAdmin,
                    Risk = it.Caution ? "caution" : "safe",
                    Note = needAdmin ? "Precisa do RAGE! aberto como administrador." : it.Reason,
                    Current = "Ativado",
                    Recommended = "Desativado",
                    Status = "warn",
                    Fixable = true,
                    Paths = string.IsNullOrEmpty(it.Path) ? null : new List<string> { it.Path },
                    Tag = it.Id,
                });
            }
            return res;
        }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var res = new List<FixResult>();
            foreach (var ci in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var id = ci.Tag as string ?? (ci.Id.StartsWith("startup.") ? ci.Id.Substring(8) : ci.Id);
                try
                {
                    Startup.SetEnabled(id, false, false, false);
                    res.Add(new FixResult { Id = ci.Id, Ok = true, Message = "Desativado na inicialização" });
                }
                catch (Exception ex) { res.Add(new FixResult { Id = ci.Id, Ok = false, Message = ex.Message }); }
            }
            return res;
        }
    }
}
