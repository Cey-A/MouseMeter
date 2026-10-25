using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>Draws the tray glyph and the application icon at any DPI.</summary>
    public static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        public enum State { Normal, Low, Critical, Charging, Offline }

        public static State StateOf(Reading r, bool hasReading, bool stale, int lowThreshold)
        {
            if (!hasReading || !r.IsValid) return State.Offline;
            if (r.Charging) return State.Charging;
            if (r.Level <= 10) return State.Critical;
            if (r.Level <= lowThreshold) return State.Low;
            return State.Normal;
        }

        public static State StateOf(Reading r, int lowThreshold)
        {
            return StateOf(r, r.IsValid, Freshness.IsStale(r), lowThreshold);
        }

        public static Color AccentFor(State s, Palette p)
        {
            switch (s)
            {
                case State.Charging: return p.Green;
                case State.Critical: return p.Red;
                case State.Low: return p.Orange;
                case State.Offline: return p.Gray;
                default: return p.Text;
            }
        }

        /// <summary>
        /// Minimal glyph: the percentage, with a hairline meter underneath.
        /// Rendered at the exact pixel size the shell asks for, so nothing is resampled.
        /// </summary>
        public static Bitmap Render(int size, int level, State state, bool darkTaskbar)
        {
            return Render(size, level, state, darkTaskbar, "");
        }

        /// <summary>
        /// Same glyph, with the device class hinted by a tiny mark in the corner so a mouse, a
        /// keyboard and a headset icon are tellable apart at 16 px without a legend.
        /// </summary>
        public static Bitmap Render(int size, int level, State state, bool darkTaskbar, string kind)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                Color ink = InkFor(state, darkTaskbar);

                bool showMeter = state != State.Offline && size >= 14;
                float meterH = Math.Max(1.5f, size / 12f);
                float meterY = size - meterH - Math.Max(0.5f, size * 0.06f);
                float textArea = showMeter ? meterY - size * 0.045f : size;

                // the class mark takes the top-left corner; shrink the digits a touch to make room
                float markW = KindMarkWidth(kind, size);
                if (markW > 0)
                {
                    using (var b = new SolidBrush(Theme.Alpha(ink, 0.75)))
                        DrawKindMark(g, kind, 0.5f, size * 0.06f, markW, size * 0.30f, b);
                }

                string label = state == State.Offline ? "--" : level.ToString();
                float maxW = size - size * 0.06f;
                Font font = FitFont(g, label, maxW, textArea);

                var sf = new StringFormat(StringFormat.GenericTypographic);
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

                // generous box, centred on the glyph area, so the text never wraps or clips
                var box = new RectangleF(-size, -size * 0.10f, size * 3, textArea + size * 0.20f);

                // nudge down by the descender so digits look optically centred
                g.TranslateTransform(0, size * 0.055f);
                using (var b = new SolidBrush(ink))
                    g.DrawString(label, font, b, box, sf);
                g.TranslateTransform(0, -size * 0.055f);

                if (showMeter)
                {
                    float pad = size * 0.16f;
                    float trackW = size - pad * 2;
                    float pct = Math.Max(0f, Math.Min(1f, level / 100f));
                    float fillW = Math.Max(meterH, trackW * pct);

                    using (var back = new SolidBrush(Color.FromArgb(darkTaskbar ? 66 : 54, ink)))
                        FillCapsule(g, back, pad, meterY, trackW, meterH);

                    using (var front = new SolidBrush(ink))
                        FillCapsule(g, front, pad, meterY, fillW, meterH);
                }

                sf.Dispose();
            }
            return bmp;
        }

        static Color InkFor(State state, bool darkTaskbar)
        {
            if (state == State.Offline) return darkTaskbar
                ? Color.FromArgb(170, 235, 235, 240)
                : Color.FromArgb(170, 60, 60, 66);
            if (state == State.Charging) return Color.FromArgb(48, 205, 88);
            if (state == State.Critical) return Color.FromArgb(255, 71, 58);
            if (state == State.Low) return Color.FromArgb(255, 159, 10);
            return darkTaskbar ? Color.FromArgb(246, 246, 248) : Color.FromArgb(30, 30, 32);
        }

        // ------------------------------------------------------------------
        // device class marks
        // ------------------------------------------------------------------

        static float KindMarkWidth(string kind, int size)
        {
            // Kept deliberately small: the digits are the content, the badge only has to
            // whisper the device class. Anything wider than ~1/3 of the glyph reads as a logo.
            if (kind == DeviceKind.Keyboard) return size * 0.30f;
            if (kind == DeviceKind.Headset) return size * 0.26f;
            return 0f;   // mouse keeps the lucky original look: digits only
        }

        /// <summary>Keyboard: three key dots in a rounded box. Headset: arc + two ear cups.</summary>
        static void DrawKindMark(Graphics g, string kind, float x, float y, float w, float h, Brush b)
        {
            if (kind == DeviceKind.Keyboard)
            {
                using (var path = Theme.RoundRect(new RectangleF(x, y, w, h), h * 0.28f))
                using (var pen = new Pen(((SolidBrush)b).Color, Math.Max(1f, h * 0.16f)))
                    g.DrawPath(pen, path);
                float d = Math.Max(0.9f, h * 0.18f);
                float cy = y + h * 0.5f - d / 2f;
                for (int i = 0; i < 3; i++)
                    g.FillEllipse(b, x + w * (0.22f + i * 0.28f) - d / 2f, cy, d, d);
            }
            else if (kind == DeviceKind.Headset)
            {
                float stroke = Math.Max(1f, h * 0.17f);
                using (var pen = new Pen(((SolidBrush)b).Color, stroke))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, x + w * 0.06f, y, w * 0.88f, h * 0.92f, 180f, 180f);
                }
                float cw = w * 0.22f, ch = h * 0.42f;
                g.FillRectangle(b, x + w * 0.04f, y + h * 0.48f, cw, ch);
                g.FillRectangle(b, x + w * 0.74f, y + h * 0.48f, cw, ch);
            }
        }

        static Font FitFont(Graphics g, string label, float maxW, float maxH)
        {
            float[] steps = new float[] { 0.78f, 0.74f, 0.70f, 0.66f, 0.62f, 0.58f, 0.54f, 0.50f, 0.46f, 0.42f, 0.38f };
            for (int i = 0; i < steps.Length; i++)
            {
                float candidate = Math.Min(maxH * steps[i], maxW * 0.98f);
                var f = new Font(Theme.DisplayFamily, candidate, FontStyle.Bold, GraphicsUnit.Pixel);
                var sz = g.MeasureString(label, f, 1000, StringFormat.GenericTypographic);
                if (sz.Width <= maxW && sz.Height <= maxH * 1.04f) return f;
                f.Dispose();
            }
            float px = Math.Min(maxH, maxW) * 0.40f;
            return new Font(Theme.DisplayFamily, px, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        static void FillCapsule(Graphics g, Brush b, float x, float y, float w, float h)
        {
            float r = h / 2f;
            using (var path = new GraphicsPath())
            {
                if (w <= h)
                {
                    path.AddEllipse(x, y, h, h);
                }
                else
                {
                    path.AddArc(x, y, h, h, 90, 180);
                    path.AddArc(x + w - h, y, h, h, 270, 180);
                    path.CloseFigure();
                }
                g.FillPath(b, path);
            }
        }

        public static Icon ToIcon(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
            finally { DestroyIcon(h); }
        }

        // ------------------------------------------------------------------
        // Application icon (also used for the settings window title block)
        // ------------------------------------------------------------------

        public static Bitmap AppIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float inset = size * 0.055f;
                var rect = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
                float radius = size * 0.235f;

                using (var path = Theme.RoundRect(rect, radius))
                using (var lg = new LinearGradientBrush(rect,
                    Color.FromArgb(58, 58, 62), Color.FromArgb(22, 22, 24), 90f))
                {
                    g.FillPath(lg, path);
                }

                using (var path = Theme.RoundRect(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * 0.5f), radius))
                using (var lg = new LinearGradientBrush(
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height * 0.5f),
                    Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    g.FillPath(lg, path);
                }

                float bw = size * 0.46f;
                float bh = size * 0.24f;
                float bx = (size - bw) / 2f - size * 0.035f;
                float by = (size - bh) / 2f;
                float br = bh * 0.32f;
                float stroke = Math.Max(1f, size * 0.038f);

                var body = new RectangleF(bx, by, bw, bh);
                using (var pen = new Pen(Color.FromArgb(245, 245, 247), stroke))
                using (var path = Theme.RoundRect(body, br))
                    g.DrawPath(pen, path);

                var nub = new RectangleF(bx + bw + size * 0.012f, by + bh * 0.28f, size * 0.045f, bh * 0.44f);
                using (var path = Theme.RoundRect(nub, nub.Width * 0.45f))
                using (var b = new SolidBrush(Color.FromArgb(245, 245, 247)))
                    g.FillPath(b, path);

                float inner = bw - stroke * 2.4f;
                var fill = new RectangleF(bx + stroke * 1.2f, by + stroke * 1.2f, inner * 0.62f, bh - stroke * 2.4f);
                using (var path = Theme.RoundRect(fill, Math.Max(1f, br - stroke)))
                using (var b = new SolidBrush(Color.FromArgb(48, 209, 88)))
                    g.FillPath(b, path);
            }
            return bmp;
        }

        /// <summary>Writes a multi-resolution .ico (PNG compressed frames, Vista+).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (var bmp = AppIcon(s))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write((uint)pngs[i].Length);
                    w.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < pngs.Count; i++) w.Write(pngs[i]);
            }
        }
    }
}
