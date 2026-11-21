using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Central de resgate: pontos de restauração e backups criados pelo RAGE!.</summary>
    public static class Rescue
    {
        public sealed class MovedFile { public string Original { get; set; } public string Backup { get; set; } }
        const string ManifestName = "manifest.json";

        /// <summary>Grava a lista de arquivos movidos para um backup de atalhos (usada na restauração).</summary>
        public static void WriteManifest(string dir, List<MovedFile> files)
        {
            try
            {
                var f = Path.Combine(dir, ManifestName);
                var all = Json.Load(f, new List<MovedFile>());
                all.AddRange(files);
                Json.Save(f, all);
            }
            catch (Exception ex) { Log.Warn("Manifesto de backup: " + ex.Message); }
        }

        static string TypeName(int t)
        {
            switch (t)
            {
                case 0: return "Instalação de programa";
                case 1: return "Desinstalação de programa";
                case 6: return "Restauração";
                case 7: return "Ponto de verificação";
                case 10: return "Instalação de driver";
                case 12: return "Alteração de configurações";
                case 13: return "Operação cancelada";
                case 14: return "Backup";
                case 16: return "Manual";
                case 18: return "Windows Update";
                default: return "Sistema";
            }
        }

        static List<object> Points()
        {
            var list = new List<(int seq, string description, DateTime? date, string type)>();
            if (!Program.IsAdmin) return new List<object>();
            try
            {
                using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                            list.Add((Convert.ToInt32(o["SequenceNumber"] ?? 0), o["Description"] as string, ToolsUtil.WmiDate(o["CreationTime"]), TypeName(Convert.ToInt32(o["RestorePointType"] ?? -1))));
            }
            catch (Exception ex) { Log.Warn("Pontos de restauração: " + ex.Message); }
            return list.OrderByDescending(x => x.seq).Select(x => (object)new { x.seq, x.description, x.date, x.type }).ToList();
        }

        static List<object> Backups()
        {
            var list = new List<(string file, string kind, DateTime date, double size)>();
            var root = Paths.Backups;
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
                try { var fi = new FileInfo(f); list.Add((f, f.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ? "registro" : "outro", fi.LastWriteTime, fi.Length)); } catch { }
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d).ToLowerInvariant();
                if (name == "registro")
                {
                    foreach (var f in Directory.GetFiles(d, "*.reg", SearchOption.AllDirectories))
                        try { var fi = new FileInfo(f); list.Add((f, "registro", fi.LastWriteTime, fi.Length)); } catch { }
                }
                else if (name == "atalhos" || name == "drivers")
                {
                    foreach (var sub in Directory.GetDirectories(d))
                        try { list.Add((sub, name, Directory.GetLastWriteTime(sub), ToolsUtil.FolderSize(sub) ?? 0)); } catch { }
                    foreach (var f in Directory.GetFiles(d))
                        try { var fi = new FileInfo(f); list.Add((f, f.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) ? "registro" : name, fi.LastWriteTime, fi.Length)); } catch { }
                }
                else
                {
                    try { list.Add((d, "outro", Directory.GetLastWriteTime(d), ToolsUtil.FolderSize(d) ?? 0)); } catch { }
                }
            }
            return list.OrderByDescending(x => x.date).Select(x => (object)new { file = x.file, name = Path.GetFileName(x.file), kind = x.kind, date = x.date, size = x.size }).ToList();
        }

        static string InsideBackups(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) throw new RpcException("Backup não informado.");
            string full;
            try { full = Path.GetFullPath(file.Trim()).TrimEnd('\\'); } catch { throw new RpcException("Caminho inválido."); }
            if (!ToolsUtil.IsUnder(full, Paths.Backups) || ToolsUtil.SamePath(full, Paths.Backups)) throw new RpcException("Só é possível usar backups da pasta do RAGE!.");
            foreach (var sub in new[] { "registro", "atalhos", "drivers" })
                if (ToolsUtil.SamePath(full, Path.Combine(Paths.Backups, sub))) throw new RpcException("Escolha um backup específico.");
            if (!File.Exists(full) && !Directory.Exists(full)) throw new RpcException("Backup não encontrado.");
            return full;
        }

        public static object RestoreBackup(string file, RpcContext ctx)
        {
            var full = InsideBackups(file);
            if (File.Exists(full) && full.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
            {
                var text = File.ReadAllText(full);
                if (text.IndexOf("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("[HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase) >= 0)
                    ToolsUtil.RequireAdmin("Restaurar este backup do registro");
                var r = Proc.Run(Proc.SysExe("reg.exe"), "import \"" + full + "\"", 60000);
                var ok = r.ExitCode == 0;
                if (ok) Activity.Add("info", "restore", "Backup do registro restaurado", Path.GetFileName(full));
                return new { ok, message = ok ? "Backup do registro restaurado." : ToolsUtil.LastMeaningful(r.Error) ?? ToolsUtil.LastMeaningful(r.Output) ?? "Falha ao importar o arquivo .reg." };
            }
            if (Directory.Exists(full) && ToolsUtil.IsUnder(full, Path.Combine(Paths.Backups, "drivers")))
            {
                ToolsUtil.RequireAdmin("Reinstalar drivers do backup");
                var r = Proc.Run(Proc.SysExe("pnputil.exe"), "/add-driver \"" + Path.Combine(full, "*.inf") + "\" /subdirs /install", 1800000, Proc.Oem, l => { var c = ToolsUtil.CleanLine(l); if (c != null) ctx.Progress(new { line = c }); }, ctx.Token);
                var ok = r.ExitCode == 0 || r.ExitCode == 3010;
                return new { ok, message = ok ? "Drivers do backup instalados." + (r.ExitCode == 3010 ? " Reinicie o computador." : "") : ToolsUtil.LastMeaningful(r.Output) ?? "Falha no pnputil (código " + r.ExitCode + ")." };
            }
            if (Directory.Exists(full))
            {
                // Atalhos/arquivos movidos: usa o manifesto ou a estrutura espelhada "<letra>\caminho"
                var moves = new List<MovedFile>();
                var manifest = Path.Combine(full, ManifestName);
                if (File.Exists(manifest)) moves = Json.Load(manifest, new List<MovedFile>()).Where(m => m.Backup != null && m.Original != null).ToList();
                else
                {
                    foreach (var f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                    {
                        var rel = f.Substring(full.Length).TrimStart('\\');
                        var i = rel.IndexOf('\\');
                        if (i != 1 || !char.IsLetter(rel[0])) continue;
                        moves.Add(new MovedFile { Backup = f, Original = rel[0] + ":\\" + rel.Substring(2) });
                    }
                }
                if (moves.Count == 0) throw new RpcException("Este backup não tem arquivos para restaurar.");
                int ok = 0, skipped = 0, failed = 0;
                foreach (var m in moves)
                {
                    try
                    {
                        if (!File.Exists(m.Backup)) { skipped++; continue; }
                        if (File.Exists(m.Original)) { skipped++; continue; }
                        var dir = Path.GetDirectoryName(m.Original);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.Move(m.Backup, m.Original);
                        ok++;
                    }
                    catch { failed++; }
                }
                if (ok > 0)
                {
                    Activity.Add("info", "restore", "Backup restaurado", ok + (ok == 1 ? " arquivo devolvido" : " arquivos devolvidos") + " ao lugar original");
                    try
                    {
                        // Remove o backup se ficou vazio
                        if (!Directory.GetFiles(full, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(ManifestName, StringComparison.OrdinalIgnoreCase))) Directory.Delete(full, true);
                    }
                    catch { }
                }
                return new { ok = failed == 0 && ok > 0, restored = ok, skipped, failed, message = ok + " restaurados" + (skipped > 0 ? ", " + skipped + " já existiam" : "") + (failed > 0 ? ", " + failed + " com erro" : "") + "." };
            }
            throw new RpcException("Este tipo de backup não pode ser restaurado automaticamente. Abra a pasta e restaure manualmente.");
        }

        public static object DeleteBackup(string file)
        {
            var full = InsideBackups(file);
            if (File.Exists(full)) File.Delete(full);
            else Directory.Delete(full, true);
            return true;
        }

        public static object CreatePoint(string description)
        {
            ToolsUtil.RequireAdmin("Criar um ponto de restauração");
            var desc = string.IsNullOrWhiteSpace(description) ? "RAGE! - ponto manual" : description.Trim();
            if (desc.Length > 200) desc = desc.Substring(0, 200);
            var ok = RestorePoint.Create(desc, out var err);
            if (!ok) throw new RpcException("Não foi possível criar o ponto de restauração" + (string.IsNullOrEmpty(err) ? "." : ": " + err) + " Verifique se a Proteção do Sistema está ativada.");
            return new { ok = true, message = "Ponto de restauração criado." };
        }

        [Rpc("rescue.list")]
        public static object ListRpc() => new { points = Points(), backups = Backups(), admin = Program.IsAdmin };

        [Rpc("rescue.createPoint")]
        public static object CreatePointRpc(string description) => CreatePoint(description);

        [Rpc("rescue.restoreBackup")]
        public static object RestoreBackupRpc(string file, RpcContext ctx) => RestoreBackup(file, ctx);

        [Rpc("rescue.deleteBackup")]
        public static object DeleteBackupRpc(string file) => DeleteBackup(file);

        [Rpc("rescue.openSystemRestore")]
        public static object OpenSystemRestore()
        {
            Process.Start(new ProcessStartInfo(Proc.SysExe("rstrui.exe")) { UseShellExecute = true });
            return true;
        }
    }
}
