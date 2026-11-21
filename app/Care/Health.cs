using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Checklist "Saúde do PC" da Visão geral. Rápido (registro, Win32 e COM local; sem PowerShell).</summary>
    public static class HealthRpc
    {
        public sealed class HealthAction
        {
            public string Label { get; set; }
            public string Route { get; set; }
            public string Rpc { get; set; }
            public string Uri { get; set; }
        }

        public sealed class HealthItem
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Detail { get; set; }
            public string Status { get; set; }   // ok | warn | bad
            public HealthAction Action { get; set; }
        }

        static HealthAction Go(string label, string route) => new HealthAction { Label = label, Route = route };

        // Verificações mais lentas (COM/WMI) ficam em cache por um tempo; o painel pode chamar o resumo com frequência.
        static readonly object Gate = new object();
        static Task<HealthItem> defTask, restoreTask;
        static DateTime defAt, restoreAt;

        static Task<HealthItem> Cached(ref Task<HealthItem> task, ref DateTime at, TimeSpan ttl, Func<HealthItem> f)
        {
            lock (Gate)
            {
                var running = task != null && !task.IsCompleted;
                if (task == null || (!running && DateTime.Now - at > ttl)) { task = Task.Run(f); at = DateTime.Now; }
                return task;
            }
        }

        [Rpc("health.summary")]
        public static object Summary()
        {
            var sw = Stopwatch.StartNew();
            var defender = Cached(ref defTask, ref defAt, TimeSpan.FromSeconds(20), DefenderItem);
            var restore = Program.IsAdmin ? Cached(ref restoreTask, ref restoreAt, TimeSpan.FromMinutes(5), RestoreItem) : null;

            var items = new List<HealthItem>();
            void Add(Func<HealthItem> f) { try { var i = f(); if (i != null) items.Add(i); } catch (Exception ex) { Log.Warn("Saúde: " + ex.Message); } }

            Add(SensorsItem);
            var defIndex = items.Count;
            Add(GameModeItem);
            Add(GameDvrItem);
            Add(DiskItem);
            Add(RamItem);
            Add(RebootItem);

            HealthItem Wait(Task<HealthItem> t, int deadlineMs)
            {
                if (t == null) return null;
                var left = (int)Math.Max(50, deadlineMs - sw.ElapsedMilliseconds);
                try { return t.Wait(left) ? t.Result : null; } catch { return null; }
            }
            var d = Wait(defender, 1200);
            if (d != null) items.Insert(defIndex, d);
            var r = Wait(restore, 1350);
            if (r != null) items.Add(r);
            return items;
        }

        static HealthItem SensorsItem()
        {
            var s = Rage.Monitor.Sensors.DriverStatus();
            var it = new HealthItem { Id = "sensors", Title = "Sensores de temperatura" };
            if (!s.Installed)
            {
                it.Status = "warn"; it.Detail = "Instale o driver PawnIO para ver a temperatura e o consumo reais da CPU";
                it.Action = Go("Instalar", "/configuracoes");
            }
            else if (!s.Admin)
            {
                it.Status = "warn"; it.Detail = "Abra o RAGE! como administrador para ler os sensores";
                it.Action = Go("Configurar", "/configuracoes");
            }
            else { it.Status = "ok"; it.Detail = "Temperaturas e consumo disponíveis" + (string.IsNullOrEmpty(s.Version) ? "" : " (PawnIO " + s.Version + ")"); }
            return it;
        }

        static HealthItem DefenderItem()
        {
            var it = new HealthItem { Id = "defender", Title = "Proteção antivírus" };
            List<Wsc.Product> avs = null;
            try { avs = Wsc.List(Wsc.Antivirus); } catch { }
            if (avs != null && avs.Count > 0)
            {
                var on = avs.FirstOrDefault(p => p.State == 0 && !p.Microsoft) ?? avs.FirstOrDefault(p => p.State == 0);
                if (on != null) { it.Status = "ok"; it.Detail = on.Microsoft ? "Microsoft Defender: proteção em tempo real ativa" : "Protegido por " + on.Name; return it; }
                var third = avs.FirstOrDefault(p => !p.Microsoft);
                if (third != null)
                {
                    it.Status = third.State == 2 ? "warn" : "bad";
                    it.Detail = third.Name + " está " + Wsc.StateText(third.State) + " e o Microsoft Defender não está protegendo o PC";
                }
                else { it.Status = "bad"; it.Detail = "A proteção em tempo real do Microsoft Defender está desligada"; }
                it.Action = Go("Ver", "/antivirus");
                return it;
            }
            // Sem Central de Segurança: lê o registro do Defender
            var off = Reg.GetInt(@"HKLM\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring") == 1
                   || Reg.GetInt(@"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware") == 1;
            it.Status = off ? "bad" : "ok";
            it.Detail = off ? "A proteção em tempo real do Microsoft Defender está desligada" : "Microsoft Defender ativo";
            if (off) it.Action = Go("Ver", "/antivirus");
            return it;
        }

        static HealthItem GameModeItem()
        {
            var on = TweakEngine.Num(TweakEngine.Read(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled")) != 0;
            return new HealthItem
            {
                Id = "gamemode", Title = "Modo de Jogo do Windows", Status = on ? "ok" : "warn",
                Detail = on ? "Ligado" : "Desligado — o Windows não prioriza o jogo aberto",
                Action = on ? null : Go("Ligar", "/ajustes"),
            };
        }

        static HealthItem GameDvrItem()
        {
            var t = TweakEngine.Find("tw.gamedvr");
            var off = t != null && TweakEngine.IsApplied(t);
            var bg = TweakEngine.Num(TweakEngine.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled")) == 1;
            return new HealthItem
            {
                Id = "gamedvr", Title = "Gravação em segundo plano", Status = off ? "ok" : "warn",
                Detail = off ? "Game DVR desligado" : bg ? "Gravando em segundo plano — pode custar FPS" : "Captura do Game DVR ligada — pode custar FPS",
                Action = off ? null : Go("Desligar", "/ajustes"),
            };
        }

        static HealthItem DiskItem()
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var d = new DriveInfo(root);
            if (!d.IsReady || d.TotalSize <= 0) return null;
            var pct = d.AvailableFreeSpace * 100.0 / d.TotalSize;
            var status = pct > 15 ? "ok" : pct > 8 ? "warn" : "bad";
            return new HealthItem
            {
                Id = "disk", Title = "Espaço no disco " + root.TrimEnd('\\'), Status = status,
                Detail = Fmt.Bytes(d.AvailableFreeSpace) + " livres de " + Fmt.Bytes(d.TotalSize) + " (" + pct.ToString("0") + "%)",
                Action = status == "ok" ? null : Go("Limpar", "/limpeza"),
            };
        }

        static HealthItem RamItem()
        {
            var m = Native.Memory();
            if (m.ullTotalPhys == 0) return null;
            var used = m.ullTotalPhys - m.ullAvailPhys;
            var load = (int)m.dwMemoryLoad;
            var status = load < 80 ? "ok" : load < 90 ? "warn" : "bad";
            return new HealthItem
            {
                Id = "ram", Title = "Uso de memória RAM", Status = status,
                Detail = Fmt.Bytes(used) + " de " + Fmt.Bytes(m.ullTotalPhys) + " em uso (" + load + "%)",
                Action = status == "ok" ? null : new HealthAction { Label = "Liberar", Rpc = "ram.clean" },
            };
        }

        static HealthItem RebootItem()
        {
            var pending = CareUtil.RebootPending();
            return new HealthItem
            {
                Id = "reboot", Title = "Reinicialização pendente", Status = pending ? "warn" : "ok",
                Detail = pending ? "O Windows precisa reiniciar para concluir atualizações" : "Nenhuma",
                Action = pending ? new HealthAction { Label = "Ver", Uri = "ms-settings:windowsupdate" } : null,
            };
        }

        /// <summary>Ponto de restauração mais recente (WMI root\default, exige administrador).</summary>
        static HealthItem RestoreItem()
        {
            DateTime? last = null;
            foreach (var o in CareUtil.Wmi(@"root\default", "SELECT CreationTime FROM SystemRestore", 1200))
            {
                try
                {
                    if (CareUtil.Prop(o, "CreationTime") is string s)
                    {
                        var dt = ManagementDateTimeConverter.ToDateTime(s);
                        if (!last.HasValue || dt > last.Value) last = dt;
                    }
                }
                catch { }
            }
            var it = new HealthItem { Id = "restore", Title = "Ponto de restauração" };
            if (!last.HasValue) { it.Status = "warn"; it.Detail = "Nenhum ponto de restauração encontrado"; it.Action = Go("Criar", "/resgate"); return it; }
            var days = (DateTime.Now - last.Value).TotalDays;
            it.Status = days <= 30 ? "ok" : "warn";
            it.Detail = "Último ponto criado " + CareUtil.Days(last.Value);
            if (days > 30) it.Action = Go("Criar", "/resgate");
            return it;
        }
    }
}
