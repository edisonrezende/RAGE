using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>
    /// Organizador da Área de Trabalho: lê os ícones como o Explorer os mostra (posição, cor dominante, fornecedor),
    /// aplica um arranjo novo e guarda o arranjo anterior para desfazer. Quem decide o arranjo é a interface.
    /// </summary>
    public static class DesktopOrganizer
    {
        // ---------- COM: a visão de pasta da Área de Trabalho (dentro do explorer.exe) ----------
        [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
        class CShellWindows { }

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        interface IShellWindows
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object pvarLoc, [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void GetWindow(); void ContextSensitiveHelp(); void InsertMenusSB(); void SetMenuSB(); void RemoveMenusSB(); void SetStatusTextSB();
            void EnableModelessSB(); void TranslateAcceleratorSB(); void BrowseObject(); void GetViewStateStream(); void GetControlWindow(); void SendControlMsg();
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        /// <summary>IFolderView + IFolderView2 (até GetViewModeAndIconSize). Só os métodos usados têm assinatura real.</summary>
        [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFolderView2
        {
            // IFolderView
            [PreserveSig] int GetCurrentViewMode(out uint mode);
            [PreserveSig] int SetCurrentViewMode(uint mode);
            [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int Item(int index, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint flags, out int count);
            [PreserveSig] int Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
            [PreserveSig] int GetSelectionMarkedItem(out int item);
            [PreserveSig] int GetFocusedItem(out int item);
            [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT pt);
            [PreserveSig] int GetSpacing(ref POINT pt);
            [PreserveSig] int GetDefaultSpacing(out POINT pt);
            [PreserveSig] int GetAutoArrange();
            [PreserveSig] int SelectItem(int item, uint flags);
            [PreserveSig] int SelectAndPositionItems(uint cidl, ref IntPtr apidl, ref POINT apt, uint flags);
            // IFolderView2
            void SetGroupBy(); void GetGroupBy(); void SetViewProperty(); void GetViewProperty(); void SetTileViewProperties(); void SetExtendedTileViewProperties(); void SetText();
            [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
            [PreserveSig] int GetCurrentFolderFlags(out uint flags);
            void GetSortColumnCount(); void SetSortColumns(); void GetSortColumns(); void GetItem(); void GetVisibleItem(); void GetSelectedItem();
            void GetSelection(); void GetSelectionState(); void InvokeVerbOnSelection(); void SetViewModeAndIconSize();
            [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int iconSize);
        }

        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        const int SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
        const uint SVGIO_ALLVIEW = 2, SVSI_POSITIONITEM = 0x80;
        const uint FWF_AUTOARRANGE = 0x1, FWF_SNAPTOGRID = 0x4;

        [DllImport("shell32.dll")] static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);
        const uint SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000, SIGDN_FILESYSPATH = 0x80058000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attrs, ref SHFILEINFO sfi, uint cb, uint flags);
        const uint SHGFI_PIDL = 0x8, SHGFI_SYSICONINDEX = 0x4000;
        [DllImport("shell32.dll", EntryPoint = "#727")] static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
        [DllImport("comctl32.dll")] static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        const int SHIL_EXTRALARGE = 2; // 48 x 48

        /// <summary>A visão de pasta vive no Explorer; as chamadas passam por proxies COM, então rodam numa thread STA própria.</summary>
        static T OnSta<T>(Func<T> f)
        {
            T result = default(T);
            ExceptionDispatchInfo err = null;
            var t = new Thread(() => { try { result = f(); } catch (Exception ex) { err = ExceptionDispatchInfo.Capture(ex); } }) { IsBackground = true, Name = "RAGE desktop" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            err?.Throw();
            return result;
        }

        static IFolderView2 DesktopView()
        {
            try
            {
                var windows = (IShellWindows)new CShellWindows();
                object loc = 0 /* CSIDL_DESKTOP */, root = new object();
                var disp = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
                if (disp == null) throw new RpcException("Não encontrei a Área de Trabalho do Windows. O Explorer está aberto?");
                var sid = SID_STopLevelBrowser; var iid = typeof(IShellBrowser).GUID;
                if (((IServiceProvider)disp).QueryService(ref sid, ref iid, out var browser) != 0 || browser == null) throw new RpcException("A Área de Trabalho não respondeu.");
                if (((IShellBrowser)browser).QueryActiveShellView(out var view) != 0 || view == null) throw new RpcException("A Área de Trabalho não respondeu.");
                return (IFolderView2)view;
            }
            catch (RpcException) { throw; }
            catch (Exception ex)
            {
                Log.Warn("Área de trabalho (COM): " + ex.Message);
                throw new RpcException("Não foi possível falar com a Área de Trabalho do Windows. Reinicie o Explorer e tente de novo.");
            }
        }

        static string NameOf(IntPtr pidl, uint sigdn)
        {
            if (SHGetNameFromIDList(pidl, sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        // ---------- Leitura dos ícones ----------
        sealed class Raw
        {
            public IntPtr Pidl; public string Id, Name, Path; public int X, Y;
        }

        sealed class Geometry
        {
            public int CellW, CellH, BaseX, BaseY, Cols, Rows, IconSize; public bool AutoArrange, SnapToGrid;
        }

        static List<Raw> ReadItems(IFolderView2 view)
        {
            var list = new List<Raw>();
            if (view.ItemCount(SVGIO_ALLVIEW, out var n) != 0) return list;
            for (int i = 0; i < n; i++)
            {
                if (view.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
                var id = NameOf(pidl, SIGDN_DESKTOPABSOLUTEPARSING);
                if (string.IsNullOrEmpty(id)) { Marshal.FreeCoTaskMem(pidl); continue; }
                view.GetItemPosition(pidl, out var pt);
                list.Add(new Raw { Pidl = pidl, Id = id, Name = NameOf(pidl, SIGDN_NORMALDISPLAY) ?? id, Path = NameOf(pidl, SIGDN_FILESYSPATH), X = pt.X, Y = pt.Y });
            }
            return list;
        }

        static void Free(List<Raw> items) { foreach (var r in items) if (r.Pidl != IntPtr.Zero) { Marshal.FreeCoTaskMem(r.Pidl); r.Pidl = IntPtr.Zero; } }

        static int Mode(IEnumerable<int> values, int fallback)
        {
            var g = values.GroupBy(v => v).OrderByDescending(x => x.Count()).FirstOrDefault();
            return g == null ? fallback : g.Key;
        }

        static Geometry ReadGeometry(IFolderView2 view, List<Raw> items)
        {
            var sp = new POINT();
            if (view.GetSpacing(ref sp) != 0 || sp.X <= 0 || sp.Y <= 0) view.GetDefaultSpacing(out sp);
            if (sp.X <= 0 || sp.Y <= 0) { sp.X = 76; sp.Y = 100; }
            view.GetCurrentFolderFlags(out var flags);
            int iconSize = 48;
            try { view.GetViewModeAndIconSize(out _, out iconSize); } catch { }

            // A lista de ícones cobre todos os monitores; o arranjo usa só a área útil do monitor principal.
            var work = Screen.PrimaryScreen.WorkingArea;
            var all = SystemInformation.VirtualScreen;
            int left = work.Left - all.Left, top = work.Top - all.Top;
            // Deslocamento do ícone dentro da célula: o resto mais comum entre os ícones que já estão lá.
            int offX = Mode(items.Where(i => i.X >= left && i.X < left + work.Width).Select(i => (i.X - left) % sp.X), 0);
            int offY = Mode(items.Where(i => i.Y >= top && i.Y < top + work.Height).Select(i => (i.Y - top) % sp.Y), 0);
            return new Geometry
            {
                CellW = sp.X, CellH = sp.Y, BaseX = left + offX, BaseY = top + offY,
                Cols = Math.Max(1, work.Width / sp.X), Rows = Math.Max(1, work.Height / sp.Y), IconSize = iconSize,
                AutoArrange = (flags & FWF_AUTOARRANGE) != 0, SnapToGrid = (flags & FWF_SNAPTOGRID) != 0,
            };
        }

        // ---------- Ícone e cor dominante ----------
        /// <summary>Lista de imagens do sistema com os ícones de 48 px (o ponteiro IImageList serve como HIMAGELIST).</summary>
        static IntPtr SystemIcons()
        {
            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IImageList
            return SHGetImageList(SHIL_EXTRALARGE, ref iid, out var list) == 0 ? list : IntPtr.Zero;
        }

        static Bitmap IconOf(IntPtr pidl, IntPtr imageList)
        {
            try
            {
                if (imageList == IntPtr.Zero) return null;
                var sfi = new SHFILEINFO();
                if (SHGetFileInfo(pidl, 0, ref sfi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_PIDL | SHGFI_SYSICONINDEX) == IntPtr.Zero) return null;
                var h = ImageList_GetIcon(imageList, sfi.iIcon, 1 /* ILD_TRANSPARENT */);
                if (h == IntPtr.Zero) return null;
                try { using (var ic = Icon.FromHandle(h)) return ic.ToBitmap(); }
                finally { DestroyIcon(h); }
            }
            catch { return null; }
        }

        sealed class Tone { public string Hex = "#8a8f98"; public double Hue, Sat, Lum = 0.5; public bool Neutral = true; }

        static void ToHsl(double r, double g, double b, out double h, out double s, out double l)
        {
            r /= 255; g /= 255; b /= 255;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            l = (max + min) / 2;
            if (d < 1e-6) { h = 0; s = 0; return; }
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = ((g - b) / d) % 6; else if (max == g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }

        /// <summary>Cor que "manda" no ícone: o matiz com mais área colorida. Ícones sem cor (cinza, preto, branco) saem como neutros.</summary>
        static Tone Dominant(Bitmap bmp)
        {
            const int Bins = 36;
            var w = new double[Bins]; var rs = new double[Bins]; var gs = new double[Bins]; var bs = new double[Bins];
            double opaque = 0, colorful = 0, ar = 0, ag = 0, ab = 0;
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            using (var copy = bmp.Clone(rect, PixelFormat.Format32bppArgb))
            {
                var data = copy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var px = new byte[data.Stride * data.Height];
                    Marshal.Copy(data.Scan0, px, 0, px.Length);
                    for (int y = 0; y < data.Height; y++)
                        for (int x = 0; x < data.Width; x++)
                        {
                            int i = y * data.Stride + x * 4;
                            double a = px[i + 3] / 255.0;
                            if (a < 0.4) continue;
                            double b = px[i], g = px[i + 1], r = px[i + 2];
                            opaque += a; ar += r * a; ag += g * a; ab += b * a;
                            double chroma = (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) / 255.0;
                            if (chroma < 0.14) continue;
                            ToHsl(r, g, b, out var h, out _, out _);
                            int bin = (int)(h / (360.0 / Bins)) % Bins;
                            double weight = a * chroma * chroma; // cor viva pesa mais que fundo escuro (o laranja do "Ai" ganha do marrom)
                            w[bin] += weight; rs[bin] += r * weight; gs[bin] += g * weight; bs[bin] += b * weight;
                            colorful += a;
                        }
                }
                finally { copy.UnlockBits(data); }
            }
            var t = new Tone();
            if (opaque <= 0) return t;
            if (colorful / opaque < 0.08)
            {
                double r = ar / opaque, g = ag / opaque, b = ab / opaque;
                ToHsl(r, g, b, out _, out _, out var l);
                t.Hex = "#" + ((int)r).ToString("x2") + ((int)g).ToString("x2") + ((int)b).ToString("x2");
                t.Lum = l;
                return t;
            }
            int best = 0; double bestW = -1;
            for (int i = 0; i < Bins; i++)
            {
                double sum = w[(i + Bins - 1) % Bins] + w[i] + w[(i + 1) % Bins];
                if (sum > bestW) { bestW = sum; best = i; }
            }
            double sw = 0, sr = 0, sg = 0, sb = 0;
            for (int k = -1; k <= 1; k++) { int i = (best + k + Bins) % Bins; sw += w[i]; sr += rs[i]; sg += gs[i]; sb += bs[i]; }
            double cr = sr / sw, cg = sg / sw, cb = sb / sw;
            ToHsl(cr, cg, cb, out var hue, out var sat, out var lum);
            t.Hex = "#" + ((int)cr).ToString("x2") + ((int)cg).ToString("x2") + ((int)cb).ToString("x2");
            t.Hue = hue; t.Sat = sat; t.Lum = lum; t.Neutral = false;
            return t;
        }

        static string Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        // ---------- Tipo do item e fornecedor ----------
        static readonly HashSet<string> Runnable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".exe", ".bat", ".cmd", ".com", ".msc", ".ps1", ".jar", ".vbs", ".appref-ms", ".application", ".lnk" };

        static readonly string[] VendorNoise =
            { "inc", "incorporated", "corporation", "corp", "llc", "ltd", "limited", "gmbh", "co", "company", "sa", "srl", "ag", "bv", "ab", "oy", "pty", "plc",
              "systems", "software", "technologies", "technology", "international", "the", "team", "foundation", "games", "entertainment", "studios", "studio", "labs" };

        /// <summary>"Adobe Systems Incorporated" e "Adobe Inc." viram a mesma chave ("adobe").</summary>
        static string VendorKey(string vendor)
        {
            if (string.IsNullOrWhiteSpace(vendor)) return null;
            var words = new string(vendor.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => !VendorNoise.Contains(x)).ToList();
            return words.Count == 0 ? null : words[0];
        }

        static string UrlOf(string file)
        {
            try
            {
                foreach (var line in File.ReadLines(file).Take(40))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
            }
            catch { }
            return null;
        }

        static string UrlVendor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var i = url.IndexOf(':');
            var scheme = i > 0 ? url.Substring(0, i).ToLowerInvariant() : "";
            switch (scheme)
            {
                case "steam": return "Steam";
                case "com.epicgames.launcher": return "Epic Games";
                case "uplay": return "Ubisoft";
                case "origin": case "origin2": case "link2ea": return "Electronic Arts";
                case "battlenet": return "Blizzard";
                case "riotclient": return "Riot Games";
                case "http":
                case "https":
                    try { var host = new Uri(url).Host; var parts = host.Split('.'); return parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : host; } catch { return null; }
                default: return null;
            }
        }

        sealed class Kind { public string Name = "file", Target, Vendor; public bool IsDir; }

        static Kind Classify(Raw r)
        {
            var k = new Kind();
            if (string.IsNullOrEmpty(r.Path)) { k.Name = "system"; return k; } // Lixeira, Este Computador...
            if (Directory.Exists(r.Path)) { k.Name = "folder"; k.IsDir = true; return k; }
            var ext = Path.GetExtension(r.Path);
            if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = Rage.Care.ShortcutsScanner.Target(r.Path);
                k.Target = target;
                if (target == null) { k.Name = "app"; return k; } // app da Store, atalho de instalador ou de shell
                if (Directory.Exists(target)) { k.Name = "shortcut"; return k; }
                if (Runnable.Contains(Path.GetExtension(target))) { k.Name = "app"; k.Vendor = ToolsUtil.Publisher(target); return k; }
                k.Name = "shortcut"; // atalho para um documento
                return k;
            }
            if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) { k.Name = "app"; k.Target = UrlOf(r.Path); k.Vendor = UrlVendor(k.Target); return k; }
            if (ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)) { k.Name = "app"; return k; }
            return k;
        }

        // ---------- Backup do arranjo ----------
        public sealed class SavedIcon { public string Id { get; set; } public string Name { get; set; } public int X { get; set; } public int Y { get; set; } }
        public sealed class SavedLayout
        {
            public DateTime At { get; set; }
            public bool AutoArrange { get; set; }
            public List<SavedIcon> Items { get; set; } = new List<SavedIcon>();
        }

        static string BackupDir => Paths.Ensure(Path.Combine(Paths.Backups, "area-de-trabalho"));

        static string LastBackupFile()
        {
            try { return Directory.GetFiles(BackupDir, "arranjo-*.json").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(); }
            catch { return null; }
        }

        static object BackupInfo()
        {
            var f = LastBackupFile();
            if (f == null) return null;
            var b = Json.Load<SavedLayout>(f, null);
            return b == null ? null : (object)new { at = b.At, count = b.Items.Count, file = f };
        }

        // ---------- RPC ----------
        [Rpc("desktop.scan")]
        public static object Scan() => OnSta(() =>
        {
            var view = DesktopView();
            var raw = ReadItems(view);
            var icons = SystemIcons();
            try
            {
                var geo = ReadGeometry(view, raw);
                var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var items = new List<object>();
                foreach (var r in raw)
                {
                    var kind = Classify(r);
                    Tone tone = new Tone(); string icon = null;
                    using (var bmp = IconOf(r.Pidl, icons))
                        if (bmp != null) { try { tone = Dominant(bmp); icon = Png(bmp); } catch { } }
                    double? size = null; DateTime? modified = null; string ext = null;
                    if (r.Path != null)
                    {
                        try
                        {
                            if (kind.IsDir) { var di = new DirectoryInfo(r.Path); modified = di.LastWriteTime; var s = ToolsUtil.FolderSize(r.Path, sw, 4000); if (s.HasValue) size = s.Value; }
                            else { var fi = new FileInfo(r.Path); modified = fi.LastWriteTime; size = fi.Length; ext = fi.Extension.TrimStart('.').ToLowerInvariant(); }
                        }
                        catch { }
                    }
                    items.Add(new
                    {
                        id = r.Id, name = r.Name, path = r.Path, kind = kind.Name, isDir = kind.IsDir, ext, target = kind.Target,
                        vendor = kind.Vendor, vendorKey = VendorKey(kind.Vendor),
                        color = tone.Hex, hue = Math.Round(tone.Hue, 1), sat = Math.Round(tone.Sat, 3), lum = Math.Round(tone.Lum, 3), neutral = tone.Neutral, icon,
                        x = r.X, y = r.Y,
                        col = Math.Round((r.X - geo.BaseX) / (double)geo.CellW, 2), row = Math.Round((r.Y - geo.BaseY) / (double)geo.CellH, 2),
                        size, modified,
                        common = r.Path != null && ToolsUtil.IsUnder(r.Path, commonDesktop),
                    });
                }
                return (object)new
                {
                    desktop = userDesktop,
                    grid = new { cols = geo.Cols, rows = geo.Rows, cellW = geo.CellW, cellH = geo.CellH, iconSize = geo.IconSize, autoArrange = geo.AutoArrange, snapToGrid = geo.SnapToGrid },
                    items,
                    backup = BackupInfo(),
                    admin = Program.IsAdmin,
                };
            }
            finally { Free(raw); if (icons != IntPtr.Zero) Marshal.Release(icons); }
        });

        public sealed class Placement { public string Id { get; set; } public int Col { get; set; } public int Row { get; set; } }

        static int Position(IFolderView2 view, List<Raw> raw, Func<Raw, POINT?> where)
        {
            int moved = 0;
            foreach (var r in raw)
            {
                var p = where(r);
                if (!p.HasValue) continue;
                var pt = p.Value; var pidl = r.Pidl;
                if (view.SelectAndPositionItems(1, ref pidl, ref pt, SVSI_POSITIONITEM) == 0) moved++;
            }
            return moved;
        }

        [Rpc("desktop.apply")]
        public static object Apply(List<Placement> items)
        {
            if (items == null || items.Count == 0) throw new RpcException("Nada para organizar.");
            return OnSta(() =>
            {
                var view = DesktopView();
                var raw = ReadItems(view);
                try
                {
                    var geo = ReadGeometry(view, raw);
                    // 1) Guarda o arranjo atual para poder desfazer
                    var backup = new SavedLayout { At = DateTime.Now, AutoArrange = geo.AutoArrange, Items = raw.Select(r => new SavedIcon { Id = r.Id, Name = r.Name, X = r.X, Y = r.Y }).ToList() };
                    var file = Path.Combine(BackupDir, "arranjo-" + backup.At.ToString("yyyy-MM-dd_HH-mm-ss") + ".json");
                    Json.Save(file, backup);
                    // 2) Com a organização automática ligada o Windows desfaz qualquer posição
                    if (geo.AutoArrange) view.SetCurrentFolderFlags(FWF_AUTOARRANGE, 0);
                    // 3) Posiciona; a segunda passada acerta quem foi empurrado por um ícone que ainda estava no caminho
                    var want = new Dictionary<string, Placement>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in items)
                        if (p?.Id != null && p.Col >= 0 && p.Row >= 0 && p.Col < geo.Cols && p.Row < geo.Rows) want[p.Id] = p;
                    Func<Raw, POINT?> target = r => want.TryGetValue(r.Id, out var p) ? new POINT { X = geo.BaseX + p.Col * geo.CellW, Y = geo.BaseY + p.Row * geo.CellH } : (POINT?)null;
                    int moved = Position(view, raw, target);
                    Position(view, raw, r =>
                    {
                        var t = target(r);
                        if (!t.HasValue || view.GetItemPosition(r.Pidl, out var now) != 0) return null;
                        return Math.Abs(now.X - t.Value.X) > geo.CellW / 2 || Math.Abs(now.Y - t.Value.Y) > geo.CellH / 2 ? t : null;
                    });
                    Activity.Add("fix", "grid_view", "Área de trabalho organizada", moved + (moved == 1 ? " ícone" : " ícones") + " reposicionados");
                    return (object)new { moved, total = raw.Count, autoArrangeWasOn = geo.AutoArrange, backup = BackupInfo() };
                }
                finally { Free(raw); }
            });
        }

        [Rpc("desktop.restore")]
        public static object Restore()
        {
            var file = LastBackupFile();
            var saved = file == null ? null : Json.Load<SavedLayout>(file, null);
            if (saved == null || saved.Items.Count == 0) throw new RpcException("Não há arranjo anterior para restaurar.");
            return OnSta(() =>
            {
                var view = DesktopView();
                var raw = ReadItems(view);
                try
                {
                    var old = saved.Items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    Func<Raw, POINT?> target = r => old.TryGetValue(r.Id, out var s) ? new POINT { X = s.X, Y = s.Y } : (POINT?)null;
                    int moved = Position(view, raw, target);
                    Position(view, raw, r =>
                    {
                        var t = target(r);
                        if (!t.HasValue || view.GetItemPosition(r.Pidl, out var now) != 0) return null;
                        return now.X != t.Value.X || now.Y != t.Value.Y ? t : null;
                    });
                    if (saved.AutoArrange) view.SetCurrentFolderFlags(FWF_AUTOARRANGE, FWF_AUTOARRANGE);
                    // O arranjo restaurado sai da fila: "Desfazer" não fica oferecendo o mesmo de novo
                    try { File.Move(file, Path.Combine(Path.GetDirectoryName(file), "restaurado-" + Path.GetFileName(file).Substring("arranjo-".Length))); } catch { }
                    Activity.Add("fix", "undo", "Arranjo da área de trabalho restaurado", moved + (moved == 1 ? " ícone" : " ícones"));
                    return (object)new { moved, backup = BackupInfo() };
                }
                finally { Free(raw); }
            });
        }
    }
}
