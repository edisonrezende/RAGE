using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Rage.Core
{
    public sealed class ProcResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public bool TimedOut { get; set; }
        public bool Ok => ExitCode == 0 && !TimedOut;
    }

    /// <summary>Executa programas de linha de comando capturando a saída.</summary>
    public static class Proc
    {
        public static readonly string Sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        public static string SysExe(string name) => Path.Combine(Sys32, name);

        static Encoding oem;
        public static Encoding Oem
        {
            get
            {
                if (oem != null) return oem;
                try { oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
                catch { oem = Encoding.Default; }
                return oem;
            }
        }

        public static ProcResult Run(string file, string args, int timeoutMs = 120000, Encoding enc = null, Action<string> onLine = null, CancellationToken ct = default, string workDir = null)
        {
            var psi = new ProcessStartInfo(file, args ?? "")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc ?? Oem,
                StandardErrorEncoding = enc ?? Oem,
                WorkingDirectory = workDir ?? Sys32,
            };
            var outSb = new StringBuilder();
            var errSb = new StringBuilder();
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data == null) return; lock (outSb) outSb.AppendLine(e.Data); try { onLine?.Invoke(e.Data); } catch { } };
                p.ErrorDataReceived += (s, e) => { if (e.Data == null) return; lock (errSb) errSb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                var sw = Stopwatch.StartNew();
                bool timedOut = false;
                while (!p.WaitForExit(200))
                {
                    if (ct.IsCancellationRequested || (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs))
                    {
                        timedOut = !ct.IsCancellationRequested;
                        try { p.Kill(); } catch { }
                        break;
                    }
                }
                try { p.WaitForExit(2000); } catch { }
                ct.ThrowIfCancellationRequested();
                return new ProcResult
                {
                    ExitCode = timedOut ? -1 : SafeExit(p),
                    Output = outSb.ToString(),
                    Error = errSb.ToString(),
                    TimedOut = timedOut,
                };
            }
        }

        static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public static Task<ProcResult> RunAsync(string file, string args, int timeoutMs = 120000, Encoding enc = null, Action<string> onLine = null, CancellationToken ct = default)
            => Task.Run(() => Run(file, args, timeoutMs, enc, onLine, ct));

        /// <summary>Roda um script PowerShell e devolve a saída (UTF-8).</summary>
        public static ProcResult Ps(string script, int timeoutMs = 120000, Action<string> onLine = null, CancellationToken ct = default)
        {
            var full = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" + script;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            var ps = Path.Combine(Sys32, @"WindowsPowerShell\v1.0\powershell.exe");
            return Run(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeoutMs, Encoding.UTF8, onLine, ct);
        }

        /// <summary>Roda PowerShell e converte o resultado do pipeline para JSON.</summary>
        public static JToken PsJson(string script, int timeoutMs = 120000, CancellationToken ct = default)
        {
            var r = Ps("$__r = @(" + script + "); if ($__r.Count -eq 0) { '[]' } else { ConvertTo-Json -InputObject $__r -Depth 5 -Compress }", timeoutMs, null, ct);
            if (!r.Ok && string.IsNullOrWhiteSpace(r.Output)) throw new RpcException(CleanPsError(r.Error));
            var txt = r.Output.Trim();
            if (txt.Length == 0) return new JArray();
            try { return JToken.Parse(txt); }
            catch { throw new RpcException("Resposta inesperada do PowerShell"); }
        }

        public static string CleanPsError(string err)
        {
            if (string.IsNullOrWhiteSpace(err)) return "Falha ao executar comando do sistema";
            var line = err.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
            var i = line.IndexOf(" : ", StringComparison.Ordinal);
            return (i > 0 && i < 60 ? line.Substring(i + 3) : line).Trim();
        }

        /// <summary>Abre algo pelo shell (URL, pasta, ms-settings:).</summary>
        public static void Open(string target, string args = null)
        {
            Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true });
        }
    }
}
