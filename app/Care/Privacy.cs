using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Rage.Core;

namespace Rage.Care
{
    internal sealed class PrivacyRule
    {
        public string Id, Group, Title, Detail, Note;
        public string Risk = "safe";
        public bool Selected = true;
        public List<JunkTarget> Targets = new List<JunkTarget>();  // pastas/arquivos por padrão
        public List<string> Files = new List<string>();            // arquivos específicos
        public List<string> RegKeys = new List<string>();          // apaga valores (e subchaves, se RegSubKeys)
        public bool RegSubKeys;
        public Func<string, bool> RegValueFilter;                  // quais valores apagar (null = todos)
        public Func<string, bool> RegCountFilter;                  // quais valores contam como item (null = todos, exceto MRUList)
        public BrowserInfo Browser;                                // navegador precisa estar fechado
        public Func<FixResult> CustomFix;
    }

    /// <summary>Categoria "privacy" — rastros de uso do Windows e dos navegadores. Senhas salvas nunca são tocadas.</summary>
    public sealed class PrivacyScanner : ICareScanner
    {
        public string Category => "privacy";
        public string Title => "Rastros de privacidade";
        public string Description => "Histórico de uso e atividade";
        public string Icon => "visibility_off";
        public int Order => 20;
        public bool DefaultOn => true;

        const string GWin = "Windows", GBrowsers = "Navegadores";
        const string Explorer = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer";
        const string QuickAccess = "f01b4d95cf55d32a"; // lista do Explorador (Acesso rápido e pastas fixadas): nunca apagar

        static readonly string[] ChromiumHistory = { "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal", "Network Action Predictor", "Network Action Predictor-journal" };
        static readonly string[] ChromiumCookies = { @"Network\Cookies", @"Network\Cookies-journal", "Cookies", "Cookies-journal", "Extension Cookies", "Extension Cookies-journal" };

