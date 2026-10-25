using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseMeter
{
    /// <summary>One battery carrying device as the OS sees it.</summary>
    public class PnpDevice
    {
        public string InstanceId = "";
        public string FriendlyName = "";
        public string ClassName = "";
        public int Level = -1;              // -1 = the OS does not know a percentage
        public string BatterySource = "";   // "BLE" / "HID" / "HFP" / "USB"
        public bool Present = true;
        public bool Connected = true;

        public string Id { get { return InstanceId; } }

        public string Transport
        {
            get
            {
                string s = InstanceId.ToLowerInvariant();
                if (s.Contains("ble#")) return "BLE";
                if (s.Contains("bth")) return "蓝牙";
                if (s.Contains("hid#")) return "2.4G";
                if (s.Contains("usb#")) return "USB";
                return BatterySource;
            }
        }

        public bool IsAudio { get { return BatterySource == "HFP" || BatterySource == "A2DP"; } }
    }

    /// <summary>
    /// Enumerates battery powered peripherals straight from Windows: BLE GATT battery service,
    /// Bluetooth HID, and Bluetooth audio (HFP) headsets. Zero vendor protocols involved.
    ///
    /// This is what makes keyboards, headsets and "any other device" work without reverse
    /// engineering each brand: the OS (and its own driver pairing) already exposes the number.
    /// </summary>
    public static class PnpBattery
    {
        const int DIGCF_PRESENT = 0x02;
        const int DIGCF_DEVICEINTERFACE = 0x10;

        // DEVPROPKEY_Device_BatteryLevel
        static readonly Guid GuidDevProps = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
        static PropertyKey PkBattery = new PropertyKey(new Guid("104ea319-6ee2-4701-bd47-8ddbfa425c1d"), 2);
        static PropertyKey PkFriendlyName = new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);
        static PropertyKey PkDeviceDesc = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
        static PropertyKey PkInstanceId = new PropertyKey(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

        static readonly Guid GuidBattery = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");       // GUID_DEVCLASS_BATTERY
        static readonly Guid GuidBluetooth = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");     // GUID_BTHPORT_DEVICE_INTERFACE
        static readonly Guid GuidBthLe = new Guid("781aee18-7733-4ce4-add0-91f41c67b592");        // GUID_BLUETOOTHLE_DEVICE_INTERFACE
        static readonly Guid GuidAudio = new Guid("6994ad04-93ef-11d0-a3cc-00a0c9223196");        // KSCATEGORY_AUDIO

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
            public PropertyKey(Guid g, int p) { fmtid = g; pid = p; }
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, ref int needed, IntPtr info);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceProperty(IntPtr set, IntPtr devInfo, ref PropertyKey key, out int type,
            IntPtr buf, int size, out int needed, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiOpenDeviceInterfaceW(IntPtr set, string path, int flags, ref SP_DEVICE_INTERFACE_DATA did);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfacePropertyW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, ref PropertyKey key,
            out int type, IntPtr buf, int size, out int needed, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int len, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);
        [DllImport("cfgmgr32.dll")]
        static extern int CM_Get_DevNode_Status(out int status, out int problem, uint devInst, int flags);

        const int CM_LOCATE_DEVNODE_NORMAL = 0;
        const int DN_DEVICE_PHANTOM = 0x00000020;
        const int DN_PRESENT = 0x00000001;

        // ------------------------------------------------------------------
        // public API
        // ------------------------------------------------------------------

        /// <summary>Every peripheral Windows currently knows a battery level for. Never throws.</summary>
        public static List<PnpDevice> Enumerate()
        {
            var list = new List<PnpDevice>();
            try
            {
                var batteries = EnumerateBatteryClass();          // BLE GATT / BTHLE / BTHENUM-HID
                var audio = EnumerateAudioEndpoints();

                // 1) audio endpoints that have a battery reading: the headset itself, not the endpoint
                var audioById = new Dictionary<string, PnpDevice>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < audio.Count; i++) audioById[audio[i].InstanceId] = audio[i];

                var consumed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < batteries.Count; i++)
                {
                    var b = batteries[i];
                    string iid = b.InstanceId.ToLowerInvariant();
                    string hit = null;
                    foreach (var kv in audioById) if (iid.Contains(kv.Key)) { hit = kv.Key; break; }

                    if (hit != null)
                    {
                        var ep = audioById[hit];
                        ep.Level = b.Level;
                        ep.Connected = b.Connected;
                        consumed[hit] = true;
                        b.BatterySource = "HFP";
                        b.FriendlyName = string.IsNullOrEmpty(ep.FriendlyName) ? b.FriendlyName : ep.FriendlyName;
                        b.InstanceId = ep.InstanceId;
                        list.Add(b);
                    }
                    else list.Add(b);
                }

                // 2) a headset that is paired but has no percentage yet: keep it, level -1, so the UI
                //    can say "已连接 · 电量未知" instead of silently dropping the device.
                for (int i = 0; i < audio.Count; i++)
                {
                    if (consumed.ContainsKey(audio[i].InstanceId)) continue;
                    if (!audio[i].Connected) continue;
                    audio[i].Level = -1;
                    list.Add(audio[i]);
                }
            }
            catch (Exception ex) { Log.Warn("pnp enumerate: " + ex.Message); }
            return list;
        }

        static List<PnpDevice> EnumerateBatteryClass()
        {
            var list = new List<PnpDevice>();
            Guid g = GuidBattery;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return list;

            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(did);
            try
            {
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;

                    string iid = DeviceInstanceId(set, ref did);
                    if (iid.Length == 0) continue;

                    string low = iid.ToLowerInvariant();
                    // ACPI / wired / system battery: not a peripheral, skip
                    if (low.StartsWith("acpi\\")) continue;
                    // USB HID device dongles report their own internal cell, not the mouse's
                    if (low.StartsWith("usb\\")) continue;

                    string src = low.Contains("ble#") || low.Contains("bthle") ? "BLE"
                               : (low.Contains("bth") ? "HID" : "USB");
                    bool connected = IsPresentAndConnected(iid);

                    int lvl = ReadBatteryLevel(set, ref did);
                    string name = DeviceString(set, ref did, PkFriendlyName);
                    if (string.IsNullOrEmpty(name)) name = DeviceString(set, ref did, PkDeviceDesc);

                    var d = new PnpDevice();
                    d.InstanceId = iid;
                    d.FriendlyName = name ?? "";
                    d.Level = lvl;
                    d.BatterySource = src;
                    d.Present = true;
                    d.Connected = connected;
                    if (lvl < 0 && !connected) continue;   // nothing to show
                    if (name == null && lvl < 0) continue;
                    list.Add(d);
                }
            }
            catch (Exception ex) { Log.Warn("pnp battery class: " + ex.Message); }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        static List<PnpDevice> EnumerateAudioEndpoints()
        {
            var list = new List<PnpDevice>();
            Guid g = GuidAudio;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return list;

            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(did);
            var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;

                    string full = InterfacePath(set, ref did);
                    string ep = DeviceHeuristics.AudioEndpointFromPath(full);
                    if (ep == null || seen.ContainsKey(ep)) continue;
                    seen[ep] = true;

                    string iid = DeviceInstanceId(set, ref did);
                    string name = DeviceString(set, ref did, PkFriendlyName);
                    string cls = DeviceHeuristics.FromName(name);
                    bool bt = (iid ?? "").ToLowerInvariant().Contains("bth");
                    if (cls != null && cls != DeviceKind.Headset) continue;   // speakers / mics
                    if (!bt && cls == null) continue;

                    var d = new PnpDevice();
                    d.InstanceId = ep;
                    d.FriendlyName = name ?? "";
                    d.BatterySource = "HFP";
                    d.Level = -1;
                    d.Connected = IsPresentAndConnected(iid);
                    list.Add(d);
                }
            }
            catch (Exception ex) { Log.Warn("pnp audio: " + ex.Message); }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        // ------------------------------------------------------------------
        // helpers
        // ------------------------------------------------------------------

        static string InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
        {
            int needed = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
            if (needed <= 0) return "";
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try
            {
                Marshal.WriteInt32(buf, (IntPtr.Size == 8) ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, needed, ref needed, IntPtr.Zero)) return "";
                return Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4)) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string DeviceInstanceId(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
        {
            string path = InterfacePath(set, ref did);
            if (path.Length == 0) return "";

            IntPtr devInfo = IntPtr.Zero;
            try
            {
                devInfo = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return "";
                if (!SetupDiOpenDeviceInterfaceW(devInfo, path, 0, ref ifData)) return "";

                int required = 0, type = 0;
                SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref PkInstanceId, out type,
                    IntPtr.Zero, 0, out required, 0);
                if (required <= 0) return "";
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    if (!SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref PkInstanceId, out type,
                            buf, required, out required, 0)) return "";
                    return Marshal.PtrToStringUni(buf) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return ""; }
            finally { if (devInfo != IntPtr.Zero && devInfo.ToInt64() != -1) SetupDiDestroyDeviceInfoList(devInfo); }
        }

        static string DeviceString(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did, PropertyKey key)
        {
            IntPtr devInfo = IntPtr.Zero;
            try
            {
                string path = InterfacePath(set, ref did);
                if (path.Length == 0) return "";
                devInfo = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return "";
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                if (!SetupDiOpenDeviceInterfaceW(devInfo, path, 0, ref ifData)) return "";

                int required = 0, type = 0;
                SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref key, out type,
                    IntPtr.Zero, 0, out required, 0);
                if (required <= 0) return "";
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    if (!SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref key, out type,
                            buf, required, out required, 0)) return "";
                    if (type == 0x00000012) return Marshal.PtrToStringUni(buf) ?? "";   // DEVPROP_TYPE_STRING
                    return "";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return ""; }
            finally { if (devInfo != IntPtr.Zero && devInfo.ToInt64() != -1) SetupDiDestroyDeviceInfoList(devInfo); }
        }

        static int ReadBatteryLevel(IntPtr set, ref SP_DEVICE_INTERFACE_DATA did)
        {
            IntPtr devInfo = IntPtr.Zero;
            try
            {
                string path = InterfacePath(set, ref did);
                if (path.Length == 0) return -1;
                devInfo = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                if (devInfo == IntPtr.Zero || devInfo.ToInt64() == -1) return -1;
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                if (!SetupDiOpenDeviceInterfaceW(devInfo, path, 0, ref ifData)) return -1;

                int required = 0, type = 0;
                SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref PkBattery, out type,
                    IntPtr.Zero, 0, out required, 0);
                if (required <= 0) return -1;

                IntPtr buf = Marshal.AllocHGlobal(required < 8 ? 8 : required);
                try
                {
                    if (!SetupDiGetDeviceInterfacePropertyW(devInfo, ref ifData, ref PkBattery, out type,
                            buf, required, out required, 0)) return -1;
                    int v;
                    if (type == 0x00000011) v = Marshal.ReadByte(buf);             // UINT8
                    else if (type == 0x00000002) v = Marshal.ReadInt16(buf);        // UINT16
                    else if (type == 0x00000003) v = Marshal.ReadInt32(buf);        // UINT32
                    else if (type == 0x00000001) v = buf.ToInt64() == 0 ? 0 : 1;    // BOOLEAN
                    else return -1;
                    return (v >= 0 && v <= 100) ? v : -1;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { return -1; }
            finally { if (devInfo != IntPtr.Zero && devInfo.ToInt64() != -1) SetupDiDestroyDeviceInfoList(devInfo); }
        }

        /// <summary>False when the device node is a stale (phantom) instance left behind by an unpair.</summary>
        static bool IsPresentAndConnected(string instanceId)
        {
            try
            {
                uint inst;
                if (CM_Locate_DevNodeW(out inst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != 0) return false;
                int status, problem;
                if (CM_Get_DevNode_Status(out status, out problem, inst, 0) != 0) return false;
                if ((status & DN_DEVICE_PHANTOM) != 0) return false;
                return (status & DN_PRESENT) != 0;
            }
            catch { return true; }   // never hide a reading just because a status call failed
        }
    }
}
