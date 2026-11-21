using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Rage.Core;

namespace Rage.Core
{
    /// <summary>Item encontrado por uma verificação (lixo, registro, ajuste, etc.).</summary>
    public sealed class CareItem
    {
        public string Id { get; set; }               // único e estável dentro da categoria, ex.: "junk.wintemp"
        public string Category { get; set; }         // junk | privacy | shortcuts | registry | tweaks | internet | security | disk | startup | updates | antivirus
        public string Group { get; set; }            // subgrupo para a interface, ex.: "Navegadores"
        public string Title { get; set; }
        public string Detail { get; set; }
        public double? Size { get; set; }            // bytes
        public int? Count { get; set; }
        public bool Selected { get; set; } = true;   // seleção padrão recomendada
        public string Risk { get; set; } = "safe";   // safe | caution
        public string Note { get; set; }             // aviso exibido quando o item é marcado
        public string Current { get; set; }          // valor atual (ajustes)
        public string Recommended { get; set; }      // valor recomendado (ajustes)
        public bool NeedsReboot { get; set; }
        public bool Fixable { get; set; } = true;    // false = só informativo / correção manual
        public string Action { get; set; }           // ex.: "ms-settings:windowsdefender" para correção manual
        public string Status { get; set; }           // ok | warn | bad (segurança)
        public List<string> Paths { get; set; }      // até 5 caminhos de exemplo
        [JsonIgnore] public object Tag { get; set; } // dados internos do scanner (não vão para a interface)
    }

    public sealed class FixResult
    {
        public string Id { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; }
        public double Freed { get; set; }           // bytes liberados (lixo/privacidade)
        public bool NeedsReboot { get; set; }
        public string Backup { get; set; }           // arquivo de backup gerado, se houver
    }

    /// <summary>Uma categoria da Verificação inteligente. Implementações são descobertas por reflexão.</summary>
    public interface ICareScanner
    {
        string Category { get; }
        string Title { get; }
        string Description { get; }
        string Icon { get; }        // nome do ícone Material Symbols
        int Order { get; }
        bool DefaultOn { get; }
        List<CareItem> Scan(RpcContext ctx);
        List<FixResult> Fix(List<CareItem> items, RpcContext ctx);
    }

    /// <summary>Orquestra as categorias: care.categories, care.scan, care.fix.</summary>
    public static class CareRpc
    {
        static List<ICareScanner> scanners;
        static readonly Dictionary<string, CareItem> lastItems = new Dictionary<string, CareItem>();
        static object lastResult;
        static DateTime? lastScanAt;

        static List<ICareScanner> Scanners
        {
            get
            {
                if (scanners != null) return scanners;
                scanners = typeof(CareRpc).Assembly.GetTypes()
                    .Where(t => typeof(ICareScanner).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    .Select(t => (ICareScanner)Activator.CreateInstance(t))
                    .OrderBy(s => s.Order).ToList();
                return scanners;
            }
        }

        [Rpc("care.categories")]
        public static object Categories() => Scanners.Select(s => new { id = s.Category, title = s.Title, description = s.Description, icon = s.Icon, defaultOn = s.DefaultOn, order = s.Order }).ToList();

        [Rpc("care.scan")]
        public static object Scan(List<string> categories, RpcContext ctx)
        {
            var list = Scanners.Where(s => categories == null || categories.Count == 0 ? s.DefaultOn : categories.Contains(s.Category)).ToList();
            var cats = new List<object>();
            int i = 0;
            foreach (var s in list)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "scan", category = s.Category, title = s.Title, index = i, total = list.Count });
                var sw = Stopwatch.StartNew();
                List<CareItem> items;
                string error = null;
                try { items = s.Scan(ctx) ?? new List<CareItem>(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("Scan " + s.Category, ex); items = new List<CareItem>(); error = ex.Message; }
                foreach (var it in items) { it.Category = it.Category ?? s.Category; lock (lastItems) lastItems[it.Id] = it; }
                var issues = items.Count(x => x.Fixable && x.Status != "ok");
                cats.Add(new
                {
                    id = s.Category, title = s.Title, icon = s.Icon, items, error,
                    size = items.Where(x => x.Size.HasValue).Sum(x => x.Size.Value),
                    count = items.Sum(x => x.Count ?? 1),
                    issues,
                    durationMs = sw.ElapsedMilliseconds,
                });
                ctx.Progress(new { phase = "scanned", category = s.Category, title = s.Title, index = i, total = list.Count, issues, size = items.Where(x => x.Size.HasValue).Sum(x => x.Size.Value) });
                i++;
            }
            lastScanAt = DateTime.Now;
            lastResult = new { at = lastScanAt, categories = cats };
            return lastResult;
        }

