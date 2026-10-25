using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>Preferences window, modelled on macOS System Settings.</summary>
    public class SettingsForm : Form
    {
        const int LW = 448;
        const int LH = 566;

        // vertical rhythm (logical px). The device card grows with the device count, so every
        // section below it is anchored to the card's bottom edge instead of a magic constant.
        const int HEADER_H = 52;
        const int DEV_Y = 60;
        const int DEV_ROW = 62;
        const int ROW_H = 42;
        const int SEC_GAP = 26;   // label -> card

        readonly Settings _cfg;
        readonly DeviceManager _dm;
        Palette _p;

        int _dpi = 96;
        bool _built;
        float _hoverClose;
        bool _drag;
        Point _dragStart;
        int _devH = DEV_ROW * 2 + 24;

        ToggleSwitch _swHud;
        ToggleSwitch _swNotify;
        ToggleSwitch _swStartup;
        ToggleSwitch _swHid;
        ToggleSwitch _swBle;
        ToggleSwitch _swPnp;
        ToggleSwitch _swAdaptive;
        Segmented _segTheme;
        MMSlider _slOpacity;
        MMSlider _slThreshold;
        FlatButton _btnInterval;
        FlatButton _btnDiag;

        public event EventHandler Changed;

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        public SettingsForm(Settings cfg, DeviceManager dm)
        {
            _cfg = cfg;
            _dm = dm;
            _p = Theme.For(cfg.Theme);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MaximizeBox = false;
            MinimizeBox = false;
            KeyPreview = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _p.Bg;
            Text = "MouseMeter";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            try { Icon = IconFactory.ToIcon(IconFactory.AppIcon(32)); }
            catch { }

            CreateControl();
        }

        float Sf { get { return _dpi / 96f; } }
        int X(float v) { return (int)Math.Round(v * Sf); }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            uint d = 0;
            try { d = GetDpiForWindow(Handle); } catch { }
            if (d >= 96 && d <= 480) _dpi = (int)d;

            ClientSize = new Size(X(LW), X(LH));
            ApplyRoundedRegion();

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);

            Dwm.RoundedCorners(Handle, true);
            Dwm.DarkTitleBar(Handle, _p.IsDark);

            if (!_built)
            {
                _built = true;
                BuildControls();
                SyncFromConfig();
            }
        }

        void ApplyRoundedRegion()
        {
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), X(12)))
                Region = new Region(path);
        }

        // ------------------------------------------------------------------
        // layout
        // ------------------------------------------------------------------

        int RightEdge { get { return X(LW - 20 - 16); } }

        int Sec1Label { get { return DEV_Y + _devH + 10; } }
        int Sec1Y { get { return Sec1Label + SEC_GAP + 8; } }   // 3 rows
        int Sec2Label { get { return Sec1Y + 3 * ROW_H + 10; } }
        int Sec2Y { get { return Sec2Label + SEC_GAP + 8; } }   // 5 rows
        int Sec3Label { get { return Sec2Y + 5 * ROW_H + 10; } }
        int Sec3Y { get { return Sec3Label + SEC_GAP + 8; } }   // 3 rows

        int DevRowCount
        {
            get
            {
                var list = _dm.Snapshot();
                return Math.Max(1, Math.Min(4, list.Count));
            }
        }

        void RecalcLayout()
        {
            int newDevH = DEV_ROW * DevRowCount + 24;
            if (newDevH == _devH) return;
            _devH = newDevH;
            int h = Sec3Y + 3 * ROW_H + 52;
            if (ClientSize.Height != X(h)) ClientSize = new Size(X(LW), X(h));
            ApplyRoundedRegion();
            LayoutControls();
        }

        void BuildControls()
        {
            Color card = _p.Card;

            _swHud = MakeSwitch();
            _swHud.CheckedChanged += delegate { _cfg.HudEnabled = _swHud.Checked; Commit(); };

            _segTheme = new Segmented(_p);
            _segTheme.Items = new string[] { "跟随系统", "浅色", "深色" };
            _segTheme.Size = new Size(X(198), X(28));
            _segTheme.BackColor = card;
            _segTheme.SelectionChanged += delegate
            {
                string[] modes = new string[] { "system", "light", "dark" };
                _cfg.HudTheme = modes[_segTheme.SelectedIndex];
                Commit();
            };
            Controls.Add(_segTheme);

            _slOpacity = new MMSlider(_p);
            _slOpacity.Minimum = 55;
            _slOpacity.Maximum = 100;
            _slOpacity.Size = new Size(X(150), X(24));
            _slOpacity.BackColor = card;
            _slOpacity.ValueChanged += delegate
            {
                _cfg.HudOpacity = _slOpacity.Value;
                Invalidate();
                Commit();
            };
            Controls.Add(_slOpacity);

            _btnInterval = new FlatButton(_p, "1 分钟");
            _btnInterval.Size = new Size(X(96), X(28));
            _btnInterval.BackColor = card;
            _btnInterval.Clicked += delegate { ShowIntervalMenu(); };
            Controls.Add(_btnInterval);

            _swNotify = MakeSwitch();
            _swNotify.CheckedChanged += delegate { _cfg.NotifyLow = _swNotify.Checked; Commit(); };

            _slThreshold = new MMSlider(_p);
            _slThreshold.Minimum = 5;
            _slThreshold.Maximum = 50;
            _slThreshold.Size = new Size(X(150), X(24));
            _slThreshold.BackColor = card;
            _slThreshold.ValueChanged += delegate
            {
                _cfg.LowThreshold = _slThreshold.Value;
                Invalidate();
                Commit();
            };
            Controls.Add(_slThreshold);

            _swStartup = MakeSwitch();
            _swStartup.CheckedChanged += delegate
            {
                _cfg.StartWithWindows = _swStartup.Checked;
                AutoStart.Apply(_cfg.StartWithWindows);
                Commit();
            };

            _swAdaptive = MakeSwitch();
            _swAdaptive.CheckedChanged += delegate { _cfg.AdaptivePolling = _swAdaptive.Checked; Commit(); };

            _swHid = MakeSwitch();
            _swHid.CheckedChanged += delegate { _cfg.UseHid = _swHid.Checked; Commit(); };

            _swBle = MakeSwitch();
            _swBle.CheckedChanged += delegate
            {
                _cfg.UseBle = _swBle.Checked;
                if (_swBle.Checked) _cfg.UsePnp = true;   // BLE and PnP share one source
                SyncFromConfig();
                Commit();
            };

            _swPnp = MakeSwitch();
            _swPnp.CheckedChanged += delegate { _cfg.UsePnp = _swPnp.Checked; Commit(); };

            _btnDiag = new FlatButton(_p, "复制诊断信息");
            _btnDiag.Size = new Size(X(110), X(30));
            _btnDiag.BackColor = _p.Bg;
            _btnDiag.Clicked += delegate
            {
                try { Clipboard.SetText(_dm.Diagnostics(_cfg)); } catch { }
                _btnDiag.Text = "已复制";
                _btnDiag.Invalidate();
            };
            Controls.Add(_btnDiag);

            LayoutControls();
        }

        ToggleSwitch MakeSwitch()
        {
            var sw = new ToggleSwitch(_p);
            sw.Size = new Size(X(46), X(27));
            sw.BackColor = _p.Card;
            Controls.Add(sw);
            return sw;
        }

        void Place(Control c, int rowY, int rightOffsetWidth)
        {
            c.Location = new Point(RightEdge - X(rightOffsetWidth), X(rowY + 7));
        }

        void LayoutControls()
        {
            if (!_built) return;

            Place(_swHud, Sec1Y, 46);
            _segTheme.Location = new Point(RightEdge - X(198), X(Sec1Y + ROW_H + 7));
            _slOpacity.Location = new Point(RightEdge - X(150) - X(46), X(Sec1Y + ROW_H * 2 + 9));

            _btnInterval.Location = new Point(RightEdge - X(96), X(Sec2Y + 7));
            Place(_swNotify, Sec2Y + ROW_H, 46);
            _slThreshold.Location = new Point(RightEdge - X(150) - X(46), X(Sec2Y + ROW_H * 2 + 9));
            Place(_swAdaptive, Sec2Y + ROW_H * 3, 46);
            Place(_swStartup, Sec2Y + ROW_H * 4, 46);

            Place(_swHid, Sec3Y, 46);
            Place(_swBle, Sec3Y + ROW_H, 46);
            Place(_swPnp, Sec3Y + ROW_H * 2, 46);

            _btnDiag.Location = new Point(X(LW - 20) - X(110), ClientSize.Height - X(46));
        }

        void ApplyThemeToControls()
        {
            BackColor = _p.Bg;
            Dwm.DarkTitleBar(Handle, _p.IsDark);
            Color card = _p.Card;

            for (int i = 0; i < Controls.Count; i++)
            {
                var c = Controls[i];
                c.BackColor = (c == _btnDiag) ? _p.Bg : card;

                var prop = c.GetType().GetProperty("Palette");
                if (prop != null) prop.SetValue(c, _p, null);
                c.Invalidate();
            }
            Invalidate(true);
        }

        void ShowIntervalMenu()
        {
            var m = MenuBuilder.Create(_p);
            int[] values = new int[] { 15, 30, 60, 120, 300, 900 };
            string[] labels = new string[] { "15 秒", "30 秒", "1 分钟", "2 分钟", "5 分钟", "15 分钟" };
            for (int i = 0; i < values.Length; i++)
            {
                int v = values[i];
                var it = MenuBuilder.Item(labels[i]);
                it.Checked = _cfg.IntervalSec == v;
                it.Click += delegate
                {
                    _cfg.IntervalSec = v;
                    _btnInterval.Text = v < 60 ? (v + " 秒") : (v / 60 + " 分钟");
                    _btnInterval.Invalidate();
                    Commit();
                };
                m.Items.Add(it);
            }
            m.Show(_btnInterval, new Point(0, _btnInterval.Height + X(4)));
        }

        /// <summary>
        /// The slider is 55..100. Above the blur ceiling it is a plain opacity number; below it
        /// the same value describes the material, so say so instead of printing a % the user
        /// will read as "how see-through" and then wonder why the card still looks solid.
        /// </summary>
        static string HudOpacityLabel(int value)
        {
            if (value >= 100) return "实色";
            if (!Dwm.AcrylicSupported()) return value + "%";   // no blur on this OS: honest number
            if (value <= 60) return "毛玻璃 强";
            if (value <= 82) return "毛玻璃 中";
            return "毛玻璃 弱";
        }

        void Commit()
        {
            _cfg.Save();
            var h = Changed;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void SyncFromConfig()
        {
            if (!_built) return;

            _swHud.SetCheckedSilent(_cfg.HudEnabled);
            _swNotify.SetCheckedSilent(_cfg.NotifyLow);
            _swStartup.SetCheckedSilent(_cfg.StartWithWindows);
            _swAdaptive.SetCheckedSilent(_cfg.AdaptivePolling);
            _swHid.SetCheckedSilent(_cfg.UseHid);
            _swBle.SetCheckedSilent(_cfg.UseBle);
            _swPnp.SetCheckedSilent(_cfg.UsePnp);
            _slOpacity.Value = _cfg.HudOpacity;
            _slThreshold.Value = _cfg.LowThreshold;
            _segTheme.SelectedIndex = _cfg.HudTheme == "light" ? 1 : (_cfg.HudTheme == "dark" ? 2 : 0);
            _btnInterval.Text = _cfg.IntervalSec < 60 ? (_cfg.IntervalSec + " 秒") : (_cfg.IntervalSec / 60 + " 分钟");
            _btnInterval.Invalidate();
            RecalcLayout();
            Invalidate();
        }

        public void SyncReading()
        {
            if (!_built) return;
            RecalcLayout();
            Invalidate(new Rectangle(0, X(DEV_Y), Width, X(_devH + 10)));
        }

        // ------------------------------------------------------------------
        // painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            g.Clear(_p.Bg);

            int margin = X(20);
            int cardW = Width - margin * 2;

            using (var icon = IconFactory.AppIcon(X(24)))
                g.DrawImage(icon, margin, X(14), X(24), X(24));

            using (var b = new SolidBrush(_p.Text))
                g.DrawString("MouseMeter", Theme.F(12.5f, true), b, margin + X(34), X(12));

            DrawCloseButton(g);

            var devCard = new RectangleF(margin, X(DEV_Y), cardW, X(_devH));
            Card.Draw(g, _p, devCard, X(12));
            DrawDeviceCard(g, devCard);

            DrawSection(g, "显示", X(Sec1Label));
            var g1 = new RectangleF(margin, X(Sec1Y), cardW, X(3 * ROW_H));
            Card.Draw(g, _p, g1, X(12));
            DrawRowLabel(g, "桌面挂件", g1, 0);
            DrawRowDivider(g, g1, ROW_H);
            DrawRowLabel(g, "挂件外观", g1, 1);
            DrawRowDivider(g, g1, ROW_H * 2);
            // "不透明度" would be a lie now: on the blur paths this knob controls how much
            // material shows through, and 100% explicitly means "no blur, solid card".
            DrawRowLabel(g, "挂件材质", g1, 2);
            DrawValue(g, HudOpacityLabel(_slOpacity.Value), g1, 2);

            DrawSection(g, "通用", X(Sec2Label));
            var g2 = new RectangleF(margin, X(Sec2Y), cardW, X(5 * ROW_H));
            Card.Draw(g, _p, g2, X(12));
            DrawRowLabel(g, "刷新间隔", g2, 0);
            DrawRowDivider(g, g2, ROW_H);
            DrawRowLabel(g, "低电量提醒", g2, 1);
            DrawRowDivider(g, g2, ROW_H * 2);
            DrawRowLabel(g, "提醒阈值", g2, 2);
            DrawValue(g, _slThreshold.Value + "%", g2, 2);
            DrawRowDivider(g, g2, ROW_H * 3);
            DrawRowLabel(g, "自适应刷新", g2, 3);
            DrawRowDivider(g, g2, ROW_H * 4);
            DrawRowLabel(g, "开机自动启动", g2, 4);

            DrawSection(g, "设备识别", X(Sec3Label));
            var g3 = new RectangleF(margin, X(Sec3Y), cardW, X(3 * ROW_H));
            Card.Draw(g, _p, g3, X(12));
            DrawRowLabel(g, "私有协议直读（鼠标/键盘）", g3, 0);
            DrawRowDivider(g, g3, ROW_H);
            DrawRowLabel(g, "蓝牙电量（BLE）", g3, 1);
            DrawRowDivider(g, g3, ROW_H * 2);
            DrawRowLabel(g, "系统电池接口（耳机）", g3, 2);

            using (var b = new SolidBrush(_p.TextFaint))
                g.DrawString("3.0 · 直读 HID 接收器 + 系统电池接口", Theme.FC(8.5f, false), b, margin + X(2),
                    ClientSize.Height - X(38));

            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), X(12)))
            using (var pen = new Pen(_p.IsDark
                ? Color.FromArgb(30, 255, 255, 255)
                : Color.FromArgb(14, 0, 0, 0), 1f))
                g.DrawPath(pen, path);
        }

        void DrawSection(Graphics g, string title, int y)
        {
            using (var b = new SolidBrush(_p.TextDim))
                g.DrawString(title, Theme.FC(9.5f, false), b, X(22), y);
        }

        void DrawRowLabel(Graphics g, string label, RectangleF card, int row)
        {
            float y = card.Y + row * X(ROW_H);
            var sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(_p.Text))
                g.DrawString(label, Theme.FC(10f, false), b,
                    new RectangleF(card.X + X(16), y, X(230), X(ROW_H)), sf);
            sf.Dispose();
        }

        void DrawValue(Graphics g, string text, RectangleF card, int row)
        {
            float y = card.Y + row * X(ROW_H);
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(_p.TextDim))
                g.DrawString(text, Theme.FT(9.5f, false), b,
                    new RectangleF(card.Right - X(16) - X(44), y, X(44), X(ROW_H)), sf);
            sf.Dispose();
        }

        void DrawRowDivider(Graphics g, RectangleF card, int rowOffset)
        {
            Card.Separator(g, _p, card.X + X(16), card.Right - X(16), card.Y + rowOffset * X(ROW_H));
        }

        void DrawDeviceCard(Graphics g, RectangleF r)
        {
            var list = _dm.Snapshot();
            if (list.Count == 0)
            {
                DrawDeviceRow(g, r, 0, Reading.Invalid(), _dm.LastError);
                return;
            }
            for (int i = 0; i < list.Count && i < 4; i++)
                DrawDeviceRow(g, r, i, list[i], null);
            if (list.Count > 4)
            {
                using (var b = new SolidBrush(_p.TextFaint))
                    g.DrawString("还有 " + (list.Count - 4) + " 个设备", Theme.FC(8.5f, false), b,
                        r.X + X(16), r.Bottom - X(20));
            }
        }

        void DrawDeviceRow(Graphics g, RectangleF card, int index, Reading rd, string error)
        {
            bool has = rd.IsValid;                       // a known percentage
            bool present = has || !string.IsNullOrEmpty(rd.Kind);
            bool stale = has && Freshness.IsStale(rd);
            int level = has ? rd.Level : 0;
            bool charging = has && rd.Charging;

            Color accent = charging ? _p.Green
                         : (!has ? _p.TextFaint
                         : (level <= 10 ? _p.Red : (level <= _cfg.LowThreshold ? _p.Orange : _p.Accent)));

            float rowY = card.Y + X(12) + index * X(DEV_ROW);
            float rowH = X(DEV_ROW);
            float cx = card.X + X(46);
            float cy = rowY + rowH / 2f - X(4);
            float radius = X(21);
            float thick = X(3);

            var ring = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var pen = new Pen(_p.Separator, thick))
                g.DrawEllipse(pen, ring);

            if (has)
            {
                using (var pen = new Pen(Theme.Alpha(accent, stale ? 0.5 : 1.0), thick))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, ring, -90f, 360f * Math.Max(1f, Math.Min(100f, level)) / 100f);
                }
            }

            string num = has ? level.ToString() : "--";
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(has ? (stale ? Theme.Alpha(_p.Text, 0.6) : _p.Text) : _p.TextFaint))
                g.DrawString(num, Theme.F(has ? 13f : 12f, true), b,
                    new RectangleF(cx - radius, cy - radius - X(1), radius * 2, radius * 2), sf);
            sf.Dispose();

            float tx = card.X + X(80);
            float tw = card.Width - (tx - card.X) - X(16);

            string name = present ? rd.DeviceName : "未检测到设备";
            var nameSf = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            using (var b = new SolidBrush(_p.Text))
                g.DrawString(name, Theme.FC(11f, true), b, new RectangleF(tx, rowY + X(9), tw - X(52), X(20)), nameSf);

            // device class badge, right aligned next to the name
            if (present)
            {
                var badge = new RectangleF(card.Right - X(16) - X(40), rowY + X(11), X(40), X(15));
                Theme.FillCapsule(g, _p.IsDark ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(22, 0, 0, 0), badge);
                var bsf = new StringFormat();
                bsf.Alignment = StringAlignment.Center;
                bsf.LineAlignment = StringAlignment.Center;
                bsf.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(_p.TextDim))
                    g.DrawString(DeviceKind.Label(rd.Kind), Theme.FC(7.5f, false), b, badge, bsf);
                bsf.Dispose();
            }

            string l1 = present
                ? (rd.Transport + " · " + rd.HardwareId + (string.IsNullOrEmpty(rd.Model) ? "" : " · " + rd.Model))
                : (error ?? "");
            using (var b = new SolidBrush(_p.TextDim))
                g.DrawString(l1, Theme.FT(9f, false), b, new RectangleF(tx, rowY + X(30), tw, X(16)), nameSf);
            nameSf.Dispose();

            Color dot = !present ? _p.TextFaint : (charging ? _p.Green : (stale ? _p.Orange : _p.Green));
            using (var b = new SolidBrush(dot))
                g.FillEllipse(b, tx, rowY + X(50), X(6), X(6));

            string l2;
            if (!present) l2 = "等待连接";
            else if (!has) l2 = "已连接 · 电量未知（Windows 未上报）";
            else if (charging) l2 = "正在充电 · " + Ago(rd.At);
            else if (stale) l2 = "已休眠 · 上次 " + Ago(rd.At);
            else l2 = "已连接 · " + Ago(rd.At);

            using (var b = new SolidBrush(_p.TextFaint))
                g.DrawString(l2, Theme.FC(8.5f, false), b, new RectangleF(tx + X(12), rowY + X(47), tw - X(12), X(16)),
                    new StringFormat { FormatFlags = StringFormatFlags.NoWrap });

            if (index < 3 && index + 1 < _dm.Snapshot().Count)
                Card.Separator(g, _p, card.X + X(16), card.Right - X(16), rowY + rowH);
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "刚刚更新";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " 分钟前";
            return (int)d.TotalHours + " 小时前";
        }

        void DrawCloseButton(Graphics g)
        {
            float size = X(26);
            float x = Width - X(18) - size;
            float y = X(13);

            if (_hoverClose > 0.01)
            {
                using (var b = new SolidBrush(Theme.Alpha(_p.Text, 0.09 * _hoverClose)))
                    g.FillEllipse(b, x, y, size, size);
            }

            using (var pen = new Pen(Theme.Alpha(_p.TextDim, 0.85), X(1.4f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                float pad = size * 0.34f;
                g.DrawLine(pen, x + pad, y + pad, x + size - pad, y + size - pad);
                g.DrawLine(pen, x + size - pad, y + pad, x + pad, y + size - pad);
            }
        }

        // ------------------------------------------------------------------
        // window behaviour
        // ------------------------------------------------------------------

        bool OverClose(Point p)
        {
            return p.X > Width - X(48) && p.Y < X(46);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool overClose = OverClose(e.Location);
            float target = overClose ? 1 : 0;
            if (Math.Abs(target - _hoverClose) > 0.01)
            {
                _hoverClose = target;
                Invalidate(new Rectangle(Width - X(56), 0, X(56), X(48)));
            }

            Cursor = overClose ? Cursors.Hand : (e.Y < X(HEADER_H) ? Cursors.SizeAll : Cursors.Default);

            if (_drag)
            {
                var p = PointToScreen(e.Location);
                Location = new Point(p.X - _dragStart.X, p.Y - _dragStart.Y);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (OverClose(e.Location)) { Hide(); return; }
            if (e.Y < X(HEADER_H))
            {
                _drag = true;
                _dragStart = e.Location;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _drag = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverClose = 0;
            Invalidate(new Rectangle(Width - X(56), 0, X(56), X(48)));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }
    }
}
