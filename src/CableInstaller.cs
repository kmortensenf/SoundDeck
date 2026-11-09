using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace SoundDeck
{
    /// <summary>
    /// Downloads the official VB-CABLE driver pack from vb-audio.com, checks its digital signature and runs its setup.
    /// VB-CABLE is donationware by VB-Audio Software (www.vb-cable.com); its license allows apps to install it for users.
    /// </summary>
    static class CableInstaller
    {
        public const string HomeUrl = "https://vb-audio.com/Cable/";
        const string FallbackZip = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip";

        public static bool IsInstalled()
        {
            return WinMM.OutputNames().Any(n => n.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Runs on a background thread. Returns true when the setup program finished.</summary>
        public static bool Install(Action<string> progress)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            string dir = Path.Combine(Path.GetTempPath(), "SoundDeck-VBCABLE-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);

            progress("Finding the latest VB-CABLE…");
            string url = FindLatestZip() ?? FallbackZip;

            progress("Downloading VB-CABLE…");
            string zip = Path.Combine(dir, "vbcable.zip");
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "SoundDeck";
                wc.DownloadFile(url, zip);
            }

            progress("Unpacking…");
            string files = Path.Combine(dir, "files");
            ZipFile.ExtractToDirectory(zip, files);
            string setup = PickSetup(files);
            if (setup == null) throw new Exception("couldn't find the setup program in the download");

            progress("Checking the digital signature…");
            VerifySignature(setup);

            progress("Waiting for the VB-CABLE installer… (click \"Install Driver\")");
            ProcessStartInfo psi = new ProcessStartInfo(setup);
            psi.UseShellExecute = true;
            psi.Verb = "runas"; // the driver setup needs admin rights; Windows shows its UAC prompt
            psi.WorkingDirectory = Path.GetDirectoryName(setup);
            Process p;
            try { p = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { return false; } // user said no to UAC
            if (p != null) p.WaitForExit();
            try { Directory.Delete(dir, true); } catch { }
            return true;
        }

        static string FindLatestZip()
        {
            try
            {
                string html;
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "SoundDeck";
                    html = wc.DownloadString(HomeUrl);
                }
                return Regex.Matches(html, @"https://download\.vb-audio\.com/Download_CABLE/VBCABLE_Driver_Pack(\d+)\.zip")
                            .Cast<Match>()
                            .OrderByDescending(m => int.Parse(m.Groups[1].Value))
                            .Select(m => m.Value)
                            .FirstOrDefault();
            }
            catch { return null; }
        }

        static string PickSetup(string dir)
        {
            string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories)
                                     .Where(f => Path.GetFileName(f).IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0)
                                     .ToArray();
            string arch = (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
                        ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "").ToUpperInvariant();
            Func<string, string, bool> has = (f, s) => Path.GetFileName(f).IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
            if (arch == "ARM64") return exes.FirstOrDefault(f => has(f, "arm64")) ?? exes.FirstOrDefault(f => has(f, "x64"));
            if (Environment.Is64BitOperatingSystem) return exes.FirstOrDefault(f => has(f, "x64"));
            return exes.FirstOrDefault(f => !has(f, "x64") && !has(f, "arm"));
        }

        // ---- Authenticode check: valid signature, and signed by VB-Audio / Vincent Burel

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_FILE_INFO
        {
            public int cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public int dwProvFlags;
            public int dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        static void VerifySignature(string path)
        {
            WINTRUST_FILE_INFO fi = new WINTRUST_FILE_INFO();
            fi.cbStruct = Marshal.SizeOf(typeof(WINTRUST_FILE_INFO));
            fi.pcwszFilePath = path;
            IntPtr pFi = Marshal.AllocHGlobal(fi.cbStruct);
            Marshal.StructureToPtr(fi, pFi, false);
            WINTRUST_DATA wd = new WINTRUST_DATA();
            wd.cbStruct = Marshal.SizeOf(typeof(WINTRUST_DATA));
            wd.dwUIChoice = 2;      // WTD_UI_NONE
            wd.dwUnionChoice = 1;   // WTD_CHOICE_FILE
            wd.pFile = pFi;
            int result;
            try { result = WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), ref wd); }
            finally
            {
                Marshal.DestroyStructure(pFi, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFi);
            }
            if (result != 0) throw new Exception("the downloaded installer's signature isn't valid (0x" + result.ToString("X8") + "), so it wasn't run");

            string subject = X509Certificate.CreateFromSignedFile(path).Subject;
            if (subject.IndexOf("Burel", StringComparison.OrdinalIgnoreCase) < 0 && subject.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("the installer is signed by someone other than VB-Audio (" + subject + "), so it wasn't run");
        }
    }
}
