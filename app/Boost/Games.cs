using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Rage.Core;

namespace Rage.Boost
{
    public sealed class Game
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }      // steam | epic | xbox | gog | ubisoft | ea | riot | battlenet | manual | auto
        public string Folder { get; set; }
        public string Exe { get; set; }
        public string Launch { get; set; }
        public bool AutoBoost { get; set; } = true;
        public bool HighPerfGpu { get; set; }
        public DateTime? LastPlayed { get; set; }
        public double PlaySeconds { get; set; }
        public string Icon { get; set; }        // data:image/png;base64
        public bool Hidden { get; set; }
    }

    /// <summary>Biblioteca de jogos: Steam, Epic, Xbox, GOG, Ubisoft, EA, Riot e jogos adicionados manualmente.</summary>
    public static class Games
    {
        static readonly object Gate = new object();
        static List<Game> games;
        static string FilePath => Paths.File("games.json");

        public static List<Game> All { get { lock (Gate) { if (games == null) Load(); return games.ToList(); } } }

        static void Load() { games = Json.Load(FilePath, new List<Game>()); }
        public static void Save() { lock (Gate) { try { Json.Save(FilePath, games); } catch (Exception ex) { Log.Error("Salvar jogos", ex); } } }

        public static readonly string[] GameDirHints =
        {
            @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\Ubisoft Game Launcher\games\",
            @"\EA Games\", @"\Origin Games\", @"\Riot Games\", @"\Rockstar Games\", @"\Battle.net\", @"\Blizzard\",
            @"\Games\", @"\Jogos\", @"\Amazon Games\Library\", @"\itch\apps\",
            @"\.minecraft\", @"\.tlauncher\", @"\Minecraft Launcher\runtime\", @"\Microsoft.4297127D64EC6_", // Minecraft Java roda pelo javaw.exe do launcher
        };

        static readonly Regex SkipExe = new Regex(@"(unins|uninst|setup|crash|report|redist|vc_?redist|dxsetup|dotnet|directx|launcher|updater|helper|easyanticheat|eac_|battleye|be_service|cefprocess|webhelper|ue4prereq|prereq|install|config|benchmark|server)", RegexOptions.IgnoreCase);

        public static Game FindByPath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            lock (Gate)
            {
                if (games == null) Load();
                return games.FirstOrDefault(g => !string.IsNullOrEmpty(g.Exe) && string.Equals(g.Exe, exePath, StringComparison.OrdinalIgnoreCase))
                    ?? games.Where(g => !string.IsNullOrEmpty(g.Folder) && exePath.StartsWith(g.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(g => g.Folder.Length).FirstOrDefault();
            }
        }

        public static bool LooksLikeGame(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            if (SkipExe.IsMatch(Path.GetFileName(exePath))) return false;
            return GameDirHints.Any(h => exePath.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0) && exePath.IndexOf(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // ---------- Varredura ----------
        public static List<Game> Scan()
        {
            var found = new List<Game>();
            void Try(Action a, string what) { try { a(); } catch (Exception ex) { Log.Warn("Jogos " + what + ": " + ex.Message); } }
            Try(() => found.AddRange(ScanSteam()), "Steam");
            Try(() => found.AddRange(ScanEpic()), "Epic");
            Try(() => found.AddRange(ScanXbox()), "Xbox");
            Try(() => found.AddRange(ScanGog()), "GOG");
            Try(() => found.AddRange(ScanUbisoft()), "Ubisoft");
            Try(() => found.AddRange(ScanUninstallKeys()), "EA/Riot/Battle.net");

            lock (Gate)
            {
                if (games == null) Load();
                foreach (var f in found)
                {
                    if (string.IsNullOrEmpty(f.Folder) || !Directory.Exists(f.Folder)) continue;
                    var ex = games.FirstOrDefault(g => g.Id == f.Id) ?? games.FirstOrDefault(g => string.Equals(g.Folder?.TrimEnd('\\'), f.Folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                    if (ex != null)
                    {
                        ex.Name = f.Name; ex.Launch = f.Launch ?? ex.Launch; ex.Folder = f.Folder;
                        if (string.IsNullOrEmpty(ex.Exe) || !File.Exists(ex.Exe)) ex.Exe = f.Exe ?? GuessExe(f.Folder);
                        if (ex.Icon == null) ex.Icon = IconData(ex.Exe);
                        continue;
                    }
                    f.Exe = f.Exe ?? GuessExe(f.Folder);
                    f.Icon = IconData(f.Exe);
                    games.Add(f);
                }
                games = games.Where(g => g.Source == "manual" || string.IsNullOrEmpty(g.Folder) || Directory.Exists(g.Folder)).OrderBy(g => g.Name).ToList();
                Save();
                return games.ToList();
            }
        }

        static IEnumerable<Game> ScanSteam()
        {
            var steam = Reg.GetStr(@"HKCU\Software\Valve\Steam", "SteamPath") ?? Reg.GetStr(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            if (steam == null) yield break;
            steam = steam.Replace('/', '\\');
            var libs = new List<string> { steam };
            var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    var t = File.ReadAllText(acf);
                    var id = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    var name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    var dir = Regex.Match(t, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (id == "228980" || name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton")) continue;
                    yield return new Game { Id = "steam-" + id, Name = name, Source = "steam", Folder = Path.Combine(apps, "common", dir), Launch = "steam://rungameid/" + id };
                }
            }
        }

        static IEnumerable<Game> ScanEpic()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                JObject j; try { j = JObject.Parse(File.ReadAllText(f)); } catch { continue; }
                var cats = j["AppCategories"]?.ToObject<string[]>() ?? new string[0];
                if (cats.Length > 0 && !cats.Contains("games")) continue;
                var loc = (string)j["InstallLocation"]; var exe = (string)j["LaunchExecutable"]; var app = (string)j["AppName"];
                yield return new Game
                {
                    Id = "epic-" + app, Name = (string)j["DisplayName"], Source = "epic", Folder = loc,
                    Exe = loc != null && exe != null ? Path.Combine(loc, exe) : null,
                    Launch = "com.epicgames.launcher://apps/" + (string)j["CatalogNamespace"] + "%3A" + (string)j["CatalogItemId"] + "%3A" + app + "?action=launch&silent=true",
                };
            }
        }

        static IEnumerable<Game> ScanXbox()
        {
            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed))
            {
                var root = Path.Combine(d.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;
                foreach (var g in Directory.GetDirectories(root))
                {
                    if (Path.GetFileName(g).Equals("GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                    var content = Path.Combine(g, "Content");
                    yield return new Game { Id = "xbox-" + Path.GetFileName(g).ToLowerInvariant(), Name = Path.GetFileName(g), Source = "xbox", Folder = Directory.Exists(content) ? content : g };
                }
            }
        }

        static IEnumerable<Game> ScanGog()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
            {
                if (k == null) yield break;
                foreach (var id in k.GetSubKeyNames())
                    using (var g = k.OpenSubKey(id))
                    {
                        var path = g?.GetValue("path") as string; var exe = g?.GetValue("exe") as string;
                        if (path == null) continue;
                        yield return new Game { Id = "gog-" + id, Name = g.GetValue("gameName") as string ?? id, Source = "gog", Folder = path, Exe = exe, Launch = exe };
                    }
            }
        }

        static IEnumerable<Game> ScanUbisoft()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs"))
            {
                if (k == null) yield break;
                foreach (var id in k.GetSubKeyNames())
                    using (var g = k.OpenSubKey(id))
                    {
                        var dir = (g?.GetValue("InstallDir") as string)?.Replace('/', '\\');
                        if (dir == null) continue;
                        yield return new Game { Id = "ubisoft-" + id, Name = new DirectoryInfo(dir.TrimEnd('\\')).Name, Source = "ubisoft", Folder = dir, Launch = "uplay://launch/" + id + "/0" };
                    }
            }
        }

        static IEnumerable<Game> ScanUninstallKeys()
        {
            var roots = new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var r in roots)
                using (var k = Registry.LocalMachine.OpenSubKey(r))
                {
                    if (k == null) continue;
                    foreach (var s in k.GetSubKeyNames())
                        using (var e = k.OpenSubKey(s))
                        {
                            var pub = e?.GetValue("Publisher") as string ?? "";
                            var name = e?.GetValue("DisplayName") as string;
                            var loc = e?.GetValue("InstallLocation") as string;
                            if (name == null || string.IsNullOrEmpty(loc)) continue;
                            string src = null;
                            if (pub.IndexOf("Riot Games", StringComparison.OrdinalIgnoreCase) >= 0 && !name.StartsWith("Riot Client")) src = "riot";
                            else if ((pub.IndexOf("Electronic Arts", StringComparison.OrdinalIgnoreCase) >= 0) && !name.StartsWith("EA app") && !name.StartsWith("Origin")) src = "ea";
                            else if (pub.IndexOf("Blizzard", StringComparison.OrdinalIgnoreCase) >= 0 && !name.StartsWith("Battle.net")) src = "battlenet";
                            if (src == null) continue;
                            yield return new Game { Id = src + "-" + s.ToLowerInvariant(), Name = name, Source = src, Folder = loc.Trim('"') };
                        }
                }
        }

        public static string GuessExe(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
                var files = new List<FileInfo>();
                void Walk(DirectoryInfo d, int depth)
                {
                    if (depth > 3) return;
                    try { files.AddRange(d.GetFiles("*.exe").Where(f => !SkipExe.IsMatch(f.Name))); } catch { }
                    try { foreach (var s in d.GetDirectories()) if (!s.Name.StartsWith("_") && !s.Name.Equals("redist", StringComparison.OrdinalIgnoreCase)) Walk(s, depth + 1); } catch { }
                }
                Walk(new DirectoryInfo(folder), 0);
                var shipping = files.FirstOrDefault(f => f.Name.IndexOf("Shipping", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("-Win64-", StringComparison.OrdinalIgnoreCase) >= 0);
                return (shipping ?? files.OrderByDescending(f => f.Length).FirstOrDefault())?.FullName;
            }
            catch { return null; }
        }

        public static string IconData(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                using (var ic = Icon.ExtractAssociatedIcon(exe))
                using (var bmp = ic.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            catch { return null; }
        }

        // ---------- Preferência de GPU (Configurações > Sistema > Tela > Gráficos) ----------
        const string GpuPrefKey = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
        public static void SetGpuPreference(string exe, bool highPerf)
        {
            if (string.IsNullOrEmpty(exe)) return;
            if (highPerf) Reg.Set(GpuPrefKey, exe, "GpuPreference=2;", RegistryValueKind.String);
            else Reg.DeleteValue(GpuPrefKey, exe);
        }
        public static bool GetGpuPreference(string exe) => !string.IsNullOrEmpty(exe) && (Reg.GetStr(GpuPrefKey, exe) ?? "").Contains("GpuPreference=2");

        public static void AddPlayTime(Game g, double seconds)
        {
            lock (Gate) { g.PlaySeconds += seconds; g.LastPlayed = DateTime.Now; }
        }

        // ---------- RPC ----------
        [Rpc("games.list")]
        public static object List()
        {
            var list = All;
            var running = GameWatcher.RunningGames();
            return list.Where(g => !g.Hidden).Select(g => new
            {
                g.Id, g.Name, g.Source, g.Folder, g.Exe, g.Launch, g.AutoBoost, g.LastPlayed, g.PlaySeconds, g.Icon,
                highPerfGpu = GetGpuPreference(g.Exe),
                running = running.Any(r => r.game.Id == g.Id),
                exists = string.IsNullOrEmpty(g.Folder) || Directory.Exists(g.Folder),
            }).ToList();
        }

        [Rpc("games.scan")]
        public static object ScanRpc()
        {
            var before = All.Count;
            Scan();
            var after = All.Count;
            if (after > before) Activity.Add("info", "sports_esports", "Biblioteca de jogos atualizada", (after - before) + " novos jogos encontrados");
            return List();
        }

        [Rpc("games.add")]
        public static object Add(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new RpcException("Executável não encontrado.");
            lock (Gate)
            {
                if (games == null) Load();
                if (games.Any(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase))) throw new RpcException("Este jogo já está na biblioteca.");
                string name;
                try { name = FileVersionInfo.GetVersionInfo(exe).ProductName; } catch { name = null; }
                if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exe);
                games.Add(new Game { Id = "manual-" + Guid.NewGuid().ToString("N").Substring(0, 10), Name = name.Trim(), Source = "manual", Exe = exe, Folder = Path.GetDirectoryName(exe), Launch = exe, Icon = IconData(exe) });
            }
            Save();
            return List();
        }

        [Rpc("games.update")]
        public static object Update(string id, bool? autoBoost, bool? highPerfGpu, string name, bool? hidden)
        {
            lock (Gate)
            {
                if (games == null) Load();
                var g = games.FirstOrDefault(x => x.Id == id) ?? throw new RpcException("Jogo não encontrado.");
                if (autoBoost.HasValue) g.AutoBoost = autoBoost.Value;
                if (!string.IsNullOrWhiteSpace(name)) g.Name = name.Trim();
                if (hidden.HasValue) g.Hidden = hidden.Value;
                if (highPerfGpu.HasValue)
                {
                    if (string.IsNullOrEmpty(g.Exe)) g.Exe = GuessExe(g.Folder);
                    if (string.IsNullOrEmpty(g.Exe)) throw new RpcException("Abra o jogo uma vez para o RAGE! descobrir o executável.");
                    SetGpuPreference(g.Exe, highPerfGpu.Value);
                    g.HighPerfGpu = highPerfGpu.Value;
                }
            }
            Save();
            return List();
        }

        [Rpc("games.remove")]
        public static object Remove(string id)
        {
            lock (Gate)
            {
                if (games == null) Load();
                var g = games.FirstOrDefault(x => x.Id == id);
                if (g != null) { if (g.Source == "manual") games.Remove(g); else g.Hidden = true; }
            }
            Save();
            return List();
        }

        [Rpc("games.launch")]
        public static object Launch(string id, bool boost = false)
        {
            var g = All.FirstOrDefault(x => x.Id == id) ?? throw new RpcException("Jogo não encontrado.");
            var target = g.Launch ?? g.Exe;
            if (string.IsNullOrEmpty(target)) throw new RpcException("Não sei como abrir este jogo. Abra pelo launcher.");
            if (boost && !BoostService.Active) BoostService.Start("game", null, "auto: " + g.Name);
            if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) });
            else Proc.Open(target);
            return true;
        }
    }

    /// <summary>Observa o primeiro plano: liga o Turbo quando um jogo abre e desliga quando ele fecha.</summary>
    public static class GameWatcher
    {
        static Thread thread;
        static volatile bool running;
        static readonly Dictionary<int, (Game game, string exe, DateTime start)> active = new Dictionary<int, (Game, string, DateTime)>();
        static readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();
        static int autoPid;
        public static int ForegroundGamePid { get; private set; }

        public static void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "RAGE-Games" };
            thread.Start();
        }
        public static void Stop() { running = false; }

        public static List<(Game game, string exe, DateTime start)> RunningGames() { lock (active) return active.Values.ToList(); }
        public static int? RunningGamePid() { lock (active) return active.Keys.Cast<int?>().FirstOrDefault(); }

        static string PathOf(int pid)
        {
            lock (pathCache)
            {
                if (pathCache.TryGetValue(pid, out var p)) return p;
                try { using (var pr = Process.GetProcessById(pid)) p = pr.MainModule?.FileName; } catch { p = null; }
                if (pathCache.Count > 500) pathCache.Clear();
                pathCache[pid] = p;
                return p;
            }
        }

        static void Loop()
        {
            try { if (Games.All.Count == 0) Games.Scan(); } catch { }
            var last = DateTime.Now;
            while (running)
            {
                try { Tick((DateTime.Now - last).TotalSeconds); } catch (Exception ex) { Log.Warn("GameWatcher: " + ex.Message); }
                last = DateTime.Now;
                Thread.Sleep(2000);
            }
        }

        static void Tick(double dt)
        {
            var st = Settings.Current;
            var fgWin = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fgWin, out var fgPidU);
            var fgPid = (int)fgPidU;
            var fgPath = fgPid > 4 ? PathOf(fgPid) : null;

            // Novo jogo em primeiro plano?
            if (fgPath != null && fgPid != System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                bool known;
                lock (active) known = active.ContainsKey(fgPid);
                if (!known)
                {
                    var g = Games.FindByPath(fgPath);
                    if (g == null && st.AutoDetectGames && Games.LooksLikeGame(fgPath) && Native.IsFullscreen(fgWin))
                    {
                        g = new Game { Id = "auto-" + Path.GetFileNameWithoutExtension(fgPath).ToLowerInvariant(), Name = SafeProduct(fgPath), Source = "auto", Exe = fgPath, Folder = Path.GetDirectoryName(fgPath), Icon = Games.IconData(fgPath) };
                    }
                    if (g != null && !g.Hidden)
                    {
                        if (string.IsNullOrEmpty(g.Exe) || !File.Exists(g.Exe)) { g.Exe = fgPath; Games.Save(); }
                        lock (active) active[fgPid] = (g, fgPath, DateTime.Now);
                        Log.Info("Jogo detectado: " + g.Name + " (" + fgPid + ")");
                        Activity.Add("info", "sports_esports", "Jogo iniciado: " + g.Name, null);
                        if (st.AutoBoost && g.AutoBoost)
                        {
                            if (!BoostService.Active) { BoostService.Start("game", fgPid, "auto: " + g.Name); autoPid = fgPid; }
                            else BoostService.AttachGame(fgPid);
                        }
                        Rage.Monitor.Overlay.ShowAuto(true); // o overlay decide pelas configurações, que podem mudar com o jogo já aberto
                        Bridge.Emit("game", new { started = true, name = g.Name, pid = fgPid });
                    }
                }
            }

            // Tempo de jogo e jogos encerrados
            List<int> ended = new List<int>();
            lock (active)
            {
                foreach (var kv in active)
                {
                    bool alive;
                    try { using (var p = Process.GetProcessById(kv.Key)) alive = !p.HasExited; } catch { alive = false; }
                    if (!alive) ended.Add(kv.Key);
                    else if (kv.Value.game.Source != "auto") Games.AddPlayTime(kv.Value.game, dt);
                }
            }
            ForegroundGamePid = fgPid > 0 && IsTracked(fgPid) ? fgPid : 0;
            foreach (var pid in ended)
            {
                (Game game, string exe, DateTime start) info;
                lock (active) { info = active[pid]; active.Remove(pid); }
                lock (pathCache) pathCache.Remove(pid);
                var mins = (DateTime.Now - info.start).TotalMinutes;
                Activity.Add("info", "sports_esports", "Jogo encerrado: " + info.game.Name, "Sessão de " + (mins >= 60 ? ((int)(mins / 60)) + "h " + ((int)mins % 60) + "min" : ((int)Math.Max(1, mins)) + " min"));
                if (info.game.Source != "auto") Games.Save();
                if (pid == autoPid && BoostService.Active && BoostService.AutoStarted) { BoostService.Stop("auto: jogo fechado"); autoPid = 0; }
                Bridge.Emit("game", new { started = false, name = info.game.Name, pid });
                if (RunningGames().Count == 0) Rage.Monitor.Overlay.ShowAuto(false);
            }
        }

        public static bool IsTracked(int pid) { lock (active) return active.ContainsKey(pid); }

        static string SafeProduct(string exe)
        {
            try { var n = FileVersionInfo.GetVersionInfo(exe).ProductName; if (!string.IsNullOrWhiteSpace(n)) return n.Trim(); } catch { }
            return Path.GetFileNameWithoutExtension(exe);
        }
    }
}
