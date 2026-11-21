using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Rage.Core;

namespace Rage
{
    public sealed class MainForm : Form
    {
        public static MainForm Instance { get; private set; }
        readonly WebView2 web;
        readonly NotifyIcon tray;
        readonly bool startHidden;
        bool reallyClose;
        bool trayTipShown;
        static readonly Color ChromeBg = Color.FromArgb(14, 14, 16);
        readonly Label loading;
        static Dictionary<string, string> resources;

        public MainForm(bool startInTray)
        {
            Instance = this;
            startHidden = startInTray;
            Text = "RAGE!";
            try { Icon = Icon.ExtractAssociatedIcon(Paths.ExePath); } catch { }
            BackColor = ChromeBg;
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            float scale = 1f;
            try { scale = GetDpiForSystem() / 96f; } catch { }
            Size = new Size((int)Math.Min(1480 * scale, wa.Width * 0.94), (int)Math.Min(920 * scale, wa.Height * 0.94));
            MinimumSize = new Size((int)Math.Min(1100 * scale, wa.Width), (int)Math.Min(680 * scale, wa.Height));
            ApplySavedBounds(wa);

            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = ChromeBg };
            Controls.Add(web);
            loading = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = ChromeBg,
                ForeColor = Color.FromArgb(150, 155, 166), Font = new Font("Segoe UI", 11f), Text = "RAGE!  ·  carregando…",
            };
            Controls.Add(loading);
            loading.BringToFront();

            tray = new NotifyIcon { Icon = Icon, Text = "RAGE!", Visible = true };
            tray.DoubleClick += (s, e) => ShowFromTray();
            tray.ContextMenuStrip = BuildTrayMenu();

