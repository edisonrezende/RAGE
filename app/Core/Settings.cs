using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Rage.Core
{
    public sealed class AppSettings
    {
        public string Theme { get; set; } = "dark";
        public bool CloseToTray { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = true;
        public bool TempFahrenheit { get; set; } = false;
        public bool RestorePointBeforeFix { get; set; } = true;
        public bool RegistryBackup { get; set; } = true;

        // Modo Turbo / jogos
        public string BoostMode { get; set; } = "game";        // work | game | economy
        public bool AutoBoost { get; set; } = true;             // liga o Turbo ao abrir um jogo da biblioteca
        public bool AutoDetectGames { get; set; } = true;       // trata executáveis em pastas de jogos como jogos
        public bool BoostCleanRam { get; set; } = true;
        public bool BoostPowerPlan { get; set; } = true;
        public bool BoostGamePriority { get; set; } = true;
        public bool BoostFlushDns { get; set; } = false;
        public bool BoostStopServices { get; set; } = true;
        public bool BoostCloseApps { get; set; } = true;
        public bool BoostNotify { get; set; } = true;
        public List<string> BoostServices { get; set; } = null;  // null = padrão recomendado
        public List<string> BoostApps { get; set; } = new List<string>();

        // RAM automática
        public bool AutoRamClean { get; set; } = false;
        public int AutoRamThreshold { get; set; } = 85;

        // Alertas
        public bool TempAlerts { get; set; } = true;
        public int CpuTempAlert { get; set; } = 95;
        public int GpuTempAlert { get; set; } = 90;

        // Overlay de jogo
        public bool OverlayEnabled { get; set; } = false;
        public string OverlayPosition { get; set; } = "top-left";
        public bool OverlayAutoInGame { get; set; } = true;
        public string OverlayHotkey { get; set; } = "Ctrl+Shift+F10";

        // Mini monitor (pílula flutuante com RAM e CPU)
        public bool MiniMonitor { get; set; } = true;
        public bool MiniMonitorHideFullscreen { get; set; } = true;
        public string MiniMonitorPos { get; set; }              // "x,y" em pixels da tela; vazio = topo, ao centro

        public bool FpsCounter { get; set; } = true;
        public bool SensorsDriverPrompted { get; set; } = false;
        public bool FirstRunDone { get; set; } = false;
        public string WindowBounds { get; set; }
    }

    public static class Settings
    {
        static string FilePath => Paths.File("settings.json");
        public static AppSettings Current { get; private set; } = new AppSettings();
        public static event Action Changed;

        public static void Load() { Current = Json.Load(FilePath, new AppSettings()); }

        public static void Save()
        {
            try { Json.Save(FilePath, Current); } catch (Exception ex) { Log.Error("Salvar configurações", ex); }
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error("Settings.Changed", ex); }
        }

        [Rpc("settings.get")]
        public static object Get() => Current;

        [Rpc("settings.set")]
        public static object Set(JObject values)
        {
            if (values == null) return Current;
            // A interface manda { values: { chave: valor } }; o RPC entrega o objeto de parâmetros inteiro.
            if (values["values"] is JObject inner) values = inner;
            var merged = JObject.FromObject(Current, Json.Serializer);
            merged.Merge(values, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
            var next = merged.ToObject<AppSettings>(Json.Serializer);
            var startChanged = next.StartWithWindows != Current.StartWithWindows;
            Current = next;
            Save();
            if (startChanged) Rage.App.Autostart.Apply(Current.StartWithWindows);
            return Current;
        }
    }
}
