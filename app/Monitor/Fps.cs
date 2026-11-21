using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Rage.Core;

namespace Rage.Monitor
{
    /// <summary>
    /// Contador de FPS sem injeção: escuta os eventos Present do DirectX (DXGI e D3D9) via ETW, como o PresentMon.
    /// Exige administrador. Funciona com jogos DirectX 9/10/11/12 e com Vulkan/OpenGL quando apresentados via DXGI.
    /// </summary>
    public static class Fps
    {
        const string SessionName = "RAGE-FPS-Session";
        static readonly Guid DXGI = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9 = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr p);
        [DllImport("advapi32.dll")] static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

        delegate void EventRecordCallback(IntPtr record);
        static EventRecordCallback callback; // mantém vivo
        static ulong session, trace;
        static IntPtr logfile, props;
        static Thread thread;
        public static bool Running { get; private set; }
        public static string Error { get; private set; }

        sealed class Stats { public readonly Queue<long> Times = new Queue<long>(); public long Last; }
        static readonly Dictionary<int, Stats> stats = new Dictionary<int, Stats>();
        static readonly double Freq = Stopwatch.Frequency;

        const int PropsSize = 120 + 2048;

        static IntPtr MakeProps()
        {
            var p = Marshal.AllocHGlobal(PropsSize);
            for (int i = 0; i < PropsSize; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, PropsSize);              // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                     // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, 0x00020000);            // Wnode.Flags = WNODE_FLAG_TRACED_GUID
            Marshal.WriteInt32(p, 48, 64);                    // BufferSize (KB)
            Marshal.WriteInt32(p, 52, 4);                     // MinimumBuffers
            Marshal.WriteInt32(p, 56, 32);                    // MaximumBuffers
            Marshal.WriteInt32(p, 64, 0x00000100);            // LogFileMode = EVENT_TRACE_REAL_TIME_MODE
            Marshal.WriteInt32(p, 68, 1);                     // FlushTimer (s)
            Marshal.WriteInt32(p, 116, 120);                  // LoggerNameOffset
            return p;
        }

