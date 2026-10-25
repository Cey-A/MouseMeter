using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MouseMeter
{
    /// <summary>A single HID top-level collection exposed by Windows.</summary>
    public class HidCollection : HidCollectionLike
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public ushort UsagePage;
        public ushort Usage;
        public int FeatureLength;
        public int InputLength;
        public string ProductName = "";
        public string Manufacturer = "";

        public string Id { get { return Vid.ToString("X4") + ":" + Pid.ToString("X4"); } }

        // explicit HidCollectionLike implementation: same fields, but keeps the struct-free
        // heuristics layer testable without Win32 types.
        int HidCollectionLike.UsagePage { get { return UsagePage; } }
        int HidCollectionLike.Usage { get { return Usage; } }
        int HidCollectionLike.FeatureLength { get { return FeatureLength; } }
        string HidCollectionLike.ProductName { get { return ProductName; } }
        string HidCollectionLike.Manufacturer { get { return Manufacturer; } }

        public bool IsVendorPage { get { return DeviceHeuristics.IsVendorPage(UsagePage); } }

        /// <summary>The device instance path (before the "#{guid}" suffix), used to group sibling collections.</summary>
        public string InstanceKey
        {
            get
            {
                string p = Path ?? "";
                int i = p.ToLowerInvariant().IndexOf("#{");
                return i > 0 ? p.Substring(0, i) : p;
            }
        }
    }

    /// <summary>Thin wrapper over the Win32 HID stack. No third-party dependency.</summary>
    public static class Hid
    {
        const int DIGCF_PRESENT = 0x02;
        const int DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x03;
        const uint OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int len);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetManufacturerString(SafeFileHandle h, StringBuilder buf, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid g, int idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, ref int needed, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        /// <summary>Last Win32 error from the most recent HID call (used for diagnostics).</summary>
        public static int LastError { get { return Marshal.GetLastWin32Error(); } }

        public static string DescribeError(int code)
        {
            switch (code)
            {
                case 0: return "ok";
                case 31: return "err31(休眠)";
                case 87: return "err87(report id/长度)";
                case 5: return "err5(通道被占用)";
                case 1167: return "err1167(设备已移除)";
                case 2: return "err2(未找到设备)";
                default: return "err" + code;
            }
        }

        /// <summary>Enumerate every HID collection currently present. Cheap enough to call per refresh.</summary>
        public static List<HidCollection> Enumerate()
        {
            var list = new List<HidCollection>();
            Guid guid;
            try { HidD_GetHidGuid(out guid); }
            catch { return list; }

            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return list;

            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(did);
            try
            {
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref did)) break;
                    int needed = 0;
                    SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, ref needed, IntPtr.Zero);
                    if (needed <= 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal(needed);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64 (4 + 2 + 2 pad), 6 on x86
                        Marshal.WriteInt32(buf, (IntPtr.Size == 8) ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref did, buf, needed, ref needed, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        var h = CreateFile(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h.IsInvalid) continue;
                        try
                        {
                            var mc = ReadCollection(h, path);
                            if (mc != null) list.Add(mc);
                        }
                        finally { h.Close(); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch (Exception ex) { Log.Warn("hid enumerate: " + ex.Message); }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        public static HidCollection ReadCollection(SafeFileHandle h, string path)
        {
            var attr = new HIDD_ATTRIBUTES();
            attr.Size = Marshal.SizeOf(attr);
            if (!HidD_GetAttributes(h, ref attr)) return null;

            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) return null;
            var caps = new HIDP_CAPS();
            HidP_GetCaps(pp, ref caps);
            HidD_FreePreparsedData(pp);

            var sb = new StringBuilder(256);
            HidD_GetProductString(h, sb, 512);

            var mc = new HidCollection();
            mc.Path = path;
            mc.Vid = attr.VendorID;
            mc.Pid = attr.ProductID;
            mc.UsagePage = caps.UsagePage;
            mc.Usage = caps.Usage;
            mc.FeatureLength = caps.FeatureReportByteLength;
            mc.InputLength = caps.InputReportByteLength;
            mc.ProductName = sb.ToString().Trim();

            var mf = new StringBuilder(256);
            HidD_GetManufacturerString(h, mf, 512);
            mc.Manufacturer = mf.ToString().Trim();
            return mc;
        }

        public static SafeFileHandle Open(string path, bool write)
        {
            uint access = write ? (GENERIC_READ | GENERIC_WRITE) : 0;
            return CreateFile(path, access, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }

        /// <summary>Send a feature report; returns false when the device rejects it.</summary>
        public static bool SetFeature(SafeFileHandle h, byte[] frame)
        {
            return HidD_SetFeature(h, frame, frame.Length);
        }

        /// <summary>Read a feature report; the report id must be pre-seeded. Returns null on failure.</summary>
        public static byte[] GetFeature(SafeFileHandle h, int len, byte reportId)
        {
            var buf = new byte[len];
            buf[0] = reportId;
            return HidD_GetFeature(h, buf, len) ? buf : null;
        }

        /// <summary>Read a feature report that is addressed by id 0 (no report id in the buffer).</summary>
        public static byte[] GetFeatureIdless(SafeFileHandle h, int len)
        {
            var buf = new byte[len];
            return HidD_GetFeature(h, buf, len) ? buf : null;
        }
    }
}
