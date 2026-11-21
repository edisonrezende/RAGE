using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Rage.Monitor
{
    /// <summary>Contadores de desempenho via PDH com caminhos em inglês (funciona em Windows de qualquer idioma).</summary>
    public sealed class Pdh : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr ud, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr ud, out IntPtr c);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
        [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint bufSize, out uint count, IntPtr buf);

        [StructLayout(LayoutKind.Explicit)]
        struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Double; }

        const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

        IntPtr query;
        readonly Dictionary<string, IntPtr> counters = new Dictionary<string, IntPtr>();

        public Pdh() { PdhOpenQuery(null, IntPtr.Zero, out query); }

        public bool Add(string key, string path)
        {
            if (PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) != 0) return false;
            counters[key] = c;
            return true;
        }

        public bool Has(string key) => counters.ContainsKey(key);

        public void Collect() { if (query != IntPtr.Zero) PdhCollectQueryData(query); }

        public double? Get(string key, bool noCap = false)
        {
            if (!counters.TryGetValue(key, out var c)) return null;
            var r = PdhGetFormattedCounterValue(c, PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0), IntPtr.Zero, out var v);
            if (r != 0 || (v.Status != 0 && v.Status != 1)) return null;
            return v.Double;
        }

        /// <summary>Valores de um contador com curinga "(*)" — nome da instância → valor.</summary>
        public Dictionary<string, double> GetArray(string key, bool noCap = true)
        {
            var res = new Dictionary<string, double>();
            if (!counters.TryGetValue(key, out var c)) return res;
            uint size = 0;
            var r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0), ref size, out var count, IntPtr.Zero);
            if ((uint)r != PDH_MORE_DATA || size == 0) return res;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                r = PdhGetFormattedCounterArray(c, PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0), ref size, out count, buf);
                if (r != 0) return res;
                // PDH_FMT_COUNTERVALUE_ITEM_W { LPWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; } = 8 + 16 bytes (x64)
                int itemSize = IntPtr.Size + 16;
                for (int i = 0; i < count; i++)
                {
                    var p = buf + i * itemSize;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p));
                    var status = (uint)Marshal.ReadInt32(p + IntPtr.Size);
                    if (status != 0 && status != 1) continue;
                    var val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p + IntPtr.Size + 8));
                    if (name == null) continue;
                    res[name] = res.TryGetValue(name, out var prev) ? prev + val : val;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
        }
    }
}
