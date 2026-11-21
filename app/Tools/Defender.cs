using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Microsoft Defender: status, verificações (MpCmdRun), atualização de assinaturas e remoção de ameaças.</summary>
    public static class Defender
    {
        const string Ns = @"\\.\root\Microsoft\Windows\Defender";

        public sealed class Threat
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Severity { get; set; }
            public string Path { get; set; }
            public string Status { get; set; }
            public DateTime? Detected { get; set; }
            public bool Active { get; set; }
        }

        public sealed class Status
        {
            public bool Available { get; set; }
            public bool Enabled { get; set; }
            public bool Realtime { get; set; }
            public bool Tamper { get; set; }
            public string SignatureVersion { get; set; }
            public int? SignatureAgeDays { get; set; }
            public string EngineVersion { get; set; }
            public DateTime? LastQuickScan { get; set; }
            public DateTime? LastFullScan { get; set; }
            public string ThirdParty { get; set; }
            public string Mode { get; set; }
            public List<Threat> Threats { get; set; } = new List<Threat>();
        }

        static string Severity(int id)
        {
            switch (id)
            {
                case 1: return "baixa";
                case 2: return "moderada";
                case 4: return "alta";
                case 5: return "grave";
                default: return "desconhecida";
            }
        }

        static string ThreatStatus(int id)
        {
            switch (id)
            {
                case 1: return "Detectada";
                case 2: return "Limpa";
                case 3: return "Em quarentena";
                case 4: return "Removida";
                case 5: return "Permitida";
                case 6: return "Bloqueada";
                case 102: return "Falha ao colocar em quarentena";
                case 103: return "Falha ao remover";
                case 104: return "Falha ao permitir";
                case 105: return "Abandonada";
                case 107: return "Falha ao bloquear";
                default: return "Desconhecido";
            }
        }

        static string ModeName(string m)
        {
            if (string.IsNullOrEmpty(m)) return null;
            var l = m.ToLowerInvariant();
            if (l.Contains("sxs")) return "Passivo (lado a lado)";
            if (l.Contains("passive")) return "Passivo";
            if (l.Contains("edr")) return "Bloqueio EDR";
            if (l.Contains("not running")) return "Desligado";
            if (l == "normal") return "Ativo";
            return m;
        }

        /// <summary>Antivírus de terceiros ativo (Central de Segurança), se houver.</summary>
        public static string ThirdPartyAv()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\SecurityCenter2"), new ObjectQuery("SELECT displayName, productState FROM AntiVirusProduct")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            var name = o["displayName"] as string;
                            if (string.IsNullOrEmpty(name) || name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            var state = Convert.ToInt64(o["productState"] ?? 0);
                            if (((state >> 12) & 0xF) != 0) return name; // ativo (1) ou pausado/expirado (2/3)
                        }
            }
            catch { }
            return null;
        }

        public static Status GetStatus(bool threats = true)
        {
            var st = new Status { ThirdParty = ThirdPartyAv() };
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(Ns), new ObjectQuery("SELECT * FROM MSFT_MpComputerStatus")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            st.Available = true;
                            bool B(string n) { try { return Convert.ToBoolean(o[n] ?? false); } catch { return false; } }
                            st.Enabled = B("AMServiceEnabled") && B("AntivirusEnabled");
                            st.Realtime = B("RealTimeProtectionEnabled");
                            st.Tamper = B("IsTamperProtected");
                            st.SignatureVersion = ToolsUtil.Str(o["AntivirusSignatureVersion"]);
                            var age = Convert.ToInt32(o["AntivirusSignatureAge"] ?? 65535);
                            st.SignatureAgeDays = age >= 0 && age < 65535 ? age : (int?)null;
                            var eng = ToolsUtil.Str(o["AMEngineVersion"]);
                            st.EngineVersion = eng == null || eng == "0.0.0.0" ? null : eng;
                            st.LastQuickScan = ToolsUtil.WmiDate(o["QuickScanEndTime"]);
                            st.LastFullScan = ToolsUtil.WmiDate(o["FullScanEndTime"]);
                            try { st.Mode = ModeName(ToolsUtil.Str(o["AMRunningMode"])); } catch { }
                        }
            }
            catch (Exception ex) { Log.Warn("Defender WMI: " + ex.Message); }
            if (threats && st.Available) st.Threats = Threats();
            return st;
        }

        public static List<Threat> Threats()
        {
            var names = new Dictionary<string, (string name, int sev, bool active)>();
            var res = new List<Threat>();
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(Ns), new ObjectQuery("SELECT ThreatID, ThreatName, SeverityID, IsActive FROM MSFT_MpThreat")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            var id = Convert.ToString(o["ThreatID"]);
                            names[id] = (o["ThreatName"] as string, Convert.ToInt32(o["SeverityID"] ?? 0), Convert.ToBoolean(o["IsActive"] ?? false));
                        }
                using (var s = new ManagementObjectSearcher(new ManagementScope(Ns), new ObjectQuery("SELECT ThreatID, ThreatStatusID, Resources, InitialDetectionTime FROM MSFT_MpThreatDetection")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            var id = Convert.ToString(o["ThreatID"]);
                            names.TryGetValue(id, out var t);
                            var resources = o["Resources"] as string[];
                            var path = resources?.FirstOrDefault();
                            if (path != null) { var i = path.IndexOf(":_", StringComparison.Ordinal); if (i > 0 && i < 12) path = path.Substring(i + 2); }
                            var statusId = Convert.ToInt32(o["ThreatStatusID"] ?? 0);
                            res.Add(new Threat
                            {
                                Id = id,
                                Name = t.name ?? "Ameaça " + id,
                                Severity = Severity(t.sev),
                                Path = path,
                                Status = ThreatStatus(statusId),
                                Detected = ToolsUtil.WmiDate(o["InitialDetectionTime"]),
                                Active = t.active,
                            });
                        }
            }
            catch (Exception ex) { Log.Warn("Defender ameaças: " + ex.Message); }
            return res.OrderByDescending(x => x.Detected).Take(100).ToList();
        }

        public static string MpCmdRun()
        {
            var plat = Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows Defender", "InstallLocation");
            if (!string.IsNullOrEmpty(plat)) { var p = Path.Combine(plat, "MpCmdRun.exe"); if (File.Exists(p)) return p; }
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(dir))
                {
                    var latest = Directory.GetDirectories(dir).Select(d => Path.Combine(d, "MpCmdRun.exe")).Where(File.Exists).OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (latest != null) return latest;
                }
            }
            catch { }
            var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            return File.Exists(pf) ? pf : null;
        }

        static void EnsureUsable(Status st)
        {
            if (!st.Available) throw new RpcException("O Microsoft Defender não está disponível neste computador.");
            if (!st.Enabled)
                throw new RpcException("O Microsoft Defender está desativado" + (st.ThirdParty != null ? " porque o " + st.ThirdParty + " está protegendo o computador." : ". Ative-o em Segurança do Windows."));
        }

        public static object Scan(string type, string path, RpcContext ctx)
        {
            var st = GetStatus(false);
            EnsureUsable(st);
            var exe = MpCmdRun() ?? throw new RpcException("MpCmdRun.exe não encontrado.");
            string args;
            switch (type)
            {
                case "full": args = "-Scan -ScanType 2"; break;
                case "custom":
                    if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) throw new RpcException("Escolha um arquivo ou pasta para verificar.");
                    args = "-Scan -ScanType 3 -File \"" + path.Trim().TrimEnd('\\') + "\"";
                    break;
                default: type = "quick"; args = "-Scan -ScanType 1"; break;
            }
            var label = type == "full" ? "Verificação completa" : type == "custom" ? "Verificação personalizada" : "Verificação rápida";
            ctx.Progress(new { line = label + " iniciada..." });
            var r = Proc.Run(exe, args, type == "full" ? 6 * 3600000 : 3600000, Proc.Oem, l => { var c = ToolsUtil.CleanLine(l); if (c != null) ctx.Progress(new { line = c }); }, ctx.Token);
            var active = Threats().Where(t => t.Active).ToList();
            var ok = r.ExitCode == 0 || r.ExitCode == 2;
            if (active.Count > 0) Activity.Add("warn", "coronavirus", label + ": " + active.Count + (active.Count == 1 ? " ameaça encontrada" : " ameaças encontradas"), string.Join(", ", active.Select(t => t.Name).Take(3)));
            else if (ok) Activity.Add("info", "shield", label + " concluída", "Nenhuma ameaça encontrada");
            return new { ok, threats = active, message = ok ? null : ToolsUtil.LastMeaningful(r.Output) ?? "Falha na verificação (código " + r.ExitCode + ")." };
        }

        public static object UpdateSignatures(RpcContext ctx)
        {
            var st = GetStatus(false);
            EnsureUsable(st);
            var exe = MpCmdRun() ?? throw new RpcException("MpCmdRun.exe não encontrado.");
            ctx.Progress(new { line = "Atualizando as definições de vírus..." });
            var r = Proc.Run(exe, "-SignatureUpdate", 900000, Proc.Oem, l => { var c = ToolsUtil.CleanLine(l); if (c != null) ctx.Progress(new { line = c }); }, ctx.Token);
            var ok = r.ExitCode == 0;
            var after = GetStatus(false);
            if (ok) Activity.Add("info", "shield", "Definições do Defender atualizadas", after.SignatureVersion);
            return new { ok, signatureVersion = after.SignatureVersion, message = ok ? "Definições atualizadas." : ToolsUtil.LastMeaningful(r.Output) ?? "Falha ao atualizar (código 0x" + r.ExitCode.ToString("X8") + ")." };
        }

        public static object RemoveThreats(RpcContext ctx)
        {
            ToolsUtil.RequireAdmin("Remover ameaças");
            var before = Threats().Where(t => t.Active).ToList();
            if (before.Count == 0) return new { ok = true, removed = 0, message = "Nenhuma ameaça ativa." };
            ctx.Progress(new { line = "Removendo " + before.Count + " ameaças..." });
            var r = Proc.Ps("Remove-MpThreat", 600000, null, ctx.Token);
            var left = Threats().Where(t => t.Active).ToList();
            var removed = Math.Max(0, before.Count - left.Count);
            var ok = left.Count == 0;
            if (removed > 0) Activity.Add("clean", "shield", removed + (removed == 1 ? " ameaça removida" : " ameaças removidas"), string.Join(", ", before.Select(t => t.Name).Take(3)));
            return new { ok, removed, message = ok ? "Ameaças removidas." : (r.Ok ? "Algumas ameaças não puderam ser removidas." : Proc.CleanPsError(r.Error)) };
        }

        [Rpc("av.status")]
        public static object StatusRpc() => GetStatus(true);

        [Rpc("av.scan")]
        public static object ScanRpc(string type, string path, RpcContext ctx) => Scan(type, path, ctx);

        [Rpc("av.update")]
        public static object UpdateRpc(RpcContext ctx) => UpdateSignatures(ctx);

        [Rpc("av.remove")]
        public static object RemoveRpc(RpcContext ctx) => RemoveThreats(ctx);
    }

    /// <summary>Verificação inteligente: verificação rápida do Microsoft Defender.</summary>
    public sealed class AntivirusScanner : ICareScanner
    {
        public string Category => "antivirus";
        public string Title => "Ameaças (Microsoft Defender)";
        public string Description => "Verificação rápida contra vírus e malware usando o Microsoft Defender.";
        public string Icon => "coronavirus";
        public int Order => 110;
        public bool DefaultOn => false;

        public List<CareItem> Scan(RpcContext ctx)
        {
            var st = Defender.GetStatus(false);
            if (!st.Available || !st.Enabled)
            {
                var third = st.ThirdParty;
                return new List<CareItem>
                {
                    new CareItem
                    {
                        Id = "antivirus.unavailable", Category = "antivirus", Group = "Microsoft Defender",
                        Title = third != null ? "Proteção feita pelo " + third : "Microsoft Defender desativado",
                        Detail = third != null ? "O Defender fica desligado enquanto outro antivírus está ativo. Use a verificação do seu antivírus." : "Ative a proteção em Segurança do Windows.",
                        Status = third != null ? "ok" : "bad", Fixable = false, Selected = false, Count = 0,
                        Action = "windowsdefender:",
                    },
                };
            }
            var exe = Defender.MpCmdRun();
            if (exe == null) throw new RpcException("MpCmdRun.exe não encontrado.");
            ctx.Progress(new { phase = "scan", category = "antivirus", line = "Verificação rápida do Microsoft Defender..." });
            var r = Proc.Run(exe, "-Scan -ScanType 1", 3600000, Proc.Oem, l => { var c = ToolsUtil.CleanLine(l); if (c != null) ctx.Progress(new { category = "antivirus", line = c }); }, ctx.Token);
            var active = Defender.Threats().Where(t => t.Active).ToList();
            var items = active.GroupBy(t => t.Id).Select(g => g.First()).Select(t => new CareItem
            {
                Id = "antivirus." + t.Id, Category = "antivirus", Group = "Ameaças encontradas",
                Title = t.Name, Detail = t.Path ?? t.Status, Status = "bad", Risk = "safe", Selected = true, Fixable = true,
                Note = "Severidade " + t.Severity + ". Será removida pelo Microsoft Defender.",
                Paths = t.Path != null ? new List<string> { t.Path } : null, Tag = t.Id,
            }).ToList();
            if (items.Count == 0)
                items.Add(new CareItem
                {
                    Id = "antivirus.clean", Category = "antivirus", Group = "Microsoft Defender",
                    Title = r.ExitCode == 0 || r.ExitCode == 2 ? "Nenhuma ameaça encontrada" : "A verificação não terminou",
                    Detail = r.ExitCode == 0 || r.ExitCode == 2 ? "Verificação rápida concluída." : ToolsUtil.LastMeaningful(r.Output) ?? "Código " + r.ExitCode,
                    Status = r.ExitCode == 0 || r.ExitCode == 2 ? "ok" : "warn", Fixable = false, Selected = false, Count = 0, Action = "windowsdefender:",
                });
            return items;
        }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            object r;
            try { r = Defender.RemoveThreats(ctx); }
            catch (Exception ex) { return items.Select(i => new FixResult { Id = i.Id, Ok = false, Message = ex.Message }).ToList(); }
            var j = Newtonsoft.Json.Linq.JObject.FromObject(r);
            var ok = (bool)j["ok"];
            var left = ok ? new HashSet<string>() : new HashSet<string>(Defender.Threats().Where(t => t.Active).Select(t => t.Id));
            return items.Select(i => new FixResult
            {
                Id = i.Id,
                Ok = !left.Contains(i.Tag as string ?? ""),
                Message = left.Contains(i.Tag as string ?? "") ? (string)j["message"] : "Ameaça removida",
            }).ToList();
        }
    }
}
