using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rage.Core;

namespace Rage.Boost
{
    /// <summary>Planos de energia (powercfg) e modo de energia do Windows 10/11 (overlay).</summary>
    public static class Power
    {
        public static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid HighPerformance = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        public static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        public const string RagePlanName = "RAGE! Turbo";

        // Modo de energia (controle deslizante das Configurações)
        public static readonly Guid OverlayBestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");
        public static readonly Guid OverlayBetterPerformance = new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
        public static readonly Guid OverlayBestEfficiency = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        public static readonly Guid OverlayBalanced = Guid.Empty;

        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid guid);
        [DllImport("powrprof.dll")] static extern uint PowerGetActualOverlayScheme(out Guid guid);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(ref Guid guid);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        public sealed class Plan { public string Guid { get; set; } public string Name { get; set; } public bool Active { get; set; } }

        public static Guid ActiveScheme()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return Guid.Empty;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); } finally { LocalFree(p); }
        }

        public static bool SetScheme(Guid g) => PowerSetActiveScheme(IntPtr.Zero, ref g) == 0;

        public static Guid? Overlay()
        {
            try { if (PowerGetEffectiveOverlayScheme(out var g) == 0) return g; } catch { }
            return null;
        }

        public static bool SetOverlay(Guid g)
        {
            try { return PowerSetActiveOverlayScheme(ref g) == 0; } catch { return false; }
        }

        public static List<Plan> List()
        {
            var r = Proc.Run(Proc.SysExe("powercfg.exe"), "/list", 15000);
            var active = ActiveScheme();
            var list = new List<Plan>();
            foreach (Match m in Regex.Matches(r.Output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+?)\)"))
            {
                var g = new Guid(m.Groups[1].Value);
                list.Add(new Plan { Guid = g.ToString(), Name = m.Groups[2].Value.Trim(), Active = g == active });
            }
            return list;
        }

        /// <summary>Cria (uma vez) o plano "RAGE! Turbo" baseado no Desempenho Máximo, com estado mínimo do processador em 100%.</summary>
        public static Guid EnsureRagePlan()
        {
            var existing = List().FirstOrDefault(p => p.Name == RagePlanName);
            if (existing != null) return new Guid(existing.Guid);
            foreach (var src in new[] { Ultimate, HighPerformance, Balanced })
            {
                var r = Proc.Run(Proc.SysExe("powercfg.exe"), "-duplicatescheme " + src, 20000);
                var m = Regex.Match(r.Output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                if (!r.Ok || !m.Success) continue;
                var g = new Guid(m.Groups[1].Value);
                Proc.Run(Proc.SysExe("powercfg.exe"), "-changename " + g + " \"" + RagePlanName + "\" \"Plano de desempenho maximo criado pelo RAGE!\"", 10000);
                // Processador na tomada: mínimo 100%, boost agressivo, sem estacionar núcleos. Na bateria fica moderado
                // (o estágio RAGE! do tuning libera a bateria explicitamente).
                Cfg(g, "SUB_PROCESSOR", "PROCTHROTTLEMIN", 100, 5);
                Cfg(g, "SUB_PROCESSOR", "PROCTHROTTLEMAX", 100, 100);
                Cfg(g, "SUB_PROCESSOR", "PERFBOOSTMODE", 2, 1);
                Cfg(g, "SUB_PROCESSOR", "CPMINCORES", 100, 100);
                Cfg(g, "SUB_PCIEXPRESS", "ASPM", 0, 0);
                Cfg(g, "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, 0); // suspensão seletiva USB
                Log.Info("Plano RAGE! Turbo criado a partir de " + src);
                return g;
            }
            return HighPerformance;
        }

        static void Cfg(Guid scheme, string sub, string setting, int ac, int dc)
        {
            Proc.Run(Proc.SysExe("powercfg.exe"), "/setacvalueindex " + scheme + " " + sub + " " + setting + " " + ac, 8000);
            Proc.Run(Proc.SysExe("powercfg.exe"), "/setdcvalueindex " + scheme + " " + sub + " " + setting + " " + dc, 8000);
        }

        // ---------- Ajustes de CPU do plano ativo ----------
        public static int? ReadSetting(Guid scheme, string sub, string setting, bool ac = true)
        {
            var r = Proc.Run(Proc.SysExe("powercfg.exe"), "/query " + scheme + " " + sub + " " + setting, 8000);
            var m = Regex.Matches(r.Output, @":\s*0x([0-9a-fA-F]{8})");
            // As duas últimas linhas são AC e DC
            if (m.Count < 2) return null;
            var val = m[m.Count - (ac ? 2 : 1)].Groups[1].Value;
            return Convert.ToInt32(val, 16);
        }

        [Rpc("power.plans")]
        public static object Plans()
        {
            var active = ActiveScheme();
            var ov = Overlay();
            return new
            {
                plans = List(),
                active = active.ToString(),
                overlay = ov?.ToString(),
                overlayName = ov == null ? null : ov == OverlayBestPerformance ? "Melhor desempenho" : ov == OverlayBestEfficiency ? "Melhor eficiência de energia" : ov == OverlayBetterPerformance ? "Desempenho equilibrado" : "Equilibrado",
                cpu = new
                {
                    minAc = ReadSetting(active, "SUB_PROCESSOR", "PROCTHROTTLEMIN"),
                    maxAc = ReadSetting(active, "SUB_PROCESSOR", "PROCTHROTTLEMAX"),
                    boostAc = ReadSetting(active, "SUB_PROCESSOR", "PERFBOOSTMODE"),
                    minDc = ReadSetting(active, "SUB_PROCESSOR", "PROCTHROTTLEMIN", false),
                    maxDc = ReadSetting(active, "SUB_PROCESSOR", "PROCTHROTTLEMAX", false),
                    boostDc = ReadSetting(active, "SUB_PROCESSOR", "PERFBOOSTMODE", false),
                },
            };
        }

        [Rpc("power.setPlan")]
        public static object SetPlan(string guid)
        {
            Guid g;
            if (guid == "rage") g = EnsureRagePlan(); else g = new Guid(guid);
            if (!SetScheme(g)) throw new RpcException("Não foi possível ativar o plano de energia.");
            Activity.Add("boost", "bolt", "Plano de energia alterado", List().FirstOrDefault(p => new Guid(p.Guid) == g)?.Name);
            return Plans();
        }

        [Rpc("power.setOverlay")]
        public static object SetOverlayRpc(string mode)
        {
            var g = mode == "performance" ? OverlayBestPerformance : mode == "efficiency" ? OverlayBestEfficiency : mode == "better" ? OverlayBetterPerformance : OverlayBalanced;
            if (!SetOverlay(g)) throw new RpcException("O modo de energia não pôde ser alterado neste computador.");
            return Plans();
        }

        /// <summary>Ajusta o processador no plano ativo: estado mínimo/máximo e modo de boost (0 desativado, 1 ativado, 2 agressivo, 3 eficiente, 4 eficiente agressivo).</summary>
        [Rpc("power.setCpu")]
        public static object SetCpu(int? minAc, int? maxAc, int? boostAc, int? minDc, int? maxDc, int? boostDc)
        {
            if (!Program.IsAdmin) throw new RpcException("Requer administrador.");
            var g = ActiveScheme();
            void Set(string verb, string setting, int? v) { if (v.HasValue) Proc.Run(Proc.SysExe("powercfg.exe"), "/" + verb + " " + g + " SUB_PROCESSOR " + setting + " " + v.Value, 8000); }
            Set("setacvalueindex", "PROCTHROTTLEMIN", minAc); Set("setacvalueindex", "PROCTHROTTLEMAX", maxAc); Set("setacvalueindex", "PERFBOOSTMODE", boostAc);
            Set("setdcvalueindex", "PROCTHROTTLEMIN", minDc); Set("setdcvalueindex", "PROCTHROTTLEMAX", maxDc); Set("setdcvalueindex", "PERFBOOSTMODE", boostDc);
            Proc.Run(Proc.SysExe("powercfg.exe"), "/setactive " + g, 8000);
            Activity.Add("boost", "speed", "Ajuste do processador aplicado", "Plano de energia ativo");
            return Plans();
        }
    }
}
