using System;
using System.Collections.Generic;
using System.Threading;

namespace MouseMeter
{
    /// <summary>How fresh a reading is; drives the poll interval and the UI grey-out.</summary>
    public static class Freshness
    {
        public const int StaleSeconds = 90;
        public const int GoneSeconds = 60 * 60;   // an hour without a single answer -> drop the row

        public static bool IsStale(Reading r) { return (DateTime.Now - r.At).TotalSeconds > StaleSeconds; }
    }

    /// <summary>
    /// Owns every source, merges their readings into one list and keeps "last known good" per
    /// device. All public members are thread safe; Read()/Refresh() block and must not run on the
    /// UI thread.
    /// </summary>
    public class DeviceManager
    {
        readonly List<IBatterySource> _sources = new List<IBatterySource>();
        readonly object _gate = new object();     // guards _byId / _previous / _order / counters
        readonly Dictionary<string, Reading> _byId = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _order = new List<string>();
        readonly Dictionary<string, Reading> _previous = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        public int Polls;
        public int Hits;
        public double LastPollMs;
        public DateTime LastPollAt = DateTime.MinValue;
        public string LastError = "";
        public string LastSourceReport = "";

        public IList<IBatterySource> Sources { get { return _sources; } }

        public DeviceManager(Settings cfg)
        {
            _sources.Add(new VendorHidSource(delegate { return cfg.UseHid; }, cfg.ReceiverIds));
            _sources.Add(new SystemBatterySource(delegate { return cfg.UseBle || cfg.UsePnp; }, DongleDeviceNames));
            // Extension point: _sources.Add(new LogitechBoltSource()); etc.
        }

        /// <summary>Names of devices a live dongle already answers for (used to suppress cache duplicates).</summary>
        List<string> DongleDeviceNames()
        {
            var names = new List<string>();
            lock (_gate)
            {
                foreach (var kv in _byId)
                {
                    var r = kv.Value;
                    if (r.Transport != "2.4G") continue;
                    if ((DateTime.Now - r.At).TotalSeconds > Freshness.StaleSeconds) continue;
                    names.Add(r.DeviceName);
                    if (!string.IsNullOrEmpty(r.Model)) names.Add(r.Model);
                }
            }
            return names;
        }

