using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace Rage.Core
{
    /// <summary>Marca um método estático como chamável pela interface: window.rage.call("nome", {params}).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RpcAttribute : Attribute
    {
        public string Name { get; }
        public RpcAttribute(string name) { Name = name; }
    }

    public sealed class RpcException : Exception
    {
        public RpcException(string m) : base(m) { }
    }

    public sealed class RpcContext
    {
        public string Id { get; set; }
        public CancellationToken Token { get; set; }
        public Action<object> ProgressSink { get; set; }
        public void Progress(object data) => ProgressSink?.Invoke(data);
        public static RpcContext None => new RpcContext { Id = "-", Token = CancellationToken.None };
    }

    public static class Rpc
    {
        static Dictionary<string, MethodInfo> map;

        public static void Init()
        {
            map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in typeof(Rpc).Assembly.GetTypes())
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var a = m.GetCustomAttribute<RpcAttribute>();
                    if (a == null) continue;
                    if (map.ContainsKey(a.Name)) throw new InvalidOperationException("RPC duplicado: " + a.Name);
                    map[a.Name] = m;
                }
            Log.Info("RPC registrados: " + map.Count);
        }

        public static IEnumerable<string> Names => map.Keys.OrderBy(k => k);

        public static async Task<object> Invoke(string method, JObject p, RpcContext ctx)
        {
            if (!map.TryGetValue(method ?? "", out var mi)) throw new RpcException("Função desconhecida: " + method);
            p = p ?? new JObject();
            var pars = mi.GetParameters();
            var args = new object[pars.Length];
            for (int i = 0; i < pars.Length; i++)
            {
                var pt = pars[i].ParameterType;
                if (pt == typeof(JObject)) args[i] = p;
                else if (pt == typeof(RpcContext)) args[i] = ctx;
                else if (pt == typeof(CancellationToken)) args[i] = ctx.Token;
                else
                {
                    var tok = p.GetValue(pars[i].Name, StringComparison.OrdinalIgnoreCase);
                    if (tok == null || tok.Type == JTokenType.Null || tok.Type == JTokenType.Undefined)
                        args[i] = pars[i].HasDefaultValue ? pars[i].DefaultValue : (pt.IsValueType ? Activator.CreateInstance(pt) : null);
                    else args[i] = tok.ToObject(pt, Json.Serializer);
                }
            }
            object r;
            try { r = mi.Invoke(null, args); }
            catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
            if (r is Task task)
            {
                await task.ConfigureAwait(false);
                if (mi.ReturnType.IsGenericType) return mi.ReturnType.GetProperty("Result").GetValue(task);
                return null;
            }
            return r;
        }

        public static Exception Unwrap(Exception ex)
        {
            while (true)
            {
                if (ex is TargetInvocationException t && t.InnerException != null) { ex = t.InnerException; continue; }
                if (ex is AggregateException a && a.InnerExceptions.Count == 1) { ex = a.InnerException; continue; }
                return ex;
            }
        }
    }

    /// <summary>Ponte WebView2 <-> C#. Mensagens: {id, method, params} / {cancel:id}. Respostas: {id, ok, result|error} e eventos {event, data}.</summary>
    public static class Bridge
    {
        /// <summary>Uma página conectada: a janela principal ou o painel do mini monitor.</summary>
        sealed class View
        {
            public System.Windows.Forms.Control Ui;
            public CoreWebView2 Core;
            public Func<bool> Visible;
        }

        static volatile View[] views = new View[0];
        static readonly object Gate = new object();
        static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new ConcurrentDictionary<string, CancellationTokenSource>();
        public static volatile bool UiVisible = true;

        /// <summary>Há alguma página na tela para receber telemetria?</summary>
        public static bool AnyVisible => views.Any(v => SafeVisible(v));
        static bool SafeVisible(View v) { try { return v.Visible(); } catch { return false; } }

        public static void Attach(System.Windows.Forms.Control control, CoreWebView2 webview, Func<bool> visible = null)
        {
            var v = new View { Ui = control, Core = webview, Visible = visible ?? (() => UiVisible) };
            lock (Gate) views = views.Concat(new[] { v }).ToArray();
            webview.WebMessageReceived += (s, e) => OnMessage(v, e);
        }

        public static void Detach(CoreWebView2 webview)
        {
            lock (Gate) views = views.Where(v => v.Core != webview).ToArray();
        }

        static void OnMessage(View from, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject msg;
            try { msg = JObject.Parse(e.WebMessageAsJson); }
            catch { return; }

            // Cada página numera as chamadas do seu jeito (r1, r2...): a chave junta a página para não colidir.
            var cancel = (string)msg["cancel"];
            if (cancel != null)
            {
                if (Running.TryGetValue(from.GetHashCode() + ":" + cancel, out var c)) c.Cancel();
                return;
            }
            var id = (string)msg["id"];
            var method = (string)msg["method"];
            var p = msg["params"] as JObject;
            var key = from.GetHashCode() + ":" + id;
            var cts = new CancellationTokenSource();
            Running[key] = cts;
            var ctx = new RpcContext { Id = id, Token = cts.Token, ProgressSink = d => Post(from, new { id, progress = d }) };
            Task.Run(async () =>
            {
                try
                {
                    var r = await Rpc.Invoke(method, p, ctx).ConfigureAwait(false);
                    Post(from, new { id, ok = true, result = r });
                }
                catch (Exception ex)
                {
                    ex = Rpc.Unwrap(ex);
                    var canceled = ex is OperationCanceledException;
                    if (!(ex is RpcException) && !canceled) Log.Error("RPC " + method, ex);
                    Post(from, new { id, ok = false, canceled, error = canceled ? "Cancelado" : ex.Message });
                }
                finally { Running.TryRemove(key, out _); cts.Dispose(); }
            });
        }

        /// <summary>Evento para todas as páginas.</summary>
        public static void Emit(string evt, object data) => Broadcast(new { @event = evt, data }, false);

        /// <summary>Evento só para as páginas que estão na tela (telemetria a cada segundo).</summary>
        public static void EmitVisible(string evt, object data) => Broadcast(new { @event = evt, data }, true);

        static void Broadcast(object o, bool onlyVisible)
        {
            var targets = views;
            if (onlyVisible) targets = targets.Where(SafeVisible).ToArray();
            if (targets.Length == 0) return;
            string json;
            try { json = Json.Serialize(o); }
            catch (Exception ex) { Log.Error("Serializar evento", ex); return; }
            foreach (var v in targets) PostJson(v, json);
        }

        static void Post(View v, object o)
        {
            string json;
            try { json = Json.Serialize(o); }
            catch (Exception ex) { Log.Error("Serializar resposta", ex); return; }
            PostJson(v, json);
        }

        static void PostJson(View v, string json)
        {
            var u = v.Ui; var c = v.Core;
            try
            {
                if (u == null || u.IsDisposed || !u.IsHandleCreated) return;
                u.BeginInvoke((Action)(() => { try { c.PostWebMessageAsJson(json); } catch { } }));
            }
            catch { }
        }
    }
}
