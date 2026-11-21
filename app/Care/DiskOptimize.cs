using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Volumes fixos, tipo de mídia, última otimização (estatísticas do Otimizador de Unidades) e disk.optimize.</summary>
    public static class DiskService
    {
        public sealed class VolumeInfo
        {
            public string Letter { get; set; }          // "C:"
            public string Label { get; set; }
            public string Media { get; set; }           // SSD | HDD | Desconhecido
            public string Bus { get; set; }
            public double Total { get; set; }
            public double Free { get; set; }
            public DateTime? LastOptimized { get; set; }
            public double? Fragmentation { get; set; }
            public string Action { get; set; }          // trim | defrag | null
            public bool Needed { get; set; }
            [JsonIgnore] public string FileSystem { get; set; }
            [JsonIgnore] public bool External { get; set; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, int size);

        static string BusName(int bus)
        {
            switch (bus)
            {
                case 1: return "SCSI";
                case 2: return "ATAPI";
                case 3: return "ATA";
                case 4: return "IEEE 1394";
                case 6: return "Fibre Channel";
                case 7: return "USB";
                case 8: return "RAID";
                case 9: return "iSCSI";
                case 10: return "SAS";
                case 11: return "SATA";
                case 12: return "SD";
                case 13: return "MMC";
                case 14: return "Virtual";
                case 15: return "Virtual (arquivo)";
                case 16: return "Espaços de Armazenamento";
                case 17: return "NVMe";
                case 18: return "SCM";
                case 19: return "UFS";
                default: return null;
            }
        }

        /// <summary>Mapa letra -> (mídia, barramento) via WMI root\Microsoft\Windows\Storage.</summary>
        static Dictionary<char, (string media, int bus)> MediaMap()
        {
            var map = new Dictionary<char, (string, int)>();
            const string scope = @"root\Microsoft\Windows\Storage";
            var disks = new Dictionary<string, (string, int)>();
            foreach (var o in CareUtil.Wmi(scope, "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", 6000))
            {
                var id = Convert.ToString(CareUtil.Prop(o, "DeviceId"));
                var mt = Convert.ToInt32(CareUtil.Prop(o, "MediaType") ?? 0);
                var bt = Convert.ToInt32(CareUtil.Prop(o, "BusType") ?? 0);
                var media = mt == 3 ? "HDD" : mt == 4 || mt == 5 ? "SSD" : bt == 17 ? "SSD" : "Desconhecido";
                if (id != null) disks[id] = (media, bt);
            }
            foreach (var o in CareUtil.Wmi(scope, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition", 6000))
            {
                var dl = CareUtil.Prop(o, "DriveLetter");
                char c = dl is char ch ? ch : dl != null ? (char)Convert.ToUInt16(dl) : '\0';
                if (!char.IsLetter(c)) continue;
                var num = Convert.ToString(CareUtil.Prop(o, "DiskNumber"));
                if (num != null && disks.TryGetValue(num, out var d)) map[char.ToUpperInvariant(c)] = d;
            }
            return map;
        }

        /// <summary>Estatísticas gravadas pelo Otimizador de Unidades: HKLM\SOFTWARE\Microsoft\Dfrg\Statistics\Volume{GUID}.</summary>
        static void ReadStats(string root, VolumeInfo v)
        {
            var sb = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(root, sb, sb.Capacity)) return;
            var name = sb.ToString(); // \\?\Volume{guid}\
            var i = name.IndexOf("Volume{", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return;
            var key = @"HKLM\SOFTWARE\Microsoft\Dfrg\Statistics\" + name.Substring(i).TrimEnd('\\');
            if (Reg.Get(key, "LastRunTime") is byte[] st && st.Length >= 16)
            {
                // SYSTEMTIME (hora local)
                int W(int o) => BitConverter.ToUInt16(st, o);
                try { if (W(0) > 1990) v.LastOptimized = new DateTime(W(0), W(2), W(6), W(8), W(10), W(12), DateTimeKind.Local); } catch { }
            }
            var frag = Reg.GetInt(key, "LastRunPercentFragmentation");
            if (frag.HasValue && v.LastOptimized.HasValue && v.Media == "HDD") v.Fragmentation = frag.Value;
        }

        public static List<VolumeInfo> List()
        {
            var list = new List<VolumeInfo>();
            Dictionary<char, (string media, int bus)> map;
            try { map = MediaMap(); } catch (Exception ex) { Log.Warn("Discos: " + ex.Message); map = new Dictionary<char, (string, int)>(); }
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var letter = char.ToUpperInvariant(d.Name[0]);
                    var v = new VolumeInfo
                    {
                        Letter = letter + ":", Label = d.VolumeLabel, Total = d.TotalSize, Free = d.AvailableFreeSpace, FileSystem = d.DriveFormat, Media = "Desconhecido",
                    };
                    if (map.TryGetValue(letter, out var m)) { v.Media = m.media; v.Bus = BusName(m.bus); v.External = m.bus == 7 || m.bus == 14 || m.bus == 15; }
                    ReadStats(d.Name, v);
                    var fs = (v.FileSystem ?? "").ToUpperInvariant();
                    var optimizable = fs == "NTFS" || fs == "REFS" || (fs.StartsWith("FAT") && v.Media == "HDD");
                    if (optimizable && v.Media == "SSD")
                    {
                        v.Action = "trim";
                        v.Needed = !v.LastOptimized.HasValue || (DateTime.Now - v.LastOptimized.Value).TotalDays > 7;
                    }
                    else if (optimizable && v.Media == "HDD" && fs != "REFS")
                    {
                        v.Action = "defrag";
                        v.Needed = (v.Fragmentation ?? 0) > 5 || !v.LastOptimized.HasValue || (DateTime.Now - v.LastOptimized.Value).TotalDays > 30;
                    }
                    list.Add(v);
                }
                catch (Exception ex) { Log.Warn("Volume " + d.Name + ": " + ex.Message); }
            }
            return list;
        }

        [Rpc("disk.volumes")]
        public static object Volumes() => List();

        internal static string Describe(VolumeInfo v)
        {
            var when = v.LastOptimized.HasValue ? CareUtil.Days(v.LastOptimized.Value) : null;
            if (v.Action == "trim") return when == null ? "Sem registro de TRIM" : "Último TRIM " + when;
            if (v.Action == "defrag")
            {
                var s = v.Fragmentation.HasValue ? "Fragmentação de " + v.Fragmentation.Value.ToString("0") + "%" : "Fragmentação desconhecida";
                return s + (when == null ? ", nunca otimizado" : ", última otimização " + when);
            }
            return "Sem otimização disponível";
        }

        internal static (bool ok, string message) Run(VolumeInfo v, RpcContext ctx, Action<string> onLine)
        {
            if (!Program.IsAdmin) throw new RpcException(CareUtil.AdminMsg);
            var l = v.Letter.Substring(0, 1);
            var mode = v.Action == "trim" ? " -ReTrim" : v.Action == "defrag" ? " -Defrag" : "";
            var script = "Optimize-Volume -DriveLetter " + l + mode + " -Verbose 4>&1 | ForEach-Object { $_.ToString() }";
            var r = Proc.Ps(script, v.Action == "defrag" ? 0 : 60 * 60000, line => { if (!string.IsNullOrWhiteSpace(line)) onLine(line.Trim()); }, ctx.Token);
            var what = v.Action == "trim" ? "TRIM" : v.Action == "defrag" ? "Desfragmentação" : "Otimização";
            if (r.TimedOut) return (false, what + " de " + v.Letter + " demorou demais e foi interrompida.");
            if (!r.Ok) return (false, "Não foi possível otimizar " + v.Letter + ": " + Proc.CleanPsError(r.Error));
            return (true, what + " de " + v.Letter + (what == "TRIM" ? " concluído" : " concluída"));
        }

        [Rpc("disk.optimize")]
        public static object Optimize(string letter, RpcContext ctx)
        {
            if (string.IsNullOrWhiteSpace(letter) || !char.IsLetter(letter.Trim()[0])) throw new RpcException("Unidade inválida.");
            var key = char.ToUpperInvariant(letter.Trim()[0]) + ":";
            var v = List().FirstOrDefault(x => x.Letter == key);
            if (v == null) throw new RpcException("A unidade " + key + " não foi encontrada ou não é um disco fixo.");
            var res = Run(v, ctx, line => ctx.Progress(new { line }));
            if (res.ok) Activity.Add("fix", "hard_drive", "Disco otimizado", v.Letter + " — " + (v.Action == "trim" ? "TRIM" : v.Action == "defrag" ? "desfragmentação" : "otimização"));
            return new { ok = res.ok, message = res.message };
        }
    }

    /// <summary>Categoria "disk" — volumes fixos que precisam de TRIM (SSD) ou desfragmentação (HDD).</summary>
    public sealed class DiskScanner : ICareScanner
    {
        public string Category => "disk";
        public string Title => "Otimização de disco";
        public string Description => "TRIM e desfragmentação";
        public string Icon => "hard_drive";
        public int Order => 90;
        public bool DefaultOn => true;

        public List<CareItem> Scan(RpcContext ctx)
        {
            return DiskService.List().Where(v => v.Needed && v.Action != null && !v.External).Select(v => new CareItem
            {
                Id = "disk." + v.Letter.Substring(0, 1), Category = Category, Group = "Unidades",
                Title = v.Letter + " · " + v.Media + (v.Bus != null ? " " + v.Bus : "") + (string.IsNullOrEmpty(v.Label) ? "" : " (" + v.Label + ")"),
                Detail = DiskService.Describe(v),
                Current = v.LastOptimized.HasValue ? CareUtil.Days(v.LastOptimized.Value) : "Nunca",
                Recommended = v.Action == "trim" ? "Executar TRIM" : "Desfragmentar",
                Note = v.Action == "defrag" ? "A desfragmentação de um HD pode levar bastante tempo." : null,
                Tag = v,
            }).ToList();
        }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                var v = it.Tag as DiskService.VolumeInfo;
                if (v == null) { results.Add(new FixResult { Id = it.Id, Message = "Faça uma nova verificação." }); continue; }
                try
                {
                    var title = it.Title;
                    var res = DiskService.Run(v, ctx, line => ctx.Progress(new { phase = "item", category = Category, id = it.Id, title, line }));
                    results.Add(new FixResult { Id = it.Id, Ok = res.ok, Message = res.message });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { results.Add(new FixResult { Id = it.Id, Message = ex.Message }); }
            }
            return results;
        }
    }
}
