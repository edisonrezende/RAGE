using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rage.Core;

namespace Rage.Boost
{
    /// <summary>
    /// "Envenenar" o PC: estágios de preparação persistentes (ficam até voltar ao Original, inclusive após reiniciar).
    /// Cada peça é um ajuste real de energia/Windows; tudo que é alterado é guardado e desfeito no "Voltar ao original".
    /// </summary>
    public static class Tuning
    {
        public sealed class StageDef
        {
            public int Level { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public string Tag { get; set; }
            public string Glyph { get; set; }
            public string Desc { get; set; }
            public int[] Stats { get; set; } // desempenho, temperatura, bateria, ruído (0-5)
        }

        public sealed class PartDef
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Real { get; set; }
            public string Icon { get; set; }
            public int MinStage { get; set; }
        }

        sealed class Saved
        {
            public string Scheme { get; set; }
            public string Overlay { get; set; }
            public int? BoostAc { get; set; }
        }

        sealed class State
        {
            public bool Active { get; set; }
            public string Stage { get; set; } = "stock";
            public DateTime? Since { get; set; }
            public Saved Prev { get; set; }
            public List<string> TweaksApplied { get; set; } = new List<string>();
            public List<string> Installed { get; set; } = new List<string>();
            public string RagePlan { get; set; }
        }

        public static readonly StageDef[] Stages =
        {
            new StageDef { Level = 0, Id = "stock", Name = "Original", Tag = "De fábrica", Glyph = "N", Stats = new[] { 2, 2, 5, 2 },
                Desc = "Como o Windows vem: equilibrado, frio e econômico." },
            new StageDef { Level = 1, Id = "street", Name = "Stage 1", Tag = "Rua", Glyph = "1", Stats = new[] { 3, 3, 4, 3 },
                Desc = "Turbo agressivo e modo de jogo. Mais resposta no dia a dia sem esquentar muito." },
            new StageDef { Level = 2, Id = "track", Name = "Stage 2", Tag = "Pista", Glyph = "2", Stats = new[] { 4, 4, 2, 4 },
                Desc = "Remap RAGE! Turbo: CPU sempre no topo e todos os núcleos acordados na tomada." },
            new StageDef { Level = 3, Id = "rage", Name = "RAGE!", Tag = "Envenenado", Glyph = "R", Stats = new[] { 5, 5, 1, 5 },
                Desc = "Tudo no máximo, inclusive na bateria, sem limitador e com prioridade de pista para os jogos." },
        };

        public static readonly PartDef[] Parts =
        {
            new PartDef { Id = "turbo", MinStage = 1, Icon = "speed", Name = "Turbo do processador",
                Real = "Turbo Boost em modo agressivo: a CPU sobe o clock antes e segura lá em cima por mais tempo." },
            new PartDef { Id = "mode", MinStage = 1, Icon = "bolt", Name = "Modo pista",
                Real = "Modo de energia do Windows em desempenho (o controle de Configurações › Sistema › Energia)." },
            new PartDef { Id = "nitro", MinStage = 1, Icon = "local_fire_department", Name = "Nitro",
                Real = "Modo de Jogo do Windows ligado e gravação em segundo plano (Game DVR) desligada." },
            new PartDef { Id = "map", MinStage = 2, Icon = "tune", Name = "Remap RAGE! Turbo",
                Real = "Plano de energia RAGE! Turbo, feito a partir do Desempenho Máximo, sem economia de PCIe e USB." },
            new PartDef { Id = "inject", MinStage = 2, Icon = "trending_up", Name = "Injeção direta",
                Real = "Na tomada o processador fica sempre no clock máximo (estado mínimo 100%): zero atraso para acelerar." },
            new PartDef { Id = "exhaust", MinStage = 2, Icon = "grid_view", Name = "Escape aberto",
                Real = "Todos os núcleos acordados: o Windows para de “estacionar” núcleos para economizar." },
            new PartDef { Id = "ecu", MinStage = 3, Icon = "flag", Name = "Prioridade de pista",
                Real = "Jogos em prioridade alta no agendador multimídia (MMCSS) e prioridade máxima ao app em primeiro plano." },
            new PartDef { Id = "limiter", MinStage = 3, Icon = "block", Name = "Sem limitador",
                Real = "Desliga a limitação de energia (Power Throttling) dos apps. Vale depois de reiniciar." },
            new PartDef { Id = "battery", MinStage = 3, Icon = "battery_charging_full", Name = "Bateria liberada",
                Real = "O mesmo desempenho fora da tomada. A bateria acaba bem mais rápido." },
            new PartDef { Id = "air", MinStage = 3, Icon = "air", Name = "Filtro de ar",
                Real = "Libera a memória RAM no momento em que o PC é envenenado." },
        };

        static readonly object Gate = new object();
        static State state;
        static string FilePath => Paths.File("tuning.json");

