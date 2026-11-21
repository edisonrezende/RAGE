using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Lista de processos com CPU/GPU/memória (mesma fonte do Gerenciador de Tarefas: NtQuerySystemInformation + PDH).</summary>
    public static class Processes
    {
        public sealed class Raw
        {
            public int Pid, ParentPid, Threads, BasePriority;
            public uint SessionId;
            public long CreateTime, CpuTime, PrivateWs, WorkingSet;
            public string Name;
        }

        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>Foto de todos os processos sem abrir handles.</summary>
        public static List<Raw> Snapshot()
        {
            int size = 1 << 20;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int st = NtQuerySystemInformation(5, buf, size, out var ret);
                    if (st == unchecked((int)0xC0000004)) { size = Math.Max(size * 2, ret + 65536); continue; }
                    if (st != 0) throw new RpcException("Falha ao listar processos (0x" + st.ToString("X8") + ")");
                    var list = new List<Raw>(400);
                    long off = 0;
                    while (true)
                    {
                        var p = new IntPtr(buf.ToInt64() + off);
                        int next = Marshal.ReadInt32(p, 0);
                        var r = new Raw
                        {
                            Threads = Marshal.ReadInt32(p, 4),
                            PrivateWs = Marshal.ReadInt64(p, 8),
                            CreateTime = Marshal.ReadInt64(p, 32),
                            CpuTime = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48),
                            BasePriority = Marshal.ReadInt32(p, 72),
                            Pid = (int)Marshal.ReadIntPtr(p, 80).ToInt64(),
                            ParentPid = (int)Marshal.ReadIntPtr(p, 88).ToInt64(),
                            SessionId = (uint)Marshal.ReadInt32(p, 100),
                            WorkingSet = Marshal.ReadIntPtr(p, 144).ToInt64(),
                        };
                        int len = (ushort)Marshal.ReadInt16(p, 56);
                        var nb = Marshal.ReadIntPtr(p, 64);
                        r.Name = nb == IntPtr.Zero || len == 0 ? (r.Pid == 0 ? "Ocioso do sistema" : "System") : Marshal.PtrToStringUni(nb, len / 2);
                        list.Add(r);
                        if (next == 0) break;
                        off += next;
                    }
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            throw new RpcException("Falha ao listar processos.");
        }

        public static string ImagePath(int pid)
        {
            if (pid <= 4) return null;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        static Dictionary<int, string> WindowTitles()
        {
            var res = new Dictionary<int, string>();
            EnumWindows((h, l) =>
            {
                try
                {
                    if (!Native.IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true; // GW_OWNER
                    var n = GetWindowTextLength(h);
                    if (n <= 0) return true;
                    Native.GetWindowThreadProcessId(h, out var pid);
                    if (res.ContainsKey((int)pid)) return true;
                    var sb = new StringBuilder(n + 2);
                    Native.GetWindowText(h, sb, sb.Capacity);
                    var t = sb.ToString().Trim();
                    if (t.Length > 0 && t != "Program Manager") res[(int)pid] = t;
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return res;
        }

        static readonly HashSet<string> Critical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Ocioso do sistema", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
            "lsass.exe", "lsaiso.exe", "dwm.exe", "fontdrvhost.exe", "svchost.exe", "LsaIso.exe", "MsMpEng.exe", "audiodg.exe", "sihost.exe", "ctfmon.exe",
        };
        static readonly HashSet<string> NoKill = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Ocioso do sistema", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
            "lsass.exe", "lsaiso.exe", "dwm.exe", "fontdrvhost.exe",
        };

        // ---------- Estado entre chamadas ----------
        static readonly object Gate = new object();
        static Dictionary<long, long> prevCpu = new Dictionary<long, long>();
        static long prevStamp;
        static readonly Dictionary<long, string> pathCache = new Dictionary<long, string>();
        static Rage.Monitor.Pdh gpuPdh;
        static bool gpuTried;

        static long Key(Raw r) => ((long)r.Pid << 40) ^ (r.CreateTime & 0xFFFFFFFFFFL);

        static string PathFor(Raw r)
        {
            var k = Key(r);
            if (pathCache.TryGetValue(k, out var p)) return p;
            p = ImagePath(r.Pid);
            if (pathCache.Count > 3000) pathCache.Clear();
            pathCache[k] = p;
            return p;
        }

        static Dictionary<int, double> GpuByPid()
        {
            var res = new Dictionary<int, double>();
            if (gpuPdh == null) return res;
            try
            {
                foreach (var kv in gpuPdh.GetArray("gpu"))
                {
                    var name = kv.Key;
                    if (!name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
                    var e = name.IndexOf('_', 4);
                    if (e < 0 || !int.TryParse(name.Substring(4, e - 4), out var pid)) continue;
                    var v = kv.Value;
                    if (!res.TryGetValue(pid, out var prev) || v > prev) res[pid] = v; // maior engine do processo
                }
            }
            catch { }
            return res;
        }

        static void EnsureGpu()
        {
            if (gpuTried) return;
            gpuTried = true;
            try
            {
                var p = new Rage.Monitor.Pdh();
                if (p.Add("gpu", @"\GPU Engine(*)\Utilization Percentage")) { gpuPdh = p; gpuPdh.Collect(); }
                else p.Dispose();
            }
            catch (Exception ex) { Log.Warn("PDH GPU por processo: " + ex.Message); }
        }

        public static string PriorityName(int basePri)
        {
            if (basePri >= 24) return "realtime";
            if (basePri >= 13) return "high";
            if (basePri >= 10) return "aboveNormal";
            if (basePri >= 8) return "normal";
            if (basePri >= 6) return "belowNormal";
            return "idle";
        }

        public static List<object> List(bool icons)
        {
            lock (Gate)
            {
                EnsureGpu();
                var cores = Environment.ProcessorCount;
                var snap = Snapshot();
                var now = Stopwatch.GetTimestamp();
                var elapsed100ns = prevStamp == 0 ? 0 : (now - prevStamp) * 10_000_000.0 / Stopwatch.Frequency;
                if (prevStamp == 0 || elapsed100ns > 15 * 10_000_000.0 || elapsed100ns < 1_000_000)
                {
                    // Primeira chamada (ou muito antiga): mede um intervalo curto para ter CPU % válido
                    prevCpu = snap.ToDictionary(Key, r => r.CpuTime);
                    if (gpuPdh != null) { gpuPdh.Collect(); }
                    Thread.Sleep(500);
                    snap = Snapshot();
                    var n2 = Stopwatch.GetTimestamp();
                    elapsed100ns = (n2 - now) * 10_000_000.0 / Stopwatch.Frequency;
                    now = n2;
                }
                if (gpuPdh != null) { gpuPdh.Collect(); }
                var gpu = GpuByPid();
                var titles = WindowTitles();
                var mySession = (uint)Process.GetCurrentProcess().SessionId;
                var next = new Dictionary<long, long>();
                var list = new List<object>(snap.Count);
                foreach (var r in snap)
                {
                    var k = Key(r);
                    next[k] = r.CpuTime;
                    double cpu = 0;
                    if (r.Pid != 0 && prevCpu.TryGetValue(k, out var prevT) && elapsed100ns > 0)
                        cpu = Math.Max(0, Math.Min(100, (r.CpuTime - prevT) / (elapsed100ns * cores) * 100.0));
                    if (r.Pid == 0) continue; // ocioso
                    var path = PathFor(r);
                    var isSystem = r.SessionId == 0 || r.Pid <= 4 || Critical.Contains(r.Name);
                    titles.TryGetValue(r.Pid, out var title);
                    string pub = path != null ? ToolsUtil.Publisher(path) : null;
                    list.Add(new
                    {
                        pid = r.Pid,
                        name = r.Name,
                        title = title ?? ToolsUtil.Description(path) ?? System.IO.Path.GetFileNameWithoutExtension(r.Name),
                        path,
                        publisher = pub,
                        cpu = Math.Round(cpu, 1),
                        mem = (double)(r.PrivateWs > 0 ? r.PrivateWs : r.WorkingSet),
                        gpu = Math.Round(Math.Min(100, gpu.TryGetValue(r.Pid, out var g) ? g : 0), 1),
                        threads = r.Threads,
                        priority = PriorityName(r.BasePriority),
                        isSystem,
                        hasWindow = title != null,
                        parent = r.ParentPid,
                        icon = icons && path != null && (!isSystem || r.SessionId == mySession) ? ToolsUtil.IconData(path) : null,
                    });
                }
                prevCpu = next;
                prevStamp = now;
                return list;
            }
        }

        // ---------- Ações ----------
        static Raw FindRaw(int pid) => Snapshot().FirstOrDefault(x => x.Pid == pid);

        public static object Kill(int pid, bool tree)
        {
            var snap = Snapshot();
            var target = snap.FirstOrDefault(x => x.Pid == pid) ?? throw new RpcException("O processo já foi encerrado.");
            if (pid <= 4 || NoKill.Contains(target.Name)) throw new RpcException("Este é um processo essencial do Windows e não pode ser encerrado.");
            if (pid == Process.GetCurrentProcess().Id) throw new RpcException("Use o menu do RAGE! para fechar o próprio app.");
            var pids = new List<int> { pid };
            if (tree)
            {
                var queue = new Queue<Raw>(); queue.Enqueue(target);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    foreach (var c in snap.Where(x => x.ParentPid == p.Pid && x.CreateTime >= p.CreateTime && x.Pid != p.Pid && !pids.Contains(x.Pid)))
                    {
                        if (NoKill.Contains(c.Name) || c.Pid == Process.GetCurrentProcess().Id) continue;
                        pids.Add(c.Pid); queue.Enqueue(c);
                    }
                }
            }
            int killed = 0; string err = null;
            foreach (var id in pids.AsEnumerable().Reverse())
            {
                try { using (var p = Process.GetProcessById(id)) { p.Kill(); p.WaitForExit(3000); killed++; } }
                catch (ArgumentException) { }
                catch (Exception ex) { if (id == pid) err = ex is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 5 ? "Sem permissão para encerrar este processo. Abra o RAGE! como administrador." : ex.Message; }
            }
            if (err != null && killed == 0) throw new RpcException(err);
            Log.Info("Processo encerrado: " + target.Name + " (" + pid + ")" + (tree ? " + árvore" : ""));
            return new { ok = true, killed };
        }

        public static object SetPriority(int pid, string priority)
        {
            ProcessPriorityClass cls;
            switch ((priority ?? "").Trim())
            {
                case "idle": cls = ProcessPriorityClass.Idle; break;
                case "belowNormal": cls = ProcessPriorityClass.BelowNormal; break;
                case "normal": cls = ProcessPriorityClass.Normal; break;
                case "aboveNormal": cls = ProcessPriorityClass.AboveNormal; break;
                case "high": cls = ProcessPriorityClass.High; break;
                default: throw new RpcException("Prioridade inválida.");
            }
            var r = FindRaw(pid) ?? throw new RpcException("O processo já foi encerrado.");
            if (pid <= 4 || NoKill.Contains(r.Name)) throw new RpcException("Não é possível mudar a prioridade de um processo essencial do Windows.");
            try { using (var p = Process.GetProcessById(pid)) p.PriorityClass = cls; }
            catch (ArgumentException) { throw new RpcException("O processo já foi encerrado."); }
            catch (System.ComponentModel.Win32Exception w) when (w.NativeErrorCode == 5) { throw new RpcException("Sem permissão para alterar este processo. Abra o RAGE! como administrador."); }
            catch (Exception ex) { throw new RpcException("Não foi possível alterar a prioridade: " + ex.Message); }
            return new { ok = true, pid, priority };
        }

        // ---------- RPC ----------
        [Rpc("proc.list")]
        public static object ListRpc(bool icons = true) => List(icons);

        [Rpc("proc.kill")]
        public static object KillRpc(int pid, bool tree = false) => Kill(pid, tree);

        [Rpc("proc.priority")]
        public static object PriorityRpc(int pid, string priority) => SetPriority(pid, priority);

        [Rpc("proc.reveal")]
        public static object RevealRpc(int pid)
        {
            var path = ImagePath(pid);
            if (path == null) throw new RpcException("Não foi possível descobrir o arquivo deste processo.");
            ToolsUtil.Reveal(path);
            return true;
        }
    }
}
