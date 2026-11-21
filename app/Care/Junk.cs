using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Rage.Core;

namespace Rage.Care
{
    /// <summary>Uma pasta (ou arquivos de uma pasta) que pertence a uma regra de limpeza.</summary>
    internal sealed class JunkTarget
    {
        public string Dir;
        public string[] Patterns;          // null = todos os arquivos
        public bool Recursive = true;
        public double MinAgeHours;         // só arquivos mais velhos que isso
        public bool RemoveEmptyDirs;       // remove subpastas que ficarem vazias (nunca a raiz)
    }

    internal sealed class JunkRule
    {
        public string Id, Group, Title, Detail, Note;
        public string Risk = "safe";
        public bool Selected = true;
        public bool NeedsAdmin;
        public List<JunkTarget> Targets = new List<JunkTarget>();
        public Func<string> RunningNote;                     // aviso quando o app/navegador está aberto
        public Func<RpcContext, CareItem> CustomScan;
        public Func<CareItem, RpcContext, FixResult> CustomFix;
    }

    /// <summary>Categoria "junk" — arquivos temporários, caches e lixo do sistema.</summary>
    public sealed class JunkScanner : ICareScanner
    {
        public string Category => "junk";
        public string Title => "Arquivos inúteis";
        public string Description => "Temporários, caches e lixo do sistema";
        public string Icon => "delete_sweep";
        public int Order => 10;
        public bool DefaultOn => true;

        const string GWin = "Windows", GBrowsers = "Navegadores", GApps = "Apps e jogos", GSys = "Sistema";
        const string ShaderNote = "Os jogos recompilam os shaders na próxima abertura (pode haver travadinhas no início).";

        // ---------- Regras ----------
        static JunkTarget T(string dir, params string[] patterns) => new JunkTarget { Dir = dir, Patterns = patterns.Length == 0 ? null : patterns };

