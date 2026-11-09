using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>Horizontal row of category tabs ("chips") above the pads, plus a compact-view toggle.</summary>
    class CategoryBar : Control
    {
        public const string AllKey = "*all", FavoritesKey = "*fav";

        public class Chip
        {
            public string Key, Label, Glyph;
            public int Count;
            public Color Dot = Color.Empty;
            public bool IsCategory { get { return !Key.StartsWith("*"); } }
            public bool AcceptsDrop { get { return Key != AllKey; } }
            internal Rectangle Bounds;
        }

        List<Chip> chips = new List<Chip>();
        string selected = AllKey;
        int hover = -1, offset, maxOffset;
        Chip dropHover;
        bool hoverAdd, hoverCompact, compact;
        Rectangle addRect, compactRect;

        public event Action<string> ChipClicked;
        public event Action<Chip, Point> ChipMenu;
        public event Action AddClicked;
        public event Action CompactToggled;

        public CategoryBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Height = Theme.S(56);
            Font = Theme.Body;
        }

        public bool Compact { get { return compact; } set { compact = value; Invalidate(); } }

        public void SetChips(List<Chip> list, string selectedKey)
        {
            chips = list;
            selected = selectedKey;
            Measure();
            Invalidate();
        }

        public List<Chip> Chips { get { return chips; } }

        /// <summary>The chip under a screen point that a dragged pad can be dropped on, or null.</summary>
        public Chip DropTargetAt(Point screen)
        {
            Point p = PointToClient(screen);
            if (!ClientRectangle.Contains(p)) return null;
            foreach (Chip c in chips) if (c.AcceptsDrop && Shift(c.Bounds).Contains(p)) return c;
            return null;
        }

        public void SetDropHover(Chip c)
        {
            if (c == dropHover) return;
            dropHover = c;
            Invalidate();
        }

        int ChipsRight { get { return Width - Theme.S(60); } }

        Rectangle Shift(Rectangle r) { r.Offset(-offset, 0); return r; }

        void Measure()
        {
            int x = Theme.S(18), h = Theme.S(32), y = (Height - h) / 2;
            foreach (Chip c in chips)
            {
                int w = Theme.S(24) + TextRenderer.MeasureText(c.Label, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (c.Glyph != null || c.Dot != Color.Empty) w += Theme.S(20);
                w += TextRenderer.MeasureText(c.Count.ToString(), Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(8);
                c.Bounds = new Rectangle(x, y, w, h);
                x += w + Theme.S(8);
            }
            int aw = Theme.S(30) + TextRenderer.MeasureText("New category", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            addRect = new Rectangle(x, y, aw, h);
            maxOffset = Math.Max(0, addRect.Right + Theme.S(12) - ChipsRight);
            offset = Math.Max(0, Math.Min(offset, maxOffset));
            compactRect = new Rectangle(Width - Theme.S(52), y, Theme.S(34), h);
        }

        protected override void OnResize(EventArgs e) { Measure(); base.OnResize(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            offset = Math.Max(0, Math.Min(maxOffset, offset - e.Delta * Theme.S(60) / 120));
            Invalidate();
        }

        void HitTest(Point p, out int chip, out bool add, out bool compactBtn)
        {
            chip = -1;
            bool inChips = p.X < ChipsRight;
            for (int i = 0; i < chips.Count && inChips; i++) if (Shift(chips[i].Bounds).Contains(p)) chip = i;
            add = inChips && Shift(addRect).Contains(p);
            compactBtn = compactRect.Contains(p);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h;
            bool a, c;
            HitTest(e.Location, out h, out a, out c);
            Cursor = h >= 0 || a || c ? Cursors.Hand : Cursors.Default;
            if (h != hover || a != hoverAdd || c != hoverCompact) { hover = h; hoverAdd = a; hoverCompact = c; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverAdd = hoverCompact = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int h;
            bool a, c;
            HitTest(e.Location, out h, out a, out c);
            if (c) { if (e.Button == MouseButtons.Left && CompactToggled != null) CompactToggled(); return; }
            if (a) { if (e.Button == MouseButtons.Left && AddClicked != null) AddClicked(); return; }
            if (h < 0) return;
            Chip chip = chips[h];
            if (e.Button == MouseButtons.Left && ChipClicked != null) ChipClicked(chip.Key);
            else if (e.Button == MouseButtons.Right && chip.IsCategory && ChipMenu != null) ChipMenu(chip, e.Location);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.SetClip(new Rectangle(0, 0, ChipsRight, Height));
            for (int i = 0; i < chips.Count; i++) DrawChip(g, chips[i], Shift(chips[i].Bounds), i == hover);

            Rectangle ar = Shift(addRect);
            Color ac = hoverAdd ? Theme.Text : Theme.Muted;
            using (GraphicsPath p = Theme.Round(new RectangleF(ar.X + 0.5f, ar.Y + 0.5f, ar.Width - 1, ar.Height - 1), ar.Height / 2f))
            using (Pen pen = new Pen(hoverAdd ? Theme.Faint : Theme.Border))
            {
                pen.DashStyle = DashStyle.Dash;
                if (hoverAdd) using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Theme.IcAdd, Theme.IconsSmall, new Rectangle(ar.X + Theme.S(11), ar.Y, Theme.S(14), ar.Height), ac,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "New category", Font, new Rectangle(ar.X + Theme.S(25), ar.Y, ar.Width, ar.Height), ac,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            g.ResetClip();

            // fade the right edge when chips are cut off
            if (offset < maxOffset)
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(ChipsRight - Theme.S(30), 0, Theme.S(30), Height),
                    Color.FromArgb(0, Theme.Bg), Theme.Bg, 0f))
                    g.FillRectangle(b, ChipsRight - Theme.S(30), 0, Theme.S(30), Height);

            // compact toggle
            Rectangle cr = compactRect;
            Theme.FillRound(g, hoverCompact ? Theme.CardHover : Theme.Card, new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1), Theme.S(8));
            Theme.DrawRound(g, Theme.Border, 1f, new RectangleF(cr.X + 0.5f, cr.Y + 0.5f, cr.Width - 1, cr.Height - 1), Theme.S(8));
            TextRenderer.DrawText(g, compact ? Theme.IcGrid : Theme.IcList, Theme.Icons, cr, hoverCompact ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        void DrawChip(Graphics g, Chip c, Rectangle r, bool hot)
        {
            bool sel = c.Key == selected;
            bool drop = c == dropHover;
            Color fill = drop ? Theme.Mix(Theme.Card, Theme.Accent, 0.35f) : sel ? Theme.Accent : hot ? Theme.CardHover : Theme.Card;
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            Theme.FillRound(g, fill, rf, r.Height / 2f);
            if (drop) Theme.DrawRound(g, Theme.Accent, Theme.S(2), rf, r.Height / 2f);
            else if (!sel) Theme.DrawRound(g, Theme.Border, 1f, rf, r.Height / 2f);

            int x = r.X + Theme.S(12);
            Color text = sel ? Color.White : Theme.Text;
            if (c.Glyph != null)
            {
                Color gc = sel ? Color.White : c.Key == FavoritesKey ? Theme.Warn : Theme.Muted;
                TextRenderer.DrawText(g, c.Glyph, Theme.IconsSmall, new Rectangle(x, r.Y, Theme.S(16), r.Height), gc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += Theme.S(20);
            }
            else if (c.Dot != Color.Empty)
            {
                int d = Theme.S(9);
                using (SolidBrush b = new SolidBrush(sel ? Color.White : c.Dot)) g.FillEllipse(b, x + 1, r.Y + (r.Height - d) / 2, d, d);
                x += Theme.S(20);
            }
            Size ts = TextRenderer.MeasureText(c.Label, Font, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, c.Label, Font, new Rectangle(x, r.Y, ts.Width + 2, r.Height), text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, c.Count.ToString(), Theme.Small, new Rectangle(x + ts.Width + Theme.S(8), r.Y, r.Right - x, r.Height),
                sel ? Color.FromArgb(220, 255, 255, 255) : Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
