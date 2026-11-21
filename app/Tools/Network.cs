using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rage.Core;

namespace Rage.Tools
{
    /// <summary>Rede: adaptadores, comparativo de DNS, troca de DNS, ping e limpeza do cache DNS.</summary>
    public static class Network
    {
        public sealed class DnsPreset { public string Id; public string Name; public string Primary; public string Secondary; public string Primary6; public string Secondary6; }

        public static readonly List<DnsPreset> Presets = new List<DnsPreset>
        {
            new DnsPreset { Id = "cloudflare", Name = "Cloudflare", Primary = "1.1.1.1", Secondary = "1.0.0.1", Primary6 = "2606:4700:4700::1111", Secondary6 = "2606:4700:4700::1001" },
            new DnsPreset { Id = "google", Name = "Google", Primary = "8.8.8.8", Secondary = "8.8.4.4", Primary6 = "2001:4860:4860::8888", Secondary6 = "2001:4860:4860::8844" },
            new DnsPreset { Id = "quad9", Name = "Quad9", Primary = "9.9.9.9", Secondary = "149.112.112.112", Primary6 = "2620:fe::fe", Secondary6 = "2620:fe::9" },
            new DnsPreset { Id = "opendns", Name = "OpenDNS", Primary = "208.67.222.222", Secondary = "208.67.220.220", Primary6 = "2620:119:35::35", Secondary6 = "2620:119:53::53" },
            new DnsPreset { Id = "adguard", Name = "AdGuard (bloqueia anúncios)", Primary = "94.140.14.14", Secondary = "94.140.15.15", Primary6 = "2a10:50c0::ad1:ff", Secondary6 = "2a10:50c0::ad2:ff" },
        };

        public static string TypeName(NetworkInterfaceType t)
        {
            switch (t)
            {
                case NetworkInterfaceType.Wireless80211: return "Wi-Fi";
                case NetworkInterfaceType.Ethernet: case NetworkInterfaceType.GigabitEthernet: case NetworkInterfaceType.FastEthernetT: case NetworkInterfaceType.FastEthernetFx: case NetworkInterfaceType.Ethernet3Megabit: return "Ethernet";
                case NetworkInterfaceType.Ppp: return "PPP";
                case NetworkInterfaceType.Wwanpp: case NetworkInterfaceType.Wwanpp2: return "Celular";
                case NetworkInterfaceType.Tunnel: return "Túnel";
                default: return "Outro";
            }
        }

        public static string Mac(NetworkInterface ni)
        {
            try
            {
                var b = ni.GetPhysicalAddress().GetAddressBytes();
                return b.Length == 0 ? null : string.Join(":", b.Select(x => x.ToString("X2")));
            }
            catch { return null; }
        }

        static bool IsVirtual(NetworkInterface ni)
        {
            var d = (ni.Description + " " + ni.Name).ToLowerInvariant();
            return d.Contains("virtual") || d.Contains("hyper-v") || d.Contains("vmware") || d.Contains("virtualbox") || d.Contains("tap-") || d.Contains("wintun") ||
                   d.Contains("wireguard") || d.Contains("vethernet") || d.Contains("loopback") || d.Contains("wan miniport") || d.Contains("zerotier") || d.Contains("tailscale") || d.Contains("hamachi");
        }

        // ---------- Wi-Fi (wlanapi) ----------
        [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint ver, IntPtr reserved, out uint negotiated, out IntPtr handle);
        [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, out int valueType);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

