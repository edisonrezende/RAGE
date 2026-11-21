using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Rage.Core;

namespace Rage.Monitor
{
    /// <summary>
    /// Mini monitor: uma pílula sempre visível com RAM e CPU, que pode ser arrastada para qualquer lugar.
    /// Um clique abre o painel com anéis, processos e atalhos. Some sozinho em tela cheia.
    /// </summary>
    public static class MiniMonitor
    {
        static MiniPill pill;
        static MiniPanel panel;
        static Timer disposeTimer;
        static volatile bool panelOpen, suppressed;
        static Snapshot last;

        public static bool PanelOpen => panelOpen;
        public static bool PanelShowing => panelOpen && !suppressed;
        public static bool Enabled => Settings.Current.MiniMonitor;

        /// <summary>Chamado uma vez, já na thread da interface.</summary>
        public static void Init()
        {
            Settings.Changed += ApplySettings;
            ApplySettings();
            if (Program.Args.Contains("--mini-open")) OnUi(() => { if (pill != null && pill.Visible) OpenPanel(); });
        }

        static void OnUi(Action a)
        {
            var mf = MainForm.Instance;
            if (mf == null || mf.IsDisposed || !mf.IsHandleCreated) return;
            try { mf.BeginInvoke(a); } catch { }
        }

        public static void ApplySettings()
        {
            OnUi(() =>
            {
                if (Enabled)
                {
                    if (pill == null || pill.IsDisposed) pill = new MiniPill();
                    if (!suppressed && !pill.Visible) { pill.Show(); pill.Render(last ?? MonitorService.Last); }
                }
                else
                {
                    ClosePanel();
                    if (pill != null && !pill.IsDisposed && pill.Visible) pill.Hide();
                }
            });
        }

        /// <summary>A cada leitura do monitor (1x por segundo).</summary>
        public static void Update(Snapshot s)
        {
            last = s;
            if (!Enabled || pill == null) return;
            OnUi(() =>
            {
                if (!Enabled || pill == null || pill.IsDisposed) return;
                var fg = Native.GetForegroundWindow();
                var ours = fg == pill.Handle || (panel != null && !panel.IsDisposed && fg == panel.Handle) || fg == MainForm.Instance?.Handle;
                var full = Settings.Current.MiniMonitorHideFullscreen && !ours && Native.IsBorderlessFullscreen(fg);
                if (full != suppressed)
                {
                    suppressed = full;
                    if (full) { pill.Hide(); if (panel != null && !panel.IsDisposed) panel.Hide(); }
                    else { pill.Show(); if (panelOpen && panel != null && !panel.IsDisposed) { PlacePanel(); panel.Show(); } }
                }
                if (!suppressed) pill.Render(s);
            });
        }

        // ---------- Painel ----------
        internal static void TogglePanel() { if (panelOpen) ClosePanel(); else OpenPanel(); }

        static void OpenPanel()
        {
            if (pill == null || pill.IsDisposed) return;
            disposeTimer?.Stop();
            if (panel == null || panel.IsDisposed) panel = new MiniPanel();
            panelOpen = true;
            PlacePanel();
            panel.Show();
            panel.Start();
            pill.Render(null);
            Bridge.Emit("mini", new { open = true });
        }

        static void ClosePanel()
        {
            if (!panelOpen) return;
            panelOpen = false;
            if (panel != null && !panel.IsDisposed) panel.Hide();
            if (pill != null && !pill.IsDisposed && pill.Visible) pill.Render(null);
            Bridge.Emit("mini", new { open = false });
            // Fechado por um tempo, o painel é descartado para devolver a memória do WebView.
            if (disposeTimer == null)
            {
                disposeTimer = new Timer { Interval = 120000 };
                disposeTimer.Tick += (s, e) =>
                {
                    disposeTimer.Stop();
                    if (panelOpen || panel == null) return;
                    try { panel.Dispose(); } catch { }
                    panel = null;
                };
            }
            disposeTimer.Stop();
            disposeTimer.Start();
        }

        /// <summary>Encosta o painel na pílula: abaixo dela, ou acima quando não cabe.</summary>
        internal static void PlacePanel()
        {
            if (panel == null || panel.IsDisposed || pill == null) return;
            var pb = pill.Bounds;
            var wa = Screen.FromRectangle(pb).WorkingArea;
            var scale = pill.UiScale;
            int w = (int)(MiniPanel.W * scale), h = (int)(MiniPanel.H * scale), gap = (int)(6 * scale);
            h = Math.Min(h, wa.Height - gap * 2);
            int x = pb.Left;
            if (x + w > wa.Right) x = pb.Right - w;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            int y = pb.Bottom + gap;
            if (y + h > wa.Bottom) y = pb.Top - gap - h;
            if (y < wa.Top) y = Math.Max(wa.Top, wa.Bottom - h);
            panel.Bounds = new Rectangle(x, y, w, h);
        }

