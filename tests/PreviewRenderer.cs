using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace MouseMeter
{
    /// <summary>
    /// Offline renderer used to eyeball the widget / tray glyph without a Windows desktop.
    /// It calls the *production* drawing code (RowPainter, IconFactory, Theme) so what you see
    /// here is what ships. Not loaded by the app itself.
    ///
    ///   dotnet run --project tests/render  (see README)
    /// </summary>
    public static class PreviewRenderer
    {
        // widget geometry
        const int WIDTH = 196, HEADER_H = 34, ROW_H = 64, FOOTER_H = 8, MAX_ROWS = 3;
        // settings geometry
        const int LW = 448, S_HEADER_H = 52, DEV_Y = 60, DEV_ROW = 62, S_ROW_H = 42, SEC_GAP = 26;

        // ---------- widget ----------

        public static Bitmap Widget(List<Reading> rows, string theme, int scalePercent, bool stale)
        {
            var p = theme == "light" ? Theme.Light() : Theme.Dark();
            float scale = scalePercent / 100f;
            int visible = Math.Min(MAX_ROWS, Math.Max(1, rows.Count));
            int logicalH = HEADER_H + ROW_H * visible + FOOTER_H;
            int w = (int)Math.Round(WIDTH * scale);
            int h = (int)Math.Round(logicalH * scale);

            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.Configure(g);
                g.Clear(p.Card);
                float s = w / (float)WIDTH;
                float fs = scale;
                float pad = 18f * s;

                using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), h * 0.115f))
                using (var pen = new Pen(p.IsDark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0), 1f))
                    g.DrawPath(pen, path);

                // header
                // With a single device the row already carries the name — repeating it 34 px
                // above the big number is pure noise. The header then only marks the state.
                var first = rows.Count > 0 ? rows[0] : Reading.Invalid();
                string title = rows.Count == 0 ? "MouseMeter" : (rows.Count == 1 ? "电量" : rows.Count + " 个设备");
                var sfLeft = new StringFormat();
                sfLeft.LineAlignment = StringAlignment.Center;
                sfLeft.FormatFlags = StringFormatFlags.NoWrap;
                sfLeft.Trimming = StringTrimming.EllipsisCharacter;
                using (var b = new SolidBrush(p.TextFaint))
                    g.DrawString(title, Theme.FC(8.5f * fs, false), b,
                        new RectangleF(pad, 8f * s, w - pad * 2 - 64 * s, 18 * s), sfLeft);
                sfLeft.Dispose();

                if (rows.Count > 0 && first.Charging)
                    RowPainter.DrawBolt(g, w - pad - 10 * s, 10f * s, 12 * s, p.Green);
                else if (rows.Count > 0 && stale)
                {
                    var sf = new StringFormat();
                    sf.Alignment = StringAlignment.Far;
                    sf.LineAlignment = StringAlignment.Center;
                    using (var b = new SolidBrush(p.TextFaint))
                        g.DrawString("已休眠", Theme.FC(8f * fs, false), b,
                            new RectangleF(w - pad - 56 * s, 8f * s, 56 * s, 18 * s), sf);
                    sf.Dispose();
                }

                if (rows.Count == 0)
                {
                    RowPainter.DrawEmpty(g, p, new RectangleF(pad, HEADER_H * s, w - pad * 2, ROW_H * s * 0.8f), s, fs,
                        "未检测到设备");
                }
                else
                {
                    for (int i = 0; i < visible && i < rows.Count; i++)
                    {
                        float y = (HEADER_H + ROW_H * i) * s;
                        if (i > 0) Card.Separator(g, p, pad, w - pad, y - 2f * s);
                        RowPainter.Draw(g, p, rows[i], new RectangleF(0, y, w, (ROW_H - 6) * s), s, fs, 20, true);
                    }
                    if (rows.Count > MAX_ROWS)
                    {
                        // match HudForm.DrawScrollHint: centre the hint inside the footer band
                        var sf = new StringFormat();
                        sf.Alignment = StringAlignment.Far;
                        sf.LineAlignment = StringAlignment.Center;
                        float fh = 11f * s;
                        using (var b = new SolidBrush(Theme.Alpha(p.TextFaint, 0.8)))
                            g.DrawString("1–3 / " + rows.Count, Theme.FC(7.5f * fs, false), b,
                                new RectangleF(w - pad - 70 * s, h - FOOTER_H * s - 4f * s + (FOOTER_H * s - fh) * 0.5f, 70 * s, fh), sf);
                        sf.Dispose();
                    }
                }
            }
            return bmp;
        }

        // ---------- settings ----------

        public static Bitmap SettingsWindow(List<Reading> rows, string theme, bool withDevices)
        {
            var p = theme == "light" ? Theme.Light() : Theme.Dark();
            var list = withDevices ? rows : new List<Reading>();
            int devH = DEV_ROW * Math.Max(1, Math.Min(4, list.Count)) + 24;

            int sec1Label = DEV_Y + devH + 10;
            int sec1Y = sec1Label + SEC_GAP + 8;
            int sec2Label = sec1Y + 3 * S_ROW_H + 10;
            int sec2Y = sec2Label + SEC_GAP + 8;
            int sec3Label = sec2Y + 5 * S_ROW_H + 10;
            int sec3Y = sec3Label + SEC_GAP + 8;
            int h = sec3Y + 3 * S_ROW_H + 52;

            var bmp = new Bitmap(LW, h);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.Configure(g);
                g.Clear(p.Bg);
                int margin = 20, cardW = LW - margin * 2;

                using (var icon = IconFactory.AppIcon(24)) g.DrawImage(icon, margin, 14, 24, 24);
                using (var b = new SolidBrush(p.Text))
                    g.DrawString("MouseMeter", Theme.F(12.5f, true), b, margin + 34, 12);

                // close glyph
                using (var pen = new Pen(Theme.Alpha(p.TextDim, 0.85), 1.4f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    float size = 26, x = LW - 18 - size, y = 13, pad = size * 0.34f;
                    g.DrawLine(pen, x + pad, y + pad, x + size - pad, y + size - pad);
                    g.DrawLine(pen, x + size - pad, y + pad, x + pad, y + size - pad);
                }

                var devCard = new RectangleF(margin, DEV_Y, cardW, devH);
                Card.Draw(g, p, devCard, 12);
                DrawDeviceRows(g, p, devCard, list);

                Section(g, p, "显示", sec1Label);
                var g1 = new RectangleF(margin, sec1Y, cardW, 3 * S_ROW_H);
                Card.Draw(g, p, g1, 12);
                Row(g, p, "桌面挂件", g1, 0);
                Sep(g, p, g1, S_ROW_H);
                Row(g, p, "挂件外观", g1, 1);
                Sep(g, p, g1, S_ROW_H * 2);
                Row(g, p, "挂件不透明度", g1, 2);
                Value(g, p, "96%", g1, 2);
                Switch(g, p, g1, 0, true);
                SegmentedPreview(g, p, new RectangleF(g1.Right - 16 - 198, g1.Y + S_ROW_H + 7, 198, 28), 2);
                Slider(g, p, new RectangleF(g1.Right - 16 - 196, g1.Y + S_ROW_H * 2 + 9, 150, 24), 0.9);

                Section(g, p, "通用", sec2Label);
                var g2 = new RectangleF(margin, sec2Y, cardW, 5 * S_ROW_H);
                Card.Draw(g, p, g2, 12);
                Row(g, p, "刷新间隔", g2, 0);
                Button(g, p, "1 分钟", new RectangleF(g2.Right - 16 - 96, g2.Y + 7, 96, 28));
                Sep(g, p, g2, S_ROW_H);
                Row(g, p, "低电量提醒", g2, 1);
                Switch(g, p, g2, 1, true);
                Sep(g, p, g2, S_ROW_H * 2);
                Row(g, p, "提醒阈值", g2, 2);
                Value(g, p, "20%", g2, 2);
                Slider(g, p, new RectangleF(g2.Right - 16 - 196, g2.Y + S_ROW_H * 2 + 9, 150, 24), 0.33);
                Sep(g, p, g2, S_ROW_H * 3);
                Row(g, p, "自适应刷新", g2, 3);
                Switch(g, p, g2, 3, true);
                Sep(g, p, g2, S_ROW_H * 4);
                Row(g, p, "开机自动启动", g2, 4);
                Switch(g, p, g2, 4, true);

                Section(g, p, "设备识别", sec3Label);
                var g3 = new RectangleF(margin, sec3Y, cardW, 3 * S_ROW_H);
                Card.Draw(g, p, g3, 12);
                Row(g, p, "私有协议直读（鼠标/键盘）", g3, 0);
                Switch(g, p, g3, 0, true);
                Sep(g, p, g3, S_ROW_H);
                Row(g, p, "蓝牙电量（BLE）", g3, 1);
                Switch(g, p, g3, 1, true);
                Sep(g, p, g3, S_ROW_H * 2);
                Row(g, p, "系统电池接口（耳机）", g3, 2);
                Switch(g, p, g3, 2, true);

                using (var b = new SolidBrush(p.TextFaint))
                    g.DrawString("3.0 · 直读 HID 接收器 + 系统电池接口", Theme.FC(8.5f, false), b, margin + 2, h - 38);
                Button(g, p, "复制诊断信息", new RectangleF(LW - 20 - 110, h - 46, 110, 30));

                using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, LW - 1f, h - 1f), 12))
                using (var pen = new Pen(p.IsDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0), 1f))
                    g.DrawPath(pen, path);
            }
            return bmp;
        }

        static void DrawDeviceRows(Graphics g, Palette p, RectangleF card, List<Reading> list)
        {
            if (list.Count == 0)
            {
                g.DrawString("未检测到设备", Theme.FC(11f, true), new SolidBrush(p.TextDim),
                    card.X + 80, card.Y + 20);
                return;
            }
            int n = Math.Min(4, list.Count);
            for (int i = 0; i < n; i++)
            {
                var rd = list[i];
                bool has = rd.IsValid;
                bool stale = has && Freshness.IsStale(rd);
                bool present = has || !string.IsNullOrEmpty(rd.Kind);
                bool charging = has && rd.Charging;
                Color accent = charging ? p.Green : (!has ? p.TextFaint
                    : (rd.Level <= 10 ? p.Red : (rd.Level <= 20 ? p.Orange : p.Accent)));

                float rowY = card.Y + 12 + i * DEV_ROW;
                float cx = card.X + 46, cy = rowY + DEV_ROW / 2f - 4, radius = 21, thick = 3;
                var ring = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
                using (var pen = new Pen(p.Separator, thick)) g.DrawEllipse(pen, ring);
                if (has)
                {
                    using (var pen = new Pen(accent, thick))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, ring, -90f, 360f * Math.Max(1f, rd.Level) / 100f);
                    }
                }
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (var b = new SolidBrush(has ? p.Text : p.TextFaint))
                    g.DrawString(has ? rd.Level.ToString() : "--", Theme.F(has ? 13f : 12f, true), b,
                        new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2), sf);
                sf.Dispose();

                float tx = card.X + 80, tw = card.Width - (tx - card.X) - 16;
                var nameSf = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
                using (var b = new SolidBrush(p.Text))
                    g.DrawString(rd.DeviceName, Theme.FC(11f, true), b, new RectangleF(tx, rowY + 9, tw - 52, 20), nameSf);

                var badge = new RectangleF(card.Right - 16 - 40, rowY + 11, 40, 15);
                Theme.FillCapsule(g, p.IsDark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0), badge);
                var bsf = new StringFormat();
                bsf.Alignment = StringAlignment.Center;
                bsf.LineAlignment = StringAlignment.Center;
                using (var b = new SolidBrush(p.TextDim))
                    g.DrawString(DeviceKind.LabelOrEmpty(rd.Kind), Theme.FC(7.5f, false), b, badge, bsf);
                bsf.Dispose();

                using (var b = new SolidBrush(p.TextDim))
                    g.DrawString(rd.Transport + " · " + rd.HardwareId, Theme.FT(9f, false), b,
                        new RectangleF(tx, rowY + 30, tw, 16), nameSf);
                nameSf.Dispose();

                using (var b = new SolidBrush(!present ? p.TextFaint : (charging ? p.Green : (stale ? p.Orange : p.Green))))
                    g.FillEllipse(b, tx, rowY + 50, 6, 6);
                string l2 = !present ? "等待连接" : (!has ? "已连接 · 电量未知（Windows 未上报）"
                    : (charging ? "正在充电 · 刚刚更新" : (stale ? "已休眠 · 上次 3 分钟前" : "已连接 · 刚刚更新")));
                using (var b = new SolidBrush(p.TextFaint))
                    g.DrawString(l2, Theme.FC(8.5f, false), b, new RectangleF(tx + 12, rowY + 47, tw - 12, 16),
                        new StringFormat { FormatFlags = StringFormatFlags.NoWrap });

                if (i < n - 1) Card.Separator(g, p, card.X + 16, card.Right - 16, rowY + DEV_ROW);
            }
        }

        static void Section(Graphics g, Palette p, string t, int y)
        {
            using (var b = new SolidBrush(p.TextDim))
                g.DrawString(t, Theme.FC(9.5f, false), b, 22, y);
        }

        static void Row(Graphics g, Palette p, string label, RectangleF card, int row)
        {
            var sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(p.Text))
                g.DrawString(label, Theme.FC(10f, false), b,
                    new RectangleF(card.X + 16, card.Y + row * S_ROW_H, 230, S_ROW_H), sf);
            sf.Dispose();
        }

        static void Value(Graphics g, Palette p, string t, RectangleF card, int row)
        {
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            using (var b = new SolidBrush(p.TextDim))
                g.DrawString(t, Theme.FT(9.5f, false), b,
                    new RectangleF(card.Right - 16 - 44, card.Y + row * S_ROW_H, 44, S_ROW_H), sf);
            sf.Dispose();
        }

        static void Sep(Graphics g, Palette p, RectangleF card, int rowOffset)
        {
            Card.Separator(g, p, card.X + 16, card.Right - 16, card.Y + rowOffset);
        }

        static void Switch(Graphics g, Palette p, RectangleF card, int row, bool on)
        {
            float w = 46, h = 27;
            var track = new RectangleF(card.Right - 16 - w, card.Y + row * S_ROW_H + 7 + h * 0.08f, w, h - h * 0.16f);
            using (var path = Theme.RoundRect(track, track.Height / 2))
            using (var b = new SolidBrush(on ? p.Green : (p.IsDark ? p.ControlOff : Color.FromArgb(226, 226, 231))))
                g.FillPath(b, path);
            float pad = track.Height * 0.11f, d = track.Height - pad * 2;
            float x = on ? track.Right - pad - d : track.X + pad;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, track.Y + pad, d, d);
        }

        static void Slider(Graphics g, Palette p, RectangleF box, double ratio)
        {
            float th = 4;
            var t = new RectangleF(box.X + th, box.Y + (box.Height - th) / 2, box.Width - th * 2, th);
            using (var path = Theme.RoundRect(t, th / 2))
            using (var b = new SolidBrush(p.IsDark ? p.ControlOff : Color.FromArgb(226, 226, 231)))
                g.FillPath(b, path);
            var f = new RectangleF(t.X, t.Y, (float)(t.Width * ratio), t.Height);
            using (var path = Theme.RoundRect(f, th / 2))
            using (var b = new SolidBrush(p.Accent)) g.FillPath(b, path);
            float d = 18, cx = t.X + (float)(t.Width * ratio), cy = box.Y + box.Height / 2;
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
            using (var pen = new Pen(Color.FromArgb(18, 0, 0, 0), 1f)) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d - .5f, d - .5f);
        }

        static void SegmentedPreview(Graphics g, Palette p, RectangleF box, int selected)
        {
            string[] items = new string[] { "跟随系统", "浅色", "深色" };
            using (var path = Theme.RoundRect(box, box.Height * 0.30f))
            using (var b = new SolidBrush(p.IsDark ? Color.FromArgb(58, 58, 60) : Color.FromArgb(228, 228, 233)))
                g.FillPath(b, path);
            float seg = box.Width / items.Length, pad = 2.5f;
            var sel = new RectangleF(box.X + selected * seg + pad, box.Y + pad, seg - pad * 2, box.Height - pad * 2);
            using (var path = Theme.RoundRect(sel, sel.Height * 0.30f))
            using (var b = new SolidBrush(p.IsDark ? Color.FromArgb(99, 99, 102) : Color.White))
                g.FillPath(b, path);
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            for (int i = 0; i < items.Length; i++)
            {
                using (var b = new SolidBrush(i == selected ? p.Text : p.TextDim))
                    g.DrawString(items[i], Theme.FC(9f, i == selected), b,
                        new RectangleF(box.X + i * seg, box.Y, seg, box.Height), sf);
            }
            sf.Dispose();
        }

        static void Button(Graphics g, Palette p, string text, RectangleF r)
        {
            using (var path = Theme.RoundRect(r, r.Height * 0.30f))
            using (var b = new SolidBrush(p.IsDark ? Color.FromArgb(58, 58, 60) : Color.FromArgb(233, 233, 238)))
                g.FillPath(b, path);
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            using (var b = new SolidBrush(p.Text))
                g.DrawString(text, Theme.FC(9f, false), b, r, sf);
            sf.Dispose();
        }

        // ---------- contact sheet  ----------

        // Bisect helper: run one stage at a time to find which call poisons the Graphics.
        public static void Bisect()
        {
            var now = DateTime.Now;
            var mouse = Fake("hid|m|mouse", DeviceKind.Mouse, "A5 ultra", 29, false, "2.4G", "0BDA:4762", now);
            var one = new List<Reading>(); one.Add(mouse);
            Step("1 Widget(dark,1 row)", delegate { using (var b = Widget(one, "dark", 100, false)) { } });
            Step("2 Widget(dark,4 rows)", delegate
            {
                var all = new List<Reading>();
                all.Add(mouse);
                all.Add(Fake("k", DeviceKind.Keyboard, "K87 Pro", 82, false, "2.4G", "0BDA:4762", now));
                all.Add(Fake("h", DeviceKind.Headset, "WH-1000XM5", 64, false, "蓝牙", "F5CE", now));
                all.Add(Fake("h2", DeviceKind.Headset, "FreeBuds 5i", -1, false, "蓝牙", "", now));
                using (var b = Widget(all, "dark", 100, false)) { }
            });
            Step("3 Settings(light)", delegate
            {
                var all = new List<Reading>(); all.Add(mouse);
                using (var b = SettingsWindow(all, "light", true)) { }
            });
            Step("4 tray glyphs", delegate { using (var b = IconFactory.Render(32, 29, IconFactory.State.Normal, true, DeviceKind.Mouse)) { } });
        }

        static void Step(string name, Action a)
        {
            try { a(); Console.WriteLine(name + " OK"); }
            catch (Exception ex) { Console.WriteLine(name + " FAIL " + ex.GetType().Name + " @ " + FirstLine(ex.StackTrace)); }
        }

        static string FirstLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            int i = t.IndexOf('\n');
            return i > 0 ? t.Substring(0, i).Trim() : t;
        }

        public static void WriteAll(string outDir)
        {
            Directory.CreateDirectory(outDir);
            var now = DateTime.Now;

            var mouse = Fake("hid|m|mouse", DeviceKind.Mouse, "A5 ultra", 29, false, "2.4G", "0BDA:4762", now);
            var kbd = Fake("hid|k|keyboard", DeviceKind.Keyboard, "K87 Pro", 82, false, "2.4G", "0BDA:4762", now);
            var head = Fake("sys|h|headset", DeviceKind.Headset, "WH-1000XM5", 64, false, "蓝牙", "F5CE340CE1CB", now);
            var headUnknown = Fake("sys|h2|headset", DeviceKind.Headset, "FreeBuds 5i", -1, false, "蓝牙", "", now);
            var charge = Fake("hid|c|mouse", DeviceKind.Mouse, "A5 ultra", 96, true, "2.4G", "0BDA:4762", now);

            var all = new List<Reading>();
            all.Add(mouse); all.Add(kbd); all.Add(head); all.Add(headUnknown);

            var one = new List<Reading>(); one.Add(mouse);
            var empty = new List<Reading>();

            Widget(one, "dark", 100, false).Save(Path.Combine(outDir, "widget-dark-1.png"));
            Widget(all, "dark", 100, false).Save(Path.Combine(outDir, "widget-dark-4.png"));
            Widget(all, "light", 100, false).Save(Path.Combine(outDir, "widget-light-4.png"));
            Widget(empty, "dark", 100, false).Save(Path.Combine(outDir, "widget-empty.png"));

            // number-width stress: 100 must not collide with the "%" or the meter end
            var wide = new List<Reading>();
            wide.Add(Fake("hid|w1|mouse", DeviceKind.Mouse, "A5 ultra", 100, false, "2.4G", "0BDA:4762", now));
            wide.Add(Fake("hid|w2|keyboard", DeviceKind.Keyboard, "K87 Pro", 100, true, "2.4G", "0BDA:4762", now));
            wide.Add(Fake("sys|w3|headset", DeviceKind.Headset, "WH-1000XM5", 7, false, "蓝牙", "F5CE", now));
            Widget(wide, "dark", 100, false).Save(Path.Combine(outDir, "widget-numbers.png"));
            Widget(wide, "light", 100, false).Save(Path.Combine(outDir, "widget-numbers-light.png"));
            var c1 = new List<Reading>(); c1.Add(charge);
            Widget(c1, "dark", 100, false).Save(Path.Combine(outDir, "widget-charging.png"));
            var st = Fake("hid|s|mouse", DeviceKind.Mouse, "A5 ultra", 12, false, "2.4G", "0BDA:4762", now.AddMinutes(-4));
            var sl = new List<Reading>(); sl.Add(st); sl.Add(kbd);
            Widget(sl, "dark", 100, true).Save(Path.Combine(outDir, "widget-stale.png"));

            SettingsWindow(all, "light", true).Save(Path.Combine(outDir, "settings-light.png"));
            SettingsWindow(all, "dark", true).Save(Path.Combine(outDir, "settings-dark.png"));
            SettingsWindow(empty, "light", false).Save(Path.Combine(outDir, "settings-empty.png"));

            // tray glyphs on a dark and a light taskbar
            var states = new IconFactory.State[] {
                IconFactory.State.Normal, IconFactory.State.Low,
                IconFactory.State.Critical, IconFactory.State.Charging, IconFactory.State.Offline };
            int[] levels = new int[] { 100, 96, 82, 36, 0 };
            string[] kinds = new string[] { DeviceKind.Mouse, DeviceKind.Keyboard, DeviceKind.Headset };

            using (var sheet = new Bitmap(11 * 40 + 20, (states.Length * kinds.Length) * 44 + 20))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(32, 32, 34));
                for (int r = 0; r < states.Length; r++)
                {
                    for (int k = 0; k < kinds.Length; k++)
                    {
                        using (var bmp = IconFactory.Render(32, levels[r], states[r], true, kinds[k]))
                            g.DrawImage(bmp, 20 + r * 120, 20 + (k) * 64, 32, 32);
                        using (var bmp = IconFactory.Render(16, levels[r], states[r], true, kinds[k]))
                            g.DrawImage(bmp, 20 + r * 120 + 40, 20 + k * 64, 16, 16);
                    }
                }
                sheet.Save(Path.Combine(outDir, "tray-glyphs.png"));
            }

            Console.WriteLine("preview written to " + outDir);
        }

        static Reading Fake(string id, string kind, string name, int level, bool charging, string transport, string hwid, DateTime at)
        {
            var r = new Reading();
            r.Id = id; r.Kind = kind; r.DeviceName = name; r.Level = level; r.Charging = charging;
            r.Transport = transport; r.HardwareId = hwid; r.At = at; r.Source = "preview";
            return r;
        }
    }
}