        static List<PrivacyRule> Rules()
        {
            var recent = Path.Combine(Fs.AppData, @"Microsoft\Windows\Recent");
            var rules = new List<PrivacyRule>
            {
                new PrivacyRule
                {
                    Id = "recentdocs", Group = GWin, Title = "Documentos recentes", Detail = "Lista de arquivos abertos recentemente (pasta Recentes)",
                    Targets = { new JunkTarget { Dir = recent, Patterns = new[] { "*.lnk" }, Recursive = false } },
                    RegKeys = { Explorer + @"\RecentDocs" }, RegSubKeys = true,
                },
                new PrivacyRule
                {
                    Id = "jumplists", Group = GWin, Title = "Listas de atalhos (Jump Lists)", Detail = "Itens recentes mostrados ao clicar com o botão direito nos apps da barra de tarefas",
                    Selected = false, Risk = "caution", Note = "Itens fixados nas listas de atalhos dos apps também serão removidos. O Acesso rápido do Explorador é mantido.",
                    Targets =
                    {
                        new JunkTarget { Dir = Path.Combine(recent, "AutomaticDestinations"), Patterns = new[] { "*.automaticDestinations-ms" }, Recursive = false },
                        new JunkTarget { Dir = Path.Combine(recent, "CustomDestinations"), Patterns = new[] { "*.customDestinations-ms" }, Recursive = false },
                    },
                },
                new PrivacyRule { Id = "runmru", Group = GWin, Title = "Histórico do Executar", Detail = "Comandos digitados em Win + R", RegKeys = { Explorer + @"\RunMRU" } },
                new PrivacyRule { Id = "typedpaths", Group = GWin, Title = "Caminhos digitados no Explorador", Detail = "Endereços digitados na barra do Explorador de Arquivos", RegKeys = { Explorer + @"\TypedPaths" } },
                new PrivacyRule { Id = "wordwheel", Group = GWin, Title = "Pesquisas no Explorador de Arquivos", Detail = "Termos pesquisados na caixa de pesquisa do Explorador", RegKeys = { Explorer + @"\WordWheelQuery" } },
            };

            if (Reg.GetInt(@"HKCU\Software\Microsoft\Clipboard", "EnableClipboardHistory") == 1)
                rules.Add(new PrivacyRule
                {
                    Id = "clipboard", Group = GWin, Title = "Histórico da área de transferência", Detail = "Itens copiados recentemente (Win + V)", Selected = false,
                    Note = "Os itens copiados recentemente somem do histórico (Win + V).", CustomFix = ClearClipboard,
                });

            var office = OfficeMruKeys();
            if (office.Count > 0)
                rules.Add(new PrivacyRule
                {
                    Id = "officemru", Group = GWin, Title = "Arquivos recentes do Office", Detail = "Listas de arquivos e pastas recentes do Word, Excel, PowerPoint e outros",
                    Selected = false, Note = "A lista de arquivos recentes do Office será apagada (os arquivos não são tocados).",
                    RegKeys = office, RegValueFilter = n => n.StartsWith("Item ", StringComparison.OrdinalIgnoreCase),
                    RegCountFilter = n => Regex.IsMatch(n, @"^Item \d+$", RegexOptions.IgnoreCase),
                });

            // Navegadores
            foreach (var b in BrowserInfo.All())
            {
                if (b.Firefox)
                {
                    var profiles = b.FirefoxProfiles(false);
                    var cookies = profiles.SelectMany(p => new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm", "cookies.sqlite-journal" }.Select(f => Path.Combine(p, f))).ToList();
                    var forms = profiles.SelectMany(p => new[] { "formhistory.sqlite", "formhistory.sqlite-wal", "formhistory.sqlite-shm", "formhistory.sqlite-journal" }.Select(f => Path.Combine(p, f))).ToList();
                    rules.Add(new PrivacyRule
                    {
                        Id = "firefox.forms", Group = GBrowsers, Title = "Histórico de formulários e pesquisas do Firefox", Detail = "Textos digitados em formulários e na barra de pesquisa",
                        Selected = false, Risk = "caution", Browser = b, Files = forms,
                        Note = "O histórico de navegação do Firefox fica no mesmo arquivo dos favoritos e por isso é mantido.",
                    });
                    rules.Add(new PrivacyRule
                    {
                        Id = "firefox.cookies", Group = GBrowsers, Title = "Cookies do Firefox", Detail = "Cookies de todos os perfis",
                        Selected = false, Risk = "caution", Browser = b, Files = cookies, Note = "Você será desconectado dos sites.",
                    });
                    continue;
                }
                var profs = b.Profiles();
                if (profs.Count == 0) continue;
                rules.Add(new PrivacyRule
                {
                    Id = b.Id + ".history", Group = GBrowsers, Title = "Histórico do " + b.Name, Detail = "Sites visitados, downloads e sugestões da barra de endereços (todos os perfis)",
                    Selected = false, Risk = "caution", Browser = b, Files = profs.SelectMany(p => ChromiumHistory.Select(f => Path.Combine(p, f))).ToList(),
                    Note = "Favoritos e senhas salvas são mantidos.",
                });
                rules.Add(new PrivacyRule
                {
                    Id = b.Id + ".cookies", Group = GBrowsers, Title = "Cookies do " + b.Name, Detail = "Cookies de todos os perfis",
                    Selected = false, Risk = "caution", Browser = b, Files = profs.SelectMany(p => ChromiumCookies.Select(f => Path.Combine(p, f))).ToList(),
                    Note = "Você será desconectado dos sites.",
                });
            }
            return rules;
        }

        static List<string> OfficeMruKeys()
        {
            var l = new List<string>();
            const string root = @"HKCU\Software\Microsoft\Office";
            foreach (var ver in CareUtil.SubKeys(root).Where(v => Regex.IsMatch(v, @"^\d+\.\d+$")))
                foreach (var app in CareUtil.SubKeys(root + "\\" + ver))
                {
                    var ap = root + "\\" + ver + "\\" + app;
                    foreach (var m in new[] { "File MRU", "Place MRU" }) if (Reg.KeyExists(ap + "\\" + m)) l.Add(ap + "\\" + m);
                    var um = ap + @"\User MRU";
                    foreach (var id in CareUtil.SubKeys(um))
                        foreach (var m in new[] { "File MRU", "Place MRU" }) if (Reg.KeyExists(um + "\\" + id + "\\" + m)) l.Add(um + "\\" + id + "\\" + m);
                }
            return l;
        }

        // ---------- Verificação ----------
        public List<CareItem> Scan(RpcContext ctx)
        {
            var items = new List<CareItem>();
            var now = DateTime.UtcNow;
            foreach (var r in Rules())
            {
                ctx.Token.ThrowIfCancellationRequested();
                try
                {
                    long size = 0; int count = 0;
                    var paths = new List<string>();
                    foreach (var f in Files(r, ctx))
                    {
                        size += f.Size; count++;
                        if (paths.Count < 5) paths.Add(f.Path);
                    }
                    foreach (var k in r.RegKeys) count += RegCount(r, k);
                    if (r.CustomFix != null && count == 0) count = 1;
                    if (count == 0) continue;
                    var it = new CareItem
                    {
                        Id = "privacy." + r.Id, Category = Category, Group = r.Group, Title = r.Title, Detail = r.Detail,
                        Size = size > 0 ? size : (double?)null, Count = r.CustomFix != null ? (int?)null : count,
                        Selected = r.Selected, Risk = r.Risk, Note = r.Note, Paths = paths.Count > 0 ? paths : null, Tag = r,
                    };
                    if (r.Browser != null && r.Browser.Running()) it.Note = (it.Note == null ? "" : it.Note + " ") + r.Browser.RunningNote(true);
                    items.Add(it);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn("Privacidade " + r.Id + ": " + ex.Message); }
            }
            return items;
        }

        static IEnumerable<Fs.Entry> Files(PrivacyRule r, RpcContext ctx)
        {
            var list = new List<Fs.Entry>();
            foreach (var t in r.Targets)
            {
                if (!Fs.DirExists(t.Dir)) continue;
                Fs.Walk(t.Dir, new Fs.WalkOptions { Recursive = t.Recursive, Pattern = Fs.Wild(t.Patterns) }, e =>
                {
                    if (Path.GetFileName(e.Path).StartsWith(QuickAccess, StringComparison.OrdinalIgnoreCase)) return;
                    list.Add(e);
                }, ctx.Token);
            }
            foreach (var f in r.Files)
            {
                var st = Fs.Stat(f);
                if (st.HasValue && (st.Value.Attr & FileAttributes.Directory) == 0) list.Add(st.Value);
            }
            return list;
        }

        static int RegCount(PrivacyRule r, string key)
        {
            var n = CareUtil.Values(key).Count(v => v.Key.Length > 0 && (r.RegCountFilter != null ? r.RegCountFilter(v.Key)
                : !v.Key.Equals("MRUList", StringComparison.OrdinalIgnoreCase) && !v.Key.Equals("MRUListEx", StringComparison.OrdinalIgnoreCase)));
            if (r.RegSubKeys) n += CareUtil.SubKeys(key).Count;
            return n;
        }

        // ---------- Correção ----------
        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var results = new List<FixResult>();
            int i = 0;
            foreach (var it in items)
            {
                ctx.Token.ThrowIfCancellationRequested();
                ctx.Progress(new { phase = "item", category = Category, id = it.Id, title = it.Title, index = i++, total = items.Count });
                var r = it.Tag as PrivacyRule;
                if (r == null) { results.Add(new FixResult { Id = it.Id, Message = "Faça uma nova verificação." }); continue; }
                try { results.Add(FixRule(it, r, ctx)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("Privacidade " + r.Id, ex); results.Add(new FixResult { Id = it.Id, Message = ex.Message }); }
            }
            return results;
        }

        static FixResult FixRule(CareItem it, PrivacyRule r, RpcContext ctx)
        {
            if (r.CustomFix != null) { var cr = r.CustomFix(); cr.Id = it.Id; return cr; }
            // Bancos de dados do navegador abertos podem corromper: só mexe com ele fechado.
            if (r.Browser != null && r.Browser.Running())
                return new FixResult { Id = it.Id, Message = "Feche o " + r.Browser.Name + " e tente novamente." };

            long freed = 0; int deleted = 0, failed = 0;
            foreach (var f in Files(r, ctx))
            {
                if (Fs.DeleteFile(f.Path)) { freed += f.Size; deleted++; } else failed++;
            }
            int regDeleted = 0;
            foreach (var key in r.RegKeys)
            {
                using (var k = Reg.Open(key, true))
                {
                    if (k == null) continue;
                    foreach (var n in k.GetValueNames())
                    {
                        if (n.Length == 0 || (r.RegValueFilter != null && !r.RegValueFilter(n))) continue;
                        try { k.DeleteValue(n, false); regDeleted++; } catch { failed++; }
                    }
                    if (r.RegSubKeys)
                        foreach (var s in k.GetSubKeyNames())
                        {
                            try { k.DeleteSubKeyTree(s, false); regDeleted++; } catch { failed++; }
                        }
                }
            }
            var ok = failed == 0 || deleted + regDeleted > 0;
            var msg = failed == 0 ? "Limpo" : deleted + regDeleted > 0 ? "Limpo parcialmente (" + failed + " itens em uso foram mantidos)" : "Os itens estão em uso e não puderam ser apagados.";
            return new FixResult { Id = it.Id, Ok = ok, Freed = freed, Message = msg };
        }

        static FixResult ClearClipboard()
        {
            var script = "Add-Type -AssemblyName System.Runtime.WindowsRuntime;" +
                         "$null = [Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime];" +
                         "if ([Windows.ApplicationModel.DataTransfer.Clipboard]::ClearHistory()) { 'ok' } else { 'falhou' }";
            var r = Proc.Ps(script, 20000);
            var ok = r.Ok && r.Output.Trim().EndsWith("ok", StringComparison.Ordinal);
            return new FixResult { Ok = ok, Message = ok ? "Histórico da área de transferência limpo" : "O Windows não permitiu limpar o histórico da área de transferência." };
        }
    }
}
