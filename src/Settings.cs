using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MouseMeter
{
    /// <summary>Device classes we can read batteries from.</summary>
    public static class DeviceKind
    {
        public const string Mouse = "mouse";
        public const string Keyboard = "keyboard";
        public const string Headset = "headset";

        public static readonly string[] All = new string[] { Mouse, Keyboard, Headset };

        public static string Label(string kind)
        {
            if (kind == Keyboard) return "键盘";
            if (kind == Headset) return "耳机";
            return "鼠标";
        }

        public static bool IsKnown(string kind) { return Array.IndexOf(All, kind) >= 0; }

        /// <summary>A device that never reported a level still gets a class, so the badge must not default to "鼠标".</summary>
        public static string LabelOrEmpty(string kind)
        {
            return IsKnown(kind) ? Label(kind) : "";
        }
    }

    /// <summary>
    /// Persistent preferences. Serialised by a tiny hand written JSON reader/writer so the
    /// app stays dependency free (no System.Web.Extensions, no Newtonsoft).
    /// </summary>
    public class Settings
    {
        // ---- polling ----
        public int IntervalSec = 60;
        public int LowThreshold = 20;
        public bool NotifyLow = true;
        public bool AdaptivePolling = true;
        public bool StartMinimized = true;

        // ---- sources ----
        public bool UseHid = true;             // proprietary dongle protocol (mouse)
        public bool UseBle = true;             // standard BLE battery over the HID stack
        public bool UsePnp = true;             // generic Windows battery (HFP headset, BTHENUM/HID)
        public bool ShowPeripheralBatteries = true;  // fall back to Windows' own view for anything we cannot decode
        public List<string> ReceiverIds = new List<string>(new string[] {
            "0BDA:4762",   // Realtek 4K dongle (A5 Ultra, verified)
            "2E3C:C365",   // A5 Ultra alternate dongle id
        });

        // ---- desktop widget ----
        public bool HudEnabled = true;
        public int HudX = -1;
        public int HudY = -1;
        public int HudOpacity = 96;      // 55..100
        public int HudScale = 100;       // 80..150
        public string Theme = "system";  // settings window: system | light | dark
        public string HudTheme = "dark"; // desktop widget: system | light | dark

        // ---- shell ----
        public bool StartWithWindows = true;
        public bool CheckUpdates = false;

        public static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseMeter");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var map = MiniJson.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));

                string v;
                if (map.TryGetValue("intervalSec", out v)) { int n; if (int.TryParse(v, out n)) s.IntervalSec = Clamp(n, 10, 3600); }
                if (map.TryGetValue("lowThreshold", out v)) { int n; if (int.TryParse(v, out n)) s.LowThreshold = Clamp(n, 5, 60); }
                if (map.TryGetValue("notifyLow", out v)) s.NotifyLow = v == "true";
                if (map.TryGetValue("adaptivePolling", out v)) s.AdaptivePolling = v == "true";
                if (map.TryGetValue("startMinimized", out v)) s.StartMinimized = v == "true";
                if (map.TryGetValue("useHid", out v)) s.UseHid = v == "true";
                if (map.TryGetValue("useBle", out v)) s.UseBle = v == "true";
                if (map.TryGetValue("usePnp", out v)) s.UsePnp = v == "true";
                if (map.TryGetValue("showPeripheralBatteries", out v)) s.ShowPeripheralBatteries = v == "true";
                if (map.TryGetValue("hudEnabled", out v)) s.HudEnabled = v == "true";
                if (map.TryGetValue("hudX", out v)) { int n; if (int.TryParse(v, out n)) s.HudX = n; }
                if (map.TryGetValue("hudY", out v)) { int n; if (int.TryParse(v, out n)) s.HudY = n; }
                if (map.TryGetValue("hudOpacity", out v)) { int n; if (int.TryParse(v, out n)) s.HudOpacity = Clamp(n, 55, 100); }
                if (map.TryGetValue("hudScale", out v)) { int n; if (int.TryParse(v, out n)) s.HudScale = Clamp(n, 80, 150); }
                if (map.TryGetValue("hudTheme", out v) && (v == "dark" || v == "light" || v == "system")) s.HudTheme = v;
                if (map.TryGetValue("theme", out v) && (v == "dark" || v == "light" || v == "system")) s.Theme = v;
                if (map.TryGetValue("startWithWindows", out v)) s.StartWithWindows = v == "true";
                if (map.TryGetValue("receiverIds", out v))
                {
                    var ids = MiniJson.ParseArray(v);
                    if (ids.Count > 0) s.ReceiverIds = ids;
                }
            }
            catch (Exception ex) { Log.Warn("settings load failed, using defaults: " + ex.Message); }
            return s;
        }

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.ReceiverIds = new List<string>(ReceiverIds);
            return c;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"intervalSec\": " + IntervalSec + ",");
            sb.AppendLine("  \"lowThreshold\": " + LowThreshold + ",");
            sb.AppendLine("  \"notifyLow\": " + B(NotifyLow) + ",");
            sb.AppendLine("  \"adaptivePolling\": " + B(AdaptivePolling) + ",");
            sb.AppendLine("  \"startMinimized\": " + B(StartMinimized) + ",");
            sb.AppendLine("  \"useHid\": " + B(UseHid) + ",");
            sb.AppendLine("  \"useBle\": " + B(UseBle) + ",");
            sb.AppendLine("  \"usePnp\": " + B(UsePnp) + ",");
            sb.AppendLine("  \"showPeripheralBatteries\": " + B(ShowPeripheralBatteries) + ",");
            sb.AppendLine("  \"hudEnabled\": " + B(HudEnabled) + ",");
            sb.AppendLine("  \"hudX\": " + HudX + ",");
            sb.AppendLine("  \"hudY\": " + HudY + ",");
            sb.AppendLine("  \"hudOpacity\": " + HudOpacity + ",");
            sb.AppendLine("  \"hudScale\": " + HudScale + ",");
            sb.AppendLine("  \"theme\": " + MiniJson.Str(Theme) + ",");
            sb.AppendLine("  \"hudTheme\": " + MiniJson.Str(HudTheme) + ",");
            sb.AppendLine("  \"startWithWindows\": " + B(StartWithWindows) + ",");
            sb.AppendLine("  \"receiverIds\": " + MiniJson.Array(ReceiverIds));
            sb.AppendLine("}");
            try { File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Log.Warn("settings save failed: " + ex.Message); }
        }

        static string B(bool v) { return v ? "true" : "false"; }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    /// <summary>Just enough JSON for a flat settings object.</summary>
    static class MiniJson
    {
        public static Dictionary<string, string> ParseObject(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            int i = text.IndexOf('{');
            if (i < 0) return map;
            i++;
            int guard = 0;
            while (i < text.Length && guard++ < 100000)
            {
                SkipWs(text, ref i);
                if (i >= text.Length || text[i] == '}') break;
                if (text[i] == ',') { i++; continue; }   // tolerate trailing / doubled commas
                string key = ReadString(text, ref i);
                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ':') i++;
                SkipWs(text, ref i);
                string value;
                if (i < text.Length && text[i] == '[') value = ReadArrayRaw(text, ref i);
                else if (i < text.Length && text[i] == '"') value = ReadString(text, ref i);
                else value = ReadBare(text, ref i);
                if (key != null && key.Length > 0) map[key] = value;
                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',') i++;
            }
            return map;
        }

        public static List<string> ParseArray(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            int i = 0;
            SkipWs(raw, ref i);
            if (i < raw.Length && raw[i] == '[') i++;
            while (i < raw.Length)
            {
                SkipWs(raw, ref i);
                if (i >= raw.Length || raw[i] == ']') break;
                string v = (raw[i] == '"') ? ReadString(raw, ref i) : ReadBare(raw, ref i);
                if (!string.IsNullOrEmpty(v)) list.Add(v);
                SkipWs(raw, ref i);
                if (i < raw.Length && raw[i] == ',') i++;
            }
            return list;
        }

        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static string Array(List<string> items)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Str(items[i]));
            }
            return sb.Append(']').ToString();
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static string ReadString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char e = s[i];
                    if (e == 'n') sb.Append('\n');
                    else if (e == 't') sb.Append('\t');
                    else if (e == 'r') sb.Append('\r');
                    else sb.Append(e);
                }
                else sb.Append(s[i]);
                i++;
            }
            i++;
            return sb.ToString();
        }

        static string ReadBare(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
            return s.Substring(start, i - start).Trim();
        }

        static string ReadArrayRaw(string s, ref int i)
        {
            int start = i;
            int depth = 0;
            while (i < s.Length)
            {
                if (s[i] == '[') depth++;
                else if (s[i] == ']') { depth--; if (depth == 0) { i++; break; } }
                i++;
            }
            return s.Substring(start, Math.Min(i, s.Length) - start);
        }
    }
}
