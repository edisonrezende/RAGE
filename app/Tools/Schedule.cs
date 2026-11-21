using System;
using System.IO;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Agendar desligar/reiniciar (shutdown.exe /t) e hibernar/suspender (tarefa única do Agendador). Funciona mesmo com o RAGE! fechado.</summary>
    public static class Schedule
    {
        sealed class State { public string Action { get; set; } public DateTime At { get; set; } }

        const string TaskName = "RAGE Agendamento";
        static string StateFile => Paths.File("schedule.json");
        static readonly object Gate = new object();

        static string Label(string a) => a == "shutdown" ? "Desligar" : a == "restart" ? "Reiniciar" : a == "hibernate" ? "Hibernar" : "Suspender";

        public static object Get()
        {
            lock (Gate)
            {
                var st = Json.Load<State>(StateFile, null);
                if (st == null) return null;
                if (st.At < DateTime.Now.AddMinutes(-2)) { Clear(); return null; }
                return new { action = st.Action, at = st.At, label = Label(st.Action) };
            }
        }

        static void Clear() { try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { } }

        static void DeleteTask()
        {
            dynamic svc = null;
            try
            {
                svc = TaskSched.Connect();
                dynamic root = svc.GetFolder("\\");
                try { root.DeleteTask(TaskName, 0); } catch { }
                TaskSched.Release(root);
            }
            catch { }
            finally { TaskSched.Release(svc); }
        }

        static bool HibernateEnabled() => (Reg.GetInt(@"HKLM\SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled") ?? 0) == 1;

        public static object Set(string action, int minutes)
        {
            if (action != "shutdown" && action != "restart" && action != "hibernate" && action != "sleep") throw new RpcException("Ação inválida.");
            if (minutes < 1 || minutes > 10080) throw new RpcException("Escolha um tempo entre 1 minuto e 7 dias.");
            if (action == "hibernate" && !HibernateEnabled()) throw new RpcException("A hibernação está desativada neste computador.");
            lock (Gate)
            {
                CancelInternal();
                var at = DateTime.Now.AddMinutes(minutes);
                if (action == "shutdown" || action == "restart")
                {
                    var msg = (action == "shutdown" ? "Desligamento" : "Reinício") + " agendado pelo RAGE! para " + at.ToString("HH:mm") + ".";
                    var r = Proc.Run(Proc.SysExe("shutdown.exe"), (action == "shutdown" ? "/s" : "/r") + " /t " + (minutes * 60) + " /c \"" + msg + "\"", 15000);
                    if (r.ExitCode != 0) throw new RpcException("Não foi possível agendar: " + (ToolsUtil.LastMeaningful(r.Error) ?? ToolsUtil.LastMeaningful(r.Output) ?? "código " + r.ExitCode));
                }
                else
                {
                    dynamic svc = TaskSched.Connect();
                    try
                    {
                        dynamic def = svc.NewTask(0);
                        def.RegistrationInfo.Description = "Criada pelo RAGE! para " + (action == "hibernate" ? "hibernar" : "suspender") + " o computador uma vez.";
                        def.RegistrationInfo.Author = "RAGE!";
                        def.Settings.Enabled = true;
                        def.Settings.StartWhenAvailable = false;
                        def.Settings.DisallowStartIfOnBatteries = false;
                        def.Settings.StopIfGoingOnBatteries = false;
                        def.Settings.ExecutionTimeLimit = "PT10M";
                        def.Settings.DeleteExpiredTaskAfter = "PT1H";
                        def.Settings.Hidden = false;
                        dynamic trig = def.Triggers.Create(1); // TASK_TRIGGER_TIME
                        trig.StartBoundary = at.ToString("yyyy-MM-dd'T'HH:mm:ss");
                        trig.EndBoundary = at.AddMinutes(30).ToString("yyyy-MM-dd'T'HH:mm:ss");
                        trig.Enabled = true;
                        dynamic act = def.Actions.Create(0); // TASK_ACTION_EXEC
                        if (action == "hibernate") { act.Path = Proc.SysExe("shutdown.exe"); act.Arguments = "/h"; }
                        else
                        {
                            act.Path = Proc.SysExe(@"WindowsPowerShell\v1.0\powershell.exe");
                            act.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.Application]::SetSuspendState([System.Windows.Forms.PowerState]::Suspend, $false, $false)\"";
                        }
                        dynamic root = svc.GetFolder("\\");
                        root.RegisterTaskDefinition(TaskName, def, 6, null, null, 3, null); // CREATE_OR_UPDATE, LOGON_INTERACTIVE_TOKEN
                        TaskSched.Release(root); TaskSched.Release(act); TaskSched.Release(trig); TaskSched.Release(def);
                    }
                    catch (RpcException) { throw; }
                    catch (Exception ex) { throw new RpcException("Não foi possível agendar: " + ex.Message); }
                    finally { TaskSched.Release(svc); }
                }
                Json.Save(StateFile, new State { Action = action, At = at });
                Activity.Add("info", "schedule", Label(action) + " agendado", "Às " + at.ToString("HH:mm") + (at.Date != DateTime.Today ? " de " + at.ToString("dd/MM") : ""));
                return Get();
            }
        }

        static void CancelInternal()
        {
            var st = Json.Load<State>(StateFile, null);
            if (st != null && (st.Action == "shutdown" || st.Action == "restart")) Proc.Run(Proc.SysExe("shutdown.exe"), "/a", 15000);
            DeleteTask();
            Clear();
        }

        public static object Cancel()
        {
            lock (Gate)
            {
                var had = Json.Load<State>(StateFile, null);
                CancelInternal();
                if (had != null) Activity.Add("info", "schedule", "Agendamento cancelado", Label(had.Action) + " às " + had.At.ToString("HH:mm"));
                return new { ok = true };
            }
        }

        [Rpc("schedule.set")]
        public static object SetRpc(string action, int minutes) => Set(action, minutes);

        [Rpc("schedule.cancel")]
        public static object CancelRpc() => Cancel();

        [Rpc("schedule.get")]
        public static object GetRpc() => Get();
    }
}
