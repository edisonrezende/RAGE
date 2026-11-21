using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Informações do sistema (WMI + registro) e relatório em texto.</summary>
    public static class SysInfo
    {
        static readonly object Gate = new object();
        static Info cached;
        static DateTime cachedAt;

        public sealed class Os { public string Name { get; set; } public string Version { get; set; } public string Build { get; set; } public DateTime? InstallDate { get; set; } public long UptimeSec { get; set; } public string User { get; set; } public string Machine { get; set; } public string Arch { get; set; } }
        public sealed class Cpu { public string Name { get; set; } public int Cores { get; set; } public int Threads { get; set; } public double BaseMHz { get; set; } public string Socket { get; set; } public double? L2 { get; set; } public double? L3 { get; set; } public bool? Virtualization { get; set; } }
        public sealed class Gpu { public string Name { get; set; } public string Driver { get; set; } public DateTime? DriverDate { get; set; } public double? Vram { get; set; } public string Resolution { get; set; } public int? Refresh { get; set; } }
        public sealed class Module { public string Slot { get; set; } public double Size { get; set; } public int? Speed { get; set; } public string Manufacturer { get; set; } public string Part { get; set; } public string Type { get; set; } }
        public sealed class Ram { public double Total { get; set; } public List<Module> Modules { get; set; } = new List<Module>(); public int? Slots { get; set; } }
        public sealed class Board { public string Manufacturer { get; set; } public string Product { get; set; } public string Bios { get; set; } public DateTime? BiosDate { get; set; } }
        public sealed class Disk { public string Model { get; set; } public double Size { get; set; } public string Media { get; set; } public string Bus { get; set; } public string Health { get; set; } }
        public sealed class Net { public string Name { get; set; } public string Type { get; set; } public string Mac { get; set; } public string Ip { get; set; } public double? Speed { get; set; } }
        public sealed class Battery { public double? Design { get; set; } public double? Full { get; set; } public double? WearPct { get; set; } public string Chemistry { get; set; } }
        public sealed class Info
        {
            public Os Os { get; set; } public Cpu Cpu { get; set; } public List<Gpu> Gpus { get; set; } public Ram Ram { get; set; } public Board Board { get; set; }
            public List<Disk> Disks { get; set; } public List<Net> Network { get; set; } public Battery Battery { get; set; }
        }

        static IEnumerable<ManagementObject> Query(string wql, string scope = null)
        {
            var list = new List<ManagementObject>();
            try
            {
                using (var s = scope == null ? new ManagementObjectSearcher(wql) : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql)))
                    foreach (ManagementObject o in s.Get()) list.Add(o);
            }
            catch (Exception ex) { Log.Warn("WMI '" + wql + "': " + ex.Message); }
            return list;
        }

        public static string CleanCpu(string n) =>
            System.Text.RegularExpressions.Regex.Replace((n ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Replace(" CPU", "").Replace("  ", " "), @"\s+", " ").Trim();

        static readonly Dictionary<string, string> Jedec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CE"] = "Samsung", ["AD"] = "SK hynix", ["2C"] = "Micron", ["9B"] = "Crucial", ["98"] = "Kingston", ["94"] = "Smart Modular", ["CD"] = "G.Skill",
            ["9E"] = "Corsair", ["EF"] = "Team Group", ["CB"] = "ADATA", ["43"] = "Ramaxel", ["51"] = "Qimonda", ["C1"] = "Infineon", ["FE"] = "Elpida",
            ["B0"] = "Patriot", ["F1"] = "Apacer", ["0B"] = "Nanya", ["4F"] = "Transcend", ["DA"] = "Netac",
        };

        static string RamMaker(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0 || s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || s.StartsWith("Undefined", StringComparison.OrdinalIgnoreCase)) return null;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^[0-9A-Fa-f]{4,16}$"))
            {
                // Código JEDEC: "80CE", "CE00...", "0194..." → banco + fabricante
                foreach (var pair in new[] { s.Length >= 4 ? s.Substring(2, 2) : null, s.Substring(0, 2) })
                    if (pair != null && Jedec.TryGetValue(pair, out var m)) return m;
                return s;
            }
            return s;
        }

        static string RamType(int t)
        {
            switch (t)
            {
                case 20: return "DDR"; case 21: return "DDR2"; case 24: return "DDR3"; case 26: return "DDR4";
                case 27: return "LPDDR"; case 28: return "LPDDR2"; case 29: return "LPDDR3"; case 30: return "LPDDR4"; case 34: return "DDR5"; case 35: return "LPDDR5";
                default: return null;
            }
        }

        static Dictionary<string, ulong> VramFromRegistry()
        {
            var res = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
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
                                    if (size == 0 && k?.GetValue("HardwareInformation.MemorySize") is byte[] ms && ms.Length >= 4) size = BitConverter.ToUInt32(ms, 0);
                                    if (size == 0 && k?.GetValue("HardwareInformation.MemorySize") is int mi) size = (uint)mi;
                                    if (desc != null && size > 0 && !res.ContainsKey(desc)) res[desc] = size;
                                }
                            }
                            catch { }
                        }
            }
            catch { }
            return res;
        }

        public static Info Get()
        {
            lock (Gate)
            {
                if (cached != null && (DateTime.Now - cachedAt).TotalSeconds < 20)
                {
                    cached.Os.UptimeSec = (long)(Native.GetTickCount64() / 1000);
                    return cached;
                }
            }
            var info = new Info();
            // Sistema
            const string nt = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            var product = Reg.GetStr(nt, "ProductName") ?? "Windows";
            var buildNum = Reg.GetStr(nt, "CurrentBuildNumber") ?? "";
            var ubr = Reg.GetInt(nt, "UBR");
            if (int.TryParse(buildNum, out var bn) && bn >= 22000) product = product.Replace("Windows 10", "Windows 11");
            DateTime? installed = null;
            var osObj = Query("SELECT Caption, InstallDate, OSArchitecture FROM Win32_OperatingSystem").FirstOrDefault();
            if (osObj != null) installed = ToolsUtil.WmiDate(osObj["InstallDate"]);
            if (installed == null) { var u = Reg.GetInt(nt, "InstallDate"); if (u.HasValue) installed = DateTimeOffset.FromUnixTimeSeconds((uint)u.Value).LocalDateTime; }
            var caption = ToolsUtil.Str(osObj?["Caption"]);
            info.Os = new Os
            {
                Name = caption != null ? caption.Replace("Microsoft ", "") : product,
                Version = Reg.GetStr(nt, "DisplayVersion") ?? Reg.GetStr(nt, "ReleaseId"),
                Build = buildNum + (ubr.HasValue ? "." + ubr : ""),
                InstallDate = installed,
                UptimeSec = (long)(Native.GetTickCount64() / 1000),
                User = Environment.UserName,
                Machine = Environment.MachineName,
                Arch = ToolsUtil.Str(osObj?["OSArchitecture"]),
            };

            // CPU
            var cpu = new Cpu();
            bool hyper = false;
            try { var cs = Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault(); hyper = cs != null && Convert.ToBoolean(cs["HypervisorPresent"] ?? false); } catch { }
            foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, SocketDesignation, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled FROM Win32_Processor"))
            {
                cpu.Name = CleanCpu(o["Name"] as string);
                cpu.Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                cpu.Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                cpu.BaseMHz = Convert.ToDouble(o["MaxClockSpeed"] ?? 0);
                cpu.Socket = ToolsUtil.Str(o["SocketDesignation"]);
                var l2 = Convert.ToDouble(o["L2CacheSize"] ?? 0); if (l2 > 0) cpu.L2 = l2 * 1024;
                var l3 = Convert.ToDouble(o["L3CacheSize"] ?? 0); if (l3 > 0) cpu.L3 = l3 * 1024;
                var v = o["VirtualizationFirmwareEnabled"];
                cpu.Virtualization = hyper ? true : v == null ? (bool?)null : Convert.ToBoolean(v);
            }
            if (cpu.Threads == 0) cpu.Threads = Environment.ProcessorCount;
            info.Cpu = cpu;

            // GPUs
            var vram = VramFromRegistry();
            info.Gpus = new List<Gpu>();
            foreach (var o in Query("SELECT Name, DriverVersion, DriverDate, AdapterRAM, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController"))
            {
                var name = ToolsUtil.Str(o["Name"]);
                if (name == null || name.IndexOf("Remote Display", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Basic Render", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                double? mem = vram.TryGetValue(name, out var vr) ? vr : (double?)null;
                if (mem == null) { var ar = Convert.ToDouble(o["AdapterRAM"] ?? 0); if (ar > 0) mem = ar; }
                var w = Convert.ToInt32(o["CurrentHorizontalResolution"] ?? 0); var h = Convert.ToInt32(o["CurrentVerticalResolution"] ?? 0);
                var hz = Convert.ToInt32(o["CurrentRefreshRate"] ?? 0);
                info.Gpus.Add(new Gpu
                {
                    Name = name,
                    Driver = ToolsUtil.Str(o["DriverVersion"]),
                    DriverDate = ToolsUtil.WmiDay(o["DriverDate"]),
                    Vram = mem,
                    Resolution = w > 0 && h > 0 ? w + " × " + h : null,
                    Refresh = hz > 1 ? hz : (int?)null,
                });
            }

            // RAM
            var ram = new Ram();
            foreach (var o in Query("SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory"))
            {
                var cap = Convert.ToDouble(o["Capacity"] ?? 0);
                var sp = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0); if (sp == 0) sp = Convert.ToInt32(o["Speed"] ?? 0);
                var slot = ToolsUtil.Str(o["DeviceLocator"]) ?? ToolsUtil.Str(o["BankLabel"]);
                ram.Modules.Add(new Module
                {
                    Slot = slot,
                    Size = cap,
                    Speed = sp > 0 ? sp : (int?)null,
                    Manufacturer = RamMaker(o["Manufacturer"] as string),
                    Part = ToolsUtil.Str(o["PartNumber"]),
                    Type = RamType(Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0)),
                });
            }
            var slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(o => Convert.ToInt32(o["MemoryDevices"] ?? 0));
            if (slots > 0) ram.Slots = slots;
            ram.Total = ram.Modules.Sum(m => m.Size);
            if (ram.Total <= 0) ram.Total = Native.Memory().ullTotalPhys;
            info.Ram = ram;

            // Placa-mãe
            var bb = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
            var bios = Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
            info.Board = new Board
            {
                Manufacturer = ToolsUtil.Str(bb?["Manufacturer"]),
                Product = ToolsUtil.Str(bb?["Product"]),
                Bios = ToolsUtil.Str(bios?["SMBIOSBIOSVersion"]),
                BiosDate = ToolsUtil.WmiDay(bios?["ReleaseDate"]),
            };

            // Discos
            info.Disks = new List<Disk>();
            foreach (var o in Query("SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk", @"\\.\root\Microsoft\Windows\Storage"))
            {
                var media = Convert.ToInt32(o["MediaType"] ?? 0); var bus = Convert.ToInt32(o["BusType"] ?? 0); var health = Convert.ToInt32(o["HealthStatus"] ?? 5);
                info.Disks.Add(new Disk
                {
                    Model = ToolsUtil.Str(o["FriendlyName"]) ?? "Disco",
                    Size = Convert.ToDouble(o["Size"] ?? 0),
                    Media = media == 4 ? "SSD" : media == 3 ? "HDD" : media == 5 ? "SCM" : bus == 17 ? "SSD" : "Desconhecido",
                    Bus = bus == 17 ? "NVMe" : bus == 11 ? "SATA" : bus == 7 ? "USB" : bus == 8 ? "RAID" : bus == 3 ? "ATA" : bus == 10 ? "SAS" : bus == 12 ? "SD" : bus == 13 ? "MMC" : null,
                    Health = health == 0 ? "Saudável" : health == 1 ? "Atenção" : health == 2 ? "Com defeito" : null,
                });
            }

            // Rede
            info.Network = new List<Net>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    info.Network.Add(new Net
                    {
                        Name = ni.Name + (string.IsNullOrEmpty(ni.Description) ? "" : " (" + ni.Description + ")"),
                        Type = Network.TypeName(ni.NetworkInterfaceType),
                        Mac = Network.Mac(ni),
                        Ip = ip,
                        Speed = ni.Speed > 0 ? ni.Speed / 1e6 : (double?)null,
                    });
                }
            }
            catch (Exception ex) { Log.Warn("Rede (sysinfo): " + ex.Message); }

            // Bateria
            var bat = Query("SELECT Chemistry, DesignCapacity, FullChargeCapacity FROM Win32_Battery").FirstOrDefault();
            if (bat != null)
            {
                double? design = null, full = null;
                var sd = Query("SELECT DesignedCapacity FROM BatteryStaticData", @"\\.\root\WMI").FirstOrDefault();
                var fc = Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", @"\\.\root\WMI").FirstOrDefault();
                if (sd != null) design = Convert.ToDouble(sd["DesignedCapacity"] ?? 0);
                if (fc != null) full = Convert.ToDouble(fc["FullChargedCapacity"] ?? 0);
                if (!(design > 0)) { var d = Convert.ToDouble(bat["DesignCapacity"] ?? 0); design = d > 0 ? d : (double?)null; }
                if (!(full > 0)) { var f = Convert.ToDouble(bat["FullChargeCapacity"] ?? 0); full = f > 0 ? f : (double?)null; }
                var chem = Convert.ToInt32(bat["Chemistry"] ?? 2);
                info.Battery = new Battery
                {
                    Design = design, Full = full,
                    WearPct = design > 0 && full > 0 ? Math.Round(Math.Max(0, (1 - full.Value / design.Value) * 100), 1) : (double?)null,
                    Chemistry = chem == 3 ? "Chumbo-ácido" : chem == 4 ? "Níquel-cádmio" : chem == 5 ? "Níquel-hidreto metálico" : chem == 6 ? "Íon de lítio" : chem == 7 ? "Zinco-ar" : chem == 8 ? "Polímero de lítio" : null,
                };
            }
            lock (Gate) { cached = info; cachedAt = DateTime.Now; }
            return info;
        }

        static string Gb(double b) => Fmt.Bytes(b);

        public static string Report()
        {
            var i = Get();
            var sb = new StringBuilder();
            var c = CultureInfo.GetCultureInfo("pt-BR");
            void L(string label, object value) { if (value == null) return; var s = Convert.ToString(value, c); if (string.IsNullOrWhiteSpace(s)) return; sb.AppendLine("  " + label.PadRight(24) + s); }
            string D(DateTime? d) => d?.ToString("dd/MM/yyyy", c);
            sb.AppendLine("RAGE! - Relatório do sistema");
            sb.AppendLine("Gerado em " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", c));
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();
            sb.AppendLine("SISTEMA OPERACIONAL");
            L("Sistema:", i.Os.Name); L("Versão:", i.Os.Version); L("Compilação:", i.Os.Build); L("Arquitetura:", i.Os.Arch);
            L("Instalado em:", D(i.Os.InstallDate));
            var up = TimeSpan.FromSeconds(i.Os.UptimeSec);
            L("Ligado há:", (up.Days > 0 ? up.Days + " d " : "") + up.Hours + " h " + up.Minutes + " min");
            L("Computador:", i.Os.Machine); L("Usuário:", i.Os.User);
            sb.AppendLine();
            sb.AppendLine("PROCESSADOR");
            L("Modelo:", i.Cpu.Name); L("Núcleos / threads:", i.Cpu.Cores + " / " + i.Cpu.Threads);
            L("Clock base:", i.Cpu.BaseMHz > 0 ? i.Cpu.BaseMHz.ToString("0", c) + " MHz" : null); L("Soquete:", i.Cpu.Socket);
            L("Cache L2:", i.Cpu.L2.HasValue ? Gb(i.Cpu.L2.Value) : null); L("Cache L3:", i.Cpu.L3.HasValue ? Gb(i.Cpu.L3.Value) : null);
            L("Virtualização:", i.Cpu.Virtualization == null ? null : i.Cpu.Virtualization.Value ? "ativada" : "desativada");
            sb.AppendLine();
            sb.AppendLine("PLACA DE VÍDEO");
            foreach (var g in i.Gpus)
            {
                L("Modelo:", g.Name); L("Memória:", g.Vram.HasValue ? Gb(g.Vram.Value) : null);
                L("Driver:", g.Driver + (g.DriverDate.HasValue ? " (" + D(g.DriverDate) + ")" : ""));
                L("Resolução:", g.Resolution + (g.Refresh.HasValue ? " a " + g.Refresh + " Hz" : ""));
                sb.AppendLine();
            }
            sb.AppendLine("MEMÓRIA");
            L("Total:", Gb(i.Ram.Total)); L("Slots:", i.Ram.Slots.HasValue ? i.Ram.Modules.Count + " de " + i.Ram.Slots + " em uso" : null);
            foreach (var m in i.Ram.Modules)
                L((m.Slot ?? "Módulo") + ":", Gb(m.Size) + " " + (m.Type ?? "") + (m.Speed.HasValue ? " " + m.Speed + " MT/s" : "") + (m.Manufacturer != null ? " · " + m.Manufacturer : "") + (m.Part != null ? " · " + m.Part : ""));
            sb.AppendLine();
            sb.AppendLine("PLACA-MÃE");
            L("Fabricante:", i.Board.Manufacturer); L("Modelo:", i.Board.Product); L("BIOS:", i.Board.Bios + (i.Board.BiosDate.HasValue ? " (" + D(i.Board.BiosDate) + ")" : ""));
            sb.AppendLine();
            sb.AppendLine("ARMAZENAMENTO");
            int dn = 0;
            foreach (var d in i.Disks) L("Disco " + (++dn) + ":", d.Model + " · " + Gb(d.Size) + " · " + d.Media + (d.Bus != null ? " " + d.Bus : "") + (d.Health != null ? " · " + d.Health : ""));
            sb.AppendLine();
            sb.AppendLine("REDE");
            foreach (var n in i.Network) L(n.Type + ":", n.Name + (n.Ip != null ? " · IP " + n.Ip : "") + (n.Speed.HasValue ? " · " + n.Speed.Value.ToString("0", c) + " Mbps" : "") + (n.Mac != null ? " · MAC " + n.Mac : ""));
            if (i.Battery != null)
            {
                sb.AppendLine();
                sb.AppendLine("BATERIA");
                L("Tipo:", i.Battery.Chemistry);
                L("Capacidade original:", i.Battery.Design.HasValue ? i.Battery.Design.Value.ToString("N0", c) + " mWh" : null);
                L("Carga máxima atual:", i.Battery.Full.HasValue ? i.Battery.Full.Value.ToString("N0", c) + " mWh" : null);
                L("Desgaste:", i.Battery.WearPct.HasValue ? i.Battery.WearPct.Value.ToString("0.0", c) + "%" : null);
            }
            return sb.ToString();
        }

        [Rpc("sysinfo.get")]
        public static object GetRpc() => Get();

        [Rpc("sysinfo.export")]
        public static object ExportRpc() => Report();
    }
}
