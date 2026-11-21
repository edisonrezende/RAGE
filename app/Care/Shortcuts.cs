using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Categoria "shortcuts" — atalhos .lnk cujo destino local não existe mais. A correção move o atalho para a pasta de backups.</summary>
    public sealed class ShortcutsScanner : ICareScanner
    {
        public string Category => "shortcuts";
        public string Title => "Atalhos inválidos";
        public string Description => "Atalhos que apontam para nada";
        public string Icon => "link_off";
        public int Order => 30;
        public bool DefaultOn => true;

        // ---------- COM: IShellLink ----------
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("45e2b4ae-b1c3-11d0-b92f-00a0c90312e1")]
        interface IShellLinkDataList
        {
            [PreserveSig] int AddDataBlock(IntPtr pDataBlock);
            [PreserveSig] int CopyDataBlock(uint dwSig, out IntPtr ppDataBlock);
            [PreserveSig] int RemoveDataBlock(uint dwSig);
            void GetFlags(out uint pdwFlags);
            void SetFlags(uint dwFlags);
        }

        const uint SLDF_HAS_DARWINID = 0x1000;  // atalho "anunciado" do Windows Installer (sem caminho real)
        const FileAttributes CloudMask = (FileAttributes)0x00400000 | (FileAttributes)0x00040000 | FileAttributes.Offline; // arquivos só na nuvem (OneDrive)

        /// <summary>Destino local do atalho, ou null para atalhos de shell, URLs, anunciados (MSI) ou ilegíveis.</summary>
        internal static string Target(string lnk)
        {
            object o = null;
            try
            {
                o = new CShellLink();
                ((IPersistFile)o).Load(lnk, 0 /*STGM_READ*/);
                if (o is IShellLinkDataList dl)
                {
                    try { dl.GetFlags(out var flags); if ((flags & SLDF_HAS_DARWINID) != 0) return null; } catch { }
                }
                var sb = new StringBuilder(1024);
                ((IShellLinkW)o).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                var p = sb.ToString().Trim();
                if (p.Length < 3 || p.StartsWith("::", StringComparison.Ordinal) || p.IndexOf("://", StringComparison.Ordinal) >= 0) return null;
                return p;
            }
            catch { return null; }
            finally { if (o != null) try { Marshal.FinalReleaseComObject(o); } catch { } }
        }

        sealed class Location { public string Group; public string Dir; public bool Recursive; public bool Common; }

        static List<Location> Locations()
        {
            string F(Environment.SpecialFolder f) { try { return Environment.GetFolderPath(f); } catch { return null; } }
            var l = new List<Location>
            {
                new Location { Group = "Área de Trabalho", Dir = F(Environment.SpecialFolder.DesktopDirectory), Recursive = false },
                new Location { Group = "Área de Trabalho (todos os usuários)", Dir = F(Environment.SpecialFolder.CommonDesktopDirectory), Recursive = false, Common = true },
                new Location { Group = "Menu Iniciar", Dir = F(Environment.SpecialFolder.StartMenu), Recursive = true },
                new Location { Group = "Menu Iniciar (todos os usuários)", Dir = F(Environment.SpecialFolder.CommonStartMenu), Recursive = true, Common = true },
                new Location { Group = "Barra de tarefas e Inicialização rápida", Dir = Path.Combine(Fs.AppData, @"Microsoft\Internet Explorer\Quick Launch"), Recursive = true },
            };
            return l.Where(x => !string.IsNullOrEmpty(x.Dir) && Fs.DirExists(x.Dir)).ToList();
        }

        sealed class Found { public string Lnk; public string Target; public Location Loc; }

        public List<CareItem> Scan(RpcContext ctx)
        {
            var items = new List<CareItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern = Fs.Wild("*.lnk");
            int checkedLinks = 0, localTargets = 0;
            foreach (var loc in Locations())
            {
                CareUtil.Item(ctx, Category, loc.Group);
                var links = new List<Fs.Entry>();
                Fs.Walk(loc.Dir, new Fs.WalkOptions { Recursive = loc.Recursive, Pattern = pattern }, e => links.Add(e), ctx.Token);
                foreach (var e in links)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    if ((e.Attr & CloudMask) != 0 || !seen.Add(e.Path)) continue;
                    checkedLinks++;
                    var target = Target(e.Path);
                    if (target != null) localTargets++;
                    if (target == null || !Fs.IsMissing(target)) continue;
                    items.Add(new CareItem
                    {
                        Id = "shortcuts." + CareUtil.Hash(e.Path), Category = Category, Group = loc.Group,
                        Title = Path.GetFileNameWithoutExtension(e.Path), Detail = target, Count = 1,
                        Paths = new List<string> { e.Path },
                        Note = loc.Common && !Program.IsAdmin ? "Atalho de todos os usuários: requer administrador para remover." : null,
                        Tag = new Found { Lnk = e.Path, Target = target, Loc = loc },
                    });
                }
            }
            Log.Info("Atalhos: " + checkedLinks + " verificados, " + localTargets + " com destino em arquivo, " + items.Count + " inválidos");
            return items;
        }

        public sealed class ManifestEntry { public string Original { get; set; } public string Backup { get; set; } public string Target { get; set; } }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            var folder = Path.Combine(Paths.Backups, "atalhos", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            var manifest = new List<ManifestEntry>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                var f = it.Tag as Found;
                if (f == null) { results.Add(new FixResult { Id = it.Id, Message = "Faça uma nova verificação." }); continue; }
                try
                {
                    if (!File.Exists(f.Lnk)) { results.Add(new FixResult { Id = it.Id, Ok = true, Message = "O atalho já não existe." }); continue; }
                    var t = Target(f.Lnk);
                    if (t == null || !Fs.IsMissing(t)) { results.Add(new FixResult { Id = it.Id, Message = "O destino voltou a existir; o atalho foi mantido." }); continue; }
                    if (f.Loc.Common && !Program.IsAdmin) { results.Add(new FixResult { Id = it.Id, Message = CareUtil.AdminMsg }); continue; }

                    // Mantém a estrutura: <backup>\C\Users\...\Desktop\Atalho.lnk
                    var rel = f.Lnk.Substring(0, 1) + f.Lnk.Substring(2);
                    var dest = Path.Combine(folder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (File.Exists(dest)) dest = Path.Combine(Path.GetDirectoryName(dest), Path.GetFileNameWithoutExtension(dest) + " (" + DateTime.Now.Ticks + ").lnk");
                    File.Move(f.Lnk, dest);
                    manifest.Add(new ManifestEntry { Original = f.Lnk, Backup = dest, Target = f.Target });
                    // Pasta do Menu Iniciar que ficou vazia sai junto (nunca a raiz)
                    if (f.Loc.Recursive)
                    {
                        var parent = Path.GetDirectoryName(f.Lnk);
                        if (parent != null && parent.Length > f.Loc.Dir.TrimEnd('\\').Length && Fs.DirExists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                            Fs.RemoveDir(parent);
                    }
                    results.Add(new FixResult { Id = it.Id, Ok = true, Message = "Movido para os backups", Backup = folder });
                }
                catch (UnauthorizedAccessException) { results.Add(new FixResult { Id = it.Id, Message = Program.IsAdmin ? "Acesso negado ao atalho." : CareUtil.AdminMsg }); }
                catch (Exception ex) { results.Add(new FixResult { Id = it.Id, Message = "Não foi possível mover o atalho: " + ex.Message }); }
            }
            if (manifest.Count > 0)
            {
                try { Json.Save(Path.Combine(folder, "manifest.json"), manifest); }
                catch (Exception ex) { Log.Warn("Manifesto de atalhos: " + ex.Message); }
            }
            return results;
        }
    }
}