        /// <summary>GUID da interface → (SSID, sinal 0–100).</summary>
        static Dictionary<Guid, (string ssid, int signal)> WifiInfo()
        {
            var res = new Dictionary<Guid, (string, int)>();
            IntPtr h = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out h) != 0) return res;
                if (WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return res;
                int count = Marshal.ReadInt32(list, 0);
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, InterfaceInfo[] (GUID 16 + WCHAR[256] 512 + state 4 = 532 bytes)
                for (int i = 0; i < count; i++)
                {
                    var p = new IntPtr(list.ToInt64() + 8 + i * 532);
                    var g = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
                    var state = Marshal.ReadInt32(p, 16 + 512);
                    if (state != 1) continue; // wlan_interface_state_connected
                    if (WlanQueryInterface(h, ref g, 7, IntPtr.Zero, out _, out var data, out _) != 0 || data == IntPtr.Zero) continue; // wlan_intf_opcode_current_connection
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: isState 4, wlanConnectionMode 4, strProfileName WCHAR[256] 512, WLAN_ASSOCIATION_ATTRIBUTES:
                        //   DOT11_SSID { uSSIDLength 4, ucSSID[32] } , dot11BssType 4, dot11Bssid 6 (+2), dot11PhyType 4, uDot11PhyIndex 4, wlanSignalQuality 4, ...
                        int baseOff = 8 + 512;
                        int len = Marshal.ReadInt32(data, baseOff);
                        var bytes = new byte[Math.Max(0, Math.Min(32, len))];
                        Marshal.Copy(new IntPtr(data.ToInt64() + baseOff + 4), bytes, 0, bytes.Length);
                        var ssid = Encoding.UTF8.GetString(bytes);
                        int sig = Marshal.ReadInt32(data, baseOff + 36 + 4 + 8 + 4 + 4);
                        if (string.IsNullOrEmpty(ssid)) ssid = Marshal.PtrToStringUni(new IntPtr(data.ToInt64() + 8));
                        res[g] = (string.IsNullOrEmpty(ssid) ? null : ssid, Math.Max(0, Math.Min(100, sig)));
                    }
                    finally { WlanFreeMemory(data); }
                }
            }
            catch { }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (h != IntPtr.Zero) WlanCloseHandle(h, IntPtr.Zero);
            }
            return res;
        }

        public static List<object> Adapters()
        {
            var wifi = WifiInfo();
            var list = new List<(int order, object item)>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var props = ni.GetIPProperties();
                    var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))?.ToString();
                    var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal && !a.IsIPv6LinkLocal))
                                   .Select(a => a.ToString()).Distinct().ToList();
                    var up = ni.OperationalStatus == OperationalStatus.Up;
                    Guid.TryParse(ni.Id, out var gid);
                    (string ssid, int signal) w = default;
                    var isWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    if (isWifi) wifi.TryGetValue(gid, out w);
                    var virt = IsVirtual(ni);
                    var item = new
                    {
                        id = ni.Id,
                        name = ni.Name,
                        desc = ni.Description,
                        type = TypeName(ni.NetworkInterfaceType),
                        status = up ? "Conectado" : ni.OperationalStatus == OperationalStatus.Down ? "Desconectado" : "Indisponível",
                        connected = up,
                        ip,
                        gateway = gw,
                        dns,
                        dnsStatic = IsStaticDns(ni.Id),
                        mac = Mac(ni),
                        speedMbps = up && ni.Speed > 0 && ni.Speed < long.MaxValue ? Math.Round(ni.Speed / 1e6) : (double?)null,
                        ssid = isWifi && up ? w.ssid : null,
                        signal = isWifi && up && w.signal > 0 ? w.signal : (int?)null,
                        @virtual = virt,
                    };
                    list.Add(((up ? 0 : 2) + (virt ? 1 : 0) + (gw != null ? 0 : 1), item));
                }
                catch { }
            }
            return list.OrderBy(x => x.order).Select(x => x.item).ToList();
        }

        static bool IsStaticDns(string id)
        {
            var ns = Reg.GetStr(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + id, "NameServer");
            return !string.IsNullOrWhiteSpace(ns);
        }

        /// <summary>Adaptador principal (conectado, com gateway).</summary>
        static NetworkInterface Primary()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderBy(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) ? 0 : 1)
                .ThenBy(n => IsVirtual(n) ? 1 : 0)
                .FirstOrDefault();
        }

        // ---------- Comparativo de DNS ----------
        static readonly string[] Domains = { "google.com", "youtube.com", "facebook.com", "instagram.com", "whatsapp.net", "microsoft.com", "wikipedia.org", "amazon.com", "netflix.com", "steampowered.com", "twitch.tv", "discord.com" };

        static byte[] BuildQuery(ushort id, string domain)
        {
            var b = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
            foreach (var label in domain.Split('.')) { b.Add((byte)label.Length); b.AddRange(Encoding.ASCII.GetBytes(label)); }
            b.AddRange(new byte[] { 0, 0, 1, 0, 1 });
            return b.ToArray();
        }

        /// <summary>Tempo de uma consulta DNS (ms) ou null se não respondeu.</summary>
        static double? QueryMs(IPAddress server, string domain, int timeoutMs, Random rnd)
        {
            try
            {
                using (var s = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.ReceiveTimeout = timeoutMs;
                    var id = (ushort)rnd.Next(1, 65535);
                    var q = BuildQuery(id, domain);
                    var ep = new IPEndPoint(server, 53);
                    var buf = new byte[1500];
                    var sw = Stopwatch.StartNew();
                    s.SendTo(q, ep);
                    while (sw.ElapsedMilliseconds < timeoutMs)
                    {
                        EndPoint from = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                        int n = s.ReceiveFrom(buf, ref from);
                        if (n >= 12 && buf[0] == (byte)(id >> 8) && buf[1] == (byte)id) return sw.Elapsed.TotalMilliseconds;
                    }
                }
            }
            catch { }
            return null;
        }

        static double? Measure(string ip, CancellationToken ct)
        {
            if (!IPAddress.TryParse(ip, out var addr)) return null;
            var rnd = new Random(ip.GetHashCode());
            QueryMs(addr, "example.com", 1000, rnd); // aquecimento
            var times = new List<double>();
            int fails = 0;
            foreach (var d in Domains)
            {
                if (ct.IsCancellationRequested) break;
                var t = QueryMs(addr, d, 1000, rnd);
                if (t.HasValue) times.Add(t.Value); else fails++;
            }
            if (times.Count == 0 || fails > Domains.Length / 2) return null;
            // Média aparando o pior resultado (picos isolados)
            var sorted = times.OrderBy(x => x).ToList();
            if (sorted.Count > 4) sorted.RemoveAt(sorted.Count - 1);
            return Math.Round(sorted.Average() + fails * 1000.0 / Domains.Length, 1);
        }

        public static List<object> DnsBenchmark(RpcContext ctx)
        {
            var prim = Primary();
            var current = prim?.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList() ?? new List<string>();
            var isStatic = prim != null && IsStaticDns(prim.Id);
            var entries = Presets.Select(p => (id: p.Id, name: p.Name, primary: p.Primary, secondary: p.Secondary, current: current.Count > 0 && current[0] == p.Primary)).ToList();
            if (current.Count > 0 && !entries.Any(e => e.current))
                entries.Insert(0, (isStatic ? "current" : "auto", isStatic ? "DNS atual (manual)" : "Automático (provedor/roteador)", current[0], current.Count > 1 ? current[1] : null, true));
            var results = new (string id, string name, string primary, string secondary, bool current, double? ms)[entries.Count];
            int done = 0;
            ctx.Progress(new { done, total = entries.Count });
            Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = entries.Count }, i =>
            {
                var e = entries[i];
                var ms = Measure(e.primary, ctx.Token);
                results[i] = (e.id, e.name, e.primary, e.secondary, e.current, ms);
                var d = Interlocked.Increment(ref done);
                ctx.Progress(new { done = d, total = entries.Count, id = e.id, ms });
            });
            ctx.Token.ThrowIfCancellationRequested();
            return results.OrderBy(r => r.ms ?? double.MaxValue).Select(r => (object)new { r.id, r.name, r.primary, r.secondary, r.ms, r.current }).ToList();
        }

        // ---------- Trocar DNS ----------
        public static object SetDns(string adapter, string preset)
        {
            ToolsUtil.RequireAdmin("Trocar o DNS");
            var ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, adapter, StringComparison.OrdinalIgnoreCase))
                  ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Name, adapter, StringComparison.OrdinalIgnoreCase))
                  ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Description, adapter, StringComparison.OrdinalIgnoreCase))
                  ?? (string.IsNullOrEmpty(adapter) ? Primary() : null);
            if (ni == null) throw new RpcException("Adaptador de rede não encontrado.");
            var name = ni.Name.Replace("\"", "");
            var netsh = Proc.SysExe("netsh.exe");
            DnsPreset p = null;
            if (preset != "auto")
            {
                p = Presets.FirstOrDefault(x => x.Id == preset);
                if (p == null) throw new RpcException("Opção de DNS desconhecida.");
            }
            ProcResult r;
            if (p == null)
            {
                r = Proc.Run(netsh, "interface ipv4 set dnsservers name=\"" + name + "\" source=dhcp", 30000);
                Proc.Run(netsh, "interface ipv6 set dnsservers name=\"" + name + "\" source=dhcp", 30000);
            }
            else
            {
                r = Proc.Run(netsh, "interface ipv4 set dnsservers name=\"" + name + "\" source=static address=" + p.Primary + " register=primary validate=no", 30000);
                if (r.ExitCode == 0) Proc.Run(netsh, "interface ipv4 add dnsservers name=\"" + name + "\" address=" + p.Secondary + " index=2 validate=no", 30000);
                var has6 = ni.Supports(NetworkInterfaceComponent.IPv6) && ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal);
                if (r.ExitCode == 0 && has6)
                {
                    var r6 = Proc.Run(netsh, "interface ipv6 set dnsservers name=\"" + name + "\" source=static address=" + p.Primary6 + " register=primary validate=no", 30000);
                    if (r6.ExitCode == 0) Proc.Run(netsh, "interface ipv6 add dnsservers name=\"" + name + "\" address=" + p.Secondary6 + " index=2 validate=no", 30000);
                }
            }
            if (r.ExitCode != 0) throw new RpcException("Não foi possível trocar o DNS: " + (ToolsUtil.LastMeaningful(r.Output) ?? "código " + r.ExitCode));
            Rage.Boost.BoostService.FlushDns();
            var label = p == null ? "automático" : p.Name;
            Activity.Add("info", "dns", "DNS alterado para " + label, ni.Name);
            return new { ok = true, adapter = ni.Name, preset = preset ?? "auto", message = "DNS de " + ni.Name + " alterado para " + label + "." };
        }

        // ---------- Ping ----------
        public static object PingHost(string host, int? count, RpcContext ctx)
        {
            if (string.IsNullOrWhiteSpace(host)) host = "1.1.1.1";
            host = host.Trim();
            if (host.Contains("://")) { try { host = new Uri(host).Host; } catch { } }
            var n = Math.Max(1, Math.Min(count ?? 10, 100));
            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                try { addr = Dns.GetHostAddresses(host).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First(); }
                catch { throw new RpcException("Não foi possível encontrar o endereço \"" + host + "\"."); }
            }
            var times = new List<double>();
            int lost = 0;
            using (var ping = new Ping())
            {
                for (int i = 0; i < n; i++)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    double? ms = null;
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var rep = ping.Send(addr, 2000);
                        if (rep.Status == IPStatus.Success) ms = rep.RoundtripTime > 0 ? rep.RoundtripTime : Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                    }
                    catch { }
                    if (ms.HasValue) times.Add(ms.Value); else lost++;
                    ctx.Progress(new { seq = i + 1, total = n, ms, ok = ms.HasValue });
                    if (i < n - 1) ctx.Token.WaitHandle.WaitOne(300);
                }
            }
            double? jitter = null;
            if (times.Count >= 2)
            {
                double sum = 0;
                for (int i = 1; i < times.Count; i++) sum += Math.Abs(times[i] - times[i - 1]);
                jitter = Math.Round(sum / (times.Count - 1), 1);
            }
            return new
            {
                host, ip = addr.ToString(), sent = n, received = times.Count,
                min = times.Count > 0 ? times.Min() : (double?)null,
                avg = times.Count > 0 ? Math.Round(times.Average(), 1) : (double?)null,
                max = times.Count > 0 ? times.Max() : (double?)null,
                loss = Math.Round(lost * 100.0 / n, 1),
                jitter,
            };
        }

        // ---------- RPC ----------
        [Rpc("net.adapters")]
        public static object AdaptersRpc() => Adapters();

        [Rpc("net.dnsBenchmark")]
        public static object DnsBenchmarkRpc(RpcContext ctx) => DnsBenchmark(ctx);

        [Rpc("net.setDns")]
        public static object SetDnsRpc(string adapter, string preset) => SetDns(adapter, preset ?? "auto");

        [Rpc("net.ping")]
        public static object PingRpc(string host, int? count, RpcContext ctx) => PingHost(host, count, ctx);

        [Rpc("net.flushDns")]
        public static object FlushDnsRpc()
        {
            var ok = Rage.Boost.BoostService.FlushDns();
            if (!ok)
            {
                var r = Proc.Run(Proc.SysExe("ipconfig.exe"), "/flushdns", 20000);
                ok = r.ExitCode == 0;
            }
            if (ok) Activity.Add("info", "dns", "Cache DNS limpo", null);
            return new { ok };
        }
    }
}
