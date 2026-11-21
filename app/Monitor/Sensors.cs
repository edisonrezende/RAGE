using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Monitor
{
    /// <summary>Sensores via LibreHardwareMonitor (temperaturas e consumo reais exigem o driver PawnIO + administrador).</summary>
    public static class Sensors
    {
        static readonly object Gate = new object();
        static Computer computer;
        public static IHardware Cpu { get; private set; }
        public static List<IHardware> Gpus { get; private set; } = new List<IHardware>();
        public static List<IHardware> Storages { get; private set; } = new List<IHardware>();
        public static IHardware Battery { get; private set; }
        public static bool Ready { get; private set; }
        public static bool CpuTempSeen;

        public sealed class DriverInfo
        {
            public bool Installed { get; set; }
            public string Version { get; set; }
            public bool Admin { get; set; }
            public bool CpuTemp { get; set; }
            public bool Ready { get; set; }
        }

        public static DriverInfo DriverStatus()
        {
            var d = new DriverInfo { Admin = Program.IsAdmin, CpuTemp = CpuTempSeen, Ready = Ready };
            try { d.Installed = LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; d.Version = LibreHardwareMonitor.PawnIo.PawnIo.Version?.ToString(); }
            catch { }
            return d;
        }

        public static void Open()
        {
            lock (Gate)
            {
                if (computer != null) return;
                try
                {
                    var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsStorageEnabled = true, IsBatteryEnabled = true };
                    c.Open();
                    computer = c;
                    Cpu = c.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    Gpus = c.Hardware.Where(h => h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel).ToList();
                    Storages = c.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
                    Battery = c.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
                    Ready = true;
                    Log.Info("Sensores: CPU=" + Cpu?.Name + " GPUs=" + string.Join(", ", Gpus.Select(g => g.Name)) + " discos=" + Storages.Count + " PawnIO=" + DriverStatus().Installed);
                }
                catch (Exception ex) { Log.Error("LibreHardwareMonitor", ex); Ready = false; }
            }
        }

        public static void Close()
        {
            lock (Gate)
            {
                try { computer?.Close(); } catch { }
                computer = null; Cpu = null; Gpus = new List<IHardware>(); Storages = new List<IHardware>(); Battery = null; Ready = false;
            }
        }

        public static void Reset() { Close(); Open(); }

        public static void Update(IHardware hw)
        {
            if (hw == null) return;
            lock (Gate)
            {
                try { hw.Update(); foreach (var s in hw.SubHardware) s.Update(); } catch { }
            }
        }

        public static IEnumerable<ISensor> All(IHardware hw) => hw == null ? Enumerable.Empty<ISensor>() : hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors));

        public static double? Find(IHardware hw, SensorType type, params string[] names)
        {
            var list = All(hw).Where(s => s.SensorType == type && s.Value.HasValue && !float.IsNaN(s.Value.Value)).ToList();
            foreach (var n in names)
            {
                var s = list.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                if (s != null) return s.Value;
            }
            return null;
        }

        public static double? Max(IHardware hw, SensorType type, Func<ISensor, bool> filter = null)
        {
            var v = All(hw).Where(s => s.SensorType == type && s.Value.HasValue && (filter == null || filter(s))).Select(s => (double)s.Value.Value).ToList();
            return v.Count == 0 ? (double?)null : v.Max();
        }

        public static string Norm(string s)
        {
            var t = (s ?? "").Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("®", "").Replace("™", "");
            return new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }
    }

    /// <summary>Informações fixas do hardware (lidas uma vez via WMI/registro).</summary>
    public static class HwInfo
    {
        public static string CpuName = "Processador";
        public static int Cores, Threads;
        public static double BaseMHz;
        public static int RamSpeed;
        public static string RamType;
        public static int RamModules, RamSlots;
        public static readonly Dictionary<string, ulong> VramByName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        public static List<PhysDisk> Disks = new List<PhysDisk>();
        public static DateTime DisksAt;

        public sealed class PhysDisk
        {
            public int Number; public string Model; public string Media; public string Bus; public ulong Size; public List<char> Letters = new List<char>();
        }

        /// <summary>Dados da CPU pelo registro (instantâneo) para o monitor começar sem esperar o WMI.</summary>
        public static void QuickCpu()
        {
            try
            {
                var name = Reg.GetStr(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
                if (!string.IsNullOrEmpty(name)) CpuName = CleanCpu(name.Trim());
                var mhz = Reg.GetInt(@"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz");
                if (mhz.HasValue && BaseMHz == 0) BaseMHz = mhz.Value;
                if (Threads == 0) Threads = Environment.ProcessorCount;
                DisksAt = DateTime.Now; // a lista de discos vem do Load() em segundo plano
            }
            catch { }
        }

        public static void Load()
        {
            try
            {
                int cores = 0, threads = 0;
                using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
                    foreach (ManagementObject o in s.Get())
                    {
                        CpuName = CleanCpu((o["Name"] as string ?? "").Trim());
                        cores += Convert.ToInt32(o["NumberOfCores"]);
                        threads += Convert.ToInt32(o["NumberOfLogicalProcessors"]);
                        BaseMHz = Convert.ToDouble(o["MaxClockSpeed"]);
                    }
                Cores = cores; Threads = threads;
            }
            catch (Exception ex) { Log.Warn("WMI CPU: " + ex.Message); }
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                    foreach (ManagementObject o in s.Get())
                    {
                        RamModules++;
                        var sp = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0);
                        if (sp == 0) sp = Convert.ToInt32(o["Speed"] ?? 0);
                        RamSpeed = Math.Max(RamSpeed, sp);
                        var t = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0);
                        RamType = t == 26 ? "DDR4" : t == 34 ? "DDR5" : t == 24 ? "DDR3" : t == 30 ? "LPDDR4" : t == 35 ? "LPDDR5" : RamType;
                    }
                using (var s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                    foreach (ManagementObject o in s.Get()) RamSlots += Convert.ToInt32(o["MemoryDevices"] ?? 0);
            }
            catch (Exception ex) { Log.Warn("WMI RAM: " + ex.Message); }
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (cls != null)
                        foreach (var n in cls.GetSubKeyNames())
                        {
                            if (!int.TryParse(n, out _)) continue;
                            try
                            {
                                using (var k = cls.OpenSubKey(n))
                                {
                                    var desc = k?.GetValue("DriverDesc") as string;
                                    var mem = k?.GetValue("HardwareInformation.qwMemorySize");
                                    ulong size = 0;
                                    if (mem is long l) size = (ulong)l;
                                    else if (mem is byte[] b && b.Length >= 8) size = BitConverter.ToUInt64(b, 0);
                                    else if (mem is byte[] b4 && b4.Length >= 4) size = BitConverter.ToUInt32(b4, 0);
                                    if (size == 0 && k?.GetValue("HardwareInformation.MemorySize") is byte[] ms && ms.Length >= 4) size = BitConverter.ToUInt32(ms, 0);
                                    if (desc != null && size > 0) VramByName[desc] = size;
                                }
                            }
                            catch { }
                        }
            }
            catch { }
            LoadDisks();
        }

        static string CleanCpu(string n) => n.Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "").Replace("  ", " ").Trim();

        public static void LoadDisks()
        {
            var list = new List<PhysDisk>();
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject o in s.Get())
                    {
                        var media = Convert.ToInt32(o["MediaType"] ?? 0);
                        var bus = Convert.ToInt32(o["BusType"] ?? 0);
                        list.Add(new PhysDisk
                        {
                            Number = int.TryParse(o["DeviceId"] as string, out var num) ? num : -1,
                            Model = (o["FriendlyName"] as string ?? "Disco").Trim(),
                            Media = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : (bus == 17 ? "SSD" : "Disco"),
                            Bus = bus == 17 ? "NVMe" : bus == 11 ? "SATA" : bus == 7 ? "USB" : bus == 8 ? "RAID" : bus == 3 ? "ATA" : bus == 10 ? "SAS" : "",
                            Size = Convert.ToUInt64(o["Size"] ?? 0UL),
                        });
                    }
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition")))
                    foreach (ManagementObject o in s.Get())
                    {
                        var letter = Convert.ToChar(o["DriveLetter"] ?? '\0');
                        if (letter == '\0') continue;
                        var dn = Convert.ToInt32(o["DiskNumber"]);
                        list.FirstOrDefault(d => d.Number == dn)?.Letters.Add(char.ToUpperInvariant(letter));
                    }
            }
            catch (Exception ex) { Log.Warn("WMI discos: " + ex.Message); }
            Disks = list;
            DisksAt = DateTime.Now;
        }
    }
}
