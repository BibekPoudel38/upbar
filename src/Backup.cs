using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

// One .zip with everything you set up: settings (topbar.ini), the app grid (apps.txt) and your notes with their
// images. Restoring only ever writes those known files back next to Upbar, so a tampered zip can't place files elsewhere.
static class Backup
{
    static string Home { get { return App.Data; } }

    // Entry names a backup may contain (and so the only files a restore will write).
    public static bool IsAllowed(string entry)
    {
        if (entry == null || entry.Contains("..") || entry.Contains("\\") || entry.Contains(":")) return false;
        return entry == "topbar.ini" || entry == "apps.txt" || entry == "clippins.txt"
            || Regex.IsMatch(entry, @"^notes/[^/]+\.md$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(entry, @"^notes/images/[^/]+\.(png|jpe?g|gif|bmp)$", RegexOptions.IgnoreCase);
    }

    public static int Save(string zipPath) // returns how many files went in
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int n = 0;
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "topbar.ini", "apps.txt", "clippins.txt" })
                if (File.Exists(Path.Combine(Home, name))) { zip.CreateEntryFromFile(Path.Combine(Home, name), name); n++; }
            string notes = Path.Combine(Home, "notes");
            if (Directory.Exists(notes))
                foreach (var f in Directory.GetFiles(notes, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(Home.Length).TrimStart('\\').Replace('\\', '/');
                    if (IsAllowed(rel)) { zip.CreateEntryFromFile(f, rel); n++; }
                }
        }
        return n;
    }

    // Writes back the allowed files from a backup (a note with the same name is replaced; others are kept).
    // The settings you had before are kept as topbar.ini.before-restore.
    public static int Restore(string zipPath)
    {
        int n = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 && e.FullName.EndsWith("/")) continue; // folder entry
                if (!IsAllowed(e.FullName) || e.Length > 50L * 1024 * 1024) continue; // unknown or oversized: skipped
                string dest = Path.Combine(Home, e.FullName.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (e.FullName == "topbar.ini" && File.Exists(dest)) File.Copy(dest, dest + ".before-restore", true);
                e.ExtractToFile(dest, true);
                n++;
            }
        return n;
    }
}

// Self-updater backed by GitHub Releases (https://github.com/BibekPoudel38/upbar/releases/latest).
static class Updater
{
    public const string Version = "1.1.0";
    public const string Repo = "BibekPoudel38/upbar";
    public static string Status = "Check for updates (v" + Version + ")";
    public static string LatestTag, DownloadUrl, ReleaseUrl;

    public static void Cleanup()
    {
        string old = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Upbar.exe.old");
        if (!File.Exists(old)) return;
        // Right after an update the old process is still exiting and holds its exe: keep trying for a few seconds.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch (IOException) { System.Threading.Thread.Sleep(500); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(500); } // a running exe reports as access denied
            }
        });
    }

    public static bool IsNewer(string remoteTag, string currentVersion)
    {
        Version r, c;
        if (!TryParse(remoteTag, out r) || !TryParse(currentVersion, out c)) return false;
        return r > c;
    }

    static bool TryParse(string s, out Version v)
    {
        v = null;
        if (string.IsNullOrEmpty(s)) return false;
        s = Regex.Replace(s.Trim(), @"^[vV]", "");
        int dash = s.IndexOf('-');
        if (dash > 0) s = s.Substring(0, dash);
        return System.Version.TryParse(s, out v);
    }

    public static void Check(Action onDone)
    {
        Status = "Checking GitHub for updates…";
        if (onDone != null) onDone();
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var web = new System.Net.WebClient { Encoding = System.Text.Encoding.UTF8 })
                {
                    web.Headers["User-Agent"] = "Upbar/" + Version;
                    string json = web.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
                    var obj = (System.Collections.Generic.Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
                    string tag = obj.ContainsKey("tag_name") ? obj["tag_name"] as string : null;
                    string html = obj.ContainsKey("html_url") ? obj["html_url"] as string : "https://github.com/" + Repo + "/releases";
                    string exeUrl = null;
                    if (obj.ContainsKey("assets") && obj["assets"] is object[])
                        foreach (var a in (object[])obj["assets"])
                        {
                            var asset = a as System.Collections.Generic.Dictionary<string, object>;
                            string name = asset != null && asset.ContainsKey("name") ? asset["name"] as string : "";
                            if (name != null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && asset.ContainsKey("browser_download_url"))
                            {
                                exeUrl = asset["browser_download_url"] as string;
                                break;
                            }
                        }
                    if (IsNewer(tag, Version))
                    {
                        LatestTag = tag;
                        DownloadUrl = exeUrl;
                        ReleaseUrl = html;
                        Status = "Update available: " + tag + (exeUrl != null ? " — click to install" : " — click to open release");
                    }
                    else
                    {
                        LatestTag = null;
                        Status = "Up to date (v" + Version + ")";
                    }
                }
            }
            catch (System.Net.WebException ex)
            {
                var resp = ex.Response as System.Net.HttpWebResponse;
                Status = resp != null && resp.StatusCode == System.Net.HttpStatusCode.NotFound ? "Up to date (v" + Version + ")" : "Couldn't check for updates";
            }
            catch (Exception) { Status = "Couldn't check for updates"; }
            if (onDone != null) onDone();
        });
    }

    public static void Install(Action onStatus)
    {
        if (App.Packaged) return; // the Store updates Store installs; swapping the exe would break the package
        if (DownloadUrl == null) { if (ReleaseUrl != null) Shell.Open(ReleaseUrl); return; }
        string url = DownloadUrl;
        DownloadUrl = null;
        Status = "Downloading " + LatestTag + "…";
        if (onStatus != null) onStatus();
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string cur = Path.Combine(dir, "Upbar.exe"), next = Path.Combine(dir, "Upbar.exe.new"), old = Path.Combine(dir, "Upbar.exe.old");
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var web = new System.Net.WebClient())
                {
                    web.Headers["User-Agent"] = "Upbar/" + Version;
                    web.DownloadFile(url, next);
                }
                if (new FileInfo(next).Length < 50 * 1024) { File.Delete(next); Status = "Download was incomplete"; if (onStatus != null) onStatus(); return; }
                if (File.Exists(old)) File.Delete(old);
                File.Move(cur, old);
                File.Move(next, cur);
                System.Diagnostics.Process.Start(cur);
                Environment.Exit(0);
            }
            catch (Exception e) { Status = "Update failed: " + e.Message; if (onStatus != null) onStatus(); }
        });
    }
}
