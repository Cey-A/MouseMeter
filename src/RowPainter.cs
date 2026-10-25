using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>
    /// Shared painting for "a list of battery rows" — used by the desktop widget and the tray
    /// panel, so both always agree on spacing, colours and wording.
    ///
    /// Row geometry (logical px, multiplied by the caller's scale):
    ///   name ......... 9.5 pt, dim, ellipsis
    ///   badge ........ device class, right aligned on the name line as plain caption text
    ///   number ....... 26 pt bold + 11 pt "%"
    ///   status ....... 8.5 pt, faint — 充电中 / transport, left aligned on the meter line
    ///   meter ........ 3 px hairline at the bottom of the row
    /// </summary>
    public static class RowPainter
    {
        public const float NamePt = 9.5f;
        public const float NumPt = 26f;
        public const float PctPt = 11f;
        public const float SmallPt = 8.5f;
        const float NumFloor = 13f;   // below this the number stops reading as the hero

        /// <summary>Accent colour for a reading, following DESIGN.md §7.</summary>
        public static Color Accent(Palette p, Reading r, int low, bool stale)
        {
            if (!r.IsValid) return p.TextFaint;
            if (r.Charging) return p.Green;
            if (r.Level <= 10) return p.Red;
            if (r.Level <= low) return p.Orange;
            return stale ? Theme.Alpha(p.Text, 0.55) : p.Text;
        }

        public static string StatusText(Reading r, Palette p)
        {
            if (!r.IsValid) return "";
            if (r.Charging) return "充电中";
            if (Freshness.IsStale(r)) return "已休眠";
            return r.Transport;
        }

        /// <summary>Badge label: the device class, but for the primary device it stays quiet.</summary>
        public static string BadgeText(Reading r)
        {
            return DeviceKind.LabelOrEmpty(r.Kind);
        }

        /// <summary>Draw one row inside rect (already in device pixels).</summary>
        public static void Draw(Graphics g, Palette p, Reading r, RectangleF rect, float s, float fs,
            int low, bool showBadge)
        {
            bool online = !string.IsNullOrEmpty(r.Kind) || r.IsValid;
            bool known = r.IsValid;
            bool stale = online && Freshness.IsStale(r);
            Color accent = Accent(p, r, low, stale);
            Color ink = online ? (stale ? Theme.Alpha(p.Text, 0.62) : p.Text) : p.TextFaint;

            float pad = 18f * s;

            // ---- name (left) + class caption (right, same baseline) ----
            string name = online ? r.DeviceName : "未检测到设备";
            float badgeW = 0f;
            if (online && showBadge && !BadgeRedundant(r)) badgeW = BadgeWidth(g, r, s, fs);
            float nameW = rect.Width - pad * 2 - badgeW - (badgeW > 0 ? 8f * s : 0f);

            // The badge box must not start exactly on the row's top edge: libgdiplus
            // (and some GDI+ builds) reject a Centre-aligned box that begins on the
            // bitmap grid line, so both captions are nudged up by 1 logical px.
            float capTop = rect.Y - 1f * s;
            float capH = 18f * s;

            var sfName = new StringFormat();
            sfName.LineAlignment = StringAlignment.Center;
            sfName.FormatFlags = StringFormatFlags.NoWrap;
            sfName.Trimming = StringTrimming.EllipsisCharacter;
            using (var b = new SolidBrush(p.TextDim))
                g.DrawString(name, Theme.FC(NamePt * fs, false), b,
                    new RectangleF(rect.X + pad, capTop, nameW, capH), sfName);
            sfName.Dispose();

            if (badgeW > 0)
                DrawBadge(g, p, r, new RectangleF(rect.Right - pad - badgeW, capTop, badgeW, capH), fs);

            // ---- number ----
            // hero line = digits + "%"; it may use everything up to the row's right padding
            string num = known ? r.Level.ToString() : "--";
            float heroBudget = rect.Width - pad * 2;
            float numSize = NumFitSize(g, num, fs, heroBudget);
            Font fNum = Theme.F(numSize, true);
            Font fPct = Theme.F(PctPt * fs, false);
            var sfTypo = StringFormat.GenericTypographic;
            var szNum = g.MeasureString(num, fNum, 999, sfTypo);
            var szPct = g.MeasureString("%", fPct, 999, sfTypo);

            // Sit the digits on a fixed bottom line instead of a fixed top: GDI+ puts extra
            // leading above a point-sized font, and the amount differs between 26 pt and the
            // smaller 3-digit size — a fixed top makes "7" float higher than "100".
            float baseSize = NumSize(num) * fs;
            float baseBottom = rect.Y + 14f * s + g.MeasureString("0", Theme.F(baseSize, true), 999, sfTypo).Height;
            float numTop = baseBottom - szNum.Height;
            using (var b = new SolidBrush(ink))
                g.DrawString(num, fNum, b, rect.X + pad - 1.5f * s, numTop, sfTypo);

            if (known)
            {
                float pctTop = numTop + szNum.Height - szPct.Height - 7f * s;
                using (var b = new SolidBrush(Theme.Alpha(accent, 0.72)))
                    g.DrawString("%", fPct, b, rect.X + pad + szNum.Width + 1f * s, pctTop, sfTypo);
            }

            // ---- status caption, left aligned on the meter line ----
            // "已休眠" is already announced once in the header; repeating it per row is noise.
            string status = known ? StatusText(r, p) : (online ? r.Transport : "");
            if (status == "已休眠") status = "";
            if (status.Length > 0 && rect.Height > 58f * fs)
            {
                var sfStatus = new StringFormat();
                sfStatus.LineAlignment = StringAlignment.Center;
                sfStatus.FormatFlags = StringFormatFlags.NoWrap;
                sfStatus.Trimming = StringTrimming.EllipsisCharacter;
                using (var b = new SolidBrush(r.Charging ? Theme.Alpha(p.Green, 0.95) : p.TextFaint))
                    g.DrawString(status, Theme.FC(SmallPt * fs, false), b,
                        new RectangleF(rect.X + pad, rect.Bottom - 19f * s, rect.Width - pad * 2, 13f * s), sfStatus);
                sfStatus.Dispose();
            }

            // ---- meter ----
            float meterH = 3.5f * s;
            float meterY = rect.Bottom - 3f * s - meterH;
            var track = new RectangleF(rect.X + pad, meterY, rect.Width - pad * 2, meterH);
            float radius = meterH / 2f;

            using (var path = Theme.RoundRect(track, radius))
            using (var b = new SolidBrush(Color.FromArgb(p.IsDark ? 32 : 24, ink)))
                g.FillPath(b, path);

            float pct = known ? Math.Max(0f, Math.Min(1f, r.Level / 100f)) : 0f;
            if (pct > 0.001f)
            {
                var fill = new RectangleF(track.X, meterY, Math.Max(meterH, track.Width * pct), meterH);
                using (var path = Theme.RoundRect(fill, radius))
                using (var b = new SolidBrush(accent))
                    g.FillPath(b, path);
            }
        }

        static float BadgeWidth(Graphics g, Reading r, float s, float fs)
        {
            string t = BadgeText(r);
            // Theme fonts are cached and shared: never wrap them in using(...) — disposing a
            // shared font kills every later DrawString on some GDI+ builds (libgdiplus).
            var sz = g.MeasureString(t, Theme.FC(8f * fs, false), 999, StringFormat.GenericTypographic);
            return sz.Width + 2f * s;
        }

        /// <summary>
        /// Hide when the instance name already ends with the class ("A5 ultra 鼠标"): the badge
        /// would repeat the same word twice on one line.
        /// </summary>
        public static bool BadgeRedundant(Reading r)
        {
            if (string.IsNullOrEmpty(r.DeviceName)) return false;
            string label = BadgeText(r);
            if (label.Length == 0) return false;
            return r.DeviceName.Trim().EndsWith(label, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Plain caption instead of a capsule. On a 196 px card four outlined pills read as
        /// four more controls; quiet text keeps the eye on the numbers.
        /// </summary>
        static void DrawBadge(Graphics g, Palette p, Reading r, RectangleF box, float fs)
        {
            string t = BadgeText(r);
            if (t.Length == 0) return;
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.None;
            using (var b = new SolidBrush(Theme.Alpha(p.TextDim, 0.85)))
                g.DrawString(t, Theme.FC(8f * fs, false), b, box, sf);
            sf.Dispose();
        }

        /// <summary>3 digits need to be ~30% narrower or "100%" overruns the meter.</summary>
        public static float NumSize(string num)
        {
            if (num.Length >= 3) return 19f;
            if (num.Length == 2) return 24f;
            return NumPt;
        }

        /// <summary>
        /// Size for the big number so digit + "%" never eat the whole meter line.
        /// Starts at NumSize() and steps down until the pair fits <paramref name="budget"/>.
        /// MeasureString is only trusted for the first step; the estimate then uses a per-glyph
        /// advance, because the GDI+ measurement of a bold 26 pt string is a few px loose and
        /// font metrics differ between Windows and libgdiplus.
        /// </summary>
        public static float NumFitSize(Graphics g, string num, float fs, float budget)
        {
            float size = NumSize(num) * fs;
            if (budget <= 0f) return size;

            Font fNum = Theme.F(size, true);
            Font fPct = Theme.F(PctPt * fs, false);
            var typo = StringFormat.GenericTypographic;
            float wNum = g.MeasureString(num, fNum, 999, typo).Width;
            float wPct = g.MeasureString("%", fPct, 999, typo).Width;
            float gap = 1f * (fs <= 0f ? 1f : fs);

            for (int guard = 0; guard < 12 && wNum + gap + wPct > budget; guard++)
            {
                float k = (budget - wPct - gap) / Math.Max(0.001f, wNum);
                if (k > 0.985f) k = 0.985f;         // always make progress
                if (k < 0.6f) k = 0.6f;             // one hard step, then re-measure
                size *= k;
                if (size < NumFloor) { size = NumFloor; break; }

                fNum = Theme.F(size, true);
                wNum = g.MeasureString(num, fNum, 999, typo).Width;
                wPct = g.MeasureString("%", fPct, 999, typo).Width;
            }
            return Math.Min(size, NumSize(num) * fs);
        }

        /// <summary>Charging bolt, used by the widget header.</summary>
        public static void DrawBolt(Graphics g, float x, float y, float h, Color c)
        {
            float w = h * 0.64f;
            var pts = new PointF[]
            {
                new PointF(x + w * 0.60f, y),
                new PointF(x + w * 0.08f, y + h * 0.56f),
                new PointF(x + w * 0.44f, y + h * 0.56f),
                new PointF(x + w * 0.32f, y + h),
                new PointF(x + w * 0.94f, y + h * 0.40f),
                new PointF(x + w * 0.52f, y + h * 0.40f)
            };
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(c))
            {
                path.AddPolygon(pts);
                g.FillPath(b, path);
            }
        }

        /// <summary>Offline / empty state text.</summary>
        public static void DrawEmpty(Graphics g, Palette p, RectangleF rect, float s, float fs, string msg)
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            using (var b = new SolidBrush(p.TextFaint))
                g.DrawString(msg, Theme.FC(9.5f * fs, false), b, rect, sf);
            sf.Dispose();
        }
    }
}