        internal static bool Expanded => panelOpen;

        // ---------- RPC ----------
        [Rpc("mini.state")]
        public static object State() => new { enabled = Enabled, open = panelOpen, hideFullscreen = Settings.Current.MiniMonitorHideFullscreen };

        [Rpc("mini.close")]
        public static object Close() { OnUi(ClosePanel); return true; }

        [Rpc("mini.setEnabled")]
        public static object SetEnabled(bool on)
        {
            Settings.Current.MiniMonitor = on;
            Settings.Save();
            return Settings.Current;
        }

        [Rpc("mini.resetPosition")]
        public static object ResetPosition()
        {
            Settings.Current.MiniMonitorPos = null;
            Settings.Save();
            OnUi(() => { if (pill != null && !pill.IsDisposed) { pill.MoveToSaved(); PlacePanel(); } });
            return true;
        }
    }

    /// <summary>A pílula: janela em camadas desenhada em GDI+, sem roubar o foco de quem está em uso.</summary>
    sealed class MiniPill : Form
    {
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        const float W = 198, H = 32;
        static readonly Font LabelFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        static readonly Font ValueFont = new Font("Segoe UI Semibold", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        Snapshot last;
        bool hover, pressed, dragging;
        Point downCursor, downLocation;
        readonly ContextMenuStrip menu;

        public MiniPill()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "RAGE! mini monitor";
            Size = PixelSize();
            MoveToSaved();
            menu = BuildMenu();
        }

        /// <summary>Escala real da tela onde a pílula está.</summary>
        public float UiScale => Native.Scale(IsHandleCreated ? Handle : IntPtr.Zero);
        Size PixelSize() { var s = UiScale; return new Size((int)Math.Ceiling(W * s), (int)Math.Ceiling(H * s)); }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x8000000 | 0x8; // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Abrir RAGE!", null, (s, e) => MainForm.Instance?.ShowFromTray());
            m.Items.Add("Limpar memória RAM", null, async (s, e) =>
            {
                var r = await Task.Run(() => Boost.RamCleaner.Clean(true));
                Activity.Add("clean", "memory", "Memória RAM liberada", r.Trimmed + " processos otimizados", r.FreedText);
                MainForm.Instance?.Notify("Memória liberada", r.FreedText + " liberados");
            });
            var turbo = new ToolStripMenuItem("Modo Turbo");
            turbo.Click += async (s, e) =>
            {
                if (Boost.BoostService.Active) await Task.Run(() => Boost.BoostService.Stop("mini monitor"));
                else await Task.Run(() => Boost.BoostService.Start(Settings.Current.BoostMode, null, "mini monitor"));
            };
            m.Items.Add(turbo);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Voltar para a posição padrão", null, (s, e) => MiniMonitor.ResetPosition());
            m.Items.Add("Ocultar o mini monitor", null, (s, e) =>
            {
                MiniMonitor.SetEnabled(false);
                MainForm.Instance?.Notify("Mini monitor oculto", "Para mostrar de novo, use o ícone do RAGE! na bandeja ou as Configurações.");
            });
            m.Opening += (s, e) => turbo.Text = Boost.BoostService.Active ? "Desligar Modo Turbo" : "Ligar Modo Turbo";
            return m;
        }

