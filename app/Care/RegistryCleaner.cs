using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Categoria "registry" — limpeza conservadora: só entradas que apontam para arquivos locais que comprovadamente não existem.
    /// Tudo que é removido é exportado antes para um .reg em backups\registro.</summary>
    public sealed class RegistryScanner : ICareScanner
    {
        public string Category => "registry";
        public string Title => "Registro do Windows";
        public string Description => "Entradas órfãs e inválidas";
        public string Icon => "account_tree";
        public int Order => 40;
        public bool DefaultOn => true;

        const string GUninstall = "Desinstaladores inválidos", GAppPaths = "Caminhos de aplicativos", GShared = "DLLs compartilhadas ausentes",
                     GMui = "Cache MUI", GRun = "Inicialização inválida", GFonts = "Fontes ausentes", GExt = "Extensões de arquivo órfãs";

        internal sealed class Finding
        {
            public string Group, Title, Key;
            public List<string> Values;   // null = a chave inteira
            public List<string> Files;    // arquivos que precisam continuar ausentes para remover
        }

        // ---------- Análise de linhas de comando ----------
        static readonly HashSet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "msiexec.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "explorer.exe", "regsvr32.exe",
            "conhost.exe", "schtasks.exe", "sc.exe", "reg.exe", "dllhost.exe", "control.exe", "java.exe", "javaw.exe", "wsl.exe", "bash.exe", "py.exe", "python.exe", "pythonw.exe",
        };

        static readonly Regex Unquoted = new Regex(@"^([A-Za-z]:\\.*?\.(exe|com|bat|cmd|scr|cpl|msc|pif|dll|ocx))(?=$|[\s,""])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static bool Rooted(string p) => p != null && p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\';

        /// <summary>Separa o executável/arquivo do início de uma linha de comando.</summary>
        static string FirstPath(string cmd, out string rest)
        {
            rest = "";
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd).Trim();
            if (cmd.StartsWith("\"", StringComparison.Ordinal))
            {
                var e = cmd.IndexOf('"', 1);
                if (e <= 1) return null;
                rest = cmd.Substring(e + 1);
                return cmd.Substring(1, e - 1).Trim();
            }
            var m = Unquoted.Match(cmd);
            if (m.Success) { rest = cmd.Substring(m.Length); return m.Groups[1].Value; }
            var sp = cmd.IndexOf(' ');
            if (sp < 0) return cmd;
            rest = cmd.Substring(sp);
            return cmd.Substring(0, sp);
        }

        /// <summary>Arquivo do qual o comando depende (ou null quando não dá para saber com segurança).</summary>
        internal static string CommandTarget(string cmd)
        {
            var exe = FirstPath(cmd, out var rest);
            if (exe == null) return null;
            var name = Path.GetFileName(exe.TrimEnd('\\'));
            if (name.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
            {
                var arg = (rest ?? "").Trim();
                string dll;
                if (arg.StartsWith("\"", StringComparison.Ordinal)) { var e = arg.IndexOf('"', 1); dll = e > 1 ? arg.Substring(1, e - 1) : null; }
                else { var c = arg.IndexOf(','); dll = c > 0 ? arg.Substring(0, c) : null; }
                dll = Fs.Clean(dll);
                return Rooted(dll) ? dll : null;
            }
            if (Hosts.Contains(name)) return null;
            return Rooted(exe) ? exe : null;
        }

        static bool UnderWindows(string p)
        {
            var win = Fs.Windows.TrimEnd('\\') + "\\";
            if (p.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrEmpty(pf) && p.StartsWith(pf.TrimEnd('\\') + "\\Windows", StringComparison.OrdinalIgnoreCase)) return true; // Windows NT, Windows Mail, Windows Media Player...
            return false;
        }

        // ---------- Verificações ----------
        static void Uninstallers(List<Finding> l)
        {
            foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall" })
                foreach (var sub in CareUtil.SubKeys(root))
                {
                    var kp = root + "\\" + sub;
                    try
                    {
                        using (var k = Reg.Open(kp))
                        {
                            if (k == null) continue;
                            var name = k.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name)) continue;                       // entradas invisíveis/do Windows
                            if (ToInt(k.GetValue("SystemComponent")) == 1) continue;
                            if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") != null) continue; // atualizações
                            if (ToInt(k.GetValue("WindowsInstaller")) == 1) continue;          // MSI: cuidado do próprio Windows Installer
                            var pub = k.GetValue("Publisher") as string ?? "";
                            if (pub.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            var us = k.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                            if (string.IsNullOrWhiteSpace(us)) continue;                        // sem desinstalador: não dá para provar que é órfão
                            var target = CommandTarget(us);
                            if (target == null || !Fs.IsMissing(target)) continue;
                            var files = new List<string> { target };
                            var loc = k.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                            if (!string.IsNullOrWhiteSpace(loc))
                            {
                                var cl = Fs.Clean(loc);
                                if (cl == null || !Fs.IsMissing(cl)) continue;                  // pasta de instalação ainda existe
                                files.Add(cl);
                            }
                            l.Add(new Finding { Group = GUninstall, Title = name.Trim(), Key = kp, Files = files });
                        }
                    }
                    catch { }
                }
        }

        static void AppPaths(List<Finding> l)
        {
            foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths" })
                foreach (var sub in CareUtil.SubKeys(root))
                {
                    var kp = root + "\\" + sub;
                    var def = Reg.Get(kp, "") as string;
                    var p = Fs.Clean(FirstPath(def, out _));
                    if (p == null || !Rooted(p) || UnderWindows(p) || !Fs.IsMissing(p)) continue;
                    l.Add(new Finding { Group = GAppPaths, Title = sub, Key = kp, Files = new List<string> { p } });
                }
        }

        static void SharedDlls(List<Finding> l)
        {
            foreach (var kp in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" })
                foreach (var v in CareUtil.Values(kp))
                {
                    var p = Fs.Clean(v.Key);
                    if (p == null || !Rooted(p) || p.IndexOf("\\WinSxS\\", StringComparison.OrdinalIgnoreCase) >= 0 || !Fs.IsMissing(p)) continue;
                    l.Add(new Finding { Group = GShared, Title = Path.GetFileName(p), Key = kp, Values = new List<string> { v.Key }, Files = new List<string> { p } });
                }
        }

        static void MuiCache(List<Finding> l)
        {
            const string kp = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
            var byExe = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in CareUtil.Values(kp))
            {
                var n = v.Key;
                if (n.Length < 4 || n.StartsWith("@", StringComparison.Ordinal) || !Rooted(n)) continue;
                var dot = n.LastIndexOf('.');
                if (dot < 0) continue;
                var suffix = n.Substring(dot + 1);
                if (!suffix.Equals("FriendlyAppName", StringComparison.OrdinalIgnoreCase) && !suffix.Equals("ApplicationCompany", StringComparison.OrdinalIgnoreCase)) continue;
                var exe = n.Substring(0, dot);
                if (!byExe.TryGetValue(exe, out var list)) byExe[exe] = list = new List<string>();
                list.Add(n);
            }
            foreach (var kv in byExe)
            {
                if (!Fs.IsMissing(kv.Key)) continue;
                l.Add(new Finding { Group = GMui, Title = Path.GetFileName(kv.Key), Key = kp, Values = kv.Value, Files = new List<string> { kv.Key } });
            }
        }

        static void RunEntries(List<Finding> l)
        {
            foreach (var kp in new[]
            {
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
            })
                foreach (var v in CareUtil.Values(kp))
                {
                    if (v.Key.Length == 0 || !(v.Value is string cmd)) continue;
                    var target = CommandTarget(cmd);
                    if (target == null || !Fs.IsMissing(target)) continue;
                    l.Add(new Finding { Group = GRun, Title = v.Key, Key = kp, Values = new List<string> { v.Key }, Files = new List<string> { target } });
                }
        }

        static void Fonts(List<Finding> l)
        {
            var fontsDir = Path.Combine(Fs.Windows, "Fonts");
            foreach (var kp in new[] { @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts" })
                foreach (var v in CareUtil.Values(kp))
                {
                    if (!(v.Value is string file) || string.IsNullOrWhiteSpace(file) || file.IndexOfAny(new[] { '|', ',' }) >= 0) continue;
                    var p = Fs.Clean(file);
                    if (p == null) continue;
                    if (!Rooted(p)) p = Path.Combine(fontsDir, p);
                    if (!Fs.IsMissing(p)) continue;
                    l.Add(new Finding { Group = GFonts, Title = v.Key, Key = kp, Values = new List<string> { v.Key }, Files = new List<string> { p } });
                }
        }

        static void OpenWithApps(List<Finding> l)
        {
            const string root = @"HKCU\Software\Classes\Applications";
            foreach (var sub in CareUtil.SubKeys(root))
            {
                var kp = root + "\\" + sub;
                var cmd = Reg.Get(kp + @"\shell\open\command", "") as string;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                var target = CommandTarget(cmd);
                if (target == null || UnderWindows(target) || !Fs.IsMissing(target)) continue;
                l.Add(new Finding { Group = GExt, Title = sub, Key = kp, Files = new List<string> { target } });
            }
        }

        static int? ToInt(object v)
        {
            if (v == null) return null;
            try { return Convert.ToInt32(v is uint u ? unchecked((int)u) : v); } catch { return null; }
        }

        public List<CareItem> Scan(RpcContext ctx)
        {
            var found = new List<Finding>();
            var steps = new (string title, Action<List<Finding>> run)[]
            {
                (GUninstall, Uninstallers), (GAppPaths, AppPaths), (GShared, SharedDlls), (GMui, MuiCache), (GRun, RunEntries), (GFonts, Fonts), (GExt, OpenWithApps),
            };
            foreach (var s in steps)
            {
                ctx.Token.ThrowIfCancellationRequested();
                CareUtil.Item(ctx, Category, s.title);
                try { s.run(found); }
                catch (Exception ex) { Log.Warn("Registro (" + s.title + "): " + ex.Message); }
            }
            return found.Select(f => new CareItem
            {
                Id = "registry." + CareUtil.Hash(f.Key + "|" + (f.Values == null ? "*" : string.Join("|", f.Values))),
                Category = Category, Group = f.Group, Title = f.Title,
                Detail = f.Values == null ? f.Key : f.Key + "\\" + f.Values[0] + (f.Values.Count > 1 ? " (+" + (f.Values.Count - 1) + ")" : ""),
                Count = 1, Paths = f.Files.Take(5).ToList(),
                Note = !CareUtil.IsHkcu(f.Key) && !Program.IsAdmin ? "Requer administrador para remover." : null,
                Tag = f,
            }).ToList();
        }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            var work = items.Where(x => x.Tag is Finding).ToList();
            foreach (var x in items.Where(x => !(x.Tag is Finding))) results.Add(new FixResult { Id = x.Id, Message = "Faça uma nova verificação." });
            if (work.Count == 0) return results;

            // 1) Backup de tudo que será removido (sem backup, nada é apagado)
            var sb = new StringBuilder();
            foreach (var it in work)
            {
                var f = (Finding)it.Tag;
                try
                {
                    if (f.Values == null) Reg.AppendExport(sb, f.Key);
                    else foreach (var v in f.Values) Reg.AppendExport(sb, f.Key, v);
                }
                catch (Exception ex) { Log.Warn("Exportar " + f.Key + ": " + ex.Message); }
            }
            string backup;
            try { backup = Reg.SaveBackup("limpeza-registro", sb); }
            catch (Exception ex)
            {
                Log.Error("Backup do registro", ex);
                results.AddRange(work.Select(x => new FixResult { Id = x.Id, Message = "Não foi possível criar o backup do registro; nada foi alterado." }));
                return results;
            }

            // 2) Remoção
            int i = 0;
            foreach (var it in work)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = work.Count });
                var f = (Finding)it.Tag;
                var r = new FixResult { Id = it.Id, Backup = backup };
                try
                {
                    if (!CareUtil.IsHkcu(f.Key) && !Program.IsAdmin) r.Message = CareUtil.AdminMsg;
                    else if (f.Files.Any(p => !Fs.IsMissing(p))) r.Message = "O arquivo voltou a existir; a entrada foi mantida.";
                    else
                    {
                        if (f.Values == null) Reg.DeleteKey(f.Key);
                        else foreach (var v in f.Values) Reg.DeleteValue(f.Key, v);
                        r.Ok = true;
                        r.Message = "Removido (backup em " + Path.GetFileName(backup) + ")";
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
                {
                    r.Message = Program.IsAdmin ? "O Windows negou acesso a esta chave." : CareUtil.AdminMsg;
                }
                catch (Exception ex) { r.Message = ex.Message; }
                results.Add(r);
            }
            return results;
        }
    }
}
