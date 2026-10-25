using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MouseMeter
{
    /// <summary>Renderer that gives context menus a macOS-like look: rounded, quiet, no icon gutter.</summary>
    public class AppleMenuRenderer : ToolStripRenderer
    {
        public Palette P;

        public AppleMenuRenderer(Palette p) { P = p; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            var ts = e.ToolStrip;
            var r = new RectangleF(0, 0, ts.Width, ts.Height);
            using (var path = Theme.RoundRect(r, 10f))
            {
                using (var b = new SolidBrush(P.Card))
                    g.FillPath(b, path);
                ts.Region = new Region(path);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            var r = new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f);
            using (var path = Theme.RoundRect(r, 10f))
            using (var pen = new Pen(Theme.Alpha(P.Text, P.IsDark ? 0.10 : 0.07), 1f))
                g.DrawPath(pen, path);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            var item = e.Item;

            if (!item.Selected || !item.Enabled) return;

            var r = new RectangleF(4.5f, 1.5f, item.Width - 9f, item.Height - 3f);
            using (var path = Theme.RoundRect(r, 6f))
            using (var b = new SolidBrush(P.IsDark
                ? Color.FromArgb(255, 62, 62, 66)
                : Color.FromArgb(255, 236, 236, 240)))
                g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            Color c = e.Item.Enabled ? P.Text : P.TextFaint;
            if (!e.Item.Enabled && e.Item.Tag as string == "caption") c = P.TextDim;

            var sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags = StringFormatFlags.NoWrap;

            bool caption = (e.Item.Tag as string) == "caption";
            Font f = caption ? Theme.FC(8.5f, false) : Theme.FC(9f, false);

            var rect = e.TextRectangle;
            var box = new RectangleF(e.TextRectangle.X, 0, e.ToolStrip.Width - e.TextRectangle.X - 8, e.Item.Height);
            using (var b = new SolidBrush(c))
                g.DrawString(e.Text, f, b, box, sf);
            sf.Dispose();
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);

            float s = 12f;
            float x = e.ImageRectangle.X + 2f;
            float y = (e.Item.Height - s) / 2f + 0.5f;

            using (var pen = new Pen(P.Accent, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new PointF[] {
                    new PointF(x, y + s * 0.52f),
                    new PointF(x + s * 0.36f, y + s * 0.86f),
                    new PointF(x + s, y + s * 0.16f)
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Configure(g);
            float y = e.Item.Height / 2f;
            using (var pen = new Pen(P.Separator, 1f))
                g.DrawLine(pen, 12f, y, e.Item.Width - 12f, y);
        }
    }

    public static class MenuBuilder
    {
        public static ContextMenuStrip Create(Palette p)
        {
            var m = new ContextMenuStrip();
            m.Renderer = new AppleMenuRenderer(p);
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            m.BackColor = p.Card;
            m.ForeColor = p.Text;
            m.Font = Theme.FC(9f, false);
            m.Padding = new Padding(0, 5, 0, 5);
            m.DropShadowEnabled = true;
            m.GripStyle = ToolStripGripStyle.Hidden;
            m.AutoSize = true;
            return m;
        }

        public static ToolStripMenuItem Item(string text)
        {
            var i = new ToolStripMenuItem(text);
            i.Padding = new Padding(2, 5, 2, 5);
            i.Margin = new Padding(0);
            i.AutoSize = true;
            return i;
        }

        public static ToolStripMenuItem Caption(string text)
        {
            var i = Item(text);
            i.Enabled = false;
            i.Tag = "caption";
            return i;
        }

        public static ToolStripSeparator Sep()
        {
            var s = new ToolStripSeparator();
            s.Margin = new Padding(0, 4, 0, 4);
            return s;
        }
    }
}