        /// <summary>Posição gravada; se não houver ou tiver ficado fora das telas, topo do monitor principal, ao centro.</summary>
        public void MoveToSaved()
        {
            var scale = UiScale;
            Point? p = null;
            try
            {
                var raw = Settings.Current.MiniMonitorPos;
                if (!string.IsNullOrEmpty(raw))
                {
                    var parts = raw.Split(',');
                    var r = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), Width, Height);
                    if (Screen.AllScreens.Any(sc => sc.Bounds.Contains(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2)))) p = r.Location;
                }
            }
            catch { }
            if (p == null)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                p = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (int)(8 * scale));
            }
            Location = p.Value;
            if (IsHandleCreated && Visible) Render(null);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = true; dragging = false;
            downCursor = Cursor.Position; downLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pressed) return;
            var c = Cursor.Position;
            int dx = c.X - downCursor.X, dy = c.Y - downCursor.Y;
            if (!dragging && Math.Abs(dx) + Math.Abs(dy) < 5 * UiScale) return;
            dragging = true;
            var target = new Rectangle(downLocation.X + dx, downLocation.Y + dy, Width, Height);
            var b = Screen.FromPoint(c).Bounds;
            target.X = Math.Max(b.Left, Math.Min(target.X, b.Right - Width));
            target.Y = Math.Max(b.Top, Math.Min(target.Y, b.Bottom - Height));
            Location = target.Location;
            MiniMonitor.PlacePanel();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                Native.SetForegroundWindow(Handle); // para o menu fechar ao clicar fora
                menu.Show(Cursor.Position);
                return;
            }
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (dragging)
            {
                dragging = false;
                Settings.Current.MiniMonitorPos = Left + "," + Top;
                Settings.Save();
                MiniMonitor.PlacePanel();
            }
            else MiniMonitor.TogglePanel();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Render(null); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Render(null); }

        public void Render(Snapshot s)
        {
            if (s != null) last = s;
            s = last;
            if (!IsHandleCreated || IsDisposed) return;
            var scale = UiScale;
            int w = (int)Math.Ceiling(W * scale), h = (int)Math.Ceiling(H * scale);
            if (Width != w || Height != h) Size = new Size(w, h); // mudou de monitor ou de escala
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    g.ScaleTransform(scale, scale);
                    var turbo = Boost.BoostService.Active;
                    using (var path = Brand.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), H / 2))
                    using (var bg = new SolidBrush(hover ? Color.FromArgb(246, 30, 30, 34) : Color.FromArgb(244, 17, 17, 20)))
                    using (var border = new Pen(turbo ? Color.FromArgb(220, 255, 106, 43) : Color.FromArgb(hover ? 84 : 52, 255, 255, 255), 1f))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                    Brand.DrawLogo(g, new RectangleF(6, 6, 20, 20));

                    using (var muted = new SolidBrush(Color.FromArgb(150, 156, 168)))
                    using (var left = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                    {
                        void Stat(float x, string label, double? pct, double warn, double danger)
                        {
                            g.DrawString(label, LabelFont, muted, new RectangleF(x, 1, 30, H), left);
                            using (var b = new SolidBrush(Brand.Zone(pct, warn, danger)))
                                g.DrawString(pct.HasValue ? pct.Value.ToString("0") + "%" : "--", ValueFont, b, new RectangleF(x + 22, 0, 44, H), right);
                        }
                        Stat(33, "RAM", s?.Ram?.Pct, 80, 92);
                        Stat(106, "CPU", s?.Cpu?.Load, 80, 95);
                    }
                    using (var div = new Pen(Color.FromArgb(36, 255, 255, 255), 1f)) g.DrawLine(div, 102.5f, 10, 102.5f, 22);

                    // seta: aponta para baixo fechado, para cima com o painel aberto
                    float cx = 183, cy = 16, d = MiniMonitor.Expanded ? -2.2f : 2.2f;
                    using (var pen = new Pen(Color.FromArgb(hover ? 255 : 190, 226, 229, 235), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(cx - 4.2f, cy - d), new PointF(cx, cy + d), new PointF(cx + 4.2f, cy - d) });
                }
                Push(bmp);
            }
        }

        void Push(Bitmap bmp)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memDc, hBmp);
            try
            {
                var size = new Size(bmp.Width, bmp.Height);
                var src = new Point(0, 0);
                var dst = new Point(Left, Top);
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBmp);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>O painel aberto: a mesma interface do app (rota /mini) num WebView2 pequeno, sem moldura.</summary>
    sealed class MiniPanel : Form
    {
        public const int W = 348, H = 468;
        readonly WebView2 web;
        bool started;

        public MiniPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Text = "RAGE! mini monitor";
            BackColor = Color.FromArgb(14, 14, 16);
            web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(web);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x8;     // TOOLWINDOW | TOPMOST
                cp.ClassStyle |= 0x20000;     // CS_DROPSHADOW
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int corner = 2; Native.DwmSetWindowAttribute(Handle, 33, ref corner, 4);   // cantos arredondados (Windows 11)
                int border = 0x002E2A2A; Native.DwmSetWindowAttribute(Handle, 34, ref border, 4); // borda discreta
            }
            catch { }
        }

        /// <summary>Carrega a página na primeira vez que o painel aparece.</summary>
        public async void Start()
        {
            if (started) return;
            started = true;
            try
            {
                var env = await MainForm.WebEnv;
                await web.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;
                MainForm.SetupView(this, web.CoreWebView2, "/mini", () => MiniMonitor.PanelShowing);
            }
            catch (Exception ex) { Log.Error("Mini monitor", ex); started = false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { if (web.CoreWebView2 != null) Bridge.Detach(web.CoreWebView2); } catch { } }
            base.Dispose(disposing);
        }
    }
}
