using System;
using System.Collections.Generic;

namespace MouseMeter
{
    /// <summary>
    /// One way of finding batteries. <see cref="Read"/> must never throw and must be cheap when
    /// nothing is around.
    /// </summary>
    public interface IBatterySource
    {
        string Name { get; }
        List<Reading> Read();
    }

    /// <summary>
    /// Vendor feature-report protocol over HID (MCHOSE / Realtek dongles).
    ///
    /// v3 changes vs v2:
    ///  * one query per physical device instead of per collection — a dongle exposes up to three
    ///    vendor collections and we used to probe every one of them, 8 times each;
    ///  * the device class comes from the response / sibling usages, so keyboards and headsets on
    ///    the same protocol are reported separately instead of being collapsed into "the mouse";
    ///  * per-device attempt budget, so a sleeping mouse no longer starves the other devices.
    /// </summary>
    public class VendorHidSource : IBatterySource
    {
        public string Name { get { return "HID 私有协议"; } }

        readonly Func<bool> _enabled;
        readonly List<string> _knownIds;
        readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public string LastError = "";
        public int Queries;

        public VendorHidSource(Func<bool> enabled, List<string> knownHardwareIds)
        {
            _enabled = enabled;
            _knownIds = knownHardwareIds ?? new List<string>();
        }

        public List<Reading> Read()
        {
            var output = new List<Reading>();
            if (!_enabled()) return output;

            List<HidCollection> all;
            try { all = Hid.Enumerate(); }
            catch (Exception ex) { LastError = ex.Message; return output; }
            if (all.Count == 0) { LastError = "no hid"; return output; }

            var groups = GroupByDevice(all);
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var target = DeviceHeuristics.PickProbeTarget(g.Items);
                if (target == null) continue;

                string kind = DeviceHeuristics.Classify(target, g.Items);
                bool whitelisted = IsKnown(g.Vid, g.Pid);

                // unknown device and the first probe already failed: stop poking it every cycle.
                int fails;
                if (_failures.TryGetValue(g.DeviceKey, out fails) && fails >= 3 && !whitelisted) continue;
                if (!whitelisted && !LooksLikeWirelessDongle(g, target)) continue;

                Queries++;
                var res = Probe((HidCollection)target, fails);
                if (res.Ok)
                {
                    _failures.Remove(g.DeviceKey);
                    var r = res.Value;
                    r.Kind = kind ?? DeviceKind.Mouse;
                    r.DeviceName = DisplayName(g, r.Kind);
                    r.Model = Protocol.ParseModel(res.Reply);
                    r.Transport = "2.4G";
                    r.HardwareId = g.Vid.ToString("X4") + ":" + g.Pid.ToString("X4");
                    r.Id = "hid|" + g.DeviceKey + "|" + r.Kind;
                    r.Source = Name;
                    output.Add(r);
                }
                else
                {
                    _failures[g.DeviceKey] = fails + 1;
                    LastError = res.Reason;
                    Log.Info("probe " + g.DeviceKey + " -> " + res.Reason + " (fail #" + (fails + 1) + ")");
                }
            }
            return output;
        }

        struct ProbeResult
        {
            public bool Ok;
            public Reading Value;
            public string Reason;
            public byte[] Reply;
        }

