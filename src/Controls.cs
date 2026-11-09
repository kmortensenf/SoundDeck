using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>Base for the owner-drawn controls: double-buffered, paints its parent's background first.</summary>
    abstract class DrawnControl : Control
    {
        protected bool hover, down;

        protected DrawnControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        protected Color ParentBack { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(ParentBack); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    }

    enum ButtonStyle { Normal, Primary, Danger, Ghost }

    class FlatButton : DrawnControl
    {
        public ButtonStyle Style;
        string icon;
        public string Icon { get { return icon; } set { icon = value; Invalidate(); } }

        public FlatButton(string text, string icon, ButtonStyle style)
        {
            Text = text;
            this.icon = icon;
            Style = style;
            Cursor = Cursors.Hand;
            Height = Theme.S(34);
            FitWidth();
        }

        public void FitWidth()
        {
            int w = Theme.S(28);
            if (!string.IsNullOrEmpty(Text)) w += TextRenderer.MeasureText(Text, Font).Width;
            if (!string.IsNullOrEmpty(icon)) w += Theme.S(string.IsNullOrEmpty(Text) ? 8 : 22);
            Width = w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill, border, text;
            switch (Style)
            {
                case ButtonStyle.Primary:
                    fill = hover ? Theme.AccentHover : Theme.Accent; border = fill; text = Color.White; break;
                case ButtonStyle.Danger:
                    fill = hover ? Theme.Mix(Theme.Card, Theme.Danger, 0.25f) : Theme.Card; border = Theme.Mix(Theme.Border, Theme.Danger, 0.6f); text = Theme.Mix(Theme.Text, Theme.Danger, 0.6f); break;
                case ButtonStyle.Ghost:
                    fill = hover ? Theme.CardHover : ParentBack; border = fill; text = hover ? Theme.Text : Theme.Muted; break;
                default:
                    fill = hover ? Theme.CardHover : Theme.Card; border = Theme.Border; text = Theme.Text; break;
            }
            if (down) fill = Theme.Mix(fill, Color.Black, 0.15f);
            if (!Enabled) { fill = Theme.Card; border = Theme.Border; text = Theme.Faint; }
            Theme.FillRound(g, fill, r, Theme.S(7));
            Theme.DrawRound(g, border, 1f, r, Theme.S(7));

            Size ts = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int iconW = string.IsNullOrEmpty(icon) ? 0 : Theme.S(16);
            int gap = iconW > 0 && ts.Width > 0 ? Theme.S(8) : 0;
            int x = (Width - (iconW + gap + ts.Width)) / 2;
            if (iconW > 0)
                TextRenderer.DrawText(g, icon, Theme.Icons, new Rectangle(x, 0, iconW, Height), text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (ts.Width > 0)
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x + iconW + gap, 0, ts.Width + 2, Height), text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    class FlatSlider : DrawnControl
    {
        int value;
        public int Maximum = 100;
        public string Label, Glyph;
        public event EventHandler ValueChanged;

        public FlatSlider(string label, string glyph)
        {
            Label = label;
            Glyph = glyph;
            Height = Theme.S(44);
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(0, Math.Min(Maximum, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        int TrackLeft { get { return Theme.S(8); } }
        int TrackRight { get { return Width - Theme.S(8); } }
        int TrackY { get { return Height - Theme.S(14); } }

        void SetFromMouse(int x)
        {
            Value = (int)Math.Round((x - TrackLeft) * (double)Maximum / Math.Max(1, TrackRight - TrackLeft));
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && Enabled) SetFromMouse(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (down && Enabled) SetFromMouse(e.X); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Enabled) Value += e.Delta > 0 ? 5 : -5;
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color label = Enabled ? Theme.Muted : Theme.Faint;
            int tx = 0;
            if (!string.IsNullOrEmpty(Glyph))
            {
                TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(0, 0, Theme.S(18), Theme.S(22)), label,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                tx = Theme.S(24);
            }
            TextRenderer.DrawText(g, Label, Theme.Small, new Rectangle(tx, 0, Width - tx - Theme.S(44), Theme.S(22)), label,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, value + "%", Theme.Small, new Rectangle(Width - Theme.S(44), 0, Theme.S(44), Theme.S(22)),
                Enabled ? Theme.Text : Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);

            float h = Theme.S(4);
            float y = TrackY - h / 2;
            float fx = TrackLeft + (TrackRight - TrackLeft) * value / (float)Maximum;
            Theme.FillRound(g, Theme.Border, new RectangleF(TrackLeft, y, TrackRight - TrackLeft, h), h / 2);
            Theme.FillRound(g, Enabled ? Theme.Accent : Theme.Faint, new RectangleF(TrackLeft, y, fx - TrackLeft, h), h / 2);
            float k = Theme.S(hover || down ? 8 : 7);
            using (SolidBrush b = new SolidBrush(Enabled ? Color.White : Theme.Muted))
                g.FillEllipse(b, fx - k, TrackY - k, k * 2, k * 2);
        }
    }

    class Toggle : DrawnControl
    {
        bool on;
        public event EventHandler CheckedChanged;

        public Toggle(string text)
        {
            Text = text;
            Height = Theme.S(30);
            Cursor = Cursors.Hand;
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (value == on) return;
                on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int sw = Theme.S(38), sh = Theme.S(20);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - sw - Theme.S(8), Height), Enabled ? Theme.Text : Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            RectangleF pill = new RectangleF(Width - sw - 1, (Height - sh) / 2f, sw, sh);
            Color fill = !Enabled ? Theme.Card : on ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.Faint : Theme.Border);
            Theme.FillRound(g, fill, pill, sh / 2f);
            float k = sh - Theme.S(6);
            float kx = on ? pill.Right - Theme.S(3) - k : pill.X + Theme.S(3);
            using (SolidBrush b = new SolidBrush(on && Enabled ? Color.White : Theme.Muted))
                g.FillEllipse(b, kx, pill.Y + Theme.S(3), k, k);
        }
    }

    /// <summary>Rounded card with a colored icon, a title and a subtitle.</summary>
    class StatusCard : DrawnControl
    {
        public Color Dot = Theme.Good;
        public string Glyph = Theme.IcCheck, Title = "", Sub = "";

        public StatusCard() { Height = Theme.S(70); }

        public void Set(Color dot, string glyph, string title, string sub)
        {
            Dot = dot; Glyph = glyph; Title = title; Sub = sub;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, Theme.Card, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10));
            int d = Theme.S(32);
            Rectangle circle = new Rectangle(Theme.S(12), Theme.S(12), d, d);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(45, Dot))) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Glyph, Theme.Icons, circle, Dot, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            int x = Theme.S(54);
            TextRenderer.DrawText(g, Title, Theme.Bold, new Rectangle(x, Theme.S(10), Width - x - Theme.S(10), Theme.S(20)), Theme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Sub, Theme.Small, new Rectangle(x, Theme.S(31), Width - x - Theme.S(10), Height - Theme.S(34)), Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }

    class SearchBox : DrawnControl
    {
        public readonly TextBox Box;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public SearchBox()
        {
            Height = Theme.S(34);
            Cursor = Cursors.IBeam;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Card;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Body;
            Box.HandleCreated += (s, e) => SendMessage(Box.Handle, 0x1501, new IntPtr(1), "Search sounds   (Ctrl+F)");
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null) return; // layout can run from the base constructor, before Box exists
            int x = Theme.S(34);
            Box.SetBounds(x, (Height - Box.PreferredHeight) / 2, Width - x - Theme.S(10), Box.PreferredHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Card, r, Theme.S(8));
            Theme.DrawRound(g, Box.Focused ? Theme.Accent : Theme.Border, 1f, r, Theme.S(8));
            TextRenderer.DrawText(g, Theme.IcSearch, Theme.Icons, new Rectangle(Theme.S(10), 0, Theme.S(18), Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    class DarkCombo : ComboBox
    {
        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Card;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ItemHeight = Theme.S(24);
            Cursor = Cursors.Hand;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = (e.State & DrawItemState.Selected) != 0 && !edit;
            using (SolidBrush b = new SolidBrush(sel ? Theme.Accent : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            Rectangle r = e.Bounds;
            r.Inflate(-Theme.S(6), 0);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, r, sel ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF) return; // WM_PAINT: repaint the frame and arrow in the dark style
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                if (!Enabled)
                {
                    // Windows paints disabled combos light grey; draw the whole face dark instead
                    using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(Theme.S(6), 0, Width - Theme.S(36), Height), Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                int aw = Theme.S(26);
                Rectangle arrow = new Rectangle(Width - aw - 1, 1, aw, Height - 2);
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, arrow);
                TextRenderer.DrawText(g, "", new Font(Theme.Icons.FontFamily, 7.5f), arrow, Enabled ? Theme.Muted : Theme.Faint,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (Pen p = new Pen(Focused || DroppedDown ? Theme.Accent : Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    class DarkDialog : Form
    {
        public DarkDialog(string title)
        {
            Text = title;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }
    }

    /// <summary>Captures one key combination. Esc cancels, Backspace/Delete clears.</summary>
    class HotkeyDialog : DarkDialog
    {
        public Keys Result = Keys.None;
        readonly string current;
        readonly bool pushToTalk; // also accepts mouse side/middle buttons
        string live = "";

        public HotkeyDialog(string title, Keys current) : this(title, current, false) { }

        public HotkeyDialog(string title, Keys current, bool pushToTalk) : base(title)
        {
            this.pushToTalk = pushToTalk;
            this.current = current == Keys.None ? "none" : PushToTalk.Describe(current);
            ClientSize = new Size(Theme.S(pushToTalk ? 460 : 400), Theme.S(210));
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!pushToTalk) return;
            Keys k = e.Button == MouseButtons.XButton1 ? Keys.XButton1 : e.Button == MouseButtons.XButton2 ? Keys.XButton2
                   : e.Button == MouseButtons.Middle ? Keys.MButton : Keys.None;
            if (k == Keys.None) return;
            Result = k;
            DialogResult = DialogResult.OK;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ClientSize.Width;
            TextRenderer.DrawText(g, Theme.IcKeyboard, Theme.IconsLarge, new Rectangle(0, Theme.S(18), w, Theme.S(50)), Theme.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, pushToTalk ? "Press your push-to-talk key or mouse button" : "Press a key combination", Theme.Title,
                new Rectangle(0, Theme.S(74), w, Theme.S(30)), Theme.Text, TextFormatFlags.HorizontalCenter);
            Rectangle box = new Rectangle(Theme.S(90), Theme.S(112), w - Theme.S(180), Theme.S(36));
            Theme.FillRound(g, Theme.Card, box, Theme.S(8));
            Theme.DrawRound(g, Theme.Accent, 1f, box, Theme.S(8));
            TextRenderer.DrawText(g, live.Length > 0 ? live + "…" : "Current: " + current, Theme.Bold, box, live.Length > 0 ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string tip = pushToTalk
                ? "Use the same key as in Discord   ·   Esc cancel   ·   Backspace turns it off"
                : "Tip: F-keys and numpad keys work well   ·   Esc cancel   ·   Backspace clear";
            TextRenderer.DrawText(g, tip, Theme.Small, new Rectangle(0, Theme.S(166), w, Theme.S(24)), Theme.Faint, TextFormatFlags.HorizontalCenter);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin)
            {
                live = ((keyData & Keys.Control) != 0 ? "Ctrl+" : "") + ((keyData & Keys.Alt) != 0 ? "Alt+" : "") + ((keyData & Keys.Shift) != 0 ? "Shift+" : "");
                Invalidate();
                return true; // wait for a non-modifier key
            }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Back || keyData == Keys.Delete) { Result = Keys.None; DialogResult = DialogResult.OK; return true; }
            Result = keyData;
            DialogResult = DialogResult.OK;
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            live = "";
            Invalidate();
            base.OnKeyUp(e);
        }
    }

    /// <summary>A text box with a rounded dark frame (a plain TextBox border is always light).</summary>
    class DarkInput : DrawnControl
    {
        public readonly TextBox Box;

        public DarkInput()
        {
            Height = Theme.S(32);
            Cursor = Cursors.IBeam;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Card;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Body;
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Box == null) return; // layout can run from the base constructor, before Box exists
            int x = Theme.S(10);
            Box.SetBounds(x, (Height - Box.PreferredHeight) / 2, Width - x * 2, Box.PreferredHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, Theme.Card, r, Theme.S(7));
            Theme.DrawRound(g, Box.Focused ? Theme.Accent : Theme.Border, 1f, r, Theme.S(7));
        }
    }

    class TextDialog : DarkDialog
    {
        public readonly TextBox Box;

        public TextDialog(string title, string label, string value, string okText) : base(title)
        {
            ClientSize = new Size(Theme.S(380), Theme.S(140));
            Label l = new Label();
            l.Text = label;
            l.ForeColor = Theme.Muted;
            l.Font = Theme.Small;
            l.SetBounds(Theme.S(20), Theme.S(16), Theme.S(300), Theme.S(18));
            Controls.Add(l);

            DarkInput input = new DarkInput();
            Box = input.Box;
            Box.Text = value;
            input.SetBounds(Theme.S(20), Theme.S(37), ClientSize.Width - Theme.S(40), Theme.S(32));
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.SuppressKeyPress = true; }
            };
            Controls.Add(input);

            FlatButton ok = new FlatButton(okText, null, ButtonStyle.Primary);
            FlatButton cancel = new FlatButton("Cancel", null, ButtonStyle.Normal);
            ok.Click += (s, e) => DialogResult = DialogResult.OK;
            cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            ok.Location = new Point(ClientSize.Width - Theme.S(20) - ok.Width, Theme.S(88));
            cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, Theme.S(88));
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        /// <summary>Shows the dialog; returns the trimmed text, or null if cancelled or empty.</summary>
        public static string Ask(IWin32Window owner, string title, string label, string value, string okText)
        {
            using (TextDialog d = new TextDialog(title, label, value, okText))
            {
                if (d.ShowDialog(owner) != DialogResult.OK) return null;
                string t = d.Box.Text.Replace('\t', ' ').Trim();
                return t.Length == 0 ? null : t;
            }
        }
    }

    /// <summary>Per-sound volume, 0–200 %. Changes apply live so the user can test while the dialog is open.</summary>
    class VolumeDialog : DarkDialog
    {
        public readonly FlatSlider Slider;

        public VolumeDialog(string soundName, int volume, Action<int> changed, Action preview) : base("Volume for \"" + soundName + "\"")
        {
            ClientSize = new Size(Theme.S(400), Theme.S(150));
            Slider = new FlatSlider("Sound volume (100% = original)", Theme.IcVolume);
            Slider.Maximum = 200;
            Slider.Value = volume;
            Slider.SetBounds(Theme.S(20), Theme.S(18), ClientSize.Width - Theme.S(40), Slider.Height);
            Slider.ValueChanged += (s, e) => changed(Slider.Value);
            Controls.Add(Slider);

            FlatButton test = new FlatButton("Preview", Theme.IcPlay, ButtonStyle.Normal);
            FlatButton reset = new FlatButton("Reset", null, ButtonStyle.Ghost);
            FlatButton done = new FlatButton("Done", null, ButtonStyle.Primary);
            test.Click += (s, e) => preview();
            reset.Click += (s, e) => Slider.Value = 100;
            done.Click += (s, e) => DialogResult = DialogResult.OK;
            int y = Theme.S(96);
            done.Location = new Point(ClientSize.Width - Theme.S(20) - done.Width, y);
            test.Location = new Point(done.Left - Theme.S(8) - test.Width, y);
            reset.Location = new Point(Theme.S(14), y);
            Controls.Add(done);
            Controls.Add(test);
            Controls.Add(reset);
        }
    }
}
