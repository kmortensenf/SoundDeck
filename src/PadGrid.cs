using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// The soundboard: a scrollable grid of owner-drawn sound pads.
    /// Click plays, the star toggles favorite, dragging a pad reorders it (or drops it on something outside the grid).
    /// </summary>
    class PadGrid : Control
    {
        List<SoundEntry> items = new List<SoundEntry>();
        Dictionary<object, float> progress = new Dictionary<object, float>();
        int scroll, contentHeight, hover = -1, cols = 1, tileW, tileH, gap, pad;
        bool hoverStar, compact;
        bool dragThumb;
        int dragY, dragScroll;

        // pad dragging
        int pressIndex = -1;
        Point pressPoint, dragPos, grabOffset;
        bool draggingPad, dragOutside;
        SoundEntry dragItem;
        int dropIndex = -1;
        readonly Timer autoScroll = new Timer();

        public bool Filtered, AllowReorder = true;

        // multi-select: Ctrl+click toggles, Shift+click selects a range, a plain click clears it
        public readonly HashSet<SoundEntry> Selected = new HashSet<SoundEntry>();
        SoundEntry anchor;
        public event Action SelectionChanged;
        public string EmptyTitle = "Your soundboard is empty";
        public string EmptySub = "Drag audio files or folders here, or click to add sounds.";
        public Func<SoundEntry, Color> ColorOf;

        public event Action<SoundEntry> PadClicked;
        public event Action<SoundEntry> StarClicked;
        public event Action<SoundEntry, Point> PadMenu;
        public event Action EmptyClicked;
        /// <summary>A pad was dropped at a new position (index within the current list).</summary>
        public event Action<SoundEntry, int> Reordered;
        /// <summary>A pad is being dragged; the point is in screen coordinates.</summary>
        public event Action<SoundEntry, Point> DragMoved;
        /// <summary>A pad was dropped outside the grid; the point is in screen coordinates.</summary>
        public event Action<SoundEntry, Point> DroppedOutside;
        public event Action DragEnded;

        public PadGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            autoScroll.Interval = 30;
            autoScroll.Tick += (s, e) => AutoScrollTick();
        }

        public bool Compact
        {
            get { return compact; }
            set { compact = value; Relayout(); Invalidate(); }
        }

        public bool IsDragging { get { return draggingPad; } }

        public void SetItems(List<SoundEntry> list)
        {
            items = list;
            hover = -1;
            int before = Selected.Count;
            Selected.RemoveWhere(s => !items.Contains(s)); // selection only covers what's on screen
            Relayout();
            Invalidate();
            if (Selected.Count != before && SelectionChanged != null) SelectionChanged();
        }

        public void ClearSelection()
        {
            if (Selected.Count == 0) return;
            Selected.Clear();
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void SelectAll()
        {
            foreach (SoundEntry s in items) Selected.Add(s);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        void ClickSelect(int i, Keys mods)
        {
            SoundEntry s = items[i];
            if ((mods & Keys.Shift) != 0 && anchor != null && items.Contains(anchor))
            {
                int a = items.IndexOf(anchor);
                for (int k = Math.Min(a, i); k <= Math.Max(a, i); k++) Selected.Add(items[k]);
            }
            else
            {
                if (!Selected.Remove(s)) Selected.Add(s);
                anchor = s;
            }
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        public void SetProgress(Dictionary<object, float> p)
        {
            if (p.Count == 0 && progress.Count == 0) return;
            progress = p;
            Invalidate();
        }

        public void RedrawPads() { Invalidate(); }

        public void ScrollToTop() { scroll = 0; Invalidate(); }

        public void CancelDrag()
        {
            if (!draggingPad) return;
            EndDrag();
        }

        // ------------------------------------------------------------ layout

        void Relayout()
        {
            pad = Theme.S(18);
            gap = Theme.S(compact ? 8 : 12);
            int avail = Math.Max(Theme.S(100), Width - pad * 2 - Theme.S(6));
            int minW = Theme.S(compact ? 160 : 190);
            cols = Math.Max(1, (avail + gap) / (minW + gap));
            tileW = (avail - gap * (cols - 1)) / cols;
            tileH = Theme.S(compact ? 50 : 96);
            int rows = (items.Count + cols - 1) / cols;
            contentHeight = pad * 2 + rows * tileH + Math.Max(0, rows - 1) * gap;
            ClampScroll();
        }

        void ClampScroll()
        {
            scroll = Math.Max(0, Math.Min(scroll, contentHeight - Height));
        }

        Rectangle TileRect(int i)
        {
            int r = i / cols, c = i % cols;
            return new Rectangle(pad + c * (tileW + gap), pad + r * (tileH + gap) - scroll, tileW, tileH);
        }

        Rectangle StarRect(Rectangle tile)
        {
            int s = Theme.S(24);
            return compact
                ? new Rectangle(tile.Right - s - Theme.S(6), tile.Y + (tile.Height - s) / 2, s, s)
                : new Rectangle(tile.Right - s - Theme.S(6), tile.Y + Theme.S(6), s, s);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = TileRect(i);
                if (r.Bottom < 0) continue;
                if (r.Top > Height) break;
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        /// <summary>The slot (index) a dragged pad would land in at this point.</summary>
        int SlotAt(Point p)
        {
            int col = Math.Max(0, Math.Min(cols - 1, (p.X - pad + gap / 2) / (tileW + gap)));
            int row = Math.Max(0, (p.Y + scroll - pad + gap / 2) / (tileH + gap));
            return Math.Max(0, Math.Min(items.Count - 1, row * cols + col));
        }

        bool HasScrollbar { get { return contentHeight > Height; } }

        Rectangle ThumbRect()
        {
            int th = Math.Max(Theme.S(36), Height * Height / Math.Max(1, contentHeight));
            int y = (int)((long)scroll * (Height - th) / Math.Max(1, contentHeight - Height));
            return new Rectangle(Width - Theme.S(9), y + Theme.S(2), Theme.S(6), th - Theme.S(4));
        }

        // ------------------------------------------------------------ mouse

        protected override void OnResize(EventArgs e) { Relayout(); base.OnResize(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta * Theme.S(70) / 120;
            ClampScroll();
            if (draggingPad) dropIndex = SlotAt(dragPos);
            else hover = HitTest(e.Location);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (HasScrollbar && e.X >= Width - Theme.S(14))
            {
                dragThumb = true; dragY = e.Y; dragScroll = scroll;
                return;
            }
            int i = HitTest(e.Location);
            if (i < 0)
            {
                if (items.Count == 0 && !Filtered && e.Button == MouseButtons.Left && EmptyClicked != null) EmptyClicked();
                return;
            }
            if (e.Button == MouseButtons.Left) { pressIndex = i; pressPoint = e.Location; }
            else if (e.Button == MouseButtons.Right && PadMenu != null) PadMenu(items[i], e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                int th = ThumbRect().Height + Theme.S(4);
                scroll = dragScroll + (int)((long)(e.Y - dragY) * (contentHeight - Height) / Math.Max(1, Height - th));
                ClampScroll();
                Invalidate();
                return;
            }

            if (pressIndex >= 0 && !draggingPad && AllowReorder &&
                (Math.Abs(e.X - pressPoint.X) > Theme.S(6) || Math.Abs(e.Y - pressPoint.Y) > Theme.S(6)))
            {
                draggingPad = true;
                dragItem = items[pressIndex];
                Rectangle r = TileRect(pressIndex);
                grabOffset = new Point(pressPoint.X - r.X, pressPoint.Y - r.Y);
                dropIndex = pressIndex;
                hover = -1;
                Cursor = Cursors.SizeAll;
                autoScroll.Start();
            }

            if (draggingPad)
            {
                dragPos = e.Location;
                dragOutside = !ClientRectangle.Contains(e.Location);
                if (!dragOutside) dropIndex = SlotAt(e.Location);
                if (DragMoved != null) DragMoved(dragItem, PointToScreen(e.Location));
                Invalidate();
                return;
            }

            int h = HitTest(e.Location);
            bool star = h >= 0 && StarRect(TileRect(h)).Contains(e.Location);
            Cursor = h >= 0 || (items.Count == 0 && !Filtered) ? Cursors.Hand : Cursors.Default;
            if (h != hover || star != hoverStar) { hover = h; hoverStar = star; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragThumb = false;
            if (e.Button != MouseButtons.Left) return;
            if (draggingPad)
            {
                SoundEntry item = dragItem;
                bool outside = dragOutside;
                int target = dropIndex;
                Point screen = PointToScreen(e.Location);
                EndDrag();
                if (outside) { if (DroppedOutside != null) DroppedOutside(item, screen); }
                else if (target != items.IndexOf(item) && Reordered != null) Reordered(item, target);
                return;
            }
            int i = pressIndex;
            pressIndex = -1;
            if (i < 0 || i >= items.Count || HitTest(e.Location) != i) return;
            Keys mods = ModifierKeys;
            if (StarRect(TileRect(i)).Contains(e.Location)) { if (StarClicked != null) StarClicked(items[i]); }
            else if ((mods & (Keys.Control | Keys.Shift)) != 0) ClickSelect(i, mods);
            else
            {
                anchor = items[i];
                ClearSelection();
                if (PadClicked != null) PadClicked(items[i]);
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (draggingPad && MouseButtons != MouseButtons.Left) EndDrag();
            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hover != -1 && !draggingPad) { hover = -1; hoverStar = false; Invalidate(); }
        }

        void EndDrag()
        {
            draggingPad = false;
            dragOutside = false;
            dragItem = null;
            pressIndex = -1;
            dropIndex = -1;
            autoScroll.Stop();
            Cursor = Cursors.Default;
            Invalidate();
            if (DragEnded != null) DragEnded();
        }

        void AutoScrollTick()
        {
            if (!draggingPad || dragOutside) return;
            int edge = Theme.S(50), step = 0;
            if (dragPos.Y < edge) step = -Theme.S(14);
            else if (dragPos.Y > Height - edge) step = Theme.S(14);
            if (step == 0) return;
            int before = scroll;
            scroll += step;
            ClampScroll();
            if (scroll != before) { dropIndex = SlotAt(dragPos); Invalidate(); }
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (items.Count == 0)
            {
                DrawEmpty(g);
                return;
            }

            // While dragging, the other pads make room for the dragged one at dropIndex.
            List<SoundEntry> view = items;
            if (draggingPad && !dragOutside)
            {
                view = new List<SoundEntry>(items);
                view.Remove(dragItem);
                view.Insert(Math.Min(dropIndex, view.Count), null);
            }

            for (int i = 0; i < view.Count; i++)
            {
                Rectangle r = TileRect(i);
                if (r.Bottom < 0) continue;
                if (r.Top > Height) break;
                SoundEntry s = view[i];
                if (s == null) DrawPlaceholder(g, r);
                else if (s == dragItem) DrawPlaceholder(g, r); // dragged outside: keep its slot visible
                else DrawPad(g, s, r, i == hover && !draggingPad, i == hover && hoverStar);
            }

            if (draggingPad && !dragOutside) // outside, the highlighted tab shows where it goes
            {
                Rectangle fr = new Rectangle(dragPos.X - grabOffset.X, dragPos.Y - grabOffset.Y, tileW, tileH);
                using (GraphicsPath shadow = Theme.Round(new RectangleF(fr.X + Theme.S(3), fr.Y + Theme.S(6), fr.Width, fr.Height), Theme.S(12)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                    g.FillPath(b, shadow);
                DrawPad(g, dragItem, fr, true, false);
            }

            if (HasScrollbar) Theme.FillRound(g, dragThumb ? Theme.Faint : Theme.Border, ThumbRect(), Theme.S(3));
        }

        void DrawPlaceholder(Graphics g, Rectangle r)
        {
            RectangleF rf = new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            Theme.FillRound(g, Color.FromArgb(25, Theme.Accent), rf, Theme.S(12));
            using (GraphicsPath p = Theme.Round(rf, Theme.S(12)))
            using (Pen pen = new Pen(Theme.Mix(Theme.Border, Theme.Accent, 0.6f), Theme.S(2)))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, p);
            }
        }

        void DrawEmpty(Graphics g)
        {
            int cy = Height / 2 - Theme.S(50);
            string title = Filtered ? "No sounds match your search" : EmptyTitle;
            string sub = Filtered ? "Search looks through all categories. Try a different word." : EmptySub;
            TextRenderer.DrawText(g, Filtered ? Theme.IcSearch : Theme.IcMusic, Theme.IconsLarge, new Rectangle(0, cy, Width, Theme.S(56)), Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, title, Theme.Title, new Rectangle(0, cy + Theme.S(66), Width, Theme.S(30)), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, sub, Theme.Body, new Rectangle(0, cy + Theme.S(100), Width, Theme.S(24)), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        void DrawPad(Graphics g, SoundEntry s, Rectangle r, bool hot, bool starHot)
        {
            float prog;
            bool playing = progress.TryGetValue(s, out prog);
            Color tint = ColorOf != null ? ColorOf(s) : Theme.PadColors[0];
            Color bg = hot ? Theme.CardHover : Theme.Card;
            RectangleF rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1);
            float radius = Theme.S(compact ? 10 : 12);

            using (GraphicsPath path = Theme.Round(rf, radius))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, path);
                if (playing)
                {
                    Region old = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, Theme.Accent)))
                        g.FillRectangle(b, rf.X, rf.Y, rf.Width * prog, rf.Height);
                    g.Clip = old;
                }
                bool sel = Selected.Contains(s);
                Color edge = sel ? Theme.AccentHover : playing ? Theme.Accent : hot ? Theme.Faint : Theme.Border;
                using (Pen p = new Pen(edge, playing || sel ? Theme.S(2) : 1f)) g.DrawPath(p, path);
            }

            // icon tile in the sound's color: music note normally, play on hover, stop while playing
            int ic = Theme.S(compact ? 28 : 34);
            int inset = Theme.S(compact ? 11 : 12);
            Rectangle icon = new Rectangle(r.X + inset, compact ? r.Y + (r.Height - ic) / 2 : r.Y + inset, ic, ic);
            Theme.FillRound(g, playing ? Theme.Accent : Color.FromArgb(hot ? 90 : 55, tint), icon, Theme.S(compact ? 7 : 9));
            string glyph = playing ? Theme.IcStop : hot ? Theme.IcPlay : Theme.IcMusic;
            TextRenderer.DrawText(g, glyph, Theme.Icons, icon, playing ? Color.White : Theme.Mix(tint, Color.White, 0.35f),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Selected.Contains(s))
            {
                // check badge on the icon's corner
                int d = Theme.S(16);
                Rectangle badge = new Rectangle(icon.Right - d + Theme.S(4), icon.Y - Theme.S(4), d, d);
                using (SolidBrush b = new SolidBrush(Theme.AccentHover)) g.FillEllipse(b, badge);
                TextRenderer.DrawText(g, Theme.IcCheck, new Font(Theme.Icons.FontFamily, 6.5f), badge, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // favorite star: always shown when set, outline on hover
            Rectangle star = StarRect(r);
            if (s.Favorite || hot)
            {
                Color sc = s.Favorite ? Theme.Warn : starHot ? Theme.Text : Theme.Faint;
                TextRenderer.DrawText(g, s.Favorite ? Theme.IcStarFilled : Theme.IcStar, Theme.Icons, star, sc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            int nx = icon.Right + Theme.S(10);
            int nameRight = star.Left - Theme.S(2);
            string info = InfoText(s);
            if (compact)
            {
                TextRenderer.DrawText(g, s.Name, Theme.Bold, new Rectangle(nx, r.Y + Theme.S(7), nameRight - nx, Theme.S(20)),
                    s.Error != null ? Theme.Danger : Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, info, Theme.Small, new Rectangle(nx, r.Y + Theme.S(27), nameRight - nx, Theme.S(16)),
                    s.Error != null ? Theme.Danger : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return;
            }

            Rectangle nameRect = new Rectangle(nx, r.Y + Theme.S(10), nameRight - nx, Theme.S(40));
            TextRenderer.DrawText(g, s.Name, Theme.Bold, nameRect, s.Error != null ? Theme.Danger : Theme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);

            int by = r.Bottom - Theme.S(30);
            int bx = r.X + Theme.S(12);
            if (s.Hotkey != Keys.None)
            {
                string hk = HotkeyText(s.Hotkey);
                Size ts = TextRenderer.MeasureText(hk, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
                Rectangle chip = new Rectangle(bx, by, ts.Width + Theme.S(28), Theme.S(20));
                Theme.FillRound(g, Theme.Bg, chip, Theme.S(5));
                TextRenderer.DrawText(g, Theme.IcKeyboard, Theme.IconsSmall, new Rectangle(chip.X + Theme.S(6), chip.Y, Theme.S(14), chip.Height),
                    Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, hk, Theme.Small, new Rectangle(chip.X + Theme.S(22), chip.Y, ts.Width + 2, chip.Height), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            string right = s.Error != null ? "Can't load" : s.Seconds < 0 ? "" : FormatLength(s.PlayedSeconds);
            if (s.Volume != 100 && s.Error == null) right = s.Volume + "%   " + right;
            if (s.Loop && s.Error == null) right = "loop   " + right;
            TextRenderer.DrawText(g, right, Theme.Small, new Rectangle(r.X, by, r.Width - Theme.S(12), Theme.S(20)),
                s.Error != null ? Theme.Danger : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        static string InfoText(SoundEntry s)
        {
            if (s.Error != null) return "Can't load";
            List<string> parts = new List<string>();
            if (s.Hotkey != Keys.None) parts.Add(HotkeyText(s.Hotkey));
            if (s.Seconds >= 0) parts.Add(FormatLength(s.PlayedSeconds));
            if (s.Volume != 100) parts.Add(s.Volume + "%");
            if (s.Loop) parts.Add("loop");
            return string.Join("  ·  ", parts);
        }

        public static string HotkeyText(Keys k)
        {
            return k == Keys.None ? "" : new KeysConverter().ConvertToString(k);
        }

        public static string FormatLength(double seconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            string s = ((int)ts.TotalMinutes) + ":" + ts.Seconds.ToString("00");
            return seconds < 10 ? s + "." + ts.Milliseconds / 100 : s; // short clips: show tenths, so 0.9 s isn't "0:00"
        }

        public static int HashIndex(string text, int count)
        {
            int h = 17;
            foreach (char c in text) h = unchecked(h * 31 + c);
            return (h & 0x7FFFFFFF) % count;
        }
    }
}
