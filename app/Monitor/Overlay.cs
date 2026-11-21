using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Rage.Core;

namespace Rage.Monitor
{
    /// <summary>Overlay de jogo: janela transparente, sempre no topo e "clicável através" com FPS, CPU, GPU e RAM.</summary>
    public static class Overlay
    {
        static OverlayForm form;
        static bool? manual;   // escolha feita pelo atalho, bandeja ou botão "Testar"; vale até um jogo abrir ou fechar
        static bool auto;      // há um jogo aberto
        static HotkeyWindow hotkeyWindow;
        static string registered;
        static bool paused;
        public static bool Visible => form != null && !form.IsDisposed && form.Visible;
        const int HotkeyId = 0x5241;

        public static void Init()
        {
            var mf = MainForm.Instance;
            if (mf == null) return;
            mf.OnUi(() =>
            {
                // Janela própria só para o atalho: a janela principal é recriada ao sair da bandeja e perdia o registro.
                if (hotkeyWindow == null) hotkeyWindow = new HotkeyWindow();
                if (RegisterHotkey(Settings.Current.OverlayHotkey)) Log.Info("Atalho do overlay: " + Settings.Current.OverlayHotkey);
                else Log.Warn("Atalho do overlay não registrado (em uso por outro programa?): " + Settings.Current.OverlayHotkey);
                return true;
            });
            if (Program.Args.Contains("--overlay-show")) Toggle(); // para testes: já abre com o overlay na tela
        }

