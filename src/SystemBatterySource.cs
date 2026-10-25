using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MouseMeter
{
    /// <summary>
    /// Reads battery levels for peripherals Windows already knows about (BLE GATT battery service,
    /// Bluetooth HID, HFP headsets). No vendor protocol involved, so it covers keyboards and
    /// headsets that the dongle protocol cannot describe.
    /// </summary>
    public class SystemBatterySource : IBatterySource
    {
        public string Name { get { return "Windows 电池接口"; } }

        readonly Func<bool> _enabled;
        readonly Func<List<string>> _dongleNames;
        DateTime _lastEnumerate = DateTime.MinValue;
        List<PnpDevice> _cache = new List<PnpDevice>();
        string _lastError = "";

        /// <param name="dongleNames">names of devices already reported through a live dongle; those must
        /// not be duplicated from the (cached) Windows view.</param>
        public SystemBatterySource(Func<bool> enabled, Func<List<string>> dongleNames)
        {
            _enabled = enabled;
            _dongleNames = dongleNames;
        }

        public List<Reading> Read()
        {
            var list = new List<Reading>();
            if (!_enabled()) return list;

            // enumerating the device tree is not free; once a minute is plenty and keeps
            // the 15 s "charging" cadence from hammering setupapi.
            List<PnpDevice> devices;
            if ((DateTime.Now - _lastEnumerate).TotalSeconds < 45 && _cache.Count > 0)
            {
                devices = _cache;
            }
            else
            {
                try { devices = PnpBattery.Enumerate(); }
                catch (Exception ex) { _lastError = ex.Message; devices = _cache; }
                _cache = devices;
                _lastEnumerate = DateTime.Now;
            }

            List<string> owned = null;
            try { if (_dongleNames != null) owned = _dongleNames(); } catch { }

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                if (!d.Connected) continue;
                if (owned != null && d.BatterySource != "HFP" && MatchesAny(d.FriendlyName, owned)) continue;

                string kind = DeviceHeuristics.FromName(d.FriendlyName);
                if (kind == null && d.IsAudio) kind = DeviceKind.Headset;
                if (kind == null) continue;   // unknown gadgets are left to the vendor probe
                // level -1 is allowed for headsets: the device is connected, Windows just has no
                // percentage yet. The UI shows "电量未知" instead of inventing a number.
                if (d.Level < 0 && kind != DeviceKind.Headset) continue;

                var r = new Reading();
                r.Id = "sys|" + d.InstanceId.ToLowerInvariant();
                r.Kind = kind;
                r.DeviceName = string.IsNullOrEmpty(d.FriendlyName) ? AutoName(kind) : d.FriendlyName;
                r.Model = "";
                r.Level = d.Level;
                r.Charging = false;           // the generic battery interface does not report charging
                r.Transport = d.Transport;
                r.HardwareId = ShortId(d.InstanceId);
                r.Source = Name;
                r.At = DateTime.Now;
                list.Add(r);
            }
            return list;
        }

        static bool MatchesAny(string name, List<string> owned)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < owned.Count; i++)
            {
                string o = (owned[i] ?? "").Trim();
                if (o.Length < 3) continue;
                if (string.Equals(name.Trim(), o, StringComparison.OrdinalIgnoreCase)) return true;
                if (name.IndexOf(o, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static string AutoName(string kind)
        {
            if (kind == DeviceKind.Keyboard) return "键盘";
            if (kind == DeviceKind.Headset) return "耳机";
            return "鼠标";
        }

        /// <summary>BLE instance ids carry a 12 hex digit address; show that, it is what users match.</summary>
        public static string ShortId(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return "";
            string s = instanceId;
            int dash = s.IndexOf('-');
            if (s.StartsWith("BLE#", StringComparison.OrdinalIgnoreCase) && dash > 4) return s.Substring(4, dash - 4);
            return s.Length <= 32 ? s : s.Substring(0, 32);
        }

        public string LastError { get { return _lastError; } }
    }
}
