using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>
    /// Frameless desktop widget. v3 shows one row per device (mouse / keyboard / headset) instead
    /// of a single number, and grows / shrinks with the device count.
    ///
    /// Drag anywhere to move, wheel to scroll when there are more devices than fit,
    /// double click opens settings, right click has the menu.
    /// </summary>
    public class HudForm : Form
    {
        const int WIDTH = 196;
        const int HEADER_H = 34;
        const int ROW_H = 64;
        const int FOOTER_H = 8;
        const int MAX_ROWS = 3;

        readonly Settings _cfg;
        Palette _p;

        List<Reading> _rows = new List<Reading>();
        bool _has;
        bool _stale;
        int _low = 20;
        int _scroll;

        bool _dragging;
        Point _dragOrigin;
        bool _positioned;
        int _dpi = 96;

        bool _hover;
        double _hoverAnim;
        System.Windows.Forms.Timer _hoverTimer;

        // Material state. 0 = solid card (any OS, transparency disabled, or user set 100%),
        // 1 = Acrylic via WCA_ACCENT_POLICY, 2 = Acrylic via DWMWA_SYSTEMBACKDROP_TYPE.
        // Resolved once per handle and re-resolved when WM_THEMECHANGED / WM_DWMCOMPOSITIONCHANGED
        // tells us the user toggled "透明效果" in Settings.
        int _material;
        bool _transparencyOn = true;

        public event EventHandler OpenSettingsRequested;
        public event EventHandler RefreshRequested;
        public event EventHandler HideRequested;
        public event EventHandler ExitRequested;

        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

        public HudForm(Settings cfg)
        {
            _cfg = cfg;
            _p = Resolve(cfg.HudTheme);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = _p.Card;
            Cursor = Cursors.Hand;

            HookMenu();
        }

        public Palette Palette { get { return _p; } }

        static Palette Resolve(string mode)
        {
            if (mode == "light") return Theme.Light();
            if (mode == "dark") return Theme.Dark();
            return Theme.SystemUsesLight() ? Theme.Light() : Theme.Dark();
        }

        float Sf { get { return _dpi / 96f; } }

        public void ApplyTheme(string mode)
        {
            _p = mode == "light" ? Theme.Light() : Theme.Dark();
            // Through ApplySurfaceStyle, not a bare BackColor assignment: on the blur path the
            // colour must stay Transparent, and hard-coding it here is how the card ends up
            // filled solid again the first time the theme changes.
            ApplySurfaceStyle();
            if (ContextMenuStrip != null) ContextMenuStrip.Renderer = new AppleMenuRenderer(_p);
            if (_material == Material.Accent) Dwm.Accent(Handle, true, TintArgb(), TintAlpha());   // tint follows the theme
            Invalidate();
        }

        int VisibleRows { get { return Math.Min(MAX_ROWS, Math.Max(1, _rows.Count)); } }

        int LogicalHeight
        {
            get
            {
                int n = _rows.Count == 0 ? 1 : VisibleRows;
                return HEADER_H + ROW_H * n + FOOTER_H;
            }
        }

        public void ApplyScale()
        {
            uint d = 0;
            try { d = GetDpiForWindow(Handle); } catch { }
            if (d >= 96 && d <= 480) _dpi = (int)d;

            double userScale = Math.Max(0.5, _cfg.HudScale / 100.0);
            int w = (int)Math.Round(WIDTH * userScale * Sf);
            int h = (int)Math.Round(LogicalHeight * userScale * Sf);

            if (Width != w || Height != h)
            {
                // keep the top-right corner pinned while the card grows downwards
                if (_positioned) Location = new Point(Right - w, Top);
                Size = new Size(w, h);
                if (_positioned) Location = ClampToScreen(Location, w, h);
                RebuildRegion();
            }

            if (!_positioned)
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                int x = _cfg.HudX < 0 ? wa.Right - w - (int)(48 * Sf) : _cfg.HudX;
                int y = _cfg.HudY < 0 ? wa.Bottom - h - (int)(120 * Sf) : _cfg.HudY;
                Location = ClampToScreen(new Point(x, y), w, h);
                _positioned = true;
            }

            // Opacity belongs to the solid path only. Re-applying it unconditionally on every
            // resize is what silently re-added WS_EX_LAYERED after ApplyMaterial had stripped it.
            if (_material == Material.Solid) Opacity = MaterialOpacity();
        }

        Point ClampToScreen(Point p, int w, int h)
        {
            var wa = Screen.FromPoint(p).WorkingArea;
            int x = Math.Max(wa.Left + 8, Math.Min(wa.Right - w - 8, p.X));
            int y = Math.Max(wa.Top + 8, Math.Min(wa.Bottom - h - 8, p.Y));
            return new Point(x, y);
        }

        const int WM_THEMECHANGED = 0x031A;
        const int WM_DWMCOMPOSITIONCHANGED = 0x031E;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Settings -> Personalisation -> Colours -> "Transparency effects" and switching the
            // wallpaper engine both land here. Re-resolve instead of assuming the state we read
            // at startup is still true; a stale accent policy leaves an opaque grey box.
            //
            // Re-resolving means calling ApplyMaterial in every case. The previous version only
            // did so when the flag actually flipped, and refreshed the tint otherwise - which
            // left _material stale after a wallpaper change and, worse, could mark the window
            // Accent while the user had transparency off.
            if (m.Msg == WM_THEMECHANGED || m.Msg == WM_DWMCOMPOSITIONCHANGED)
            {
                ApplyMaterial();
            }
        }

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
            Dwm.RoundedCorners(Handle, true);
            ApplyMaterial();
        }

        // ---------- material ----------

        /// <summary>
        /// Decides how the card is composited. The slider is the user's "how solid do I want it"
        /// knob and means different things per layer:
        ///
        ///   100%  -> solid card, no blur at all (also what you get on Win10 / transparency off)
        ///   55-99 -> Acrylic; the slider becomes the tint alpha *on top of* the blur, so 55% is
        ///            a very see-through frosted pane and 99% is a barely blurred solid-ish card.
        ///
        /// Falling back is deliberate rather than best-effort: a half-applied accent policy leaves
        /// an unreadable black rectangle, which is worse than the flat card we had before.
        /// </summary>
        void ApplyMaterial()
        {
            if (!IsHandleCreated) return;

            _transparencyOn = Dwm.TransparencyEnabled();
            // Backdrop is preferred when available: it is the compositor's own material, so it
            // picks up the wallpaper, the theme accent and the user's contrast settings without
            // us hand-tuning a tint. The Win10 accent policy stays as the fallback.
            int want = Material.Pick(_cfg.HudOpacity, _transparencyOn,
                Dwm.AcrylicSupported(), Dwm.BackdropSupported());

            _material = want;

            // Both blur APIs draw *behind* the client area and expect the window to declare
            // what it does not paint. Get that declaration in place before asking for the
            // material, not after: a window that is still WS_EX_LAYERED at the moment
            // DWMWA_SYSTEMBACKDROP_TYPE lands gets the attribute but no visible backing, and
            // one that was not frame-extended gets the material behind an invisible border.
            //
            // Form.Opacity is the only thing that sets WS_EX_LAYERED, and it is only meaningful
            // on the solid path (dim the whole card). Blur paths dim through the tint instead,
            // so they must run at Opacity=1 and must not be layered.
            ApplySurfaceStyle();

            if (_material == Material.Backdrop)
            {
                Dwm.ExtendFrame(Handle);
                if (Dwm.Backdrop(Handle, 3) != 0)
                {
                    // The attribute is documented but rejected by some builds / GPUs. Rather
                    // than ship a transparent window with nothing behind it, drop to the
                    // accent policy, which has been around since Win10 1803.
                    _material = Material.Accent;
                    Dwm.Accent(Handle, true, TintArgb(), TintAlpha());
                    ApplySurfaceStyle();
                }
                else Dwm.Accent(Handle, false); // clear any WCA accent left over from a fallback
            }
            else if (_material == Material.Accent)
            {
                Dwm.ExtendFrame(Handle);
                Dwm.Accent(Handle, true, TintArgb(), TintAlpha());
            }
            else
            {
                Dwm.Accent(Handle, false);
                Dwm.ExtendFrame(Handle, false);
            }

            RebuildRegion();
            Invalidate();
        }

        /// <summary>
        /// Push <see cref="_material"/> into the three properties that decide whether the client
        /// area is painted at all: the layered style, the back colour, and who paints the card.
        ///
        /// Split out and called at every point where _material can change, because all three
        /// disagree with each other when they go stale:
        ///   * Layered + backdrop  -> washed-out flat card (the coupling this exists to avoid).
        ///   * Opaque BackColor    -> WinForms fills the whole client area, covering the blur.
        ///   * SupportsTransparentBackColor not set -> Color.Transparent throws.
        /// </summary>
        void ApplySurfaceStyle()
        {
            // Required before WinForms will accept Color.Transparent at all. Must precede the
            // BackColor assignment below, so it is not conditional on the branch taken.
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);

            // The rules live in Material so they can be asserted without a window; see
            // Material.OpacityFor / BackColorIsTransparent / LayeredAllowed.
            bool wantTransparent = Material.BackColorIsTransparent(_material);
            Color wantBack = wantTransparent ? Color.Transparent : _p.Card;
            double wantOpacity = Material.OpacityFor(_material, _cfg.HudOpacity);

            // Assign only on change: every BackColor setter on a transparent form triggers a
            // parent-paint walk, and this runs on every resize.
            if (BackColor != wantBack) BackColor = wantBack;
            if (Opacity != wantOpacity) Opacity = wantOpacity;

            Invalidate();
        }

        /// <summary>Delegates to <see cref="Material"/> so the rule stays testable off-UI.</summary>
        double MaterialOpacity()
        {
            return Material.OpacityFor(_material, _cfg.HudOpacity);
        }

        /// <summary>ABGR tint for the accent policy — DWM reads it as 0xAABBGGRR.</summary>
        int TintArgb()
        {
            Color c = _p.IsDark ? Color.FromArgb(32, 32, 34) : Color.FromArgb(250, 250, 252);
            // The scalar rides in the high byte; the accent policy multiplies it by the alpha.
            return Material.TintWord(TintAlpha(), c.R, c.G, c.B);
        }

        /// <summary>Delegates to <see cref="Material"/> so the mapping stays testable off-UI.</summary>
        int TintAlpha()
        {
            return Material.TintAlpha(_cfg.HudOpacity, _p.IsDark);
        }

        /// <summary>
        /// The window stays rectangular in every material mode. A Window.Region is clipped by
        /// USER32 before DWM composites, which (a) aliases the corners and (b) cuts a 1 px seam
        /// where the material ends. Rounding is drawn instead: the blur paths get a
        /// DwmSetWindowAttribute(33) round corner from the compositor, the solid path gets a
        /// painted silhouette in <see cref="OnPaint"/>. Re-applied on resize because the corner
        /// radius follows the window height.
        /// </summary>
        void RebuildRegion()
        {
            Region = null;
            Dwm.RoundedCorners(Handle, true);
        }

        void HookMenu()
        {
            var menu = MenuBuilder.Create(_p);

            var refresh = MenuBuilder.Item("立即刷新");
            refresh.Click += delegate { var h = RefreshRequested; if (h != null) h(this, EventArgs.Empty); };
            var settings = MenuBuilder.Item("设置…");
            settings.Click += delegate { var h = OpenSettingsRequested; if (h != null) h(this, EventArgs.Empty); };
            var hide = MenuBuilder.Item("隐藏挂件");
            hide.Click += delegate { var h = HideRequested; if (h != null) h(this, EventArgs.Empty); };
            var exit = MenuBuilder.Item("退出 MouseMeter");
            exit.Click += delegate { var h = ExitRequested; if (h != null) h(this, EventArgs.Empty); };

            menu.Items.Add(refresh);
            menu.Items.Add(settings);
            menu.Items.Add(hide);
            menu.Items.Add(MenuBuilder.Sep());
            menu.Items.Add(exit);
            ContextMenuStrip = menu;
        }

        /// <summary>Feed the widget a fresh snapshot. Re-lays out when the device count changes.</summary>
        public void UpdateReadings(List<Reading> rows, bool has, bool stale, int lowThreshold)
        {
            _rows = rows ?? new List<Reading>();
            _has = has;
            _stale = stale;
            _low = lowThreshold;
            if (_scroll > Math.Max(0, _rows.Count - MAX_ROWS)) _scroll = Math.Max(0, _rows.Count - MAX_ROWS);
            ApplyScale();   // cheap when the size did not change
            Invalidate();
        }

        // ---------- interaction ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragOrigin = e.Location;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                var p = PointToScreen(e.Location);
                Location = new Point(p.X - _dragOrigin.X, p.Y - _dragOrigin.Y);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                _dragging = false;
                _cfg.HudX = Location.X;
                _cfg.HudY = Location.Y;
                _cfg.Save();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_rows.Count <= MAX_ROWS) return;
            int max = _rows.Count - MAX_ROWS;
            int next = e.Delta > 0 ? _scroll - 1 : _scroll + 1;
            _scroll = Math.Max(0, Math.Min(max, next));
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            var h = OpenSettingsRequested;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            AnimateHover();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            AnimateHover();
        }

        void AnimateHover()
        {
            if (_hoverTimer == null)
            {
                _hoverTimer = new System.Windows.Forms.Timer();
                _hoverTimer.Interval = 16;
                _hoverTimer.Tick += delegate
                {
                    double target = _hover ? 1 : 0;
                    double d = target - _hoverAnim;
                    if (Math.Abs(d) < 0.04) { _hoverAnim = target; _hoverTimer.Stop(); }
                    else _hoverAnim += d * 0.25;
                    Invalidate();
                };
            }
            _hoverTimer.Start();
        }

        // ---------- painting ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            // Every path paints its own punched silhouette: the window rectangle is wider than
            // the visible card because DWM needs an unclipped surface for the material, and the
            // leftover pixels must stay empty or the "rounded" card shows square shoulders.
            float radius = Height * 0.115f;
            var cardRect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (var cardPath = Theme.RoundRect(cardRect, radius))
            {
                if (Material.ClearsBeforeTint(_material))
                {
                    // Clear to the surface colour BEFORE painting the tint. This is the whole
                    // trick: the DWM material is behind the client area, so anything GDI writes
                    // here covers it permanently. Without this the leftover pixels outside
                    // cardPath keep whatever the previous frame left there, and the card ends
                    // up looking opaque no matter what tint alpha we asked for.
                    // TransparentBackground makes the blit leave those pixels alone.
                    g.Clear(Color.Transparent);

                    // Half of the tint lives here and half in the accent policy (or backdrop):
                    // painting it all in GDI would make the blur invisible, painting none would
                    // let white text sit on a bright wallpaper.
                    int a = Material.GdiTintAlpha(_cfg.HudOpacity, _p.IsDark);
                    using (var b = new SolidBrush(Color.FromArgb(a, _p.Card)))
                        g.FillPath(b, cardPath);
                }
                else
                {
                    g.Clear(_p.Card);   // opaque path: nothing outside the rectangle to worry about
                }
            }

            float s = Width / (float)WIDTH;
            float fs = (float)(_cfg.HudScale / 100.0);   // font pt already scales with DPI

            if (_hoverAnim > 0.01)
            {
                using (var b = new SolidBrush(Color.FromArgb((int)(8 * _hoverAnim), 255, 255, 255)))
                using (var hp = Theme.RoundRect(new RectangleF(0, 0, Width, Height), radius))
                    g.FillPath(b, hp);
            }

            float pad = 18f * s;

            // hairline border: light card needs a darker edge, dark card a highlight
            Color borderColor = _p.IsDark
                ? Color.FromArgb(26, 255, 255, 255)
                : Color.FromArgb(16, 0, 0, 0);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Height * 0.115f))
            using (var pen = new Pen(borderColor, 1f))
                g.DrawPath(pen, path);

            DrawHeader(g, s, fs, pad);

            if (_rows.Count == 0)
            {
                RowPainter.DrawEmpty(g, _p,
                    new RectangleF(pad, HEADER_H * s, Width - pad * 2, ROW_H * s * 0.8f), s, fs,
                    _has ? "读取中…" : "未检测到设备");
            }
            else
            {
                int n = VisibleRows;
                for (int i = 0; i < n; i++)
                {
                    int idx = _scroll + i;
                    if (idx >= _rows.Count) break;
                    float y = (HEADER_H + ROW_H * i) * s;
                    if (i > 0)
                        Card.Separator(g, _p, pad, Width - pad, y - 2f * s);
                    var rect = new RectangleF(0, y, Width, (ROW_H - 6) * s);
                    RowPainter.Draw(g, _p, _rows[idx], rect, s, fs, _low, true);
                }
                if (_rows.Count > MAX_ROWS) DrawScrollHint(g, s, fs, pad, n);
            }
        }

        void DrawHeader(Graphics g, float s, float fs, float pad)
        {
            var first = _rows.Count > 0 ? _rows[0] : Reading.Invalid();
            bool charging = _rows.Count > 0 && first.Charging;
            bool anyStale = _rows.Count > 0 && _stale;

            string title;
            // With a single device the row already carries the name — repeating it 34 px above
            // the big number is pure noise, so the header only marks the state.
            if (_rows.Count == 0) title = "MouseMeter";
            else if (_rows.Count == 1) title = "电量";
            else title = _rows.Count + " 个设备";

            var sfLeft = new StringFormat();
            sfLeft.LineAlignment = StringAlignment.Center;
            sfLeft.FormatFlags = StringFormatFlags.NoWrap;
            sfLeft.Trimming = StringTrimming.EllipsisCharacter;
            float rightReserve = 64f * s;
            using (var b = new SolidBrush(_p.TextFaint))
                g.DrawString(title, Theme.FC(8.5f * fs, false), b,
                    new RectangleF(pad, 8f * s, Width - pad * 2 - rightReserve, 18f * s), sfLeft);
            sfLeft.Dispose();

            if (charging) RowPainter.DrawBolt(g, Width - pad - 10 * s, 10f * s, 12 * s, _p.Green);
            else if (anyStale)
            {
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Far;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (var b = new SolidBrush(_p.TextFaint))
                    g.DrawString("已休眠", Theme.FC(8f * fs, false), b,
                        new RectangleF(Width - pad - 56 * s, 8f * s, 56 * s, 18f * s), sf);
                sf.Dispose();
            }
        }

        void DrawScrollHint(Graphics g, float s, float fs, float pad, int n)
        {
            // Sits inside the rounded bottom: the old 10 px box ended on the card edge and the
            // corner radius clipped the last glyph. Centre it in the footer band instead.
            string hint = (_scroll + 1) + "–" + (_scroll + n) + " / " + _rows.Count;
            float h = 11f * s;
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(Theme.Alpha(_p.TextFaint, 0.8)))
                g.DrawString(hint, Theme.FC(7.5f * fs, false), b,
                    new RectangleF(Width - pad - 70 * s, Height - FOOTER_H * s - 4f * s + (FOOTER_H * s - h) * 0.5f, 70 * s, h), sf);
            sf.Dispose();
        }
    }
}
