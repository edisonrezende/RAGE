using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.App
{
    public static class AppRpc
    {
        [Rpc("app.info")]
        public static object Info()
        {
            string osName = Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
            var build = Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber") ?? "";
            var ubr = Reg.GetInt(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
            var display = Reg.GetStr(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) osName = osName.Replace("Windows 10", "Windows 11");
            string wv = null;
            try { wv = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            return new
            {
                name = "RAGE!",
                version = Program.Version,
                isAdmin = Program.IsAdmin,
                portable = Paths.Portable,
                dataDir = Paths.Data,
                exe = Paths.ExePath,
                os = osName + " " + display,
                osBuild = build + (ubr.HasValue ? "." + ubr : ""),
                machine = Environment.MachineName,
                user = Environment.UserName,
                uptimeSec = (long)(Native.GetTickCount64() / 1000),
                startedAt = Program.StartedAt,
                webview = wv,
                sensors = Monitor.Sensors.DriverStatus(),
            };
        }

        [Rpc("app.elevate")]
        public static object Elevate()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Paths.ExePath, "--from-elevation") { UseShellExecute = true, Verb = "runas" });
            }
            catch (System.ComponentModel.Win32Exception) { throw new RpcException("Permissão de administrador recusada."); }
            Task.Delay(300).ContinueWith(_ => MainForm.Instance?.Quit());
            return true;
        }

        [Rpc("app.open")]
        public static object Open(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) throw new RpcException("Destino vazio");
            Proc.Open(target);
            return true;
        }

        [Rpc("app.reveal")]
        public static object Reveal(string path)
        {
            if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
            else
            {
                var dir = Path.GetDirectoryName(path ?? "");
                if (Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\"");
                else throw new RpcException("Caminho não encontrado");
            }
            return true;
        }

        [Rpc("app.regedit")]
        public static object OpenRegedit(string key)
        {
            Reg.Set(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", "Computador\\" + Reg.FullHive(key), RegistryValueKind.String);
            Process.Start(new ProcessStartInfo("regedit.exe", "-m") { UseShellExecute = true });
            return true;
        }

        [Rpc("app.pickFolder")]
        public static object PickFolder(string title)
        {
            return MainForm.Instance.OnUi(() =>
            {
                using (var d = new FolderBrowserDialog { Description = title ?? "Escolha uma pasta", ShowNewFolderButton = false })
                    return d.ShowDialog(MainForm.Instance) == DialogResult.OK ? d.SelectedPath : null;
            });
        }

        [Rpc("app.pickFiles")]
        public static object PickFiles(string title, bool multi = true, string filter = null)
        {
            return MainForm.Instance.OnUi(() =>
            {
                using (var d = new OpenFileDialog { Title = title ?? "Escolha arquivos", Multiselect = multi, Filter = filter ?? "Todos os arquivos (*.*)|*.*" })
                    return d.ShowDialog(MainForm.Instance) == DialogResult.OK ? d.FileNames : new string[0];
            });
        }

        [Rpc("app.saveText")]
        public static object SaveText(string fileName, string content, string filter = null)
        {
            return MainForm.Instance.OnUi(() =>
            {
                using (var d = new SaveFileDialog { FileName = fileName ?? "relatorio.txt", Filter = filter ?? "Texto (*.txt)|*.txt|Todos (*.*)|*.*" })
                {
                    if (d.ShowDialog(MainForm.Instance) != DialogResult.OK) return null;
                    File.WriteAllText(d.FileName, content ?? "", new UTF8Encoding(true));
                    return d.FileName;
                }
            });
        }

        [Rpc("app.setTheme")]
        public static object SetTheme(string theme)
        {
            Settings.Current.Theme = theme == "light" ? "light" : "dark";
            Settings.Save();
            MainForm.Instance?.OnUi(() => { MainForm.Instance.ApplyTitleBar(Settings.Current.Theme); return true; });
            return true;
        }

        [Rpc("app.show")]
        public static object Show() { MainForm.Instance?.ShowFromTray(); return true; }

        [Rpc("app.hide")]
        public static object Hide() { MainForm.Instance?.HideToTray(); return true; }

        [Rpc("app.quit")]
        public static object Quit() { MainForm.Instance?.Quit(); return true; }

        [Rpc("app.notify")]
        public static object Notify(string title, string text) { MainForm.Instance?.Notify(title, text); return true; }

        [Rpc("app.openDataFolder")]
        public static object OpenData() { Process.Start("explorer.exe", "\"" + Paths.Data + "\""); return true; }

        [Rpc("app.installSensorDriver")]
        public static async Task<object> InstallSensorDriver(RpcContext ctx)
        {
            if (!Program.IsAdmin) throw new RpcException("Abra o RAGE! como administrador para instalar o driver de sensores.");
            ctx.Progress(new { line = "Instalando PawnIO pelo winget..." });
            var r = await Proc.RunAsync("winget", "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                600000, Encoding.UTF8, l => ctx.Progress(new { line = l }), ctx.Token);
            Monitor.Sensors.Reset();
            var st = Monitor.Sensors.DriverStatus();
            if (!st.Installed) throw new RpcException("Não foi possível instalar o PawnIO (código " + r.ExitCode + "). Tente instalar manualmente em pawnio.eu.");
            Activity.Add("info", "memory", "Driver de sensores instalado", "PawnIO " + st.Version);
            return st;
        }
    }

    /// <summary>Atalho na Área de Trabalho. Com noUac, o atalho dispara uma tarefa agendada já elevada (sem a pergunta do UAC).</summary>
    public static class Shortcut
    {
        public const string LaunchTask = "RAGE Launch";

        static string TaskXml(string exe, string args) =>
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>Abre o RAGE! como administrador sem pedir confirmação.</Description></RegistrationInfo>\r\n" +
            "  <Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority><AllowStartOnDemand>true</AllowStartOnDemand></Settings>\r\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(exe) + "</Command><Arguments>" + System.Security.SecurityElement.Escape(args) + "</Arguments>" +
            "<WorkingDirectory>" + System.Security.SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\r\n" +
            "</Task>";

        public static bool EnsureLaunchTask()
        {
            var xml = Path.Combine(Path.GetTempPath(), "rage-launch-task.xml");
            File.WriteAllText(xml, TaskXml(Paths.ExePath, "--from-elevation"), Encoding.Unicode);
            try
            {
                var r = Proc.Run(Proc.SysExe("schtasks.exe"), "/Create /F /TN \"" + LaunchTask + "\" /XML \"" + xml + "\"", 20000);
                if (!r.Ok) Log.Warn("Tarefa de abertura: " + r.Output + r.Error);
                return r.Ok;
            }
            finally { try { File.Delete(xml); } catch { } }
        }

        public static bool LaunchTaskExists() => Proc.Run(Proc.SysExe("schtasks.exe"), "/Query /TN \"" + LaunchTask + "\"", 10000).Ok;

        [Rpc("app.desktopShortcut")]
        public static object Create(bool noUac = true)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var lnk = Path.Combine(desktop, "RAGE!.lnk");
            string target = Paths.ExePath, args = "";
            if (noUac)
            {
                if (!Program.IsAdmin) throw new RpcException("Abra o RAGE! como administrador para criar o atalho sem confirmação.");
                if (!EnsureLaunchTask()) throw new RpcException("Não foi possível criar a tarefa de abertura.");
                target = Proc.SysExe("schtasks.exe");
                args = "/run /tn \"" + LaunchTask + "\"";
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.Arguments = args;
                sc.WorkingDirectory = Paths.AppDir;
                sc.IconLocation = Paths.ExePath + ",0";
                sc.Description = "RAGE! - monitor, limpeza e Modo Turbo";
                sc.WindowStyle = noUac ? 7 : 1; // minimizado: esconde o console do schtasks
                sc.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            Activity.Add("info", "shortcut", "Atalho criado na Área de Trabalho", noUac ? "Abre sem pedir permissão de administrador" : null);
            return new { path = lnk, noUac };
        }

        /// <summary>Se a pasta portátil mudou de lugar, atualiza a tarefa de abertura.</summary>
        public static void EnsureCurrent()
        {
            if (Program.IsAdmin && LaunchTaskExists()) EnsureLaunchTask();
        }
    }

    /// <summary>Iniciar com o Windows via Agendador de Tarefas (com privilégios elevados, sem prompt do UAC).</summary>
    public static class Autostart
    {
        const string TaskName = "RAGE Autostart";

        public static void Apply(bool enable)
        {
            try
            {
                if (enable)
                {
                    // XML para: rodar também na bateria, sem limite de tempo (o padrão do schtasks encerra após 72 h) e 20 s após o logon.
                    var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                        "  <RegistrationInfo><Description>Inicia o RAGE! na bandeja ao entrar no Windows.</Description></RegistrationInfo>\r\n" +
                        "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><Delay>PT20S</Delay></LogonTrigger></Triggers>\r\n" +
                        "  <Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
                        "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                        "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>5</Priority></Settings>\r\n" +
                        "  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(Paths.ExePath) + "</Command><Arguments>--tray</Arguments>" +
                        "<WorkingDirectory>" + System.Security.SecurityElement.Escape(Paths.AppDir) + "</WorkingDirectory></Exec></Actions>\r\n" +
                        "</Task>";
                    var f = Path.Combine(Path.GetTempPath(), "rage-autostart.xml");
                    File.WriteAllText(f, xml, Encoding.Unicode);
                    try
                    {
                        var r = Proc.Run(Proc.SysExe("schtasks.exe"), "/Create /F /TN \"" + TaskName + "\" /XML \"" + f + "\"", 20000);
                        if (!r.Ok) Log.Warn("schtasks create: " + r.Output + r.Error);
                    }
                    finally { try { File.Delete(f); } catch { } }
                }
                else Proc.Run(Proc.SysExe("schtasks.exe"), "/Delete /F /TN \"" + TaskName + "\"", 20000);
            }
            catch (Exception ex) { Log.Error("Autostart", ex); }
        }

        public static void EnsureCurrent()
        {
            if (Settings.Current.StartWithWindows && Program.IsAdmin) Apply(true);
        }
    }
}