        [Rpc("care.last")]
        public static object Last() => lastResult;

        [Rpc("care.fix")]
        public static object Fix(List<string> ids, RpcContext ctx)
        {
            if (ids == null || ids.Count == 0) throw new RpcException("Nenhum item selecionado.");
            List<CareItem> items;
            lock (lastItems) items = ids.Where(lastItems.ContainsKey).Select(x => lastItems[x]).ToList();
            if (items.Count == 0) throw new RpcException("Faça uma nova verificação antes de corrigir.");

            bool restorePoint = false;
            var risky = items.Any(x => x.Category == "registry" || x.Category == "tweaks" || x.Category == "internet" || x.Category == "security");
            if (risky && Settings.Current.RestorePointBeforeFix && Program.IsAdmin)
            {
                ctx.Progress(new { phase = "restore", title = "Criando ponto de restauração" });
                restorePoint = RestorePoint.Create("RAGE! - antes das correções", out _);
            }

            var results = new List<FixResult>();
            var groups = items.GroupBy(x => x.Category).ToList();
            int gi = 0;
            foreach (var g in groups)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var sc = Scanners.FirstOrDefault(s => s.Category == g.Key);
                if (sc == null) continue;
                ctx.Progress(new { phase = "fix", category = sc.Category, title = sc.Title, index = gi++, total = groups.Count });
                try { results.AddRange(sc.Fix(g.ToList(), ctx) ?? new List<FixResult>()); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error("Fix " + g.Key, ex);
                    results.AddRange(g.Select(x => new FixResult { Id = x.Id, Ok = false, Message = ex.Message }));
                }
            }
            lock (lastItems) foreach (var r in results.Where(r => r.Ok)) lastItems.Remove(r.Id);

            var freed = results.Sum(r => r.Freed);
            var ok = results.Count(r => r.Ok);
            var failed = results.Count(r => !r.Ok);
            Activity.Add("clean", "auto_fix_high", ok + " itens corrigidos",
                string.Join(", ", groups.Select(g => Scanners.FirstOrDefault(s => s.Category == g.Key)?.Title).Where(t => t != null)),
                freed > 0 ? Fmt.Bytes(freed) : null);
            return new
            {
                ok, failed, freed, restorePoint,
                needsReboot = results.Any(r => r.Ok && r.NeedsReboot),
                backups = results.Where(r => r.Backup != null).Select(r => r.Backup).Distinct().ToList(),
                details = results,
            };
        }
    }

    /// <summary>Pontos de restauração do sistema.</summary>
    public static class RestorePoint
    {
        public static bool Create(string description, out string error)
        {
            error = null;
            try
            {
                // O Windows limita a 1 ponto a cada 24 h; zera o limite temporariamente.
                const string key = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
                var prev = Reg.Get(key, "SystemRestorePointCreationFrequency");
                Reg.Set(key, "SystemRestorePointCreationFrequency", 0, Microsoft.Win32.RegistryValueKind.DWord);
                try
                {
                    var desc = (description ?? "RAGE!").Replace("'", "");
                    var r = Proc.Ps("Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue; Checkpoint-Computer -Description '" + desc + "' -RestorePointType MODIFY_SETTINGS", 180000);
                    if (!r.Ok) { error = Proc.CleanPsError(r.Error); Log.Warn("Ponto de restauração: " + error); return false; }
                }
                finally
                {
                    if (prev == null) Reg.DeleteValue(key, "SystemRestorePointCreationFrequency");
                    else Reg.Set(key, "SystemRestorePointCreationFrequency", prev, Microsoft.Win32.RegistryValueKind.DWord);
                }
                Activity.Add("info", "restore", "Ponto de restauração criado", description);
                return true;
            }
            catch (Exception ex) { error = ex.Message; Log.Error("Ponto de restauração", ex); return false; }
        }
    }
}
