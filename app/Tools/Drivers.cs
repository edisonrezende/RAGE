using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Drivers instalados (Win32_PnPSignedDriver), dispositivos com problema e backup via pnputil.</summary>
    public static class Drivers
    {
        static string ProblemText(int code)
        {
            switch (code)
            {
                case 1: return "Dispositivo não configurado corretamente (código 1)";
                case 3: return "O driver pode estar corrompido (código 3)";
                case 10: return "O dispositivo não pode ser iniciado (código 10)";
                case 12: return "Recursos insuficientes para o dispositivo (código 12)";
                case 14: return "Reinicie o computador para o dispositivo funcionar (código 14)";
                case 18: return "Reinstale os drivers deste dispositivo (código 18)";
                case 19: return "Informações de configuração incompletas ou danificadas (código 19)";
                case 21: return "O Windows está removendo este dispositivo (código 21)";
                case 22: return "Dispositivo desativado (código 22)";
                case 24: return "Dispositivo ausente ou não funcionando direito (código 24)";
                case 28: return "Drivers não instalados (código 28)";
                case 29: return "Dispositivo desativado pelo firmware (código 29)";
                case 31: return "O Windows não conseguiu carregar o driver (código 31)";
                case 32: return "Serviço do driver desativado (código 32)";
                case 37: return "Falha ao iniciar o driver (código 37)";
                case 39: return "Driver ausente ou corrompido (código 39)";
                case 41: return "Falha ao carregar o driver do dispositivo (código 41)";
                case 43: return "O Windows parou o dispositivo porque ele relatou problemas (código 43)";
                case 48: return "Driver bloqueado por problemas conhecidos (código 48)";
                case 52: return "Assinatura digital do driver não verificada (código 52)";
                default: return "Problema no dispositivo (código " + code + ")";
            }
        }

        static bool IsMicrosoft(string provider) =>
            string.IsNullOrEmpty(provider) || provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 || provider.Equals("Windows", StringComparison.OrdinalIgnoreCase);

        public static object List()
        {
            var problems = new Dictionary<string, (string name, int code, string cls, string mfr)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode, PNPClass, Manufacturer FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0"))
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            var code = Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0);
                            if (code == 0 || code == 45) continue; // 45 = dispositivo não conectado agora
                            var id = o["PNPDeviceID"] as string;
                            if (id == null) continue;
                            problems[id] = (o["Name"] as string, code, o["PNPClass"] as string, o["Manufacturer"] as string);
                        }
                    }
            }
            catch (Exception ex) { Log.Warn("WMI PnPEntity: " + ex.Message); }

            var items = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var oldLimit = DateTime.Now.AddYears(-3);
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, DriverVersion, DriverDate, InfName, DeviceID FROM Win32_PnPSignedDriver"))
                    foreach (ManagementObject o in s.Get())
                    {
                        using (o)
                        {
                            var name = ToolsUtil.Str(o["DeviceName"]);
                            var devId = o["DeviceID"] as string;
                            if (name == null || devId == null) continue;
                            if (!seen.Add(devId)) continue;
                            var provider = ToolsUtil.Str(o["DriverProviderName"]);
                            var date = ToolsUtil.WmiDay(o["DriverDate"]);
                            problems.TryGetValue(devId, out var pr);
                            items.Add(new
                            {
                                name,
                                @class = ToolsUtil.Str(o["DeviceClass"]),
                                manufacturer = ToolsUtil.Str(o["Manufacturer"]),
                                provider,
                                version = ToolsUtil.Str(o["DriverVersion"]),
                                date,
                                inf = ToolsUtil.Str(o["InfName"]),
                                deviceId = devId,
                                old = date.HasValue && date.Value.Year >= 2000 && date.Value < oldLimit && !IsMicrosoft(provider), // drivers INF da Intel usam 1968 de propósito
                                problem = pr.code != 0 ? ProblemText(pr.code) : null,
                            });
                        }
                    }
            }
            catch (Exception ex) { Log.Error("WMI PnPSignedDriver", ex); throw new RpcException("Não foi possível listar os drivers: " + ex.Message); }

            // Dispositivos com problema sem driver instalado (ex.: código 28)
            foreach (var kv in problems.Where(p => !seen.Contains(p.Key)))
                items.Add(new
                {
                    name = kv.Value.name ?? "Dispositivo desconhecido",
                    @class = kv.Value.cls,
                    manufacturer = kv.Value.mfr,
                    provider = (string)null,
                    version = (string)null,
                    date = (DateTime?)null,
                    inf = (string)null,
                    deviceId = kv.Key,
                    old = false,
                    problem = ProblemText(kv.Value.code),
                });

            var count = problems.Count(p => p.Value.code != 22);
            return new { items, problems = count };
        }

        public static object Backup(string folder, RpcContext ctx)
        {
            ToolsUtil.RequireAdmin("O backup de drivers");
            if (string.IsNullOrWhiteSpace(folder))
                folder = Path.Combine(Paths.Backups, "drivers", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            folder = Path.GetFullPath(folder.Trim());
            if (folder.Length <= 3) folder = Path.Combine(folder, "Backup de drivers " + DateTime.Now.ToString("yyyy-MM-dd"));
            if (ToolsUtil.IsUnder(folder, ToolsUtil.WinDir)) throw new RpcException("Escolha uma pasta fora do Windows.");
            Directory.CreateDirectory(folder);
            ctx.Progress(new { line = "Exportando drivers para " + folder + "..." });
            var r = Proc.Run(Proc.SysExe("pnputil.exe"), "/export-driver * \"" + folder + "\"", 1800000, Proc.Oem, l =>
            {
                var c = ToolsUtil.CleanLine(l);
                if (c != null) ctx.Progress(new { line = c });
            }, ctx.Token);
            int count = 0;
            try { count = Directory.GetDirectories(folder).Length; if (count == 0) count = Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length; } catch { }
            var ok = r.ExitCode == 0 && count > 0;
            if (ok) Activity.Add("info", "memory", "Backup de drivers concluído", count + " drivers em " + folder);
            return new { ok, count, folder, message = ok ? null : ToolsUtil.LastMeaningful(r.Output) ?? "Falha no pnputil (código " + r.ExitCode + ")" };
        }

        [Rpc("drivers.list")]
        public static object ListRpc() => List();

        [Rpc("drivers.backup")]
        public static object BackupRpc(string folder, RpcContext ctx) => Backup(folder, ctx);
    }
}