        /// <summary>All known devices, freshest first, ordered mouse -> keyboard -> headset -> other.</summary>
        public List<Reading> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<Reading>();
                for (int i = 0; i < _order.Count; i++)
                {
                    Reading r;
                    if (_byId.TryGetValue(_order[i], out r)) list.Add(r);
                }
                list.Sort(Compare);
                return list;
            }
        }

        static int KindRank(string kind)
        {
            if (kind == DeviceKind.Mouse) return 0;
            if (kind == DeviceKind.Keyboard) return 1;
            if (kind == DeviceKind.Headset) return 2;
            return 3;
        }

        static int Compare(Reading a, Reading b)
        {
            int k = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
            if (k != 0) return k;
            return string.Compare(a.DeviceName, b.DeviceName, StringComparison.CurrentCultureIgnoreCase);
        }

        public bool IsStale
        {
            get
            {
                var snap = Snapshot();
                return snap.Count > 0 && Freshness.IsStale(snap[0]);
            }
        }

        /// <summary>Blocking poll of every source. Returns true when at least one device answered.</summary>
        public bool Refresh()
        {
            var started = DateTime.Now;
            bool any = false;
            var report = new System.Text.StringBuilder();

            for (int i = 0; i < _sources.Count; i++)
            {
                IBatterySource src = _sources[i];
                List<Reading> got = null;
                try { got = src.Read(); }
                catch (Exception ex) { LastError = src.Name + ": " + ex.Message; Log.Exception("source " + src.Name, ex); }

                if (got != null && got.Count > 0)
                {
                    any = true;
                    report.Append(src.Name).Append('=').Append(got.Count).Append(' ');
                    Merge(got);
                }
                else report.Append(src.Name).Append("=0 ");
            }

            lock (_gate)
            {
                // forget devices that stayed silent for an hour (unpaired, unplugged)
                var dead = new List<string>();
                foreach (var kv in _byId)
                    if ((DateTime.Now - kv.Value.At).TotalSeconds > Freshness.GoneSeconds) dead.Add(kv.Key);
                for (int i = 0; i < dead.Count; i++) { _byId.Remove(dead[i]); _order.Remove(dead[i]); }

                Polls++;
                if (any) Hits++;
                LastPollMs = (DateTime.Now - started).TotalMilliseconds;
                LastPollAt = DateTime.Now;
                LastSourceReport = report.ToString().Trim();
            }

            if (!any && _byId.Count == 0)
                LastError = _sources.Count == 0 ? "没有可用的数据源" : "未检测到设备（接收器未插入或设备休眠）";

            Log.Info("poll " + LastSourceReport + " devices=" + _byId.Count + " elapsed=" + (int)LastPollMs + "ms");
            return any;
        }

        /// <summary>Merge one source's readings. Charging is sticky: the generic Windows interface
        /// never reports it, so a device that once said "charging" keeps the flag while its level rises.</summary>
        void Merge(List<Reading> incoming)
        {
            lock (_gate)
            {
                for (int i = 0; i < incoming.Count; i++)
                {
                    var r = incoming[i];
                    if (!r.IsValid || string.IsNullOrEmpty(r.Id)) continue;

                    Reading prev;
                    if (_previous.TryGetValue(r.Id, out prev))
                    {
                        if (!r.Charging && prev.Charging)
                        {
                            if (r.Level > prev.Level) r.Charging = true;      // still filling up
                            else if (r.Level >= 100) r.Charging = true;       // full but plugged
                        }
                    }
                    _previous[r.Id] = r;

                    if (!_byId.ContainsKey(r.Id)) _order.Add(r.Id);
                    _byId[r.Id] = r;
                }
            }
        }

        /// <summary>Snapshot plus the "Windows' own view" rows for devices we could not decode ourselves.</summary>
        public List<Reading> SnapshotWithPeripherals(bool includePeripherals)
        {
            var list = Snapshot();
            if (!includePeripherals) return list;

            try
            {
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++) seenNames.Add(Norm(list[i].DeviceName));

                var pnp = PnpBattery.Enumerate();
                for (int i = 0; i < pnp.Count; i++)
                {
                    var d = pnp[i];
                    if (d.Level < 0 || !d.Connected) continue;
                    if (seenNames.Contains(Norm(d.FriendlyName))) continue;
                    if (string.IsNullOrEmpty(d.FriendlyName)) continue;

                    var r = new Reading();
                    r.Id = "sys|" + d.InstanceId.ToLowerInvariant();
                    string kind = DeviceHeuristics.FromName(d.FriendlyName);
                    r.Kind = kind ?? (d.IsAudio ? DeviceKind.Headset : "other");
                    r.DeviceName = d.FriendlyName;
                    r.Level = d.Level;
                    r.Transport = d.Transport;
                    r.HardwareId = SystemBatterySource.ShortId(d.InstanceId);
                    r.Source = "系统";
                    r.At = DateTime.Now;
                    list.Add(r);
                }
            }
            catch (Exception ex) { Log.Warn("peripherals: " + ex.Message); }
            return list;
        }

        static string Norm(string s) { return (s ?? "").Trim().ToLowerInvariant(); }

        /// <summary>The device the widget shows: first mouse, else first keyboard, else first headset.</summary>
        public static Reading? Primary(List<Reading> list)
        {
            if (list == null || list.Count == 0) return null;
            for (int i = 0; i < list.Count; i++) if (list[i].Kind == DeviceKind.Mouse) return list[i];
            return list[0];
        }

        /// <summary>Adaptive interval in seconds.</summary>
        public int NextIntervalSec(int configured, bool adaptive)
        {
            if (!adaptive) return Math.Max(10, configured);
            var snap = Snapshot();
            if (snap.Count == 0) return Math.Max(30, Math.Min(120, configured));
            bool charging = false, low = false;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap[i].Charging) charging = true;
                if (snap[i].Level <= 15) low = true;
            }
            if (charging) return 15;
            if (low) return 30;
            return Math.Max(30, configured);
        }

        public string Diagnostics(Settings cfg)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("MouseMeter 3.0 diagnostics");
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
            sb.AppendLine("Sources: " + _sources.Count + " (hid=" + cfg.UseHid + " ble=" + cfg.UseBle + " pnp=" + cfg.UsePnp + ")");
            for (int i = 0; i < _sources.Count; i++) sb.AppendLine("  - " + _sources[i].Name);
            sb.AppendLine("Polls: " + Polls + "  hits: " + Hits + "  last=" + (int)LastPollMs + "ms  " + LastSourceReport);
            var snap = Snapshot();
            for (int i = 0; i < snap.Count; i++)
            {
                var r = snap[i];
                sb.AppendLine("  [" + DeviceKind.Label(r.Kind) + "] " + r.DeviceName + " " + r.Level + "%"
                    + (r.Charging ? " charging" : "") + "  " + r.Transport + " " + r.HardwareId
                    + "  src=" + r.Source + "  age=" + (int)(DateTime.Now - r.At).TotalSeconds + "s");
            }
            if (snap.Count == 0) sb.AppendLine("  no devices. " + LastError);
            sb.AppendLine("ReceiverIds: " + string.Join(", ", cfg.ReceiverIds.ToArray()));
            string tail = Log.Tail(15);
            if (tail.Length > 0) sb.AppendLine("--- log ---").AppendLine(tail);
            return sb.ToString();
        }
    }
}
