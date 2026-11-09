using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>Checks GitHub for a newer release and installs it in place (the exe is portable, so that's just a file swap).</summary>
    static class Updater
    {
        public const string Repo = "kmortensenf/SoundDeck";
        public const string ReleasesUrl = "https://github.com/" + Repo + "/releases";

        public class Release
        {
            public Version Version;
            public string Tag, ZipUrl, PageUrl;
        }

        /// <summary>This build's version. SOUNDDECK_VERSION lets a test copy pretend to be older.</summary>
        public static Version Current
        {
            get
            {
                Version v;
                string fake = Environment.GetEnvironmentVariable("SOUNDDECK_VERSION");
                if (fake != null && Version.TryParse(fake, out v)) return v;
                return Assembly.GetExecutingAssembly().GetName().Version;
            }
        }

        public static string Short(Version v) { return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build); }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "SoundDeck/" + Short(Current);
            return wc;
        }

        /// <summary>The latest release, or null if it isn't newer than this build. Throws on network errors.</summary>
        public static Release FindNewer()
        {
            string json;
            using (WebClient wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            }
            Release r = new Release();
            r.Tag = Field(json, "tag_name");
            r.PageUrl = Field(json, "html_url");
            r.ZipUrl = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"").Cast<Match>().Select(m => m.Groups[1].Value).FirstOrDefault();
            Version v;
            if (r.Tag == null || !Version.TryParse(r.Tag.TrimStart('v', 'V'), out v)) return null;
            r.Version = v;
            return Compare(v, Current) > 0 ? r : null;
        }

        static string Field(string json, string name)
        {
            Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        static int Compare(Version a, Version b)
        {
            return new Version(a.Major, a.Minor, Math.Max(0, a.Build)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build)));
        }

        /// <summary>Downloads the release and swaps it in next to the running exe. Call Restart() afterwards.</summary>
        public static void Install(Release r, Action<string> progress)
        {
            string prefix = "https://github.com/" + Repo + "/releases/download/";
            if (r.ZipUrl == null || !r.ZipUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new Exception("the release has no download from " + Repo);

            string exe = Application.ExecutablePath;
            string dir = Path.GetDirectoryName(exe);
            try { File.WriteAllText(Path.Combine(dir, ".sounddeck-write-test"), ""); File.Delete(Path.Combine(dir, ".sounddeck-write-test")); }
            catch { throw new Exception("SoundDeck's folder isn't writable. Download the new version from GitHub instead."); }

            string temp = Path.Combine(Path.GetTempPath(), "SoundDeck-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                progress("Downloading SoundDeck " + Short(r.Version) + "…");
                string zip = Path.Combine(temp, "update.zip");
                using (WebClient wc = Client()) wc.DownloadFile(r.ZipUrl, zip);

                progress("Installing…");
                string files = Path.Combine(temp, "files");
                ZipFile.ExtractToDirectory(zip, files);
                string newExe = Directory.GetFiles(files, "SoundDeck.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (newExe == null) throw new Exception("the download doesn't contain SoundDeck.exe");

                // A running exe can't be overwritten, but it can be renamed.
                string old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                try { File.Copy(newExe, exe); }
                catch { File.Move(old, exe); throw; }

                string readme = Path.Combine(Path.GetDirectoryName(newExe), "Read me.txt");
                if (File.Exists(readme)) try { File.Copy(readme, Path.Combine(dir, "Read me.txt"), true); } catch { }
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        /// <summary>Starts the new exe; it waits for this instance to exit before it takes over.</summary>
        public static void Restart()
        {
            Process.Start(Application.ExecutablePath, "--after-update");
        }

        /// <summary>Removes the previous version's exe left behind by an update.</summary>
        public static void CleanUp()
        {
            try
            {
                string old = Application.ExecutablePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { } // the old process may still be closing; next start tries again
        }
    }
}
