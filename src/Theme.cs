using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MouseMeter
{
    /// <summary>Colour + typography tokens, loosely following the macOS / iOS palette.</summary>
    public class Palette
    {
        public Color Bg;
        public Color Card;
        public Color CardAlt;
        public Color Text;
        public Color TextDim;
        public Color TextFaint;
        public Color Separator;
        public Color Accent;
        public Color Green;
        public Color Orange;
        public Color Red;
        public Color Gray;
        public Color ControlOff;
        public bool IsDark;
    }

    public static class Theme
    {
        public static Palette Light()
        {
            var p = new Palette();
            p.IsDark = false;
            p.Bg = Color.FromArgb(242, 242, 247);
            p.Card = Color.FromArgb(255, 255, 255);
            p.CardAlt = Color.FromArgb(249, 249, 251);
            p.Text = Color.FromArgb(29, 29, 31);
            p.TextDim = Color.FromArgb(134, 134, 139);
            p.TextFaint = Color.FromArgb(174, 174, 180);
            p.Separator = Color.FromArgb(229, 229, 234);
            p.Accent = Color.FromArgb(0, 122, 255);
            p.Green = Color.FromArgb(52, 199, 89);
            p.Orange = Color.FromArgb(255, 159, 10);
            p.Red = Color.FromArgb(255, 69, 58);
            p.Gray = Color.FromArgb(142, 142, 147);
            p.ControlOff = Color.FromArgb(227, 227, 232);
            return p;
        }

        public static Palette Dark()
        {
            var p = new Palette();
            p.IsDark = true;
            p.Bg = Color.FromArgb(28, 28, 30);
            p.Card = Color.FromArgb(44, 44, 46);
            p.CardAlt = Color.FromArgb(36, 36, 38);
            p.Text = Color.FromArgb(245, 245, 247);
            p.TextDim = Color.FromArgb(152, 152, 157);
            p.TextFaint = Color.FromArgb(110, 110, 115);
            p.Separator = Color.FromArgb(56, 56, 58);
            p.Accent = Color.FromArgb(10, 132, 255);
            p.Green = Color.FromArgb(48, 209, 88);
            p.Orange = Color.FromArgb(255, 179, 64);
            p.Red = Color.FromArgb(255, 105, 97);
            p.Gray = Color.FromArgb(142, 142, 147);
            p.ControlOff = Color.FromArgb(56, 56, 58);
            return p;
        }

        public static Palette For(string mode)
        {
            if (mode == "dark") return Dark();
            if (mode == "light") return Light();
            return SystemUsesLight() ? Light() : Dark();
        }

        public static bool SystemUsesLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    if (v is int) return (int)v == 1;
                }
            }
            catch { }
            return false;
        }

        // ---------- typography ----------

        static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        public static readonly string DisplayFamily = PickFamily("Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI");
        public static readonly string TextFamily = PickFamily("Segoe UI Variable Text", "Segoe UI", "Tahoma");
        public static readonly string CjkFamily = PickFamily("Microsoft YaHei UI", "Microsoft YaHei", "SimSun");

        static string PickFamily(params string[] candidates)
        {
            try
            {
                using (var col = new InstalledFontCollection())
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in col.Families) names.Add(f.Name);
                    foreach (var c in candidates) if (names.Contains(c)) return c;
                }
            }
            catch { }
            return "Segoe UI";
        }

        /// <summary>Cached font accessor. size is in points, which WinForms scales by DPI automatically.</summary>
        public static Font F(float size, bool bold)
        {
            return Get("D", DisplayFamily, size, bold);
        }

        public static Font FT(float size, bool bold)
        {
            return Get("T", TextFamily, size, bold);
        }

        public static Font FC(float size, bool bold)
        {
            return Get("C", CjkFamily, size, bold);
        }

        /// <summary>
        /// Fonts are cached and shared, so nobody may dispose them. Sizes are also clamped:
        /// a caller that does arithmetic with a scale factor can end up at 0 pt or below,
        /// which throws inside GDI+ instead of just looking wrong.
        /// </summary>
        static Font Get(string tag, string family, float size, bool bold)
        {
            if (size < 1f) size = 1f;
            if (size > 200f) size = 200f;
            size = (float)Math.Round(size * 2f) / 2f;   // half point steps keep the cache small
            string key = tag + "|" + size.ToString("0.0") + "|" + bold;
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            f = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            _fonts[key] = f;
            return f;
        }

        // ---------- drawing helpers ----------

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(0.1f, radius * 2f);
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Small capsule badge used for the device class (鼠标 / 键盘 / 耳机).</summary>
        public static void FillCapsule(Graphics g, Color color, RectangleF r)
        {
            float rad = Math.Min(r.Height, r.Width) / 2f;
            using (var path = RoundRect(r, rad))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, Color color, RectangleF r, float radius, float width)
        {
            using (var path = RoundRect(r, radius))
            using (var p = new Pen(color, width))
                g.DrawPath(p, path);
        }

        public static void Configure(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        public static Color Alpha(Color c, double a)
        {
            int v = (int)Math.Round(255 * Math.Max(0, Math.Min(1, a)));
            return Color.FromArgb(v, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>Win32 window chrome helpers: rounded corners, dark titlebar, drop shadow.</summary>
    public static class Dwm
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowCompositionAttribData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        public static void RoundedCorners(IntPtr hwnd, bool on)
        {
            int pref = on ? 2 : 1; // 2 = round, 1 = don't round
            try { DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int)); } catch { }
        }

        /// <summary>
        /// Makes the client area consume the DWM material. Without this a frameless window has
        /// no frame to extend and the backdrop is only drawn behind the (invisible) border.
        /// </summary>
        public static void ExtendFrame(IntPtr hwnd)
        {
            ExtendFrame(hwnd, true);
        }

        public static void ExtendFrame(IntPtr hwnd, bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                // Passing -1 for all four margins is the "sheet of glass" idiom.
                var m = new MARGINS();
                m.cxLeftWidth = on ? -1 : 0;
                m.cxRightWidth = on ? -1 : 0;
                m.cyTopHeight = on ? -1 : 0;
                m.cyBottomHeight = on ? -1 : 0;
                DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch { }
        }

        /// <summary>True on Windows 11 22H2 (build 22621) and later, where the DWM backdrop API exists.</summary>
        public static bool BackdropSupported()
        {
            try { return Environment.OSVersion.Version.Build >= 22621; }
            catch { return false; }
        }

        /// <summary>
        /// ACCENT_ENABLE_ACRYLICBLURBEHIND exists since Windows 10 1803. Below that the only
        /// usable state is plain blur (3), which we deliberately do not use: it has no tint and
        /// looks like a bug rather than a material.
        /// </summary>
        public static bool AcrylicSupported()
        {
            try
            {
                var v = Environment.OSVersion.Version;
                return v.Major >= 10 && v.Build >= 17134;
            }
            catch { return false; }
        }

        /// <summary>
        /// Mirrors Settings -> Personalisation -> Colours -> "Transparency effects". When it is
        /// off, DWM ignores the accent policy and we would be left with an unreadable slab, so
        /// the caller has to fall back to the solid card.
        /// </summary>
        public static bool TransparencyEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return true;
                    object v = k.GetValue("EnableTransparency");
                    if (v == null) return true;      // absent on older builds = enabled
                    return Convert.ToInt32(v) != 0;
                }
            }
            catch { return true; }
        }

        /// <summary>Turn the acrylic/blur accent policy on or off.</summary>
        public static void Accent(IntPtr hwnd, bool on)
        {
            Accent(hwnd, on, 0, 0);
        }

        /// <summary>
        /// Apply or clear the acrylic accent policy. <paramref name="tintArgb"/> is the DWM
        /// 0xAABBGGRR colour; <paramref name="alpha"/> is the tint opacity (0-255).
        /// </summary>
        public static void Accent(IntPtr hwnd, bool on, int tintArgb, int alpha)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var accent = new AccentPolicy();
                accent.AccentState = on ? 4 : 0;   // 4 = ACRYLICBLURBEHIND, 0 = disabled
                accent.AccentFlags = on ? 2 : 0;
                // The high byte of GradientColor is the tint alpha that DWM actually honours;
                // the rest of the dword is the colour in ABGR.
                int rgb = tintArgb & 0x00FFFFFF;
                accent.GradientColor = (Math.Max(0, Math.Min(255, alpha)) << 24) | rgb;
                accent.AnimationId = 0;

                int size = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WindowCompositionAttribData();
                    data.Attribute = 19; // WCA_ACCENT_POLICY
                    data.Data = ptr;
                    data.SizeOfData = size;
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            catch { }
        }

        public static void DarkTitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try { DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)); } catch { }
        }

        /// <summary>
        /// Windows 11 22H2+ backdrop: 2=Mica, 3=Acrylic, 4=Tabbed. Returns the HRESULT so the
        /// caller can fall back — some builds and GPU drivers accept the window and then reject
        /// the attribute, and a silent 0 there would leave a fully transparent rectangle.
        /// </summary>
        public static int Backdrop(IntPtr hwnd, int type)
        {
            if (hwnd == IntPtr.Zero) return -1;
            try { return DwmSetWindowAttribute(hwnd, 38, ref type, sizeof(int)); }
            catch { return -1; }
        }

    }
}
