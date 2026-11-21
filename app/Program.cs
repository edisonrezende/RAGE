using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Rage.Core;

namespace Rage
{
    static class Program
    {
        public static bool IsAdmin { get; private set; }
        public static string[] Args { get; private set; }
        public static readonly DateTime StartedAt = DateTime.Now;
        public static readonly string Version = typeof(Program).Assembly.GetName().Version.ToString(3);
        public static uint ShowMessage;

        [STAThread]
        static int Main(string[] args)
        {
            Args = args;
            IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            Paths.Init();
            InitWebView2Loader();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Exceção não tratada", e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => Log.Error("Exceção na interface", e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Settings.Load();
            Activity.Load();
            Rpc.Init();

            if (args.Contains("--cli")) return Cli.Run(args);

            // Reabre como administrador (limpeza, serviços e sensores exigem). Se o usuário recusar, segue em modo limitado.
            if (!IsAdmin && !args.Contains("--no-elevate") && !args.Contains("--from-elevation"))
            {
                try
                {
                    var a = string.Join(" ", args.Select(Quote).Concat(new[] { "--from-elevation" }));
                    Process.Start(new ProcessStartInfo(Paths.ExePath, a) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Paths.AppDir });
                    return 0;
                }
                catch (Win32Exception) { Log.Warn("Elevação recusada, iniciando em modo limitado"); }
            }

            ShowMessage = Native.RegisterWindowMessage("RAGE_SHOW_WINDOW_7F3A");
            using (var mutex = new Mutex(true, "Local\\RAGE_SingleInstance_7F3A", out bool created))
            {
                if (!created)
                {
                    Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                Log.Info("RAGE! " + Version + " iniciado (admin=" + IsAdmin + ", portátil=" + Paths.Portable + ")");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(args.Contains("--tray")));
                Services.StopAll();
                return 0;
            }
        }

        static string Quote(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;

        /// <summary>Versão portátil: o WebView2Loader.dll fica em .\lib junto com as outras DLLs.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void InitWebView2Loader()
        {
            try
            {
                var lib = Path.Combine(Paths.AppDir, "lib");
                if (File.Exists(Path.Combine(lib, "WebView2Loader.dll")))
                    Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(lib);
            }
            catch (Exception ex) { Log.Warn("WebView2Loader: " + ex.Message); }
        }
    }

    /// <summary>Modo linha de comando para testes: RAGE.exe --cli metodo --params "{json}" --out arquivo.json</summary>
    static class Cli
    {
        public static int Run(string[] args)
        {
            string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var method = Arg("--cli");
            var pjson = Arg("--params");
            var pfile = Arg("--params-file");
            if (pfile != null) pjson = File.ReadAllText(pfile);
            var outFile = Arg("--out");
            object result;
            int code = 0;
            try
            {
                if (method == "list") result = Rpc.Names.ToArray();
                else
                {
                    if (method.StartsWith("monitor.") || method.StartsWith("fps."))
                    {
                        Services.StartMonitor();
                        if (method.StartsWith("fps.")) Monitor.Fps.Start();
                        var sw = Stopwatch.StartNew();
                        while (sw.ElapsedMilliseconds < 30000 && (Monitor.MonitorService.Last == null || sw.ElapsedMilliseconds < 2500)) Thread.Sleep(100);
                        Thread.Sleep(method.StartsWith("fps.") ? 3000 : 1200);
                    }
                    var p = string.IsNullOrEmpty(pjson) ? new JObject() : JObject.Parse(pjson);
                    var ctx = new RpcContext { Id = "cli", Token = CancellationToken.None, ProgressSink = d => Log.Info("progresso: " + Json.Serialize(d)) };
                    result = Rpc.Invoke(method, p, ctx).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex) { ex = Rpc.Unwrap(ex); result = new { error = ex.Message, type = ex.GetType().Name, stack = ex.StackTrace }; code = 1; }
            var text = Json.Pretty(new { admin = Program.IsAdmin, method, result });
            if (outFile != null) File.WriteAllText(outFile, text, new UTF8Encoding(false));
            else { Native.AttachConsole(-1); Console.Out.WriteLine(text); }
            Services.StopAll();
            return code;
        }
    }
}
