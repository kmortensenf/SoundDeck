using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SoundDeck
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool created;
            // one instance per data folder (a test copy with SOUNDDECK_DATA can run next to the real one)
            string data = Environment.GetEnvironmentVariable("SOUNDDECK_DATA");
            string mutexName = "SoundDeck.SingleInstance" + (data == null ? "" : "." + data.ToLowerInvariant().GetHashCode().ToString("X"));
            using (Mutex single = new Mutex(true, mutexName, out created))
            {
                if (!created)
                {
                    MessageBox.Show("SoundDeck is already running.", "SoundDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) Theme.Init(g.DpiX / 96f);
                AudioDecoder.Startup();
                Application.Run(new MainForm());
            }
        }
    }
}
