using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Utilitários compartilhados pelas ferramentas.</summary>
    public static class ToolsUtil
    {
        public static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        public static void RequireAdmin(string what = null)
        {
            if (!Program.IsAdmin)
                throw new RpcException((what ?? "Esta ação") + " precisa do RAGE! aberto como administrador.");
        }

        public static string Expand(string s) => string.IsNullOrEmpty(s) ? s : Environment.ExpandEnvironmentVariables(s);

        public static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            var p = path.TrimEnd('\\') + "\\";
            var d = dir.TrimEnd('\\') + "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        public static bool SamePath(string a, string b) =>
            a != null && b != null && string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---------- Linha de comando → executável ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint SearchPath(string lpPath, string lpFileName, string lpExtension, int nBufferLength, StringBuilder lpBuffer, IntPtr lpFilePart);

        /// <summary>Extrai o caminho do executável de uma linha de comando (com ou sem aspas, rundll32, variáveis de ambiente).</summary>
        public static string ExeFromCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            var s = Expand(cmd.Trim());
            string exe, rest;
            SplitCommand(s, out exe, out rest);
            if (string.IsNullOrEmpty(exe)) return null;
            exe = Resolve(exe);
            var name = Path.GetFileName(exe ?? "").ToLowerInvariant();
            if ((name == "rundll32.exe" || name == "rundll32") && !string.IsNullOrEmpty(rest))
            {
                var dll = rest.Trim();
                if (dll.StartsWith("\"")) { var e = dll.IndexOf('"', 1); dll = e > 1 ? dll.Substring(1, e - 1) : dll.Trim('"'); }
                else { var c = dll.IndexOf(','); if (c > 0) dll = dll.Substring(0, c); else { var sp = dll.IndexOf(' '); if (sp > 0) dll = dll.Substring(0, sp); } }
                dll = dll.Trim().TrimEnd(',');
                var r = Resolve(dll);
                if (r != null && File.Exists(r)) return r;
            }
            return exe;
        }

        public static void SplitCommand(string s, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.StartsWith("\""))
            {
                var e = s.IndexOf('"', 1);
                if (e > 1) { exe = s.Substring(1, e - 1); args = s.Substring(e + 1).Trim(); }
                else exe = s.Trim('"');
                return;
            }
            var parts = s.Split(' ');
            for (int i = 1; i <= parts.Length; i++)
            {
                var p = string.Join(" ", parts, 0, i);
                if (p.Length > 2 && (File.Exists(p) || (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p + ".exe"))))
                {
                    exe = File.Exists(p) ? p : p + ".exe";
                    args = string.Join(" ", parts, i, parts.Length - i).Trim();
                    return;
                }
            }
            exe = parts[0];
            args = string.Join(" ", parts, 1, parts.Length - 1).Trim();
        }

        static string Resolve(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return exe;
            exe = exe.Trim();
            try
            {
                if (Path.IsPathRooted(exe)) return exe;
                var sb = new StringBuilder(520);
                if (SearchPath(null, exe, ".exe", sb.Capacity, sb, IntPtr.Zero) > 0) return sb.ToString();
                var sys = Path.Combine(Proc.Sys32, exe);
                if (File.Exists(sys)) return sys;
                if (File.Exists(sys + ".exe")) return sys + ".exe";
            }
            catch { }
            return exe;
        }

        // ---------- Metadados de arquivo (com cache) ----------
        static readonly ConcurrentDictionary<string, string> publisherCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, string> descCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, string> iconCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Publisher(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return publisherCache.GetOrAdd(path, p =>
            {
                try
                {
                    if (!File.Exists(p)) return null;
                    var c = FileVersionInfo.GetVersionInfo(p).CompanyName;
                    if (!string.IsNullOrWhiteSpace(c)) return c.Trim();
                }
                catch { }
                return Signer(p);
            });
        }

        /// <summary>Nome do assinante digital (CN) do arquivo, sem validar a cadeia.</summary>
        public static string Signer(string path)
        {
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(path);
                var subj = cert.Subject ?? "";
                foreach (var part in subj.Split(','))
                {
                    var t = part.Trim();
                    if (t.StartsWith("O=", StringComparison.OrdinalIgnoreCase)) return t.Substring(2).Trim('"');
                }
                foreach (var part in subj.Split(','))
                {
                    var t = part.Trim();
                    if (t.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return t.Substring(3).Trim('"');
                }
            }
            catch { }
            return null;
        }

        public static string Description(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return descCache.GetOrAdd(path, p =>
            {
                try
                {
                    if (!File.Exists(p)) return null;
                    var vi = FileVersionInfo.GetVersionInfo(p);
                    var d = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
                    return string.IsNullOrWhiteSpace(d) ? null : d.Trim();
                }
                catch { return null; }
            });
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        /// <summary>Ícone como data URL PNG. Aceita "arquivo.exe", "arquivo.dll,índice" e ".ico". Resultado em cache.</summary>
        public static string IconData(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            return iconCache.GetOrAdd(spec, s =>
            {
                try
                {
                    var t = Expand(s.Trim());
                    int index = 0;
                    var comma = t.LastIndexOf(',');
                    if (comma > 2 && int.TryParse(t.Substring(comma + 1).Trim(), out var ix)) { index = ix; t = t.Substring(0, comma); }
                    t = t.Trim().Trim('"');
                    if (!File.Exists(t)) return null;
                    if (t.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var ic = new Icon(t, 32, 32)) return ToData(ic);
                    }
                    if (index != 0)
                    {
                        var large = new IntPtr[1];
                        if (ExtractIconEx(t, index, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                        {
                            try { using (var ic = Icon.FromHandle(large[0])) return ToData(ic); }
                            finally { DestroyIcon(large[0]); }
                        }
                    }
                    return Rage.Boost.Games.IconData(t);
                }
                catch { return null; }
            });
        }

        static string ToData(Icon ic)
        {
            using (var bmp = ic.ToBitmap())
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        public static string ImageFileData(string file, int maxBytes = 40000)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
                var fi = new FileInfo(file);
                if (fi.Length > maxBytes) return null;
                var ext = fi.Extension.ToLowerInvariant();
                var mime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".ico" ? "image/x-icon" : null;
                if (mime == null) return null;
                return "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(file));
            }
            catch { return null; }
        }

        public static bool IsMicrosoft(string publisher) =>
            !string.IsNullOrEmpty(publisher) && publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------- Tamanho de pasta com limite de tempo ----------
        public static long? FolderSize(string dir, Stopwatch budget = null, long budgetMs = 0, int maxFiles = 200000)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                long total = 0; int files = 0;
                var stack = new Stack<DirectoryInfo>();
                stack.Push(new DirectoryInfo(dir));
                while (stack.Count > 0)
                {
                    if (budget != null && budgetMs > 0 && budget.ElapsedMilliseconds > budgetMs) return null;
                    var d = stack.Pop();
                    IEnumerable<FileSystemInfo> entries;
                    try { entries = d.EnumerateFileSystemInfos(); } catch { continue; }
                    try
                    {
                        foreach (var e in entries)
                        {
                            if ((e.Attributes & FileAttributes.Directory) != 0)
                            {
                                if ((e.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push((DirectoryInfo)e);
                            }
                            else { total += ((FileInfo)e).Length; if (++files > maxFiles) return total; }
                        }
                    }
                    catch { }
                }
                return total;
            }
            catch { return null; }
        }

        // ---------- Registro: data da última gravação ----------
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryInfoKey(SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcClass, IntPtr lpReserved, IntPtr lpcSubKeys, IntPtr lpcMaxSubKeyLen,
            IntPtr lpcMaxClassLen, IntPtr lpcValues, IntPtr lpcMaxValueNameLen, IntPtr lpcMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);

        public static DateTime? KeyLastWrite(RegistryKey k)
        {
            try
            {
                if (k == null) return null;
                if (RegQueryInfoKey(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ft) == 0 && ft > 0)
                    return DateTime.FromFileTime(ft);
            }
            catch { }
            return null;
        }

        // ---------- Linhas de console ----------
        /// <summary>Limpa uma linha de saída de console (remove retorno de carro, barras de progresso vazias, girador).</summary>
        public static string CleanLine(string l)
        {
            if (l == null) return null;
            var i = l.LastIndexOf('\r');
            if (i >= 0) l = l.Substring(i + 1);
            l = l.Replace("\0", "").TrimEnd();
            var t = l.Trim();
            if (t.Length == 0) return null;
            if (t.Length <= 2 && "-\\|/".IndexOf(t[0]) >= 0) return null;
            return l;
        }

        public static string LastMeaningful(string output, int maxLen = 300)
        {
            if (string.IsNullOrEmpty(output)) return null;
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Replace("\0", "").Trim()).Where(x => x.Length > 1 && x.Any(char.IsLetter)).ToList();
            var l = lines.LastOrDefault();
            if (l != null && l.Length > maxLen) l = l.Substring(0, maxLen) + "…";
            return l;
        }

        public static void Reveal(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else throw new RpcException("Arquivo não encontrado.");
        }

        public static DateTime? WmiDate(object v)
        {
            try
            {
                var s = v as string;
                if (string.IsNullOrEmpty(s)) return null;
                return System.Management.ManagementDateTimeConverter.ToDateTime(s);
            }
            catch { return null; }
        }

        /// <summary>Data sem hora do WMI (datas de driver/BIOS vêm em UTC 00:00 e não devem mudar de dia no fuso local).</summary>
        public static DateTime? WmiDay(object v)
        {
            var s = v as string;
            if (string.IsNullOrEmpty(s) || s.Length < 8) return null;
            return DateTime.TryParseExact(s.Substring(0, 8), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }

        public static string Str(object v) { var s = v as string; return string.IsNullOrWhiteSpace(s) ? null : s.Trim(); }
    }

    /// <summary>Proteção de pastas do sistema para as ferramentas que apagam arquivos.</summary>
    public static class SafePaths
    {
        static string Sf(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return null; } }

        static readonly Lazy<List<string>> exactRoots = new Lazy<List<string>>(() =>
        {
            var list = new List<string>
            {
                Sf(Environment.SpecialFolder.ProgramFiles), Sf(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432"),
                Sf(Environment.SpecialFolder.CommonProgramFiles), Sf(Environment.SpecialFolder.CommonProgramFilesX86),
                Sf(Environment.SpecialFolder.CommonApplicationData),
                Sf(Environment.SpecialFolder.UserProfile), Path.GetDirectoryName(Sf(Environment.SpecialFolder.UserProfile) ?? ""),
                Environment.GetEnvironmentVariable("PUBLIC"),
                Sf(Environment.SpecialFolder.ApplicationData), Sf(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", "AppData"),
                Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", @"AppData\LocalLow"),
                Sf(Environment.SpecialFolder.DesktopDirectory), Sf(Environment.SpecialFolder.MyDocuments),
                Sf(Environment.SpecialFolder.MyPictures), Sf(Environment.SpecialFolder.MyMusic), Sf(Environment.SpecialFolder.MyVideos),
                Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", "Downloads"),
                Sf(Environment.SpecialFolder.CommonDesktopDirectory), Sf(Environment.SpecialFolder.CommonDocuments),
                Sf(Environment.SpecialFolder.StartMenu), Sf(Environment.SpecialFolder.CommonStartMenu),
                Sf(Environment.SpecialFolder.Programs), Sf(Environment.SpecialFolder.CommonPrograms),
                Sf(Environment.SpecialFolder.Startup), Sf(Environment.SpecialFolder.CommonStartup),
                Path.GetTempPath().TrimEnd('\\'),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData) ?? "", "Temp"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData) ?? "", "Programs"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData) ?? "", "Packages"),
                Path.Combine(Sf(Environment.SpecialFolder.ApplicationData) ?? "", "Microsoft"),
                Path.Combine(Sf(Environment.SpecialFolder.LocalApplicationData) ?? "", "Microsoft"),
                Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Sf(Environment.SpecialFolder.Favorites), Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", "Saved Games"),
                Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", "Contacts"), Path.Combine(Sf(Environment.SpecialFolder.UserProfile) ?? "", "Links"),
            };
            return list.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });

        static readonly Lazy<List<string>> treeRoots = new Lazy<List<string>>(() =>
        {
            var pf = new[] { Sf(Environment.SpecialFolder.ProgramFiles), Sf(Environment.SpecialFolder.ProgramFilesX86), Environment.GetEnvironmentVariable("ProgramW6432") }
                .Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var roaming = Sf(Environment.SpecialFolder.ApplicationData) ?? "";
            var local = Sf(Environment.SpecialFolder.LocalApplicationData) ?? "";
            var list = new List<string>
            {
                ToolsUtil.WinDir, Path.Combine(Sf(Environment.SpecialFolder.CommonApplicationData) ?? "", "Microsoft"), Paths.AppDir, Paths.Data,
                // Chaves de criptografia (DPAPI), credenciais e certificados do usuário: apagar quebra senhas salvas e logins
                Path.Combine(roaming, @"Microsoft\Protect"), Path.Combine(roaming, @"Microsoft\Credentials"), Path.Combine(roaming, @"Microsoft\Crypto"),
                Path.Combine(roaming, @"Microsoft\SystemCertificates"), Path.Combine(roaming, @"Microsoft\Vault"), Path.Combine(roaming, @"Microsoft\Windows"),
                Path.Combine(local, @"Microsoft\Credentials"), Path.Combine(local, @"Microsoft\Vault"), Path.Combine(local, @"Microsoft\Windows"),
                Path.Combine(local, @"Microsoft\WindowsApps"), Path.Combine(local, "Packages"),
            };
            foreach (var p in pf)
                foreach (var n in new[] { "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
                                          "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer",
                                          "Common Files\\microsoft shared", "Microsoft Update Health Tools", "Reference Assemblies", "dotnet" })
                    list.Add(Path.Combine(p, n));
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    var r = d.RootDirectory.FullName;
                    foreach (var n in new[] { "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "Config.Msi" })
                        list.Add(Path.Combine(r, n));
                }
                catch { }
            }
            return list.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });

        /// <summary>Devolve o motivo (texto) se o caminho não pode ser apagado; null se for seguro.</summary>
        public static string Check(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Caminho vazio.";
            string full;
            try { full = Path.GetFullPath(path.Trim()).TrimEnd('\\'); }
            catch { return "Caminho inválido."; }
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) return "Caminho inválido.";
            if (full.Length <= 3 || (Path.GetPathRoot(full) ?? "").TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase)) return "A raiz de uma unidade não pode ser apagada.";
            foreach (var r in treeRoots.Value)
                if (ToolsUtil.IsUnder(full, r)) return "Pasta protegida do sistema: " + r;
            foreach (var r in exactRoots.Value)
                if (ToolsUtil.SamePath(full, r)) return "Pasta essencial do Windows ou do usuário: " + r;
            // Uma pasta que contém áreas protegidas também não pode ser apagada inteira
            foreach (var r in treeRoots.Value.Concat(exactRoots.Value))
                if (ToolsUtil.IsUnder(r, full) && !ToolsUtil.SamePath(r, full)) return "Esta pasta contém uma área protegida do sistema: " + r;
            try
            {
                // Arquivos de sistema na raiz da unidade (pagefile.sys, hiberfil.sys, swapfile.sys...)
                var parent = Path.GetDirectoryName(full);
                if (parent != null && parent.TrimEnd('\\').Length <= 2 && File.Exists(full))
                {
                    var a = File.GetAttributes(full);
                    if ((a & FileAttributes.System) != 0) return "Arquivo do sistema na raiz da unidade.";
                }
            }
            catch { }
            return null;
        }

        public static bool IsProtected(string path) => Check(path) != null;

        /// <summary>Pastas que as varreduras de duplicados/vazios não devem percorrer.</summary>
        public static bool SkipWhenScanning(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return true;
            var name = Path.GetFileName(dir.TrimEnd('\\'));
            if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var r in treeRoots.Value)
                if (ToolsUtil.IsUnder(dir, r)) return true;
            return false;
        }
    }

    /// <summary>Tarefas do Agendador via COM (Schedule.Service).</summary>
    public static class TaskSched
    {
        public static dynamic Connect()
        {
            var t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) throw new RpcException("Agendador de Tarefas indisponível.");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc;
        }

        public static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }
    }
}