        ProbeResult Probe(HidCollection col, int failures)
        {
            var res = new ProbeResult();
            res.Value = Reading.Invalid();

            // first sighting: two attempts; a device that keeps failing gets one attempt so it can
            // never take the poll thread hostage (a sleeping mouse is the normal case).
            int attempts = failures == 0 ? 2 : 1;
            var frame = Protocol.BuildRequest(0x06);

            for (int a = 0; a < attempts; a++)
            {
                var h = Hid.Open(col.Path, true);
                if (h.IsInvalid)
                {
                    res.Reason = "open " + Hid.DescribeError(Hid.LastError);
                    System.Threading.Thread.Sleep(Retry.DelayFor(a));
                    continue;
                }
                try
                {
                    if (!Hid.SetFeature(h, frame))
                    {
                        res.Reason = "set " + Hid.DescribeError(Hid.LastError);
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(30);
                        var reply = Hid.GetFeature(h, Protocol.FrameLength, Protocol.ResponseReportId);
                        Reading r;
                        string why;
                        if (reply == null)
                        {
                            res.Reason = "get " + Hid.DescribeError(Hid.LastError);
                        }
                        else if (Protocol.TryParse(reply, out r, out why))
                        {
                            res.Ok = true;
                            res.Value = r;
                            res.Reply = reply;
                            res.Reason = why;
                            return res;
                        }
                        else
                        {
                            res.Reason = "parse " + why;
                        }
                    }
                }
                catch (Exception ex) { res.Reason = "exception " + ex.Message; }
                finally { h.Close(); }

                if (a + 1 < attempts) System.Threading.Thread.Sleep(Retry.DelayFor(a));
            }
            return res;
        }

        bool IsKnown(ushort vid, ushort pid)
        {
            string id = vid.ToString("X4") + ":" + pid.ToString("X4");
            for (int i = 0; i < _knownIds.Count; i++)
                if (string.Equals(_knownIds[i], id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Unknown VID:PID: only probe when the strings look like a wireless receiver, otherwise a
        /// random vendor interface would be poked on every poll.
        /// </summary>
        static bool LooksLikeWirelessDongle(DeviceGroup g, HidCollectionLike target)
        {
            string s = ((target.ProductName ?? "") + " " + (target.Manufacturer ?? "")).ToLowerInvariant();
            if (s.Contains("receiver") || s.Contains("dongle") || s.Contains("2.4")) return true;
            // a keyboard / mouse whose vendor collection carries no strings still deserves one try
            string k = DeviceHeuristics.Classify(target, g.Items);
            return k != null;
        }

        static string DisplayName(DeviceGroup g, string kind)
        {
            string n = g.ProductName;
            if (string.IsNullOrEmpty(n) || n.Length <= 2) n = "";
            // dongles report "2.4G Receiver" — not something to show as the device name
            if (n.ToLowerInvariant().Contains("receiver") || n.ToLowerInvariant().Contains("dongle")) n = "";
            if (n.Length > 0) return n;
            if (kind == DeviceKind.Keyboard) return "无线键盘";
            if (kind == DeviceKind.Headset) return "无线耳机";
            return "无线鼠标";
        }

        // ------------------------------------------------------------------
        // grouping
        // ------------------------------------------------------------------

        sealed class DeviceGroup
        {
            public string DeviceKey;
            public ushort Vid;
            public ushort Pid;
            public string ProductName = "";
            public List<HidCollectionLike> Items = new List<HidCollectionLike>();
        }

        /// <summary>
        /// Collapse all top level collections that share one physical device into a group.
        /// Dongles expose keyboard + consumer + vendor collections under the same instance key;
        /// probing each of them separately is both slower and can return the same answer twice.
        /// </summary>
        static List<DeviceGroup> GroupByDevice(List<HidCollection> all)
        {
            var map = new Dictionary<string, DeviceGroup>(StringComparer.OrdinalIgnoreCase);
            var order = new List<DeviceGroup>();

            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                string key = c.InstanceKey;
                DeviceGroup g;
                if (!map.TryGetValue(key, out g))
                {
                    g = new DeviceGroup();
                    g.DeviceKey = key;
                    g.Vid = c.Vid;
                    g.Pid = c.Pid;
                    g.ProductName = c.ProductName ?? "";
                    map[key] = g;
                    order.Add(g);
                }
                else if (string.IsNullOrEmpty(g.ProductName) && !string.IsNullOrEmpty(c.ProductName))
                {
                    g.ProductName = c.ProductName;
                }
                g.Items.Add(c);
            }
            return order;
        }
    }
}
