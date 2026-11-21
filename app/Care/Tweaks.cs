using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Um valor de registro gravado por um ajuste.</summary>
    internal sealed class RegOp
    {
        public string Path, Name;
        public object Value;
        public RegistryValueKind Kind;
        public object Default;            // padrão do Windows usado no "reverter" sem backup (null = apagar o valor)
        public object AbsentAs;           // valor efetivo quando o valor não existe
        public Func<object, bool> Match;  // o valor atual já atende? (padrão: igual a Value)
    }

    internal sealed class Tweak
    {
        public string Id, Category, Title, Desc, Note, RecommendedText;
        public string Risk = "safe";
        public bool NeedsReboot, Admin, OneShot;
        public Func<bool> Recommended = () => true;
        public Func<bool> Available = () => true;
        public Func<List<RegOp>> Ops = () => new List<RegOp>();
        public Func<bool> Applied;               // substitui a verificação pelos valores de registro
        public Action<TweakBackup> Capture;      // guarda estado extra antes de aplicar (só se ainda não houver)
        public Action Apply;                     // ação extra depois de gravar o registro
        public Action<TweakBackup> Revert;       // ação extra depois de restaurar o registro (backup pode ser null)
        public Func<string> Current;             // texto do valor atual
        public Func<string> DescNow;             // descrição dinâmica
    }

    public sealed class TweakBackupValue
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public bool Existed { get; set; }
        public string Kind { get; set; }
        public long? Num { get; set; }
        public string Str { get; set; }
        public string[] Multi { get; set; }
        public string Bin { get; set; }
    }

    public sealed class TweakBackup
    {
        public DateTime At { get; set; }
        public List<TweakBackupValue> Values { get; set; } = new List<TweakBackupValue>();
        public Dictionary<string, string> Extra { get; set; } = new Dictionary<string, string>();
    }

    public sealed class TweakBackupStore
    {
        public Dictionary<string, TweakBackup> Items { get; set; } = new Dictionary<string, TweakBackup>();
    }

    /// <summary>Motor de ajustes reversíveis (tweaks.list / tweaks.apply / tweaks.revert). O valor anterior fica em data\tweaks-backup.json.</summary>
    public static class TweakEngine
    {
        static readonly object Gate = new object();
        static List<Tweak> all;
        internal static List<Tweak> All { get { lock (Gate) return all ?? (all = TweakCatalog.Build()); } }
        static string StoreFile => Paths.File("tweaks-backup.json");

        internal static Tweak Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var t = All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (t != null) return t;
            // aceita também o id do item da verificação ("tweaks.tw.gamemode", "internet.net.dns")
            var dot = id.IndexOf('.');
            return dot > 0 ? All.FirstOrDefault(x => x.Id.Equals(id.Substring(dot + 1), StringComparison.OrdinalIgnoreCase)) : null;
        }

        // ---------- Valores ----------
        internal static object Read(string path, string name)
        {
            try { using (var k = Reg.Open(path)) return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); }
            catch { return null; }
        }

        internal static long? Num(object v)
        {
            switch (v)
            {
                case int i: return unchecked((uint)i);
                case uint u: return u;
                case long l: return l;
                case string s: return long.TryParse(s.Trim(), out var x) ? x : (long?)null;
                default: return null;
            }
        }

        static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            var na = Num(a); var nb = Num(b);
            if (na.HasValue && nb.HasValue) return na.Value == nb.Value;
            return string.Equals(Convert.ToString(a)?.Trim(), Convert.ToString(b)?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static object Effective(RegOp op) => Read(op.Path, op.Name) ?? op.AbsentAs;

        internal static bool Matches(RegOp op)
        {
            var v = Effective(op);
            return op.Match != null ? op.Match(v) : Same(v, op.Value);
        }

        internal static bool IsApplied(Tweak t)
        {
            if (t.OneShot) return false;
            try { return t.Applied != null ? t.Applied() : t.Ops().All(Matches); }
            catch { return false; }
        }

        static TweakBackupValue Capture(string path, string name)
        {
            var b = new TweakBackupValue { Path = path, Name = name };
            using (var k = Reg.Open(path))
            {
                if (k == null) return b;
                var v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) return b;
                var kind = k.GetValueKind(name);
                b.Existed = true;
                b.Kind = kind.ToString();
                switch (kind)
                {
                    case RegistryValueKind.DWord: b.Num = unchecked((uint)Convert.ToInt32(v)); break;
                    case RegistryValueKind.QWord: b.Num = Convert.ToInt64(v); break;
                    case RegistryValueKind.MultiString: b.Multi = (string[])v; break;
                    case RegistryValueKind.Binary: b.Bin = Convert.ToBase64String((byte[])v); break;
                    default: b.Str = Convert.ToString(v); break;
                }
            }
            return b;
        }

        static void Restore(TweakBackupValue v)
        {
            if (!v.Existed) { DeleteValue(v.Path, v.Name); return; }
            Enum.TryParse<RegistryValueKind>(v.Kind, out var kind);
            switch (kind)
            {
                case RegistryValueKind.DWord: Reg.Set(v.Path, v.Name, unchecked((int)(uint)(v.Num ?? 0)), kind); break;
                case RegistryValueKind.QWord: Reg.Set(v.Path, v.Name, v.Num ?? 0, kind); break;
                case RegistryValueKind.MultiString: Reg.Set(v.Path, v.Name, v.Multi ?? new string[0], kind); break;
                case RegistryValueKind.Binary: Reg.Set(v.Path, v.Name, Convert.FromBase64String(v.Bin ?? ""), kind); break;
                case RegistryValueKind.ExpandString: Reg.Set(v.Path, v.Name, v.Str ?? "", kind); break;
                default: Reg.Set(v.Path, v.Name, v.Str ?? "", RegistryValueKind.String); break;
            }
        }

        static void DeleteValue(string path, string name)
        {
            if (!Reg.KeyExists(path)) return;
            Reg.DeleteValue(path, name);
        }

        static TweakBackupStore LoadStore() => Json.Load(StoreFile, new TweakBackupStore()) ?? new TweakBackupStore();
        static void SaveStore(TweakBackupStore s) => Json.Save(StoreFile, s);

        static string Key(string path, string name) => (path + "|" + name).ToLowerInvariant();

        static string Friendly(Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is SecurityException)
                return Program.IsAdmin ? "O Windows negou acesso a esta configuração." : CareUtil.AdminMsg;
            return ex.Message;
        }

        // ---------- Aplicar / reverter ----------
        internal static FixResult Apply(Tweak t)
        {
            var r = new FixResult { Id = t.Id, NeedsReboot = t.NeedsReboot };
            if (t.Admin && !Program.IsAdmin) { r.Message = CareUtil.AdminMsg; return r; }
            if (!t.Available()) { r.Message = "Este ajuste não está disponível neste computador."; return r; }
            try
            {
                if (t.OneShot)
                {
                    t.Apply?.Invoke();
                    r.Ok = true; r.Message = "Concluído";
                    return r;
                }
                List<RegOp> ops;
                lock (Gate)
                {
                    var store = LoadStore();
                    if (!store.Items.TryGetValue(t.Id, out var b)) { b = new TweakBackup { At = DateTime.Now }; store.Items[t.Id] = b; }
                    ops = t.Ops();
                    foreach (var op in ops)
                        if (!b.Values.Any(v => Key(v.Path, v.Name) == Key(op.Path, op.Name))) b.Values.Add(Capture(op.Path, op.Name));
                    t.Capture?.Invoke(b);
                    SaveStore(store); // o valor anterior fica salvo antes de qualquer mudança
                }
                foreach (var op in ops) Reg.Set(op.Path, op.Name, op.Value, op.Kind);
                t.Apply?.Invoke();
                NetInfo.Invalidate();
                r.Ok = IsApplied(t);
                r.Message = r.Ok ? (t.NeedsReboot ? "Aplicado. Reinicie o PC para valer." : "Aplicado") : "O Windows não confirmou a alteração.";
            }
            catch (Exception ex) { Log.Warn("Ajuste " + t.Id + ": " + ex.Message); r.Ok = false; r.Message = Friendly(ex); }
            return r;
        }

        internal static FixResult Revert(Tweak t)
        {
            var r = new FixResult { Id = t.Id, NeedsReboot = t.NeedsReboot };
            if (t.OneShot) { r.Ok = true; r.NeedsReboot = false; r.Message = "Nada a desfazer: esta ação não deixa alterações."; return r; }
            if (t.Admin && !Program.IsAdmin) { r.Message = CareUtil.AdminMsg; return r; }
            try
            {
                lock (Gate)
                {
                    var store = LoadStore();
                    store.Items.TryGetValue(t.Id, out var b);
                    var done = new HashSet<string>();
                    foreach (var op in t.Ops())
                    {
                        var key = Key(op.Path, op.Name);
                        done.Add(key);
                        var prev = b?.Values.FirstOrDefault(v => Key(v.Path, v.Name) == key);
                        if (prev != null) Restore(prev);
                        else if (op.Default == null) DeleteValue(op.Path, op.Name);
                        else Reg.Set(op.Path, op.Name, op.Default, op.Kind);
                    }
                    if (b != null) foreach (var v in b.Values.Where(v => !done.Contains(Key(v.Path, v.Name)))) Restore(v);
                    t.Revert?.Invoke(b);
                    store.Items.Remove(t.Id);
                    SaveStore(store);
                }
                NetInfo.Invalidate();
                r.Ok = true;
                r.Message = t.NeedsReboot ? "Revertido. Reinicie o PC para valer." : "Revertido";
            }
            catch (Exception ex) { Log.Warn("Reverter " + t.Id + ": " + ex.Message); r.Ok = false; r.Message = Friendly(ex); }
            return r;
        }

        static bool SafeBool(Func<bool> f, bool def) { try { return f(); } catch { return def; } }
        static string SafeText(Func<string> f) { try { return f?.Invoke(); } catch { return null; } }

        internal static List<object> Items(string category)
        {
            var list = new List<object>();
            foreach (var t in All)
            {
                if (category != null && !t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                if (!SafeBool(t.Available, false)) continue;
                list.Add(new
                {
                    id = t.Id, category = t.Category, title = t.Title, desc = SafeText(t.DescNow) ?? t.Desc,
                    applied = IsApplied(t), recommended = SafeBool(t.Recommended, false), needsReboot = t.NeedsReboot, risk = t.Risk,
                    valueText = SafeText(t.Current), recommendedText = t.RecommendedText, note = t.Note, admin = t.Admin, oneShot = t.OneShot,
                });
            }
            return list;
        }

        // ---------- RPCs ----------
        [Rpc("tweaks.list")]
        public static object List(string category) => Items(string.IsNullOrWhiteSpace(category) ? null : category);

        [Rpc("tweaks.apply")]
        public static object ApplyRpc(List<string> ids, string category, RpcContext ctx) => Run(ids, category, ctx, true);

        [Rpc("tweaks.revert")]
        public static object RevertRpc(List<string> ids, string category, RpcContext ctx) => Run(ids, category, ctx, false);

        static object Run(List<string> ids, string category, RpcContext ctx, bool apply)
        {
            if (ids == null || ids.Count == 0) throw new RpcException("Nenhum ajuste selecionado.");
            var results = new List<FixResult>();
            int i = 0;
            foreach (var id in ids)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var t = Find(id);
                ctx.Progress(new { phase = "item", id, title = t?.Title, index = i++, total = ids.Count });
                if (t == null) { results.Add(new FixResult { Id = id, Message = "Ajuste desconhecido." }); continue; }
                var r = apply ? Apply(t) : Revert(t);
                r.Id = id;
                results.Add(r);
            }
            var ok = results.Where(r => r.Ok).ToList();
            if (ok.Count > 0)
            {
                var titles = ok.Select(r => Find(r.Id)?.Title).Where(x => x != null).ToList();
                Activity.Add("fix", apply ? "tune" : "undo",
                    apply ? (ok.Count == 1 ? "Ajuste aplicado" : ok.Count + " ajustes aplicados") : (ok.Count == 1 ? "Ajuste revertido" : ok.Count + " ajustes revertidos"),
                    string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? " e mais " + (titles.Count - 3) : ""));
            }
            return new { results, items = Items(string.IsNullOrWhiteSpace(category) ? null : category) };
        }

        // ---------- Verificação inteligente ----------
        static string GroupName(string cat)
        {
            switch (cat)
            {
                case "gaming": return "Jogos";
                case "system": return "Sistema";
                case "privacy": return "Privacidade";
                case "internet": return "Rede";
                default: return cat;
            }
        }

        internal static List<CareItem> Pending(string scannerCategory, string[] cats, RpcContext ctx)
        {
            var items = new List<CareItem>();
            foreach (var t in All)
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (!cats.Contains(t.Category)) continue;
                if (!SafeBool(t.Available, false) || !SafeBool(t.Recommended, false) || IsApplied(t)) continue;
                var note = t.Note;
                if (t.Admin && !Program.IsAdmin) note = (note == null ? "" : note + " ") + "Requer administrador.";
                items.Add(new CareItem
                {
                    Id = scannerCategory + "." + t.Id, Category = scannerCategory, Group = GroupName(t.Category),
                    Title = t.Title, Detail = SafeText(t.DescNow) ?? t.Desc, Current = SafeText(t.Current), Recommended = t.RecommendedText,
                    NeedsReboot = t.NeedsReboot, Risk = t.Risk, Note = note, Selected = true, Tag = t.Id,
                });
            }
            return items;
        }

        internal static List<FixResult> FixItems(string category, List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                var t = Find(it.Tag as string ?? it.Id);
                var r = t == null ? new FixResult { Message = "Ajuste desconhecido." } : Apply(t);
                r.Id = it.Id;
                results.Add(r);
            }
            return results;
        }
    }

    /// <summary>Categoria "tweaks" — ajustes recomendados de jogos, sistema e privacidade ainda não aplicados.</summary>
    public sealed class TweaksScanner : ICareScanner
    {
        public string Category => "tweaks";
        public string Title => "Otimização do sistema";
        public string Description => "Ajustes de desempenho e jogos";
        public string Icon => "tune";
        public int Order => 60;
        public bool DefaultOn => true;
        public List<CareItem> Scan(RpcContext ctx) => TweakEngine.Pending(Category, new[] { "gaming", "system", "privacy" }, ctx);
        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx) => TweakEngine.FixItems(Category, items, ctx);
    }

    /// <summary>Categoria "internet" — ajustes de rede recomendados ainda não aplicados.</summary>
    public sealed class InternetScanner : ICareScanner
    {
        public string Category => "internet";
        public string Title => "Otimização da internet";
        public string Description => "TCP, DNS e latência";
        public string Icon => "network_check";
        public int Order => 70;
        public bool DefaultOn => true;
        public List<CareItem> Scan(RpcContext ctx) => TweakEngine.Pending(Category, new[] { "internet" }, ctx);
        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx) => TweakEngine.FixItems(Category, items, ctx);
    }

    /// <summary>Estado global do TCP lido por WMI (estruturado, sem texto localizado do netsh). Guardado por alguns segundos.</summary>
    internal static class NetInfo
    {
        static readonly object Gate = new object();
        static DateTime at;
        static int? autoTuning, ecn, rss;

        public static void Invalidate() { lock (Gate) at = DateTime.MinValue; }

        static void Load()
        {
            lock (Gate)
            {
                if ((DateTime.Now - at).TotalSeconds < 5) return;
                autoTuning = ecn = rss = null;
                const string scope = @"root\StandardCimv2";
                foreach (var o in CareUtil.Wmi(scope, "SELECT SettingName, AutoTuningLevelLocal, EcnCapability FROM MSFT_NetTCPSetting WHERE SettingName='Internet'", 5000))
                {
                    var a = CareUtil.Prop(o, "AutoTuningLevelLocal"); if (a != null) autoTuning = Convert.ToInt32(a);
                    var e = CareUtil.Prop(o, "EcnCapability"); if (e != null) ecn = Convert.ToInt32(e);
                }
                foreach (var o in CareUtil.Wmi(scope, "SELECT ReceiveSideScaling FROM MSFT_NetOffloadGlobalSetting", 5000))
                {
                    var v = CareUtil.Prop(o, "ReceiveSideScaling"); if (v != null) rss = Convert.ToInt32(v);
                }
                at = DateTime.Now;
            }
        }

        public static int? AutoTuning { get { Load(); return autoTuning; } }
        public static int? Ecn { get { Load(); return ecn; } }
        public static int? Rss { get { Load(); return rss; } }

        public static readonly string[] LevelNames = { "disabled", "highlyrestricted", "restricted", "normal", "experimental" };
        public static readonly string[] LevelText = { "Desativado", "Muito restrito", "Restrito", "Normal", "Experimental" };

        public static void Netsh(string args)
        {
            var r = Proc.Run(Proc.SysExe("netsh.exe"), args, 30000);
            if (!r.Ok) throw new Exception("O netsh não aceitou a alteração (código " + r.ExitCode + ").");
        }
    }

    /// <summary>Lista de ajustes disponíveis.</summary>
    internal static class TweakCatalog
    {
        const string GameBar = @"HKCU\Software\Microsoft\GameBar";
        const string GameStore = @"HKCU\System\GameConfigStore";
        const string GameDvr = @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR";
        const string Gfx = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        const string DxPrefs = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
        const string SysProfile = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        const string GamesTask = SysProfile + @"\Tasks\Games";
        const string Mouse = @"HKCU\Control Panel\Mouse";
        const string Desktop = @"HKCU\Control Panel\Desktop";
        const string Control = @"HKLM\SYSTEM\CurrentControlSet\Control";
        const string Advanced = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string TcpIfaces = @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

        static RegOp D(string path, string name, int value, int? def, int? absentAs = null, Func<object, bool> match = null)
            => new RegOp { Path = path, Name = name, Value = value, Kind = RegistryValueKind.DWord, Default = def, AbsentAs = absentAs, Match = match };

        static RegOp S(string path, string name, string value, string def, string absentAs = null, Func<object, bool> match = null)
            => new RegOp { Path = path, Name = name, Value = value, Kind = RegistryValueKind.String, Default = def, AbsentAs = absentAs, Match = match };

        static Func<object, bool> AtMost(long max) => v => { var n = TweakEngine.Num(v); return n.HasValue && n.Value <= max; };
        static long? N(string path, string name) => TweakEngine.Num(TweakEngine.Read(path, name));
        static string OnOff(bool on, string yes = "Ligado", string no = "Desligado") => on ? yes : no;

        // ----- Win32 -----
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint uiParam, int[] pvParam, uint winIni);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SystemParametersInfo(uint action, uint uiParam, IntPtr pvParam, uint winIni);
        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")] static extern int DnsFlushResolverCache();
        [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(IntPtr caps);
        const uint SPI_SETMOUSE = 0x0004, SPI_SETMENUSHOWDELAY = 0x006B, SPIF_SENDCHANGE = 0x2;

        static void ApplyMouseNow()
        {
            int P(string n, int def) => (int)(N(Mouse, n) ?? def);
            SystemParametersInfo(SPI_SETMOUSE, 0, new[] { P("MouseThreshold1", 6), P("MouseThreshold2", 10), P("MouseSpeed", 1) }, SPIF_SENDCHANGE);
        }

        static void ApplyMenuDelayNow() => SystemParametersInfo(SPI_SETMENUSHOWDELAY, (uint)(N(Desktop, "MenuShowDelay") ?? 400), IntPtr.Zero, SPIF_SENDCHANGE);

        static void RefreshExplorer() { try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

        internal static bool? HiberFilePresent()
        {
            var buf = Marshal.AllocHGlobal(256);
            try
            {
                for (int i = 0; i < 256; i++) Marshal.WriteByte(buf, i, 0);
                if (!GetPwrCapabilities(buf)) return null;
                return Marshal.ReadByte(buf, 8) != 0; // SYSTEM_POWER_CAPABILITIES.HiberFilePresent
            }
            catch { return null; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal static long HiberFileSize()
        {
            var root = System.IO.Path.GetPathRoot(Fs.Windows) ?? "C:\\";
            var st = Fs.Stat(System.IO.Path.Combine(root, "hiberfil.sys"));
            return st?.Size ?? 0;
        }

        static void Powercfg(string args)
        {
            var r = Proc.Run(Proc.SysExe("powercfg.exe"), args, 60000);
            if (!r.Ok) throw new Exception("O powercfg não aceitou a alteração (código " + r.ExitCode + ").");
        }

        // ----- Agendamento de GPU (D3DKMT) -----
        [StructLayout(LayoutKind.Sequential)] struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)] struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 p);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref uint hAdapter);

        static bool hagsRead;
        static bool? hagsSupported, hagsEnabled;

        /// <summary>Lê KMTQAITYPE_WDDM_2_7_CAPS de cada adaptador: bit 0 = suportado, bit 1 = ligado.</summary>
        static void ReadHags()
        {
            if (hagsRead) return;
            hagsRead = true;
            try
            {
                var e = new ENUMADAPTERS2();
                if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return;
                const int size = 20; // D3DKMT_ADAPTERINFO
                e.pAdapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
                try
                {
                    if (D3DKMTEnumAdapters2(ref e) != 0) return;
                    bool sup = false, on = false;
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var h = (uint)Marshal.ReadInt32(e.pAdapters, i * size);
                        var data = Marshal.AllocHGlobal(4);
                        try
                        {
                            Marshal.WriteInt32(data, 0);
                            var q = new QUERYADAPTERINFO { hAdapter = h, Type = 70 /*KMTQAITYPE_WDDM_2_7_CAPS*/, pPrivateDriverData = data, PrivateDriverDataSize = 4 };
                            if (D3DKMTQueryAdapterInfo(ref q) == 0)
                            {
                                var caps = Marshal.ReadInt32(data);
                                if ((caps & 1) != 0) sup = true;
                                if ((caps & 2) != 0) on = true;
                            }
                        }
                        finally { Marshal.FreeHGlobal(data); D3DKMTCloseAdapter(ref h); }
                    }
                    hagsSupported = sup; hagsEnabled = on;
                }
                finally { Marshal.FreeHGlobal(e.pAdapters); }
            }
            catch (Exception ex) { Log.Warn("HAGS: " + ex.Message); }
        }

        // ----- DirectXUserGlobalSettings ("Chave=Valor;Chave=Valor;") -----
        static string MergeDx(string current, string key, string value)
        {
            var parts = (current ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            parts.RemoveAll(p => p.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            if (value != null) parts.Add(key + "=" + value);
            return parts.Count == 0 ? null : string.Join(";", parts) + ";";
        }

        static string DxValue(string key)
        {
            var cur = TweakEngine.Read(DxPrefs, "DirectXUserGlobalSettings") as string ?? "";
            foreach (var p in cur.Split(';'))
            {
                var kv = p.Split('=');
                if (kv.Length == 2 && kv[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return kv[1].Trim();
            }
            return null;
        }

        // ----- Interfaces de rede com IPv4 (para o Nagle) -----
        static List<string> ActiveInterfaces()
        {
            var l = new List<string>();
            foreach (var id in CareUtil.SubKeys(TcpIfaces))
            {
                var kp = TcpIfaces + "\\" + id;
                bool Has(object v)
                {
                    if (v is string s) return s.Length > 0 && s != "0.0.0.0";
                    if (v is string[] a) return a.Any(x => !string.IsNullOrWhiteSpace(x) && x != "0.0.0.0");
                    return false;
                }
                if (Has(TweakEngine.Read(kp, "DhcpIPAddress")) || Has(TweakEngine.Read(kp, "IPAddress"))) l.Add(kp);
            }
            return l;
        }

        static void SetService(string name, bool start)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (start) { if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); }
                    else if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); }
                }
            }
            catch (Exception ex) { Log.Warn("Serviço " + name + ": " + ex.Message); }
        }

        public static List<Tweak> Build()
        {
            var win11 = CareUtil.Build() >= 22000;
            var list = new List<Tweak>();

            // ================= Jogos =================
            list.Add(new Tweak
            {
                Id = "tw.gamemode", Category = "gaming", Title = "Modo de Jogo do Windows", RecommendedText = "Ligado",
                Desc = "Dá prioridade ao jogo em primeiro plano e evita que o Windows Update instale drivers ou mostre avisos durante a partida.",
                Ops = () => new List<RegOp> { D(GameBar, "AutoGameModeEnabled", 1, null, 1), D(GameBar, "AllowAutoGameMode", 1, null, 1) },
                Current = () => OnOff(TweakEngine.Num(TweakEngine.Read(GameBar, "AutoGameModeEnabled")) != 0),
            });
            Tweak dvr = null;
            dvr = new Tweak
            {
                Id = "tw.gamedvr", Category = "gaming", Title = "Desligar gravação em segundo plano (Game DVR)", RecommendedText = "Desligado",
                Desc = "Desliga a captura de jogos da Xbox Game Bar, que grava em segundo plano, consome GPU e disco e pode custar FPS.",
                Ops = () => new List<RegOp> { D(GameStore, "GameDVR_Enabled", 0, 1, 1), D(GameDvr, "AppCaptureEnabled", 0, null, 0), D(GameDvr, "HistoricalCaptureEnabled", 0, null, 0) },
                Current = () => OnOff(!TweakEngine.IsApplied(dvr)),
            };
            list.Add(dvr);
            list.Add(new Tweak
            {
                Id = "tw.hags", Category = "gaming", Title = "Agendamento de GPU acelerado por hardware", RecommendedText = "Ligado", NeedsReboot = true, Admin = true,
                Desc = "A própria placa de vídeo organiza a fila de trabalho, o que reduz a latência e o uso de CPU em GPUs recentes.",
                Available = () => { ReadHags(); return hagsSupported != false; },
                Recommended = () => { ReadHags(); return hagsSupported == true; },
                Ops = () => new List<RegOp> { D(Gfx, "HwSchMode", 2, null) },
                Applied = () => { ReadHags(); var m = N(Gfx, "HwSchMode"); return m == 2 || (m == null && hagsEnabled == true); },
                Current = () => { ReadHags(); var m = N(Gfx, "HwSchMode"); return m == 2 ? "Ligado" : m == 1 ? "Desligado" : hagsEnabled == true ? "Ligado (padrão do driver)" : "Desligado"; },
            });
            list.Add(new Tweak
            {
                Id = "tw.windowed", Category = "gaming", Title = "Otimizações para jogos em janela", RecommendedText = "Ligado",
                Desc = "Usa o modelo de apresentação moderno do DirectX em jogos em janela ou sem bordas: menos latência e suporte a taxa de atualização variável.",
                Available = () => win11,
                Ops = () =>
                {
                    var cur = TweakEngine.Read(DxPrefs, "DirectXUserGlobalSettings") as string;
                    return new List<RegOp> { new RegOp { Path = DxPrefs, Name = "DirectXUserGlobalSettings", Kind = RegistryValueKind.String, Value = MergeDx(cur, "SwapEffectUpgradeEnable", "1"), Default = MergeDx(cur, "SwapEffectUpgradeEnable", null) } };
                },
                Applied = () => DxValue("SwapEffectUpgradeEnable") == "1",
                Current = () => OnOff(DxValue("SwapEffectUpgradeEnable") == "1"),
            });
            Tweak mmcss = null;
            mmcss = new Tweak
            {
                Id = "tw.mmcss", Category = "gaming", Title = "Prioridade de jogos no agendador multimídia", RecommendedText = "Jogos em prioridade alta", Admin = true,
                Desc = "Reserva menos CPU para tarefas em segundo plano e dá prioridade alta de CPU, GPU e disco aos jogos (MMCSS).",
                Ops = () => new List<RegOp>
                {
                    D(SysProfile, "SystemResponsiveness", 10, 20, 20, AtMost(10)),
                    D(GamesTask, "GPU Priority", 8, 8, 8), D(GamesTask, "Priority", 6, 2, 2),
                    S(GamesTask, "Scheduling Category", "High", "Medium", "Medium"), S(GamesTask, "SFIO Priority", "High", "Normal", "Normal"),
                },
                Current = () => TweakEngine.IsApplied(mmcss) ? "Jogos em prioridade alta" : "Padrão (reserva de " + (N(SysProfile, "SystemResponsiveness") ?? 20) + "% para segundo plano)",
            };
            list.Add(mmcss);
            list.Add(new Tweak
            {
                Id = "tw.mouse", Category = "gaming", Title = "Desligar aceleração do mouse", RecommendedText = "Desligada",
                Desc = "Desativa \"Aumentar a precisão do ponteiro\": o cursor anda na mesma proporção da mão, deixando a mira consistente.",
                Ops = () => new List<RegOp> { S(Mouse, "MouseSpeed", "0", "1", "1"), S(Mouse, "MouseThreshold1", "0", "6", "6"), S(Mouse, "MouseThreshold2", "0", "10", "10") },
                Apply = ApplyMouseNow, Revert = _ => ApplyMouseNow(),
                Current = () => OnOff((N(Mouse, "MouseSpeed") ?? 1) != 0, "Ligada", "Desligada"),
            });
            list.Add(new Tweak
            {
                Id = "tw.priority", Category = "gaming", Title = "Prioridade máxima ao programa em primeiro plano", RecommendedText = "0x26", Admin = true,
                Desc = "Dá fatias de CPU mais longas e fixas ao app em primeiro plano (Win32PrioritySeparation = 0x26).",
                Note = "Pode deixar tarefas em segundo plano (downloads, gravação, streaming) mais lentas.",
                Recommended = () => false,
                Ops = () => new List<RegOp> { D(Control + @"\PriorityControl", "Win32PrioritySeparation", 0x26, 2, 2) },
                Current = () => { var v = N(Control + @"\PriorityControl", "Win32PrioritySeparation") ?? 2; return v == 2 ? "Padrão do Windows (0x2)" : "0x" + v.ToString("X"); },
            });
            list.Add(new Tweak
            {
                Id = "tw.powerthrottling", Category = "gaming", Title = "Desativar a limitação de energia (Power Throttling)", RecommendedText = "Desativada", Admin = true, NeedsReboot = true,
                Desc = "Impede o Windows de reduzir a velocidade da CPU para apps em segundo plano.",
                Note = "Não recomendado em notebooks usando a bateria: aumenta o consumo e o calor.",
                Recommended = () => !CareUtil.HasBattery(),
                Ops = () => new List<RegOp> { D(Control + @"\Power\PowerThrottling", "PowerThrottlingOff", 1, null, 0) },
                Current = () => OnOff((N(Control + @"\Power\PowerThrottling", "PowerThrottlingOff") ?? 0) == 0, "Ativa", "Desativada"),
            });

            // ================= Sistema =================
            list.Add(new Tweak
            {
                Id = "tw.menu", Category = "system", Title = "Menus mais rápidos", RecommendedText = "100 ms",
                Desc = "Reduz o atraso para abrir submenus de 400 ms para 100 ms.",
                Ops = () => new List<RegOp> { S(Desktop, "MenuShowDelay", "100", "400", "400", AtMost(100)) },
                Apply = ApplyMenuDelayNow, Revert = _ => ApplyMenuDelayNow(),
                Current = () => (N(Desktop, "MenuShowDelay") ?? 400) + " ms",
            });
            list.Add(new Tweak
            {
                Id = "tw.hungapps", Category = "system", Title = "Encerrar apps travados mais rápido", RecommendedText = "3 s / 5 s", Risk = "caution",
                Desc = "Ao desligar, fecha sozinho os apps que não respondem (AutoEndTasks), esperando 3 s por apps travados e 5 s pelos demais.",
                Note = "Apps com trabalho não salvo podem ser fechados sem perguntar ao desligar.",
                Recommended = () => false,
                Ops = () => new List<RegOp>
                {
                    S(Desktop, "AutoEndTasks", "1", "0", "0"),
                    S(Desktop, "HungAppTimeout", "3000", "5000", "5000", AtMost(3000)),
                    S(Desktop, "WaitToKillAppTimeout", "5000", "20000", "20000", AtMost(5000)),
                },
                Current = () => "Espera " + ((N(Desktop, "HungAppTimeout") ?? 5000) / 1000.0).ToString("0.#") + " s / " + ((N(Desktop, "WaitToKillAppTimeout") ?? 20000) / 1000.0).ToString("0.#") + " s"
                                + ((N(Desktop, "AutoEndTasks") ?? 0) == 1 ? ", fecha sozinho" : ", pergunta antes"),
            });
            list.Add(new Tweak
            {
                Id = "tw.svctimeout", Category = "system", Title = "Desligamento mais rápido de serviços", RecommendedText = "2 s", Admin = true,
                Desc = "Reduz de 5 para 2 segundos o tempo que o Windows espera os serviços encerrarem ao desligar.",
                Ops = () => new List<RegOp> { S(Control, "WaitToKillServiceTimeout", "2000", "5000", "5000", AtMost(2000)) },
                Current = () => ((N(Control, "WaitToKillServiceTimeout") ?? 5000) / 1000.0).ToString("0.#") + " s",
            });
            list.Add(new Tweak
            {
                Id = "tw.8dot3", Category = "system", Title = "Desativar nomes curtos 8.3 do NTFS", RecommendedText = "Desativados", Admin = true, NeedsReboot = true, Risk = "caution",
                Desc = "Para de criar um nome no formato antigo (ex.: PROGRA~1) para cada arquivo novo, o que acelera pastas com muitos arquivos.",
                Note = "Programas muito antigos podem depender de nomes curtos.",
                Ops = () => new List<RegOp> { D(Control + @"\FileSystem", "NtfsDisable8dot3NameCreation", 1, 2, 2) },
                Current = () =>
                {
                    switch (N(Control + @"\FileSystem", "NtfsDisable8dot3NameCreation") ?? 2)
                    {
                        case 0: return "Ativados em todas as unidades";
                        case 1: return "Desativados";
                        case 3: return "Desativados (exceto no disco do sistema)";
                        default: return "Definido por unidade";
                    }
                },
            });
            list.Add(new Tweak
            {
                Id = "tw.hibernate", Category = "system", Title = "Desligar a hibernação", RecommendedText = "Desligada", Admin = true, Risk = "caution",
                Desc = "Apaga o hiberfil.sys e libera espaço no disco do sistema.",
                DescNow = () => { var s = HiberFileSize(); return s > 0 ? "Apaga o hiberfil.sys e libera " + Fmt.Bytes(s) + " no disco do sistema." : "Apaga o hiberfil.sys e libera espaço no disco do sistema."; },
                Note = "Sem hibernação o PC não pode hibernar e a Inicialização Rápida do Windows fica desligada.",
                Recommended = () => false,
                Available = () => HiberFilePresent() != null,
                Applied = () => HiberFilePresent() == false,
                Capture = b => { if (!b.Extra.ContainsKey("hibernate")) b.Extra["hibernate"] = HiberFilePresent() == false ? "off" : "on"; },
                Apply = () => Powercfg("/hibernate off"),
                Revert = b => { string prev = null; if (b == null || !b.Extra.TryGetValue("hibernate", out prev) || prev != "off") Powercfg("/hibernate on"); },
                Current = () => HiberFilePresent() == true ? "Ligada (hiberfil.sys: " + Fmt.Bytes(HiberFileSize()) + ")" : "Desligada",
            });
            list.Add(new Tweak
            {
                Id = "tw.fileext", Category = "system", Title = "Mostrar extensões de arquivos", RecommendedText = "Visíveis",
                Desc = "Exibe a extensão (.exe, .pdf, .jpg) no Explorador de Arquivos e ajuda a identificar arquivos perigosos disfarçados.",
                Ops = () => new List<RegOp> { D(Advanced, "HideFileExt", 0, 1, 1) },
                Apply = RefreshExplorer, Revert = _ => RefreshExplorer(),
                Current = () => (N(Advanced, "HideFileExt") ?? 1) == 0 ? "Visíveis" : "Ocultas",
            });

            // ================= Privacidade =================
            Tweak tele = null;
            tele = new Tweak
            {
                Id = "tw.telemetry", Category = "privacy", Title = "Desligar a telemetria", RecommendedText = "Desligada", Admin = true,
                Desc = "Desativa o serviço Experiências do Usuário Conectado e Telemetria (DiagTrack) e limita o envio de dados de diagnóstico.",
                Ops = () => new List<RegOp>
                {
                    D(@"HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start", 4, 2, 2),
                    D(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null),
                },
                Apply = () => SetService("DiagTrack", false),
                Revert = _ => { if ((N(@"HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start") ?? 2) != 4) SetService("DiagTrack", true); },
                Current = () =>
                {
                    if (TweakEngine.IsApplied(tele)) return "Desligada";
                    var svcOff = (N(@"HKLM\SYSTEM\CurrentControlSet\Services\DiagTrack", "Start") ?? 2) == 4;
                    return svcOff ? "Parcial (serviço desativado, política não definida)" : "Ativa";
                },
            };
            list.Add(tele);
            Tweak ads = null;
            ads = new Tweak
            {
                Id = "tw.ads", Category = "privacy", Title = "Desligar dicas, sugestões e anúncios do Windows", RecommendedText = "Desligados",
                Desc = "Remove apps sugeridos no Menu Iniciar, dicas e propagandas do Windows e a instalação silenciosa de apps promovidos.",
                Ops = () => new[] { "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled",
                                    "SystemPaneSuggestionsEnabled", "SilentInstalledAppsEnabled", "SoftLandingEnabled" }.Select(n => D(Cdm, n, 0, 1, 1)).ToList(),
                Current = () => OnOff(!TweakEngine.IsApplied(ads), "Ligados", "Desligados"),
            };
            list.Add(ads);
            list.Add(new Tweak
            {
                Id = "tw.adid", Category = "privacy", Title = "Desligar o ID de publicidade", RecommendedText = "Desligado",
                Desc = "Impede que os apps usem um identificador do seu perfil para mostrar anúncios personalizados.",
                Ops = () => new List<RegOp> { D(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1, 1) },
                Current = () => OnOff((N(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled") ?? 1) != 0),
            });
            list.Add(new Tweak
            {
                Id = "tw.startrec", Category = "privacy", Title = "Desligar recomendações no Menu Iniciar", RecommendedText = "Desligadas",
                Desc = "Tira dicas, atalhos promovidos e apps recomendados da área \"Recomendado\" do Menu Iniciar.",
                Available = () => win11,
                Ops = () => new List<RegOp> { D(Advanced, "Start_IrisRecommendations", 0, null, 1) },
                Current = () => OnOff((N(Advanced, "Start_IrisRecommendations") ?? 1) != 0, "Ligadas", "Desligadas"),
            });
            list.Add(new Tweak
            {
                Id = "tw.bing", Category = "privacy", Title = "Desligar o Bing na pesquisa do Menu Iniciar", RecommendedText = "Desligado", NeedsReboot = true,
                Desc = "A pesquisa do Menu Iniciar passa a mostrar só resultados do computador, sem sugestões da web.",
                Ops = () => new List<RegOp> { D(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null, 0) },
                Current = () => OnOff((N(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions") ?? 0) != 1),
            });

            // ================= Internet =================
            list.Add(new Tweak
            {
                Id = "net.autotuning", Category = "internet", Title = "Ajuste automático da janela TCP", RecommendedText = "Normal", Admin = true,
                Desc = "Mantém o ajuste automático da janela de recebimento do TCP no nível Normal, o melhor para downloads e jogos.",
                Applied = () => (NetInfo.AutoTuning ?? 3) == 3,
                Capture = b => { var l = NetInfo.AutoTuning; if (!b.Extra.ContainsKey("level") && l.HasValue && l >= 0 && l < 5) b.Extra["level"] = NetInfo.LevelNames[l.Value]; },
                Apply = () => NetInfo.Netsh("int tcp set global autotuninglevel=normal"),
                Revert = b => { string lvl = null; if (b == null || !b.Extra.TryGetValue("level", out lvl)) lvl = "normal"; NetInfo.Netsh("int tcp set global autotuninglevel=" + lvl); },
                Current = () => { var l = NetInfo.AutoTuning; return l.HasValue && l >= 0 && l < 5 ? NetInfo.LevelText[l.Value] : null; },
            });
            list.Add(new Tweak
            {
                Id = "net.throttle", Category = "internet", Title = "Remover limitação de rede para multimídia", RecommendedText = "Desativada", Admin = true, NeedsReboot = true,
                Desc = "Desliga o limitador de pacotes que o Windows aplica à rede enquanto toca áudio ou vídeo (NetworkThrottlingIndex).",
                Ops = () => new List<RegOp> { D(SysProfile, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10, 10) },
                Current = () => { var v = N(SysProfile, "NetworkThrottlingIndex") ?? 10; return v == 0xFFFFFFFF ? "Desativada" : "Ativa (" + v + ")"; },
            });
            Tweak nagle = null;
            nagle = new Tweak
            {
                Id = "net.nagle", Category = "internet", Title = "Desligar o algoritmo de Nagle", RecommendedText = "Desligado", Admin = true, NeedsReboot = true,
                Desc = "Envia pacotes pequenos na hora, sem agrupar (TcpAckFrequency e TCPNoDelay nas placas de rede conectadas).",
                Note = "Só ajuda em alguns jogos online; não muda a velocidade da internet.",
                Available = () => ActiveInterfaces().Count > 0,
                Ops = () => ActiveInterfaces().SelectMany(k => new[] { D(k, "TcpAckFrequency", 1, null), D(k, "TCPNoDelay", 1, null) }).ToList(),
                Current = () => OnOff(!TweakEngine.IsApplied(nagle)),
            };
            list.Add(nagle);
            list.Add(new Tweak
            {
                Id = "net.dns", Category = "internet", Title = "Limpar o cache de DNS", RecommendedText = "Limpar", OneShot = true,
                Desc = "Apaga os endereços de sites guardados pelo Windows. Resolve sites que não abrem depois de mudar de rede ou de DNS.",
                Apply = () => { if (DnsFlushResolverCache() == 0) throw new Exception("O Windows não conseguiu limpar o cache de DNS."); },
            });
            list.Add(new Tweak
            {
                Id = "net.rss", Category = "internet", Title = "Ativar RSS (processamento de rede em vários núcleos)", RecommendedText = "Ligado", Admin = true,
                Desc = "Distribui o recebimento de pacotes entre vários núcleos da CPU, reduzindo gargalos em conexões rápidas.",
                Applied = () => (NetInfo.Rss ?? 1) == 1,
                Capture = b => { var v = NetInfo.Rss; if (!b.Extra.ContainsKey("rss") && v.HasValue) b.Extra["rss"] = v == 1 ? "enabled" : "disabled"; },
                Apply = () => NetInfo.Netsh("int tcp set global rss=enabled"),
                Revert = b => { string v = null; if (b == null || !b.Extra.TryGetValue("rss", out v)) v = "enabled"; NetInfo.Netsh("int tcp set global rss=" + v); },
                Current = () => NetInfo.Rss.HasValue ? OnOff(NetInfo.Rss == 1) : null,
            });
            list.Add(new Tweak
            {
                Id = "net.ecn", Category = "internet", Title = "Ativar ECN (aviso de congestionamento)", RecommendedText = "Ligado", Admin = true,
                Desc = "Com ECN, os roteadores avisam sobre congestionamento em vez de descartar pacotes.",
                Note = "Alguns roteadores antigos não lidam bem com ECN; reverta se notar quedas de conexão.",
                Recommended = () => false,
                Applied = () => (NetInfo.Ecn ?? 0) == 1,
                Capture = b => { var v = NetInfo.Ecn; if (!b.Extra.ContainsKey("ecn") && v.HasValue) b.Extra["ecn"] = v == 1 ? "enabled" : "disabled"; },
                Apply = () => NetInfo.Netsh("int tcp set global ecncapability=enabled"),
                Revert = b => { string v = null; if (b == null || !b.Extra.TryGetValue("ecn", out v)) v = "disabled"; NetInfo.Netsh("int tcp set global ecncapability=" + v); },
                Current = () => NetInfo.Ecn.HasValue ? OnOff(NetInfo.Ecn == 1) : null,
            });
            return list;
        }
    }
}