        static State Current { get { if (state == null) state = Json.Load(FilePath, new State()); return state; } }
        static void Save() { try { Json.Save(FilePath, state); } catch (Exception ex) { Log.Error("Salvar tuning", ex); } }
        static int LevelOf(string id) => Stages.FirstOrDefault(s => s.Id == id)?.Level ?? 0;

        public static object Lite()
        {
            var s = Current;
            return new { active = s.Active, stage = s.Active ? s.Stage : "stock", level = s.Active ? LevelOf(s.Stage) : 0 };
        }

        // ---------- powercfg ----------
        static void Pc(string args) => Proc.Run(Proc.SysExe("powercfg.exe"), args, 10000);
        static void CpuAc(Guid g, string setting, int v) => Pc("/setacvalueindex " + g + " SUB_PROCESSOR " + setting + " " + v);
        static void CpuDc(Guid g, string setting, int v) => Pc("/setdcvalueindex " + g + " SUB_PROCESSOR " + setting + " " + v);

        static List<string> NotApplied(IEnumerable<string> ids, RpcContext ctx)
        {
            try
            {
                var list = JToken.FromObject(Rpc.Invoke("tweaks.list", new JObject(), ctx).GetAwaiter().GetResult(), Json.Serializer) as JArray;
                var applied = new HashSet<string>((list ?? new JArray()).Where(t => (bool?)t["applied"] == true).Select(t => (string)t["id"]));
                return ids.Where(i => !applied.Contains(i)).ToList();
            }
            catch (Exception ex) { Log.Warn("tweaks.list: " + ex.Message); return ids.ToList(); }
        }

        static List<string> ApplyTweaks(List<string> ids, RpcContext ctx)
        {
            var ok = new List<string>();
            if (ids.Count == 0) return ok;
            try
            {
                var r = JToken.FromObject(Rpc.Invoke("tweaks.apply", new JObject { ["ids"] = new JArray(ids) }, ctx).GetAwaiter().GetResult(), Json.Serializer);
                foreach (var res in (r["results"] as JArray) ?? new JArray())
                    if ((bool?)res["ok"] == true) ok.Add((string)res["id"]);
            }
            catch (Exception ex) { Log.Warn("tweaks.apply: " + ex.Message); }
            return ok;
        }

        // ---------- RPC ----------
        [Rpc("tune.status")]
        public static object Status()
        {
            var s = Current;
            var level = s.Active ? LevelOf(s.Stage) : 0;
            return new
            {
                active = s.Active,
                stage = s.Active ? s.Stage : "stock",
                level,
                since = s.Active ? s.Since : null,
                admin = Program.IsAdmin,
                stages = Stages,
                parts = Parts.Select(p => new { p.Id, p.Name, p.Real, p.Icon, p.MinStage, installed = s.Active && s.Installed.Contains(p.Id) }).ToList(),
                plan = Power.List().FirstOrDefault(p => p.Active)?.Name,
            };
        }

