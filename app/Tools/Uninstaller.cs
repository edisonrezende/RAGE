using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Programas instalados (Win32 + Microsoft Store), desinstalação e restos.</summary>
    public static class Uninstaller
    {
        public sealed class App
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Publisher { get; set; }
            public string Version { get; set; }
            public double? Size { get; set; }
            public DateTime? InstallDate { get; set; }
            public string Icon { get; set; }
            public string Location { get; set; }
            public string Source { get; set; }      // win32 | store
            public bool CanUninstall { get; set; }
            [Newtonsoft.Json.JsonIgnore] public string KeyPath;      // HKLM\...\Uninstall\X (caminho absoluto, view 64)
            [Newtonsoft.Json.JsonIgnore] public string Uninstall;
            [Newtonsoft.Json.JsonIgnore] public bool Msi;
            [Newtonsoft.Json.JsonIgnore] public string ProductCode;
            [Newtonsoft.Json.JsonIgnore] public string PackageFullName;
        }

        sealed class Root { public string Tag; public string Path; }

        static readonly Root[] Roots =
        {
            new Root { Tag = "HKLM", Path = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" },
            new Root { Tag = "HKLM32", Path = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" },
            new Root { Tag = "HKCU", Path = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall" },
            new Root { Tag = "HKCU32", Path = @"HKCU\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" },
        };

        static readonly ConcurrentDictionary<string, long?> sizeCache = new ConcurrentDictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        static readonly object Gate = new object();
        static List<App> last;

        // ---------- Win32 ----------
        static IEnumerable<App> Win32()
        {
            var res = new List<App>();
            foreach (var root in Roots)
            {
                using (var k = Reg.Open(root.Path))
                {
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (var e = k.OpenSubKey(sub))
                            {
                                if (e == null) continue;
                                var name = ToolsUtil.Str(e.GetValue("DisplayName"));
                                if (name == null) continue;
                                if (Convert.ToInt32(e.GetValue("SystemComponent") ?? 0) == 1) continue;
                                if (e.GetValue("ParentKeyName") != null) continue;
                                var rel = (e.GetValue("ReleaseType") as string ?? "");
                                if (Regex.IsMatch(rel, "update|hotfix|service pack", RegexOptions.IgnoreCase)) continue;
                                if (Regex.IsMatch(name, @"\(KB\d{6,}\)|^Security Update|^Hotfix|^Update for", RegexOptions.IgnoreCase)) continue;
                                var uninst = ToolsUtil.Str(e.GetValue("UninstallString"));
                                var isMsi = Convert.ToInt32(e.GetValue("WindowsInstaller") ?? 0) == 1;
                                var productCode = Regex.IsMatch(sub, @"^\{[0-9A-Fa-f-]{36}\}$") ? sub : null;
                                if (uninst == null && !(isMsi && productCode != null)) continue;
                                var noRemove = Convert.ToInt32(e.GetValue("NoRemove") ?? 0) == 1;
                                var loc = ToolsUtil.Str(e.GetValue("InstallLocation"))?.Trim('"');
                                double? size = null;
                                var est = e.GetValue("EstimatedSize");
                                if (est != null) { try { var kb = Convert.ToInt64(est is int i ? (uint)i : est); if (kb > 0) size = kb * 1024.0; } catch { } }
                                DateTime? date = null;
                                var ds = ToolsUtil.Str(e.GetValue("InstallDate"));
                                if (ds != null && ds.Length >= 8 && DateTime.TryParseExact(ds.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) date = d;
                                if (date == null) date = ToolsUtil.KeyLastWrite(e);
                                var icon = ToolsUtil.Str(e.GetValue("DisplayIcon"));
                                res.Add(new App
                                {
                                    Id = "win32:" + root.Tag + ":" + sub,
                                    Name = name,
                                    Publisher = ToolsUtil.Str(e.GetValue("Publisher")),
                                    Version = ToolsUtil.Str(e.GetValue("DisplayVersion")),
                                    Size = size,
                                    InstallDate = date,
                                    Location = loc,
                                    Source = "win32",
                                    CanUninstall = !noRemove,
                                    KeyPath = root.Path + "\\" + sub,
                                    Uninstall = uninst,
                                    Msi = isMsi || (uninst ?? "").IndexOf("msiexec", StringComparison.OrdinalIgnoreCase) >= 0,
                                    ProductCode = productCode,
                                    Icon = icon, // resolvido depois
                                });
                            }
                        }
                        catch { }
                    }
                }
            }
            // Mesma entrada em mais de uma chave (ex.: HKLM e HKCU): mantém a primeira
            return res.GroupBy(a => (a.Name + "|" + a.Version).ToLowerInvariant()).Select(g => g.First());
        }

        static string IconFor(App a)
        {
            var spec = a.Icon;
            if (!string.IsNullOrEmpty(spec))
            {
                var t = spec.Trim();
                var ic = ToolsUtil.IconData(t.StartsWith("\"") ? t.Replace("\"", "") : t);
                if (ic != null) return ic;
            }
            var exe = a.Uninstall != null && !a.Msi ? ToolsUtil.ExeFromCommand(a.Uninstall) : null;
            if (!string.IsNullOrEmpty(a.Location) && Directory.Exists(a.Location))
            {
                try
                {
                    var main = Directory.GetFiles(a.Location, "*.exe").Where(f => !Regex.IsMatch(Path.GetFileName(f), "unins|uninst|setup|update|crash|helper", RegexOptions.IgnoreCase))
                        .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                    if (main != null) return ToolsUtil.IconData(main);
                }
                catch { }
            }
            return exe != null ? ToolsUtil.IconData(exe) : null;
        }

        // ---------- Microsoft Store ----------
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHLoadIndirectString(string src, System.Text.StringBuilder outBuf, int cch, IntPtr reserved);

        static string Indirect(string fullName, string pkgName, string res)
        {
            if (string.IsNullOrEmpty(res) || !res.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return res;
            var key = res.Substring("ms-resource:".Length);
            var candidates = new List<string>();
            if (key.StartsWith("//")) candidates.Add("@{" + fullName + "? " + res + "}");
            else if (key.StartsWith("/")) candidates.Add("@{" + fullName + "? ms-resource://" + pkgName + key + "}");
            else
            {
                candidates.Add("@{" + fullName + "? ms-resource://" + pkgName + "/resources/" + key + "}");
                candidates.Add("@{" + fullName + "? ms-resource://" + pkgName + "/" + key + "}");
                candidates.Add("@{" + fullName + "? " + res + "}");
            }
            foreach (var c in candidates)
            {
                var sb = new System.Text.StringBuilder(512);
                if (SHLoadIndirectString(c, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
            }
            return null;
        }

        static IEnumerable<App> Store()
        {
            var res = new List<App>();
            JToken arr;
            try
            {
                arr = Proc.PsJson("Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' } | " +
                                  "Select-Object Name, PackageFullName, Version, InstallLocation, @{n='Sig';e={[string]$_.SignatureKind}}", 60000);
            }
            catch (Exception ex) { Log.Warn("Apps da Store: " + ex.Message); return res; }
            foreach (var p in arr.Children<JObject>())
            {
                try
                {
                    var full = (string)p["PackageFullName"]; var pkg = (string)p["Name"]; var loc = (string)p["InstallLocation"];
                    if (full == null || loc == null) continue;
                    string display = null, pubName = null, logo = null;
                    var manifest = Path.Combine(loc, "AppxManifest.xml");
                    if (File.Exists(manifest))
                    {
                        var doc = new XmlDocument();
                        doc.Load(manifest);
                        var ns = new XmlNamespaceManager(doc.NameTable);
                        ns.AddNamespace("m", doc.DocumentElement.NamespaceURI);
                        display = doc.SelectSingleNode("/m:Package/m:Properties/m:DisplayName", ns)?.InnerText;
                        pubName = doc.SelectSingleNode("/m:Package/m:Properties/m:PublisherDisplayName", ns)?.InnerText;
                        logo = doc.SelectSingleNode("/m:Package/m:Properties/m:Logo", ns)?.InnerText;
                        // Pacotes sem aplicativo visível (extensões, componentes) ficam de fora
                        var apps = doc.SelectNodes("/m:Package/m:Applications/m:Application", ns);
                        if (apps == null || apps.Count == 0) continue;
                        var visible = false;
                        foreach (XmlNode a in apps)
                        {
                            var ve = a.ChildNodes.Cast<XmlNode>().FirstOrDefault(n => n.LocalName == "VisualElements");
                            var hidden = ve?.Attributes?["AppListEntry"]?.Value;
                            if (!string.Equals(hidden, "none", StringComparison.OrdinalIgnoreCase)) visible = true;
                        }
                        if (!visible) continue;
                    }
                    display = Indirect(full, pkg, display) ?? pkg;
                    pubName = Indirect(full, pkg, pubName);
                    if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) display = pkg;
                    DateTime? date = null;
                    try { date = Directory.GetCreationTime(loc); } catch { }
                    res.Add(new App
                    {
                        Id = "store:" + full,
                        Name = display,
                        Publisher = pubName,
                        Version = (string)p["Version"],
                        Location = loc,
                        InstallDate = date,
                        Source = "store",
                        CanUninstall = true,
                        PackageFullName = full,
                        Icon = StoreLogo(loc, logo),
                    });
                }
                catch { }
            }
            return res;
        }

        static string StoreLogo(string loc, string logo)
        {
            try
            {
                if (string.IsNullOrEmpty(logo)) return null;
                var rel = logo.Replace('/', '\\');
                var dir = Path.Combine(loc, Path.GetDirectoryName(rel) ?? "");
                var bn = Path.GetFileNameWithoutExtension(rel);
                var ext = Path.GetExtension(rel);
                var exact = Path.Combine(loc, rel);
                if (File.Exists(exact)) return ToolsUtil.ImageFileData(exact);
                if (!Directory.Exists(dir)) return null;
                var files = Directory.GetFiles(dir, bn + ".*" + ext);
                var pick = files.FirstOrDefault(f => f.IndexOf("scale-100", StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? files.FirstOrDefault(f => f.IndexOf("targetsize-32", StringComparison.OrdinalIgnoreCase) >= 0 && f.IndexOf("altform", StringComparison.OrdinalIgnoreCase) < 0)
                        ?? files.OrderBy(f => new FileInfo(f).Length).FirstOrDefault();
                return pick != null ? ToolsUtil.ImageFileData(pick) : null;
            }
            catch { return null; }
        }

        // ---------- Lista ----------
        public static List<App> Collect(bool icons, CancellationToken ct)
        {
            var store = System.Threading.Tasks.Task.Run(() => Store().ToList());
            var win = Win32().ToList();
            foreach (var a in win) a.Icon = icons ? IconFor(a) : null;
            // Tamanho estimado pela pasta quando o instalador não informa (limitado a ~4 s no total)
            var sw = Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.ForEach(win.Where(a => a.Size == null && !string.IsNullOrEmpty(a.Location)), new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, a =>
            {
                if (!IsAppFolder(a.Location)) return;
                if (sizeCache.TryGetValue(a.Location, out var c)) { if (c.HasValue) a.Size = c; return; }
                if (sw.ElapsedMilliseconds > 4000) return;
                var s = ToolsUtil.FolderSize(a.Location, sw, 4000);
                if (s.HasValue) { sizeCache[a.Location] = s; a.Size = s; }
            });
            List<App> st;
            try { st = store.Result; } catch { st = new List<App>(); }
            foreach (var a in st)
            {
                if (!icons) a.Icon = null;
                if (sizeCache.TryGetValue(a.Location, out var c)) { a.Size = c; continue; }
                if (sw.ElapsedMilliseconds > 7000) continue;
                var s = ToolsUtil.FolderSize(a.Location, sw, 7000);
                if (s.HasValue && s.Value > 0) { sizeCache[a.Location] = s; a.Size = s; } // 0 = pasta sem acesso
            }
            var all = win.Concat(st).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            lock (Gate) last = all;
            return all;
        }

        /// <summary>Pasta própria do app (não a raiz de Program Files, Windows etc.).</summary>
        static bool IsAppFolder(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                var full = Path.GetFullPath(dir).TrimEnd('\\');
                if (full.Length <= 3) return false;
                if (ToolsUtil.IsUnder(full, ToolsUtil.WinDir)) return false;
                return SafePaths.Check(full) == null || full.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static App Find(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new RpcException("Programa não informado.");
            App a;
            lock (Gate) a = last?.FirstOrDefault(x => x.Id == id);
            if (a == null) a = Collect(false, CancellationToken.None).FirstOrDefault(x => x.Id == id);
            return a ?? throw new RpcException("Programa não encontrado. Atualize a lista.");
        }

        // ---------- Desinstalar ----------
        public static object Uninstall(string id, RpcContext ctx)
        {
            var a = Find(id);
            if (!a.CanUninstall) throw new RpcException("Este programa não permite desinstalação.");
            ctx.Progress(new { line = "Desinstalando " + a.Name + "..." });
            bool ok; string message = null;
            if (a.Source == "store")
            {
                var r = Proc.Ps("Remove-AppxPackage -Package '" + a.PackageFullName.Replace("'", "''") + "'", 600000, null, ctx.Token);
                ok = r.Ok;
                message = ok ? "Aplicativo removido." : Proc.CleanPsError(r.Error);
            }
            else
            {
                string file, args;
                if (a.Msi && a.ProductCode != null) { file = Proc.SysExe("msiexec.exe"); args = "/x " + a.ProductCode; }
                else
                {
                    var cmd = ToolsUtil.Expand(a.Uninstall);
                    ToolsUtil.SplitCommand(cmd, out file, out args);
                    if (file != null && (file.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("msiexec", StringComparison.OrdinalIgnoreCase)))
                    {
                        file = Proc.SysExe("msiexec.exe");
                        args = Regex.Replace(args ?? "", @"/I(\{)", "/X$1", RegexOptions.IgnoreCase); // alguns registram /I (reparar) no lugar de /X
                    }
                }
                if (string.IsNullOrEmpty(file)) throw new RpcException("Desinstalador não encontrado.");
                if (Path.IsPathRooted(file) && !File.Exists(file)) throw new RpcException("O desinstalador não existe mais: " + file);
                int exit = RunAndWait(file, args, ctx);
                var stillThere = Reg.KeyExists(a.KeyPath);
                ok = !stillThere;
                message = ok ? "Programa desinstalado." : exit == 0 ? "O desinstalador terminou, mas o programa ainda aparece instalado (talvez a desinstalação tenha sido cancelada)." : "O desinstalador terminou com o código " + exit + ".";
            }
            lock (Gate) last = null;
            var leftovers = ok ? Leftovers(a) : new List<object>();
            if (ok) Activity.Add("clean", "delete", "Programa desinstalado: " + a.Name, a.Publisher);
            return new { ok, message, leftovers };
        }

        static int RunAndWait(string file, string args, RpcContext ctx)
        {
            Process p;
            try { p = Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true, WorkingDirectory = Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : Proc.Sys32 }); }
            catch (System.ComponentModel.Win32Exception ex) { throw new RpcException(ex.NativeErrorCode == 1223 ? "Desinstalação cancelada." : "Não foi possível abrir o desinstalador: " + ex.Message); }
            if (p == null) return 0;
            var tracked = new Dictionary<int, long>(); // pid → createTime
            int exit = 0;
            using (p)
            {
                var root = Processes.Snapshot().FirstOrDefault(x => x.Pid == p.Id);
                tracked[p.Id] = root?.CreateTime ?? 0;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalMinutes < 60)
                {
                    if (ctx.Token.IsCancellationRequested) break;
                    var snap = Processes.Snapshot();
                    // Acompanha os filhos (NSIS/Inno copiam o desinstalador para %TEMP% e encerram o processo original)
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var r in snap)
                            if (!tracked.ContainsKey(r.Pid) && tracked.TryGetValue(r.ParentPid, out var pct) && r.CreateTime >= pct) { tracked[r.Pid] = r.CreateTime; added = true; }
                    } while (added);
                    var alive = snap.Where(r => tracked.TryGetValue(r.Pid, out var ct) && ct == r.CreateTime).ToList();
                    var tempUninst = snap.Any(r => r.SessionId != 0 && Regex.IsMatch(r.Name, @"^(Un_?[A-Z]?\.exe|Au_\.exe|unins\d*\.exe|_iu\w*\.(tmp|exe))$", RegexOptions.IgnoreCase));
                    if (alive.Count == 0 && !tempUninst) break;
                    Thread.Sleep(400);
                }
                try { if (p.HasExited) exit = p.ExitCode; } catch { }
            }
            Thread.Sleep(800);
            return exit;
        }

        static string Norm(string s) => Regex.Replace(s ?? "", @"[^\p{L}\p{N}]", "").ToLowerInvariant();

        /// <summary>Pastas remanescentes com o nome do app/fabricante em Program Files, ProgramData e AppData.</summary>
        static List<object> Leftovers(App a)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddName(string n) { if (!string.IsNullOrWhiteSpace(n) && n.Trim().Length >= 3) names.Add(n.Trim()); }
            AddName(a.Name);
            AddName(Regex.Replace(a.Name, @"\s*[\(\[].*$", ""));
            AddName(Regex.Replace(a.Name, @"\s+v?\d+(\.\d+)*.*$", ""));
            if (!string.IsNullOrEmpty(a.Location)) AddName(Path.GetFileName(a.Location.TrimEnd('\\')));
            var pub = (a.Publisher ?? "").Trim();
            var pubShort = Regex.Replace(pub, @"[,\.]?\s*(Inc|LLC|Ltd|GmbH|Corp|Corporation|Co|S\.?A|Ltda|FZ-LLC|Limited|AB|AG|BV)\.?$", "", RegexOptions.IgnoreCase).Trim();
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            }.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Pastas usadas por outros programas instalados não entram
            List<App> others;
            try { others = Win32().Where(x => x.Id != a.Id).ToList(); } catch { others = new List<App>(); }
            var otherLocs = others.Select(x => x.Location).Where(x => !string.IsNullOrEmpty(x)).Select(x => x.TrimEnd('\\')).ToList();
            var pubShared = others.Any(x => string.Equals((x.Publisher ?? "").Trim(), pub, StringComparison.OrdinalIgnoreCase));

            var found = new List<string>();
            void Consider(string dir)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                var full = Path.GetFullPath(dir).TrimEnd('\\');
                if (SafePaths.Check(full) != null) return;
                if (otherLocs.Any(o => ToolsUtil.IsUnder(o, full) || ToolsUtil.SamePath(o, full))) return;
                if (found.Any(f => ToolsUtil.IsUnder(full, f))) return;
                found.RemoveAll(f => ToolsUtil.IsUnder(f, full));
                found.Add(full);
            }
            if (!string.IsNullOrEmpty(a.Location)) Consider(a.Location);
            foreach (var r in roots)
            {
                string[] subs;
                try { subs = Directory.GetDirectories(r); } catch { continue; }
                foreach (var d in subs)
                {
                    var leaf = Path.GetFileName(d);
                    var nl = Norm(leaf);
                    if (nl.Length < 3) continue;
                    if (names.Any(n => Norm(n) == nl)) { Consider(d); continue; }
                    // Pasta do fabricante: só a subpasta com o nome do app (ou a pasta inteira se o fabricante não tiver outros programas)
                    if (pub.Length >= 3 && (nl == Norm(pub) || (pubShort.Length >= 3 && nl == Norm(pubShort))))
                    {
                        string[] inner;
                        try { inner = Directory.GetDirectories(d); } catch { inner = new string[0]; }
                        var hit = inner.Where(x => names.Any(n => Norm(n) == Norm(Path.GetFileName(x)))).ToList();
                        foreach (var h in hit) Consider(h);
                        if (hit.Count == 0 && !pubShared) Consider(d);
                    }
                }
            }
            var sw = Stopwatch.StartNew();
            return found.Select(f => (object)new { path = f, size = (double)(ToolsUtil.FolderSize(f, sw, 8000) ?? 0) }).ToList();
        }

        public static object RemoveLeftovers(List<string> paths)
        {
            if (paths == null || paths.Count == 0) throw new RpcException("Nenhuma pasta selecionada.");
            return Files.RecycleMany(paths, "Restos de programas removidos");
        }

        // ---------- RPC ----------
        [Rpc("apps.list")]
        public static object ListRpc(RpcContext ctx, bool icons = true) => Collect(icons, ctx.Token);

        [Rpc("apps.uninstall")]
        public static object UninstallRpc(string id, RpcContext ctx) => Uninstall(id, ctx);

        [Rpc("apps.removeLeftovers")]
        public static object RemoveLeftoversRpc(List<string> paths) => RemoveLeftovers(paths);
    }
}
