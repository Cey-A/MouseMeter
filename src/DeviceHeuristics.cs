using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MouseMeter
{
    /// <summary>
    /// Pure heuristics that answer "what kind of device is this?" from names, PnP ids and HID
    /// usage pages. Kept free of Win32 so tests/ can exercise it on any machine.
    ///
    /// Order matters: headsets must be tested before keyboards ("Gaming Headset Keyboard" style
    /// product names exist), and keyboards before mice.
    /// </summary>
    public static class DeviceHeuristics
    {
        public const int UsageMouse = 0x02;
        public const int UsageJoystick = 0x04;
        public const int UsageGamePad = 0x05;
        public const int UsageKeyboard = 0x06;
        public const int UsageKeypad = 0x07;
        public const int PageGenericDesktop = 0x01;
        public const int PageDigitizer = 0x0D;
        public const int PageConsumer = 0x0C;

        // AudioEndpoint service instances: the last path segment before the '{guid}' looks like
        //  {0.0.0.00000000}.{<iface>}|{<device>}\<iface>\<device>.{b3b2...}
        // anything after the last '.' that looks like the endpoint GUID; the path may or may not
        // carry a trailing #{interface-class-guid} suffix, so anchor on "end of string or '{'".
        static readonly Regex AudioEndpointRx = new Regex(@"\.\{?([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}?(?:#\{.*\})?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        static readonly string[] HeadsetWords = new string[] {
            "headset", "headphone", "earbud", "earphone", "speaker", "hest", "耳机", "耳麦", "音箱"
        };
        static readonly string[] KeyboardWords = new string[] {
            "keyboard", "keypad", "键盘", "keychron", "kbd"
        };
        static readonly string[] MouseWords = new string[] {
            "mouse", "鼠标", "gaming mouse"
        };

        /// <summary>The audio endpoint id embedded in a PnP device instance path, or null.</summary>
        public static string AudioEndpointFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.TrimEnd('\\');
            var m = AudioEndpointRx.Match(p);
            if (!m.Success) return null;
            return m.Groups[1].Value.ToLowerInvariant();
        }

        /// <summary>Classify from a display name plus any extra keywords (product string, PnP friendly name).</summary>
        public static string FromName(string name)
        {
            string s = (name ?? "").ToLowerInvariant();
            if (s.Length == 0) return null;
            for (int i = 0; i < HeadsetWords.Length; i++) if (s.Contains(HeadsetWords[i])) return DeviceKind.Headset;
            for (int i = 0; i < KeyboardWords.Length; i++) if (s.Contains(KeyboardWords[i])) return DeviceKind.Keyboard;
            for (int i = 0; i < MouseWords.Length; i++) if (s.Contains(MouseWords[i])) return DeviceKind.Mouse;
            return null;
        }

        /// <summary>Classify from a HID usage page / usage pair.</summary>
        public static string FromUsage(int page, int usage)
        {
            if (page == PageGenericDesktop)
            {
                if (usage == UsageMouse) return DeviceKind.Mouse;
                if (usage == UsageKeyboard || usage == UsageKeypad) return DeviceKind.Keyboard;
                if (usage == UsageJoystick || usage == UsageGamePad) return null;   // game controller, ignore
            }
            if (page == PageDigitizer) return null;
            return null;
        }

        /// <summary>
        /// Whether a HID collection of this usage can carry a vendor battery protocol at all.
        /// Excludes the high frequency mouse / keyboard / consumer interfaces: probing those with a
        /// 65-byte feature report is both useless and disruptive to typing.
        /// </summary>
        public static bool IsVendorPage(int page)
        {
            return page == 0xFF01 || page == 0xFFFF || page == 0xFFA0 || page == 0xFF60 || page == 0xFF00;
        }

        public static bool IsProbeCandidate(HidCollectionLike c)
        {
            if (c == null || c.FeatureLength < Protocol.MinFrame) return false;
            if (IsVendorPage(c.UsagePage)) return true;
            if (c.UsagePage == PageGenericDesktop && (c.Usage == UsageMouse || c.Usage == UsageKeyboard || c.Usage == UsageKeypad)) return false;
            if (c.UsagePage == PageConsumer) return false;
            return false;
        }

        /// <summary>What kind of device is this collection part of? Falls back to the whole device's usages.</summary>
        public static string Classify(HidCollectionLike self, IList<HidCollectionLike> siblings)
        {
            // 1) the collection itself
            string k = FromUsage(self.UsagePage, self.Usage);
            if (k != null) return k;

            // 2) any sibling collection of the same physical device (a keyboard dongle exposes
            //    keyboard + consumer + vendor collections; the vendor one has no usage of its own)
            if (siblings != null)
            {
                for (int i = 0; i < siblings.Count; i++)
                {
                    if (ReferenceEquals(siblings[i], self)) continue;
                    string s = FromUsage(siblings[i].UsagePage, siblings[i].Usage);
                    if (s == DeviceKind.Keyboard) return DeviceKind.Keyboard;
                    if (s == DeviceKind.Mouse) return DeviceKind.Mouse;
                }
            }

            // 3) product / manufacturer strings on the vendor collection (dongles often report
            //    "2.4G Receiver" with no device words, so this is best effort)
            k = FromName(self.ProductName);
            if (k != null) return k;
            k = FromName(self.Manufacturer);
            if (k != null) return k;

            return null;
        }

        /// <summary>Pick the single best collection to talk to for one physical device.</summary>
        public static HidCollectionLike PickProbeTarget(IList<HidCollectionLike> collections)
        {
            HidCollectionLike best = null;
            for (int i = 0; i < collections.Count; i++)
            {
                var c = collections[i];
                if (!IsProbeCandidate(c)) continue;
                if (best == null) { best = c; continue; }
                // prefer the canonical vendor page used by the verified mouse protocol
                bool cPerfect = c.UsagePage == 0xFF01 && c.Usage == 0x0001;
                bool bPerfect = best.UsagePage == 0xFF01 && best.Usage == 0x0001;
                if (cPerfect && !bPerfect) best = c;
            }
            return best;
        }

        /// <summary>Short human label for the transport, from a PnP instance path.</summary>
        public static string TransportOf(string pnpId)
        {
            string s = (pnpId ?? "").ToLowerInvariant();
            if (s.Contains("ble#")) return "BLE";
            if (s.Contains("bth")) return "蓝牙";
            if (s.Contains("usb\\") || s.StartsWith("usb#")) return "USB";
            if (s.Contains("hid#") || s.Contains(@"hid\")) return "2.4G";
            return "";
        }

        public static bool LooksLikeDongle(string pnpId)
        {
            string s = (pnpId ?? "").ToLowerInvariant();
            return s.Contains("receiver") || s.Contains("dongle") || s.Contains("2.4g");
        }

        /// <summary>Parse "0BDA:4762" into vid / pid.</summary>
        public static bool TryParseHwId(string id, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            if (string.IsNullOrEmpty(id)) return false;
            string s = id.Trim().Replace("VID_", "").Replace("PID_", "");
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon >= s.Length - 1) return false;
            string a = s.Substring(0, colon);
            string b = s.Substring(colon + 1);
            return ushort.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                && ushort.TryParse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }
    }

    /// <summary>Structural interface so the heuristics can be tested without the Win32 types.</summary>
    public interface HidCollectionLike
    {
        int UsagePage { get; }
        int Usage { get; }
        int FeatureLength { get; }
        string ProductName { get; }
        string Manufacturer { get; }
    }
}
