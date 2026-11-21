using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Benchmark reprodutível (CPU, memória, disco) e teste de estresse da CPU.</summary>
    public static class Bench
    {
        public sealed class Scores
        {
            public double? CpuSingle { get; set; }
            public double? CpuMulti { get; set; }
            public double? Memory { get; set; }     // GB/s
            public double? DiskRead { get; set; }   // MB/s
            public double? DiskWrite { get; set; }  // MB/s
        }
        public sealed class SystemInfo { public string Cpu { get; set; } public string Gpu { get; set; } public double Ram { get; set; } public int Threads { get; set; } }
        public sealed class Result
        {
            public string Id { get; set; }
            public DateTime At { get; set; }
            public Scores Scores { get; set; } = new Scores();
            public double Total { get; set; }
            public bool Partial { get; set; }
            public List<string> Tests { get; set; }
            public SystemInfo System { get; set; }
            public long DurationMs { get; set; }
            public string Version { get; set; }
        }

        const string Version = "1";
        static string HistoryFile => Paths.File("bench.json");
        static readonly object RunGate = new object();
        static volatile bool running;

        // Fatores de escala (calibrados uma vez; não mudar sem trocar a Version)
        const double CpuScale = 1.0 / 24.0;  // pontos por unidade/s (i5-9300H ≈ 1000 em 1 núcleo)
        const double WeightSingle = 1.0, WeightMulti = 0.25, WeightMemory = 40, WeightRead = 0.25, WeightWrite = 0.25;

        // ---------- Carga de CPU determinística ----------
        // Uma "unidade" = inteiros (xorshift/multiplicação/tabela), ponto flutuante (Mandelbrot) e hash (FNV/mistura) sobre um buffer fixo.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Unit(ulong seed, uint[] table, byte[] data)
        {
            // Inteiros: gerador xorshift + tabela de 16 K entradas (64 KB, cabe na L2)
            ulong x = seed | 1, acc = 0;
            int mask = table.Length - 1;
            for (int i = 0; i < 2048; i++)
            {
                x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                var m = x * 0x9E3779B97F4A7C15UL;
                var idx = (int)(m >> 40) & mask;
                table[idx] = table[idx] * 1664525u + 1013904223u + (uint)i;
                acc += table[(idx + 97) & mask] ^ (m >> 17);
                if ((i & 63) == 0) acc += (ulong)((long)(m >> 1) / ((long)(x & 0xFFFF) + 3));
            }
            // Ponto flutuante: Mandelbrot 24x16 com até 48 iterações
            double fsum = 0;
            for (int py = 0; py < 16; py++)
                for (int px = 0; px < 24; px++)
                {
                    double cr = -2.0 + px * (2.6 / 24) + (seed & 7) * 1e-4, ci = -1.2 + py * (2.4 / 16);
                    double zr = 0, zi = 0; int it = 0;
                    while (it < 48 && zr * zr + zi * zi < 4.0) { var t = zr * zr - zi * zi + cr; zi = 2 * zr * zi + ci; zr = t; it++; }
                    fsum += it + Math.Sqrt(zr * zr + zi * zi + 1.0);
                }
            // Hash: FNV-1a + mistura estilo murmur sobre 4 KB
            ulong h = 14695981039346656037UL ^ seed;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= 1099511628211UL;
                if ((i & 31) == 31) { h ^= h >> 33; h *= 0xff51afd7ed558ccdUL; h ^= h >> 33; }
            }
            return acc ^ h ^ (ulong)(fsum * 1000);
        }

        static byte[] MakeData()
        {
            var d = new byte[4096];
            ulong s = 0x12345678ABCDEFUL;
            for (int i = 0; i < d.Length; i++) { s ^= s << 13; s ^= s >> 7; s ^= s << 17; d[i] = (byte)s; }
            return d;
        }

        static uint[] MakeTable() { var t = new uint[16384]; for (int i = 0; i < t.Length; i++) t[i] = (uint)(i * 2654435761u); return t; }

        static ulong sink;

        /// <summary>Executa unidades em N threads por um tempo fixo; devolve unidades/s (descartando o aquecimento).</summary>
        static double RunCpu(int threads, double warmupSec, double measureSec, CancellationToken ct, Action<double> onPct)
        {
            var counts = new long[threads * 16]; // espaçado para evitar falso compartilhamento
            var stop = 0;
            var start = new ManualResetEventSlim(false);
            var ths = new List<Thread>();
            for (int t = 0; t < threads; t++)
            {
                int slot = t * 16;
                var th = new Thread(() =>
                {
                    var table = MakeTable(); var data = MakeData();
                    ulong seed = (ulong)(slot + 1) * 0x51ED27UL, local = 0;
                    start.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        local += Unit(seed++, table, data);
                        Interlocked.Increment(ref counts[slot]);
                    }
                    sink ^= local;
                }) { IsBackground = true, Priority = threads == 1 ? ThreadPriority.AboveNormal : ThreadPriority.Normal, Name = "RAGE-Bench" };
                th.Start(); ths.Add(th);
            }
            start.Set();
            var sw = Stopwatch.StartNew();
            long baseCount = 0; double baseT = 0;
            var total = warmupSec + measureSec;
            double rate = 0;
            try
            {
                while (sw.Elapsed.TotalSeconds < total)
                {
                    if (ct.IsCancellationRequested) break;
                    Thread.Sleep(50);
                    if (baseT == 0 && sw.Elapsed.TotalSeconds >= warmupSec)
                    {
                        baseT = sw.Elapsed.TotalSeconds;
                        baseCount = Sum(counts);
                    }
                    onPct?.Invoke(Math.Min(1, sw.Elapsed.TotalSeconds / total));
                }
            }
            finally
            {
                var endCount = Sum(counts); var endT = sw.Elapsed.TotalSeconds;
                Volatile.Write(ref stop, 1);
                foreach (var th in ths) th.Join(5000);
                rate = endT > baseT && baseT > 0 ? (endCount - baseCount) / (endT - baseT) : 0;
            }
            ct.ThrowIfCancellationRequested();
            return rate;
        }
        static long Sum(long[] c) { long s = 0; for (int i = 0; i < c.Length; i += 16) s += Interlocked.Read(ref c[i]); return s; }

        // ---------- Memória ----------
        static double RunMemory(CancellationToken ct, Action<double> onPct)
        {
            const int size = 128 * 1024 * 1024;
            var src = new byte[size]; var dst = new byte[size];
            for (int i = 0; i < size; i += 4096) { src[i] = (byte)i; dst[i] = 1; } // toca as páginas
            int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
            int chunk = size / threads;
            long bytes = 0;
            var stop = 0;
            var ths = new List<Thread>();
            var go = new ManualResetEventSlim(false);
            for (int t = 0; t < threads; t++)
            {
                int off = t * chunk;
                var th = new Thread(() =>
                {
                    go.Wait();
                    while (Volatile.Read(ref stop) == 0)
                    {
                        Buffer.BlockCopy(src, off, dst, off, chunk);
                        Interlocked.Add(ref bytes, chunk);
                    }
                }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                th.Start(); ths.Add(th);
            }
            go.Set();
            var sw = Stopwatch.StartNew();
            Thread.Sleep(500);
            var b0 = Interlocked.Read(ref bytes); var t0 = sw.Elapsed.TotalSeconds;
            while (sw.Elapsed.TotalSeconds < 4.0 && !ct.IsCancellationRequested) { Thread.Sleep(50); onPct?.Invoke(sw.Elapsed.TotalSeconds / 4.0); }
            var b1 = Interlocked.Read(ref bytes); var t1 = sw.Elapsed.TotalSeconds;
            Volatile.Write(ref stop, 1);
            foreach (var th in ths) th.Join(5000);
            src = null; dst = null;
            GC.Collect();
            ct.ThrowIfCancellationRequested();
            // Convenção STREAM (cópia = leitura + escrita)
            return (b1 - b0) * 2.0 / (t1 - t0) / 1e9;
        }

        // ---------- Disco ----------
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, out int written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, out int read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetEndOfFile(SafeFileHandle h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, CREATE_ALWAYS = 2, OPEN_EXISTING = 3;
        const uint FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000, FILE_ATTRIBUTE_TEMPORARY = 0x100;

        static void RunDisk(CancellationToken ct, Action<double> onPct, out double readMBs, out double writeMBs)
        {
            readMBs = 0; writeMBs = 0;
            var dir = Path.GetTempPath();
            long fileSize = 256L * 1024 * 1024;
            var drive = new DriveInfo(Path.GetPathRoot(dir));
            if (drive.AvailableFreeSpace < fileSize + 1024L * 1024 * 1024)
                throw new RpcException("Pouco espaço livre em " + drive.Name + " para o teste de disco (precisa de 1,3 GB).");
            var file = Path.Combine(dir, "rage-bench-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            const int block = 4 * 1024 * 1024;
            var buf = VirtualAlloc(IntPtr.Zero, (UIntPtr)block, 0x3000, 0x04); // MEM_COMMIT|MEM_RESERVE, PAGE_READWRITE (alinhado à página)
            if (buf == IntPtr.Zero) throw new RpcException("Memória insuficiente para o teste de disco.");
            try
            {
                // Dados incompressíveis
                var tmp = new byte[block];
                new Random(1234).NextBytes(tmp);
                Marshal.Copy(tmp, 0, buf, block);

                // Escrita sem cache (write-through)
                using (var h = CreateFile(file, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero, CREATE_ALWAYS, FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_ATTRIBUTE_TEMPORARY, IntPtr.Zero))
                {
                    if (h.IsInvalid) throw new RpcException("Não foi possível criar o arquivo de teste: " + Native.LastError());
                    SetFilePointerEx(h, fileSize, out _, 0); SetEndOfFile(h); SetFilePointerEx(h, 0, out _, 0);
                    long written = 0; var sw = Stopwatch.StartNew();
                    long pos = 0;
                    while (written < fileSize * 2 && (written < fileSize || sw.Elapsed.TotalSeconds < 2.0))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pos >= fileSize) { SetFilePointerEx(h, 0, out _, 0); pos = 0; }
                        if (!WriteFile(h, buf, block, out var w, IntPtr.Zero) || w != block) throw new RpcException("Falha ao gravar o arquivo de teste: " + Native.LastError());
                        written += w; pos += w;
                        onPct?.Invoke(Math.Min(0.5, written / (double)fileSize * 0.25));
                    }
                    writeMBs = written / 1e6 / sw.Elapsed.TotalSeconds;
                }
                // Leitura sequencial sem cache
                using (var h = CreateFile(file, GENERIC_READ, 1, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero))
                {
                    if (h.IsInvalid) throw new RpcException("Não foi possível abrir o arquivo de teste: " + Native.LastError());
                    long read = 0; var sw = Stopwatch.StartNew();
                    while (read < fileSize * 4 && (read < fileSize || sw.Elapsed.TotalSeconds < 2.0))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!ReadFile(h, buf, block, out var r, IntPtr.Zero)) throw new RpcException("Falha ao ler o arquivo de teste: " + Native.LastError());
                        if (r == 0) { SetFilePointerEx(h, 0, out _, 0); continue; }
                        read += r;
                        onPct?.Invoke(0.5 + Math.Min(0.5, sw.Elapsed.TotalSeconds / 4.0));
                    }
                    readMBs = read / 1e6 / sw.Elapsed.TotalSeconds;
                }
            }
            finally
            {
                VirtualFree(buf, UIntPtr.Zero, 0x8000);
                try { if (File.Exists(file)) File.Delete(file); } catch (Exception ex) { Log.Warn("Apagar arquivo do benchmark: " + ex.Message); }
            }
        }

        // ---------- Execução ----------
        static SystemInfo Sys()
        {
            var si = new SystemInfo { Threads = Environment.ProcessorCount, Ram = Native.Memory().ullTotalPhys };
            // Fontes rápidas (registro e D3DKMT) — o WMI de vídeo pode levar segundos
            try { si.Cpu = SysInfo.CleanCpu(Reg.GetStr(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")); } catch { }
            try
            {
                var ads = Rage.Monitor.D3dkmt.Enumerate().Where(a => !a.Software && !string.IsNullOrEmpty(a.Name) && a.Name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) < 0).ToList();
                si.Gpu = (ads.FirstOrDefault(a => a.Discrete) ?? ads.FirstOrDefault())?.Name;
            }
            catch { }
            if (string.IsNullOrEmpty(si.Cpu))
            {
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                        foreach (ManagementObject o in s.Get()) using (o) si.Cpu = SysInfo.CleanCpu(o["Name"] as string);
                }
                catch { }
            }
            return si;
        }

        public static Result Run(List<string> tests, RpcContext ctx)
        {
            var all = new[] { "cpuSingle", "cpuMulti", "memory", "disk" };
            var list = (tests == null || tests.Count == 0 ? all.ToList() : tests.Where(t => all.Contains(t)).Distinct().ToList());
            if (list.Count == 0) throw new RpcException("Nenhum teste válido.");
            lock (RunGate)
            {
                if (running) throw new RpcException("Um benchmark já está em andamento.");
                if (Stress.Running) throw new RpcException("Pare o teste de estresse antes de rodar o benchmark.");
                running = true;
            }
            var proc = Process.GetCurrentProcess();
            var prevPri = proc.PriorityClass;
            var total = Stopwatch.StartNew();
            try
            {
                try { proc.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                var res = new Result { Id = Guid.NewGuid().ToString("N").Substring(0, 12), At = DateTime.Now, Tests = list, System = Sys(), Version = Version };
                // Pesos de duração de cada teste para o percentual geral
                var dur = new Dictionary<string, double> { ["cpuSingle"] = 7, ["cpuMulti"] = 8, ["memory"] = 5, ["disk"] = 6 };
                double totalDur = list.Sum(t => dur[t]), doneDur = 0;
                foreach (var t in list)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var label = t == "cpuSingle" ? "CPU (1 núcleo)" : t == "cpuMulti" ? "CPU (todos os núcleos)" : t == "memory" ? "Memória" : "Disco";
                    var lastSent = DateTime.MinValue;
                    Action<double> pct = f =>
                    {
                        if ((DateTime.Now - lastSent).TotalMilliseconds < 200) return;
                        lastSent = DateTime.Now;
                        ctx.Progress(new { test = t, pct = Math.Round((doneDur + f * dur[t]) / totalDur * 100), label });
                    };
                    ctx.Progress(new { test = t, pct = Math.Round(doneDur / totalDur * 100), label });
                    switch (t)
                    {
                        case "cpuSingle": res.Scores.CpuSingle = Math.Round(RunCpu(1, 1.0, 6.0, ctx.Token, pct) * CpuScale); break;
                        case "cpuMulti": res.Scores.CpuMulti = Math.Round(RunCpu(Environment.ProcessorCount, 1.0, 7.0, ctx.Token, pct) * CpuScale); break;
                        case "memory": res.Scores.Memory = Math.Round(RunMemory(ctx.Token, pct), 1); break;
                        case "disk":
                            RunDisk(ctx.Token, pct, out var rd, out var wr);
                            res.Scores.DiskRead = Math.Round(rd); res.Scores.DiskWrite = Math.Round(wr);
                            break;
                    }
                    doneDur += dur[t];
                }
                var s = res.Scores;
                res.Total = Math.Round((s.CpuSingle ?? 0) * WeightSingle + (s.CpuMulti ?? 0) * WeightMulti + (s.Memory ?? 0) * WeightMemory + (s.DiskRead ?? 0) * WeightRead + (s.DiskWrite ?? 0) * WeightWrite);
                res.Partial = list.Count < all.Length;
                res.DurationMs = total.ElapsedMilliseconds;
                ctx.Progress(new { test = "done", pct = 100, label = "Concluído" });
                Save(res);
                var parts = new List<string>();
                if (s.CpuSingle.HasValue) parts.Add("CPU 1 núcleo " + s.CpuSingle);
                if (s.CpuMulti.HasValue) parts.Add("multi " + s.CpuMulti);
                if (s.Memory.HasValue) parts.Add("memória " + s.Memory.Value.ToString("0.0") + " GB/s");
                if (s.DiskRead.HasValue) parts.Add("disco " + s.DiskRead + "/" + s.DiskWrite + " MB/s");
                Activity.Add("bench", "speed", res.Partial ? "Benchmark parcial concluído" : "Benchmark concluído", string.Join(" · ", parts), res.Total.ToString("N0") + " pts");
                return res;
            }
            finally
            {
                try { proc.PriorityClass = prevPri; } catch { }
                lock (RunGate) running = false;
            }
        }

        static void Save(Result r)
        {
            try
            {
                var hist = Json.Load(HistoryFile, new List<Result>());
                hist.Insert(0, r);
                if (hist.Count > 50) hist.RemoveRange(50, hist.Count - 50);
                Json.Save(HistoryFile, hist);
            }
            catch (Exception ex) { Log.Warn("Salvar histórico do benchmark: " + ex.Message); }
        }

        [Rpc("bench.run")]
        public static object RunRpc(List<string> tests, RpcContext ctx) => Run(tests, ctx);

        [Rpc("bench.history")]
        public static object HistoryRpc() => Json.Load(HistoryFile, new List<Result>());

        internal static void StressWorker(CancellationToken ct)
        {
            var table = MakeTable(); var data = MakeData();
            ulong seed = (ulong)Thread.CurrentThread.ManagedThreadId, local = 0;
            while (!ct.IsCancellationRequested) local += Unit(seed++, table, data);
            sink ^= local;
        }
    }

    /// <summary>Teste de estresse: carga máxima em todos os núcleos por um tempo definido.</summary>
    public static class Stress
    {
        static readonly object Gate = new object();
        static CancellationTokenSource cts;
        static DateTime startedAt;
        static int minutes, threads;
        static Timer timer;

        public static bool Running { get { lock (Gate) return cts != null && !cts.IsCancellationRequested; } }

        public static object Status()
        {
            lock (Gate)
            {
                var run = cts != null && !cts.IsCancellationRequested;
                var elapsed = run ? (DateTime.Now - startedAt).TotalSeconds : 0;
                return new
                {
                    running = run,
                    startedAt = run ? startedAt : (DateTime?)null,
                    minutes = run ? minutes : 0,
                    threads = run ? threads : 0,
                    elapsedSec = Math.Round(elapsed),
                    remainingSec = run ? Math.Max(0, Math.Round(minutes * 60 - elapsed)) : 0,
                };
            }
        }

        public static object Start(int minutesReq, int? threadsReq)
        {
            lock (Gate)
            {
                if (cts != null && !cts.IsCancellationRequested) throw new RpcException("O teste de estresse já está rodando.");
                minutes = Math.Max(1, Math.Min(minutesReq <= 0 ? 10 : minutesReq, 180));
                threads = Math.Max(1, Math.Min(threadsReq ?? Environment.ProcessorCount, Environment.ProcessorCount));
                cts = new CancellationTokenSource();
                startedAt = DateTime.Now;
                var token = cts.Token;
                for (int i = 0; i < threads; i++)
                    new Thread(() => { try { Bench.StressWorker(token); } catch { } }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RAGE-Stress" }.Start();
                timer = new Timer(_ => Stop("tempo esgotado"), null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
                Log.Info("Estresse iniciado: " + threads + " threads por " + minutes + " min");
                Activity.Add("info", "local_fire_department", "Teste de estresse iniciado", threads + " threads · " + minutes + " min");
            }
            return Status();
        }

        public static object Stop(string reason = "manual")
        {
            lock (Gate)
            {
                if (cts == null) return Status();
                var dur = DateTime.Now - startedAt;
                cts.Cancel(); cts = null;
                try { timer?.Dispose(); } catch { }
                timer = null;
                Log.Info("Estresse parado (" + reason + ")");
                Activity.Add("info", "local_fire_department", "Teste de estresse finalizado", "Duração: " + (int)dur.TotalMinutes + " min " + dur.Seconds + " s");
            }
            return Status();
        }

        [Rpc("stress.start")]
        public static object StartRpc(int minutes, int? threads) => Start(minutes, threads);

        [Rpc("stress.stop")]
        public static object StopRpc() => Stop();

        [Rpc("stress.status")]
        public static object StatusRpc() => Status();
    }
}
