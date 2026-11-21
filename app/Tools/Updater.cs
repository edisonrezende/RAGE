using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Programas desatualizados via winget (saída localizada: colunas pela linha de traços).</summary>
    public static class Updater
    {
        public sealed class Update
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public string Available { get; set; }
            public string Source { get; set; }
        }

        static readonly object Gate = new object();
        static List<Update> cache;
        static DateTime cacheAt;

        public static string WingetPath()
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            return File.Exists(alias) ? alias : "winget";
        }

        static ProcResult RunWinget(string args, int timeoutMs, Action<string> onLine, System.Threading.CancellationToken ct)
        {
            try { return Proc.Run(WingetPath(), args, timeoutMs, Encoding.UTF8, onLine, ct); }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new RpcException("O winget (Instalador de Aplicativos) não está disponível. Instale o \"Instalador de Aplicativos\" pela Microsoft Store.");
            }
        }

        public static List<Update> List(bool force, System.Threading.CancellationToken ct)
        {
            lock (Gate)
                if (!force && cache != null && (DateTime.Now - cacheAt).TotalSeconds < 120) return cache.ToList();
            var r = RunWinget("upgrade --include-unknown --accept-source-agreements --disable-interactivity", 180000, null, ct);
            if (r.TimedOut) throw new RpcException("O winget demorou demais para responder. Tente de novo.");
            var list = Parse(r.Output);
            if (list.Count == 0 && r.ExitCode != 0 && !(r.Output ?? "").Contains("---"))
            {
                var msg = ToolsUtil.LastMeaningful(r.Output) ?? ToolsUtil.LastMeaningful(r.Error);
                // Sem atualizações o winget também sai com código ≠ 0 em algumas versões; só é erro se não houver tabela nem mensagem conhecida
                if (r.ExitCode != unchecked((int)0x8A15002B)) Log.Warn("winget upgrade: código " + r.ExitCode + " " + msg);
            }
            lock (Gate) { cache = list; cacheAt = DateTime.Now; }
            return list.ToList();
        }

        // ---------- Parser ----------
        static bool IsWide(int cp) =>
            (cp >= 0x1100 && cp <= 0x115F) || (cp >= 0x2E80 && cp <= 0xA4CF && cp != 0x303F) || (cp >= 0xAC00 && cp <= 0xD7A3) ||
            (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0xFE30 && cp <= 0xFE4F) || (cp >= 0xFF00 && cp <= 0xFF60) || (cp >= 0xFFE0 && cp <= 0xFFE6) ||
            (cp >= 0x1F300 && cp <= 0x1FAFF) || (cp >= 0x20000 && cp <= 0x3FFFD);

        /// <summary>Converte a linha em células de exibição (caracteres largos ocupam 2), como o winget calcula as colunas.</summary>
        static List<string> Cells(string line)
        {
            var cells = new List<string>(line.Length);
            for (int i = 0; i < line.Length; i++)
            {
                int cp; string s;
                if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length) { cp = char.ConvertToUtf32(line[i], line[i + 1]); s = line.Substring(i, 2); i++; }
                else { cp = line[i]; s = line[i].ToString(); }
                cells.Add(s);
                if (IsWide(cp)) cells.Add("");
            }
            return cells;
        }

        static string Slice(List<string> cells, int start, int end)
        {
            if (start >= cells.Count) return "";
            end = Math.Min(end, cells.Count);
            var sb = new StringBuilder();
            for (int i = start; i < end; i++) sb.Append(cells[i]);
            return sb.ToString().Trim();
        }

        public static List<Update> Parse(string output)
        {
            var res = new List<Update>();
            if (string.IsNullOrEmpty(output)) return res;
            var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => { var i = l.LastIndexOf('\r'); return (i >= 0 ? l.Substring(i + 1) : l).TrimEnd(); }).ToList();
            for (int i = 1; i < lines.Count; i++)
            {
                if (!Regex.IsMatch(lines[i], @"^-{10,}\s*$")) continue;
                var header = lines[i - 1];
                // Começo de cada coluna = início de cada palavra do cabeçalho
                var hc = Cells(header);
                var starts = new List<int>();
                for (int c = 0; c < hc.Count; c++)
                    if (hc[c] != " " && hc[c] != "" && (c == 0 || hc[c - 1] == " ")) starts.Add(c);
                if (starts.Count > 5)
                {
                    // Cabeçalho com palavras compostas: só valem inícios após 2+ espaços
                    var s2 = new List<int> { starts[0] };
                    for (int c = 1; c < starts.Count; c++)
                        if (starts[c] >= 2 && hc[starts[c] - 1] == " " && hc[starts[c] - 2] == " ") s2.Add(starts[c]);
                    starts = s2;
                }
                if (starts.Count < 4) continue;
                for (int j = i + 1; j < lines.Count; j++)
                {
                    var l = lines[j];
                    if (string.IsNullOrWhiteSpace(l)) break;
                    if (Regex.IsMatch(l, @"^-{10,}\s*$")) break;
                    var cells = Cells(l);
                    if (cells.Count <= starts[2]) break; // rodapé ("19 atualizações disponíveis.")
                    if (starts[1] > 0 && starts[1] - 1 < cells.Count && cells[starts[1] - 1] != " ") break;
                    var name = Slice(cells, 0, starts[1]);
                    var id = Slice(cells, starts[1], starts[2]);
                    var ver = Slice(cells, starts[2], starts[3]);
                    var avail = Slice(cells, starts[3], starts.Count > 4 ? starts[4] : int.MaxValue);
                    var src = starts.Count > 4 ? Slice(cells, starts[4], int.MaxValue) : null;
                    if (id.Length == 0 || id.Contains(" ") || avail.Length == 0) continue;
                    if (res.Any(x => x.Id == id)) continue;
                    res.Add(new Update { Id = id, Name = name.TrimEnd('…').Trim(), Version = ver, Available = avail, Source = string.IsNullOrEmpty(src) ? null : src });
                }
            }
            return res;
        }

        // ---------- Instalar ----------
        public static List<object> Install(List<string> ids, RpcContext ctx)
        {
            if (ids == null || ids.Count == 0) throw new RpcException("Nenhum programa selecionado.");
            var results = new List<object>();
            int okCount = 0;
            var names = new List<string>();
            List<Update> known;
            lock (Gate) known = cache?.ToList() ?? new List<Update>();
            foreach (var id in ids.Distinct())
            {
                ctx.Token.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(id ?? "", @"^[\w\.\-+]+$")) { results.Add(new { id, ok = false, message = "Identificador inválido." }); continue; }
                var nm = known.FirstOrDefault(k => k.Id == id)?.Name ?? id;
                ctx.Progress(new { id, line = "Atualizando " + nm + "..." });
                string lastLine = null;
                var r = RunWinget("upgrade --id " + id + " -e --silent --include-unknown --accept-package-agreements --accept-source-agreements --disable-interactivity", 1800000, l =>
                {
                    var c = ToolsUtil.CleanLine(l);
                    if (c == null) return;
                    if (c.Any(char.IsLetter)) lastLine = c.Trim();
                    ctx.Progress(new { id, line = c });
                }, ctx.Token);
                var ok = r.ExitCode == 0;
                string message;
                if (ok) { message = "Atualizado."; okCount++; names.Add(nm); }
                else if (r.TimedOut) message = "Tempo esgotado.";
                else message = (lastLine ?? "Falha") + " (código 0x" + r.ExitCode.ToString("X8") + ")";
                results.Add(new { id, ok, message });
            }
            lock (Gate) cache = null;
            if (okCount > 0) Activity.Add("fix", "system_update", okCount + (okCount == 1 ? " programa atualizado" : " programas atualizados"), string.Join(", ", names.Take(5)));
            return results;
        }

        // ---------- RPC ----------
        [Rpc("updates.list")]
        public static object ListRpc(RpcContext ctx, bool force = true) => List(force, ctx.Token);

        [Rpc("updates.install")]
        public static object InstallRpc(List<string> ids, RpcContext ctx) => Install(ids, ctx);
    }

    /// <summary>Verificação inteligente: programas com atualização disponível (winget).</summary>
    public sealed class UpdatesScanner : ICareScanner
    {
        public string Category => "updates";
        public string Title => "Programas desatualizados";
        public string Description => "Programas instalados com versão nova disponível (via winget).";
        public string Icon => "system_update";
        public int Order => 100;
        public bool DefaultOn => false;

        public List<CareItem> Scan(RpcContext ctx)
        {
            var list = Updater.List(false, ctx.Token);
            return list.Select(u =>
            {
                var unknown = string.IsNullOrEmpty(u.Version) || u.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || u.Version.Equals("Desconhecido", StringComparison.OrdinalIgnoreCase);
                return new CareItem
                {
                    Id = "updates." + u.Id,
                    Category = "updates",
                    Group = u.Source == "msstore" ? "Microsoft Store" : "Programas",
                    Title = u.Name,
                    Detail = (unknown ? "Versão instalada desconhecida" : u.Version) + " → " + u.Available,
                    Current = unknown ? null : u.Version,
                    Recommended = u.Available,
                    Selected = !unknown,
                    Risk = "safe",
                    Note = unknown ? "Não foi possível ler a versão instalada; o winget pode reinstalar o programa." : "O programa pode ser fechado durante a atualização.",
                    Status = "warn",
                    Fixable = true,
                    Tag = u.Id,
                };
            }).ToList();
        }

        public List<FixResult> Fix(List<CareItem> items, RpcContext ctx)
        {
            var ids = items.Select(i => i.Tag as string ?? i.Id.Substring("updates.".Length)).ToList();
            var map = items.ToDictionary(i => i.Tag as string ?? i.Id.Substring("updates.".Length), i => i.Id);
            var res = Updater.Install(ids, ctx);
            return res.Select(o =>
            {
                var j = Newtonsoft.Json.Linq.JObject.FromObject(o);
                var id = (string)j["id"];
                return new FixResult { Id = map.TryGetValue(id, out var cid) ? cid : "updates." + id, Ok = (bool)j["ok"], Message = (string)j["message"] };
            }).ToList();
        }
    }
}
