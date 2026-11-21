using System;
using System.Threading.Tasks;
using Rage.Core;

namespace Rage
{
    /// <summary>Inicia/para os serviços em segundo plano do app.</summary>
    public static class Services
    {
        public static void StartMonitor() => Monitor.MonitorService.Start();

        public static void StartAll()
        {
            Monitor.MonitorService.Start();
            Boost.GameWatcher.Start();
            Task.Run(() =>
            {
                try { Monitor.Fps.Start(); } catch (Exception ex) { Log.Error("FPS", ex); }
                try { App.Autostart.EnsureCurrent(); } catch { }
                try { App.Shortcut.EnsureCurrent(); } catch { }
            });
            try { Monitor.Overlay.Init(); } catch (Exception ex) { Log.Error("Overlay", ex); }
            Settings.Changed += () => { try { Monitor.Overlay.ApplySettings(); } catch { } };
        }

        public static void StopAll()
        {
            try { Boost.GameWatcher.Stop(); } catch { }
            try { Monitor.Fps.Stop(); } catch { }
            try { Monitor.MonitorService.Stop(); } catch { }
        }
    }
}
