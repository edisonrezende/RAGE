using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Rage.Core;

namespace Rage.Monitor
{
    public sealed class CpuSnap
    {
        public string Name { get; set; }
        public int Cores { get; set; }
        public int Threads { get; set; }
        public double? Load { get; set; }
        public double[] PerCore { get; set; }
        public double? Clock { get; set; }
        public double BaseClock { get; set; }
        public double? Temp { get; set; }
        public string TempSource { get; set; }
        public double? Power { get; set; }
        public double[] CoreTemps { get; set; }
    }

    public sealed class GpuSnap
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        public string Kind { get; set; }
        public string State { get; set; }       // active | sleep
        public double? Load { get; set; }
        public double? Temp { get; set; }
        public double? HotSpot { get; set; }
        public double? Clock { get; set; }
        public double? MemClock { get; set; }
        public double? Power { get; set; }
        public double? VramUsed { get; set; }
        public double? VramTotal { get; set; }
        public double? SharedUsed { get; set; }
        public double? Fan { get; set; }
        public double? FanPct { get; set; }
        public double? MaxTemp { get; set; }
    }

    public sealed class RamSnap
    {
        public double Total { get; set; }
        public double Used { get; set; }
        public double Avail { get; set; }
        public double Pct { get; set; }
        public double? Standby { get; set; }
        public double? Modified { get; set; }
        public double CommitUsed { get; set; }
        public double CommitLimit { get; set; }
        public int Speed { get; set; }
        public string Type { get; set; }
        public int Modules { get; set; }
        public int Slots { get; set; }
    }

    public sealed class DiskSnap
    {
        public string Letter { get; set; }
        public string Label { get; set; }
        public string Fs { get; set; }
        public double Total { get; set; }
        public double Free { get; set; }
        public double? Read { get; set; }
        public double? Write { get; set; }
        public double? Active { get; set; }
        public string Model { get; set; }
        public string Media { get; set; }
        public string Bus { get; set; }
        public double? Temp { get; set; }
        public double? Life { get; set; }
        public bool System { get; set; }
        public bool Removable { get; set; }
    }

    public sealed class NetSnap
    {
        public double? Down { get; set; }
        public double? Up { get; set; }
        public double? Ping { get; set; }
        public string Adapter { get; set; }
        public string Kind { get; set; }
        public long LinkMbps { get; set; }
    }

    public sealed class BatterySnap
    {
        public bool Present { get; set; }
        public double Pct { get; set; }
        public bool Plugged { get; set; }
        public bool Charging { get; set; }
        public double? RateW { get; set; }
        public int? MinutesLeft { get; set; }
        public double? Wear { get; set; }
    }

    public sealed class Snapshot
    {
        public long T { get; set; }
        public CpuSnap Cpu { get; set; }
        public List<GpuSnap> Gpus { get; set; }
        public RamSnap Ram { get; set; }
        public List<DiskSnap> Disks { get; set; }
        public NetSnap Net { get; set; }
        public BatterySnap Battery { get; set; }
        public object System { get; set; }
        public object Boost { get; set; }
        public object Game { get; set; }
        public object Tune { get; set; }
    }

    sealed class Ring
    {
        readonly float[] buf; int start, count;
        public Ring(int cap) { buf = new float[cap]; }
        public void Push(double? v)
        {
            var f = v.HasValue ? (float)v.Value : float.NaN;
            if (count < buf.Length) { buf[(start + count) % buf.Length] = f; count++; }
            else { buf[start] = f; start = (start + 1) % buf.Length; }
        }
        public float?[] Last(int n)
        {
            n = Math.Min(n, count);
            var r = new float?[n];
            for (int i = 0; i < n; i++) { var x = buf[(start + count - n + i) % buf.Length]; r[i] = float.IsNaN(x) ? (float?)null : (float)Math.Round(x, 2); }
            return r;
        }
    }

    /// <summary>Coleta os sensores 1x por segundo, guarda 15 minutos de histórico e envia para a interface.</summary>
    public static class MonitorService
    {
        const int HistorySize = 900;
        static Thread thread;
        static volatile bool running;
        static Pdh pdh;
        static List<D3dkmt.Adapter> adapters = new List<D3dkmt.Adapter>();
        static readonly Dictionary<string, Ring> history = new Dictionary<string, Ring>();
        static readonly List<long> times = new List<long>();
        public static Snapshot Last { get; private set; }
        static readonly List<double> tzSeen = new List<double>();
        static Dictionary<string, int> powerStates = new Dictionary<string, int>();
        static DateTime powerStatesAt, netInfoAt, storageAt, lastCpuAlert, lastGpuAlert, lastAutoRam;
        static NetSnap netInfo = new NetSnap();
        static volatile float pingMs = float.NaN;
        static int tick;
        static readonly Dictionary<string, (double? temp, double? life)> storageCache = new Dictionary<string, (double?, double?)>();

        static readonly string[] Series = { "cpuTemp", "cpuLoad", "cpuPower", "cpuClock", "gpuTemp", "gpuLoad", "gpuPower", "ramPct", "netDown", "netUp", "ping", "fps", "diskActive" };

        public static void Start()
        {
            if (running) return;
            running = true;
            foreach (var s in Series) history[s] = new Ring(HistorySize);
            thread = new Thread(Loop) { IsBackground = true, Name = "RAGE-Monitor", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            new Thread(PingLoop) { IsBackground = true, Name = "RAGE-Ping" }.Start();
        }

        public static void Stop() { running = false; }

        static void Init()
        {
            HwInfo.QuickCpu();
            pdh = new Pdh();
            pdh.Add("cpu", @"\Processor Information(_Total)\% Processor Utility");
            pdh.Add("perf", @"\Processor Information(_Total)\% Processor Performance");
            pdh.Add("freq", @"\Processor Information(_Total)\Processor Frequency");
            pdh.Add("cores", @"\Processor Information(*)\% Processor Utility");
            pdh.Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
            pdh.Add("vram", @"\GPU Adapter Memory(*)\Dedicated Usage");
            pdh.Add("shared", @"\GPU Adapter Memory(*)\Shared Usage");
            pdh.Add("dr", @"\LogicalDisk(*)\Disk Read Bytes/sec");
            pdh.Add("dw", @"\LogicalDisk(*)\Disk Write Bytes/sec");
            pdh.Add("didle", @"\LogicalDisk(*)\% Idle Time");
            pdh.Add("nr", @"\Network Interface(*)\Bytes Received/sec");
            pdh.Add("ns", @"\Network Interface(*)\Bytes Sent/sec");
            pdh.Add("sb0", @"\Memory\Standby Cache Core Bytes");
            pdh.Add("sb1", @"\Memory\Standby Cache Normal Priority Bytes");
            pdh.Add("sb2", @"\Memory\Standby Cache Reserve Bytes");
            pdh.Add("mod", @"\Memory\Modified Page List Bytes");
            pdh.Add("tz", @"\Thermal Zone Information(*)\Temperature");
            pdh.Collect();
            try
            {
                adapters = D3dkmt.Enumerate().Where(a => !a.Software && a.RenderSupported && !string.IsNullOrEmpty(a.Name)
                    && a.Name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) < 0
                    && a.Name.IndexOf("Remote", StringComparison.OrdinalIgnoreCase) < 0).ToList();
                // sem bit de tipo: decide pelo fabricante
                foreach (var a in adapters.Where(x => !x.Discrete && !x.Integrated))
                {
                    if (a.Name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 && a.Name.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) < 0) a.Integrated = true;
                    else a.Discrete = true;
                }
            }
            catch (Exception ex) { Log.Warn("D3DKMT: " + ex.Message); }
            // WMI e LibreHardwareMonitor demoram alguns segundos: carregam em paralelo enquanto o monitor já funciona.
            new Thread(() =>
            {
                try { HwInfo.Load(); } catch (Exception ex) { Log.Warn("HwInfo: " + ex.Message); }
                try { Sensors.Open(); } catch (Exception ex) { Log.Warn("Sensores: " + ex.Message); }
            }) { IsBackground = true, Name = "RAGE-SensorsInit" }.Start();
        }

        static void Loop()
        {
            try { Init(); } catch (Exception ex) { Log.Error("Monitor init", ex); }
            var sw = Stopwatch.StartNew();
            long next = 0;
            while (running)
            {
                try { Sample(); }
                catch (Exception ex) { Log.Error("Monitor", ex); }
                next += 1000;
                var wait = next - sw.ElapsedMilliseconds;
                if (wait < 0) { next = sw.ElapsedMilliseconds; wait = 0; }
                Thread.Sleep((int)Math.Max(50, wait));
            }
            try { Sensors.Close(); pdh?.Dispose(); } catch { }
        }

        static void PingLoop()
        {
            using (var p = new Ping())
                while (running)
                {
                    try
                    {
                        var r = p.Send("1.1.1.1", 1500);
                        pingMs = r.Status == IPStatus.Success ? r.RoundtripTime : float.NaN;
                    }
                    catch { pingMs = float.NaN; }
                    Thread.Sleep(3000);
                }
        }

        static void Sample()
        {
            tick++;
            pdh.Collect();
            var now = DateTime.Now;

            // ---------- CPU ----------
            Sensors.Update(Sensors.Cpu);
            var cpu = new CpuSnap
            {
                Name = HwInfo.CpuName, Cores = HwInfo.Cores, Threads = HwInfo.Threads, BaseClock = HwInfo.BaseMHz,
                Load = Clamp(pdh.Get("cpu")),
            };
            var perf = pdh.Get("perf", true);
            var freq = pdh.Get("freq") ?? HwInfo.BaseMHz;
            if (perf.HasValue && freq > 0) cpu.Clock = Math.Round(freq * perf.Value / 100.0);
            var cores = pdh.GetArray("cores", false)
                .Where(k => !k.Key.Contains("_Total"))
                .OrderBy(k => CoreOrder(k.Key)).Select(k => Math.Round(Math.Min(100, k.Value), 1)).ToArray();
            cpu.PerCore = cores;
            if (Sensors.Cpu != null)
            {
                var t = Sensors.Find(Sensors.Cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core Max", "CPU Cores", "Core Average")
                        ?? Sensors.Max(Sensors.Cpu, SensorType.Temperature, s => !s.Name.Contains("Distance"));
                if (t.HasValue && t > 0) { cpu.Temp = Math.Round(t.Value, 1); cpu.TempSource = "sensor"; Sensors.CpuTempSeen = true; }
                var pw = Sensors.Find(Sensors.Cpu, SensorType.Power, "CPU Package", "Package");
                if (pw.HasValue && pw > 0) cpu.Power = Math.Round(pw.Value, 1);
                cpu.CoreTemps = Sensors.All(Sensors.Cpu).Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("Core #") && !s.Name.Contains("Distance") && s.Value.HasValue)
                    .Select(s => Math.Round((double)s.Value.Value, 0)).ToArray();
            }
            if (cpu.Temp == null)
            {
                // Zona térmica ACPI: só usamos se o valor variar (em muitos notebooks é fixo e não reflete a CPU).
                var tz = pdh.GetArray("tz").Values.Where(v => v > 200).Select(v => v - 273.15).ToList();
                if (tz.Count > 0)
                {
                    var v = Math.Round(tz.Max(), 1);
                    if (tzSeen.Count < 600) tzSeen.Add(v);
                    if (tzSeen.Distinct().Count() > 2) { cpu.Temp = v; cpu.TempSource = "acpi"; }
                }
            }

            // ---------- GPUs ----------
            var gpus = SampleGpus(now);

            // ---------- RAM ----------
            var m = Native.Memory();
            Native.GetPerformanceInfo(out var pi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION)));
            var page = (double)pi.PageSize.ToUInt64();
            var standby = Sum(pdh.Get("sb0"), pdh.Get("sb1"), pdh.Get("sb2"));
            var ram = new RamSnap
            {
                Total = m.ullTotalPhys, Avail = m.ullAvailPhys, Used = m.ullTotalPhys - m.ullAvailPhys,
                Pct = Math.Round((m.ullTotalPhys - m.ullAvailPhys) * 100.0 / Math.Max(1, m.ullTotalPhys), 1),
                Standby = standby, Modified = pdh.Get("mod"),
                CommitUsed = pi.CommitTotal.ToUInt64() * page, CommitLimit = pi.CommitLimit.ToUInt64() * page,
                Speed = HwInfo.RamSpeed, Type = HwInfo.RamType, Modules = HwInfo.RamModules, Slots = HwInfo.RamSlots,
            };

            // ---------- Discos ----------
            if ((now - HwInfo.DisksAt).TotalSeconds > 120) HwInfo.LoadDisks();
            if ((now - storageAt).TotalSeconds >= 15) { storageAt = now; SampleStorage(); }
            var disks = SampleDisks();

            // ---------- Rede ----------
            if ((now - netInfoAt).TotalSeconds >= 20) { netInfoAt = now; RefreshNetInfo(); }
            var net = new NetSnap
            {
                Down = pdh.GetArray("nr").Where(k => !k.Key.Contains("isatap") && !k.Key.Contains("Loopback")).Sum(k => k.Value),
                Up = pdh.GetArray("ns").Where(k => !k.Key.Contains("isatap") && !k.Key.Contains("Loopback")).Sum(k => k.Value),
                Ping = float.IsNaN(pingMs) ? (double?)null : pingMs,
                Adapter = netInfo.Adapter, Kind = netInfo.Kind, LinkMbps = netInfo.LinkMbps,
            };

            // ---------- Bateria ----------
            BatterySnap bat = null;
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown)
            {
                if (tick % 5 == 1) Sensors.Update(Sensors.Battery);
                bat = new BatterySnap
                {
                    Present = true,
                    Pct = Math.Round(ps.BatteryLifePercent * 100),
                    Plugged = ps.PowerLineStatus == PowerLineStatus.Online,
                    Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0,
                    MinutesLeft = ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining / 60 : (int?)null,
                };
                if (Sensors.Battery != null)
                {
                    bat.RateW = Sensors.Find(Sensors.Battery, SensorType.Power, "Charge/Discharge Rate", "Discharge Rate", "Charge Rate");
                    bat.Wear = Sensors.Find(Sensors.Battery, SensorType.Level, "Degradation Level");
                }
            }

            var snap = new Snapshot
            {
                T = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Cpu = cpu, Gpus = gpus, Ram = ram, Disks = disks, Net = net, Battery = bat,
                System = new { processes = pi.ProcessCount, threads = pi.ThreadCount, handles = pi.HandleCount, uptime = (long)(Native.GetTickCount64() / 1000) },
                Boost = Boost.BoostService.Status(),
                Game = Fps.Current(),
                Tune = Boost.Tuning.Lite(),
            };
            Last = snap;

            // ---------- Histórico ----------
            var g = PrimaryGpu(gpus);
            lock (history)
            {
                times.Add(snap.T); if (times.Count > HistorySize) times.RemoveAt(0);
                history["cpuTemp"].Push(cpu.Temp);
                history["cpuLoad"].Push(cpu.Load);
                history["cpuPower"].Push(cpu.Power);
                history["cpuClock"].Push(cpu.Clock);
                history["gpuTemp"].Push(g?.Temp);
                history["gpuLoad"].Push(g?.Load);
                history["gpuPower"].Push(g?.Power);
                history["ramPct"].Push(ram.Pct);
                history["netDown"].Push(net.Down);
                history["netUp"].Push(net.Up);
                history["ping"].Push(net.Ping);
                history["fps"].Push((snap.Game as Fps.GameInfo)?.Fps);
                history["diskActive"].Push(disks.FirstOrDefault(d => d.System)?.Active);
            }

            if (Bridge.AnyVisible) Bridge.EmitVisible("telemetry", snap);
            Overlay.Update(snap);
            MiniMonitor.Update(snap);
            Alerts(snap, g, now);
        }

        static int CoreOrder(string inst)
        {
            var parts = inst.Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) ? a * 1000 + b : 99999;
        }

        public static GpuSnap PrimaryGpu(List<GpuSnap> gpus) =>
            gpus?.FirstOrDefault(x => x.Kind == "discrete") ?? gpus?.FirstOrDefault();

        static List<GpuSnap> SampleGpus(DateTime now)
        {
            var res = new List<GpuSnap>();
            if ((now - powerStatesAt).TotalSeconds >= 2) { powerStatesAt = now; try { powerStates = DevicePower.DisplayStates(); } catch { } }
            var eng = pdh.GetArray("gpu");
            // Utilização por adaptador = maior engine (soma dos processos), como no Gerenciador de Tarefas
            var perEngine = new Dictionary<string, double>();
            foreach (var kv in eng)
            {
                var luid = Between(kv.Key, "luid_", "_phys");
                var engIdx = After(kv.Key, "_eng_");
                if (luid == null || engIdx == null) continue;
                var key = luid + "|" + engIdx;
                perEngine[key] = (perEngine.TryGetValue(key, out var v) ? v : 0) + kv.Value;
            }
            var vram = pdh.GetArray("vram");
            var shared = pdh.GetArray("shared");

            foreach (var a in adapters)
            {
                var luidKey = a.Luid.ToLowerInvariant();
                var load = perEngine.Where(k => k.Key.StartsWith(luidKey + "|", StringComparison.OrdinalIgnoreCase)).Select(k => k.Value).DefaultIfEmpty(0).Max();
                var gs = new GpuSnap
                {
                    Id = a.Luid, Name = a.Name, Kind = a.Discrete ? "discrete" : "integrated",
                    Vendor = a.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ? "nvidia" : a.Name.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || a.Name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ? "amd" : a.Name.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 ? "intel" : "other",
                    Load = Math.Round(Math.Min(100, load), 1),
                    VramUsed = vram.Where(k => k.Key.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase) >= 0).Sum(k => k.Value),
                    SharedUsed = shared.Where(k => k.Key.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase) >= 0).Sum(k => k.Value),
                    MaxTemp = a.MaxTemp,
                    State = "active",
                };
                var nm = Sensors.Norm(a.Name);
                var ps = powerStates.FirstOrDefault(k => Sensors.Norm(k.Key) == nm);
                bool asleep = ps.Key != null && ps.Value >= 4 && a.Discrete;
                if (asleep) gs.State = "sleep";
                if (HwInfo.VramByName.TryGetValue(a.Name, out var total)) gs.VramTotal = total;

                var perfData = D3dkmt.QueryPerf(a.Handle);
                if (perfData != null && !asleep)
                {
                    gs.Temp = perfData.TempC;
                    gs.Fan = perfData.FanRpm;
                    gs.MemClock = perfData.MemClockMHz;
                }

                var hw = Sensors.Gpus.FirstOrDefault(h => Sensors.Norm(h.Name) == nm) ?? Sensors.Gpus.FirstOrDefault(h => nm.Contains(Sensors.Norm(h.Name)) || Sensors.Norm(h.Name).Contains(nm));
                if (hw != null && !asleep)
                {
                    Sensors.Update(hw);
                    var t = Sensors.Find(hw, SensorType.Temperature, "GPU Core", "GPU Temperature", "GPU VR SoC", "GPU");
                    if (t.HasValue && t > 0) gs.Temp = Math.Round(t.Value, 1);
                    gs.HotSpot = Sensors.Find(hw, SensorType.Temperature, "GPU Hot Spot");
                    gs.Clock = Sensors.Find(hw, SensorType.Clock, "GPU Core", "GPU Shader");
                    gs.MemClock = Sensors.Find(hw, SensorType.Clock, "GPU Memory") ?? gs.MemClock;
                    gs.Power = Sensors.Find(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU Core", "GPU Board Power");
                    var fan = Sensors.Find(hw, SensorType.Fan, "GPU Fan", "GPU Fan 1", "GPU");
                    if (fan.HasValue && fan > 0) gs.Fan = fan;
                    gs.FanPct = Sensors.Find(hw, SensorType.Control, "GPU Fan", "GPU Fan 1");
                    var mt = Sensors.Find(hw, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                    if (mt.HasValue && mt > 0 && gs.VramTotal == null) gs.VramTotal = mt.Value * 1024 * 1024;
                    if (gs.Power.HasValue) gs.Power = Math.Round(gs.Power.Value, 1);
                    if (gs.Clock.HasValue) gs.Clock = Math.Round(gs.Clock.Value);
                    if (gs.MemClock.HasValue) gs.MemClock = Math.Round(gs.MemClock.Value);
                }
                res.Add(gs);
            }
            return res.OrderBy(x => x.Kind == "discrete" ? 0 : 1).ToList();
        }

        static void SampleStorage()
        {
            foreach (var hw in Sensors.Storages)
            {
                Sensors.Update(hw);
                var t = Sensors.Find(hw, SensorType.Temperature, "Temperature", "Composite Temperature", "Temperature 1");
                var life = Sensors.Find(hw, SensorType.Level, "Remaining Life", "Life");
                if (life == null)
                {
                    var used = Sensors.Find(hw, SensorType.Level, "Percentage Used");
                    if (used.HasValue) life = Math.Max(0, 100 - used.Value);
                }
                storageCache[Sensors.Norm(hw.Name)] = (t, life);
            }
        }

        static List<DiskSnap> SampleDisks()
        {
            var list = new List<DiskSnap>();
            var dr = pdh.GetArray("dr"); var dw = pdh.GetArray("dw"); var idle = pdh.GetArray("didle");
            var sysLetter = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)[0]);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                    var letter = char.ToUpperInvariant(d.Name[0]);
                    var inst = letter + ":";
                    var phys = HwInfo.Disks.FirstOrDefault(p => p.Letters.Contains(letter));
                    var ds = new DiskSnap
                    {
                        Letter = inst, Label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "Removível" : "Disco local") : d.VolumeLabel,
                        Fs = d.DriveFormat, Total = d.TotalSize, Free = d.AvailableFreeSpace,
                        Read = dr.TryGetValue(inst, out var r) ? r : (double?)null,
                        Write = dw.TryGetValue(inst, out var w) ? w : (double?)null,
                        Active = idle.TryGetValue(inst, out var i) ? Math.Round(Math.Max(0, Math.Min(100, 100 - i)), 1) : (double?)null,
                        Model = phys?.Model, Media = phys?.Media, Bus = phys?.Bus,
                        System = letter == sysLetter, Removable = d.DriveType == DriveType.Removable,
                    };
                    if (phys != null)
                    {
                        var pn = Sensors.Norm(phys.Model);
                        var hit = storageCache.FirstOrDefault(k => k.Key == pn || k.Key.Contains(pn) || pn.Contains(k.Key));
                        if (hit.Key != null) { ds.Temp = hit.Value.temp; ds.Life = hit.Value.life; }
                    }
                    list.Add(ds);
                }
                catch { }
            }
            return list.OrderBy(x => x.System ? 0 : 1).ThenBy(x => x.Letter).ToList();
        }

        static void RefreshNetInfo()
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .Where(n => n.GetIPProperties().GatewayAddresses.Any(gw => gw.Address != null && !gw.Address.ToString().StartsWith("0.")))
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                    .FirstOrDefault();
                if (nic == null) { netInfo = new NetSnap { Adapter = null, Kind = "offline" }; return; }
                netInfo = new NetSnap
                {
                    Adapter = nic.Name,
                    Kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet",
                    LinkMbps = nic.Speed > 0 ? nic.Speed / 1000000 : 0,
                };
            }
            catch { }
        }

        static void Alerts(Snapshot s, GpuSnap g, DateTime now)
        {
            var st = Settings.Current;
            if (st.TempAlerts)
            {
                if (s.Cpu.Temp >= st.CpuTempAlert && (now - lastCpuAlert).TotalMinutes > 5)
                {
                    lastCpuAlert = now;
                    MainForm.Instance?.Notify("CPU muito quente: " + s.Cpu.Temp?.ToString("0") + " °C", "Verifique a ventilação. Limite configurado: " + st.CpuTempAlert + " °C.", ToolTipIcon.Warning);
                    Activity.Add("warn", "device_thermostat", "Alerta de temperatura da CPU", s.Cpu.Temp?.ToString("0") + " °C");
                }
                if (g?.Temp >= st.GpuTempAlert && (now - lastGpuAlert).TotalMinutes > 5)
                {
                    lastGpuAlert = now;
                    MainForm.Instance?.Notify("GPU muito quente: " + g.Temp?.ToString("0") + " °C", "Limite configurado: " + st.GpuTempAlert + " °C.", ToolTipIcon.Warning);
                    Activity.Add("warn", "device_thermostat", "Alerta de temperatura da GPU", g.Temp?.ToString("0") + " °C");
                }
            }
            if (st.AutoRamClean && s.Ram.Pct >= st.AutoRamThreshold && (now - lastAutoRam).TotalMinutes > 5)
            {
                lastAutoRam = now;
                System.Threading.Tasks.Task.Run(() =>
                {
                    var r = Boost.RamCleaner.Clean(false);
                    Activity.Add("clean", "memory", "Limpeza automática de RAM", "RAM acima de " + st.AutoRamThreshold + "%", r.FreedText);
                });
            }
        }

        static double? Clamp(double? v) => v.HasValue ? Math.Round(Math.Max(0, Math.Min(100, v.Value)), 1) : (double?)null;
        static double? Sum(params double?[] v) => v.All(x => x == null) ? (double?)null : v.Sum(x => x ?? 0);
        static string Between(string s, string a, string b) { var i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return null; i += a.Length; var j = s.IndexOf(b, i, StringComparison.Ordinal); return j < 0 ? null : s.Substring(i, j - i); }
        static string After(string s, string a) { var i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return null; i += a.Length; var j = s.IndexOf('_', i); return j < 0 ? s.Substring(i) : s.Substring(i, j - i); }

        // ---------- RPC ----------
        [Rpc("monitor.snapshot")]
        public static object GetSnapshot() => Last;

        [Rpc("monitor.history")]
        public static object History(int? seconds)
        {
            var n = Math.Min(HistorySize, Math.Max(10, seconds ?? HistorySize));
            lock (history)
            {
                var res = new Dictionary<string, object>();
                foreach (var kv in history) res[kv.Key] = kv.Value.Last(n);
                res["t"] = times.Skip(Math.Max(0, times.Count - n)).ToArray();
                return res;
            }
        }

        [Rpc("monitor.sensors")]
        public static object AllSensors()
        {
            var list = new List<object>();
            foreach (var hw in new[] { Sensors.Cpu }.Concat(Sensors.Gpus).Concat(Sensors.Storages).Concat(new[] { Sensors.Battery }).Where(h => h != null))
                list.Add(new
                {
                    name = hw.Name,
                    type = hw.HardwareType.ToString(),
                    sensors = Sensors.All(hw).Where(s => s.Value.HasValue).Select(s => new { name = s.Name, type = s.SensorType.ToString(), value = Math.Round((double)s.Value.Value, 2), min = s.Min, max = s.Max }).ToList(),
                });
            return new { driver = Sensors.DriverStatus(), hardware = list };
        }
    }
}
