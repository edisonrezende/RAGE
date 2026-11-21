using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Varredura e exclusão de arquivos via Win32: não segue junções/links simbólicos, aceita caminhos longos e ignora pastas sem acesso.</summary>
    internal static class Fs
    {
        public struct Entry
        {
            public string Path;
            public long Size;
            public DateTime LastWriteUtc;
            public FileAttributes Attr;
        }

        public sealed class WalkOptions
        {
            public bool Recursive = true;
            public Regex Pattern;                      // filtro pelo nome do arquivo (null = todos)
            public DateTime? OlderThanUtc;             // só arquivos modificados antes disso
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationLow, ftCreationHigh, ftAccessLow, ftAccessHigh, ftWriteLow, ftWriteHigh;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        static readonly IntPtr Invalid = new IntPtr(-1);
        const uint InvalidAttr = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool RemoveDirectoryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetFileAttributesW(string path, uint attr);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveTypeW(string root);

        static List<string> excluded;
        static List<string> Excluded
        {
            get
            {
                if (excluded != null) return excluded;
                var l = new List<string>();
                try { l.Add(Paths.AppDir.TrimEnd('\\')); } catch { }
                try { if (Paths.Data != null) l.Add(Paths.Data.TrimEnd('\\')); } catch { }
                excluded = l;
                return l;
            }
        }

        /// <summary>Pastas do próprio RAGE! nunca entram na limpeza.</summary>
        public static bool IsExcluded(string path)
        {
            foreach (var ex in Excluded)
                if (path.Equals(ex, StringComparison.OrdinalIgnoreCase) || path.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Prefixo \\?\ só quando necessário (o caminho já deve estar normalizado).</summary>
        public static string Long(string p)
        {
            if (p.Length < 240 || p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p;
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + p.Substring(2);
            return @"\\?\" + p;
        }

        static DateTime FileTime(uint high, uint low)
        {
            var v = ((long)high << 32) | low;
            if (v <= 0 || v > 2650467743999999999L) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(v); } catch { return DateTime.MinValue; }
        }

        /// <summary>Percorre uma pasta chamando onFile para cada arquivo. dirs (opcional) recebe as subpastas visitadas, pais antes dos filhos.</summary>
        public static void Walk(string root, WalkOptions o, Action<Entry> onFile, CancellationToken ct, List<Entry> dirs = null)
        {
            if (string.IsNullOrEmpty(root)) return;
            root = root.TrimEnd('\\');
            if (root.Length < 3 || IsExcluded(root)) return;
            o = o ?? new WalkOptions();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                var h = FindFirstFileExW(Long(dir + "\\*"), 1 /*FindExInfoBasic*/, out var fd, 0, IntPtr.Zero, 2 /*LARGE_FETCH*/);
                if (h == Invalid) continue;
                try
                {
                    do
                    {
                        var name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        var full = dir + "\\" + name;
                        var attr = (FileAttributes)fd.dwFileAttributes;
                        if ((attr & FileAttributes.Directory) != 0)
                        {
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue; // nunca segue junções/links
                            if (!o.Recursive) continue;
                            if (IsExcluded(full)) continue;
                            stack.Push(full);
                            dirs?.Add(new Entry { Path = full, Attr = attr, LastWriteUtc = FileTime(fd.ftWriteHigh, fd.ftWriteLow) });
                            continue;
                        }
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        if (o.Pattern != null && !o.Pattern.IsMatch(name)) continue;
                        var wt = FileTime(fd.ftWriteHigh, fd.ftWriteLow);
                        if (o.OlderThanUtc.HasValue && wt > o.OlderThanUtc.Value) continue;
                        onFile(new Entry { Path = full, Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, LastWriteUtc = wt, Attr = attr });
                    } while (FindNextFileW(h, out fd));
                }
                finally { FindClose(h); }
            }
        }

        /// <summary>Informações de um único arquivo (funciona mesmo com o arquivo em uso, ex.: hiberfil.sys).</summary>
        public static Entry? Stat(string path)
        {
            var h = FindFirstFileExW(Long(path), 1, out var fd, 0, IntPtr.Zero, 0);
            if (h == Invalid) return null;
            FindClose(h);
            return new Entry { Path = path, Size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, LastWriteUtc = FileTime(fd.ftWriteHigh, fd.ftWriteLow), Attr = (FileAttributes)fd.dwFileAttributes };
        }

        /// <summary>Apaga um arquivo; arquivos em uso ou sem permissão são ignorados (retorna false).</summary>
        public static bool DeleteFile(string path)
        {
            var lp = Long(path);
            if (DeleteFileW(lp)) return true;
            var err = Marshal.GetLastWin32Error();
            if (err == 5)
            {
                var a = GetFileAttributesW(lp);
                if (a != InvalidAttr && (a & 1) != 0 && SetFileAttributesW(lp, a & ~1u) && DeleteFileW(lp)) return true;
            }
            return false;
        }

        public static bool RemoveDir(string path) => RemoveDirectoryW(Long(path));

        public enum State { Exists, Missing, Unknown }

        public static State Check(string path)
        {
            if (string.IsNullOrEmpty(path)) return State.Unknown;
            var a = GetFileAttributesW(Long(path));
            if (a != InvalidAttr) return State.Exists;
            var e = Marshal.GetLastWin32Error();
            return e == 2 || e == 3 ? State.Missing : State.Unknown; // sem acesso, nome inválido etc. = não sabemos (não acusa)
        }

        public static bool Exists(string path) => Check(path) == State.Exists;
        public static bool DirExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var a = GetFileAttributesW(Long(path.TrimEnd('\\')));
            return a != InvalidAttr && (a & 0x10) != 0;
        }

        /// <summary>Unidade fixa presente (C:, D: interno). Removíveis, rede, CD e unidades ausentes ficam de fora.</summary>
        public static bool OnFixedDrive(string path)
        {
            if (path == null || path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsLetter(path[0])) return false;
            return GetDriveTypeW(path.Substring(0, 3)) == 3;
        }

        /// <summary>Normaliza um caminho vindo do registro/atalho: expande variáveis, tira aspas e espaços. Null se inválido.</summary>
        public static string Clean(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"').Trim());
            if (p.Length < 3 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            if (p.IndexOf('/') >= 0) p = p.Replace('/', '\\');
            return p;
        }

        static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        static readonly string Pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        static IEnumerable<string> Alternates(string p)
        {
            string Swap(string from, string to) => p.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase) ? to + p.Substring(from.Length) : null;
            var list = new[]
            {
                Swap(Pf, Pf86), Swap(Pf86, Pf),
                Swap(Path.Combine(Win, "System32"), Path.Combine(Win, "SysWOW64")),
                Swap(Path.Combine(Win, "SysWOW64"), Path.Combine(Win, "System32")),
                Swap(Path.Combine(Win, "Sysnative"), Path.Combine(Win, "System32")),
            };
            return list.Where(x => x != null);
        }

        /// <summary>True só quando temos certeza de que o arquivo/pasta local não existe (unidade fixa presente, sem acesso negado, nem na variante 32/64 bits).</summary>
        public static bool IsMissing(string path)
        {
            path = Clean(path);
            if (path == null || !OnFixedDrive(path)) return false;
            if (path.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) return false; // pasta protegida dos apps da Store
            try { path = Path.GetFullPath(path); } catch { return false; }
            if (Check(path) != State.Missing) return false;
            foreach (var alt in Alternates(path)) if (Check(alt) != State.Missing) return false;
            return true;
        }

        public static Regex Wild(params string[] patterns)
        {
            if (patterns == null || patterns.Length == 0) return null;
            var parts = patterns.Select(p => "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$");
            return new Regex(string.Join("|", parts), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static string Combine(string a, params string[] parts)
        {
            if (string.IsNullOrEmpty(a)) return null;
            var p = a;
            foreach (var x in parts) p = Path.Combine(p, x);
            return p;
        }

        public static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string Windows = Win;
        public static string LocalLow => Path.Combine(Path.GetDirectoryName(LocalAppData) ?? LocalAppData, "LocalLow");

        /// <summary>Lista subpastas (sem links), ignorando erros.</summary>
        public static List<string> SubDirs(string dir)
        {
            var l = new List<string>();
            if (!DirExists(dir)) return l;
            try
            {
                foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    try { if ((d.Attributes & FileAttributes.ReparsePoint) == 0) l.Add(d.FullName); } catch { }
                }
            }
            catch { }
            return l;
        }
    }

    /// <summary>Utilitários comuns dos módulos de cuidado.</summary>
    internal static class CareUtil
    {
        public const string AdminMsg = "Requer privilégios de administrador. Reabra o RAGE! como administrador.";

        public static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var b = sha.ComputeHash(Encoding.UTF8.GetBytes((s ?? "").ToLowerInvariant()));
                return string.Concat(b.Take(6).Select(x => x.ToString("x2")));
            }
        }

        public static bool AnyRunning(params string[] names)
        {
            foreach (var n in names)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); } catch { continue; }
                var any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        /// <summary>Processos pelo nome cujo executável contém um trecho do caminho (null = qualquer). Sem acesso ao caminho conta como "aberto".</summary>
        public static bool RunningWithPath(string name, Func<string, bool> pathOk)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(name); } catch { return false; }
            var found = false;
            foreach (var p in ps)
            {
                if (!found)
                {
                    if (pathOk == null) found = true;
                    else
                    {
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (path == null || pathOk(path)) found = true;
                    }
                }
                p.Dispose();
            }
            return found;
        }

        public static void Item(RpcContext ctx, string category, string title)
        {
            try { ctx?.Progress(new { phase = "item", category, title }); } catch { }
        }

        public static string Days(DateTime when)
        {
            var d = (int)Math.Floor((DateTime.Now - when).TotalDays);
            if (d <= 0) return "hoje";
            if (d == 1) return "ontem";
            return "há " + d + " dias";
        }

        /// <summary>Valores de uma chave (sem expandir variáveis).</summary>
        public static List<KeyValuePair<string, object>> Values(string path)
        {
            var l = new List<KeyValuePair<string, object>>();
            try
            {
                using (var k = Reg.Open(path))
                {
                    if (k == null) return l;
                    foreach (var n in k.GetValueNames())
                    {
                        object v = null;
                        try { v = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { }
                        l.Add(new KeyValuePair<string, object>(n, v));
                    }
                }
            }
            catch { }
            return l;
        }

        public static List<string> SubKeys(string path)
        {
            try { using (var k = Reg.Open(path)) return k?.GetSubKeyNames().ToList() ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        public static bool IsHkcu(string path) => path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) || path.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase);

        public static int Build()
        {
            int.TryParse(Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber"), out var b);
            return b;
        }

        public static bool HasBattery()
        {
            try { return (System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0; }
            catch { return false; }
        }

        public static bool RebootPending()
        {
            return Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
                || Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        }

        /// <summary>Executa uma consulta WMI simples, devolvendo os objetos (ou lista vazia).</summary>
        public static List<System.Management.ManagementBaseObject> Wmi(string scope, string query, int timeoutMs = 8000)
        {
            var l = new List<System.Management.ManagementBaseObject>();
            try
            {
                var opts = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromMilliseconds(timeoutMs), ReturnImmediately = true, Rewindable = false };
                using (var s = new System.Management.ManagementObjectSearcher(new System.Management.ManagementScope(scope), new System.Management.ObjectQuery(query), opts))
                    foreach (var o in s.Get()) l.Add(o);
            }
            catch (Exception ex) { Log.Warn("WMI " + scope + " " + query + ": " + ex.Message); }
            return l;
        }

        public static object Prop(System.Management.ManagementBaseObject o, string name)
        {
            try { return o[name]; } catch { return null; }
        }
    }

    /// <summary>Navegadores conhecidos e seus perfis.</summary>
    internal sealed class BrowserInfo
    {
        public string Id;
        public string Name;
        public string Process;
        public Func<string, bool> PathFilter;     // diferencia Opera e Opera GX (mesmo opera.exe)
        public bool Firefox;
        public List<string> Roots = new List<string>(); // Chromium: pastas "User Data" (ou equivalente)

        public bool Installed => Firefox ? FirefoxProfiles(true).Count > 0 || FirefoxProfiles(false).Count > 0 : Roots.Any(Fs.DirExists);
        public bool Running() => CareUtil.RunningWithPath(Process, PathFilter);

        public string RunningNote(bool privacy) => privacy
            ? "Feche o " + Name + " antes de limpar (os arquivos ficam bloqueados enquanto ele está aberto)."
            : "O " + Name + " está aberto: arquivos em uso serão ignorados. Feche o navegador para limpar tudo.";

        /// <summary>Pastas de perfil (Default, Profile 1, ..., a própria raiz no Opera, perfis laterais).</summary>
        public List<string> Profiles()
        {
            var l = new List<string>();
            foreach (var root in Roots.Where(Fs.DirExists))
            {
                if (IsProfile(root, true)) l.Add(root);
                foreach (var d in Fs.SubDirs(root)) if (IsProfile(d, false)) l.Add(d);
                var side = Path.Combine(root, "_side_profiles");
                foreach (var d in Fs.SubDirs(side)) l.Add(d);
            }
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool IsProfile(string dir, bool isRoot)
        {
            var name = Path.GetFileName(dir);
            if (!isRoot && (name.Equals("Default", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase) || name.Equals("System Profile", StringComparison.OrdinalIgnoreCase))) return true;
            if (isRoot) return File.Exists(Path.Combine(dir, "Preferences")) || Fs.DirExists(Path.Combine(dir, "Cache")) || File.Exists(Path.Combine(dir, "History"));
            return File.Exists(Path.Combine(dir, "Preferences"));
        }

        public List<string> FirefoxProfiles(bool local)
        {
            var root = Path.Combine(local ? Fs.LocalAppData : Fs.AppData, @"Mozilla\Firefox\Profiles");
            return Fs.SubDirs(root);
        }

        public static List<BrowserInfo> All()
        {
            var la = Fs.LocalAppData; var ra = Fs.AppData;
            return new List<BrowserInfo>
            {
                new BrowserInfo { Id = "chrome", Name = "Google Chrome", Process = "chrome", Roots = { Path.Combine(la, @"Google\Chrome\User Data") } },
                new BrowserInfo { Id = "edge", Name = "Microsoft Edge", Process = "msedge", Roots = { Path.Combine(la, @"Microsoft\Edge\User Data") } },
                new BrowserInfo { Id = "brave", Name = "Brave", Process = "brave", Roots = { Path.Combine(la, @"BraveSoftware\Brave-Browser\User Data") } },
                new BrowserInfo { Id = "opera", Name = "Opera", Process = "opera", PathFilter = p => p.IndexOf("Opera GX", StringComparison.OrdinalIgnoreCase) < 0,
                    Roots = { Path.Combine(ra, @"Opera Software\Opera Stable"), Path.Combine(la, @"Opera Software\Opera Stable") } },
                new BrowserInfo { Id = "operagx", Name = "Opera GX", Process = "opera", PathFilter = p => p.IndexOf("Opera GX", StringComparison.OrdinalIgnoreCase) >= 0,
                    Roots = { Path.Combine(ra, @"Opera Software\Opera GX Stable"), Path.Combine(la, @"Opera Software\Opera GX Stable") } },
                new BrowserInfo { Id = "vivaldi", Name = "Vivaldi", Process = "vivaldi", Roots = { Path.Combine(la, @"Vivaldi\User Data") } },
                new BrowserInfo { Id = "firefox", Name = "Firefox", Process = "firefox", Firefox = true },
            };
        }

        /// <summary>Subpastas de cache de um perfil Chromium/Electron/CEF (não inclui histórico, cookies, senhas ou dados de sites).</summary>
        public static readonly string[] ProfileCacheDirs = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnWebGPUCache", "DawnGraphiteCache" };
        public static readonly string[] RootCacheDirs = { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" };
    }
}
