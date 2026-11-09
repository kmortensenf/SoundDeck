using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundDeck
{
    static class Theme
    {
        public static readonly Color Bg = Hex(0x111114);
        public static readonly Color Panel = Hex(0x18181D);
        public static readonly Color Card = Hex(0x222229);
        public static readonly Color CardHover = Hex(0x2B2B34);
        public static readonly Color Border = Hex(0x33333E);
        public static readonly Color Text = Hex(0xEDEDF2);
        public static readonly Color Muted = Hex(0x9696A3);
        public static readonly Color Faint = Hex(0x5E5E6B);
        public static readonly Color Accent = Hex(0x8B5CF6);
        public static readonly Color AccentHover = Hex(0x9F78FF);
        public static readonly Color Danger = Hex(0xF05252);
        public static readonly Color Good = Hex(0x34D399);
        public static readonly Color Warn = Hex(0xFBBF24);

        /// <summary>Colors for the stripe on each sound pad, picked from the sound's name.</summary>
        public static readonly Color[] PadColors =
        {
            Hex(0x8B5CF6), Hex(0x3B82F6), Hex(0x06B6D4), Hex(0x10B981),
            Hex(0x84CC16), Hex(0xF59E0B), Hex(0xF97316), Hex(0xEF4444), Hex(0xEC4899),
        };

        public static float Scale = 1f;
        public static Font Body, Small, Bold, Title, SectionFont, Icons, IconsSmall, IconsLarge;

        public static void Init(float dpiScale)
        {
            Scale = dpiScale;
            Body = new Font("Segoe UI", 9.5f);
            Small = new Font("Segoe UI", 8.5f);
            Bold = new Font("Segoe UI Semibold", 10f);
            Title = new Font("Segoe UI Semibold", 14f);
            SectionFont = new Font("Segoe UI Semibold", 8f);
            string iconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Icons = new Font(iconFont, 10f);
            IconsSmall = new Font(iconFont, 8f);
            IconsLarge = new Font(iconFont, 28f);
        }

        // more glyphs (written as escapes)
        public static readonly string IcStar = Glyph(0xE734), IcStarFilled = Glyph(0xE735), IcTag = Glyph(0xE8EC),
            IcGrid = Glyph(0xF0E2), IcList = Glyph(0xE8FD), IcAll = Glyph(0xE8A9), IcEdit = Glyph(0xE70F),
            IcDelete = Glyph(0xE74D), IcChevronRight = Glyph(0xE76C), IcRecord = Glyph(0xE7C8);

        static string Glyph(int code) { return ((char)code).ToString(); }

        // Segoe Fluent Icons / MDL2 glyphs
        public const string IcAdd = "", IcStop = "", IcPlay = "", IcSearch = "", IcMic = "",
            IcVolume = "", IcHeadphones = "", IcKeyboard = "", IcMusic = "", IcRefresh = "",
            IcDownload = "", IcCheck = "", IcWarning = "", IcFolder = "", IcFile = "";

        public static int S(int px) { return (int)Math.Round(px * Scale); }

        public static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = Round(r, radius)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, float width, RectangleF r, float radius)
        {
            using (GraphicsPath p = Round(r, radius)) using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        static bool FontExists(string name)
        {
            using (Font f = new Font(name, 10f)) return f.Name == name;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark title bar (Windows 10 20H1+ / 11).</summary>
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                int caption = 0x1D1814; // COLORREF (BGR) of Panel, Windows 11 only
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
            }
            catch { }
        }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }

        class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.CardHover; } }
            public override Color MenuItemSelected { get { return Theme.CardHover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHover; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }
    }
}