        static List<JunkRule> Rules()
        {
            var la = Fs.LocalAppData; var ra = Fs.AppData; var pd = Fs.ProgramData; var win = Fs.Windows;
            var rules = new List<JunkRule>();

            // ----- Windows -----
            var temps = new[] { Path.GetTempPath(), Path.Combine(la, "Temp") }.Select(x => x.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            rules.Add(new JunkRule
            {
                Id = "usertemp", Group = GWin, Title = "Arquivos temporários do usuário", Detail = temps[0] + " (arquivos com mais de 24 horas)",
                Targets = temps.Select(t => new JunkTarget { Dir = t, MinAgeHours = 24, RemoveEmptyDirs = true }).ToList(),
            });
            rules.Add(new JunkRule
            {
                Id = "wintemp", Group = GWin, Title = "Temporários do Windows", Detail = Path.Combine(win, "Temp") + " (arquivos com mais de 24 horas)", NeedsAdmin = true,
                Targets = { new JunkTarget { Dir = Path.Combine(win, "Temp"), MinAgeHours = 24, RemoveEmptyDirs = true } },
            });
            var pending = CareUtil.RebootPending();
            rules.Add(new JunkRule
            {
                Id = "wucache", Group = GWin, Title = "Cache do Windows Update", Detail = "Atualizações já baixadas e instaladas (SoftwareDistribution\\Download)", NeedsAdmin = true,
                Selected = !pending,
                Note = pending ? "Há uma atualização aguardando reinicialização. Reinicie o PC antes de limpar este item." : null,
                Targets = { new JunkTarget { Dir = Path.Combine(win, @"SoftwareDistribution\Download"), MinAgeHours = 48, RemoveEmptyDirs = true } },
            });
            rules.Add(new JunkRule
            {
                Id = "docache", Group = GWin, Title = "Cache da Otimização de Entrega", Detail = "Pedaços de atualizações compartilhados com outros PCs", NeedsAdmin = true,
                Targets =
                {
                    new JunkTarget { Dir = Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), RemoveEmptyDirs = true },
                    new JunkTarget { Dir = Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization\Cache"), RemoveEmptyDirs = true },
                },
            });
            var wer = new List<JunkTarget>();
            foreach (var b in new[] { Path.Combine(pd, @"Microsoft\Windows\WER"), Path.Combine(la, @"Microsoft\Windows\WER") })
                foreach (var s in new[] { "ReportArchive", "ReportQueue", "Temp" })
                    wer.Add(new JunkTarget { Dir = Path.Combine(b, s), RemoveEmptyDirs = true });
            rules.Add(new JunkRule { Id = "wer", Group = GWin, Title = "Relatórios de erros do Windows", Detail = "Relatórios de falhas de apps já enviados ou na fila", Targets = wer });
            rules.Add(new JunkRule
            {
                Id = "dumps", Group = GWin, Title = "Despejos de memória", Detail = "Minidump, MEMORY.DMP, CrashDumps e LiveKernelReports",
                Note = "Só servem para diagnosticar travamentos e telas azuis.",
                Targets =
                {
                    T(Path.Combine(win, "Minidump"), "*.dmp"),
                    new JunkTarget { Dir = win, Patterns = new[] { "MEMORY.DMP" }, Recursive = false },
                    new JunkTarget { Dir = Path.Combine(la, "CrashDumps"), RemoveEmptyDirs = true },
                    T(Path.Combine(win, "LiveKernelReports"), "*.dmp"),
                },
            });
            rules.Add(new JunkRule
            {
                Id = "thumbcache", Group = GWin, Title = "Cache de miniaturas", Detail = "Miniaturas de fotos e vídeos do Explorador (recriadas quando necessário)",
                Targets = { new JunkTarget { Dir = Path.Combine(la, @"Microsoft\Windows\Explorer"), Patterns = new[] { "thumbcache_*.db" }, Recursive = false } },
            });
            rules.Add(new JunkRule
            {
                Id = "winlogs", Group = GWin, Title = "Logs do Windows", Detail = "Logs antigos do CBS e do DISM (mais de 7 dias)", NeedsAdmin = true,
                Targets =
                {
                    new JunkTarget { Dir = Path.Combine(win, @"Logs\CBS"), Patterns = new[] { "*.log", "*.cab" }, Recursive = false, MinAgeHours = 24 * 7 },
                    new JunkTarget { Dir = Path.Combine(win, @"Logs\DISM"), Patterns = new[] { "*.log" }, Recursive = false, MinAgeHours = 24 * 7 },
                },
            });
            rules.Add(new JunkRule
            {
                Id = "prefetch", Group = GWin, Title = "Prefetch", Detail = Path.Combine(win, "Prefetch"), NeedsAdmin = true, Selected = false,
                Note = "Deixa a inicialização dos apps mais lenta por um tempo, até o Windows recriar estes arquivos.",
                Targets = { new JunkTarget { Dir = Path.Combine(win, "Prefetch"), Patterns = new[] { "*.pf" }, Recursive = false } },
            });
            rules.Add(new JunkRule { Id = "recyclebin", Group = GWin, Title = "Lixeira", Detail = "Arquivos na Lixeira de todas as unidades", CustomScan = ScanRecycleBin, CustomFix = EmptyRecycleBin });

            // ----- Navegadores (só cache: histórico e cookies ficam em "Rastros de privacidade") -----
            foreach (var b in BrowserInfo.All())
            {
                var targets = new List<JunkTarget>();
                if (b.Firefox)
                {
                    foreach (var p in b.FirefoxProfiles(true))
                    {
                        targets.Add(new JunkTarget { Dir = Path.Combine(p, "cache2") });
                        targets.Add(new JunkTarget { Dir = Path.Combine(p, "startupCache") });
                    }
                }
                else targets.AddRange(ChromiumCacheTargets(b.Roots, b.Profiles()));
                if (targets.Count == 0) continue;
                var bb = b;
                rules.Add(new JunkRule
                {
                    Id = b.Id, Group = GBrowsers, Title = "Cache do " + b.Name, Detail = b.Firefox ? "Cache de páginas (cache2)" : "Cache, Code Cache, GPUCache e cache de shaders de todos os perfis",
                    Targets = targets, RunningNote = () => bb.Running() ? bb.RunningNote(false) : null,
                });
            }

            // ----- Apps e jogos -----
            var discord = new List<JunkTarget>();
            foreach (var d in new[] { "discord", "discordptb", "discordcanary" })
                foreach (var c in new[] { "Cache", "Code Cache", "GPUCache" })
                    discord.Add(new JunkTarget { Dir = Path.Combine(ra, d, c) });
            rules.Add(App("discord", "Cache do Discord", "Cache, Code Cache e GPUCache", discord, "Discord", "Discord", "DiscordPTB", "DiscordCanary"));

            rules.Add(App("spotify", "Cache do Spotify", "Músicas e imagens em cache (as músicas baixadas para ouvir offline são mantidas)", new List<JunkTarget>
            {
                new JunkTarget { Dir = Path.Combine(la, @"Spotify\Data") },
                new JunkTarget { Dir = Path.Combine(la, @"Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache\Spotify\Data") },
            }, "Spotify", "Spotify"));

            var teams = new List<JunkTarget>();
            foreach (var c in new[] { "Cache", "Code Cache", "GPUCache", "tmp" }) teams.Add(new JunkTarget { Dir = Path.Combine(ra, @"Microsoft\Teams", c) });
            var newTeams = Path.Combine(la, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView");
            teams.AddRange(ChromiumCacheTargets(new List<string> { newTeams }, Fs.SubDirs(newTeams).Concat(new[] { newTeams }).ToList()));
            rules.Add(App("teams", "Cache do Microsoft Teams", "Cache do Teams clássico e do novo Teams", teams, "Microsoft Teams", "Teams", "ms-teams"));

            var steamPath = Fs.Clean(Reg.GetStr(@"HKCU\Software\Valve\Steam", "SteamPath"));
            var steam = new List<JunkTarget>();
            var html = Path.Combine(la, @"Steam\htmlcache");
            steam.AddRange(ChromiumCacheTargets(new List<string> { html }, Fs.SubDirs(html).Concat(new[] { html }).ToList()));
            if (steamPath != null)
            {
                steam.Add(new JunkTarget { Dir = Path.Combine(steamPath, @"appcache\httpcache"), RemoveEmptyDirs = true });
                steam.Add(new JunkTarget { Dir = Path.Combine(steamPath, "logs"), MinAgeHours = 24 });
                steam.Add(new JunkTarget { Dir = Path.Combine(steamPath, "dumps"), RemoveEmptyDirs = true });
            }
            rules.Add(App("steam", "Cache da Steam", "Cache da loja, cache HTTP, logs e despejos de falhas", steam, "Steam", "steam", "steamwebhelper"));

            var epic = Fs.SubDirs(Path.Combine(la, @"EpicGamesLauncher\Saved"))
                .Where(d => Path.GetFileName(d).StartsWith("webcache", StringComparison.OrdinalIgnoreCase))
                .Select(d => new JunkTarget { Dir = d, RemoveEmptyDirs = true }).ToList();
            rules.Add(App("epic", "Cache do Epic Games Launcher", "Cache da loja (webcache)", epic, "Epic Games Launcher", "EpicGamesLauncher"));

            rules.Add(App("battlenet", "Cache do Battle.net", "Cache do aplicativo e do navegador interno", new List<JunkTarget>
            {
                new JunkTarget { Dir = Path.Combine(la, @"Battle.net\Cache"), RemoveEmptyDirs = true },
                new JunkTarget { Dir = Path.Combine(la, @"Battle.net\BrowserCache"), RemoveEmptyDirs = true },
                new JunkTarget { Dir = Path.Combine(pd, @"Battle.net\Cache"), RemoveEmptyDirs = true },
                new JunkTarget { Dir = Path.Combine(pd, @"Blizzard Entertainment\Battle.net\Cache"), RemoveEmptyDirs = true },
            }, "Battle.net", "Battle.net"));

            rules.Add(Shader("d3dcache", "Cache de shaders do DirectX", Path.Combine(la, "D3DSCache"), new[] { Path.Combine(la, "D3DSCache") }));
            rules.Add(Shader("nvidiacache", "Cache de shaders da NVIDIA", "DXCache, GLCache e NV_Cache", new[]
            {
                Path.Combine(la, @"NVIDIA\DXCache"), Path.Combine(la, @"NVIDIA\GLCache"),
                Path.Combine(Fs.LocalLow, @"NVIDIA\PerDriverVersion\DXCache"), Path.Combine(Fs.LocalLow, @"NVIDIA\PerDriverVersion\GLCache"),
                Path.Combine(pd, @"NVIDIA Corporation\NV_Cache"),
            }));
            rules.Add(Shader("amdcache", "Cache de shaders da AMD", "DxCache, DxcCache, GLCache e VkCache", new[]
            {
                Path.Combine(la, @"AMD\DxCache"), Path.Combine(la, @"AMD\DxcCache"), Path.Combine(la, @"AMD\GLCache"), Path.Combine(la, @"AMD\VkCache"),
            }));
            rules.Add(Shader("intelcache", "Cache de shaders da Intel", "ShaderCache da Intel", new[] { Path.Combine(Fs.LocalLow, @"Intel\ShaderCache") }));

            // ----- Sistema -----
            rules.Add(new JunkRule
            {
                Id = "setuplogs", Group = GSys, Title = "Logs de instalação antigos", Detail = "Logs de instalação e atualização do Windows com mais de 30 dias", NeedsAdmin = true,
                Targets =
                {
                    new JunkTarget { Dir = Path.Combine(win, "Panther"), Patterns = new[] { "*.log", "*.etl" }, MinAgeHours = 24 * 30 },
                    new JunkTarget { Dir = Path.Combine(win, @"Logs\MoSetup"), Patterns = new[] { "*.log" }, MinAgeHours = 24 * 30 },
                    new JunkTarget { Dir = Path.Combine(win, @"Logs\WindowsUpdate"), Patterns = new[] { "*.etl" }, Recursive = false, MinAgeHours = 24 * 30 },
                    new JunkTarget { Dir = win, Patterns = new[] { "*.log" }, Recursive = false, MinAgeHours = 24 * 30 },
                },
            });
            rules.Add(new JunkRule
            {
                Id = "iconcache", Group = GSys, Title = "Cache de ícones", Detail = "IconCache.db e iconcache_*.db", Selected = false,
                Note = "Os ícones são recriados; alguns podem aparecer em branco até reiniciar o PC.",
                Targets =
                {
                    new JunkTarget { Dir = la, Patterns = new[] { "IconCache.db" }, Recursive = false },
                    new JunkTarget { Dir = Path.Combine(la, @"Microsoft\Windows\Explorer"), Patterns = new[] { "iconcache_*.db" }, Recursive = false },
                },
            });
            rules.Add(new JunkRule { Id = "windowsold", Group = GSys, Title = "Instalação anterior do Windows (Windows.old)", CustomScan = ScanWindowsOld });
            return rules;
        }

        static JunkRule App(string id, string title, string detail, List<JunkTarget> targets, string appName, params string[] processes)
        {
            return new JunkRule
            {
                Id = id, Group = GApps, Title = title, Detail = detail, Targets = targets,
                RunningNote = () => CareUtil.AnyRunning(processes) ? "O " + appName + " está aberto: arquivos em uso serão ignorados. Feche-o para limpar tudo." : null,
            };
        }

        static JunkRule Shader(string id, string title, string detail, string[] dirs) => new JunkRule
        {
            Id = id, Group = GApps, Title = title, Detail = detail, Selected = false, Risk = "caution", Note = ShaderNote,
            Targets = dirs.Select(d => new JunkTarget { Dir = d, RemoveEmptyDirs = true }).ToList(),
        };

        static List<JunkTarget> ChromiumCacheTargets(List<string> roots, List<string> profiles)
        {
            var l = new List<JunkTarget>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string d) { if (Fs.DirExists(d) && seen.Add(d)) l.Add(new JunkTarget { Dir = d }); }
            foreach (var p in profiles) foreach (var c in BrowserInfo.ProfileCacheDirs) Add(Path.Combine(p, c));
            foreach (var r in roots) foreach (var c in BrowserInfo.RootCacheDirs) Add(Path.Combine(r, c));
            return l;
        }

        // ---------- Verificação ----------
        public List<CareItem> Scan(RpcContext ctx)
        {
            var rules = Rules();
            var results = new CareItem[rules.Count];
            var po = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ctx.Token };
            Parallel.For(0, rules.Count, po, i =>
            {
                var r = rules[i];
                CareUtil.Item(ctx, Category, r.Title);
                try { results[i] = r.CustomScan != null ? r.CustomScan(ctx) : ScanRule(r, ctx.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn("Lixo " + r.Id + ": " + ex.Message); }
                if (results[i] != null)
                {
                    results[i].Id = "junk." + r.Id;
                    results[i].Category = Category;
                    results[i].Group = r.Group;
                    results[i].Tag = r;
                    var running = r.RunningNote?.Invoke();
                    if (running != null) results[i].Note = results[i].Note == null ? running : results[i].Note + " " + running;
                }
            });
            return results.Where(x => x != null).ToList();
        }

        static Fs.WalkOptions Options(JunkTarget t, DateTime nowUtc) => new Fs.WalkOptions
        {
            Recursive = t.Recursive,
            Pattern = Fs.Wild(t.Patterns),
            OlderThanUtc = t.MinAgeHours > 0 ? nowUtc.AddHours(-t.MinAgeHours) : (DateTime?)null,
        };

        static CareItem ScanRule(JunkRule r, CancellationToken ct)
        {
            long size = 0; int count = 0;
            var top = new List<Fs.Entry>();
            var now = DateTime.UtcNow;
            foreach (var t in r.Targets)
            {
                if (!Fs.DirExists(t.Dir)) continue;
                Fs.Walk(t.Dir, Options(t, now), e => { size += e.Size; count++; AddTop(top, e); }, ct);
            }
            if (count == 0) return null;
            return new CareItem
            {
                Title = r.Title, Detail = r.Detail, Size = size, Count = count, Selected = r.Selected, Risk = r.Risk, Note = r.Note,
                Paths = top.Select(x => x.Path).ToList(),
            };
        }

        static void AddTop(List<Fs.Entry> top, Fs.Entry e)
        {
            if (top.Count < 5) { top.Add(e); top.Sort((a, b) => b.Size.CompareTo(a.Size)); return; }
            if (e.Size <= top[4].Size) return;
            top[4] = e;
            top.Sort((a, b) => b.Size.CompareTo(a.Size));
        }

        // ---------- Correção ----------
        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var r = it.Tag as JunkRule;
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                if (r == null) { results.Add(new FixResult { Id = it.Id, Message = "Faça uma nova verificação." }); continue; }
                try
                {
                    if (r.CustomFix != null) results.Add(r.CustomFix(it, ctx));
                    else if (r.CustomScan != null) results.Add(new FixResult { Id = it.Id, Message = "Este item é só informativo." });
                    else results.Add(FixRule(it, r, ctx.Token));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("Limpeza " + r.Id, ex); results.Add(new FixResult { Id = it.Id, Message = ex.Message }); }
            }
            return results;
        }

