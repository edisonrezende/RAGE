using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Rage.Core
{
    /// <summary>Utilitários de registro com caminhos no formato "HKLM\SOFTWARE\...".</summary>
    public static class Reg
    {
        public static RegistryKey Root(string path, out string sub, RegistryView view = RegistryView.Registry64)
        {
            var i = path.IndexOf('\\');
            var hive = (i < 0 ? path : path.Substring(0, i)).ToUpperInvariant();
            sub = i < 0 ? "" : path.Substring(i + 1);
            RegistryHive h;
            switch (hive)
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": h = RegistryHive.LocalMachine; break;
                case "HKCU": case "HKEY_CURRENT_USER": h = RegistryHive.CurrentUser; break;
                case "HKCR": case "HKEY_CLASSES_ROOT": h = RegistryHive.ClassesRoot; break;
                case "HKU": case "HKEY_USERS": h = RegistryHive.Users; break;
                default: throw new ArgumentException("Hive inválida: " + hive);
            }
            return RegistryKey.OpenBaseKey(h, view);
        }

        public static RegistryKey Open(string path, bool write = false, RegistryView view = RegistryView.Registry64)
        {
            using (var root = Root(path, out var sub, view))
                return string.IsNullOrEmpty(sub) ? root : root.OpenSubKey(sub, write);
        }

        public static RegistryKey Create(string path, RegistryView view = RegistryView.Registry64)
        {
            using (var root = Root(path, out var sub, view)) return root.CreateSubKey(sub, true);
        }

        public static object Get(string path, string name, object def = null, RegistryView view = RegistryView.Registry64)
        {
            try { using (var k = Open(path, false, view)) return k?.GetValue(name, def, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? def; }
            catch { return def; }
        }

        public static int? GetInt(string path, string name)
        {
            var v = Get(path, name);
            if (v == null) return null;
            try { return Convert.ToInt32(v is uint u ? unchecked((int)u) : v); } catch { return null; }
        }

        public static string GetStr(string path, string name) => Get(path, name) as string;

        public static void Set(string path, string name, object value, RegistryValueKind kind, RegistryView view = RegistryView.Registry64)
        {
            using (var k = Create(path, view)) k.SetValue(name, value, kind);
        }

        public static bool DeleteValue(string path, string name, RegistryView view = RegistryView.Registry64)
        {
            using (var k = Open(path, true, view))
            {
                if (k == null || k.GetValue(name) == null && !k.GetValueNames().Contains(name)) return false;
                k.DeleteValue(name, false);
                return true;
            }
        }

        public static bool DeleteKey(string path, RegistryView view = RegistryView.Registry64)
        {
            var i = path.LastIndexOf('\\');
            using (var parent = Open(path.Substring(0, i), true, view))
            {
                if (parent == null) return false;
                parent.DeleteSubKeyTree(path.Substring(i + 1), false);
                return true;
            }
        }

        public static bool KeyExists(string path, RegistryView view = RegistryView.Registry64)
        {
            try { using (var k = Open(path, false, view)) return k != null; } catch { return false; }
        }

        // ---------- Backup em .reg ----------
        public static string FullHive(string path)
        {
            var i = path.IndexOf('\\');
            var h = (i < 0 ? path : path.Substring(0, i)).ToUpperInvariant();
            var rest = i < 0 ? "" : path.Substring(i);
            switch (h)
            {
                case "HKLM": h = "HKEY_LOCAL_MACHINE"; break;
                case "HKCU": h = "HKEY_CURRENT_USER"; break;
                case "HKCR": h = "HKEY_CLASSES_ROOT"; break;
                case "HKU": h = "HKEY_USERS"; break;
            }
            return h + rest;
        }

        /// <summary>Gera o texto .reg de uma chave inteira (recursivo) ou de um único valor.</summary>
        public static void AppendExport(StringBuilder sb, string path, string valueName = null, RegistryView view = RegistryView.Registry64)
        {
            using (var k = Open(path, false, view))
            {
                if (k == null) return;
                if (valueName != null)
                {
                    sb.AppendLine("[" + FullHive(path) + "]");
                    AppendValue(sb, k, valueName);
                    sb.AppendLine();
                    return;
                }
                ExportKey(sb, k, path);
            }
        }

        static void ExportKey(StringBuilder sb, RegistryKey k, string path)
        {
            sb.AppendLine("[" + FullHive(path) + "]");
            foreach (var n in k.GetValueNames()) AppendValue(sb, k, n);
            sb.AppendLine();
            foreach (var s in k.GetSubKeyNames())
            {
                try { using (var c = k.OpenSubKey(s)) if (c != null) ExportKey(sb, c, path + "\\" + s); } catch { }
            }
        }

        static void AppendValue(StringBuilder sb, RegistryKey k, string name)
        {
            object v;
            RegistryValueKind kind;
            try { v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames); kind = k.GetValueKind(name); }
            catch { return; }
            if (v == null) return;
            var key = string.IsNullOrEmpty(name) ? "@" : "\"" + Esc(name) + "\"";
            switch (kind)
            {
                case RegistryValueKind.String: sb.AppendLine(key + "=\"" + Esc((string)v) + "\""); break;
                case RegistryValueKind.DWord: sb.AppendLine(key + "=dword:" + unchecked((uint)Convert.ToInt32(v)).ToString("x8")); break;
                case RegistryValueKind.QWord: sb.AppendLine(key + "=hex(b):" + Hex(BitConverter.GetBytes(Convert.ToInt64(v)))); break;
                case RegistryValueKind.ExpandString: sb.AppendLine(key + "=hex(2):" + Hex(Encoding.Unicode.GetBytes((string)v + "\0"))); break;
                case RegistryValueKind.MultiString: sb.AppendLine(key + "=hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Join("\0", (string[])v) + "\0\0"))); break;
                case RegistryValueKind.Binary: sb.AppendLine(key + "=hex:" + Hex((byte[])v)); break;
                default: break;
            }
        }

        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        static string Hex(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));

        public static string SaveBackup(string label, StringBuilder body)
        {
            var dir = Paths.Ensure(Path.Combine(Paths.Backups, "registro"));
            var f = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + label + ".reg");
            File.WriteAllText(f, "Windows Registry Editor Version 5.00\r\n\r\n" + body, Encoding.Unicode);
            return f;
        }
    }
}