        [Rpc("tune.engage")]
        public static object Engage(string stage, RpcContext ctx)
        {
            if (!Program.IsAdmin) throw new RpcException("Abra o RAGE! como administrador para envenenar o PC.");
            var level = LevelOf(stage);
            if (level == 0) return Disengage(ctx);
            lock (Gate)
            {
                if (Current.Active) DisengageCore(ctx, silent: true);
                var s = Current;
                s.Prev = new Saved { Scheme = Power.ActiveScheme().ToString(), Overlay = Power.Overlay()?.ToString() };
                s.TweaksApplied = new List<string>();
                s.Installed = new List<string>();
                var prevScheme = Power.ActiveScheme();

                void Step(string part, Func<bool> act, string fail = null)
                {
                    var def = Parts.First(p => p.Id == part);
                    ctx.Progress(new { part, name = def.Name, status = "install" });
                    bool ok;
                    try { ok = act(); } catch (Exception ex) { Log.Warn("Tuning " + part + ": " + ex.Message); ok = false; }
                    if (ok) s.Installed.Add(part);
                    ctx.Progress(new { part, name = def.Name, status = ok ? "ok" : "fail", message = ok ? null : (fail ?? "Não suportado neste PC") });
                }

                // Stage 1+: turbo agressivo e modo de energia
                if (level == 1)
                {
                    Step("turbo", () =>
                    {
                        s.Prev.BoostAc = Power.ReadSetting(prevScheme, "SUB_PROCESSOR", "PERFBOOSTMODE");
                        CpuAc(prevScheme, "PERFBOOSTMODE", 2);
                        Pc("/setactive " + prevScheme);
                        return true;
                    });
                    Step("mode", () => Power.SetOverlay(Power.OverlayBetterPerformance) || Power.SetOverlay(Power.OverlayBestPerformance), "Este PC não tem o controle de modo de energia");
                }
                else
                {
                    // Stage 2+: plano RAGE! Turbo (inclui turbo agressivo, mínimo 100% e sem estacionar núcleos)
                    Power.SetOverlay(Power.OverlayBestPerformance);
                    Guid plan = Guid.Empty;
                    Step("map", () =>
                    {
                        plan = Power.EnsureRagePlan();
                        s.RagePlan = plan.ToString();
                        CpuAc(plan, "PROCTHROTTLEMIN", 100); CpuAc(plan, "PROCTHROTTLEMAX", 100);
                        CpuAc(plan, "PERFBOOSTMODE", 2); CpuAc(plan, "CPMINCORES", 100);
                        if (level >= 3) { CpuDc(plan, "PROCTHROTTLEMIN", 100); CpuDc(plan, "PERFBOOSTMODE", 2); CpuDc(plan, "CPMINCORES", 100); }
                        else { CpuDc(plan, "PROCTHROTTLEMIN", 5); CpuDc(plan, "PERFBOOSTMODE", 1); }
                        return Power.SetScheme(plan);
                    }, "Não foi possível criar o plano RAGE! Turbo");
                    var mapped = s.Installed.Contains("map");
                    foreach (var p in new[] { "turbo", "mode", "inject", "exhaust" })
                        Step(p, () => mapped, "Depende do Remap RAGE! Turbo");
                    if (level >= 3) Step("battery", () => mapped, "Depende do Remap RAGE! Turbo");
                }

                // Nitro: modo de jogo e sem Game DVR (só desfazemos o que nós mesmos ligamos)
                Step("nitro", () =>
                {
                    var need = NotApplied(new[] { "tw.gamemode", "tw.gamedvr" }, ctx);
                    var ok = ApplyTweaks(need, ctx);
                    s.TweaksApplied.AddRange(ok);
                    return ok.Count == need.Count;
                });

                if (level >= 3)
                {
                    Step("ecu", () =>
                    {
                        var need = NotApplied(new[] { "tw.mmcss", "tw.priority" }, ctx);
                        var ok = ApplyTweaks(need, ctx);
                        s.TweaksApplied.AddRange(ok);
                        return ok.Count == need.Count;
                    });
                    Step("limiter", () =>
                    {
                        var need = NotApplied(new[] { "tw.powerthrottling" }, ctx);
                        var ok = ApplyTweaks(need, ctx);
                        s.TweaksApplied.AddRange(ok);
                        return ok.Count == need.Count;
                    });
                    Step("air", () => { RamCleaner.Clean(false); return true; });
                }

                s.Active = true;
                s.Stage = stage;
                s.Since = DateTime.Now;
                Save();
                var def0 = Stages.First(x => x.Id == stage);
                Activity.Add("boost", "local_fire_department", "PC envenenado: " + def0.Name + (def0.Level < 3 ? " · " + def0.Tag : ""), s.Installed.Count + " peças instaladas");
                Log.Info("Tuning ligado: " + stage + " (" + string.Join(", ", s.Installed) + ")");
                var st = Status();
                Bridge.Emit("tune", Lite());
                return st;
            }
        }

        [Rpc("tune.disengage")]
        public static object Disengage(RpcContext ctx)
        {
            lock (Gate)
            {
                DisengageCore(ctx, silent: false);
                Bridge.Emit("tune", Lite());
                return Status();
            }
        }

        static void DisengageCore(RpcContext ctx, bool silent)
        {
            var s = Current;
            if (!s.Active) return;
            if (!Program.IsAdmin) throw new RpcException("Abra o RAGE! como administrador para voltar ao original.");
            ctx.Progress(new { part = "restore", name = "Voltando ao original", status = "install" });
            try
            {
                if (s.TweaksApplied.Count > 0)
                    Rpc.Invoke("tweaks.revert", new JObject { ["ids"] = new JArray(s.TweaksApplied) }, ctx).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Log.Warn("Reverter ajustes do tuning: " + ex.Message); }
            try
            {
                if (LevelOf(s.Stage) >= 3 && Guid.TryParse(s.RagePlan, out var plan))
                { CpuDc(plan, "PROCTHROTTLEMIN", 5); CpuDc(plan, "PERFBOOSTMODE", 1); CpuDc(plan, "CPMINCORES", 0); }
                if (s.Prev != null && Guid.TryParse(s.Prev.Scheme, out var prev))
                {
                    if (LevelOf(s.Stage) == 1 && s.Prev.BoostAc.HasValue) CpuAc(prev, "PERFBOOSTMODE", s.Prev.BoostAc.Value);
                    Power.SetScheme(prev);
                    Pc("/setactive " + prev);
                }
                if (s.Prev?.Overlay != null && Guid.TryParse(s.Prev.Overlay, out var ov)) Power.SetOverlay(ov);
            }
            catch (Exception ex) { Log.Warn("Restaurar energia do tuning: " + ex.Message); }
            var was = Stages.FirstOrDefault(x => x.Id == s.Stage);
            s.Active = false; s.Stage = "stock"; s.Since = null; s.Installed = new List<string>(); s.TweaksApplied = new List<string>();
            Save();
            ctx.Progress(new { part = "restore", name = "Voltando ao original", status = "ok" });
            if (!silent) Activity.Add("boost", "settings_backup_restore", "PC de volta ao original", was != null ? "Saiu de " + was.Name : null);
        }
    }
}