        internal static FixResult DeleteTargets(string id, IEnumerable<JunkTarget> targets, CancellationToken ct, out int deleted, out int skipped)
        {
            long freed = 0; deleted = 0; skipped = 0;
            var now = DateTime.UtcNow;
            foreach (var t in targets)
            {
                if (!Fs.DirExists(t.Dir)) continue;
                var files = new List<Fs.Entry>();
                var dirs = t.RemoveEmptyDirs ? new List<Fs.Entry>() : null;
                Fs.Walk(t.Dir, Options(t, now), e => files.Add(e), ct, dirs);
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Fs.DeleteFile(f.Path)) { freed += f.Size; deleted++; } else skipped++;
                }
                if (dirs != null)
                {
                    var limit = t.MinAgeHours > 0 ? now.AddHours(-t.MinAgeHours) : DateTime.MaxValue;
                    for (int i = dirs.Count - 1; i >= 0; i--)
                        if (dirs[i].LastWriteUtc <= limit) Fs.RemoveDir(dirs[i].Path); // só sai se estiver vazia
                }
            }
            return new FixResult { Id = id, Freed = freed };
        }

        static FixResult FixRule(CareItem it, JunkRule r, CancellationToken ct)
        {
            if (r.NeedsAdmin && !Program.IsAdmin) return new FixResult { Id = it.Id, Message = CareUtil.AdminMsg };
            var res = DeleteTargets(it.Id, r.Targets, ct, out var deleted, out var skipped);
            res.Ok = deleted > 0 || skipped == 0;
            if (deleted == 0 && skipped > 0)
                res.Message = (r.RunningNote?.Invoke() != null ? "Os arquivos estão em uso. Feche o programa e tente de novo." : "Os arquivos estão em uso e foram mantidos.");
            else
                res.Message = Fmt.Bytes(res.Freed) + " liberados" + (skipped > 0 ? " (" + skipped + (skipped == 1 ? " arquivo em uso foi mantido)" : " arquivos em uso foram mantidos)") : "");
            return res;
        }

        // ---------- Lixeira ----------
        [StructLayout(LayoutKind.Sequential)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; } // alinhamento natural em x64 (24 bytes)

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

        /// <summary>Pastas da Lixeira do usuário atual em cada unidade fixa (X:\$Recycle.Bin\SID).</summary>
        static List<string> BinDirs()
        {
            var l = new List<string>();
            string sid;
            try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { sid = null; }
            if (sid == null) return l;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var p = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
                    if (Fs.DirExists(p)) l.Add(p);
                }
                catch { }
            }
            return l;
        }

        /// <summary>Mede a Lixeira pelos arquivos $R (conteúdo) e $I (informações); o SHQueryRecycleBin às vezes ignora itens.</summary>
        static bool MeasureBin(out long size, out long items, CancellationToken ct)
        {
            size = 0; items = 0;
            var dirs = BinDirs();
            if (dirs.Count == 0)
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) };
                if (SHQueryRecycleBinW(null, ref info) != 0) return false;
                size = info.i64Size; items = info.i64NumItems;
                return true;
            }
            long s = 0, n = 0;
            foreach (var dir in dirs)
            {
                Fs.Walk(dir, new Fs.WalkOptions(), e =>
                {
                    var rel = e.Path.Substring(dir.Length + 1);
                    var top = rel.IndexOf('\\') < 0;
                    if (top && !rel.StartsWith("$R", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) return; // desktop.ini
                    s += e.Size;
                    if (top && rel.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) n++;
                }, ct);
                n += Fs.SubDirs(dir).Count(x => Path.GetFileName(x).StartsWith("$R", StringComparison.OrdinalIgnoreCase));
            }
            size = s; items = n;
            return true;
        }

        static CareItem ScanRecycleBin(RpcContext ctx)
        {
            if (!MeasureBin(out var size, out var items, ctx.Token) || items <= 0) return null;
            return new CareItem { Title = "Lixeira", Detail = "Arquivos na Lixeira de todas as unidades", Size = size, Count = (int)Math.Min(items, int.MaxValue) };
        }

        static FixResult EmptyRecycleBin(CareItem it, RpcContext ctx)
        {
            MeasureBin(out var before, out _, ctx.Token);
            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, 1 | 2 | 4); // sem confirmação, sem progresso, sem som
            MeasureBin(out var after, out var left, ctx.Token);
            if (left > 0)
            {
                // Itens que a API deixou para trás: apaga os pares $R/$I da Lixeira do próprio usuário (desktop.ini fica)
                foreach (var dir in BinDirs())
                {
                    foreach (var sub in Fs.SubDirs(dir).Where(x => Path.GetFileName(x).StartsWith("$R", StringComparison.OrdinalIgnoreCase)))
                    {
                        DeleteTargets(it.Id, new[] { new JunkTarget { Dir = sub, RemoveEmptyDirs = true } }, ctx.Token, out _, out _);
                        Fs.RemoveDir(sub);
                    }
                    var files = new List<Fs.Entry>();
                    Fs.Walk(dir, new Fs.WalkOptions { Recursive = false, Pattern = Fs.Wild("$R*", "$I*") }, e => files.Add(e), ctx.Token);
                    foreach (var f in files) Fs.DeleteFile(f.Path);
                }
                MeasureBin(out after, out left, ctx.Token);
            }
            var freed = Math.Max(0, before - after);
            var ok = left == 0;
            return new FixResult
            {
                Id = it.Id, Ok = ok, Freed = freed,
                Message = ok ? "Lixeira esvaziada (" + Fmt.Bytes(freed) + ")" : "Alguns itens da Lixeira estão em uso e foram mantidos (código 0x" + hr.ToString("x8") + ").",
            };
        }

        // ---------- Windows.old (só informativo) ----------
        static CareItem ScanWindowsOld(RpcContext ctx)
        {
            var dir = Path.Combine(Path.GetPathRoot(Fs.Windows) ?? "C:\\", "Windows.old");
            if (!Fs.DirExists(dir)) return null;
            long size = 0; int count = 0; bool partial = false;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token))
            {
                cts.CancelAfter(2500);
                try { Fs.Walk(dir, new Fs.WalkOptions(), e => { size += e.Size; count++; }, cts.Token); }
                catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested) { partial = true; }
            }
            return new CareItem
            {
                Title = "Instalação anterior do Windows (Windows.old)",
                Detail = (partial ? "Mais de " : "") + Fmt.Bytes(size) + " em " + count.ToString("N0") + " arquivos (" + dir + ")",
                Fixable = false, Selected = false, Action = "ms-settings:storagesense",
                Note = "Para remover, use Configurações › Sistema › Armazenamento › Arquivos temporários › Instalação anterior do Windows.",
                Paths = new List<string> { dir },
            };
        }
    }
}
