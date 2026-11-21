using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Produtos registrados na Central de Segurança do Windows (API oficial wscapi: IWSCProductList).</summary>
    internal static class Wsc
    {
        [ComImport, Guid("8C38232E-3A45-4A27-92B0-1A16A975F669"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IWscProduct
        {
            string ProductName { [return: MarshalAs(UnmanagedType.BStr)] get; }
            int ProductState { get; }      // WSC_SECURITY_PRODUCT_STATE: 0 ligado, 1 desligado, 2 pausado, 3 expirado
            int SignatureStatus { get; }   // 0 atualizado, 1 desatualizado
            string RemediationPath { [return: MarshalAs(UnmanagedType.BStr)] get; }
            string ProductStateTimestamp { [return: MarshalAs(UnmanagedType.BStr)] get; }
            string ProductGuid { [return: MarshalAs(UnmanagedType.BStr)] get; }
            int ProductIsDefault { get; }
        }

        [ComImport, Guid("722A338C-6E8E-4E72-AC27-1417FB0C81C2"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
        interface IWSCProductList
        {
            void Initialize(uint provider);
            int Count { get; }
            IWscProduct this[uint index] { get; }
        }

        public sealed class Product { public string Name; public int State; public int Signature; public bool Microsoft; }

        public const uint Firewall = 1, Antivirus = 4;

        public static List<Product> List(uint provider)
        {
            var l = new List<Product>();
            object o = null;
            try
            {
                o = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("17072F7B-9ABE-4A74-A261-1EB76B55107A")));
                var list = (IWSCProductList)o;
                list.Initialize(provider);
                for (uint i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    try
                    {
                        var name = p.ProductName ?? "";
                        l.Add(new Product { Name = name, State = p.ProductState, Signature = p.SignatureStatus, Microsoft = name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0 });
                    }
                    finally { Marshal.ReleaseComObject(p); }
                }
                return l;
            }
            catch (Exception ex) { Log.Warn("WSC: " + ex.Message); }
            finally { if (o != null) try { Marshal.ReleaseComObject(o); } catch { } }

            // Plano B: WMI SecurityCenter2 (productState: bits 12-15 = estado, bits 4-7 = assinaturas)
            var cls = provider == Firewall ? "FirewallProduct" : "AntiVirusProduct";
            foreach (var mo in CareUtil.Wmi(@"root\SecurityCenter2", "SELECT displayName, productState FROM " + cls, 3000))
            {
                var name = CareUtil.Prop(mo, "displayName") as string ?? "";
                var st = Convert.ToInt64(CareUtil.Prop(mo, "productState") ?? 0);
                var s = (int)((st >> 12) & 0xF);
                l.Add(new Product { Name = name, State = s == 1 ? 0 : s == 0 ? 1 : s, Signature = (int)((st >> 4) & 0xF) == 0 ? 0 : 1, Microsoft = name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 });
            }
            return l;
        }

        public static string StateText(int s) => s == 0 ? "ligado" : s == 2 ? "pausado" : s == 3 ? "expirado" : "desligado";
    }

    /// <summary>Estado do Microsoft Defender via WMI (mesma fonte do Get-MpComputerStatus, sem abrir o PowerShell).</summary>
    internal sealed class DefenderStatus
    {
        public bool ServiceEnabled, AntivirusEnabled, RealTime, Tamper;
        public DateTime? SignatureUpdated;
        public int? SignatureAgeDays;
        public string RunningMode;

        public static DefenderStatus Get()
        {
            var mo = CareUtil.Wmi(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus", 5000).FirstOrDefault();
            if (mo == null) return null;
            bool B(string n) => CareUtil.Prop(mo, n) is bool b && b;
            var d = new DefenderStatus
            {
                ServiceEnabled = B("AMServiceEnabled"), AntivirusEnabled = B("AntivirusEnabled"), RealTime = B("RealTimeProtectionEnabled"), Tamper = B("IsTamperProtected"),
                RunningMode = CareUtil.Prop(mo, "AMRunningMode") as string,
            };
            try { if (CareUtil.Prop(mo, "AntivirusSignatureLastUpdated") is string s && s.Length > 0) d.SignatureUpdated = ManagementDateTimeConverter.ToDateTime(s); } catch { }
            var age = CareUtil.Prop(mo, "AntivirusSignatureAge");
            if (age != null) { var a = Convert.ToInt64(age); if (a >= 0 && a < 65535) d.SignatureAgeDays = (int)a; }
            return d;
        }
    }

    /// <summary>Categoria "security" — checklist de reforço de segurança (itens ok também voltam, com fixable=false).</summary>
    public sealed class SecurityScanner : ICareScanner
    {
        public string Category => "security";
        public string Title => "Reforço de segurança";
        public string Description => "Firewall, Defender, UAC";
        public string Icon => "shield";
        public int Order => 80;
        public bool DefaultOn => true;

        const string GAv = "Antivírus", GSys = "Sistema", GNet = "Rede", GAcc = "Contas";
        const string UacKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        const string ExplorerKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
        const string ExplorerPol = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        const string ExplorerPolCu = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

        static CareItem Item(string id, string group, string title, string status, string detail, bool fixable, string current = null, string recommended = null,
            string action = null, bool reboot = false, string note = null)
        {
            return new CareItem
            {
                Id = "security." + id, Group = group, Title = title, Status = status, Detail = detail, Fixable = fixable && status != "ok",
                Selected = fixable && status != "ok", Current = current, Recommended = recommended, Action = action, NeedsReboot = reboot, Note = note, Tag = id,
                Risk = "safe",
            };
        }

        public List<CareItem> Scan(RpcContext ctx)
        {
            var items = new List<CareItem>();
            void Run(string what, Action a)
            {
                ctx.Token.ThrowIfCancellationRequested();
                CareUtil.Item(ctx, Category, what);
                try { a(); } catch (OperationCanceledException) { throw; } catch (Exception ex) { Log.Warn("Segurança (" + what + "): " + ex.Message); }
            }

            Run("Antivírus", () => items.AddRange(AntivirusItems()));
            Run("Firewall", () => items.Add(FirewallItem()));
            Run("UAC", () => items.Add(UacItem()));
            Run("SmartScreen", () => items.Add(SmartScreenItem()));
            Run("Execução automática", () => items.Add(AutorunItem()));
            Run("SMBv1", () => items.Add(Smb1Item()));
            Run("Área de Trabalho Remota", () => items.Add(RdpItem()));
            Run("Conta Convidado", () => { var g = GuestItem(); if (g != null) items.Add(g); });
            Run("Windows Update", () => { var w = UpdateItem(); if (w != null) items.Add(w); });
            foreach (var it in items) it.Category = Category;
            return items;
        }

        // ---------- Antivírus ----------
        static List<CareItem> AntivirusItems()
        {
            var l = new List<CareItem>();
            var avs = Wsc.List(Wsc.Antivirus);
            var third = avs.Where(p => !p.Microsoft).ToList();
            var thirdOn = third.FirstOrDefault(p => p.State == 0);
            var def = DefenderStatus.Get();
            const string sec = "windowsdefender://threat";

            // Proteção em tempo real
            if (thirdOn != null)
                l.Add(Item("realtime", GAv, "Proteção em tempo real", "ok", "Protegido por " + thirdOn.Name, false, "Ligada"));
            else if (def != null && def.RealTime && def.AntivirusEnabled)
                l.Add(Item("realtime", GAv, "Proteção em tempo real", "ok", "Microsoft Defender ativo", false, "Ligada"));
            else if (third.Count > 0)
            {
                var p = third[0];
                l.Add(Item("realtime", GAv, "Proteção em tempo real", p.State == 2 ? "warn" : "bad",
                    p.Name + " está registrado como antivírus, mas está " + Wsc.StateText(p.State) + ". Ative-o ou desinstale-o para o Microsoft Defender voltar a proteger o PC.",
                    false, Wsc.StateText(p.State), "Ligada", sec));
            }
            else
                l.Add(Item("realtime", GAv, "Proteção em tempo real", "bad", "A proteção em tempo real do Microsoft Defender está desligada.", def != null,
                    "Desligada", "Ligada", sec, note: def != null && def.Tamper ? "Se a Proteção contra Adulteração impedir a mudança, ligue pela Segurança do Windows." : null));

            // Assinaturas
            if (thirdOn != null)
                l.Add(Item("signatures", GAv, "Definições de vírus atualizadas", thirdOn.Signature == 0 ? "ok" : "warn",
                    thirdOn.Signature == 0 ? "Atualizadas pelo " + thirdOn.Name : "As definições do " + thirdOn.Name + " estão desatualizadas. Atualize pelo próprio antivírus.", false));
            else if (def != null && def.AntivirusEnabled)
            {
                var age = def.SignatureUpdated.HasValue ? (int)Math.Floor((DateTime.Now - def.SignatureUpdated.Value).TotalDays) : def.SignatureAgeDays ?? 999;
                var when = def.SignatureUpdated.HasValue ? "Atualizadas " + CareUtil.Days(def.SignatureUpdated.Value) : age < 999 ? "Atualizadas há " + age + " dias" : "Sem data de atualização";
                l.Add(Item("signatures", GAv, "Definições de vírus atualizadas", age <= 3 ? "ok" : age <= 7 ? "warn" : "bad",
                    when + (age > 3 ? " — o recomendado é no máximo 3 dias." : ""), true, when, "No máximo 3 dias"));
            }
            else if (third.Count > 0)
                l.Add(Item("signatures", GAv, "Definições de vírus atualizadas", third[0].Signature == 0 ? "ok" : "warn",
                    third[0].Signature == 0 ? "Atualizadas pelo " + third[0].Name : "As definições do " + third[0].Name + " estão desatualizadas.", false, action: sec));
            return l;
        }

        // ---------- Firewall ----------
        static readonly (int id, string name)[] Profiles = { (1, "Domínio"), (2, "Privada"), (4, "Pública") };

        static object FwPolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));

        static bool FwEnabled(object fw, int profile) =>
            Convert.ToBoolean(fw.GetType().InvokeMember("FirewallEnabled", BindingFlags.GetProperty, null, fw, new object[] { profile }));

        static CareItem FirewallItem()
        {
            var third = Wsc.List(Wsc.Firewall).FirstOrDefault(p => p.State == 0 && !IsWindowsFirewall(p.Name));
            object fw = null;
            try
            {
                fw = FwPolicy();
                var off = Profiles.Where(p => !FwEnabled(fw, p.id)).Select(p => p.name).ToList();
                if (off.Count == 0) return Item("firewall", GNet, "Firewall do Windows", "ok", "Ativo em todos os perfis de rede", false, "Ativo");
                if (third != null) return Item("firewall", GNet, "Firewall", "ok", "Gerenciado por " + third.Name, false, "Ativo");
                return Item("firewall", GNet, "Firewall do Windows", "bad", "Desligado nos perfis: " + string.Join(", ", off), true, "Desligado (" + string.Join(", ", off) + ")", "Ativo em todos os perfis");
            }
            finally { if (fw != null) Marshal.ReleaseComObject(fw); }
        }

        static bool IsWindowsFirewall(string name) => name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------- UAC / SmartScreen / Autorun ----------
        static CareItem UacItem()
        {
            var lua = Reg.GetInt(UacKey, "EnableLUA") ?? 1;
            var consent = Reg.GetInt(UacKey, "ConsentPromptBehaviorAdmin") ?? 5;
            if (lua == 0) return Item("uac", GSys, "Controle de Conta de Usuário (UAC)", "bad", "O UAC está desativado: qualquer programa pode alterar o sistema sem pedir permissão.", true, "Desativado", "Ativado", reboot: true);
            if (consent == 0) return Item("uac", GSys, "Controle de Conta de Usuário (UAC)", "warn", "O UAC está configurado para nunca notificar.", true, "Nunca notificar", "Notificar quando apps fizerem alterações");
            return Item("uac", GSys, "Controle de Conta de Usuário (UAC)", "ok", "Ativo", false, "Ativado");
        }

        static CareItem SmartScreenItem()
        {
            var pol = Reg.GetInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
            if (pol == 0) return Item("smartscreen", GSys, "SmartScreen", "bad", "O SmartScreen foi desligado por uma política do sistema.", false, "Desligado (política)", "Ligado", "windowsdefender://appbrowser");
            var v = Reg.GetStr(ExplorerKey, "SmartScreenEnabled");
            if (v != null && v.Equals("Off", StringComparison.OrdinalIgnoreCase))
                return Item("smartscreen", GSys, "SmartScreen", "bad", "A verificação de apps e arquivos baixados está desligada.", true, "Desligado", "Avisar");
            return Item("smartscreen", GSys, "SmartScreen", "ok", "Verifica apps e arquivos baixados", false, "Ligado");
        }

        static CareItem AutorunItem()
        {
            var noAutorun = Reg.GetInt(ExplorerPol, "NoAutorun");
            var lm = Reg.GetInt(ExplorerPol, "NoDriveTypeAutoRun");
            var cu = Reg.GetInt(ExplorerPolCu, "NoDriveTypeAutoRun");
            if (noAutorun == 1 || (lm.HasValue && (lm.Value & 0xFF) == 0xFF) || (cu.HasValue && (cu.Value & 0xFF) == 0xFF))
                return Item("autorun", GSys, "Execução automática de mídias removíveis", "ok", "Desativada para pendrives, CDs e unidades de rede", false, "Desativada");
            var cur = lm ?? cu;
            return Item("autorun", GSys, "Execução automática de mídias removíveis", "warn", "Ativada — pendrives e CDs podem executar programas ao serem conectados.", true,
                cur.HasValue ? "Ativada (0x" + cur.Value.ToString("X2") + ")" : "Padrão do Windows", "Desativada (0xFF)");
        }

        // ---------- SMBv1 / RDP ----------
        static bool ServiceOn(string svc)
        {
            var k = @"HKLM\SYSTEM\CurrentControlSet\Services\" + svc;
            if (!Reg.KeyExists(k)) return false;
            return (Reg.GetInt(k, "Start") ?? 4) != 4;
        }

        static CareItem Smb1Item()
        {
            var client = ServiceOn("mrxsmb10");
            var server = ServiceOn("srv") && (Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "SMB1") ?? 1) != 0;
            if (!client && !server) return Item("smb1", GNet, "Protocolo SMBv1", "ok", "Desativado (protocolo antigo usado por ataques como o WannaCry)", false, "Desativado");
            var which = client && server ? "cliente e servidor" : client ? "cliente" : "servidor";
            return Item("smb1", GNet, "Protocolo SMBv1", "bad", "O SMBv1 (" + which + ") está ativo. É um protocolo antigo e inseguro, explorado por ataques como o WannaCry.", true,
                "Ativo (" + which + ")", "Desativado", reboot: true);
        }

        static CareItem RdpItem()
        {
            var deny = Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") ?? 1;
            if (deny != 0) return Item("rdp", GNet, "Área de Trabalho Remota", "ok", "Desativada", false, "Desativada");
            return Item("rdp", GNet, "Área de Trabalho Remota", "warn", "Ligada: o PC aceita conexões remotas. Desligue se você não usa.", false, "Ligada", "Desligada", "ms-settings:remotedesktop");
        }

        // ---------- Conta Convidado ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct USER_INFO_20 { public string name; public string full_name; public string comment; public uint flags; public uint user_id; }
        [StructLayout(LayoutKind.Sequential)] struct USER_INFO_1008 { public uint flags; }
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserEnum(string server, int level, int filter, out IntPtr buf, int max, out int read, out int total, ref int resume);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserSetInfo(string server, string user, int level, ref USER_INFO_1008 info, out int parmErr);
        [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr b);
        const uint UF_ACCOUNTDISABLE = 0x2;

        /// <summary>Conta interna de convidado (RID 501 — o nome muda com o idioma, ex.: "Convidado").</summary>
        static bool FindGuest(out string name, out uint flags)
        {
            name = null; flags = 0;
            int resume = 0;
            if (NetUserEnum(null, 20, 2 /*FILTER_NORMAL_ACCOUNT*/, out var buf, -1, out var read, out _, ref resume) != 0) return false;
            try
            {
                var size = Marshal.SizeOf(typeof(USER_INFO_20));
                for (int i = 0; i < read; i++)
                {
                    var u = (USER_INFO_20)Marshal.PtrToStructure(new IntPtr(buf.ToInt64() + i * size), typeof(USER_INFO_20));
                    if (u.user_id == 501) { name = u.name; flags = u.flags; return true; }
                }
            }
            finally { NetApiBufferFree(buf); }
            return false;
        }

        static CareItem GuestItem()
        {
            if (!FindGuest(out var name, out var flags)) return Item("guest", GAcc, "Conta Convidado", "ok", "Não existe neste PC", false, "Inexistente");
            if ((flags & UF_ACCOUNTDISABLE) != 0) return Item("guest", GAcc, "Conta Convidado", "ok", "Desativada", false, "Desativada");
            return Item("guest", GAcc, "Conta Convidado", "bad", "A conta \"" + name + "\" está ativa e permite entrar no PC sem senha.", true, "Ativa", "Desativada");
        }

        // ---------- Windows Update ----------
        static object Com(object o, string name, BindingFlags kind, params object[] args) => o.GetType().InvokeMember(name, kind, null, o, args);

        static readonly string[] DefenderKbs = { "KB2267602", "KB2461484", "KB4052623", "KB915597", "KB2310138" };

        internal static DateTime? LastWindowsUpdate()
        {
            object session = null, searcher = null, hist = null;
            try
            {
                session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
                searcher = Com(session, "CreateUpdateSearcher", BindingFlags.InvokeMethod);
                var total = Convert.ToInt32(Com(searcher, "GetTotalHistoryCount", BindingFlags.InvokeMethod));
                if (total <= 0) return null;
                hist = Com(searcher, "QueryHistory", BindingFlags.InvokeMethod, 0, Math.Min(total, 100));
                var count = Convert.ToInt32(Com(hist, "Count", BindingFlags.GetProperty));
                DateTime? best = null;
                for (int i = 0; i < count; i++)
                {
                    var e = Com(hist, "Item", BindingFlags.GetProperty, i);
                    try
                    {
                        var op = Convert.ToInt32(Com(e, "Operation", BindingFlags.GetProperty));
                        var rc = Convert.ToInt32(Com(e, "ResultCode", BindingFlags.GetProperty));
                        var title = Convert.ToString(Com(e, "Title", BindingFlags.GetProperty)) ?? "";
                        if (op != 1 || (rc != 2 && rc != 3)) continue; // instalação concluída
                        if (title.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0 || DefenderKbs.Any(k => title.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                        var d = Convert.ToDateTime(Com(e, "Date", BindingFlags.GetProperty));
                        if (!best.HasValue || d > best.Value) best = d;
                    }
                    finally { if (e != null && Marshal.IsComObject(e)) Marshal.ReleaseComObject(e); }
                }
                return best?.ToLocalTime();
            }
            catch (Exception ex) { Log.Warn("Histórico do Windows Update: " + ex.Message); return null; }
            finally
            {
                foreach (var o in new[] { hist, searcher, session }) if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

        static CareItem UpdateItem()
        {
            var last = LastWindowsUpdate();
            if (!last.HasValue) return Item("windowsupdate", GSys, "Windows Update", "warn", "Não há registro de atualizações do Windows instaladas recentemente.", false, null, null, "ms-settings:windowsupdate");
            var days = (DateTime.Now - last.Value).TotalDays;
            var when = "Última atualização instalada " + CareUtil.Days(last.Value) + " (" + last.Value.ToString("dd/MM/yyyy") + ")";
            return days <= 30
                ? Item("windowsupdate", GSys, "Windows Update", "ok", when, false, CareUtil.Days(last.Value))
                : Item("windowsupdate", GSys, "Windows Update", "warn", when + ". Procure atualizações no Windows Update.", false, CareUtil.Days(last.Value), "Nos últimos 30 dias", "ms-settings:windowsupdate");
        }

        // ---------- Correções ----------
        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                var r = new FixResult { Id = it.Id };
                try
                {
                    if (!Program.IsAdmin) r.Message = CareUtil.AdminMsg;
                    else FixOne(it.Tag as string, r, ctx);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException) { r.Ok = false; r.Message = "O Windows negou acesso a esta configuração."; }
                catch (Exception ex) { r.Ok = false; r.Message = ex.Message; Log.Warn("Segurança " + it.Id + ": " + ex.Message); }
                results.Add(r);
            }
            return results;
        }

        static string Backup(string label, params (string key, string value)[] values)
        {
            var sb = new StringBuilder();
            foreach (var v in values) Reg.AppendExport(sb, v.key, v.value);
            return sb.Length > 0 ? Reg.SaveBackup(label, sb) : null;
        }

        static void FixOne(string id, FixResult r, RpcContext ctx)
        {
            switch (id)
            {
                case "realtime":
                {
                    var ps = Proc.Ps("Set-MpPreference -DisableRealtimeMonitoring $false", 60000);
                    var d = DefenderStatus.Get();
                    r.Ok = d != null && d.RealTime;
                    r.Message = r.Ok ? "Proteção em tempo real ligada" : "O Windows não permitiu ligar a proteção" + (ps.Ok ? "" : " (" + Proc.CleanPsError(ps.Error) + ")") + ". Ligue em Segurança do Windows › Proteção contra vírus e ameaças.";
                    break;
                }
                case "signatures":
                {
                    var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
                    if (!File.Exists(exe)) { r.Message = "MpCmdRun.exe não encontrado."; return; }
                    var p = Proc.Run(exe, "-SignatureUpdate", 15 * 60000, null, l => { if (!string.IsNullOrWhiteSpace(l)) ctx.Progress(new { phase = "item", category = "security", line = l.Trim() }); }, ctx.Token);
                    r.Ok = p.Ok;
                    r.Message = p.Ok ? "Definições atualizadas" : "Falha ao atualizar as definições (código " + p.ExitCode + "). Verifique a conexão com a internet.";
                    break;
                }
                case "firewall":
                {
                    var fw = FwPolicy();
                    try
                    {
                        foreach (var p in Profiles)
                            if (!FwEnabled(fw, p.id)) fw.GetType().InvokeMember("FirewallEnabled", BindingFlags.SetProperty, null, fw, new object[] { p.id, true });
                        r.Ok = Profiles.All(p => FwEnabled(fw, p.id));
                    }
                    finally { Marshal.ReleaseComObject(fw); }
                    r.Message = r.Ok ? "Firewall ligado em todos os perfis" : "O firewall não pôde ser ligado (pode estar bloqueado por uma política).";
                    break;
                }
                case "uac":
                {
                    r.Backup = Backup("seguranca-uac", (UacKey, "EnableLUA"), (UacKey, "ConsentPromptBehaviorAdmin"));
                    var lua = Reg.GetInt(UacKey, "EnableLUA") ?? 1;
                    if (lua == 0) { Reg.Set(UacKey, "EnableLUA", 1, RegistryValueKind.DWord); r.NeedsReboot = true; }
                    if ((Reg.GetInt(UacKey, "ConsentPromptBehaviorAdmin") ?? 5) == 0) Reg.Set(UacKey, "ConsentPromptBehaviorAdmin", 5, RegistryValueKind.DWord);
                    r.Ok = true;
                    r.Message = r.NeedsReboot ? "UAC ativado. Reinicie o PC para valer." : "UAC configurado para notificar";
                    break;
                }
                case "smartscreen":
                {
                    r.Backup = Backup("seguranca-smartscreen", (ExplorerKey, "SmartScreenEnabled"));
                    Reg.Set(ExplorerKey, "SmartScreenEnabled", "Warn", RegistryValueKind.String);
                    r.Ok = true; r.Message = "SmartScreen ligado";
                    break;
                }
                case "autorun":
                {
                    r.Backup = Backup("seguranca-autorun", (ExplorerPol, "NoDriveTypeAutoRun"));
                    Reg.Set(ExplorerPol, "NoDriveTypeAutoRun", 0xFF, RegistryValueKind.DWord);
                    r.Ok = true; r.Message = "Execução automática desativada";
                    break;
                }
                case "smb1":
                {
                    var p = Proc.Ps("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -ErrorAction SilentlyContinue | Out-Null;" +
                                    "Set-SmbServerConfiguration -EnableSMB1Protocol $false -Force -ErrorAction SilentlyContinue | Out-Null; 'ok'", 10 * 60000, null, ctx.Token);
                    r.Ok = p.Ok;
                    r.NeedsReboot = true;
                    r.Message = p.Ok ? "SMBv1 desativado. Reinicie o PC para concluir." : "Não foi possível desativar o SMBv1: " + Proc.CleanPsError(p.Error);
                    break;
                }
                case "guest":
                {
                    if (!FindGuest(out var name, out var flags)) { r.Ok = true; r.Message = "A conta não existe."; break; }
                    var info = new USER_INFO_1008 { flags = flags | UF_ACCOUNTDISABLE };
                    var rc = NetUserSetInfo(null, name, 1008, ref info, out _);
                    r.Ok = rc == 0;
                    r.Message = r.Ok ? "Conta Convidado desativada" : "Não foi possível desativar a conta (código " + rc + ").";
                    break;
                }
                default:
                    r.Message = "Este item precisa ser resolvido manualmente.";
                    break;
            }
        }
    }
}
