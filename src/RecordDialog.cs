using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// Shows a recording: live peaks while recording, then the finished sound with draggable trim handles.
    /// Works in stereo 48 kHz sample frames.
    /// </summary>
    class WaveformView : Control
    {
        short[] data;
        List<float> livePeaks;
        float[] columns = new float[0];
        int start, end;                 // trim range in frames
        float playhead = -1;            // frame position while previewing, or -1
        int dragging;                   // 0 none, 1 start handle, 2 end handle
        int hoverHandle;

        public event EventHandler TrimChanged;

        public WaveformView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
        }

        public int Frames { get { return data == null ? 0 : data.Length / 2; } }
        public int TrimStart { get { return start; } }
        public int TrimEnd { get { return end; } }

        public void ShowLive(List<float> peaks)
        {
            data = null;
            livePeaks = peaks;
            Invalidate();
        }

        public void ShowSound(short[] samples)
        {
            livePeaks = null;
            data = samples;
            start = 0;
            end = Frames;
            BuildColumns();
            Invalidate();
        }

        public void Clear()
        {
            data = null;
            livePeaks = null;
            columns = new float[0];
            Invalidate();
        }

        public void SetTrim(int s, int e)
        {
            start = Math.Max(0, Math.Min(s, Frames));
            end = Math.Max(start, Math.Min(e, Frames));
            Invalidate();
            if (TrimChanged != null) TrimChanged(this, EventArgs.Empty);
        }

        public void SetPlayhead(float frame)
        {
            playhead = frame;
            Invalidate();
        }

        Rectangle Area { get { return new Rectangle(Theme.S(10), Theme.S(10), Width - Theme.S(20), Height - Theme.S(20)); } }

        void BuildColumns()
        {
            Rectangle a = Area;
            int n = Math.Max(1, a.Width / Theme.S(3));
            columns = new float[n];
            if (Frames == 0) return;
            for (int c = 0; c < n; c++)
            {
                int f0 = (int)((long)Frames * c / n), f1 = (int)((long)Frames * (c + 1) / n);
                int peak = 0;
                for (int f = f0; f < f1; f++)
                {
                    int l = Math.Abs((int)data[f * 2]), r = Math.Abs((int)data[f * 2 + 1]);
                    if (l > peak) peak = l;
                    if (r > peak) peak = r;
                }
                columns[c] = peak / 32768f;
            }
        }

        protected override void OnResize(EventArgs e) { if (data != null) BuildColumns(); base.OnResize(e); }

        int FrameToX(long frame) { Rectangle a = Area; return a.X + (int)(a.Width * frame / Math.Max(1, Frames)); }
        int XToFrame(int x) { Rectangle a = Area; return (int)((long)Math.Max(0, Math.Min(a.Width, x - a.X)) * Frames / Math.Max(1, a.Width)); }

        int HandleAt(Point p)
        {
            if (data == null) return 0;
            int grab = Theme.S(10);
            int ds = Math.Abs(p.X - FrameToX(start)), de = Math.Abs(p.X - FrameToX(end));
            if (ds <= grab && ds <= de) return 1;
            if (de <= grab) return 2;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || data == null) return;
            dragging = HandleAt(e.Location);
            if (dragging == 0)
            {
                // clicking the waveform moves the nearest handle there
                int f = XToFrame(e.X);
                dragging = Math.Abs(f - start) < Math.Abs(f - end) ? 1 : 2;
                OnMouseMove(e);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging != 0)
            {
                int f = XToFrame(e.X);
                int minGap = WinMM.SampleRate / 20; // keep at least 50 ms
                if (dragging == 1) SetTrim(Math.Min(f, end - minGap), end);
                else SetTrim(start, Math.Max(f, start + minGap));
                return;
            }
            int h = HandleAt(e.Location);
            Cursor = h != 0 ? Cursors.SizeWE : data != null ? Cursors.Hand : Cursors.Default;
            if (h != hoverHandle) { hoverHandle = h; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = 0; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, Theme.Card, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10));
            Rectangle a = Area;
            int mid = a.Y + a.Height / 2;
            float half = a.Height / 2f - 2;
            int barW = Math.Max(1, Theme.S(2));

            if (livePeaks != null)
            {
                // newest on the right, scrolling left
                int n = a.Width / Theme.S(3);
                int first = Math.Max(0, livePeaks.Count - n);
                using (SolidBrush b = new SolidBrush(Theme.Danger))
                    for (int i = first; i < livePeaks.Count; i++)
                    {
                        int x = a.Right - (livePeaks.Count - i) * Theme.S(3);
                        float h = Math.Max(1f, livePeaks[i] * half);
                        g.FillRectangle(b, x, mid - h, barW, h * 2);
                    }
                return;
            }
            if (data == null)
            {
                using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, a.X, mid, a.Right, mid);
                return;
            }

            int sx = FrameToX(start), ex = FrameToX(end);
            using (SolidBrush inside = new SolidBrush(Theme.Accent))
            using (SolidBrush outside = new SolidBrush(Theme.Faint))
                for (int c = 0; c < columns.Length; c++)
                {
                    int x = a.X + (int)((long)a.Width * c / columns.Length);
                    float h = Math.Max(1f, columns[c] * half);
                    g.FillRectangle(x >= sx && x <= ex ? inside : outside, x, mid - h, barW, h * 2);
                }

            // dim the parts that will be cut off
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, Theme.Bg)))
            {
                g.FillRectangle(dim, a.X, a.Y, sx - a.X, a.Height);
                g.FillRectangle(dim, ex, a.Y, a.Right - ex, a.Height);
            }

            DrawHandle(g, sx, a, hoverHandle == 1 || dragging == 1);
            DrawHandle(g, ex, a, hoverHandle == 2 || dragging == 2);

            if (playhead >= 0)
                using (Pen p = new Pen(Color.White, 1.5f))
                {
                    int px = FrameToX((long)playhead);
                    g.DrawLine(p, px, a.Y, px, a.Bottom);
                }
        }

        void DrawHandle(Graphics g, int x, Rectangle a, bool hot)
        {
            Color c = hot ? Theme.AccentHover : Theme.Accent;
            using (Pen p = new Pen(c, Theme.S(2))) g.DrawLine(p, x, a.Y, x, a.Bottom);
            int w = Theme.S(10), h = Theme.S(22);
            Theme.FillRound(g, c, new RectangleF(x - w / 2f, a.Y + (a.Height - h) / 2f, w, h), Theme.S(3));
        }
    }

    /// <summary>Big round record / stop button.</summary>
    class RecordButton : DrawnControl
    {
        bool recording;
        public bool Recording { get { return recording; } set { recording = value; Invalidate(); } }

        public RecordButton()
        {
            Size = new Size(Theme.S(56), Theme.S(56));
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color ring = hover ? Theme.Mix(Theme.Danger, Color.White, 0.2f) : Theme.Danger;
            if (!Enabled) ring = Theme.Faint;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(40, ring))) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            using (Pen p = new Pen(ring, Theme.S(2))) g.DrawEllipse(p, Theme.S(2), Theme.S(2), Width - Theme.S(5), Height - Theme.S(5));
            int inner = Theme.S(recording ? 18 : 24);
            RectangleF r = new RectangleF((Width - inner) / 2f, (Height - inner) / 2f, inner, inner);
            if (recording) Theme.FillRound(g, Color.White, r, Theme.S(4));
            else using (SolidBrush b = new SolidBrush(ring)) g.FillEllipse(b, r);
        }
    }

    /// <summary>Record from the microphone or from the computer's own audio, trim, preview and save to the board.</summary>
    class RecordDialog : DarkDialog
    {
        enum State { Ready, Recording, Review }

        readonly int micDevice;
        readonly string micName;
        State state = State.Ready;
        bool fromComputer;
        Recording rec;
        MicCapture micCap;
        LoopbackCapture loopCap;
        readonly Stopwatch clock = new Stopwatch();
        short[] samples;
        OutputDevice preview;
        readonly object previewTag = new object();

        readonly FlatButton btnMic, btnPc, btnPreview, btnAutoTrim, btnAgain, btnSave, btnCancel;
        readonly Label lblSource, lblTime, lblHint, lblSel;
        readonly RecordButton btnRec;
        readonly WaveformView wave;
        readonly DarkInput nameInput;
        TextBox nameBox { get { return nameInput.Box; } }
        readonly DarkCombo catBox;
        readonly Panel meter;
        readonly Timer timer = new Timer();

        // results
        public short[] ResultSamples;
        public string ResultName, ResultCategory;

        public RecordDialog(int micDeviceId, string micDeviceName, List<string> categories, string defaultCategory) : base("Record a sound")
        {
            micDevice = micDeviceId;
            micName = micDeviceName;
            ClientSize = new Size(Theme.S(640), Theme.S(470));
            KeyPreview = true;
            int x = Theme.S(22), w = ClientSize.Width - Theme.S(44);

            // --- source
            Label src = SmallLabel("RECORD FROM", Theme.Faint);
            src.Font = Theme.SectionFont;
            src.SetBounds(x, Theme.S(16), w, Theme.S(18));
            btnMic = new FlatButton("Microphone", Theme.IcMic, ButtonStyle.Primary);
            btnPc = new FlatButton("Computer audio", Theme.IcVolume, ButtonStyle.Normal);
            btnMic.Location = new Point(x, Theme.S(38));
            btnPc.Location = new Point(btnMic.Right + Theme.S(8), Theme.S(38));
            btnMic.Click += (s, e) => SetSource(false);
            btnPc.Click += (s, e) => SetSource(true);
            lblSource = SmallLabel("", Theme.Muted);
            lblSource.SetBounds(btnPc.Right + Theme.S(14), Theme.S(38), ClientSize.Width - btnPc.Right - Theme.S(36), Theme.S(34));
            lblSource.TextAlign = ContentAlignment.MiddleLeft;

            // --- waveform
            wave = new WaveformView();
            wave.SetBounds(x, Theme.S(88), w, Theme.S(140));
            wave.TrimChanged += (s, e) => UpdateSelectionLabel();

            // --- record row
            btnRec = new RecordButton();
            btnRec.Location = new Point(x, Theme.S(242));
            btnRec.Click += (s, e) => { if (state == State.Recording) StopRecording(); else StartRecording(); };
            lblTime = new Label();
            lblTime.Font = new Font("Segoe UI Semibold", 18f);
            lblTime.ForeColor = Theme.Text;
            lblTime.SetBounds(btnRec.Right + Theme.S(14), Theme.S(242), Theme.S(130), Theme.S(34));
            lblTime.Text = "0:00.0";
            lblHint = SmallLabel("", Theme.Muted);
            lblHint.SetBounds(btnRec.Right + Theme.S(16), Theme.S(276), w - Theme.S(80), Theme.S(22));
            meter = new Panel();
            meter.SetBounds(lblTime.Right + Theme.S(10), Theme.S(254), w - (lblTime.Right - x) - Theme.S(10), Theme.S(10));
            meter.Paint += PaintMeter;
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(meter, true, null);

            btnPreview = new FlatButton("Preview", Theme.IcPlay, ButtonStyle.Normal);
            btnAutoTrim = new FlatButton("Trim silence", null, ButtonStyle.Ghost);
            btnPreview.Click += (s, e) => TogglePreview();
            btnAutoTrim.Click += (s, e) => AutoTrim();
            btnPreview.Location = new Point(lblTime.Right + Theme.S(10), Theme.S(246));
            btnAutoTrim.Location = new Point(btnPreview.Right + Theme.S(8), Theme.S(246));
            lblSel = SmallLabel("", Theme.Muted);
            lblSel.TextAlign = ContentAlignment.MiddleRight;
            lblSel.SetBounds(btnAutoTrim.Right + Theme.S(8), Theme.S(246), x + w - btnAutoTrim.Right - Theme.S(8), Theme.S(34));

            // --- name + category
            Label ln = SmallLabel("Name", Theme.Muted);
            ln.SetBounds(x, Theme.S(318), Theme.S(200), Theme.S(18));
            nameInput = new DarkInput();
            nameInput.Box.Text = "Recording " + DateTime.Now.ToString("yyyy-MM-dd HH.mm");
            nameInput.SetBounds(x, Theme.S(339), w * 3 / 5 - Theme.S(10), Theme.S(32));
            Label lc = SmallLabel("Category", Theme.Muted);
            lc.SetBounds(x + w * 3 / 5, Theme.S(318), Theme.S(200), Theme.S(18));
            catBox = new DarkCombo();
            catBox.SetBounds(x + w * 3 / 5, Theme.S(339), w * 2 / 5, Theme.S(30));
            catBox.Items.Add(new DeviceItem("No category", -1));
            foreach (string c in categories) catBox.Items.Add(new DeviceItem(c, 0));
            catBox.SelectedIndex = Math.Max(0, categories.IndexOf(defaultCategory) + 1);

            // --- bottom buttons
            btnSave = new FlatButton("Save to board", Theme.IcCheck, ButtonStyle.Primary);
            btnCancel = new FlatButton("Cancel", null, ButtonStyle.Normal);
            btnAgain = new FlatButton("Record again", Theme.IcRefresh, ButtonStyle.Ghost);
            int by = ClientSize.Height - Theme.S(56);
            btnSave.Location = new Point(x + w - btnSave.Width, by);
            btnCancel.Location = new Point(btnSave.Left - Theme.S(8) - btnCancel.Width, by);
            btnAgain.Location = new Point(x - Theme.S(6), by);
            btnSave.Click += (s, e) => Save();
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            btnAgain.Click += (s, e) => Reset();

            Controls.AddRange(new Control[] { src, btnMic, btnPc, lblSource, wave, btnRec, lblTime, lblHint, meter,
                btnPreview, btnAutoTrim, lblSel, ln, nameInput, lc, catBox, btnSave, btnCancel, btnAgain });

            timer.Interval = 50;
            timer.Tick += (s, e) => TimerTick();
            timer.Start();
            SetSource(false);
            UpdateState();
        }

        static Label SmallLabel(string text, Color color)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Small;
            l.ForeColor = color;
            return l;
        }

        void SetSource(bool computer)
        {
            if (state == State.Recording) return;
            fromComputer = computer;
            btnMic.Style = computer ? ButtonStyle.Normal : ButtonStyle.Primary;
            btnPc.Style = computer ? ButtonStyle.Primary : ButtonStyle.Normal;
            btnMic.Invalidate();
            btnPc.Invalidate();
            lblSource.Text = computer
                ? "Records everything playing on your PC — start recording, then play the clip."
                : "Uses " + micName + ".";
        }

        void UpdateState()
        {
            bool review = state == State.Review;
            btnRec.Recording = state == State.Recording;
            btnRec.Visible = !review;
            meter.Visible = state != State.Review;
            btnPreview.Visible = btnAutoTrim.Visible = lblSel.Visible = review;
            lblTime.Left = review ? wave.Left : btnRec.Right + Theme.S(14);
            btnPreview.Left = lblTime.Right + Theme.S(10);
            btnAutoTrim.Left = btnPreview.Right + Theme.S(8);
            btnSave.Enabled = review;
            btnAgain.Visible = review;
            btnMic.Enabled = btnPc.Enabled = state != State.Recording;
            if (state == State.Ready) lblHint.Text = "Press the red button or Space to start recording.";
            else if (state == State.Recording) lblHint.Text = "Recording… press the button or Space to stop (max " + Recording.MaxSeconds / 60 + " min).";
            else lblHint.Text = "Drag the purple handles to trim. Space plays a preview.";
            lblHint.Left = review ? wave.Left : btnRec.Right + Theme.S(16);
            lblHint.Top = review ? Theme.S(284) : Theme.S(276);
        }

        void StartRecording()
        {
            StopPreview();
            rec = new Recording();
            try
            {
                if (fromComputer) loopCap = new LoopbackCapture(rec);
                else
                {
                    micCap = new MicCapture(micDevice);
                    Recording r = rec;
                    micCap.OnData = (buf, n) => r.AppendShorts(buf, n);
                }
            }
            catch (Exception ex)
            {
                rec = null;
                MessageBox.Show(this, "Couldn't start recording: " + ex.Message, "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            clock.Restart();
            state = State.Recording;
            wave.ShowLive(new List<float>());
            UpdateState();
        }

        void StopRecording()
        {
            StopCapture();
            clock.Stop();
            samples = rec.ToStereo48k();
            if (samples.Length < WinMM.SampleRate / 5) // under 0.1 s of audio
            {
                state = State.Ready;
                wave.Clear();
                UpdateState();
                lblHint.Text = fromComputer
                    ? "Nothing was captured. Make sure the sound plays through your default speakers while recording."
                    : "Nothing was captured. Check that the right microphone is selected in the sidebar.";
                return;
            }
            state = State.Review;
            wave.ShowSound(samples);
            AutoTrim();
            UpdateState();
            UpdateSelectionLabel();
        }

        void StopCapture()
        {
            if (micCap != null) { micCap.OnData = null; micCap.Dispose(); micCap = null; }
            if (loopCap != null) { loopCap.Dispose(); loopCap = null; }
        }

        void Reset()
        {
            StopPreview();
            samples = null;
            state = State.Ready;
            wave.Clear();
            lblTime.Text = "0:00.0";
            UpdateState();
        }

        /// <summary>Cut silence off both ends (keeps a little air around the sound).</summary>
        void AutoTrim()
        {
            if (samples == null) return;
            int frames = samples.Length / 2, first = -1, last = -1;
            const int threshold = 650; // about -34 dBFS
            for (int f = 0; f < frames; f++)
                if (Math.Abs((int)samples[f * 2]) > threshold || Math.Abs((int)samples[f * 2 + 1]) > threshold) { first = f; break; }
            for (int f = frames - 1; f >= 0; f--)
                if (Math.Abs((int)samples[f * 2]) > threshold || Math.Abs((int)samples[f * 2 + 1]) > threshold) { last = f; break; }
            if (first < 0) { wave.SetTrim(0, frames); return; }
            wave.SetTrim(Math.Max(0, first - WinMM.SampleRate / 20), Math.Min(frames, last + WinMM.SampleRate / 6));
        }

        void UpdateSelectionLabel()
        {
            if (samples == null) return;
            double s = wave.TrimStart / (double)WinMM.SampleRate, e = wave.TrimEnd / (double)WinMM.SampleRate;
            lblTime.Text = FormatTime(e - s);
            lblSel.Text = FormatTime(s) + " – " + FormatTime(e);
        }

        static string FormatTime(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return (int)t.TotalMinutes + ":" + t.Seconds.ToString("00") + "." + t.Milliseconds / 100;
        }

        short[] Trimmed()
        {
            int s = wave.TrimStart, e = wave.TrimEnd;
            short[] r = new short[(e - s) * 2];
            Array.Copy(samples, s * 2, r, 0, r.Length);
            return r;
        }

        void TogglePreview()
        {
            if (preview != null && preview.Progress().ContainsKey(previewTag)) { StopPreview(); return; }
            try
            {
                if (preview == null) preview = new OutputDevice(-1); // the user's own speakers, never the virtual mic
                preview.StopAll();
                preview.Play(Trimmed(), previewTag, 1f);
                btnPreview.Text = "Stop";
                btnPreview.Icon = Theme.IcStop;
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't play the preview: " + ex.Message, "SoundDeck"); }
        }

        void StopPreview()
        {
            if (preview != null) preview.StopAll();
            btnPreview.Text = "Preview";
            btnPreview.Icon = Theme.IcPlay;
            wave.SetPlayhead(-1);
        }

        void TimerTick()
        {
            if (state == State.Recording)
            {
                lblTime.Text = FormatTime(clock.Elapsed.TotalSeconds);
                wave.ShowLive(rec.Peaks());
                meter.Invalidate();
                if (rec.Full) StopRecording();
            }
            else if (state == State.Review && preview != null)
            {
                float p;
                if (preview.Progress().TryGetValue(previewTag, out p)) wave.SetPlayhead(wave.TrimStart + p * (wave.TrimEnd - wave.TrimStart));
                else if (btnPreview.Text == "Stop") StopPreview();
            }
        }

        void PaintMeter(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = meter.ClientRectangle;
            Theme.FillRound(g, Theme.Card, r, r.Height / 2f);
            if (state != State.Recording || rec == null) return;
            // perceptual scale: -48 dB .. 0 dB
            float level = rec.Level;
            float db = level <= 0 ? -48f : (float)(20 * Math.Log10(level));
            float t = Math.Max(0f, Math.Min(1f, (db + 48f) / 48f));
            Color c = t > 0.92f ? Theme.Danger : t > 0.75f ? Theme.Warn : Theme.Good;
            Theme.FillRound(g, c, new RectangleF(0, 0, r.Width * t, r.Height), r.Height / 2f);
        }

        void Save()
        {
            if (samples == null) return;
            string name = nameBox.Text.Replace('\t', ' ').Trim();
            if (name.Length == 0) { nameBox.Focus(); return; }
            ResultSamples = Trimmed();
            ResultName = name;
            ResultCategory = catBox.SelectedIndex <= 0 ? "" : catBox.Text;
            DialogResult = DialogResult.OK;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space && !nameBox.Focused)
            {
                if (state == State.Review) TogglePreview();
                else if (state == State.Recording) StopRecording();
                else StartRecording();
                return true;
            }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter && state == State.Review) { Save(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            StopCapture();
            if (preview != null) { preview.Dispose(); preview = null; }
            base.OnFormClosed(e);
        }
    }
}
