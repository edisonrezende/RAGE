using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rage.Core;

namespace Rage.Boost
{
    public static class RamCleaner
    {
        public sealed class Result
        {
            public double Before { get; set; }
            public double After { get; set; }
            public double Freed { get; set; }
            public string FreedText { get; set; }
            public int Trimmed { get; set; }
            public bool StandbyPurged { get; set; }
            public double PctBefore { get; set; }
            public double PctAfter { get; set; }
        }

        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "dwm", "audiodg", "fontdrvhost", "MsMpEng", "NisSrv", "SecurityHealthService", "vmmem", "vmmemWSL",
        };

        static readonly object Gate = new object();

        /// <summary>
        /// Libera RAM: reduz o conjunto de trabalho dos processos em segundo plano e esvazia a lista de espera (standby).
        /// O jogo/app em primeiro plano é preservado, a menos que includeForeground seja true.
        /// </summary>
        public static Result Clean(bool includeForeground, int? keepPid = null)
        {
            lock (Gate)
            {
                var m0 = Native.Memory();
                var res = new Result { Before = m0.ullTotalPhys - m0.ullAvailPhys, PctBefore = m0.dwMemoryLoad };
                var fg = (int)Native.ForegroundPid();
                var self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == 0 || p.Id == 4 || p.Id == self || Protected.Contains(p.ProcessName)) continue;
                        if (!includeForeground && (p.Id == fg || p.Id == keepPid)) continue;
                        if (Native.EmptyWorkingSet(p.Handle)) res.Trimmed++;
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                if (Program.IsAdmin)
                {
                    try
                    {
                        Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
                        Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
                        int cmd = Native.MemoryFlushModifiedList;
                        Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref cmd, 4);
                        cmd = Native.MemoryPurgeStandbyList;
                        res.StandbyPurged = Native.NtSetSystemInformation(Native.SystemMemoryListInformation, ref cmd, 4) == 0;
                    }
                    catch (Exception ex) { Log.Warn("Purge standby: " + ex.Message); }
                }
                var m1 = Native.Memory();
                res.After = m1.ullTotalPhys - m1.ullAvailPhys;
                res.PctAfter = m1.dwMemoryLoad;
                res.Freed = Math.Max(0, res.Before - res.After);
                res.FreedText = Fmt.Bytes(res.Freed);
                return res;
            }
        }

        [Rpc("ram.clean")]
        public static object CleanRpc(bool includeForeground = false)
        {
            var r = Clean(includeForeground);
            Activity.Add("clean", "memory", "Memória RAM liberada", r.Trimmed + " processos otimizados", r.FreedText);
            return r;
        }

        [Rpc("ram.top")]
        public static object Top(int? limit)
        {
            return Process.GetProcesses().Select(p =>
            {
                try { return new { pid = p.Id, name = p.ProcessName, ws = (double)p.WorkingSet64, priv = (double)p.PrivateMemorySize64 }; }
                catch { return null; }
                finally { p.Dispose(); }
            }).Where(x => x != null && x.pid != 0).OrderByDescending(x => x.ws).Take(limit ?? 12).ToList();
        }
    }
}