        /// <summary>Registra o atalho global. Falso se a combinação for inválida ou já estiver em uso por outro programa.</summary>
        static bool RegisterHotkey(string hotkey)
        {
            if (hotkeyWindow == null) return false;
            Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
            registered = null;
            if (!ParseHotkey(hotkey, out var mods, out var vk)) return false;
            if (!Native.RegisterHotKey(hotkeyWindow.Handle, HotkeyId, mods | 0x4000, vk)) return false; // MOD_NOREPEAT
            registered = hotkey;
            return true;
        }

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            foreach (var part in (s ?? "").Split('+').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": mods |= 2; break;
                    case "alt": mods |= 1; break;
                    case "shift": mods |= 4; break;
                    case "win": mods |= 8; break;
                    default:
                        if (vk != 0 || char.IsDigit(part[0]) || !Enum.TryParse<Keys>(part, true, out var k)) return false;
                        vk = (uint)k;
                        break;
                }
            }
            return vk != 0;
        }

        public static void OnHotkey(int id) { if (id == HotkeyId) Toggle(); }

        public static void Toggle()
        {
            manual = !Visible;
            Apply();
        }

        public static void ShowAuto(bool show) { auto = show; manual = null; Apply(); }

        static void Apply()
        {
            var mf = MainForm.Instance;
            if (mf == null) return;
            mf.BeginInvoke((Action)(() =>
            {
                var want = manual ?? (auto && Settings.Current.OverlayEnabled && Settings.Current.OverlayAutoInGame);
                if (want)
                {
                    if (form == null || form.IsDisposed) form = new OverlayForm();
                    if (!form.Visible) form.Show();
                    form.Render(MonitorService.Last);
                }
                else if (form != null && form.Visible) form.Hide();
                Bridge.Emit("overlay", new { visible = want });
            }));
        }

        public static void Update(Snapshot s)
        {
            var f = form;
            if (f == null || f.IsDisposed || !f.Visible) return;
            try { f.BeginInvoke((Action)(() => f.Render(s))); } catch { }
        }

        [Rpc("overlay.toggle")]
        public static object ToggleRpc(bool? show)
        {
            manual = show ?? !Visible;
            Apply();
            return new { visible = manual };
        }

        [Rpc("overlay.applySettings")]
        public static object ApplySettings()
        {
            var want = Settings.Current.OverlayHotkey;
            if (!paused && want != registered) MainForm.Instance?.OnUi(() => RegisterHotkey(want));
            Apply();
            return true;
        }

        /// <summary>Troca o atalho. Só grava se o Windows aceitar a combinação; senão mantém o anterior.</summary>
        [Rpc("overlay.setHotkey")]
        public static object SetHotkey(string hotkey)
        {
            var mf = MainForm.Instance;
            hotkey = (hotkey ?? "").Trim();
            if (!ParseHotkey(hotkey, out _, out _)) throw new RpcException("Combinação inválida. Use Ctrl, Alt ou Shift junto com uma tecla.");
            var previous = Settings.Current.OverlayHotkey;
            paused = false;
            if (mf != null && !mf.OnUi(() => RegisterHotkey(hotkey)))
            {
                mf.OnUi(() => RegisterHotkey(previous));
                throw new RpcException("O Windows recusou " + hotkey + ": outro programa já usa esse atalho. Escolha outra combinação.");
            }
            Settings.Current.OverlayHotkey = hotkey;
            Settings.Save();
            return new { hotkey };
        }

        /// <summary>Solta o atalho enquanto a tela de configurações captura uma nova combinação.</summary>
        [Rpc("overlay.captureHotkey")]
        public static object CaptureHotkey(bool on)
        {
            paused = on;
            var mf = MainForm.Instance;
            if (mf == null) return true;
            if (on) mf.OnUi(() => { if (hotkeyWindow != null) Native.UnregisterHotKey(hotkeyWindow.Handle, HotkeyId); registered = null; return true; });
            else mf.OnUi(() => RegisterHotkey(Settings.Current.OverlayHotkey));
            return true;
        }
    }

    /// <summary>Janela invisível que só recebe o WM_HOTKEY.</summary>
    sealed class HotkeyWindow : NativeWindow
    {
        public HotkeyWindow() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); } // HWND_MESSAGE
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) Overlay.OnHotkey(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    sealed class OverlayForm : Form
    {
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        static readonly Font Big = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font Label = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        static readonly Font Val = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(10, 10);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;

        static string T(double? v) => v.HasValue ? v.Value.ToString("0") + "°" : "--";
        static string P(double? v) => v.HasValue ? v.Value.ToString("0") + "%" : "--";

        public void Render(Snapshot s)
        {
            if (s == null) return;
            var g0 = MonitorService.PrimaryGpu(s.Gpus);
            var game = s.Game as Fps.GameInfo;
            var rows = new[]
            {
                ("CPU", T(s.Cpu?.Temp), P(s.Cpu?.Load)),
                ("GPU", g0?.State == "sleep" ? "zzz" : T(g0?.Temp), P(g0?.Load)),
                ("RAM", s.Ram != null ? (s.Ram.Used / 1073741824.0).ToString("0.0") + "G" : "--", P(s.Ram?.Pct)),
            };
            var scale = Native.Scale(Handle);
            int w = (int)(178 * scale), rowH = (int)(19 * scale), pad = (int)(10 * scale);
            int head = (int)(30 * scale);
            int h = pad * 2 + head + rows.Length * rowH;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (var path = Round(new RectangleF(0.5f, 0.5f, w - 1, h - 1), 10 * scale))
                    using (var bg = new SolidBrush(Color.FromArgb(200, 12, 12, 14)))
                    using (var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                    g.ScaleTransform(scale, scale);
                    using (var white = new SolidBrush(Color.FromArgb(245, 246, 248)))
                    using (var muted = new SolidBrush(Color.FromArgb(150, 156, 168)))
                    {
                        var fps = game?.Fps;
                        Brand.DrawLogo(g, new RectangleF(9, 10, 17, 17));
                        g.DrawString(fps.HasValue ? fps.Value.ToString("0") + " FPS" : "-- FPS", Big, white, 30, 8);
                        if (game?.Low1 != null) g.DrawString("1% " + game.Low1.Value.ToString("0"), Label, muted, 122, 13);
                        float y = 38;
                        foreach (var r in rows)
                        {
                            g.DrawString(r.Item1, Label, muted, 10, y + 2);
                            g.DrawString(r.Item2, Val, white, 58, y);
                            g.DrawString(r.Item3, Val, white, 118, y);
                            y += 19;
                        }
                    }
                }
                Place(w, h);
                Push(bmp);
            }
            // Jogos em tela cheia sem bordas também se marcam como "sempre no topo" e passam por cima ao ganhar o foco.
            Native.KeepOnTop(Handle);
        }

        void Place(int w, int h)
        {
            var fg = Native.GetForegroundWindow();
            var scr = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
            var b = scr.Bounds;
            int m = (int)(14 * Native.Scale(Handle));
            var pos = Settings.Current.OverlayPosition ?? "top-left";
            int x = pos.EndsWith("right") ? b.Right - w - m : b.Left + m;
            int y = pos.StartsWith("bottom") ? b.Bottom - h - m - 40 : b.Top + m;
            Bounds = new Rectangle(x, y, w, h);
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

        static GraphicsPath Round(RectangleF r, float rad) => Brand.Round(r, rad);
    }
}
