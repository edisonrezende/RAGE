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
        static System.Windows.Forms.Control ui;
        static CoreWebView2 core;
        static readonly ConcurrentDictionary<string, CancellationTokenSource> Running = new ConcurrentDictionary<string, CancellationTokenSource>();
        public static volatile bool UiVisible = true;

        public static void Attach(System.Windows.Forms.Control control, CoreWebView2 webview)
        {
            ui = control;
            core = webview;
            core.WebMessageReceived += OnMessage;
        }

        static void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject msg;
            try { msg = JObject.Parse(e.WebMessageAsJson); }
            catch { return; }

            var cancel = (string)msg["cancel"];
            if (cancel != null)
            {
                if (Running.TryGetValue(cancel, out var c)) c.Cancel();
                return;
            }
            var id = (string)msg["id"];
            var method = (string)msg["method"];
            var p = msg["params"] as JObject;
            var cts = new CancellationTokenSource();
            Running[id] = cts;
            var ctx = new RpcContext { Id = id, Token = cts.Token, ProgressSink = d => Post(new { id, progress = d }) };
            Task.Run(async () =>
            {
                try
                {
                    var r = await Rpc.Invoke(method, p, ctx).ConfigureAwait(false);
                    Post(new { id, ok = true, result = r });
                }
                catch (Exception ex)
                {
                    ex = Rpc.Unwrap(ex);
                    var canceled = ex is OperationCanceledException;
                    if (!(ex is RpcException) && !canceled) Log.Error("RPC " + method, ex);
                    Post(new { id, ok = false, canceled, error = canceled ? "Cancelado" : ex.Message });
                }
                finally { Running.TryRemove(id, out _); cts.Dispose(); }
            });
        }

        public static void Emit(string evt, object data) => Post(new { @event = evt, data });

        static void Post(object o)
        {
            var c = core; var u = ui;
            if (c == null || u == null || u.IsDisposed) return;
            string json;
            try { json = Json.Serialize(o); }
            catch (Exception ex) { Log.Error("Serializar resposta", ex); return; }
            try
            {
                if (!u.IsHandleCreated) return;
                u.BeginInvoke((Action)(() => { try { c.PostWebMessageAsJson(json); } catch { } }));
            }
            catch { }
        }
    }
}