        public static void Start()
        {
            if (Running || !Program.IsAdmin || !Settings.Current.FpsCounter) return;
            try
            {
                props = MakeProps();
                var r = StartTraceW(out session, SessionName, props);
                if (r == 183) // já existe (sessão antiga) — para e recria
                {
                    var tmp = MakeProps();
                    ControlTraceW(0, SessionName, tmp, 1);
                    Marshal.FreeHGlobal(tmp);
                    r = StartTraceW(out session, SessionName, props);
                }
                if (r != 0) { Error = "StartTrace " + r; Log.Warn("FPS: " + Error); return; }
                var g1 = DXGI; var g2 = D3D9;
                EnableTraceEx2(session, ref g1, 1, 4, 0xFFFFFFFFFFFFFFFF, 0, 0, IntPtr.Zero);
                EnableTraceEx2(session, ref g2, 1, 4, 0xFFFFFFFFFFFFFFFF, 0, 0, IntPtr.Zero);

                callback = OnEvent;
                logfile = Marshal.AllocHGlobal(448);
                for (int i = 0; i < 448; i++) Marshal.WriteByte(logfile, i, 0);
                var namePtr = Marshal.StringToHGlobalUni(SessionName);
                Marshal.WriteIntPtr(logfile, 8, namePtr);                               // LoggerName
                Marshal.WriteInt32(logfile, 28, 0x00000100 | 0x10000000);               // ProcessTraceMode = REAL_TIME | EVENT_RECORD
                Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(callback)); // EventRecordCallback
                trace = OpenTraceW(logfile);
                if (trace == ulong.MaxValue) { Error = "OpenTrace falhou"; Log.Warn("FPS: " + Error); StopSession(); return; }
                Running = true;
                thread = new Thread(() =>
                {
                    try { ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); }
                    catch (Exception ex) { Log.Warn("ProcessTrace: " + ex.Message); }
                    Running = false;
                }) { IsBackground = true, Name = "RAGE-FPS" };
                thread.Start();
                Log.Info("Contador de FPS (ETW) iniciado");
            }
            catch (Exception ex) { Error = ex.Message; Log.Error("FPS", ex); }
        }

        static void OnEvent(IntPtr rec)
        {
            try
            {
                // EVENT_HEADER: ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40
                var id = (ushort)Marshal.ReadInt16(rec, 40);
                var a = Marshal.ReadInt32(rec, 24);
                bool present = (a == unchecked((int)0xCA11C036) && (id == 42 || id == 55)) || (a == 0x783ACA0A && id == 1);
                if (!present) return;
                var pid = Marshal.ReadInt32(rec, 12);
                var ts = Marshal.ReadInt64(rec, 16);
                lock (stats)
                {
                    if (!stats.TryGetValue(pid, out var s)) { s = new Stats(); stats[pid] = s; }
                    s.Times.Enqueue(ts);
                    s.Last = ts;
                    var cutoff = ts - (long)(Freq * 10); // 10 s de janela
                    while (s.Times.Count > 0 && s.Times.Peek() < cutoff) s.Times.Dequeue();
                }
            }
            catch { }
        }

        static void StopSession()
        {
            try { if (props != IntPtr.Zero) ControlTraceW(session, null, props, 1); } catch { }
        }

        public static void Stop()
        {
            if (!Running && session == 0) return;
            try { StopSession(); if (trace != 0) CloseTrace(trace); } catch { }
            Running = false;
            session = 0; trace = 0;
        }

        public sealed class GameInfo
        {
            public int Pid { get; set; }
            public string Name { get; set; }
            public double? Fps { get; set; }
            public double? FrameTime { get; set; }
            public double? Low1 { get; set; }
            public bool IsGame { get; set; }
        }

        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static string NameOf(int pid)
        {
            lock (names)
            {
                if (names.TryGetValue(pid, out var n)) return n;
                try { using (var p = Process.GetProcessById(pid)) n = p.ProcessName; } catch { n = "pid " + pid; }
                if (names.Count > 300) names.Clear();
                names[pid] = n;
                return n;
            }
        }

        public static GameInfo Measure(int pid)
        {
            long now = Stopwatch.GetTimestamp();
            long[] times;
            lock (stats)
            {
                if (!stats.TryGetValue(pid, out var s) || now - s.Last > Freq * 1.5) return null;
                times = s.Times.ToArray();
            }
            if (times.Length < 3) return null;
            var oneSec = now - (long)Freq;
            var lastSec = times.Count(t => t >= oneSec);
            var deltas = new List<double>(times.Length);
            for (int i = 1; i < times.Length; i++) deltas.Add((times[i] - times[i - 1]) * 1000.0 / Freq);
            var recent = deltas.Skip(Math.Max(0, deltas.Count - Math.Max(1, lastSec))).ToList();
            var ft = recent.Count > 0 ? recent.Average() : (double?)null;
            deltas.Sort();
            var p99 = deltas[Math.Min(deltas.Count - 1, (int)Math.Floor(deltas.Count * 0.99))];
            return new GameInfo
            {
                Pid = pid, Name = NameOf(pid),
                Fps = lastSec,
                FrameTime = ft.HasValue ? Math.Round(ft.Value, 2) : (double?)null,
                Low1 = p99 > 0 ? Math.Round(1000.0 / p99) : (double?)null,
            };
        }

        /// <summary>Jogo/app em primeiro plano com FPS medido (ou o jogo da sessão do Turbo).</summary>
        public static GameInfo Current()
        {
            if (!Running) return null;
            var fg = (int)Native.ForegroundPid();
            var info = fg > 0 ? Measure(fg) : null;
            if (info == null)
            {
                var gp = Rage.Boost.GameWatcher.RunningGamePid();
                if (gp.HasValue) info = Measure(gp.Value);
            }
            if (info != null) info.IsGame = Rage.Boost.GameWatcher.IsTracked(info.Pid);
            // limpa PIDs antigos
            lock (stats)
            {
                var old = stats.Where(k => Stopwatch.GetTimestamp() - k.Value.Last > Freq * 30).Select(k => k.Key).ToList();
                foreach (var k in old) stats.Remove(k);
            }
            return info;
        }

        [Rpc("fps.status")]
        public static object Status()
        {
            List<GameInfo> all;
            lock (stats) all = stats.Keys.ToList().Select(Measure).Where(x => x != null).ToList();
            return new { running = Running, error = Error, admin = Program.IsAdmin, current = Current(), apps = all.OrderByDescending(x => x.Fps).ToList() };
        }
    }
}
