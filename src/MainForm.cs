using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SoundDeck
{
    class SoundEntry
    {
        public string Path;
        public string Name;
        public Keys Hotkey;
        public volatile short[] Data;
        public double Seconds = -1;
        public string Error;
        public string Category = "";   // "" = no category
        public bool Favorite;
        public int Volume = 100;       // per-sound volume in %, 0–200
        public float NormGain = 1f;    // loudness-matching gain, measured when the sound is loaded
        public int TrimStartMs, TrimEndMs; // trimmed part to play; TrimEndMs 0 = to the end
        public PlayMode Mode;
        public bool Loop;

        public SoundEntry Clone() { return (SoundEntry)MemberwiseClone(); }

        /// <summary>Length of the part that actually plays (after trimming), or -1 while not loaded yet.</summary>
        public double PlayedSeconds
        {
            get
            {
                if (Seconds < 0) return Seconds;
                double end = TrimEndMs > 0 ? Math.Min(Seconds, TrimEndMs / 1000.0) : Seconds;
                return Math.Max(0, end - TrimStartMs / 1000.0);
            }
        }
    }

    /// <summary>What pressing a sound's pad or hotkey does while it's already playing.</summary>
    enum PlayMode { Stop = 0, Restart = 1, Overlap = 2 }

    class MainForm : Form
    {
        static readonly string[] AudioExts = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".mp4", ".aiff", ".aif" };
        const int StopHotkeyId = 1, ReplayHotkeyId = 2, SoundHotkeyBase = 1000;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // SOUNDDECK_DATA lets a test copy run with its own board and settings
        readonly string dataDir = Environment.GetEnvironmentVariable("SOUNDDECK_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoundDeck");
        readonly List<SoundEntry> sounds = new List<SoundEntry>();
        readonly List<int> registeredIds = new List<int>();
        readonly BlockingCollection<SoundEntry> decodeQueue = new BlockingCollection<SoundEntry>();
        readonly Dictionary<string, string> settings = new Dictionary<string, string>();
        Keys stopKey = Keys.None, replayKey = Keys.None, pttKey = Keys.None;
        bool loading, installing;
        string installStep;

        // push-to-talk: held down while sounds play into the virtual mic
        bool pttHeld;
        DateTime pttLastActive;

        // instant replay, updates, tray, undo
        ReplayBuffer replay;
        Updater.Release pendingUpdate;
        readonly bool justUpdated, startHidden;
        NotifyIcon tray;
        bool exiting, trayHintShown;
        List<KeyValuePair<int, SoundEntry>> lastRemoved;
        readonly System.Windows.Forms.Timer deviceTimer = new System.Windows.Forms.Timer();
        string deviceSnapshot;

        OutputDevice virtualOut, monitorOut;
        MicCapture mic;
        Dictionary<object, float> playing = new Dictionary<object, float>();

        Panel header, sidebar, statusBar;
        PadGrid grid;
        CategoryBar catBar;
        readonly List<string> categories = new List<string>();
        string view = CategoryBar.AllKey; // which tab is shown: all, favorites or a category name
        ContextMenuStrip chipMenu;
        string chipTarget;
        SearchBox search;
        FlatButton btnAdd, btnRecord, btnStopAll, btnInstall, btnStopKey, btnRefresh, btnUpdate;
        FlatButton btnPtt, btnReplayKey, btnReplayNow, btnExport, btnCheckUpdates;
        StatusCard cableCard;
        DarkCombo cbVirtual, cbMic, cbMonitor;
        Toggle tgMic, tgDenoise, tgMonitor, tgStopPrev, tgNormalize, tgReplay, tgStartup, tgTray;
        FlatSlider slSound, slMic, slMonitor;
        LinkLabel credit;
        ContextMenuStrip padMenu, addMenu, bulkMenu;
        SoundEntry menuTarget;
        readonly List<Control> sideOrder = new List<Control>();
        readonly HashSet<Control> sideHidden = new HashSet<Control>();

        string flashText;
        Color flashColor;
        DateTime flashUntil;

        public MainForm(bool justUpdated, bool startHidden)
        {
            this.justUpdated = justUpdated;
            this.startHidden = startHidden;
            Text = "SoundDeck";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(Theme.S(1120), Theme.S(800));
            MinimumSize = new Size(Theme.S(820), Theme.S(600));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            KeyPreview = true;
            DoubleBuffered = true;

            BuildHeader();
            BuildSidebar();
            BuildStatusBar();
            BuildMenus();

            grid = new PadGrid();
            grid.Dock = DockStyle.Fill;
            grid.AllowDrop = true;
            grid.DragEnter += OnDragEnter;
            grid.DragDrop += OnDragDrop;
            grid.PadClicked += Trigger;
            grid.PadMenu += (s, p) =>
            {
                menuTarget = s;
                if (grid.Selected.Count > 1 && grid.Selected.Contains(s)) bulkMenu.Show(grid, p);
                else padMenu.Show(grid, p);
            };
            grid.SelectionChanged += () => statusBar.Invalidate();
            grid.EmptyClicked += () => addMenu.Show(grid, grid.PointToClient(Cursor.Position));
            grid.StarClicked += ToggleFavorite;
            grid.ColorOf = PadColor;
            grid.Reordered += MoveSound;
            grid.DragMoved += (s, screen) => catBar.SetDropHover(catBar.DropTargetAt(screen));
            grid.DroppedOutside += DropOnCategory;
            grid.DragEnded += () => catBar.SetDropHover(null);

            catBar = new CategoryBar();
            catBar.Dock = DockStyle.Top;
            catBar.ChipClicked += ShowView;
            catBar.ChipMenu += (c, p) => { chipTarget = c.Key; chipMenu.Show(catBar, p); };
            catBar.AddClicked += () => { string c = NewCategory(); if (c != null) ShowView(c); };
            catBar.CompactToggled += () =>
            {
                grid.Compact = catBar.Compact = !grid.Compact;
                SaveSettings();
            };

            Panel main = new Panel();
            main.Dock = DockStyle.Fill;
            main.Controls.Add(grid);
            main.Controls.Add(catBar);

            Controls.Add(main);
            Controls.Add(sidebar);
            Controls.Add(statusBar);
            Controls.Add(header);

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 40;
            timer.Tick += (s, e) => Tick();
            timer.Start();

            Thread worker = new Thread(DecodeWorker);
            worker.IsBackground = true;
            worker.Name = "SoundDeck decoder";
            worker.Start();

            // headsets plugged in or out: refresh the device lists once things settle
            deviceTimer.Interval = 1500;
            deviceTimer.Tick += (s, e) => { deviceTimer.Stop(); DevicesMaybeChanged(); };

            // if SoundDeck dies with the push-to-talk key held, let go of it
            AppDomain.CurrentDomain.ProcessExit += (s, e) => PttRelease();
            AppDomain.CurrentDomain.UnhandledException += (s, e) => PttRelease();
        }

        // ================================================================ layout

        void BuildHeader()
        {
            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.S(68);
            header.BackColor = Theme.Panel;
            header.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle logo = new Rectangle(Theme.S(20), (header.Height - Theme.S(36)) / 2, Theme.S(36), Theme.S(36));
                using (GraphicsPath p = Theme.Round(logo, Theme.S(10)))
                using (LinearGradientBrush b = new LinearGradientBrush(logo, Theme.Accent, Theme.Hex(0xEC4899), 45f))
                    g.FillPath(b, p);
                TextRenderer.DrawText(g, Theme.IcMusic, new Font(Theme.Icons.FontFamily, 14f), logo, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "SoundDeck", Theme.Title, new Point(logo.Right + Theme.S(12), logo.Y + Theme.S(4)), Theme.Text, TextFormatFlags.NoPadding);
                using (Pen pen = new Pen(Theme.Border)) g.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            search = new SearchBox();
            search.Box.TextChanged += (s, e) => RefreshGrid();
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    SoundEntry first = FilteredSounds().FirstOrDefault();
                    if (first != null) Play(first);
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    if (search.Box.Text.Length > 0) search.Box.Text = ""; else grid.Focus();
                    e.SuppressKeyPress = true;
                }
            };

            btnAdd = new FlatButton("Add sounds", Theme.IcAdd, ButtonStyle.Primary);
            btnAdd.Click += (s, e) => addMenu.Show(btnAdd, new Point(0, btnAdd.Height + Theme.S(4)));
            btnStopAll = new FlatButton("Stop all", Theme.IcStop, ButtonStyle.Danger);
            btnStopAll.Click += (s, e) => StopAll();
            btnRecord = new FlatButton("Record", Theme.IcRecord, ButtonStyle.Normal);
            btnRecord.Click += (s, e) => OpenRecorder();
            new ToolTip().SetToolTip(btnRecord, "Record from your microphone or from your PC's audio (Ctrl+R)");
            btnUpdate = new FlatButton("Update", Theme.IcDownload, ButtonStyle.Primary);
            btnUpdate.Visible = false;
            btnUpdate.Click += (s, e) => InstallUpdate();

            header.Controls.Add(search);
            header.Controls.Add(btnUpdate);
            header.Controls.Add(btnRecord);
            header.Controls.Add(btnAdd);
            header.Controls.Add(btnStopAll);
            header.Resize += (s, e) => LayoutHeader();
        }

        void LayoutHeader()
        {
            int cy = (header.Height - btnAdd.Height) / 2;
            int x = header.Width - sideWidth - Theme.S(18);
            btnStopAll.Location = new Point(header.Width - Theme.S(18) - btnStopAll.Width, cy);
            btnAdd.Location = new Point(x - btnAdd.Width, cy);
            btnRecord.Location = new Point(btnAdd.Left - Theme.S(8) - btnRecord.Width, cy);
            int left = btnRecord.Left;
            if (btnUpdate.Visible)
            {
                btnUpdate.Location = new Point(btnRecord.Left - Theme.S(8) - btnUpdate.Width, cy);
                left = btnUpdate.Left;
            }
            int sw = Math.Min(Theme.S(340), left - Theme.S(16) - Theme.S(220));
            search.SetBounds(left - Theme.S(12) - Math.Max(Theme.S(120), sw), cy, Math.Max(Theme.S(120), sw), Theme.S(34));
        }

        int sideWidth { get { return Theme.S(320); } }

        void BuildSidebar()
        {
            sidebar = new Panel();
            sidebar.Dock = DockStyle.Right;
            sidebar.Width = sideWidth;
            sidebar.BackColor = Theme.Panel;
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(sidebar, true, null);
            sidebar.Paint += (s, e) =>
            {
                using (Pen p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, 0, sidebar.Height);
                if (sideContent <= sidebar.Height) return;
                // thin dark scroll indicator instead of the light Windows scrollbar
                int th = Math.Max(Theme.S(30), sidebar.Height * sidebar.Height / sideContent);
                int ty = sideScroll * (sidebar.Height - th) / (sideContent - sidebar.Height);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(e.Graphics, Theme.Border, new Rectangle(sidebar.Width - Theme.S(7), ty + Theme.S(2), Theme.S(4), th - Theme.S(4)), Theme.S(2));
            };
            sidebar.Resize += (s, e) => LayoutSidebar();
            sidebar.MouseWheel += SidebarWheel;
            sidebar.MouseEnter += (s, e) => sidebar.Focus();

            // --- virtual mic
            AddSide(Section("VIRTUAL MICROPHONE"), Theme.S(4));
            cableCard = new StatusCard();
            AddSide(cableCard, Theme.S(8));
            btnInstall = new FlatButton("Install virtual mic", Theme.IcDownload, ButtonStyle.Primary);
            btnInstall.Click += (s, e) => InstallCable();
            AddSide(btnInstall, Theme.S(10));
            credit = new LinkLabel();
            credit.Text = "Virtual mic powered by VB-CABLE, donationware by VB-Audio · vb-cable.com";
            credit.LinkArea = new LinkArea(credit.Text.IndexOf("vb-cable.com"), "vb-cable.com".Length);
            credit.Font = Theme.Small;
            credit.ForeColor = Theme.Faint;
            credit.LinkColor = Theme.Muted;
            credit.ActiveLinkColor = Theme.Accent;
            credit.Height = Theme.S(32);
            credit.LinkClicked += (s, e) => { try { Process.Start(CableInstaller.HomeUrl); } catch { } };
            AddSide(credit, Theme.S(6));
            Label sendTo = Field("Send sounds to");
            btnRefresh = new FlatButton("", Theme.IcRefresh, ButtonStyle.Ghost);
            btnRefresh.Size = new Size(Theme.S(26), Theme.S(22));
            btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRefresh.Click += (s, e) => { LoadDevices(); OpenAudio(); Flash("Audio devices refreshed.", 3, Theme.Good); };
            new ToolTip().SetToolTip(btnRefresh, "Refresh audio devices");
            sendTo.Height = Theme.S(24);
            sendTo.TextAlign = ContentAlignment.MiddleLeft;
            sendTo.Controls.Add(btnRefresh);
            sendTo.Resize += (s, e) => btnRefresh.Location = new Point(sendTo.Width - btnRefresh.Width, 1);
            AddSide(sendTo, Theme.S(4));
            cbVirtual = new DarkCombo();
            AddSide(cbVirtual, Theme.S(4));

            // --- voice
            AddSide(Section("YOUR VOICE"), Theme.S(20));
            tgMic = new Toggle("Mix my microphone in");
            AddSide(tgMic, Theme.S(4));
            cbMic = new DarkCombo();
            AddSide(cbMic, Theme.S(4));
            tgDenoise = new Toggle("Remove background noise");
            AddSide(tgDenoise, Theme.S(4));
            slMic = new FlatSlider("Voice volume", Theme.IcMic);
            AddSide(slMic, Theme.S(6));

            // --- sounds
            AddSide(Section("SOUNDS"), Theme.S(18));
            slSound = new FlatSlider("Volume others hear", Theme.IcVolume);
            AddSide(slSound, Theme.S(2));
            tgNormalize = new Toggle("Even out loudness");
            AddSide(tgNormalize, Theme.S(6));
            tgMonitor = new Toggle("Let me hear sounds too");
            AddSide(tgMonitor, Theme.S(8));
            cbMonitor = new DarkCombo();
            AddSide(cbMonitor, Theme.S(4));
            slMonitor = new FlatSlider("My volume", Theme.IcHeadphones);
            AddSide(slMonitor, Theme.S(6));

            // --- playback
            AddSide(Section("PLAYBACK"), Theme.S(18));
            tgStopPrev = new Toggle("Stop previous sound on play");
            AddSide(tgStopPrev, Theme.S(4));
            btnStopKey = new FlatButton("", Theme.IcKeyboard, ButtonStyle.Normal);
            btnStopKey.Click += (s, e) => SetStopHotkey();
            AddSide(btnStopKey, Theme.S(8));
            btnPtt = new FlatButton("", Theme.IcMic, ButtonStyle.Normal);
            btnPtt.Click += (s, e) => SetPttKey();
            AddSide(btnPtt, Theme.S(8));

            // --- instant replay
            AddSide(Section("INSTANT REPLAY"), Theme.S(22));
            tgReplay = new Toggle("Keep the last 30 s of PC audio");
            AddSide(tgReplay, Theme.S(4));
            btnReplayKey = new FlatButton("", Theme.IcKeyboard, ButtonStyle.Normal);
            btnReplayKey.Click += (s, e) => SetReplayHotkey();
            AddSide(btnReplayKey, Theme.S(6));
            btnReplayNow = new FlatButton("Save replay now", Theme.IcRecord, ButtonStyle.Ghost);
            btnReplayNow.Click += (s, e) => SaveReplay();
            AddSide(btnReplayNow, Theme.S(4));

            // --- app
            AddSide(Section("APP"), Theme.S(18));
            tgStartup = new Toggle("Start with Windows");
            AddSide(tgStartup, Theme.S(4));
            tgTray = new Toggle("Keep running in the tray when closed");
            AddSide(tgTray, Theme.S(4));
            btnExport = new FlatButton("Export board to share…", Theme.IcFolder, ButtonStyle.Normal);
            btnExport.Click += (s, e) => ExportBoard();
            AddSide(btnExport, Theme.S(8));
            btnCheckUpdates = new FlatButton("Version " + Updater.Short(Updater.Current) + "  ·  Check for updates", Theme.IcRefresh, ButtonStyle.Ghost);
            btnCheckUpdates.Click += (s, e) => CheckForUpdates(true);
            AddSide(btnCheckUpdates, Theme.S(6));
            Panel spacer = new Panel();
            spacer.Height = Theme.S(14);
            AddSide(spacer, 0);

            ToolTip tips = new ToolTip();
            tips.SetToolTip(tgNormalize, "Measures how loud each sound is and evens them out, so no clip is much louder than the rest.");
            tips.SetToolTip(btnPtt, "If you use push-to-talk in Discord, SoundDeck holds your key down while a sound plays.");
            tips.SetToolTip(tgReplay, "Keeps the last 30 seconds of what's playing on your PC in memory (never on disk), so you can save a funny moment after it happened.");
            tips.SetToolTip(tgTray, "Closing the window keeps SoundDeck running next to the clock, so hotkeys and your mic keep working.");
            tgNormalize.CheckedChanged += (s, e) => { if (!loading) SaveSettings(); };
            tips.SetToolTip(tgDenoise, "Cleans up your microphone (fan, hum, background noise) before sounds are mixed in. " +
                "Use this instead of Discord's Krisp noise suppression, which also removes your sound effects.");
            tgDenoise.CheckedChanged += (s, e) =>
            {
                if (mic != null) mic.Suppressor = tgDenoise.Checked ? new NoiseSuppressor() : null;
                if (!loading) SaveSettings();
            };
            tgReplay.CheckedChanged += (s, e) => { if (!loading) { UpdateReplay(); SaveSettings(); } };
            tgStartup.CheckedChanged += (s, e) => { if (!loading) SetStartWithWindows(tgStartup.Checked); };
            tgTray.CheckedChanged += (s, e) => { if (!loading) { UpdateTray(); SaveSettings(); } };

            EventHandler deviceChanged = (s, e) => { if (!loading) { OpenAudio(); SaveSettings(); } };
            cbVirtual.SelectedIndexChanged += deviceChanged;
            cbMic.SelectedIndexChanged += deviceChanged;
            cbMonitor.SelectedIndexChanged += deviceChanged;
            tgMic.CheckedChanged += deviceChanged;
            tgMonitor.CheckedChanged += deviceChanged;
            tgStopPrev.CheckedChanged += (s, e) => { if (!loading) SaveSettings(); };
            EventHandler volChanged = (s, e) => { ApplyVolumes(); if (!loading) SaveSettings(); };
            slSound.ValueChanged += volChanged;
            slMic.ValueChanged += volChanged;
            slMonitor.ValueChanged += volChanged;
        }

        void AddSide(Control c, int gapAbove)
        {
            c.Tag = gapAbove;
            sidebar.Controls.Add(c);
            sideOrder.Add(c);
            if (!(c is FlatSlider) && !(c is ComboBox)) c.MouseWheel += SidebarWheel; // sliders and lists use the wheel themselves
        }

        int sideScroll, sideContent;

        void SidebarWheel(object sender, MouseEventArgs e)
        {
            sideScroll -= e.Delta * Theme.S(60) / 120;
            LayoutSidebar();
            sidebar.Invalidate();
        }

        void SetSideVisible(Control c, bool visible)
        {
            if (visible) sideHidden.Remove(c); else sideHidden.Add(c);
            c.Visible = visible;
            LayoutSidebar();
        }

        void LayoutSidebar()
        {
            if (sidebar == null) return;
            int total = Theme.S(18);
            foreach (Control c in sideOrder)
                if (!sideHidden.Contains(c)) total += (int)c.Tag + c.Height;
            sideContent = total;
            sideScroll = Math.Max(0, Math.Min(sideScroll, total - sidebar.Height));

            int y = Theme.S(18) - sideScroll;
            int x = Theme.S(20);
            int w = sidebar.ClientSize.Width - x * 2;
            sidebar.SuspendLayout();
            foreach (Control c in sideOrder)
            {
                if (sideHidden.Contains(c)) continue;
                y += (int)c.Tag;
                c.SetBounds(x, y, w, c.Height);
                y += c.Height;
            }
            sidebar.ResumeLayout();
            sidebar.Invalidate();
        }

        static Label Section(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.SectionFont;
            l.ForeColor = Theme.Faint;
            l.Height = Theme.S(18);
            return l;
        }

        static Label Field(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.Small;
            l.ForeColor = Theme.Muted;
            l.Height = Theme.S(20);
            return l;
        }

        void BuildStatusBar()
        {
            statusBar = new Panel();
            statusBar.Dock = DockStyle.Bottom;
            statusBar.Height = Theme.S(34);
            statusBar.BackColor = Theme.Panel;
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(statusBar, true, null);
            statusBar.Paint += (s, e) => PaintStatus(e.Graphics);
        }

        void PaintStatus(Graphics g)
        {
            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, 0, 0, statusBar.Width, 0);
            int h = statusBar.Height;
            string glyph, text;
            Color color;
            if (flashText != null && DateTime.Now < flashUntil)
            {
                glyph = flashColor == Theme.Danger || flashColor == Theme.Warn ? Theme.IcWarning : Theme.IcCheck;
                text = flashText;
                color = flashColor;
            }
            else if (grid.Selected.Count > 0)
            {
                glyph = Theme.IcCheck;
                color = Theme.AccentHover;
                text = grid.Selected.Count + " selected  ·  right-click for actions  ·  Delete removes  ·  Esc clears the selection";
            }
            else if (playing.Count > 0)
            {
                glyph = Theme.IcPlay;
                color = Theme.Accent;
                text = "Now playing: " + string.Join(", ", playing.Keys.Cast<SoundEntry>().Select(x => x.Name));
            }
            else
            {
                glyph = Theme.IcKeyboard;
                color = Theme.Faint;
                text = sounds.Count == 0 ? "Tip: drop audio files anywhere in this window."
                    : "Click to play · drag to reorder or onto a tab · right-click for hotkey, volume and more · Esc stops everything";
            }
            TextRenderer.DrawText(g, glyph, Theme.Icons, new Rectangle(Theme.S(16), 0, Theme.S(20), h), color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(Theme.S(42), 0, statusBar.Width - Theme.S(200), h),
                color == Theme.Faint ? Theme.Muted : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string right = sounds.Count + (sounds.Count == 1 ? " sound" : " sounds");
            TextRenderer.DrawText(g, right, Theme.Small, new Rectangle(0, 0, statusBar.Width - Theme.S(16), h), Theme.Faint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        void BuildMenus()
        {
            addMenu = DarkMenu();
            addMenu.Items.Add("Add files…", null, (s, e) => AddFilesDialog());
            addMenu.Items.Add("Add a whole folder…", null, (s, e) => AddFolderDialog());
            addMenu.Items.Add(new ToolStripSeparator());
            addMenu.Items.Add("Record a sound…   Ctrl+R", null, (s, e) => OpenRecorder());
            addMenu.Items.Add("Import a shared board…", null, (s, e) => ImportBoard());

            bulkMenu = DarkMenu();
            bulkMenu.Items.Add("-");
            bulkMenu.Opening += (s, e) => BuildBulkMenu();

            // The pad menu is rebuilt every time it opens, so it can show the sound's current state.
            padMenu = DarkMenu();
            padMenu.Items.Add("-"); // ContextMenuStrip won't open while empty
            padMenu.Opening += (s, e) => BuildPadMenu();

            chipMenu = DarkMenu();
            chipMenu.Items.Add("Rename category…", null, (s, e) => RenameCategory(chipTarget));
            chipMenu.Items.Add("Move left", null, (s, e) => MoveCategory(chipTarget, -1));
            chipMenu.Items.Add("Move right", null, (s, e) => MoveCategory(chipTarget, 1));
            chipMenu.Items.Add("Sort sounds A–Z", null, (s, e) => SortCategory(chipTarget));
            chipMenu.Items.Add(new ToolStripSeparator());
            chipMenu.Items.Add("Delete category (keeps the sounds)", null, (s, e) => DeleteCategory(chipTarget));
        }

        void BuildPadMenu()
        {
            padMenu.Items.Clear();
            SoundEntry t = menuTarget;
            if (t == null) return;
            padMenu.Items.Add(playing.ContainsKey(t) ? "Stop" : "Play", null, (s, e) => { if (playing.ContainsKey(t)) StopSound(t); else Play(t); });
            if (t.Error != null) padMenu.Items.Add("Locate the file…", null, (s, e) => Relink(t));
            padMenu.Items.Add(new ToolStripSeparator());
            padMenu.Items.Add("Trim, loop and play mode…", null, (s, e) => EditSound(t)).Enabled = t.Error == null;
            padMenu.Items.Add(t.Hotkey == Keys.None ? "Set hotkey…" : "Change hotkey (" + PadGrid.HotkeyText(t.Hotkey) + ")…", null, (s, e) => SetHotkey(t));
            if (t.Hotkey != Keys.None)
                padMenu.Items.Add("Clear hotkey", null, (s, e) =>
                {
                    t.Hotkey = Keys.None;
                    grid.RedrawPads();
                    RegisterHotkeys();
                    SaveSounds();
                });
            padMenu.Items.Add("Volume… (" + t.Volume + "%)", null, (s, e) => EditVolume(t));
            padMenu.Items.Add(t.Favorite ? "Remove from favorites" : "Add to favorites", null, (s, e) => ToggleFavorite(t));

            ToolStripMenuItem move = new ToolStripMenuItem("Move to category");
            ToolStripDropDownMenu sub = (ToolStripDropDownMenu)move.DropDown;
            sub.Renderer = new DarkMenuRenderer();
            sub.ShowImageMargin = false;
            sub.ShowCheckMargin = true;
            foreach (string c in categories)
            {
                string cat = c;
                ToolStripMenuItem item = new ToolStripMenuItem(cat, null, (s, e) => SetCategory(t, cat));
                item.Checked = t.Category == cat;
                sub.Items.Add(item);
            }
            ToolStripMenuItem none = new ToolStripMenuItem("No category", null, (s, e) => SetCategory(t, ""));
            none.Checked = t.Category == "";
            if (categories.Count > 0) sub.Items.Add(new ToolStripSeparator());
            sub.Items.Add(none);
            sub.Items.Add("New category…", null, (s, e) => { string c = NewCategory(); if (c != null) SetCategory(t, c); });
            padMenu.Items.Add(move);

            padMenu.Items.Add(new ToolStripSeparator());
            padMenu.Items.Add("Rename…", null, (s, e) => Rename(t));
            padMenu.Items.Add("Show in folder", null, (s, e) => Process.Start("explorer.exe", "/select,\"" + t.Path + "\""));
            padMenu.Items.Add(new ToolStripSeparator());
            padMenu.Items.Add("Remove from board", null, (s, e) => Remove(new List<SoundEntry> { t }));
        }

        /// <summary>Menu for right-clicking a pad that's part of a multi-selection.</summary>
        void BuildBulkMenu()
        {
            bulkMenu.Items.Clear();
            List<SoundEntry> sel = SelectedInOrder();
            if (sel.Count == 0) return;
            ToolStripItem title = bulkMenu.Items.Add(sel.Count + " sounds selected");
            title.Enabled = false;
            bulkMenu.Items.Add(new ToolStripSeparator());
            bulkMenu.Items.Add("Add to favorites", null, (s, e) => { foreach (SoundEntry x in sel) x.Favorite = true; AfterBulk(); });
            bulkMenu.Items.Add("Remove from favorites", null, (s, e) => { foreach (SoundEntry x in sel) x.Favorite = false; AfterBulk(); });

            ToolStripMenuItem move = new ToolStripMenuItem("Move to category");
            ToolStripDropDownMenu sub = (ToolStripDropDownMenu)move.DropDown;
            sub.Renderer = new DarkMenuRenderer();
            sub.ShowImageMargin = false;
            foreach (string c in categories)
            {
                string cat = c;
                sub.Items.Add(cat, null, (s, e) => { foreach (SoundEntry x in sel) x.Category = cat; AfterBulk(); });
            }
            if (categories.Count > 0) sub.Items.Add(new ToolStripSeparator());
            sub.Items.Add("No category", null, (s, e) => { foreach (SoundEntry x in sel) x.Category = ""; AfterBulk(); });
            sub.Items.Add("New category…", null, (s, e) =>
            {
                string c = NewCategory();
                if (c == null) return;
                foreach (SoundEntry x in sel) x.Category = c;
                AfterBulk();
            });
            bulkMenu.Items.Add(move);
            bulkMenu.Items.Add("Reset volume to 100%", null, (s, e) => { foreach (SoundEntry x in sel) x.Volume = 100; AfterBulk(); });
            bulkMenu.Items.Add(new ToolStripSeparator());
            bulkMenu.Items.Add("Clear selection", null, (s, e) => grid.ClearSelection());
            bulkMenu.Items.Add("Remove " + sel.Count + " sounds from board", null, (s, e) => Remove(sel));
        }

        List<SoundEntry> SelectedInOrder() { return sounds.Where(s => grid.Selected.Contains(s)).ToList(); }

        void AfterBulk()
        {
            RefreshGrid();
            SaveSounds();
        }

        static ContextMenuStrip DarkMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.Font = Theme.Body;
            m.ShowImageMargin = false;
            return m;
        }

        // ================================================================ lifecycle

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            loading = true;
            LoadSettings();
            LoadSounds();
            slSound.Value = GetInt("soundVolume", 75);
            slMic.Value = GetInt("micVolume", 100);
            slMonitor.Value = GetInt("monitorVolume", 50);
            tgMic.Checked = GetInt("micPassthrough", 1) == 1;
            tgMonitor.Checked = GetInt("monitor", 1) == 1;
            tgStopPrev.Checked = GetInt("stopPrevious", 0) == 1;
            stopKey = (Keys)GetInt("stopHotkey", 0);
            replayKey = (Keys)GetInt("replayHotkey", 0);
            pttKey = (Keys)GetInt("pushToTalkKey", 0);
            tgNormalize.Checked = GetInt("normalize", 1) == 1;
            tgDenoise.Checked = GetInt("denoise", 1) == 1;
            tgReplay.Checked = GetInt("instantReplay", 0) == 1;
            tgTray.Checked = GetInt("tray", 0) == 1;
            tgStartup.Checked = StartsWithWindows();
            grid.Compact = catBar.Compact = GetInt("compact", 0) == 1;
            view = Get("view", CategoryBar.AllKey);
            if (view != CategoryBar.AllKey && view != CategoryBar.FavoritesKey && !categories.Contains(view)) view = CategoryBar.AllKey;
            LoadDevices();
            loading = false;

            OpenAudio();
            UpdateStopKeyButton();
            UpdatePttButton();
            UpdateReplayButton();
            UpdateReplay();
            UpdateTray();
            if (tgStartup.Checked) SetStartWithWindows(true); // keeps the path right if the exe was moved
            RefreshGrid();
            RegisterHotkeys();
            LayoutSidebar();
            deviceSnapshot = DeviceSignature();
            foreach (SoundEntry s in sounds) decodeQueue.Add(s);

            if (justUpdated) Flash("Updated to SoundDeck " + Updater.Short(Updater.Current) + ". Enjoy!", 8, Theme.Good);
            // look for a new version shortly after starting, quietly
            System.Windows.Forms.Timer later = new System.Windows.Forms.Timer();
            later.Interval = 4000;
            later.Tick += (s, ev) => { later.Stop(); later.Dispose(); CheckForUpdates(false); };
            later.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!startHidden) return;
            if (tgTray.Checked) Hide();                      // started with Windows: live in the tray
            else WindowState = FormWindowState.Minimized;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // with the tray option on, closing the window only hides it
            if (tgTray.Checked && !exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(4000, "SoundDeck is still running",
                        "Hotkeys and your mic keep working. Right-click the icon here to quit.", ToolTipIcon.Info);
                }
                return;
            }
            PttRelease();
            SaveSettings();
            SaveSounds();
            UnregisterHotkeys();
            CloseAudio();
            if (replay != null) { replay.Dispose(); replay = null; }
            if (tray != null) tray.Visible = false;
            decodeQueue.CompleteAdding();
            base.OnFormClosing(e);
        }

        /// <summary>Really quit (from the tray menu or the updater), even with "keep running in the tray" on.</summary>
        void ExitApp()
        {
            exiting = true;
            Close();
        }

        void ShowFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { search.Box.Focus(); search.Box.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.R)) { OpenRecorder(); return true; }
            if (keyData == Keys.Escape && grid.IsDragging) { grid.CancelDrag(); return true; }
            if (keyData == Keys.Escape && grid.Selected.Count > 0) { grid.ClearSelection(); return true; }
            if (keyData == Keys.Escape && !search.Box.Focused) { StopAll(); return true; }
            if (keyData == (Keys.Control | Keys.Z) && !search.Box.Focused) { UndoRemove(); return true; }
            if (keyData == (Keys.Control | Keys.A) && !search.Box.Focused) { grid.SelectAll(); return true; }
            if (keyData == Keys.Delete && !search.Box.Focused && grid.Selected.Count > 0) { Remove(SelectedInOrder()); return true; }
            // Ctrl+1..9 switch tabs
            Keys code = keyData & Keys.KeyCode;
            if ((keyData & Keys.Modifiers) == Keys.Control && code >= Keys.D1 && code <= Keys.D9)
            {
                int i = code - Keys.D1;
                if (i < catBar.Chips.Count) ShowView(catBar.Chips[i].Key);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) // WM_HOTKEY
            {
                int id = m.WParam.ToInt32();
                if (id == StopHotkeyId) StopAll();
                else if (id == ReplayHotkeyId) SaveReplay();
                else if (id >= SoundHotkeyBase && id - SoundHotkeyBase < sounds.Count) Trigger(sounds[id - SoundHotkeyBase]);
                return;
            }
            if (m.Msg == 0x0219 && m.WParam.ToInt32() == 0x0007) // WM_DEVICECHANGE / DBT_DEVNODES_CHANGED
            {
                deviceTimer.Stop();
                deviceTimer.Start();
            }
            base.WndProc(ref m);
        }

        void Tick()
        {
            Dictionary<object, float> p = new Dictionary<object, float>();
            foreach (OutputDevice o in new[] { virtualOut, monitorOut })
            {
                if (o == null) continue;
                foreach (KeyValuePair<object, float> kv in o.Progress())
                {
                    float old;
                    if (!p.TryGetValue(kv.Key, out old) || kv.Value > old) p[kv.Key] = kv.Value;
                }
            }
            bool changed = p.Count > 0 || playing.Count > 0;
            playing = p;

            // let go of push-to-talk shortly after the last sound into the virtual mic ends
            if (pttHeld)
            {
                bool active = virtualOut != null && virtualOut.Progress().Count > 0;
                if (active) pttLastActive = DateTime.Now;
                else if ((DateTime.Now - pttLastActive).TotalMilliseconds > 300) PttRelease();
            }
            grid.SetProgress(p);
            if (changed || flashText != null) statusBar.Invalidate();
            if (flashText != null && DateTime.Now >= flashUntil) { flashText = null; statusBar.Invalidate(); }
        }

        // ================================================================ audio

        void LoadDevices()
        {
            bool wasLoading = loading;
            loading = true;
            List<string> outs = WinMM.OutputNames();
            List<string> ins = WinMM.InputNames();

            cbVirtual.Items.Clear();
            cbVirtual.Items.Add(new DeviceItem("(None – speakers only)", -2));
            cbMonitor.Items.Clear();
            cbMonitor.Items.Add(new DeviceItem("Default playback device", -1));
            for (int i = 0; i < outs.Count; i++)
            {
                cbVirtual.Items.Add(new DeviceItem(outs[i], i));
                cbMonitor.Items.Add(new DeviceItem(outs[i], i));
            }
            cbMic.Items.Clear();
            cbMic.Items.Add(new DeviceItem("Default microphone", -1));
            for (int i = 0; i < ins.Count; i++)
                if (!ins[i].StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)) // never feed the cable back into itself
                    cbMic.Items.Add(new DeviceItem(ins[i], i));

            string cable = CableName(outs);
            string savedVirtual;
            if (!settings.TryGetValue("virtualDevice", out savedVirtual)) savedVirtual = cable;
            SelectByName(cbVirtual, savedVirtual);
            SelectByName(cbMonitor, Get("monitorDevice", null));
            SelectByName(cbMic, Get("micDevice", null));
            loading = wasLoading;
            UpdateCableCard();
        }

        static string CableName(List<string> outs)
        {
            // Prefer the regular stereo "CABLE Input" over variants like "CABLE In 16ch".
            return outs.FirstOrDefault(n => n.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                ?? outs.FirstOrDefault(n =>
                {
                    string l = n.ToLowerInvariant();
                    return l.Contains("voicemeeter input") || l.Contains("vb-audio") || l.Contains("virtual audio cable");
                });
        }

        static void SelectByName(ComboBox cb, string name)
        {
            cb.SelectedIndex = 0;
            if (string.IsNullOrEmpty(name)) return;
            for (int i = 0; i < cb.Items.Count; i++)
                if (((DeviceItem)cb.Items[i]).Name == name) { cb.SelectedIndex = i; return; }
        }

        void UpdateCableCard()
        {
            bool installed = CableName(WinMM.OutputNames()) != null;
            DeviceItem v = cbVirtual.SelectedItem as DeviceItem;
            bool routed = v != null && v.Id != -2 && virtualOut != null;
            if (installing)
                cableCard.Set(Theme.Accent, Theme.IcDownload, "Installing virtual mic…", installStep ?? "");
            else if (!installed)
                cableCard.Set(Theme.Warn, Theme.IcWarning, "Virtual mic not installed", "Install it once so Discord, games and OBS can hear your sounds.");
            else if (!routed)
                cableCard.Set(Theme.Warn, Theme.IcWarning, "Virtual mic is off", "Pick \"CABLE Input\" under Send sounds to.");
            else
                cableCard.Set(Theme.Good, Theme.IcCheck, "Virtual mic ready", "In Discord: Input Device \"CABLE Output\", Noise Suppression off.");
            btnInstall.Enabled = !installing;
            if ((installed && !installing) == !sideHidden.Contains(btnInstall))
                SetSideVisible(btnInstall, !installed || installing);
        }

        void OpenAudio()
        {
            CloseAudio();
            List<string> errors = new List<string>();

            DeviceItem v = cbVirtual.SelectedItem as DeviceItem;
            bool hasVirtual = v != null && v.Id != -2;
            if (hasVirtual)
            {
                try { virtualOut = new OutputDevice(v.Id); }
                catch (Exception ex) { errors.Add(v.Name + ": " + ex.Message); }
            }
            if (virtualOut != null && tgMic.Checked)
            {
                DeviceItem m = cbMic.SelectedItem as DeviceItem;
                try
                {
                    mic = new MicCapture(m == null ? -1 : m.Id);
                    if (tgDenoise.Checked) mic.Suppressor = new NoiseSuppressor();
                    virtualOut.Mic = mic;
                }
                catch (Exception ex) { errors.Add("Microphone: " + ex.Message); }
            }
            if (tgMonitor.Checked)
            {
                DeviceItem mo = cbMonitor.SelectedItem as DeviceItem;
                try { monitorOut = new OutputDevice(mo == null ? -1 : mo.Id); }
                catch (Exception ex) { errors.Add("Speakers: " + ex.Message); }
            }
            tgMic.Enabled = slSound.Enabled = hasVirtual;
            cbMic.Enabled = slMic.Enabled = tgDenoise.Enabled = hasVirtual && tgMic.Checked;
            cbMonitor.Enabled = slMonitor.Enabled = tgMonitor.Checked;
            ApplyVolumes();
            UpdateCableCard();
            if (errors.Count > 0) Flash(string.Join("   ", errors), 8, Theme.Danger);
        }

        void CloseAudio()
        {
            if (virtualOut != null) { virtualOut.Dispose(); virtualOut = null; }
            if (mic != null) { mic.Dispose(); mic = null; }
            if (monitorOut != null) { monitorOut.Dispose(); monitorOut = null; }
        }

        void ApplyVolumes()
        {
            if (virtualOut != null) { virtualOut.Volume = slSound.Value / 100f; virtualOut.MicVolume = slMic.Value / 100f; }
            if (monitorOut != null) monitorOut.Volume = slMonitor.Value / 100f;
        }

        void Play(SoundEntry s)
        {
            short[] d = s.Data;
            if (d == null)
            {
                try { DecodeEntry(s); d = s.Data; }
                catch (Exception ex) { s.Error = ex.Message; }
                grid.RedrawPads();
                if (d == null) { Flash("Can't play \"" + s.Name + "\": " + s.Error, 6, Theme.Danger); return; }
            }
            if (virtualOut == null && monitorOut == null)
            {
                Flash("Nothing to play on: install the virtual mic or turn on \"Let me hear sounds too\".", 6, Theme.Warn);
                return;
            }
            if (tgStopPrev.Checked) StopAll();

            float gain = PlayGain(s);
            int start = (int)Math.Min(d.Length, (long)s.TrimStartMs * WinMM.SampleRate / 1000 * 2);
            int end = s.TrimEndMs > 0 ? (int)Math.Min(d.Length, (long)s.TrimEndMs * WinMM.SampleRate / 1000 * 2) : d.Length;
            Action start1 = () =>
            {
                if (virtualOut != null) virtualOut.Play(d, s, gain, start, end, s.Loop);
                if (monitorOut != null) monitorOut.Play(d, s, gain, start, end, s.Loop);
            };

            // With push-to-talk, press the key first and give the voice app a moment to open the mic,
            // otherwise the start of the sound gets cut off.
            if (pttKey != Keys.None && virtualOut != null && !pttHeld)
            {
                PttPress();
                After(80, start1);
            }
            else
            {
                if (pttHeld) pttLastActive = DateTime.Now;
                start1();
            }
        }

        /// <summary>Clicking a pad or pressing its hotkey: what happens depends on the sound's play mode.</summary>
        void Trigger(SoundEntry s)
        {
            bool isPlaying = (virtualOut != null && virtualOut.Progress().ContainsKey(s)) || (monitorOut != null && monitorOut.Progress().ContainsKey(s));
            if (isPlaying && s.Mode == PlayMode.Stop) { StopSound(s); return; }
            if (isPlaying && s.Mode == PlayMode.Restart) StopSound(s);
            Play(s);
        }

        float PlayGain(SoundEntry s)
        {
            return s.Volume / 100f * (tgNormalize.Checked ? s.NormGain : 1f);
        }

        static void After(int ms, Action a)
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = ms;
            t.Tick += (o, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        // ---- push-to-talk

        void PttPress()
        {
            if (pttHeld || pttKey == Keys.None) return;
            PushToTalk.Press(pttKey);
            pttHeld = true;
            pttLastActive = DateTime.Now;
        }

        void PttRelease()
        {
            if (!pttHeld) return;
            pttHeld = false;
            try { PushToTalk.Release(pttKey); } catch { }
        }

        void SetPttKey()
        {
            Keys k;
            UnregisterHotkeys();
            try
            {
                using (HotkeyDialog d = new HotkeyDialog("Push-to-talk key", pttKey, true))
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    k = d.Result;
                }
            }
            finally { RegisterHotkeys(); }
            if (k != Keys.None && (k == stopKey || k == replayKey || sounds.Any(x => x.Hotkey == k)))
            {
                Flash("That key is already a SoundDeck hotkey. Pick a different push-to-talk key.", 6, Theme.Warn);
                return;
            }
            PttRelease();
            pttKey = k;
            UpdatePttButton();
            SaveSettings();
            if (k != Keys.None) Flash("SoundDeck now holds " + PushToTalk.Describe(k) + " while sounds play. Use the same key in Discord.", 7, Theme.Good);
        }

        void UpdatePttButton()
        {
            btnPtt.Text = "Push-to-talk key:  " + (pttKey == Keys.None ? "off" : PushToTalk.Describe(pttKey));
        }

        void StopSound(SoundEntry s)
        {
            if (virtualOut != null) virtualOut.Stop(s);
            if (monitorOut != null) monitorOut.Stop(s);
        }

        void StopAll()
        {
            if (virtualOut != null) virtualOut.StopAll();
            if (monitorOut != null) monitorOut.StopAll();
        }

        void InstallCable()
        {
            if (installing) return;
            installing = true;
            installStep = "Starting…";
            UpdateCableCard();
            Task.Factory.StartNew(() =>
            {
                string error = null;
                bool ok = false;
                try
                {
                    ok = CableInstaller.Install(step => BeginInvoke(new Action(() => { installStep = step; UpdateCableCard(); })));
                }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(() => InstallFinished(ok, error)));
            });
        }

        void InstallFinished(bool ok, string error)
        {
            installing = false;
            LoadDevices();
            string cable = CableName(WinMM.OutputNames());
            if (cable != null)
            {
                loading = true;
                SelectByName(cbVirtual, cable);
                loading = false;
                SaveSettings();
            }
            OpenAudio();

            if (error != null) Flash("Couldn't install the virtual mic: " + error, 12, Theme.Danger);
            else if (!ok) Flash("Install cancelled.", 5, Theme.Warn);
            else if (cable != null)
            {
                Flash("Virtual mic installed!", 6, Theme.Good);
                MessageBox.Show(this, "The virtual mic is installed.\n\nIn Discord, go to Settings → Voice & Video and set Input Device to \"CABLE Output\".\n\n"
                    + "If you don't hear anything, restart your PC once — VB-Audio recommends it after installing.", "SoundDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "Almost done! Restart your PC to finish installing the virtual mic, then open SoundDeck again.", "SoundDeck",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            UpdateCableCard();
        }

        void DecodeWorker()
        {
            foreach (SoundEntry s in decodeQueue.GetConsumingEnumerable())
            {
                if (s.Data != null) continue;
                try { DecodeEntry(s); }
                catch (Exception ex) { s.Error = ex.Message; }
                try { BeginInvoke(new Action(() => grid.RedrawPads())); } catch { }
            }
        }

        static void DecodeEntry(SoundEntry s)
        {
            short[] d = AudioDecoder.Decode(s.Path);
            s.Seconds = d.Length / 2.0 / WinMM.SampleRate;
            s.NormGain = Loudness.GainFor(d);
            s.Error = null;
            s.Data = d;
        }

        // ================================================================ sounds

        bool Searching { get { return search.Box.Text.Trim().Length > 0; } }

        /// <summary>The sounds shown right now: search results (across all tabs), or the current tab.</summary>
        List<SoundEntry> FilteredSounds()
        {
            string f = search.Box.Text.Trim().ToLowerInvariant();
            if (f.Length > 0)
                return sounds.Where(s => s.Name.ToLowerInvariant().Contains(f) || s.Category.ToLowerInvariant().Contains(f)
                    || PadGrid.HotkeyText(s.Hotkey).ToLowerInvariant().Contains(f)).ToList();
            return sounds.Where(InView).ToList();
        }

        bool InView(SoundEntry s)
        {
            if (view == CategoryBar.AllKey) return true;
            if (view == CategoryBar.FavoritesKey) return s.Favorite;
            return s.Category == view;
        }

        void RefreshGrid()
        {
            grid.Filtered = Searching;
            grid.AllowReorder = !Searching;
            if (view == CategoryBar.FavoritesKey)
            {
                grid.EmptyTitle = "No favorites yet";
                grid.EmptySub = "Click the star on a sound, or drag a sound onto this tab.";
            }
            else if (view != CategoryBar.AllKey)
            {
                grid.EmptyTitle = "\"" + view + "\" is empty";
                grid.EmptySub = "Drag sounds onto this tab, or drop audio files here to add them to it.";
            }
            else
            {
                grid.EmptyTitle = "Your soundboard is empty";
                grid.EmptySub = "Drag audio files or folders here, or click to add sounds.";
            }
            grid.SetItems(FilteredSounds());
            RefreshChips();
            statusBar.Invalidate();
        }

        void RefreshChips()
        {
            List<CategoryBar.Chip> chips = new List<CategoryBar.Chip>();
            chips.Add(MakeChip(CategoryBar.AllKey, "All", Theme.IcAll, sounds.Count, Color.Empty));
            chips.Add(MakeChip(CategoryBar.FavoritesKey, "Favorites", Theme.IcStarFilled, sounds.Count(s => s.Favorite), Color.Empty));
            foreach (string c in categories)
            {
                string cat = c;
                chips.Add(MakeChip(cat, cat, null, sounds.Count(s => s.Category == cat), CategoryColor(cat)));
            }
            catBar.SetChips(chips, Searching ? null : view);
        }

        static CategoryBar.Chip MakeChip(string key, string label, string glyph, int count, Color dot)
        {
            CategoryBar.Chip c = new CategoryBar.Chip();
            c.Key = key; c.Label = label; c.Glyph = glyph; c.Count = count; c.Dot = dot;
            return c;
        }

        Color CategoryColor(string cat)
        {
            int i = categories.IndexOf(cat);
            return Theme.PadColors[(i < 0 ? 0 : i + 1) % Theme.PadColors.Length];
        }

        /// <summary>Sounds in a category share its color, so groups stand out even in the All tab.</summary>
        Color PadColor(SoundEntry s)
        {
            if (s.Category.Length > 0 && categories.Contains(s.Category)) return CategoryColor(s.Category);
            return Theme.PadColors[PadGrid.HashIndex(s.Name, Theme.PadColors.Length)];
        }

        void ShowView(string key)
        {
            if (Searching) search.Box.Text = ""; // clicking a tab leaves search
            view = key;
            grid.ScrollToTop();
            RefreshGrid();
            SaveSettings();
        }

        /// <summary>A pad was dragged to slot <paramref name="newIndex"/> of the visible list; apply that to the full list.</summary>
        void MoveSound(SoundEntry moved, int newIndex)
        {
            List<SoundEntry> visible = FilteredSounds();
            visible.Remove(moved);
            newIndex = Math.Max(0, Math.Min(newIndex, visible.Count));
            sounds.Remove(moved);
            if (newIndex < visible.Count) sounds.Insert(sounds.IndexOf(visible[newIndex]), moved);           // before the pad it lands in front of
            else if (visible.Count > 0) sounds.Insert(sounds.IndexOf(visible[visible.Count - 1]) + 1, moved); // after the last visible pad
            else sounds.Add(moved);
            RefreshGrid();
            RegisterHotkeys(); // hotkey ids follow list positions
            SaveSounds();
        }

        void DropOnCategory(SoundEntry s, Point screen)
        {
            CategoryBar.Chip chip = catBar.DropTargetAt(screen);
            catBar.SetDropHover(null);
            if (chip == null) return;
            if (chip.Key == CategoryBar.FavoritesKey)
            {
                if (!s.Favorite) ToggleFavorite(s);
                return;
            }
            SetCategory(s, chip.Key);
        }

        void SetCategory(SoundEntry s, string cat)
        {
            if (s.Category == cat) return;
            s.Category = cat;
            RefreshGrid();
            SaveSounds();
            Flash(cat.Length == 0 ? "Removed \"" + s.Name + "\" from its category." : "Moved \"" + s.Name + "\" to " + cat + ".", 3, Theme.Good);
        }

        void ToggleFavorite(SoundEntry s)
        {
            s.Favorite = !s.Favorite;
            RefreshGrid();
            SaveSounds();
        }

        void EditVolume(SoundEntry s)
        {
            using (VolumeDialog d = new VolumeDialog(s.Name, s.Volume, v => { s.Volume = v; grid.RedrawPads(); }, () => { StopSound(s); Play(s); }))
                d.ShowDialog(this);
            SaveSounds();
        }

        static string CleanCategoryName(string name)
        {
            if (name == null) return null;
            name = name.Replace('\t', ' ').TrimStart('*').Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>Asks for a name and creates the category. Returns its name, or null if cancelled.</summary>
        string NewCategory()
        {
            string name = CleanCategoryName(TextDialog.Ask(this, "New category", "Category name", "", "Create"));
            if (name == null) return null;
            if (!categories.Contains(name)) categories.Add(name);
            RefreshGrid();
            SaveSounds();
            return name;
        }

        void EnsureCategory(string name)
        {
            if (!categories.Contains(name)) categories.Add(name);
        }

        void RenameCategory(string cat)
        {
            string name = CleanCategoryName(TextDialog.Ask(this, "Rename category", "Category name", cat, "Save"));
            if (name == null || name == cat) return;
            if (categories.Contains(name)) { Flash("There's already a category called \"" + name + "\".", 5, Theme.Warn); return; }
            categories[categories.IndexOf(cat)] = name;
            foreach (SoundEntry s in sounds) if (s.Category == cat) s.Category = name;
            if (view == cat) view = name;
            RefreshGrid();
            SaveSounds();
            SaveSettings();
        }

        void MoveCategory(string cat, int dir)
        {
            int i = categories.IndexOf(cat), j = i + dir;
            if (i < 0 || j < 0 || j >= categories.Count) return;
            categories[i] = categories[j];
            categories[j] = cat;
            RefreshGrid();
            grid.RedrawPads(); // colors follow category positions
            SaveSounds();
        }

        void SortCategory(string cat)
        {
            List<SoundEntry> members = sounds.Where(s => s.Category == cat).ToList();
            List<SoundEntry> sorted = members.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            // put the sorted sounds back into the slots the category already occupies
            List<int> slots = members.Select(s => sounds.IndexOf(s)).OrderBy(x => x).ToList();
            for (int k = 0; k < slots.Count; k++) sounds[slots[k]] = sorted[k];
            RefreshGrid();
            RegisterHotkeys();
            SaveSounds();
        }

        void DeleteCategory(string cat)
        {
            int n = sounds.Count(s => s.Category == cat);
            categories.Remove(cat);
            foreach (SoundEntry s in sounds) if (s.Category == cat) s.Category = "";
            if (view == cat) view = CategoryBar.AllKey;
            RefreshGrid();
            SaveSounds();
            SaveSettings();
            Flash("Deleted category \"" + cat + "\". Its " + n + " sound" + (n == 1 ? " is" : "s are") + " still under All.", 5, Theme.Good);
        }

        /// <summary>
        /// New sounds go into the open category. A folder added from the All or Favorites tab becomes its own category.
        /// </summary>
        void AddFiles(IEnumerable<string> paths)
        {
            string target = view != CategoryBar.AllKey && view != CategoryBar.FavoritesKey ? view : "";
            int added = 0;
            List<string> newCats = new List<string>();
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    string cat = target;
                    if (cat.Length == 0)
                    {
                        cat = CleanCategoryName(Path.GetFileName(p.TrimEnd('\\', '/'))) ?? "";
                        if (cat.Length > 0 && !categories.Contains(cat)) newCats.Add(cat);
                    }
                    foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories).Where(IsAudioFile).OrderBy(x => x))
                        if (AddOne(f, cat)) added++;
                    continue;
                }
                if (AddOne(p, target)) added++;
            }
            foreach (string c in newCats) if (sounds.Any(s => s.Category == c)) EnsureCategory(c);
            if (added > 0)
            {
                RefreshGrid();
                SaveSounds();
                string where = newCats.Count == 1 ? " to the new category \"" + newCats[0] + "\"" : target.Length > 0 ? " to " + target : "";
                Flash("Added " + added + " sound" + (added == 1 ? "" : "s") + where + ".", 4, Theme.Good);
            }
        }

        string RecordingsDir
        {
            get
            {
                if (Environment.GetEnvironmentVariable("SOUNDDECK_DATA") != null) return Path.Combine(dataDir, "Recordings"); // test copies
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SoundDeck Recordings");
            }
        }

        void OpenRecorder()
        {
            DeviceItem m = cbMic.SelectedItem as DeviceItem;
            string cat = view != CategoryBar.AllKey && view != CategoryBar.FavoritesKey ? view : "";
            using (RecordDialog d = new RecordDialog(m == null ? -1 : m.Id, m == null ? "your default microphone" : m.ToString(), categories, cat))
                if (d.ShowDialog(this) == DialogResult.OK) AddRecording(d);
        }

        /// <summary>Saves a finished recording (or replay) as a WAV file and puts it on the board.</summary>
        void AddRecording(RecordDialog d)
        {
            try
            {
                Directory.CreateDirectory(RecordingsDir);
                string file = string.Join("_", d.ResultName.Split(Path.GetInvalidFileNameChars())).Trim();
                if (file.Length == 0) file = "Recording";
                string path = Path.Combine(RecordingsDir, file + ".wav");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(RecordingsDir, file + " (" + i + ").wav");
                Recording.WriteWav(path, d.ResultSamples, 0, d.ResultSamples.Length);

                SoundEntry e = new SoundEntry();
                e.Path = path;
                e.Name = d.ResultName;
                e.Category = d.ResultCategory;
                e.Favorite = view == CategoryBar.FavoritesKey;
                e.Data = d.ResultSamples;
                e.Seconds = d.ResultSamples.Length / 2.0 / WinMM.SampleRate;
                e.NormGain = Loudness.GainFor(d.ResultSamples);
                sounds.Add(e);
                RefreshGrid();
                SaveSounds();
                Flash("Saved \"" + e.Name + "\" to your board (file: Documents\\SoundDeck Recordings).", 6, Theme.Good);
            }
            catch (Exception ex) { Flash("Couldn't save the recording: " + ex.Message, 8, Theme.Danger); }
        }

        // ---- instant replay

        void UpdateReplay()
        {
            if (tgReplay.Checked && replay == null)
            {
                try { replay = new ReplayBuffer(); }
                catch (Exception ex)
                {
                    Flash("Instant replay couldn't start: " + ex.Message, 8, Theme.Danger);
                    loading = true;
                    tgReplay.Checked = false;
                    loading = false;
                }
            }
            else if (!tgReplay.Checked && replay != null)
            {
                replay.Dispose();
                replay = null;
            }
            btnReplayKey.Enabled = btnReplayNow.Enabled = tgReplay.Checked;
            RegisterHotkeys();
        }

        void SaveReplay()
        {
            if (replay == null) { Flash("Turn on instant replay in the sidebar first.", 5, Theme.Warn); return; }
            short[] clip = replay.Snapshot();
            if (clip.Length < WinMM.SampleRate / 5)
            {
                Flash("Nothing to save yet: instant replay only keeps what played since it was turned on.", 6, Theme.Warn);
                return;
            }
            if (!Visible) ShowFromTray();
            Activate();
            string cat = view != CategoryBar.AllKey && view != CategoryBar.FavoritesKey ? view : "";
            using (RecordDialog d = new RecordDialog(clip, categories, cat))
                if (d.ShowDialog(this) == DialogResult.OK) AddRecording(d);
        }

        void SetReplayHotkey()
        {
            Keys k;
            if (!AskHotkey("Save-replay hotkey", replayKey, out k)) return;
            if (k != Keys.None) ClearDuplicate(k);
            replayKey = k;
            UpdateReplayButton();
            RegisterHotkeys();
            SaveSettings();
            SaveSounds();
        }

        void UpdateReplayButton()
        {
            btnReplayKey.Text = "Save-replay hotkey:  " + (replayKey == Keys.None ? "not set" : PadGrid.HotkeyText(replayKey));
        }

        // ---- trim / play mode, relinking

        void EditSound(SoundEntry s)
        {
            if (s.Data == null)
            {
                try { DecodeEntry(s); }
                catch (Exception ex) { s.Error = ex.Message; grid.RedrawPads(); return; }
            }
            using (TrimDialog d = new TrimDialog(s, PlayGain(s)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                s.TrimStartMs = d.StartMs;
                s.TrimEndMs = d.EndMs;
                s.Mode = d.Mode;
                s.Loop = d.Loop;
            }
            grid.RedrawPads();
            SaveSounds();
        }

        /// <summary>Points a missing sound at its new location; other missing sounds from the same folder follow along.</summary>
        void Relink(SoundEntry s)
        {
            string newPath;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Where is \"" + Path.GetFileName(s.Path) + "\" now?";
                d.FileName = Path.GetFileName(s.Path);
                d.Filter = "Audio files|" + string.Join(";", AudioExts.Select(x => "*" + x)) + "|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                newPath = d.FileName;
            }
            string oldDir = Path.GetDirectoryName(s.Path), newDir = Path.GetDirectoryName(newPath);
            int fixedCount = 0;
            foreach (SoundEntry x in sounds)
            {
                string candidate = x == s ? newPath
                    : x.Error != null && x.Path.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(newDir, x.Path.Substring(oldDir.Length).TrimStart('\\'))
                        : null;
                if (candidate == null || !File.Exists(candidate)) continue;
                x.Path = candidate;
                x.Error = null;
                x.Data = null;
                x.Seconds = -1;
                decodeQueue.Add(x);
                fixedCount++;
            }
            grid.RedrawPads();
            SaveSounds();
            Flash(fixedCount > 1 ? "Found " + fixedCount + " sounds in the new folder." : "Found it.", 4, Theme.Good);
        }

        bool AddOne(string path, string category)
        {
            if (!File.Exists(path)) return false;
            string full = Path.GetFullPath(path);
            if (sounds.Any(s => string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase))) return false;
            SoundEntry e = new SoundEntry();
            e.Path = full;
            e.Name = Path.GetFileNameWithoutExtension(full);
            e.Category = category;
            e.Favorite = view == CategoryBar.FavoritesKey;
            sounds.Add(e);
            decodeQueue.Add(e);
            return true;
        }

        static bool IsAudioFile(string p)
        {
            return AudioExts.Contains(Path.GetExtension(p).ToLowerInvariant());
        }

        void AddFilesDialog()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = "Add sounds";
                d.Filter = "Audio files|" + string.Join(";", AudioExts.Select(x => "*" + x)) + "|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
            }
        }

        void AddFolderDialog()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Add every audio file in this folder (and its subfolders)";
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(new[] { d.SelectedPath });
            }
        }

        void Remove(List<SoundEntry> list)
        {
            if (list.Count == 0) return;
            lastRemoved = list.Select(s => new KeyValuePair<int, SoundEntry>(sounds.IndexOf(s), s)).OrderBy(kv => kv.Key).ToList();
            foreach (SoundEntry s in list)
            {
                StopSound(s);
                sounds.Remove(s);
            }
            grid.ClearSelection();
            RefreshGrid();
            RegisterHotkeys();
            SaveSounds();
            Flash("Removed " + (list.Count == 1 ? "\"" + list[0].Name + "\"" : list.Count + " sounds") + " from the board. Press Ctrl+Z to undo.", 6, Theme.Good);
        }

        void UndoRemove()
        {
            if (lastRemoved == null) return;
            foreach (KeyValuePair<int, SoundEntry> kv in lastRemoved) // ascending, so earlier positions are restored first
                sounds.Insert(Math.Min(kv.Key, sounds.Count), kv.Value);
            Flash("Restored " + lastRemoved.Count + " sound" + (lastRemoved.Count == 1 ? "" : "s") + ".", 4, Theme.Good);
            lastRemoved = null;
            RefreshGrid();
            RegisterHotkeys();
            SaveSounds();
        }

        void Rename(SoundEntry s)
        {
            string name = TextDialog.Ask(this, "Rename sound", "Name", s.Name, "Save");
            if (name == null) return;
            s.Name = name;
            RefreshGrid();
            SaveSounds();
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AddFiles(files);
        }

        // ================================================================ hotkeys

        void SetHotkey(SoundEntry s)
        {
            Keys k;
            if (!AskHotkey("Hotkey for \"" + s.Name + "\"", s.Hotkey, out k)) return;
            if (k != Keys.None) ClearDuplicate(k);
            s.Hotkey = k;
            grid.RedrawPads();
            RegisterHotkeys();
            SaveSounds();
            SaveSettings();
        }

        void SetStopHotkey()
        {
            Keys k;
            if (!AskHotkey("Stop-all hotkey", stopKey, out k)) return;
            if (k != Keys.None) ClearDuplicate(k);
            stopKey = k;
            UpdateStopKeyButton();
            RegisterHotkeys();
            SaveSettings();
            SaveSounds();
        }

        void ClearDuplicate(Keys k)
        {
            if (stopKey == k) { stopKey = Keys.None; UpdateStopKeyButton(); }
            if (replayKey == k) { replayKey = Keys.None; UpdateReplayButton(); }
            if (pttKey == k) { pttKey = Keys.None; UpdatePttButton(); Flash("That was your push-to-talk key, so push-to-talk is now off.", 6, Theme.Warn); }
            foreach (SoundEntry x in sounds) if (x.Hotkey == k) x.Hotkey = Keys.None;
            grid.RedrawPads();
        }

        bool AskHotkey(string title, Keys current, out Keys result)
        {
            UnregisterHotkeys(); // so the key reaches the dialog instead of firing
            try
            {
                using (HotkeyDialog d = new HotkeyDialog(title, current))
                {
                    bool ok = d.ShowDialog(this) == DialogResult.OK;
                    result = d.Result;
                    return ok;
                }
            }
            finally
            {
                RegisterHotkeys();
            }
        }

        void UpdateStopKeyButton()
        {
            btnStopKey.Text = "Stop-all hotkey:  " + (stopKey == Keys.None ? "not set" : PadGrid.HotkeyText(stopKey));
        }

        void RegisterHotkeys()
        {
            UnregisterHotkeys();
            List<string> failed = new List<string>();
            if (stopKey != Keys.None && !TryRegister(StopHotkeyId, stopKey)) failed.Add(PadGrid.HotkeyText(stopKey));
            if (replayKey != Keys.None && replay != null && !TryRegister(ReplayHotkeyId, replayKey)) failed.Add(PadGrid.HotkeyText(replayKey));
            for (int i = 0; i < sounds.Count; i++)
                if (sounds[i].Hotkey != Keys.None && !TryRegister(SoundHotkeyBase + i, sounds[i].Hotkey)) failed.Add(PadGrid.HotkeyText(sounds[i].Hotkey));
            if (failed.Count > 0) Flash("Already used by another program: " + string.Join(", ", failed), 8, Theme.Warn);
        }

        bool TryRegister(int id, Keys k)
        {
            uint mod = 0x4000; // MOD_NOREPEAT
            if ((k & Keys.Alt) != 0) mod |= 1;
            if ((k & Keys.Control) != 0) mod |= 2;
            if ((k & Keys.Shift) != 0) mod |= 4;
            if (!RegisterHotKey(Handle, id, mod, (uint)(k & Keys.KeyCode))) return false;
            registeredIds.Add(id);
            return true;
        }

        void UnregisterHotkeys()
        {
            foreach (int id in registeredIds) UnregisterHotKey(Handle, id);
            registeredIds.Clear();
        }

        // ================================================================ status

        void Flash(string msg, int seconds, Color color)
        {
            flashText = msg;
            flashColor = color;
            flashUntil = DateTime.Now.AddSeconds(seconds);
            statusBar.Invalidate();
        }

        // ================================================================ persistence

        string SettingsFile { get { return Path.Combine(dataDir, "settings.txt"); } }
        string SoundsFile { get { return Path.Combine(dataDir, "sounds.txt"); } }

        void LoadSettings()
        {
            settings.Clear();
            if (!File.Exists(SettingsFile)) return;
            foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) settings[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
        }

        void SaveSettings()
        {
            if (loading) return;
            settings["virtualDevice"] = DeviceName(cbVirtual);
            settings["monitorDevice"] = DeviceName(cbMonitor);
            settings["micDevice"] = DeviceName(cbMic);
            settings["soundVolume"] = slSound.Value.ToString();
            settings["micVolume"] = slMic.Value.ToString();
            settings["monitorVolume"] = slMonitor.Value.ToString();
            settings["micPassthrough"] = tgMic.Checked ? "1" : "0";
            settings["monitor"] = tgMonitor.Checked ? "1" : "0";
            settings["stopPrevious"] = tgStopPrev.Checked ? "1" : "0";
            settings["stopHotkey"] = ((int)stopKey).ToString();
            settings["compact"] = grid.Compact ? "1" : "0";
            settings["replayHotkey"] = ((int)replayKey).ToString();
            settings["pushToTalkKey"] = ((int)pttKey).ToString();
            settings["normalize"] = tgNormalize.Checked ? "1" : "0";
            settings["denoise"] = tgDenoise.Checked ? "1" : "0";
            settings["instantReplay"] = tgReplay.Checked ? "1" : "0";
            settings["tray"] = tgTray.Checked ? "1" : "0";
            settings["view"] = view;
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllLines(SettingsFile, settings.Select(kv => kv.Key + "=" + kv.Value), Encoding.UTF8);
            }
            catch (Exception ex) { Flash("Could not save settings: " + ex.Message, 6, Theme.Danger); }
        }

        void LoadSounds()
        {
            sounds.Clear();
            if (!File.Exists(SoundsFile)) return;
            foreach (string line in File.ReadAllLines(SoundsFile, Encoding.UTF8))
            {
                string[] parts = line.Split('\t');
                SoundEntry s = ParseSound(parts);
                if (s != null) sounds.Add(s);
            }

            categories.Clear();
            if (File.Exists(CategoriesFile))
                foreach (string line in File.ReadAllLines(CategoriesFile, Encoding.UTF8))
                    if (line.Trim().Length > 0 && !categories.Contains(line.Trim())) categories.Add(line.Trim());
            foreach (SoundEntry s in sounds) // never lose a category a sound refers to
                if (s.Category.Length > 0) EnsureCategory(s.Category);
        }

        string CategoriesFile { get { return Path.Combine(dataDir, "categories.txt"); } }

        void SaveSounds()
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                File.WriteAllLines(SoundsFile, sounds.Select(s => FormatSound(s, s.Path)), Encoding.UTF8);
                File.WriteAllLines(CategoriesFile, categories, Encoding.UTF8);
            }
            catch (Exception ex) { Flash("Could not save sound list: " + ex.Message, 6, Theme.Danger); }
        }

        // One sound per line, tab-separated:
        // hotkey, name, path, category, favorite, volume, trim start ms, trim end ms, play mode, loop.
        // Older files have fewer columns (1.0: the first three; 1.1–1.2: the first six).
        static string FormatSound(SoundEntry s, string path)
        {
            return string.Join("\t", new[] {
                ((int)s.Hotkey).ToString(), s.Name, path, s.Category, s.Favorite ? "1" : "0", s.Volume.ToString(),
                s.TrimStartMs.ToString(), s.TrimEndMs.ToString(), ((int)s.Mode).ToString(), s.Loop ? "1" : "0" });
        }

        static SoundEntry ParseSound(string[] parts)
        {
            if (parts.Length < 3) return null;
            SoundEntry s = new SoundEntry();
            int n;
            int.TryParse(parts[0], out n);
            s.Hotkey = (Keys)n;
            s.Name = parts[1];
            s.Path = parts[2];
            if (parts.Length > 3) s.Category = parts[3];
            if (parts.Length > 4) s.Favorite = parts[4] == "1";
            if (parts.Length > 5 && int.TryParse(parts[5], out n)) s.Volume = Math.Max(0, Math.Min(200, n));
            if (parts.Length > 6 && int.TryParse(parts[6], out n)) s.TrimStartMs = Math.Max(0, n);
            if (parts.Length > 7 && int.TryParse(parts[7], out n)) s.TrimEndMs = Math.Max(0, n);
            if (parts.Length > 8 && int.TryParse(parts[8], out n) && n >= 0 && n <= 2) s.Mode = (PlayMode)n;
            if (parts.Length > 9) s.Loop = parts[9] == "1";
            return s;
        }

        // ================================================================ sharing boards

        /// <summary>Packs the board (sounds, categories, settings per sound) into one .sdboard file for friends.</summary>
        void ExportBoard()
        {
            List<SoundEntry> list = sounds.Where(s => File.Exists(s.Path)).ToList();
            if (list.Count == 0) { Flash("There's nothing to export yet.", 4, Theme.Warn); return; }
            string target;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Export board";
                d.Filter = "SoundDeck board|*.sdboard";
                d.FileName = "My SoundDeck board.sdboard";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                target = d.FileName;
            }
            try
            {
                WriteBoard(target, list, categories);
                Flash("Exported " + list.Count + " sounds to " + Path.GetFileName(target) + ".", 6, Theme.Good);
                Process.Start("explorer.exe", "/select,\"" + target + "\"");
            }
            catch (Exception ex) { Flash("Couldn't export the board: " + ex.Message, 8, Theme.Danger); }
        }

        /// <summary>A .sdboard file is a zip: sounds/…, board.txt (same line format as sounds.txt, paths inside the zip) and categories.txt.</summary>
        internal static void WriteBoard(string target, List<SoundEntry> list, List<string> categories)
        {
            if (File.Exists(target)) File.Delete(target);
            using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.Open(target, System.IO.Compression.ZipArchiveMode.Create))
            {
                List<string> lines = new List<string>();
                for (int i = 0; i < list.Count; i++)
                {
                    SoundEntry s = list[i];
                    string inZip = "sounds/" + (i + 1).ToString("000") + " " + Path.GetFileName(s.Path);
                    System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, s.Path, inZip);
                    SoundEntry copy = s.Clone();
                    copy.Hotkey = Keys.None; // hotkeys are personal; the receiver picks their own
                    lines.Add(FormatSound(copy, inZip));
                }
                WriteZipText(zip, "board.txt", lines);
                WriteZipText(zip, "categories.txt", categories.Where(c => list.Any(s => s.Category == c)));
            }
        }

        /// <summary>Unpacks a .sdboard into <paramref name="dir"/> and returns its sounds (paths pointing into dir).</summary>
        internal static List<SoundEntry> ReadBoard(string source, string dir, List<string> categoriesOut)
        {
            System.IO.Compression.ZipFile.ExtractToDirectory(source, dir);
            string boardFile = Path.Combine(dir, "board.txt");
            if (!File.Exists(boardFile)) throw new Exception("this isn't a SoundDeck board file");
            string catFile = Path.Combine(dir, "categories.txt");
            if (File.Exists(catFile))
                foreach (string c in File.ReadAllLines(catFile, Encoding.UTF8)) if (c.Trim().Length > 0) categoriesOut.Add(c.Trim());

            List<SoundEntry> result = new List<SoundEntry>();
            string root = Path.GetFullPath(dir);
            foreach (string line in File.ReadAllLines(boardFile, Encoding.UTF8))
            {
                SoundEntry s = ParseSound(line.Split('\t'));
                if (s == null) continue;
                string full = Path.GetFullPath(Path.Combine(dir, s.Path.Replace('/', '\\')));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue; // stay inside the board folder
                s.Path = full;
                s.Hotkey = Keys.None;
                result.Add(s);
            }
            return result;
        }

        static void WriteZipText(System.IO.Compression.ZipArchive zip, string name, IEnumerable<string> lines)
        {
            using (StreamWriter w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                foreach (string l in lines) w.WriteLine(l);
        }

        void ImportBoard()
        {
            string source;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Import a shared board";
                d.Filter = "SoundDeck board|*.sdboard;*.zip";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                source = d.FileName;
            }
            try
            {
                string boardName = CleanCategoryName(Path.GetFileNameWithoutExtension(source)) ?? "Imported";
                string dir = Path.Combine(Path.GetDirectoryName(RecordingsDir), "SoundDeck Boards", boardName);
                for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Path.GetDirectoryName(RecordingsDir), "SoundDeck Boards", boardName + " (" + i + ")");
                List<string> cats = new List<string>();
                List<SoundEntry> imported = ReadBoard(source, dir, cats);
                foreach (string c in cats) EnsureCategory(c);
                foreach (SoundEntry s in imported)
                {
                    if (s.Category.Length > 0) EnsureCategory(s.Category);
                    sounds.Add(s);
                    decodeQueue.Add(s);
                }
                int added = imported.Count;
                RefreshGrid();
                SaveSounds();
                Flash("Imported " + added + " sounds from \"" + boardName + "\". Right-click a sound to give it a hotkey.", 7, Theme.Good);
            }
            catch (Exception ex) { Flash("Couldn't import the board: " + ex.Message, 8, Theme.Danger); }
        }

        // ================================================================ tray, startup, devices, updates

        void UpdateTray()
        {
            if (tray == null)
            {
                tray = new NotifyIcon();
                tray.Icon = Icon;
                tray.Text = "SoundDeck";
                ContextMenuStrip m = DarkMenu();
                m.Items.Add("Open SoundDeck", null, (s, e) => ShowFromTray());
                m.Items.Add("Stop all sounds", null, (s, e) => StopAll());
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Quit SoundDeck", null, (s, e) => ExitApp());
                tray.ContextMenuStrip = m;
                tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
            }
            tray.Visible = tgTray.Checked;
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool StartsWithWindows()
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("SoundDeck") != null;
        }

        void SetStartWithWindows(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("SoundDeck", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("SoundDeck", false);
                }
            }
            catch (Exception ex) { Flash("Couldn't change the startup setting: " + ex.Message, 6, Theme.Danger); }
        }

        static string DeviceSignature()
        {
            return string.Join("|", WinMM.OutputNames()) + "#" + string.Join("|", WinMM.InputNames());
        }

        void DevicesMaybeChanged()
        {
            string now = DeviceSignature();
            if (now == deviceSnapshot) return;
            deviceSnapshot = now;
            LoadDevices();
            OpenAudio();
            Flash("Audio devices changed, so SoundDeck reconnected.", 4, Theme.Good);
        }

        void CheckForUpdates(bool manual)
        {
            if (manual) Flash("Checking for updates…", 3, Theme.Good);
            Task.Factory.StartNew(() =>
            {
                Updater.Release r = null;
                string error = null;
                try { r = Updater.FindNewer(); }
                catch (Exception ex) { error = ex.Message; }
                try { BeginInvoke(new Action(() => UpdateCheckDone(r, error, manual))); } catch { }
            });
        }

        void UpdateCheckDone(Updater.Release r, string error, bool manual)
        {
            if (error != null) { if (manual) Flash("Couldn't check for updates: " + error, 6, Theme.Warn); return; }
            pendingUpdate = r;
            btnUpdate.Visible = r != null;
            if (r != null)
            {
                btnUpdate.Text = "Update to " + Updater.Short(r.Version);
                btnUpdate.FitWidth();
                Flash("SoundDeck " + Updater.Short(r.Version) + " is available. Click \"Update\" at the top.", 8, Theme.Good);
            }
            else if (manual) Flash("You have the latest version (" + Updater.Short(Updater.Current) + ").", 4, Theme.Good);
            LayoutHeader();
        }

        void InstallUpdate()
        {
            Updater.Release r = pendingUpdate;
            if (r == null) return;
            if (MessageBox.Show(this, "Download and install SoundDeck " + Updater.Short(r.Version) + "?\n\nSoundDeck restarts by itself. Your sounds and settings are kept.",
                    "Update SoundDeck", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            btnUpdate.Enabled = false;
            Task.Factory.StartNew(() =>
            {
                string error = null;
                try { Updater.Install(r, step => BeginInvoke(new Action(() => Flash(step, 30, Theme.Good)))); }
                catch (Exception ex) { error = ex.Message; }
                BeginInvoke(new Action(() =>
                {
                    if (error != null)
                    {
                        btnUpdate.Enabled = true;
                        Flash("The update failed: " + error, 10, Theme.Danger);
                        if (MessageBox.Show(this, "The update couldn't be installed automatically:\n" + error + "\n\nOpen the download page instead?",
                                "Update SoundDeck", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            try { Process.Start(r.PageUrl ?? Updater.ReleasesUrl); } catch { }
                        return;
                    }
                    Updater.Restart();
                    ExitApp();
                }));
            });
        }

        static string DeviceName(ComboBox cb)
        {
            DeviceItem d = cb.SelectedItem as DeviceItem;
            return d == null ? "" : d.Name;
        }

        string Get(string key, string def)
        {
            string v;
            return settings.TryGetValue(key, out v) ? v : def;
        }

        int GetInt(string key, int def)
        {
            int v;
            return int.TryParse(Get(key, null), out v) ? v : def;
        }
    }
}