            if (startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForSystem();

        void ApplySavedBounds(Rectangle wa)
        {
            try
            {
                var b = Settings.Current.WindowBounds;
                if (string.IsNullOrEmpty(b)) return;
                var p = b.Split(',');
                var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)) || r.Width < 400 || r.Height < 300) return;
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
                if (p.Length > 4 && p[4] == "max") WindowState = FormWindowState.Maximized;
            }
            catch { }
        }

        void SaveBounds()
        {
            try
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (r.Width < 400) return;
                Settings.Current.WindowBounds = r.X + "," + r.Y + "," + r.Width + "," + r.Height + (WindowState == FormWindowState.Maximized ? ",max" : "");
                Settings.Save();
            }
            catch { }
        }

        ContextMenuStrip BuildTrayMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Abrir RAGE!", null, (s, e) => ShowFromTray());
            var turbo = new ToolStripMenuItem("Modo Turbo");
            m.Items.Add(turbo);
            m.Items.Add("Limpar memória RAM", null, async (s, e) =>
            {
                var r = await Task.Run(() => Boost.RamCleaner.Clean(true));
                Notify("Memória liberada", r.FreedText + " liberados");
            });
            var overlay = new ToolStripMenuItem("Overlay de jogo");
            overlay.Click += (s, e) => Monitor.Overlay.Toggle();
            m.Items.Add(overlay);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Sair", null, (s, e) => Quit());
            m.Opening += (s, e) =>
            {
                var active = Boost.BoostService.Active;
                turbo.Text = active ? "Desligar Modo Turbo" : "Ligar Modo Turbo";
                overlay.Checked = Monitor.Overlay.Visible;
            };
            turbo.Click += async (s, e) =>
            {
                if (Boost.BoostService.Active) await Task.Run(() => Boost.BoostService.Stop("bandeja"));
                else await Task.Run(() => Boost.BoostService.Start(Settings.Current.BoostMode, null, "bandeja"));
            };
            return m;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar(Settings.Current.Theme);
            Native.ChangeWindowMessageFilterEx(Handle, Program.ShowMessage, 1, IntPtr.Zero);
        }

        public void ApplyTitleBar(string theme)
        {
            try
            {
                var dark = theme != "light";
                // COLORREF = 0x00BBGGRR
                Native.DarkTitleBar(Handle, dark, dark ? 0x00100E0E : 0x00FAF7F5);
            }
            catch { }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Services.StartAll();
            await InitWebView();
            if (startHidden) BeginInvoke((Action)(() => { Hide(); WindowState = FormWindowState.Normal; ShowInTaskbar = true; Bridge.UiVisible = false; }));
        }

        async Task InitWebView()
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection,msWebOOUI,msPdfOOUI --disable-background-timer-throttling");
                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView, opts);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                Log.Error("WebView2", ex);
                var r = MessageBox.Show(this,
                    "O RAGE! precisa do Microsoft Edge WebView2 Runtime para exibir a interface.\n\nDeseja abrir a página de download agora?\n\nDetalhe: " + ex.Message,
                    "RAGE!", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) Proc.Open("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Quit();
                return;
            }

            var core = web.CoreWebView2;
            var dev = Environment.GetEnvironmentVariable("RAGE_DEV_URL");
            var debug = !string.IsNullOrEmpty(dev) || Program.Args.Contains("--devtools");
            core.Settings.AreDefaultContextMenusEnabled = debug;
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = debug;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            Bridge.Attach(this, core);

            core.NewWindowRequested += (s, e) => { e.Handled = true; TryOpenExternal(e.Uri); };
            core.NavigationCompleted += (s, e) => { if (loading.Visible) { loading.Visible = false; loading.SendToBack(); } };
            core.NavigationStarting += (s, e) =>
            {
                var u = e.Uri ?? "";
                if (u.StartsWith("https://rage.app/") || (!string.IsNullOrEmpty(dev) && u.StartsWith(dev)) || u.StartsWith("about:")) return;
                e.Cancel = true;
                TryOpenExternal(u);
            };

            if (!string.IsNullOrEmpty(dev)) { core.Navigate(dev); return; }
            LoadResources();
            core.AddWebResourceRequestedFilter("https://rage.app/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (s, e) => Serve(core, e);
            var pi = Array.IndexOf(Program.Args, "--page");
            var page = pi >= 0 && pi + 1 < Program.Args.Length ? Program.Args[pi + 1] : null;
            core.Navigate("https://rage.app/index.html" + (string.IsNullOrEmpty(page) ? "" : "#" + (page.StartsWith("/") ? page : "/" + page)));
        }

        static void TryOpenExternal(string uri)
        {
            if (uri != null && (uri.StartsWith("http://") || uri.StartsWith("https://"))) { try { Proc.Open(uri); } catch { } }
        }

        static void LoadResources()
        {
            resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith("ui/")) resources[n.Substring(3).Replace('\\', '/')] = n;
        }

        static readonly Dictionary<string, string> Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8",
            [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".ico"] = "image/x-icon",
            [".woff2"] = "font/woff2", [".woff"] = "font/woff", [".ttf"] = "font/ttf", [".json"] = "application/json", [".webp"] = "image/webp",
        };

        static void Serve(CoreWebView2 core, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                var uri = new Uri(e.Request.Uri);
                var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                if (path == "") path = "index.html";
                if (!resources.TryGetValue(path, out var res)) res = resources.TryGetValue("index.html", out var idx) ? idx : null;
                if (res == null) { e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
                var ext = Path.GetExtension(res);
                var type = Mime.TryGetValue(ext, out var t) ? t : "application/octet-stream";
                var cache = ext == ".html" ? "no-cache" : "max-age=31536000, immutable";
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + type + "\r\nCache-Control: " + cache + "\r\nAccess-Control-Allow-Origin: *");
            }
            catch (Exception ex) { Log.Error("Servir recurso", ex); }
        }

        public void ShowFromTray()
        {
            if (InvokeRequired) { BeginInvoke((Action)ShowFromTray); return; }
            Show();
            ShowInTaskbar = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            Native.SetForegroundWindow(Handle);
            Bridge.UiVisible = true;
            Bridge.Emit("app.shown", null);
        }

        public void HideToTray()
        {
            if (InvokeRequired) { BeginInvoke((Action)HideToTray); return; }
            if (Visible) SaveBounds();
            Hide();
            Bridge.UiVisible = false;
            if (!trayTipShown)
            {
                trayTipShown = true;
                Notify("O RAGE! continua ativo", "Monitorando e pronto para o Modo Turbo. Clique no ícone para abrir.");
            }
        }

        public void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
        {
            if (InvokeRequired) { BeginInvoke((Action)(() => Notify(title, text, icon))); return; }
            try { tray.ShowBalloonTip(4000, title, text, icon); } catch { }
        }

        public void Quit()
        {
            if (InvokeRequired) { BeginInvoke((Action)Quit); return; }
            reallyClose = true;
            Close();
        }

        public T OnUi<T>(Func<T> f) => InvokeRequired ? (T)Invoke(f) : f();

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyClose && e.CloseReason == CloseReason.UserClosing && Settings.Current.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (Visible) SaveBounds();
            try { if (Boost.BoostService.Active) Boost.BoostService.Stop("saída"); } catch { }
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated && Visible) Bridge.UiVisible = WindowState != FormWindowState.Minimized;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            Bridge.UiVisible = Visible && WindowState != FormWindowState.Minimized;
        }

        protected override void WndProc(ref Message m)
        {
            if (Program.ShowMessage != 0 && m.Msg == Program.ShowMessage) { ShowFromTray(); return; }
            if (m.Msg == 0x0312) { Monitor.Overlay.OnHotkey(m.WParam.ToInt32()); return; } // WM_HOTKEY
            base.WndProc(ref m);
        }
    }
}
