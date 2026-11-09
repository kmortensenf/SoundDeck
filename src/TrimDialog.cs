using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// Trims any sound on the board without touching its file: the start and end are stored on the board.
    /// Also sets how the sound reacts to being pressed again, and whether it loops.
    /// </summary>
    class TrimDialog : DarkDialog
    {
        readonly short[] data;
        readonly WaveformView wave;
        readonly Label lblTime;
        readonly FlatButton btnPreview;
        readonly DarkCombo modeBox;
        readonly Toggle tgLoop;
        readonly Timer timer = new Timer();
        OutputDevice preview;
        readonly object previewTag = new object();
        readonly float gain;

        public int StartMs, EndMs;   // EndMs 0 = to the end
        public PlayMode Mode;
        public bool Loop;

        public TrimDialog(SoundEntry s, float playGain) : base("Edit \"" + s.Name + "\"")
        {
            data = s.Data;
            gain = playGain;
            ClientSize = new Size(Theme.S(640), Theme.S(400));
            KeyPreview = true;
            int x = Theme.S(22), w = ClientSize.Width - Theme.S(44);

            Label head = new Label();
            head.Text = "TRIM";
            head.Font = Theme.SectionFont;
            head.ForeColor = Theme.Faint;
            head.SetBounds(x, Theme.S(16), w, Theme.S(18));

            wave = new WaveformView();
            wave.SetBounds(x, Theme.S(38), w, Theme.S(140));
            wave.ShowSound(data);
            int frames = data.Length / 2;
            int sf = (int)((long)s.TrimStartMs * WinMM.SampleRate / 1000);
            int ef = s.TrimEndMs > 0 ? (int)((long)s.TrimEndMs * WinMM.SampleRate / 1000) : frames;
            wave.SetTrim(sf, ef);
            wave.TrimChanged += (o, e) => UpdateLabel();

            lblTime = new Label();
            lblTime.Font = new Font("Segoe UI Semibold", 16f);
            lblTime.ForeColor = Theme.Text;
            lblTime.SetBounds(x, Theme.S(192), Theme.S(120), Theme.S(32));

            btnPreview = new FlatButton("Preview", Theme.IcPlay, ButtonStyle.Normal);
            btnPreview.Location = new Point(x + Theme.S(130), Theme.S(192));
            btnPreview.Click += (o, e) => TogglePreview();
            FlatButton silence = new FlatButton("Trim silence", null, ButtonStyle.Ghost);
            silence.Location = new Point(btnPreview.Right + Theme.S(8), Theme.S(192));
            silence.Click += (o, e) => TrimSilence();
            FlatButton reset = new FlatButton("Use whole sound", null, ButtonStyle.Ghost);
            reset.Location = new Point(silence.Right + Theme.S(4), Theme.S(192));
            reset.Click += (o, e) => wave.SetTrim(0, frames);

            Label hint = new Label();
            hint.Text = "Drag the purple handles. The original file isn't changed.";
            hint.Font = Theme.Small;
            hint.ForeColor = Theme.Muted;
            hint.SetBounds(x, Theme.S(230), w, Theme.S(20));

            Label head2 = new Label();
            head2.Text = "WHEN PRESSED AGAIN WHILE PLAYING";
            head2.Font = Theme.SectionFont;
            head2.ForeColor = Theme.Faint;
            head2.SetBounds(x, Theme.S(266), w, Theme.S(18));
            modeBox = new DarkCombo();
            modeBox.Items.Add("Stop it");
            modeBox.Items.Add("Start it over");
            modeBox.Items.Add("Play another copy on top");
            modeBox.SelectedIndex = (int)s.Mode;
            modeBox.SetBounds(x, Theme.S(288), w / 2, Theme.S(30));
            tgLoop = new Toggle("Loop (repeat until stopped)");
            tgLoop.Checked = s.Loop;
            tgLoop.SetBounds(x + w / 2 + Theme.S(20), Theme.S(288), w / 2 - Theme.S(20), Theme.S(30));

            FlatButton save = new FlatButton("Save", Theme.IcCheck, ButtonStyle.Primary);
            FlatButton cancel = new FlatButton("Cancel", null, ButtonStyle.Normal);
            int by = ClientSize.Height - Theme.S(56);
            save.Location = new Point(x + w - save.Width, by);
            cancel.Location = new Point(save.Left - Theme.S(8) - cancel.Width, by);
            save.Click += (o, e) => Save();
            cancel.Click += (o, e) => DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { head, wave, lblTime, btnPreview, silence, reset, hint, head2, modeBox, tgLoop, save, cancel });
            timer.Interval = 50;
            timer.Tick += (o, e) => TickPreview();
            timer.Start();
            UpdateLabel();
        }

        void UpdateLabel()
        {
            lblTime.Text = PadGrid.FormatLength((wave.TrimEnd - wave.TrimStart) / (double)WinMM.SampleRate);
        }

        void TrimSilence()
        {
            int frames = data.Length / 2, first = -1, last = -1;
            const int threshold = 650;
            for (int f = 0; f < frames && first < 0; f++)
                if (Math.Abs((int)data[f * 2]) > threshold || Math.Abs((int)data[f * 2 + 1]) > threshold) first = f;
            for (int f = frames - 1; f >= 0 && last < 0; f--)
                if (Math.Abs((int)data[f * 2]) > threshold || Math.Abs((int)data[f * 2 + 1]) > threshold) last = f;
            if (first >= 0) wave.SetTrim(Math.Max(0, first - WinMM.SampleRate / 20), Math.Min(frames, last + WinMM.SampleRate / 6));
        }

        void TogglePreview()
        {
            if (preview != null && preview.Progress().ContainsKey(previewTag)) { StopPreview(); return; }
            try
            {
                if (preview == null) preview = new OutputDevice(-1); // your own speakers only
                preview.StopAll();
                preview.Play(data, previewTag, gain, wave.TrimStart * 2, wave.TrimEnd * 2, false);
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

        void TickPreview()
        {
            if (preview == null) return;
            float p;
            if (preview.Progress().TryGetValue(previewTag, out p)) wave.SetPlayhead(wave.TrimStart + p * (wave.TrimEnd - wave.TrimStart));
            else if (btnPreview.Text == "Stop") StopPreview();
        }

        void Save()
        {
            int frames = data.Length / 2;
            StartMs = (int)((long)wave.TrimStart * 1000 / WinMM.SampleRate);
            EndMs = wave.TrimEnd >= frames ? 0 : (int)((long)wave.TrimEnd * 1000 / WinMM.SampleRate);
            Mode = (PlayMode)modeBox.SelectedIndex;
            Loop = tgLoop.Checked;
            DialogResult = DialogResult.OK;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space) { TogglePreview(); return true; }
            if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
            if (keyData == Keys.Enter) { Save(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (preview != null) { preview.Dispose(); preview = null; }
            base.OnFormClosed(e);
        }
    }
}
