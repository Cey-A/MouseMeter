using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>iOS style switch. 44 x 26 logical pixels.</summary>
    public class ToggleSwitch : Control
    {
        bool _checked;
        double _anim;
        readonly System.Windows.Forms.Timer _timer;

        public event EventHandler CheckedChanged;

        public ToggleSwitch(Palette p)
        {
            Palette = p;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15;
            _timer.Tick += delegate
            {
                double target = _checked ? 1 : 0;
                double d = target - _anim;
                if (Math.Abs(d) < 0.06) { _anim = target; _timer.Stop(); }
                else _anim += d * 0.32;
                Invalidate();
            };
        }

        public Palette Palette { get; set; }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _timer.Start();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetCheckedSilent(bool value)
        {
            _checked = value;
            _anim = value ? 1 : 0;
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            float w = Width, h = Height;
            float inset = h * 0.08f;
            var track = new RectangleF(0, inset, w, h - inset * 2);
            float r = track.Height / 2f;

            Color off = Palette.IsDark ? Palette.ControlOff : Color.FromArgb(226, 226, 231);
            Color on = Palette.Green;
            Color trackColor = Theme.Mix(off, on, _anim);

            using (var path = Theme.RoundRect(track, r))
            using (var b = new SolidBrush(trackColor))
                g.FillPath(b, path);

            float pad = track.Height * 0.11f;
            float thumbD = track.Height - pad * 2;
            float x = track.X + pad + (float)((track.Width - thumbD - pad * 2) * _anim);
            float y = track.Y + pad;

            using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                g.FillEllipse(shadow, x - 0.5f, y + 1.2f, thumbD + 1f, thumbD + 1f);

            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, x, y, thumbD, thumbD);
        }
    }

    /// <summary>Hairline slider with a white knob.</summary>
    public class MMSlider : Control
    {
        int _min, _max = 100, _value = 50;
        bool _dragging;

        public event EventHandler ValueChanged;

        public MMSlider(Palette p)
        {
            Palette = p;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public Palette Palette { get; set; }

        public int Minimum { get { return _min; } set { _min = value; } }
        public int Maximum { get { return _max; } set { _max = value; } }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_min, Math.Min(_max, value));
                if (v == _value) return;
                _value = v;
                Invalidate();
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        float Ratio { get { return (float)(_value - _min) / Math.Max(1, _max - _min); } }

        RectangleF TrackRect()
        {
            float th = 4f;
            return new RectangleF(th, (Height - th) / 2f, Width - th * 2, th);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { _dragging = true; SetFromX(e.X); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        void SetFromX(int x)
        {
            var t = TrackRect();
            double r = (x - t.X) / Math.Max(1, t.Width);
            r = Math.Max(0, Math.Min(1, r));
            Value = (int)Math.Round(_min + r * (_max - _min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            var t = TrackRect();
            float rh = t.Height / 2f;
            Color rail = Palette.IsDark ? Palette.ControlOff : Color.FromArgb(226, 226, 231);

            using (var path = Theme.RoundRect(t, rh))
            using (var b = new SolidBrush(rail))
                g.FillPath(b, path);

            float fillW = t.Width * Ratio;
            if (fillW > 0.5f)
            {
                var f = new RectangleF(t.X, t.Y, Math.Max(rh * 2, fillW), t.Height);
                using (var path = Theme.RoundRect(f, rh))
                using (var b = new SolidBrush(Palette.Accent))
                    g.FillPath(b, path);
            }

            float d = Math.Min(Height - 4, 18f);
            float cx = t.X + t.Width * Ratio;
            float cy = Height / 2f;
            var knob = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);

            using (var sh = new SolidBrush(Color.FromArgb(30, 0, 0, 0)))
                g.FillEllipse(sh, knob.X, knob.Y + 1f, d, d);

            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, knob);

            using (var p = new Pen(Color.FromArgb(18, 0, 0, 0), 1f))
                g.DrawEllipse(p, knob.X, knob.Y, d - 0.5f, d - 0.5f);
        }
    }

    /// <summary>Compact segmented picker (iOS "Appearance" style).</summary>
    public class Segmented : Control
    {
        string[] _items = new string[0];
        int _selected;
        int _hover = -1;

        public event EventHandler SelectionChanged;

        public Segmented(Palette p)
        {
            Palette = p;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public Palette Palette { get; set; }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = Math.Max(0, Math.Min(_items.Length - 1, value));
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectionChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitTest(e.X);
            if (idx != _hover) { _hover = idx; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int idx = HitTest(e.X);
            if (idx >= 0) SelectedIndex = idx;
        }

        int HitTest(int x)
        {
            if (_items.Length == 0) return -1;
            float seg = (float)Width / _items.Length;
            int i = (int)(x / Math.Max(1f, seg));
            return i < 0 ? 0 : (i >= _items.Length ? _items.Length - 1 : i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            Color rail = Palette.IsDark ? Color.FromArgb(58, 58, 60) : Color.FromArgb(228, 228, 233);
            var all = new RectangleF(0, 0, Width, Height);
            using (var path = Theme.RoundRect(all, Height * 0.30f))
            using (var b = new SolidBrush(rail))
                g.FillPath(b, path);

            if (_items.Length == 0) return;

            float seg = (float)Width / _items.Length;
            float pad = 2.5f;
            var sel = new RectangleF(_selected * seg + pad, pad, seg - pad * 2, Height - pad * 2);

            using (var path = Theme.RoundRect(sel, sel.Height * 0.30f))
            {
                var sh = sel;
                sh.Offset(0, 1.5f);
                sh.Height += 1.5f;
                using (var p2 = Theme.RoundRect(sh, sh.Height * 0.30f))
                using (var b = new SolidBrush(Color.FromArgb(32, 0, 0, 0)))
                    g.FillPath(b, p2);
            }

            using (var path = Theme.RoundRect(sel, sel.Height * 0.30f))
            using (var b = new SolidBrush(Palette.IsDark ? Color.FromArgb(99, 99, 102) : Color.White))
                g.FillPath(b, path);

            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;

            for (int i = 0; i < _items.Length; i++)
            {
                var cell = new RectangleF(i * seg, 0, seg, Height);
                Color c = (i == _selected) ? Palette.Text : Palette.TextDim;
                using (var b = new SolidBrush(c))
                    g.DrawString(_items[i], Theme.FC(9f, i == _selected), b, cell, sf);
            }
            sf.Dispose();
        }
    }

    /// <summary>Flat rounded button used for secondary actions.</summary>
    public class FlatButton : Control
    {
        bool _hover;

        public event EventHandler Clicked;

        public FlatButton(Palette p, string text)
        {
            Palette = p;
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public Palette Palette { get; set; }
        public bool Primary { get; set; }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var h = Clicked;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            Color bg;
            Color fg;
            if (Primary)
            {
                bg = _hover ? Theme.Mix(Palette.Accent, Color.White, 0.12) : Palette.Accent;
                fg = Color.White;
            }
            else
            {
                Color basec = Palette.IsDark ? Color.FromArgb(58, 58, 60) : Color.FromArgb(233, 233, 238);
                bg = _hover ? Theme.Mix(basec, Palette.Text, 0.10) : basec;
                fg = Palette.Text;
            }

            var r = new RectangleF(0, 0, Width - 0.5f, Height - 0.5f);
            using (var path = Theme.RoundRect(r, Height * 0.30f))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);

            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(fg))
                g.DrawString(Text, Theme.FC(9f, false), b, new RectangleF(0, 0, Width, Height), sf);
            sf.Dispose();
        }
    }

    /// <summary>Rounded group container drawn behind rows.</summary>
    public static class Card
    {
        public static void Draw(Graphics g, Palette p, RectangleF r, float radius)
        {
            using (var path = Theme.RoundRect(r, radius))
            using (var b = new SolidBrush(p.Card))
                g.FillPath(b, path);
        }

        public static void Separator(Graphics g, Palette p, float x1, float x2, float y)
        {
            using (var pen = new Pen(p.Separator, 1f))
            {
                pen.Alignment = PenAlignment.Center;
                g.DrawLine(pen, x1, y, x2, y);
            }
        }
    }
}
