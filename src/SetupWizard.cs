using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// First-run guide: virtual mic → your microphone → Discord settings → an automatic end-to-end test → done.
    /// Every choice is applied straight to the main window's settings, so nothing has to be set twice.
    /// </summary>
    class SetupWizard : DarkDialog
    {
        static readonly string[] StepNames = { "Welcome", "Virtual mic", "Your microphone", "Discord", "Test it", "Done" };

        readonly MainForm main;
        int step;
        readonly Panel rail;
        readonly List<Panel> pages = new List<Panel>();
        readonly FlatButton btnBack, btnNext, btnSkip;
        readonly Timer timer = new Timer();
        int x0, cw;

        // virtual mic
        StatusCard cableCard;
        FlatButton btnInstall;
        Label cableHint;
        // microphone
        DarkCombo micBox;
        Panel meter;
        MicCapture meterCap;
        volatile float meterLevel;
        Toggle tgVoice, tgNoise;
        // discord
        FlatButton btnPtt;
        // test
        FlatButton btnTest;
        StatusCard testCard;
        Toggle tgListen;
        MicCapture listenCap;
        OutputDevice listenOut;
        bool testing;

        /// <summary>What the user asked to do when closing from the last page.</summary>
        public string FinishAction;

        public SetupWizard(MainForm main) : base("SoundDeck setup")
        {
            this.main = main;
            ClientSize = new Size(Theme.S(760), Theme.S(520));
            x0 = Theme.S(232);
            cw = ClientSize.Width - x0 - Theme.S(32);

            rail = new Panel();
            rail.SetBounds(0, 0, Theme.S(200), ClientSize.Height);
            rail.BackColor = Theme.Bg;
            rail.Paint += PaintRail;
            Controls.Add(rail);

            pages.Add(BuildWelcome());
            pages.Add(BuildCable());
            pages.Add(BuildMic());
            pages.Add(BuildDiscord());
            pages.Add(BuildTest());
            pages.Add(BuildDone());
            foreach (Panel p in pages) Controls.Add(p);

            int by = ClientSize.Height - Theme.S(60);
            btnNext = new FlatButton("Let's go", null, ButtonStyle.Primary);
            btnBack = new FlatButton("Back", null, ButtonStyle.Normal);
            btnSkip = new FlatButton("Skip setup", null, ButtonStyle.Ghost);
            btnNext.Click += (s, e) => Next();
            btnBack.Click += (s, e) => Go(step - 1);
            btnSkip.Click += (s, e) => { FinishAction = null; DialogResult = DialogResult.OK; };
            btnSkip.Location = new Point(x0 - Theme.S(8), by);
            Controls.Add(btnNext);
            Controls.Add(btnBack);
            Controls.Add(btnSkip);

            timer.Interval = 60;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Go(0);
        }

        // ================================================================ pages

        Panel Page()
        {
            Panel p = new Panel();
            p.SetBounds(x0, Theme.S(28), cw, ClientSize.Height - Theme.S(100));
            p.BackColor = Theme.Panel;
            return p;
        }

        static Label Title(Panel p, string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Title;
            l.ForeColor = Theme.Text;
            l.SetBounds(0, y, p.Width, Theme.S(34));
            p.Controls.Add(l);
            return l;
        }

        static Label Para(Panel p, string text, int y, int height)
        {
            Label l = new Label();
            l.UseMnemonic = false; // show "&" as-is ("Voice & Video")
            l.Text = text;
            l.Font = Theme.Body;
            l.ForeColor = Theme.Muted;
            l.SetBounds(0, y, p.Width, height);
            p.Controls.Add(l);
            return l;
        }

        Panel BuildWelcome()
        {
            Panel p = Page();
            Label icon = new Label();
            icon.Text = Theme.IcMusic;
            icon.Font = Theme.IconsLarge;
            icon.ForeColor = Theme.Accent;
            icon.SetBounds(0, Theme.S(20), Theme.S(60), Theme.S(60));
            p.Controls.Add(icon);
            Title(p, "Welcome to SoundDeck", Theme.S(90));
            Para(p, "This quick setup gets your sounds into Discord, games and other voice apps. It takes about two minutes.\n\n" +
                    "We'll set up a virtual microphone, pick your real mic, go through the Discord settings that matter, " +
                    "and then test that everything actually works.", Theme.S(134), Theme.S(120));
            Para(p, "You can open this guide again any time from the sidebar (App → Setup guide).", Theme.S(270), Theme.S(40)).ForeColor = Theme.Faint;
            return p;
        }

        Panel BuildCable()
        {
            Panel p = Page();
            Title(p, "Virtual microphone", 0);
            Para(p, "Windows can't play sounds into a microphone by itself, so SoundDeck uses VB-CABLE: a free virtual audio " +
                    "cable by VB-Audio. SoundDeck sends your voice and your sounds into it, and Discord listens to it as its microphone.",
                 Theme.S(44), Theme.S(70));
            cableCard = new StatusCard();
            cableCard.SetBounds(0, Theme.S(124), cw, Theme.S(70));
            p.Controls.Add(cableCard);
            btnInstall = new FlatButton("Install virtual mic", Theme.IcDownload, ButtonStyle.Primary);
            btnInstall.Location = new Point(0, Theme.S(208));
            btnInstall.Click += (s, e) => main.InstallCable();
            p.Controls.Add(btnInstall);
            cableHint = Para(p, "Installing asks for administrator rights, then click \"Install Driver\" in VB-Audio's installer. " +
                    "If it asks you to restart, do that and open SoundDeck again: this guide picks up from here.", Theme.S(254), Theme.S(60));
            cableHint.ForeColor = Theme.Faint;
            return p;
        }

        Panel BuildMic()
        {
            Panel p = Page();
            Title(p, "Your microphone", 0);
            Para(p, "Pick the microphone you talk into. Then say something: the bar should move.", Theme.S(44), Theme.S(24));
            micBox = new DarkCombo();
            micBox.SetBounds(0, Theme.S(78), cw, Theme.S(30));
            micBox.SelectedIndexChanged += (s, e) => MicChanged();
            p.Controls.Add(micBox);
            meter = new Panel();
            meter.SetBounds(0, Theme.S(122), cw, Theme.S(12));
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(meter, true, null);
            meter.Paint += PaintMeter;
            p.Controls.Add(meter);

            tgVoice = new Toggle("Mix my voice in (so people hear me and my sounds)");
            tgVoice.SetBounds(0, Theme.S(160), cw, Theme.S(30));
            tgVoice.CheckedChanged += (s, e) => main.MicToggle.Checked = tgVoice.Checked;
            p.Controls.Add(tgVoice);
            tgNoise = new Toggle("Remove background noise from my voice");
            tgNoise.SetBounds(0, Theme.S(196), cw, Theme.S(30));
            tgNoise.CheckedChanged += (s, e) => main.DenoiseToggle.Checked = tgNoise.Checked;
            p.Controls.Add(tgNoise);
            Para(p, "Noise removal replaces Discord's own noise suppression (Krisp), which would also remove your sounds.",
                 Theme.S(232), Theme.S(40)).ForeColor = Theme.Faint;
            return p;
        }

        Panel BuildDiscord()
        {
            Panel p = Page();
            Title(p, "Discord settings", 0);
            Para(p, "In Discord, open Settings → Voice & Video and set these:", Theme.S(44), Theme.S(24));
            string[,] items =
            {
                { "Input Device", "CABLE Output (VB-Audio Virtual Cable)" },
                { "Noise Suppression", "None (Krisp would remove your sounds)" },
                { "Echo Cancellation", "Off" },
                { "Automatic Gain Control", "Off" },
            };
            for (int i = 0; i < items.GetLength(0); i++)
            {
                int y = Theme.S(76) + i * Theme.S(40);
                Label num = new Label();
                num.Text = (i + 1).ToString();
                num.Font = Theme.Bold;
                num.ForeColor = Theme.Accent;
                num.SetBounds(0, y + Theme.S(2), Theme.S(22), Theme.S(24));
                Label what = new Label();
                what.Text = items[i, 0];
                what.Font = Theme.Bold;
                what.ForeColor = Theme.Text;
                what.SetBounds(Theme.S(24), y, Theme.S(190), Theme.S(26));
                Label val = new Label();
                val.Text = items[i, 1];
                val.Font = Theme.Body;
                val.ForeColor = Theme.Muted;
                val.SetBounds(Theme.S(214), y + Theme.S(1), cw - Theme.S(214), Theme.S(26));
                p.Controls.AddRange(new Control[] { num, what, val });
            }
            FlatButton open = new FlatButton("Open Discord", Theme.IcChevronRight, ButtonStyle.Normal);
            open.Location = new Point(0, Theme.S(244));
            open.Click += (s, e) => { try { Process.Start("discord://-/settings/voice"); } catch { MessageBox.Show(this, "Discord doesn't seem to be installed.", "SoundDeck"); } };
            p.Controls.Add(open);

            Label ptt = new Label();
            ptt.Text = "Do you use push-to-talk in Discord? Then SoundDeck needs the same key, so it can hold it while sounds play:";
            ptt.Font = Theme.Body;
            ptt.ForeColor = Theme.Muted;
            ptt.SetBounds(0, Theme.S(296), cw, Theme.S(40));
            p.Controls.Add(ptt);
            btnPtt = new FlatButton("", Theme.IcMic, ButtonStyle.Normal);
            btnPtt.Location = new Point(0, Theme.S(338));
            btnPtt.Click += (s, e) => { main.SetPttKey(); UpdatePttButton(); };
            p.Controls.Add(btnPtt);
            return p;
        }

        Panel BuildTest()
        {
            Panel p = Page();
            Title(p, "Test it", 0);
            Para(p, "SoundDeck plays a short chime into the virtual mic and listens on the other end of the cable, " +
                    "the same place Discord listens.", Theme.S(44), Theme.S(44));
            btnTest = new FlatButton("Play test sound", Theme.IcPlay, ButtonStyle.Primary);
            btnTest.Location = new Point(0, Theme.S(100));
            btnTest.Click += (s, e) => RunTest();
            p.Controls.Add(btnTest);
            testCard = new StatusCard();
            testCard.SetBounds(0, Theme.S(150), cw, Theme.S(76));
            testCard.Visible = false;
            p.Controls.Add(testCard);

            tgListen = new Toggle("Hear what Discord hears (use headphones)");
            tgListen.SetBounds(0, Theme.S(246), cw, Theme.S(30));
            tgListen.CheckedChanged += (s, e) => SetListening(tgListen.Checked);
            p.Controls.Add(tgListen);
            Para(p, "Talk and play a sound: you'll hear exactly what your friends get. With speakers instead of headphones " +
                    "this can squeal, so turn it off again afterwards.", Theme.S(282), Theme.S(44)).ForeColor = Theme.Faint;
            return p;
        }

        Panel BuildDone()
        {
            Panel p = Page();
            Label icon = new Label();
            icon.Text = Theme.IcCheck;
            icon.Font = Theme.IconsLarge;
            icon.ForeColor = Theme.Good;
            icon.SetBounds(0, Theme.S(10), Theme.S(60), Theme.S(60));
            p.Controls.Add(icon);
            Title(p, "You're all set!", Theme.S(76));
            Para(p, "• Add sounds by dragging files onto the window, or record them right in SoundDeck.\n" +
                    "• Right-click a sound to give it a hotkey: hotkeys work while you're in a game.\n" +
                    "• Turn on Instant replay in the sidebar to save funny moments after they happen.\n" +
                    "• Keep SoundDeck running while you're in a call: your voice goes through it.", Theme.S(120), Theme.S(110));
            FlatButton add = new FlatButton("Add sounds", Theme.IcAdd, ButtonStyle.Primary);
            add.Location = new Point(0, Theme.S(246));
            add.Click += (s, e) => { FinishAction = "add"; DialogResult = DialogResult.OK; };
            FlatButton rec = new FlatButton("Record a sound", Theme.IcRecord, ButtonStyle.Normal);
            rec.Location = new Point(add.Right + Theme.S(8), Theme.S(246));
            rec.Click += (s, e) => { FinishAction = "record"; DialogResult = DialogResult.OK; };
            p.Controls.Add(add);
            p.Controls.Add(rec);
            return p;
        }

        // ================================================================ navigation

        void Next()
        {
            if (step == pages.Count - 1) { FinishAction = null; DialogResult = DialogResult.OK; return; }
            Go(step + 1);
        }

        void Go(int s)
        {
            if (s < 0 || s >= pages.Count) return;
            // leaving pages: stop anything that holds a device open
            if (step == 2 && s != 2) StopMeter();
            if (step == 4 && s != 4) tgListen.Checked = false;
            step = s;
            for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == step;
            if (step == 1) UpdateCable();
            if (step == 2) EnterMic();
            if (step == 3) UpdatePttButton();
            if (step == 4) testCard.Visible = false;
            btnBack.Visible = step > 0 && step < pages.Count - 1;
            btnSkip.Visible = step < pages.Count - 1;
            UpdateNext();
            rail.Invalidate();
        }

        void UpdateNext()
        {
            btnNext.Text = step == 0 ? "Let's go" : step == pages.Count - 1 ? "Finish" : step == 1 && !main.CableInstalled ? "Skip for now" : "Next";
            btnNext.FitWidth();
            int by = ClientSize.Height - Theme.S(60);
            btnNext.Location = new Point(ClientSize.Width - Theme.S(32) - btnNext.Width, by);
            btnBack.Location = new Point(btnNext.Left - Theme.S(8) - btnBack.Width, by);
        }

        void PaintRail(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = Theme.S(36);
            for (int i = 0; i < StepNames.Length; i++)
            {
                bool done = i < step, cur = i == step;
                int d = Theme.S(24);
                Rectangle dot = new Rectangle(Theme.S(26), y, d, d);
                using (SolidBrush b = new SolidBrush(cur ? Theme.Accent : done ? Color.FromArgb(60, Theme.Accent) : Theme.Card)) g.FillEllipse(b, dot);
                string mark = done ? Theme.IcCheck : (i + 1).ToString();
                TextRenderer.DrawText(g, mark, done ? Theme.IconsSmall : Theme.Small, dot, cur ? Color.White : done ? Theme.AccentHover : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, StepNames[i], cur ? Theme.Bold : Theme.Body, new Point(dot.Right + Theme.S(12), y + Theme.S(3)),
                    cur ? Theme.Text : done ? Theme.Muted : Theme.Faint, TextFormatFlags.NoPadding);
                if (i < StepNames.Length - 1)
                    using (Pen pen = new Pen(Theme.Border, 2)) g.DrawLine(pen, dot.X + d / 2, dot.Bottom + Theme.S(4), dot.X + d / 2, dot.Bottom + Theme.S(24));
                y += Theme.S(52);
            }
        }

        // ================================================================ virtual mic page

        void UpdateCable()
        {
            if (main.Installing)
            {
                cableCard.Set(Theme.Accent, Theme.IcDownload, "Installing…", main.InstallStep ?? "");
                btnInstall.Visible = false;
            }
            else if (main.CableInstalled)
            {
                main.UseCable();
                cableCard.Set(Theme.Good, Theme.IcCheck, "VB-CABLE is installed", "SoundDeck is sending to \"CABLE Input\". You're good to go.");
                btnInstall.Visible = cableHint.Visible = false;
            }
            else
            {
                cableCard.Set(Theme.Warn, Theme.IcWarning, "Not installed yet", "Click the button below. It's free and takes a minute.");
                btnInstall.Visible = cableHint.Visible = true;
            }
            UpdateNext();
        }

        // ================================================================ microphone page

        void EnterMic()
        {
            micBox.Items.Clear();
            foreach (object o in main.MicCombo.Items) micBox.Items.Add(o);
            micBox.SelectedIndex = Math.Max(0, main.MicCombo.SelectedIndex); // MicChanged opens the meter
            tgVoice.Checked = true;  // pushes through to the main window
            tgNoise.Checked = main.DenoiseToggle.Checked;
        }

        void MicChanged()
        {
            if (main.MicCombo.SelectedIndex != micBox.SelectedIndex) main.MicCombo.SelectedIndex = micBox.SelectedIndex;
            StopMeter();
            DeviceItem d = micBox.SelectedItem as DeviceItem;
            try
            {
                meterCap = new MicCapture(d == null ? -1 : d.Id);
                meterCap.OnData = (buf, n) =>
                {
                    int peak = 0;
                    for (int i = 0; i < n; i++) { int a = Math.Abs((int)buf[i]); if (a > peak) peak = a; }
                    meterLevel = Math.Max(peak / 32768f, meterLevel * 0.85f);
                };
            }
            catch { meterCap = null; }
        }

        void StopMeter()
        {
            if (meterCap != null) { meterCap.OnData = null; meterCap.Dispose(); meterCap = null; }
            meterLevel = 0;
        }

        void PaintMeter(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = meter.ClientRectangle;
            Theme.FillRound(g, Theme.Card, r, r.Height / 2f);
            float level = meterLevel;
            float db = level <= 0 ? -60f : (float)(20 * Math.Log10(level));
            float t = Math.Max(0f, Math.Min(1f, (db + 60f) / 60f));
            Color c = t > 0.92f ? Theme.Danger : t > 0.75f ? Theme.Warn : Theme.Good;
            if (t > 0) Theme.FillRound(g, c, new RectangleF(0, 0, Math.Max(r.Height, r.Width * t), r.Height), r.Height / 2f);
        }

        // ================================================================ discord page

        void UpdatePttButton()
        {
            Keys k = main.PushToTalkKey;
            btnPtt.Text = k == Keys.None ? "I use push-to-talk: set my key" : "Push-to-talk key: " + PushToTalk.Describe(k) + " (change)";
            btnPtt.FitWidth();
        }

        // ================================================================ test page

        /// <summary>
        /// Plays a chime into CABLE Input and records CABLE Output at the same time. If the chime's pitch shows up there,
        /// the whole chain works: SoundDeck → virtual cable → whatever app uses "CABLE Output" as its mic.
        /// </summary>
        void RunTest()
        {
            if (testing) return;
            OutputDevice o = main.VirtualOutput;
            int cableOut = WinMM.InputNames().FindIndex(n => n.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));
            if (o == null || !main.CableInstalled)
            {
                ShowResult(false, "SoundDeck isn't sending to the virtual mic", "Go back to \"Virtual mic\" and install VB-CABLE first.");
                return;
            }
            if (cableOut < 0)
            {
                ShowResult(false, "The other end of the cable is missing", "Windows hasn't finished setting up VB-CABLE. Restart your PC and try again.");
                return;
            }
            if (main.SoundVolume == 0)
            {
                ShowResult(false, "\"Volume others hear\" is at 0%", "Turn it up in the sidebar, then test again.");
                return;
            }

            testing = true;
            btnTest.Enabled = false;
            ShowResult(true, "Testing…", "Playing the chime and listening on the cable.");
            testCard.Set(Theme.Accent, Theme.IcPlay, "Testing…", "Playing the chime and listening on the cable.");
            List<float> heard = new List<float>();
            MicCapture cap;
            try
            {
                cap = new MicCapture(cableOut);
                cap.OnData = (buf, n) => { lock (heard) for (int i = 0; i < n; i += 2) heard.Add((buf[i] + buf[i + 1]) / 65536f); };
            }
            catch (Exception ex)
            {
                testing = false;
                btnTest.Enabled = true;
                ShowResult(false, "Couldn't listen on the cable", ex.Message);
                return;
            }
            object tag = new object();
            After(200, () => o.Play(Chime(), tag, 1f));
            After(1600, () =>
            {
                cap.OnData = null;
                cap.Dispose();
                float[] x;
                lock (heard) x = heard.ToArray();
                double tone = 0, total = 0;
                if (x.Length > 0)
                {
                    tone = Goertzel(x, 880);
                    foreach (float v in x) total += v * v;
                }
                bool ok = total > 1e-6 && tone / total > 0.15;
                if (ok) ShowResult(true, "It works! The chime came through the virtual mic", "Anything using \"CABLE Output\" as its microphone, like Discord, hears your sounds.");
                else if (total <= 1e-6) ShowResult(false, "Nothing came through the cable", "Check that \"Send sounds to\" in the sidebar is \"CABLE Input\". If you just installed VB-CABLE, restart your PC.");
                else ShowResult(false, "Something came through, but not the chime", "Another program might be using the cable. Close other audio tools and try again.");
                testing = false;
                btnTest.Enabled = true;
            });
        }

        void ShowResult(bool ok, string title, string sub)
        {
            testCard.Visible = true;
            testCard.Set(ok ? Theme.Good : Theme.Danger, ok ? Theme.IcCheck : Theme.IcWarning, title, sub);
        }

        /// <summary>A short two-note chime; its first note (880 Hz) is what the test listens for.</summary>
        static short[] Chime()
        {
            int n = (int)(WinMM.SampleRate * 1.0);
            short[] s = new short[n * 2];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / WinMM.SampleRate;
                double a = 0.35 * Math.Exp(-3 * t) * Math.Sin(2 * Math.PI * 880 * t);
                if (t > 0.25) a += 0.25 * Math.Exp(-4 * (t - 0.25)) * Math.Sin(2 * Math.PI * 1318.5 * t);
                double fade = Math.Min(1, i / 240.0); // no click at the start
                short v = (short)(a * fade * 32767);
                s[i * 2] = v;
                s[i * 2 + 1] = v;
            }
            return s;
        }

        /// <summary>Energy at one frequency (Goertzel), on the same scale as the sum of squares.</summary>
        static double Goertzel(float[] x, double freq)
        {
            double k = 2 * Math.Cos(2 * Math.PI * freq / WinMM.SampleRate), s1 = 0, s2 = 0;
            foreach (float v in x)
            {
                double s0 = v + k * s1 - s2;
                s2 = s1;
                s1 = s0;
            }
            double power = s1 * s1 + s2 * s2 - k * s1 * s2;
            return 2 * power / x.Length;
        }

        void SetListening(bool on)
        {
            if (listenOut != null) { listenOut.Dispose(); listenOut = null; }
            if (listenCap != null) { listenCap.Dispose(); listenCap = null; }
            if (!on) return;
            int cableOut = WinMM.InputNames().FindIndex(n => n.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase));
            if (cableOut < 0) { tgListen.Checked = false; ShowResult(false, "VB-CABLE isn't set up yet", "Install it first (step 2)."); return; }
            try
            {
                listenCap = new MicCapture(cableOut);
                listenOut = new OutputDevice(-1);
                listenOut.Volume = 0f;
                listenOut.Mic = listenCap;
            }
            catch (Exception ex)
            {
                tgListen.Checked = false;
                ShowResult(false, "Couldn't start listening", ex.Message);
            }
        }

        // ================================================================ misc

        void Tick()
        {
            if (step == 1) UpdateCable();
            if (step == 2) meter.Invalidate();
        }

        static void After(int ms, Action a)
        {
            Timer t = new Timer();
            t.Interval = ms;
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            StopMeter();
            SetListening(false);
            base.OnFormClosed(e);
        }
    }
}
