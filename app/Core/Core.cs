using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Rage.Core
{
    /// <summary>Pastas do app. Modo portátil: tudo fica em ".\data" ao lado do executável.</summary>
    public static class Paths
    {
        public static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;
        public static readonly string AppDir = Path.GetDirectoryName(ExePath);
        public static string Data { get; private set; }
        public static bool Portable { get; private set; }

        public static string Backups => Ensure(Path.Combine(Data, "backups"));
        public static string Logs => Ensure(Path.Combine(Data, "logs"));
        public static string WebView => Ensure(Path.Combine(Data, "webview"));
        public static string File(string name) => Path.Combine(Data, name);

        public static void Init()
        {
            var local = Path.Combine(AppDir, "data");
            if (CanWrite(local)) { Data = local; Portable = true; }
            else { Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAGE"); Portable = false; }
            Directory.CreateDirectory(Data);
        }

        public static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

        static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".probe");
                System.IO.File.WriteAllText(probe, "ok");
                System.IO.File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false } },
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,
        };
        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Settings);
        public static string Pretty(object o) => JsonConvert.SerializeObject(o, Formatting.Indented, Settings);
        public static T Deserialize<T>(string s) => JsonConvert.DeserializeObject<T>(s, Settings);
        public static JToken From(object o) => o == null ? JValue.CreateNull() : JToken.FromObject(o, Serializer);

        public static T Load<T>(string path, T fallback)
        {
            try { if (System.IO.File.Exists(path)) return Deserialize<T>(System.IO.File.ReadAllText(path, Encoding.UTF8)) ?? fallback; }
            catch (Exception ex) { Log.Warn("Falha ao ler " + path + ": " + ex.Message); }
            return fallback;
        }

        public static void Save(string path, object o)
        {
            var tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, Pretty(o), new UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Replace(tmp, path, null);
            else System.IO.File.Move(tmp, path);
        }
    }

    /// <summary>Log em arquivo (data\logs\rage.log).</summary>
    public static class Log
    {
        static readonly object Gate = new object();
        static string FilePath => Path.Combine(Paths.Logs, "rage.log");

        public static void Info(string m) => Write("INFO", m);
        public static void Warn(string m) => Write("WARN", m);
        public static void Error(string m, Exception ex = null) => Write("ERRO", ex == null ? m : m + " :: " + ex);

        static void Write(string level, string m)
        {
            try
            {
                lock (Gate)
                {
                    var f = FilePath;
                    var fi = new FileInfo(f);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        var old = f + ".1";
                        if (System.IO.File.Exists(old)) System.IO.File.Delete(old);
                        System.IO.File.Move(f, old);
                    }
                    System.IO.File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + m + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
            Debug.WriteLine(level + " " + m);
        }
    }

    /// <summary>Feed de "Eventos recentes" exibido na Visão geral.</summary>
    public static class Activity
    {
        public sealed class Item
        {
            public string Id { get; set; }
            public DateTime At { get; set; }
            public string Icon { get; set; }
            public string Title { get; set; }
            public string Detail { get; set; }
            public string Value { get; set; }
            public string Kind { get; set; } // clean | boost | bench | fix | info | warn
        }

        static readonly object Gate = new object();
        static List<Item> items = new List<Item>();
        static string FilePath => Paths.File("activity.json");

        public static void Load() { items = Json.Load(FilePath, new List<Item>()); }

        public static Item Add(string kind, string icon, string title, string detail = null, string value = null)
        {
            var it = new Item { Id = Guid.NewGuid().ToString("N"), At = DateTime.Now, Icon = icon, Title = title, Detail = detail, Value = value, Kind = kind };
            lock (Gate)
            {
                items.Insert(0, it);
                if (items.Count > 300) items.RemoveRange(300, items.Count - 300);
                try { Json.Save(FilePath, items); } catch { }
            }
            Bridge.Emit("activity", it);
            return it;
        }

        public static List<Item> Recent(int n) { lock (Gate) return items.Take(n).ToList(); }
        public static List<Item> All() { lock (Gate) return items.ToList(); }

        [Rpc("activity.list")]
        public static object List(int? limit) => Recent(limit ?? 50);

        [Rpc("activity.clear")]
        public static object Clear() { lock (Gate) { items.Clear(); try { Json.Save(FilePath, items); } catch { } } return true; }
    }

    public static class Fmt
    {
        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (Math.Abs(b) >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return (i == 0 ? b.ToString("0") : b.ToString(b >= 100 ? "0" : "0.0")) + " " + u[i];
        }
    }
}
