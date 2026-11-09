using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// The settings you set once and rarely touch (devices, hotkeys, instant replay, app options), on pages
    /// picked from a menu on the left, so nothing needs scrolling. The main window owns the controls on the
    /// pages and keeps this window around for next time.
    /// </summary>
    class SettingsDialog : DarkDialog
    {
        class PageInfo { public string Name, Glyph; public Panel Panel; public int NextY; public Rectangle NavRect; }

        readonly List<PageInfo> pages = new List<PageInfo>();
        readonly Panel nav;
        int current = -1, hover = -1;
        readonly int contentX, contentW;

        public SettingsDialog() : base("Settings")
        {
            ClientSize = new Size(Theme.S(760), Theme.S(540));
            contentX = Theme.S(228);
            contentW = ClientSize.Width - contentX - Theme.S(32);

            nav = new Panel();
            nav.SetBounds(0, 0, Theme.S(200), ClientSize.Height);
            nav.BackColor = Theme.Bg;
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(nav, true, null);
            nav.Paint += PaintNav;
            nav.MouseMove += (s, e) =>
            {
                int h = pages.FindIndex(p => p.NavRect.Contains(e.Location));
                nav.Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                if (h != hover) { hover = h; nav.Invalidate(); }
            };
            nav.MouseLeave += (s, e) => { hover = -1; nav.Invalidate(); };
            nav.MouseClick += (s, e) =>
            {
                int h = pages.FindIndex(p => p.NavRect.Contains(e.Location));
                if (h >= 0) ShowPage(h);
            };
            Controls.Add(nav);
        }

        public int ContentWidth { get { return contentW; } }

        /// <summary>Adds a page; fill it with Add().</summary>
        public int AddPage(string name, string glyph, string title)
        {
            PageInfo p = new PageInfo();
            p.Name = name;
            p.Glyph = glyph;
            p.Panel = new Panel();
            p.Panel.SetBounds(contentX, Theme.S(24), contentW, ClientSize.Height - Theme.S(48));
            p.Panel.BackColor = Theme.Panel;
            p.Panel.Visible = false;
            Label t = new Label();
            t.UseMnemonic = false;
            t.Text = title;
            t.Font = Theme.Title;
            t.ForeColor = Theme.Text;
            t.SetBounds(0, 0, contentW, Theme.S(34));
            p.Panel.Controls.Add(t);
            p.NextY = Theme.S(46);
            pages.Add(p);
            Controls.Add(p.Panel);
            int y = Theme.S(30);
            foreach (PageInfo q in pages) { q.NavRect = new Rectangle(Theme.S(12), y, Theme.S(176), Theme.S(38)); y += Theme.S(42); }
            if (current < 0) ShowPage(0);
            return pages.Count - 1;
        }

        /// <summary>Stacks a control under the previous one on a page, full width unless it sets its own.</summary>
        public void Add(int page, Control c, int gapAbove, bool fullWidth)
        {
            PageInfo p = pages[page];
            p.NextY += gapAbove;
            c.Location = new Point(0, p.NextY);
            if (fullWidth) c.Width = contentW;
            p.Panel.Controls.Add(c);
            p.NextY += c.Height;
        }

        public Label Note(int page, string text, int lines)
        {
            Label l = new Label();
            l.UseMnemonic = false;
            l.Text = text;
            l.Font = Theme.Small;
            l.ForeColor = Theme.Faint;
            l.Height = Theme.S(4 + 17 * lines);
            Add(page, l, Theme.S(4), true);
            return l;
        }

        public Label Heading(int page, string text, int gapAbove)
        {
            Label l = new Label();
            l.UseMnemonic = false;
            l.Text = text;
            l.Font = Theme.SectionFont;
            l.ForeColor = Theme.Faint;
            l.Height = Theme.S(18);
            Add(page, l, gapAbove, true);
            return l;
        }

        public void ShowPage(int i)
        {
            current = i;
            for (int k = 0; k < pages.Count; k++) pages[k].Panel.Visible = k == i;
            nav.Invalidate();
        }

        void PaintNav(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < pages.Count; i++)
            {
                PageInfo p = pages[i];
                Rectangle r = p.NavRect;
                if (i == current) Theme.FillRound(g, Theme.Card, r, Theme.S(8));
                else if (i == hover) Theme.FillRound(g, Color.FromArgb(120, Theme.Card), r, Theme.S(8));
                if (i == current) Theme.FillRound(g, Theme.Accent, new Rectangle(r.X, r.Y + Theme.S(9), Theme.S(3), r.Height - Theme.S(18)), Theme.S(1));
                Color c = i == current ? Theme.Text : Theme.Muted;
                TextRenderer.DrawText(g, p.Glyph, Theme.Icons, new Rectangle(r.X + Theme.S(14), r.Y, Theme.S(20), r.Height), i == current ? Theme.AccentHover : Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, p.Name, i == current ? Theme.Bold : Theme.Body, new Rectangle(r.X + Theme.S(44), r.Y, r.Width - Theme.S(48), r.Height), c,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        // Shown with ShowDialog, so closing only hides it: it (and the controls on it) are reused next time.
    }
}
