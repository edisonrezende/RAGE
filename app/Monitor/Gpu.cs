using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Rage.Monitor
{
    /// <summary>Adaptadores de vídeo via D3DKMT (mesma fonte do Gerenciador de Tarefas): nome, LUID, tipo e dados de desempenho.</summary>
    public static class D3dkmt
    {
        [StructLayout(LayoutKind.Sequential)]
        struct ADAPTERINFO { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int bPrecise; }
        [StructLayout(LayoutKind.Sequential)]
        struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)]
        struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint DataSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct CLOSEADAPTER { public uint hAdapter; }

        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 p);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER p);

        const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8, KMTQAITYPE_ADAPTERTYPE = 15, KMTQAITYPE_ADAPTERPERFDATA = 62, KMTQAITYPE_ADAPTERPERFDATA_CAPS = 63;

        public sealed class Adapter
        {
            public uint Handle;
            public string Luid;          // formato do contador PDH: 0x00000000_0x0000D1F0
            public string Name;
            public bool Software, Integrated, Discrete, RenderSupported;
            public double? MaxTemp;
        }

        public sealed class PerfData { public double? TempC; public double? FanRpm; public double? PowerPct; public double? MemClockMHz; }

        public static List<Adapter> Enumerate()
        {
            var list = new List<Adapter>();
            var e = new ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list;
            int sz = Marshal.SizeOf(typeof(ADAPTERINFO));
            var buf = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
            try
            {
                e.pAdapters = buf;
                if (D3DKMTEnumAdapters2(ref e) != 0) return list;
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var ai = (ADAPTERINFO)Marshal.PtrToStructure(buf + i * sz, typeof(ADAPTERINFO));
                    var a = new Adapter { Handle = ai.hAdapter, Luid = "0x" + ai.LuidHigh.ToString("X8") + "_0x" + ai.LuidLow.ToString("X8") };
                    a.Name = QueryName(ai.hAdapter);
                    var type = QueryUInt(ai.hAdapter, KMTQAITYPE_ADAPTERTYPE);
                    if (type.HasValue)
                    {
                        var t = type.Value;
                        a.RenderSupported = (t & 1) != 0;
                        a.Software = (t & 4) != 0;
                        a.Discrete = (t & 16) != 0;
                        a.Integrated = (t & 32) != 0;
                    }
                    a.MaxTemp = QueryCapsMaxTemp(ai.hAdapter);
                    list.Add(a);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        static string QueryName(uint h)
        {
            const int size = 260 * 2 * 4;
            var p = Marshal.AllocHGlobal(size);
            try
            {
                var q = new QUERYADAPTERINFO { hAdapter = h, Type = KMTQAITYPE_ADAPTERREGISTRYINFO, pData = p, DataSize = size };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                return Marshal.PtrToStringUni(p)?.Trim();
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        static uint? QueryUInt(uint h, int type)
        {
            var p = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.WriteInt64(p, 0); Marshal.WriteInt64(p + 8, 0);
                var q = new QUERYADAPTERINFO { hAdapter = h, Type = type, pData = p, DataSize = 4 };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                return (uint)Marshal.ReadInt32(p);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        static double? QueryCapsMaxTemp(uint h)
        {
            // D3DKMT_ADAPTER_PERFDATACAPS { UINT32 PhysicalAdapterIndex; ULONGLONG MaxMemoryBandwidth; ULONGLONG MaxPCIEBandwidth; ULONG MaxFanRPM; ULONG TemperatureMax; ULONG TemperatureWarning; }
            var p = Marshal.AllocHGlobal(40);
            try
            {
                for (int i = 0; i < 40; i += 8) Marshal.WriteInt64(p + i, 0);
                var q = new QUERYADAPTERINFO { hAdapter = h, Type = KMTQAITYPE_ADAPTERPERFDATA_CAPS, pData = p, DataSize = 40 };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                var tmax = (uint)Marshal.ReadInt32(p + 28);
                return tmax > 0 ? tmax / 10.0 : (double?)null;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Temperatura/ventoinha/energia relatadas pelo driver (WDDM 2.4+). Não acorda a GPU dedicada.</summary>
        public static PerfData QueryPerf(uint h)
        {
            // D3DKMT_ADAPTER_PERFDATA: idx(4)+pad(4) | MemFreq(8) MaxMemFreq(8) MaxMemFreqOC(8) MemBW(8) PCIeBW(8) | FanRPM(4) Power(4) Temperature(4) PowerStateOverride(1)
            const int size = 64;
            var p = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p + i, 0);
                var q = new QUERYADAPTERINFO { hAdapter = h, Type = KMTQAITYPE_ADAPTERPERFDATA, pData = p, DataSize = size };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                var memFreq = Marshal.ReadInt64(p + 8);
                var fan = (uint)Marshal.ReadInt32(p + 48);
                var power = (uint)Marshal.ReadInt32(p + 52);
                var temp = (uint)Marshal.ReadInt32(p + 56);
                return new PerfData
                {
                    TempC = temp > 0 && temp < 2000 ? temp / 10.0 : (double?)null,
                    FanRpm = fan > 0 ? fan : (double?)null,
                    PowerPct = power > 0 ? power / 10.0 : (double?)null,
                    MemClockMHz = memFreq > 0 ? memFreq / 1e6 : (double?)null,
                };
            }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    /// <summary>Estado de energia dos dispositivos de vídeo (D0 = ativo, D3 = desligado) sem acordá-los.</summary>
    public static class DevicePower
    {
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint idx, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint regType, byte[] buf, uint size, out uint req);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        static Guid DisplayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        const uint DIGCF_PRESENT = 2, SPDRP_DEVICEDESC = 0, SPDRP_FRIENDLYNAME = 0xC, SPDRP_DEVICE_POWER_DATA = 0x1E;

        /// <summary>Nome do dispositivo → estado mais recente (1=D0 ... 4=D3).</summary>
        public static Dictionary<string, int> DisplayStates()
        {
            var res = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var set = SetupDiGetClassDevs(ref DisplayClass, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return res;
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    var name = Str(set, ref d, SPDRP_FRIENDLYNAME) ?? Str(set, ref d, SPDRP_DEVICEDESC);
                    var buf = new byte[64];
                    if (name != null && SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_DEVICE_POWER_DATA, out _, buf, (uint)buf.Length, out _))
                        res[name] = BitConverter.ToInt32(buf, 4);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return res;
        }

        static string Str(IntPtr set, ref SP_DEVINFO_DATA d, uint prop)
        {
            var buf = new byte[1024];
            if (!SetupDiGetDeviceRegistryProperty(set, ref d, prop, out _, buf, (uint)buf.Length, out var req)) return null;
            return System.Text.Encoding.Unicode.GetString(buf, 0, (int)Math.Max(0, req - 2)).TrimEnd('\0').Trim();
        }
    }
}
