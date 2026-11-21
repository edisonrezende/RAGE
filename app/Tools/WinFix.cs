using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Reparos do Windows: SFC, DISM, rede, caches, Explorer, Windows Update, Store, disco, relógio e impressão.</summary>
    public static class WinFix
    {
        public sealed class Item
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Desc { get; set; }
            public string Icon { get; set; }
            public int Minutes { get; set; }
            public bool NeedsReboot { get; set; }
            public bool Admin { get; set; }
        }

        static readonly List<Item> Items = new List<Item>
        {
            new Item { Id = "sfc", Title = "Verificar arquivos do sistema (SFC)", Desc = "Procura e repara arquivos protegidos do Windows corrompidos ou alterados.", Icon = "verified_user", Minutes = 15, Admin = true },
            new Item { Id = "dism-check", Title = "Checar a imagem do Windows (rápido)", Desc = "DISM /CheckHealth: verifica se a imagem do sistema foi marcada como corrompida.", Icon = "fact_check", Minutes = 1, Admin = true },
            new Item { Id = "dism-scan", Title = "Analisar a imagem do Windows", Desc = "DISM /ScanHealth: análise completa da imagem do sistema em busca de corrupção.", Icon = "search", Minutes = 10, Admin = true },
            new Item { Id = "dism-restore", Title = "Reparar a imagem do Windows", Desc = "DISM /RestoreHealth: repara a imagem do sistema baixando arquivos do Windows Update. Rode antes do SFC se ele não conseguir reparar.", Icon = "healing", Minutes = 20, Admin = true },
            new Item { Id = "component-cleanup", Title = "Limpar componentes antigos do Windows", Desc = "DISM /StartComponentCleanup: remove versões antigas de componentes substituídos por atualizações (libera espaço na pasta WinSxS).", Icon = "cleaning_services", Minutes = 15, Admin = true },
            new Item { Id = "network-reset", Title = "Redefinir a rede", Desc = "Redefine o Winsock e a pilha TCP/IP e limpa o cache DNS. Resolve muitos problemas de conexão. Precisa reiniciar.", Icon = "wifi_off", Minutes = 1, NeedsReboot = true, Admin = true },
            new Item { Id = "dns-flush", Title = "Limpar o cache DNS", Desc = "Apaga os endereços de sites guardados pelo Windows. Ajuda quando um site não abre ou mudou de servidor.", Icon = "dns", Minutes = 1, Admin = false },
            new Item { Id = "icon-cache", Title = "Reconstruir o cache de ícones", Desc = "Corrige ícones em branco ou errados na Área de Trabalho e no Explorador de Arquivos (o Explorer é reiniciado).", Icon = "imagesmode", Minutes = 1, Admin = false },
            new Item { Id = "restart-explorer", Title = "Reiniciar o Explorador do Windows", Desc = "Reinicia a barra de tarefas, o Menu Iniciar e as janelas de pastas quando travam.", Icon = "restart_alt", Minutes = 1, Admin = false },
            new Item { Id = "windows-update-reset", Title = "Redefinir o Windows Update", Desc = "Para os serviços de atualização, renomeia as pastas SoftwareDistribution e catroot2 e religa tudo. Resolve atualizações travadas ou com erro.", Icon = "update", Minutes = 3, Admin = true },
            new Item { Id = "store-reset", Title = "Redefinir a Microsoft Store", Desc = "Limpa o cache da Microsoft Store (wsreset). A Store abre sozinha no final.", Icon = "storefront", Minutes = 2, Admin = false },
            new Item { Id = "chkdsk-scan", Title = "Verificar o disco do sistema", Desc = "chkdsk /scan: verifica o sistema de arquivos da unidade do Windows sem precisar reiniciar.", Icon = "hard_drive", Minutes = 10, Admin = true },
            new Item { Id = "time-sync", Title = "Sincronizar o relógio", Desc = "Acerta a hora do Windows com o servidor de horário da internet.", Icon = "schedule", Minutes = 1, Admin = true },
            new Item { Id = "print-spooler", Title = "Limpar a fila de impressão", Desc = "Cancela documentos travados na fila e reinicia o serviço de impressão.", Icon = "print", Minutes = 1, Admin = true },
        };

        sealed class Ctx
        {
            public RpcContext Rpc;
            public StringBuilder Out = new StringBuilder();
            public void Line(string l)
            {
                var c = ToolsUtil.CleanLine(l);
                if (c == null) return;
                lock (Out) Out.AppendLine(c);
                Rpc.Progress(new { line = c });
            }
            public ProcResult Run(string exe, string args, int timeoutMin, Encoding enc = null)
            {
                Line("> " + Path.GetFileName(exe) + " " + args);
                return Proc.Run(exe, args, timeoutMin * 60000, enc ?? Proc.Oem, Line, Rpc.Token);
            }
        }

        public static object Run(string id, RpcContext rpc)
        {
            var item = Items.FirstOrDefault(i => i.Id == id) ?? throw new RpcException("Reparo desconhecido.");
            if (item.Admin) ToolsUtil.RequireAdmin("Este reparo");
            var c = new Ctx { Rpc = rpc };
            c.Line(item.Title + "...");
            int exit = 0; bool ok; string msg = null;
            var sys = Proc.Sys32;
            switch (id)
            {
                case "sfc":
                {
                    var r = c.Run(Path.Combine(sys, "sfc.exe"), "/scannow", 90, Encoding.Unicode);
                    exit = r.ExitCode; ok = r.ExitCode == 0;
                    break;
                }
                case "dism-check": { var r = c.Run(Path.Combine(sys, "dism.exe"), "/Online /Cleanup-Image /CheckHealth", 20); exit = r.ExitCode; ok = exit == 0; break; }
                case "dism-scan": { var r = c.Run(Path.Combine(sys, "dism.exe"), "/Online /Cleanup-Image /ScanHealth", 90); exit = r.ExitCode; ok = exit == 0; break; }
                case "dism-restore": { var r = c.Run(Path.Combine(sys, "dism.exe"), "/Online /Cleanup-Image /RestoreHealth", 120); exit = r.ExitCode; ok = exit == 0; break; }
                case "component-cleanup": { var r = c.Run(Path.Combine(sys, "dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", 120); exit = r.ExitCode; ok = exit == 0; break; }
                case "network-reset":
                {
                    var a = c.Run(Path.Combine(sys, "netsh.exe"), "winsock reset", 5);
                    var b = c.Run(Path.Combine(sys, "netsh.exe"), "int ip reset", 5);
                    var d = c.Run(Path.Combine(sys, "ipconfig.exe"), "/flushdns", 2);
                    exit = a.ExitCode != 0 ? a.ExitCode : b.ExitCode != 0 ? b.ExitCode : d.ExitCode;
                    ok = a.ExitCode == 0; // "int ip reset" pode falhar em uma chave sem impedir o resto
                    msg = ok ? "Rede redefinida. Reinicie o computador para concluir." : null;
                    break;
                }
                case "dns-flush":
                {
                    ok = Rage.Boost.BoostService.FlushDns();
                    if (!ok) { var r = c.Run(Path.Combine(sys, "ipconfig.exe"), "/flushdns", 2); exit = r.ExitCode; ok = exit == 0; }
                    msg = ok ? "Cache DNS limpo." : null;
                    break;
                }
                case "icon-cache": ok = IconCache(c, out msg); break;
                case "restart-explorer": ok = RestartExplorer(c); msg = ok ? "Explorer reiniciado." : "Não foi possível reiniciar o Explorer."; break;
                case "windows-update-reset": ok = ResetWindowsUpdate(c, out msg); break;
                case "store-reset":
                {
                    var r = c.Run(Path.Combine(sys, "wsreset.exe"), "", 10);
                    exit = r.ExitCode; ok = exit == 0;
                    msg = ok ? "Cache da Microsoft Store limpo." : null;
                    break;
                }
                case "chkdsk-scan":
                {
                    var drive = Path.GetPathRoot(ToolsUtil.WinDir).TrimEnd('\\');
                    var r = c.Run(Path.Combine(sys, "chkdsk.exe"), drive + " /scan", 90);
                    exit = r.ExitCode; ok = exit == 0;
                    if (exit == 1) { ok = true; msg = "Erros encontrados e corrigidos."; }
                    else if (exit == 2 || exit == 3) msg = "Foram encontrados problemas que precisam de reparo offline (chkdsk /f na próxima inicialização).";
                    break;
                }
                case "time-sync":
                {
                    try
                    {
                        using (var sc = new ServiceController("w32time"))
                        {
                            if (sc.StartType == ServiceStartMode.Disabled) throw new RpcException("O serviço Horário do Windows está desativado.");
                            if (sc.Status != ServiceControllerStatus.Running) { c.Line("Iniciando o serviço Horário do Windows..."); sc.Start(); sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
                        }
                    }
                    catch (RpcException) { throw; }
                    catch (Exception ex) { c.Line("Aviso: " + ex.Message); }
                    var r = c.Run(Path.Combine(sys, "w32tm.exe"), "/resync /force", 3);
                    if (r.ExitCode != 0)
                    {
                        c.Run(Path.Combine(sys, "w32tm.exe"), "/config /update /manualpeerlist:\"time.windows.com,0x9 a.st1.ntp.br,0x9\" /syncfromflags:manual", 2);
                        r = c.Run(Path.Combine(sys, "w32tm.exe"), "/resync /force", 3);
                    }
                    exit = r.ExitCode; ok = exit == 0;
                    msg = ok ? "Relógio sincronizado: " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss") : null;
                    break;
                }
                case "print-spooler": ok = ClearSpooler(c, out msg); break;
                default: throw new RpcException("Reparo desconhecido.");
            }
            if (msg == null) msg = ToolsUtil.LastMeaningful(c.Out.ToString()) ?? (ok ? "Concluído." : "Falhou (código " + exit + ").");
            if (ok) Activity.Add("fix", item.Icon, item.Title, msg);
            else Activity.Add("warn", item.Icon, item.Title + " falhou", msg);
            return new { ok, exitCode = exit, message = msg, needsReboot = ok && item.NeedsReboot };
        }

        static bool RestartExplorer(Ctx c)
        {
            c.Line("Encerrando o Explorer...");
            var session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { if (p.SessionId == session) { p.Kill(); p.WaitForExit(5000); } } catch { } finally { p.Dispose(); }
            }
            Thread.Sleep(1500);
            // O Windows costuma reabrir o Explorer sozinho; se não, abre de novo
            if (Process.GetProcessesByName("explorer").All(p => { try { return p.SessionId != session; } catch { return true; } }))
            {
                c.Line("Abrindo o Explorer...");
                try { Process.Start(new ProcessStartInfo(Path.Combine(ToolsUtil.WinDir, "explorer.exe")) { UseShellExecute = true }); }
                catch (Exception ex) { c.Line("Erro: " + ex.Message); return false; }
            }
            c.Line("Explorer reiniciado.");
            return true;
        }

        static bool IconCache(Ctx c, out string msg)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            try { c.Run(Path.Combine(Proc.Sys32, "ie4uinit.exe"), "-show", 1); } catch { }
            var session = Process.GetCurrentProcess().SessionId;
            c.Line("Encerrando o Explorer...");
            foreach (var p in Process.GetProcessesByName("explorer"))
                try { if (p.SessionId == session) { p.Kill(); p.WaitForExit(5000); } } catch { } finally { p.Dispose(); }
            Thread.Sleep(800);
            int deleted = 0;
            var files = new List<string>();
            var legacy = Path.Combine(local, "IconCache.db");
            if (File.Exists(legacy)) files.Add(legacy);
            var exp = Path.Combine(local, @"Microsoft\Windows\Explorer");
            if (Directory.Exists(exp)) files.AddRange(Directory.GetFiles(exp, "iconcache_*.db"));
            foreach (var f in files)
            {
                try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); deleted++; c.Line("Apagado: " + Path.GetFileName(f)); }
                catch (Exception ex) { c.Line("Em uso: " + Path.GetFileName(f) + " (" + ex.Message + ")"); }
            }
            Thread.Sleep(500);
            if (Process.GetProcessesByName("explorer").All(p => { try { return p.SessionId != session; } catch { return true; } }))
                try { Process.Start(new ProcessStartInfo(Path.Combine(ToolsUtil.WinDir, "explorer.exe")) { UseShellExecute = true }); } catch { }
            msg = deleted > 0 ? "Cache de ícones reconstruído (" + deleted + " arquivos)." : "Nenhum arquivo de cache foi apagado (podem estar em uso). Reinicie o computador e tente de novo.";
            return deleted > 0 || files.Count == 0;
        }

        static void StopService(Ctx c, string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) return;
                    c.Line("Parando " + sc.DisplayName + "...");
                    if (sc.CanStop) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ServiceProcess.TimeoutException) { c.Line("Aviso: " + name + " demorou para parar."); }
        }

        static void StartService(Ctx c, string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.StartType == ServiceStartMode.Disabled || sc.Status == ServiceControllerStatus.Running) return;
                    c.Line("Iniciando " + sc.DisplayName + "...");
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { c.Line("Aviso: " + name + ": " + ex.Message); }
        }

        static bool ResetWindowsUpdate(Ctx c, out string msg)
        {
            var services = new[] { "wuauserv", "UsoSvc", "bits", "cryptSvc", "msiserver" };
            foreach (var s in services) StopService(c, s);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var ok = true;
            foreach (var dir in new[] { Path.Combine(ToolsUtil.WinDir, "SoftwareDistribution"), Path.Combine(Proc.Sys32, "catroot2") })
            {
                if (!Directory.Exists(dir)) continue;
                var dest = dir + ".old-" + stamp;
                try { Directory.Move(dir, dest); c.Line("Renomeado: " + Path.GetFileName(dir) + " → " + Path.GetFileName(dest)); }
                catch (Exception ex) { ok = false; c.Line("Não foi possível renomear " + Path.GetFileName(dir) + ": " + ex.Message); }
            }
            foreach (var s in Enumerable.Reverse(services)) StartService(c, s);
            msg = ok ? "Windows Update redefinido. Procure atualizações de novo em Configurações." : "Alguns itens estavam em uso. Reinicie o computador e tente de novo.";
            return ok;
        }

        static bool ClearSpooler(Ctx c, out string msg)
        {
            StopService(c, "Spooler");
            var dir = Path.Combine(Proc.Sys32, @"spool\PRINTERS");
            int n = 0, fail = 0;
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { File.Delete(f); n++; } catch { fail++; }
                }
            c.Line(n + " arquivos removidos da fila.");
            StartService(c, "Spooler");
            msg = fail == 0 ? (n > 0 ? "Fila de impressão limpa (" + n + " arquivos)." : "A fila de impressão já estava vazia. Serviço reiniciado.") : "Alguns arquivos da fila estavam em uso.";
            return fail == 0;
        }

        [Rpc("fix.list")]
        public static object ListRpc() => Items.Select(i => new { i.Id, i.Title, i.Desc, i.Icon, i.Minutes, i.NeedsReboot, i.Admin, available = !i.Admin || Program.IsAdmin }).ToList();

        [Rpc("fix.run")]
        public static object RunRpc(string id, RpcContext ctx) => Run(id, ctx);
    }
}
