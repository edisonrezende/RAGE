using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Arquivos grandes, duplicados, pastas vazias, Lixeira e destruição segura.</summary>
    public static class Files
    {
        static string ValidRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new RpcException("Escolha uma pasta.");
            string full;
            try { full = Path.GetFullPath(ToolsUtil.Expand(root.Trim())); } catch { throw new RpcException("Pasta inválida."); }
            if (!Directory.Exists(full)) throw new RpcException("Pasta não encontrada: " + full);
            return full;
        }

        /// <summary>Percorre arquivos sem seguir junções/links simbólicos.</summary>
        static IEnumerable<FileInfo> Walk(string root, bool skipSystem, CancellationToken ct, Action<string> onDir)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                onDir?.Invoke(d.FullName);
                List<FileSystemInfo> entries;
                try { entries = d.EnumerateFileSystemInfos().ToList(); } catch { continue; }
                foreach (var e in entries)
                {
                    FileAttributes a;
                    try { a = e.Attributes; } catch { continue; }
                    if ((a & FileAttributes.Directory) != 0)
                    {
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;
                        var name = e.Name;
                        if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                        if (skipSystem && SafePaths.SkipWhenScanning(e.FullName)) continue;
                        stack.Push((DirectoryInfo)e);
                    }
                    else if ((a & FileAttributes.ReparsePoint) == 0) yield return (FileInfo)e;
                }
            }
        }

        // ---------- Grandes ----------
        public static List<object> Large(string root, double? minSize, int? limit, RpcContext ctx)
        {
            root = ValidRoot(root);
            var min = (long)Math.Max(1, minSize ?? 100L * 1024 * 1024);
            var lim = Math.Max(1, Math.Min(limit ?? 200, 5000));
            var heap = new SortedSet<(long size, string path, DateTime mod, string ext)>(Comparer<(long, string, DateTime, string)>.Create((x, y) =>
            {
                var c = x.Item1.CompareTo(y.Item1);
                return c != 0 ? c : string.Compare(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
            }));
            long scanned = 0;
            var sw = Stopwatch.StartNew();
            string current = root;
            foreach (var f in Walk(root, false, ctx.Token, d => current = d))
            {
                scanned++;
                if (sw.ElapsedMilliseconds > 250) { sw.Restart(); ctx.Progress(new { scanned, current }); }
                long len;
                try { len = f.Length; } catch { continue; }
                if (len < min) continue;
                if (heap.Count >= lim && len <= heap.Min.size) continue;
                DateTime mod; try { mod = f.LastWriteTime; } catch { mod = DateTime.MinValue; }
                heap.Add((len, f.FullName, mod, f.Extension.TrimStart('.').ToLowerInvariant()));
                if (heap.Count > lim) heap.Remove(heap.Min);
            }
            ctx.Progress(new { scanned, current = (string)null });
            return heap.Reverse().Select(x => (object)new { path = x.path, size = (double)x.size, modified = x.mod, ext = x.ext }).ToList();
        }

        // ---------- Duplicados ----------
        static string HashFile(string path, long maxBytes, CancellationToken ct)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                var buf = new byte[1 << 20];
                long left = maxBytes <= 0 ? long.MaxValue : maxBytes;
                int n;
                while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    md5.TransformBlock(buf, 0, n, null, 0);
                    left -= n;
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static List<object> Duplicates(string root, double? minSize, RpcContext ctx)
        {
            root = ValidRoot(root);
            var min = (long)Math.Max(1, minSize ?? 1024 * 1024);
            var bySize = new Dictionary<long, List<FileInfo>>();
            long scanned = 0;
            var sw = Stopwatch.StartNew();
            string current = root;
            foreach (var f in Walk(root, true, ctx.Token, d => current = d))
            {
                scanned++;
                if (sw.ElapsedMilliseconds > 250) { sw.Restart(); ctx.Progress(new { phase = "scan", scanned, current }); }
                long len; try { len = f.Length; } catch { continue; }
                if (len < min) continue;
                if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new List<FileInfo>();
                l.Add(f);
            }
            var candidates = bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).ToList();
            int total = candidates.Sum(c => c.Value.Count), hashed = 0;
            var groups = new List<(string hash, long size, List<FileInfo> files)>();
            foreach (var c in candidates)
            {
                ctx.Token.ThrowIfCancellationRequested();
                // 1) hash parcial (primeiros 64 KB)
                var partial = new Dictionary<string, List<FileInfo>>();
                foreach (var f in c.Value)
                {
                    hashed++;
                    if (sw.ElapsedMilliseconds > 250) { sw.Restart(); ctx.Progress(new { phase = "hash", scanned, hashed, total, current = f.FullName }); }
                    try
                    {
                        var h = HashFile(f.FullName, 64 * 1024, ctx.Token);
                        if (!partial.TryGetValue(h, out var pl)) partial[h] = pl = new List<FileInfo>();
                        pl.Add(f);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                // 2) hash completo dos que coincidiram
                foreach (var pg in partial.Values.Where(v => v.Count > 1))
                {
                    var full = new Dictionary<string, List<FileInfo>>();
                    foreach (var f in pg)
                    {
                        if (sw.ElapsedMilliseconds > 250) { sw.Restart(); ctx.Progress(new { phase = "hash", scanned, hashed, total, current = f.FullName }); }
                        try
                        {
                            var h = c.Key <= 64 * 1024 ? HashFile(f.FullName, 64 * 1024, ctx.Token) : HashFile(f.FullName, 0, ctx.Token);
                            if (!full.TryGetValue(h, out var fl)) full[h] = fl = new List<FileInfo>();
                            fl.Add(f);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                    foreach (var kv in full.Where(v => v.Value.Count > 1)) groups.Add((kv.Key, c.Key, kv.Value));
                }
            }
            ctx.Progress(new { phase = "done", scanned, hashed, total });
            return groups.OrderByDescending(g => g.size * (g.files.Count - 1)).Take(2000).Select(g => (object)new
            {
                hash = g.hash,
                size = (double)g.size,
                files = g.files.Select(f => new { path = f.FullName, modified = SafeMod(f) }).OrderBy(f => f.modified).ToList(),
            }).ToList();
        }

        static DateTime SafeMod(FileInfo f) { try { return f.LastWriteTime; } catch { return DateTime.MinValue; } }

        // ---------- Pastas vazias ----------
        public static List<string> Empty(string root, RpcContext ctx)
        {
            root = ValidRoot(root);
            var res = new List<string>();
            var sw = Stopwatch.StartNew();
            long scanned = 0;
            // Retorna true se a pasta (recursivamente) não tem arquivos
            bool Visit(DirectoryInfo d, bool isRoot)
            {
                ctx.Token.ThrowIfCancellationRequested();
                scanned++;
                if (sw.ElapsedMilliseconds > 250) { sw.Restart(); ctx.Progress(new { scanned, current = d.FullName }); }
                List<FileSystemInfo> entries;
                try { entries = d.EnumerateFileSystemInfos().ToList(); } catch { return false; }
                bool empty = true;
                var emptyChildren = new List<string>();
                foreach (var e in entries)
                {
                    FileAttributes a; try { a = e.Attributes; } catch { empty = false; continue; }
                    if ((a & FileAttributes.Directory) == 0) { empty = false; continue; }
                    if ((a & FileAttributes.ReparsePoint) != 0 || SafePaths.SkipWhenScanning(e.FullName)) { empty = false; continue; }
                    if (Visit((DirectoryInfo)e, false)) emptyChildren.Add(e.FullName);
                    else empty = false;
                }
                if (empty && !isRoot && SafePaths.Check(d.FullName) == null) return true;
                // Esta pasta não é vazia (ou é a raiz/protegida): reporta as filhas vazias de nível mais alto
                foreach (var c in emptyChildren) res.Add(c);
                return false;
            }
            if (SafePaths.SkipWhenScanning(root)) throw new RpcException("Esta pasta é do sistema e não pode ser analisada.");
            Visit(new DirectoryInfo(root), true);
            return res.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ---------- Lixeira ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>Envia para a Lixeira (sem caixas de diálogo de erro). Retorna mensagem de erro ou null.</summary>
        public static string ToRecycleBin(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING),
            };
            var r = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) return "Operação cancelada.";
            if (r != 0) return r == 0x78 || r == 5 ? "Acesso negado (arquivo em uso ou sem permissão)." : r == 0x20 ? "Arquivo em uso por outro programa." : "Falha ao enviar para a Lixeira (código 0x" + r.ToString("X") + ").";
            if (File.Exists(path) || Directory.Exists(path)) return "O item ainda existe (talvez esteja em uso).";
            return null;
        }

        public static object RecycleMany(List<string> paths, string activityTitle)
        {
            if (paths == null || paths.Count == 0) throw new RpcException("Nenhum item selecionado.");
            int ok = 0, failed = 0; double freed = 0;
            var errors = new List<object>();
            foreach (var raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string p;
                try { p = Path.GetFullPath(raw.Trim()); } catch { failed++; errors.Add(new { path = raw, message = "Caminho inválido." }); continue; }
                var why = SafePaths.Check(p);
                if (why != null) { failed++; errors.Add(new { path = p, message = why }); continue; }
                if (!File.Exists(p) && !Directory.Exists(p)) { failed++; errors.Add(new { path = p, message = "Não encontrado." }); continue; }
                double size = 0;
                try { size = File.Exists(p) ? new FileInfo(p).Length : (ToolsUtil.FolderSize(p) ?? 0); } catch { }
                var err = ToRecycleBin(p);
                if (err == null) { ok++; freed += size; }
                else { failed++; errors.Add(new { path = p, message = err }); }
            }
            if (ok > 0) Activity.Add("clean", "delete", activityTitle ?? "Itens enviados para a Lixeira", ok + (ok == 1 ? " item" : " itens"), freed > 0 ? Fmt.Bytes(freed) : null);
            return new { ok, failed, freed, errors };
        }

        // ---------- Destruição segura ----------
        public static object Shred(List<string> paths, int? passes, RpcContext ctx)
        {
            if (paths == null || paths.Count == 0) throw new RpcException("Nenhum arquivo selecionado.");
            var np = Math.Max(1, Math.Min(passes ?? 1, 7));
            // Monta a lista de arquivos (pastas são expandidas) e valida tudo antes de começar
            var files = new List<string>();
            var dirs = new List<string>();
            var errors = new List<object>();
            int failed = 0;
            foreach (var raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string p;
                try { p = Path.GetFullPath(raw.Trim()); } catch { failed++; errors.Add(new { path = raw, message = "Caminho inválido." }); continue; }
                var why = SafePaths.Check(p);
                if (why != null) { failed++; errors.Add(new { path = p, message = why }); continue; }
                if (File.Exists(p)) files.Add(p);
                else if (Directory.Exists(p))
                {
                    if (SafePaths.SkipWhenScanning(p)) { failed++; errors.Add(new { path = p, message = "Pasta protegida do sistema." }); continue; }
                    var di = new DirectoryInfo(p);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { failed++; errors.Add(new { path = p, message = "Links e junções não são destruídos." }); continue; }
                    files.AddRange(Walk(p, true, ctx.Token, null).Select(f => f.FullName));
                    dirs.Add(p);
                }
                else { failed++; errors.Add(new { path = p, message = "Não encontrado." }); }
            }
            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            long totalBytes = 0;
            foreach (var f in files) { try { totalBytes += new FileInfo(f).Length; } catch { } }
            long doneBytes = 0;
            int ok = 0, index = 0;
            var buf = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
            using (var rng = RandomNumberGenerator.Create())
            {
                foreach (var f in files)
                {
                    if (ctx.Token.IsCancellationRequested) break; // nunca para no meio de um arquivo
                    index++;
                    ctx.Progress(new { current = f, index, total = files.Count, pct = totalBytes > 0 ? Math.Round(doneBytes * 100.0 / totalBytes, 1) : index * 100.0 / files.Count });
                    try
                    {
                        long len = new FileInfo(f).Length;
                        File.SetAttributes(f, FileAttributes.Normal);
                        using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                        {
                            for (int pass = 0; pass < np; pass++)
                            {
                                fs.Position = 0;
                                long left = len;
                                while (left > 0)
                                {
                                    var n = (int)Math.Min(buf.Length, left);
                                    rng.GetBytes(buf);
                                    fs.Write(buf, 0, n);
                                    left -= n;
                                    if (sw.ElapsedMilliseconds > 300)
                                    {
                                        sw.Restart();
                                        var cur = doneBytes + ((long)pass * len + (len - left)) / np;
                                        ctx.Progress(new { current = f, index, total = files.Count, pass = pass + 1, passes = np, pct = totalBytes > 0 ? Math.Round(Math.Min(100, cur * 100.0 / totalBytes), 1) : 0 });
                                    }
                                }
                                fs.Flush(true);
                            }
                            fs.SetLength(0);
                        }
                        // Renomeia algumas vezes para apagar o nome original da MFT
                        var dir = Path.GetDirectoryName(f);
                        var cur2 = f;
                        for (int i = 0; i < 3; i++)
                        {
                            var nn = Path.Combine(dir, Path.GetRandomFileName().Replace(".", "") + ".tmp");
                            File.Move(cur2, nn);
                            cur2 = nn;
                        }
                        File.SetCreationTime(cur2, new DateTime(2000, 1, 1)); File.SetLastWriteTime(cur2, new DateTime(2000, 1, 1)); File.SetLastAccessTime(cur2, new DateTime(2000, 1, 1));
                        File.Delete(cur2);
                        ok++;
                        doneBytes += len;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add(new { path = f, message = ex is UnauthorizedAccessException ? "Acesso negado." : ex is IOException ? "Arquivo em uso por outro programa." : ex.Message });
                    }
                }
            }
            // Remove as pastas que ficaram vazias (da mais profunda para a mais rasa)
            foreach (var d in dirs)
            {
                try
                {
                    foreach (var sub in Directory.GetDirectories(d, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                        try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
            ctx.Progress(new { current = (string)null, index, total = files.Count, pct = 100 });
            if (ok > 0) Activity.Add("clean", "local_fire_department", "Arquivos destruídos com segurança", ok + (ok == 1 ? " arquivo" : " arquivos") + " · " + np + (np == 1 ? " passagem" : " passagens"), Fmt.Bytes(doneBytes));
            return new { ok, failed, errors, canceled = ctx.Token.IsCancellationRequested };
        }

        // ---------- RPC ----------
        [Rpc("files.large")]
        public static object LargeRpc(string root, double? minSize, int? limit, RpcContext ctx) => Large(root, minSize, limit, ctx);

        [Rpc("files.duplicates")]
        public static object DuplicatesRpc(string root, double? minSize, RpcContext ctx) => Duplicates(root, minSize, ctx);

        [Rpc("files.empty")]
        public static object EmptyRpc(string root, RpcContext ctx) => Empty(root, ctx);

        [Rpc("files.recycle")]
        public static object RecycleRpc(List<string> paths) => RecycleMany(paths, "Itens enviados para a Lixeira");

        [Rpc("files.shred")]
        public static object ShredRpc(List<string> paths, int? passes, RpcContext ctx) => Shred(paths, passes, ctx);
    }
}
