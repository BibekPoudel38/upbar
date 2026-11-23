using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.UI.Notifications;

// Live activities read from Windows (nothing is timed or stored by the bar itself):
//  - ClockApp: timers and alarms the Windows Clock app has scheduled (it books their "done" notification in advance).
//  - Downloads: browser downloads in progress, seen as their partial files in the Downloads folder.
static class ClockApp
{
    public class Item { public DateTime When; public bool Timer; public string Label; }
    const string Aumid = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";

    // Scheduled timers and alarms, soonest first. Read-only; empty when the Clock app has none (or isn't installed).
    public static List<Item> Upcoming()
    {
        var list = new List<Item>();
        try
        {
            foreach (var t in ToastNotificationManager.CreateToastNotifier(Aumid).GetScheduledToastNotifications())
            {
                var texts = t.Content.GetElementsByTagName("text");
                string first = texts.Length > 0 ? texts[0].InnerText : "", second = texts.Length > 1 ? texts[1].InnerText : "";
                bool timer = first.Contains("TimerDone");
                list.Add(new Item { When = t.DeliveryTime.LocalDateTime, Timer = timer, Label = timer ? second : first.StartsWith("ms-resource:") ? "Alarm" : first });
            }
        }
        catch (Exception) { } // no Clock app, or Windows refused: nothing to show
        list.Sort((a, b) => a.When.CompareTo(b.When));
        return list;
    }

    // What the chip should show now: a running timer first, else an alarm due within 12 hours (null: nothing).
    public static Item Pick(List<Item> items, DateTime now)
    {
        foreach (var i in items) if (i.Timer && i.When > now) return i;
        foreach (var i in items) if (!i.Timer && i.When > now && i.When - now < TimeSpan.FromHours(12)) return i;
        return null;
    }

    public static string Left(TimeSpan t) // 4:07, 1:02:30
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }
}

static class Downloads
{
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, int flags, IntPtr token, out IntPtr path);

    static readonly string[] Partial = { ".crdownload", ".part", ".partial", ".opdownload" }; // Chrome/Edge/Brave, Firefox, others, Opera
    static FileSystemWatcher watch;
    public static string Active;      // full path of the partial file being written (null: none)
    public static string Finished;    // a download that just completed
    public static event Action Changed; // raised on a worker thread

    public static bool IsPartial(string path) { foreach (var e in Partial) if (path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return true; return false; }

    // "file.zip.crdownload" -> "file.zip"; Chrome's placeholder "Unconfirmed 123.crdownload" -> "file"
    public static string Name(string partial)
    {
        string n = Path.GetFileName(partial);
        foreach (var e in Partial) if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) { n = n.Substring(0, n.Length - e.Length); break; }
        return n.StartsWith("Unconfirmed ") ? "file" : n;
    }

    public static long Size(string path) { try { return new FileInfo(path).Length; } catch (Exception) { return 0; } }

    public static void Start()
    {
        if (watch != null) return;
        string dir = null;
        IntPtr p;
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0) { dir = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); }
        if (dir == null || !Directory.Exists(dir)) return;
        try
        {
            watch = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size, IncludeSubdirectories = false };
            watch.Created += (s, e) => { if (IsPartial(e.FullPath)) { Active = e.FullPath; Raise(); } };
            watch.Changed += (s, e) => { if (IsPartial(e.FullPath)) { Active = e.FullPath; Raise(); } }; // grows: refresh the size shown
            watch.Deleted += (s, e) => { if (e.FullPath == Active) { Active = null; Raise(); } };     // cancelled
            watch.Renamed += (s, e) =>
            {
                if (IsPartial(e.FullPath)) { Active = e.FullPath; Raise(); return; }               // placeholder got its real name
                if (IsPartial(e.OldFullPath)) { if (e.OldFullPath == Active) Active = null; Finished = e.FullPath; Raise(); } // done
            };
            watch.EnableRaisingEvents = true;
        }
        catch (Exception) { watch = null; } // folder not watchable: no download chip
    }

    static int lastRaise;
    static void Raise()
    {
        int now = Environment.TickCount; // writes come in floods: tell the bar at most twice a second (the timer below catches the last one)
        if (unchecked(now - lastRaise) < 500 && Active != null) return;
        lastRaise = now;
        var c = Changed;
        if (c != null) c();
    }
}

// The next meeting from the Windows calendar (any account added to Windows): read once a minute, read-only.
static class Meetings
{
    public class Item { public string Subject, Link; public DateTime Start; }

    // A meeting that started up to 10 minutes ago or starts within 15 minutes (null: none).
    public static Item Next()
    {
        try
        {
            var store = Sys.Wait(Windows.ApplicationModel.Appointments.AppointmentManager.RequestStoreAsync(Windows.ApplicationModel.Appointments.AppointmentStoreAccessType.AllCalendarsReadOnly));
            var opts = new Windows.ApplicationModel.Appointments.FindAppointmentsOptions();
            foreach (var p in new[] { Windows.ApplicationModel.Appointments.AppointmentProperties.Subject, Windows.ApplicationModel.Appointments.AppointmentProperties.StartTime,
                                      Windows.ApplicationModel.Appointments.AppointmentProperties.AllDay, Windows.ApplicationModel.Appointments.AppointmentProperties.Location,
                                      Windows.ApplicationModel.Appointments.AppointmentProperties.OnlineMeetingLink })
                opts.FetchProperties.Add(p);
            Item best = null;
            foreach (var a in Sys.Wait(store.FindAppointmentsAsync(DateTimeOffset.Now.AddMinutes(-10), TimeSpan.FromMinutes(25), opts)))
            {
                if (a.AllDay) continue;
                var start = a.StartTime.LocalDateTime;
                if (best != null && start >= best.Start) continue;
                string link = null;
                try { link = TopBar.WebLink(a.OnlineMeetingLink); } catch (Exception) { }
                if (link == null) { var m = System.Text.RegularExpressions.Regex.Match(a.Location ?? "", @"https?://\S+"); if (m.Success) link = TopBar.WebLink(m.Value); } // Teams/Zoom links often sit in the location
                best = new Item { Subject = string.IsNullOrEmpty(a.Subject) ? "Meeting" : a.Subject, Start = start, Link = link };
            }
            return best;
        }
        catch (Exception) { return null; } // no calendar access or no calendar
    }

    public static string Label(Item m, DateTime now)
    {
        int mins = (int)Math.Ceiling((m.Start - now).TotalMinutes);
        string s = m.Subject.Length > 26 ? m.Subject.Substring(0, 25) + "…" : m.Subject;
        return s + (mins > 0 ? " in " + mins + " min" : " now") + (m.Link != null ? " · Join" : "");
    }
}

// Device and system health, all read-only and cheap; the bar asks on device changes and every few minutes.
static class Health
{
    public class Batt { public string Name; public int Pct; public bool Charging; }
    const string BattKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery
    static readonly Dictionary<string, Batt> lastBle = new Dictionary<string, Batt>(StringComparer.OrdinalIgnoreCase);

    // Connected Bluetooth devices and their battery (Pct = -1 when the device doesn't report one). ~0.9 s: worker thread only.
    // Only devices that are connected right now are included; Windows keeps cached battery values on disconnected PnP nodes.
    public static List<Batt> Batteries()
    {
        var list = new List<Batt>();
        try
        {
            var conn = new List<string>();
            foreach (var c in Sys.Wait(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected))))
            {
                string n = CleanName(c.Name);
                if (n.Length > 0 && !conn.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) conn.Add(n);
            }
            if (conn.Count > 0)
            {
                var all = Sys.Wait(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True", new[] { BattKey }, Windows.Devices.Enumeration.DeviceInformationKind.Device));
                foreach (var d in all)
                {
                    object v;
                    if (!d.Properties.TryGetValue(BattKey, out v) || v == null) continue;
                    string name = CleanName(d.Name);
                    if (conn.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)) && !list.Exists(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new Batt { Name = name, Pct = Convert.ToInt32(v) });
                }
                foreach (var name in conn)
                    if (!list.Exists(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                        list.Add(new Batt { Name = name, Pct = -1 });
            }
        }
        catch (Exception) { } // Bluetooth off or unavailable
        LowEnergy(list);
        return list;
    }

    // Phones plugged in over USB: an MTP (file transfer) or ADB (USB debugging) device. Name -> "File transfer" etc.
    // The model ("Pixel 9 Pro XL") is the USB bus-reported name; the driver's own name is generic ("Android Composite ADB Interface").
    public static Dictionary<string, string> UsbPhones()
    {
        var map = new Dictionary<string, string>();
        const string Bus = "{540b947e-8b40-45bc-a8a2-6a0b894cbda2} 4", Wpd = "{eec5ad98-8080-425f-922a-dabf3de3f69a}", Adb = "{3f966bd9-fa04-4ec5-991c-d326973b2b0e}";
        try
        {
            string aqs = "System.Devices.Present:=System.StructuredQueryType.Boolean#True AND (System.Devices.ClassGuid:=\"" + Wpd + "\" OR System.Devices.ClassGuid:=\"" + Adb + "\")";
            foreach (var d in Sys.Wait(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(aqs, new[] { Bus, "System.Devices.ClassGuid", "System.Devices.DeviceInstanceId" }, Windows.Devices.Enumeration.DeviceInformationKind.Device)))
            {
                object id, bus, cls;
                d.Properties.TryGetValue("System.Devices.DeviceInstanceId", out id);
                if (id == null || !id.ToString().StartsWith("USB", StringComparison.OrdinalIgnoreCase)) continue; // WPD also covers cameras over Bluetooth/network
                d.Properties.TryGetValue(Bus, out bus);
                d.Properties.TryGetValue("System.Devices.ClassGuid", out cls);
                string name = bus != null && bus.ToString().Length > 0 && bus.ToString() != "MTP" ? bus.ToString() : d.Name;
                bool mtp = cls != null && new Guid(cls.ToString()) == new Guid(Wpd);
                string was;
                map.TryGetValue(name, out was);
                map[name] = mtp || was == "File transfer" ? "File transfer" : "Charging · USB debugging";
            }
        }
        catch (Exception) { }
        return map;
    }

    // scrcpy (phone screen mirroring over ADB) on PATH or where winget/scoop put it; null when not installed.
    public static string Scrcpy()
    {
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(local, @"Microsoft\WinGet\Links"));
        dirs.Add(Path.Combine(home, @"scoop\shims"));
        foreach (var d in dirs)
        {
            try { string f = Path.Combine(d.Trim(), "scrcpy.exe"); if (d.Trim().Length > 0 && File.Exists(f)) return f; }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        try // winget's zip install: no Links alias, and PATH only updates for apps started after it
        {
            string pk = Path.Combine(local, @"Microsoft\WinGet\Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.GetDirectories(pk, "Genymobile.scrcpy*"))
                    foreach (var f in Directory.GetFiles(d, "scrcpy.exe", SearchOption.AllDirectories)) return f;
        }
        catch (Exception) { }
        return null;
    }

    // Phones ADB can reach right now (USB or wireless debugging), from `adb devices -l`. Worker thread only.
    public class AdbDev { public string Serial, Model; public bool Wireless; }
    public static List<AdbDev> Adb = new List<AdbDev>(); // latest reading; replaced whole, never edited

    // Re-reads Adb; true when the list changed. Uses the adb bundled with scrcpy so both talk to the same adb server.
    // shortcut: only when scrcpy is installed (no point otherwise); runs on device change and the minute tick.
    public static bool RefreshAdb()
    {
        string sc = Scrcpy(), adb = sc == null ? null : Path.Combine(Path.GetDirectoryName(sc), "adb.exe");
        var list = new List<AdbDev>();
        if (adb != null && File.Exists(adb))
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(adb, "devices -l") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
                {
                    string outp = p.StandardOutput.ReadToEnd(); // adb exits on its own once it has listed
                    if (!p.WaitForExit(5000)) p.Kill();
                    list = ParseAdb(outp);
                }
            }
            catch (Exception) { } // adb missing or refused
        string a = string.Join("|", Adb.ConvertAll(d => d.Serial)), b = string.Join("|", list.ConvertAll(d => d.Serial));
        Adb = list;
        return a != b;
    }

    // "46231FDAS002SW  device product:komodo model:Pixel_9_Pro_XL ..." -> serial + "Pixel 9 Pro XL"; skips unauthorized/offline.
    public static List<AdbDev> ParseAdb(string s)
    {
        var list = new List<AdbDev>();
        foreach (var line in s.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\S+)\s+device\b(?:.*\bmodel:(\S+))?");
            if (!m.Success) continue;
            string serial = m.Groups[1].Value;
            list.Add(new AdbDev { Serial = serial, Model = m.Groups[2].Success ? m.Groups[2].Value.Replace('_', ' ') : serial, Wireless = serial.Contains("._adb-tls-connect.") || serial.Contains(":") });
        }
        return list;
    }

    // Opens the phone's screen with scrcpy, over USB when it is plugged in, else wireless. False when no phone/scrcpy.
    public static bool Mirror(string model)
    {
        string sc = Scrcpy();
        var all = Adb;
        var d = all.Find(x => !x.Wireless && (model == null || x.Model == model)) ?? all.Find(x => model == null || x.Model == model);
        if (sc == null || d == null) return false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sc, "-s \"" + d.Serial + "\"") { UseShellExecute = false, CreateNoWindow = true }); return true; }
        catch (Exception) { return false; }
    }

    // Runs `adb -s serial shell cmd` and returns what it printed ("" on failure). Worker thread only.
    static string AdbShell(string serial, string cmd)
    {
        string sc = Scrcpy();
        if (sc == null) return "";
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(sc), "adb.exe"), "-s \"" + serial + "\" shell " + cmd) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000)) p.Kill();
                return o.Trim();
            }
        }
        catch (Exception) { return ""; }
    }

    // The phone in the bar: battery and signal over adb. Null while no phone is reachable.
    public class PhoneState { public string Serial, Model; public bool Wireless, Charging; public int Battery = -1, Signal = -1; }
    public static volatile PhoneState Phone;

    // Reads the first reachable phone, USB first (~1 s); returns whether anything shown changed. Worker thread only.
    // shortcut: one phone in the bar; with several the others still mirror from their own adb serials. Add pills per phone if asked.
    public static bool ReadPhone()
    {
        var all = Adb;
        var d = all.Find(x => !x.Wireless) ?? (all.Count > 0 ? all[0] : null);
        PhoneState s = null;
        if (d != null)
        {
            s = ParsePhone(AdbShell(d.Serial, "\"dumpsys battery; dumpsys telephony.registry 2>/dev/null | grep -m1 mSignalStrength\""));
            s.Serial = d.Serial; s.Model = d.Model; s.Wireless = d.Wireless;
        }
        var was = Phone;
        Phone = s;
        if (was == null || s == null) return was != s;
        return was.Serial != s.Serial || was.Battery != s.Battery || was.Charging != s.Charging || was.Signal != s.Signal;
    }

    public static PhoneState ParsePhone(string o)
    {
        var s = new PhoneState();
        var m = System.Text.RegularExpressions.Regex.Match(o, @"^\s*level:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (m.Success) s.Battery = Math.Min(100, int.Parse(m.Groups[1].Value));
        s.Charging = o.Contains("powered: true");
        foreach (System.Text.RegularExpressions.Match x in System.Text.RegularExpressions.Regex.Matches(o, @"\blevel\s*=\s*(\d)")) s.Signal = Math.Max(s.Signal, Math.Min(4, x.Groups[1].Value[0] - '0')); // 0..4, the best of LTE/NR/...
        return s;
    }

    // The phone's clearable notifications, newest first (ongoing and system ones left out). Worker thread only.
    public class PhoneNote { public string Pkg, Title, Text; public DateTime When; public bool Skip; }
    public static List<PhoneNote> LastNotes;
    public static string LastNotesSerial;
    public static List<PhoneNote> PhoneNotes(string serial)
    {
        // the full dump is over 1 MB: the phone keeps only the lines read here
        var list = ParseNotes(AdbShell(serial, @"""dumpsys notification --noredact | grep -E '^    NotificationRecord\(|^      flags=|android\.title=|android\.text=|^            when=|^  [A-Za-z]'"""));
        LastNotes = list;
        LastNotesSerial = serial;
        return list;
    }

    public static List<PhoneNote> ParseNotes(string o)
    {
        var list = new List<PhoneNote>();
        PhoneNote cur = null;
        foreach (var raw in o.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (l.StartsWith("    NotificationRecord("))
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"pkg=(\S+)");
                cur = new PhoneNote { Pkg = m.Groups[1].Value };
                cur.Skip = cur.Pkg == "android";
                list.Add(cur);
                continue;
            }
            if (l.StartsWith("  ") && l.Length > 2 && l[2] != ' ') { if (cur != null) break; continue; } // next section: the list is over
            if (cur == null) continue;
            string t = l.Trim();
            if (t.StartsWith("flags=")) { if (System.Text.RegularExpressions.Regex.IsMatch(t, @"ONGOING_EVENT|NO_CLEAR|FOREGROUND_SERVICE")) cur.Skip = true; }
            else if (t.StartsWith("when=") && cur.When == DateTime.MinValue)
            {
                foreach (var n in t.Substring(5).Split('/'))
                {
                    long ms;
                    if (long.TryParse(n, out ms) && ms > 0) { cur.When = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime(); break; }
                }
            }
            else if (t.StartsWith("android.title=") && cur.Title == null) cur.Title = NoteValue(t);
            else if (t.StartsWith("android.text=") && cur.Text == null) cur.Text = NoteValue(t);
        }
        list.RemoveAll(n => n.Skip || string.IsNullOrEmpty(n.Title));
        list.Sort((a, b) => b.When.CompareTo(a.When));
        return list;
    }

    static string NoteValue(string t) // "android.title=String (Hello)" -> "Hello"
    {
        var m = System.Text.RegularExpressions.Regex.Match(t, @"=\S+ \((.*)\)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Copies files to the phone's Download folder; returns null when done, else why not. Worker thread only.
    public static string Push(string serial, string[] files)
    {
        string sc = Scrcpy();
        if (sc == null) return "scrcpy (and its adb) isn't installed";
        var args = new System.Text.StringBuilder("-s \"" + serial + "\" push");
        foreach (var f in files) args.Append(" \"" + f + "\""); // Windows paths can't contain quotes
        args.Append(" /sdcard/Download/");
        try
        {
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(sc), "adb.exe"), args.ToString()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                p.OutputDataReceived += (s, e) => { }; // drained: progress lines would otherwise fill the pipe
                p.BeginOutputReadLine();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) return err.Trim().Length > 0 ? err.Trim().Split('\n')[0].Trim() : "adb push failed";
                // Update timestamp to now and notify Android MediaStore so the file sits at the top of Downloads / Recent / Photos.
                var scan = new System.Text.StringBuilder("\"");
                foreach (var f in files)
                {
                    string n = Path.GetFileName(f).Replace("\"", "").Replace("'", "'\\''");
                    scan.Append("touch '/sdcard/Download/").Append(n).Append("'; am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file:///sdcard/Download/").Append(n).Append("' >/dev/null 2>&1; ");
                }
                scan.Append("\"");
                AdbShell(serial, scan.ToString());
                return null;
            }
        }
        catch (Exception e) { return e.Message; }
    }

    public static void OpenDownloads(string serial)
    {
        AdbShell(serial, "am start -a android.intent.action.VIEW_DOWNLOADS >/dev/null 2>&1");
    }

    // An app's name from the phone's app list if it was read already, else from its package ("com.whatsapp" -> "Whatsapp").
    public static string AppName(string serial, string pkg)
    {
        if (Monitor.TryEnter(appCache)) // busy reading the list (~2 s): don't wait for it
        {
            try { List<KeyValuePair<string, string>> apps; if (appCache.TryGetValue(serial, out apps)) foreach (var a in apps) if (a.Value == pkg) return a.Key; }
            finally { Monitor.Exit(appCache); }
        }
        string last = pkg.Substring(pkg.LastIndexOf('.') + 1);
        return last.Length > 0 ? char.ToUpper(last[0]) + last.Substring(1) : pkg;
    }

    // The open desktop screen: its id on the phone (-1 until scrcpy reports it, or once closed) and which phone.
    public static volatile int DesktopDisplay = -1;
    public static string DesktopSerial;
    static System.Diagnostics.Process desktopProc;

    // The phone's launchable apps as (name, package), from scrcpy; ~2 s, so cached per phone. Worker thread only.
    static readonly Dictionary<string, List<KeyValuePair<string, string>>> appCache = new Dictionary<string, List<KeyValuePair<string, string>>>();
    public static List<KeyValuePair<string, string>> PhoneApps(string serial)
    {
        lock (appCache)
        {
            List<KeyValuePair<string, string>> list;
            if (appCache.TryGetValue(serial, out list)) return list;
            list = new List<KeyValuePair<string, string>>();
            string sc = Scrcpy();
            if (sc == null) return list;
            try
            {
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sc, "-s \"" + serial + "\" --list-apps") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.ErrorDataReceived += (s, e) => { }; // drain, unused
                    p.BeginErrorReadLine();
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) p.Kill();
                    list = ParseApps(o);
                }
            }
            catch (Exception) { }
            if (list.Count > 0) appCache[serial] = list; // an empty answer (phone busy) is asked again next time
            return list;
        }
    }

    // " * Chrome                         com.android.chrome" -> (Chrome, com.android.chrome), sorted by name.
    public static List<KeyValuePair<string, string>> ParseApps(string s)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var l in s.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(l.TrimEnd('\r'), @"^ [*-] (.+?)\s{2,}([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)$");
            if (m.Success) list.Add(new KeyValuePair<string, string>(m.Groups[1].Value.Trim(), m.Groups[2].Value));
        }
        list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    // Opens an app on the open desktop screen (moving it there if it's open on the phone). Worker thread only.
    public static bool LaunchOnDesktop(string package)
    {
        int disp = DesktopDisplay;
        string serial = DesktopSerial;
        return disp >= 0 && serial != null && LaunchApp(serial, package, disp);
    }

    // Starts an app's launcher activity on the phone itself (disp -1) or another of its screens. Worker thread only.
    public static bool LaunchApp(string serial, string package, int disp)
    {
        // the package goes into a phone shell command line: only a plain package name may get there
        if (!System.Text.RegularExpressions.Regex.IsMatch(package, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")) return false;
        string o = AdbShell(serial, "\"am start" + (disp >= 0 ? " --display " + disp : "") + " -n $(cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER " + package + " | tail -1) 2>&1\"");
        return o.Length > 0 && o.IndexOf("Error", StringComparison.Ordinal) < 0 && o.IndexOf("Exception", StringComparison.Ordinal) < 0;
    }

    // The phone's desktop on a second, PC-sized screen. The screen is one the phone makes itself (Developer options >
    // Simulate secondary displays) and scrcpy just shows it: Android only runs its desktop - taskbar launches, window title
    // bars - on screens it owns, not on scrcpy's own --new-display ones ("Failed to add desk").
    // Returns null once started, else why it can't (shown with setup steps). Worker thread only.
    public static string Desktop(string model, string size) // size: "WxH/dpi"
    {
        string sc = Scrcpy();
        if (sc == null) return "scrcpy isn't installed";
        RefreshAdb();
        var all = Adb;
        var d = all.Find(x => !x.Wireless && (model == null || x.Model == model)) ?? all.Find(x => model == null || x.Model == model);
        if (d == null) return "The phone isn't reachable over USB or wireless debugging";
        if (desktopProc != null && !desktopProc.HasExited && DesktopSerial == d.Serial) return null; // already open
        if (!System.Text.RegularExpressions.Regex.IsMatch(size, @"^\d+x\d+/\d+$")) return "Bad desktop size";
        int sdk;
        int.TryParse(AdbShell(d.Serial, "getprop ro.build.version.sdk"), out sdk);
        if (sdk > 0 && sdk < 29) return "Desktop mode needs Android 10 or newer";
        // Lets any app open as a window on another screen (else apps that don't opt in fall back to the phone); Android 16+
        // (SDK 36) also runs its desktop there - read at boot, so the first time needs a phone restart. All are Developer
        // options and can be switched off there.
        bool wasOn = AdbShell(d.Serial, "settings get global force_desktop_mode_on_external_displays") == "1";
        AdbShell(d.Serial, "settings put global enable_freeform_support 1; settings put global force_allow_on_external 1; settings put global force_resizable_activities 1" + (sdk >= 36 ? "; settings put global force_desktop_mode_on_external_displays 1" : ""));
        if (sdk >= 36 && !wasOn) return "Restart your phone once to finish turning on desktop mode";

        string serial = d.Serial;
        AdbShell(serial, "settings put global overlay_display_devices " + size);
        int disp = -1;
        for (int i = 0; i < 30 && disp < 0; i++) // the phone adds it within a second or so
        {
            var m = System.Text.RegularExpressions.Regex.Match(AdbShell(serial, "\"dumpsys display | grep -oE 'Overlay #[0-9]+., displayId [0-9]+' | head -1\""), @"displayId (\d+)");
            if (m.Success) disp = int.Parse(m.Groups[1].Value); else Thread.Sleep(200);
        }
        if (disp < 0) { AdbShell(serial, "settings put global overlay_display_devices null"); return "The phone didn't create the desktop screen"; }
        try
        {
            // uhid keyboard: the PC keyboard acts as a real one on the phone (shortcuts, typing in any app)
            var p = new System.Diagnostics.Process { StartInfo = new System.Diagnostics.ProcessStartInfo(sc, "-s \"" + serial + "\" --display-id=" + disp + " --keyboard=uhid --max-fps=60 --video-bit-rate=16M --window-title=\"" + d.Model + " desktop\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            DesktopSerial = serial;
            p.OutputDataReceived += (s, e) => { }; // drained: an unread pipe that fills up stalls scrcpy
            p.ErrorDataReceived += (s, e) => { };
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => { if (desktopProc == p) { DesktopDisplay = -1; AdbShell(serial, "settings put global overlay_display_devices null"); } }; // window closed: remove the screen
            p.Start();
            desktopProc = p;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            DesktopDisplay = disp;
            return null;
        }
        catch (Exception e) { AdbShell(serial, "settings put global overlay_display_devices null"); return "scrcpy couldn't start: " + e.Message; }
    }

    // Bluetooth LE devices (most mice and keyboards) report battery through the standard Battery service (0x180F)
    // instead; read it from each connected one. Devices without it (some keep it to their own app) still show as connected (Pct = -1).
    static void LowEnergy(List<Batt> list)
    {
        try
        {
            foreach (var d in Sys.Wait(Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected))))
            {
                string name = CleanName(d.Name);
                if (name.Length == 0) continue;
                var ex = list.Find(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
                if (ex != null && ex.Pct >= 0) continue;
                int pct = -1;
                try
                {
                    using (var dev = Within(Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(d.Id)))
                    {
                        if (dev == null || dev.ConnectionStatus != Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) continue;
                        var svc = Within(dev.GetGattServicesForUuidAsync(Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids.Battery, Windows.Devices.Bluetooth.BluetoothCacheMode.Cached));
                        if (svc != null && svc.Services.Count > 0)
                            using (var s = svc.Services[0])
                            {
                                var ch = Within(s.GetCharacteristicsForUuidAsync(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicUuids.BatteryLevel));
                                if (ch != null && ch.Characteristics.Count > 0)
                                {
                                    var r = Within(ch.Characteristics[0].ReadValueAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached)); // a fresh reading
                                    if (r != null && r.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success && r.Value.Length >= 1 && dev.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                                    {
                                        int v = Windows.Storage.Streams.DataReader.FromBuffer(r.Value).ReadByte();
                                        if (v <= 100) pct = v;
                                    }
                                }
                            }
                    }
                }
                catch (Exception) { } // the device went to sleep mid-read: next time
                bool chg = false;
                if (pct < 0) pct = HidBattery(d.Id, out chg);
                if (pct >= 0) lastBle[name] = new Batt { Name = name, Pct = pct, Charging = chg };
                else { Batt prev; if (lastBle.TryGetValue(name, out prev)) { pct = prev.Pct; chg = prev.Charging; } }
                if (ex != null) { ex.Pct = pct; ex.Charging = chg; }
                else list.Add(new Batt { Name = name, Pct = pct, Charging = chg });
            }
        }
        catch (Exception) { } // no Bluetooth LE
    }

    [StructLayout(LayoutKind.Sequential)] struct HID_GUID { public int a; public short b, c; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] d; }
    [StructLayout(LayoutKind.Sequential)] struct SP_DEV_IF_DATA { public int cbSize; public HID_GUID ClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] struct SP_DEV_IF_DETAIL { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Path; }
    [StructLayout(LayoutKind.Sequential)] struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputLen, OutputLen, FeatureLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out HID_GUID g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(Microsoft.Win32.SafeHandles.SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll")] static extern bool HidD_SetOutputReport(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int len);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref HID_GUID g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr d, IntPtr i, ref HID_GUID g, int idx, ref SP_DEV_IF_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr d, ref SP_DEV_IF_DATA data, ref SP_DEV_IF_DETAIL det, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr d);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CancelIo(Microsoft.Win32.SafeHandles.SafeFileHandle h);

    // Mice/peripherals on Compx/Beken BLE chips (e.g. ProtoArc) omit GATT 0x180F and report battery on vendor HID page 0xFF02 (OP 0x04).
    static int HidBattery(string bleId, out bool charging)
    {
        charging = false;
        var m = System.Text.RegularExpressions.Regex.Match(bleId ?? "", @"([0-9a-f]{2}(?::[0-9a-f]{2}){5})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return -1;
        string mac = m.Groups[1].Value.Replace(":", "");
        HID_GUID g;
        HidD_GetHidGuid(out g);
        IntPtr devs = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (devs == IntPtr.Zero || devs == (IntPtr)(-1)) return -1;
        try
        {
            var data = new SP_DEV_IF_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEV_IF_DATA)) };
            for (int i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref g, i, ref data); i++)
            {
                var det = new SP_DEV_IF_DETAIL { cbSize = IntPtr.Size == 8 ? 8 : 6 };
                int req;
                if (!SetupDiGetDeviceInterfaceDetail(devs, ref data, ref det, 1024, out req, IntPtr.Zero) || det.Path.IndexOf(mac, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool isVendor = false;
                using (var h0 = CreateFile(det.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                {
                    IntPtr pp;
                    if (!h0.IsInvalid && HidD_GetPreparsedData(h0, out pp))
                    {
                        HIDP_CAPS c;
                        if (HidP_GetCaps(pp, out c) >= 0 && c.UsagePage == 0xFF02 && c.InputLen == 17 && c.OutputLen == 17) isVendor = true;
                        HidD_FreePreparsedData(pp);
                    }
                }
                if (!isVendor) continue;
                using (var h = CreateFile(det.Path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero))
                {
                    if (h.IsInvalid) continue;
                    using (var fs = new FileStream(h, FileAccess.ReadWrite, 17, true))
                    {
                        var cmd = new byte[17];
                        cmd[0] = 0x08; cmd[1] = 0x04; cmd[16] = 0x49;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            var buf = new byte[17];
                            var ar = fs.BeginRead(buf, 0, 17, null, null); // queue read before sending so an immediate BLE reply is never missed
                            HidD_SetOutputReport(h, cmd, 17);
                            fs.Write(cmd, 0, 17);
                            if (ar.AsyncWaitHandle.WaitOne(500))
                            {
                                if (fs.EndRead(ar) >= 8 && buf[0] == 0x08 && buf[1] == 0x04 && buf[6] > 0 && buf[6] <= 100)
                                {
                                    charging = buf[7] != 0;
                                    return buf[6];
                                }
                            }
                            else { CancelIo(h); try { fs.EndRead(ar); } catch (Exception) { } }
                        }
                    }
                }
            }
        }
        catch (Exception) { }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return -1;
    }

    // Waits at most 3 s for a Bluetooth answer (a sleeping device may never reply), else gives up with null.
    static T Within<T>(Windows.Foundation.IAsyncOperation<T> op) where T : class
    {
        var until = DateTime.Now.AddSeconds(3);
        while (op.Status == Windows.Foundation.AsyncStatus.Started && DateTime.Now < until) Thread.Sleep(10);
        if (op.Status == Windows.Foundation.AsyncStatus.Completed) return op.GetResults();
        op.Cancel();
        return null;
    }

    // "Rockerz 512 ANC Hands-Free AG" -> "Rockerz 512 ANC"
    public static List<Batt> Last = new List<Batt>(); // latest reading, for the devices widget
    public static event Action Updated;                // UI thread, after the bar takes a new reading
    public static void Publish(List<Batt> l) { Last = l; var u = Updated; if (u != null) u(); }

    static readonly List<Windows.Devices.Enumeration.DeviceWatcher> btWatchers = new List<Windows.Devices.Enumeration.DeviceWatcher>();
    // Event-driven watcher (zero polling): fires onChange as soon as a Bluetooth or BLE device connects or disconnects.
    public static void WatchBluetooth(Action onChange)
    {
        try
        {
            foreach (var aqs in new[] {
                Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected),
                Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected) })
            {
                var w = Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(aqs);
                bool live = false;
                w.EnumerationCompleted += (s, e) => live = true;
                w.Added += (s, e) => { if (live) onChange(); };
                w.Removed += (s, e) => { if (live) onChange(); };
                w.Updated += (s, e) => { if (live) onChange(); };
                w.Start();
                btWatchers.Add(w);
            }
        }
        catch (Exception) { }
    }

    public static string CleanName(string n) { return System.Text.RegularExpressions.Regex.Replace(n ?? "", @"\s+(Hands-Free( AG| HF)?|Stereo|Avrcp Transport)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(); }

    // A fixed drive running out: under 10 % free and under 25 GB, or under 5 GB whatever its size (null: all fine).
    public static string LowDisk()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                long free = d.AvailableFreeSpace, gb = free >> 30;
                if (free < d.TotalSize / 10 && gb < 25 || gb < 5) return d.Name.TrimEnd('\\') + " " + gb + " GB left";
            }
            catch (IOException) { } // drive went away
        }
        return null;
    }

    public static bool RestartPending()
    {
        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")) return k != null;
    }

    // Windows doesn't tell apps who is capturing the screen (its usage records stopped updating), so this names a
    // known screen recorder that's running. shortcut: process names only; Teams/Zoom sharing can't be seen at all.
    static readonly string[] Recorders = { "obs64", "obs32", "obs", "Streamlabs OBS", "ffmpeg", "XSplit.Core", "ScreenRec", "bdcam", "CamtasiaRecorder" };
    public static string Recorder()
    {
        string found = null;
        foreach (var p in System.Diagnostics.Process.GetProcesses()) // one snapshot of running apps
        {
            if (found == null && Array.IndexOf(Recorders, p.ProcessName) >= 0) found = p.ProcessName;
            p.Dispose();
        }
        if (found == null) return null;
        return found.StartsWith("obs", StringComparison.OrdinalIgnoreCase) || found.StartsWith("Streamlabs") ? "OBS" : found == "ffmpeg" ? "Screen recorder" : found;
    }
}

// Windows virtual desktops, read from Explorer's registry record (no public API lists them). The bar is told the
// moment it changes (registry change notification), with no polling.
static class Desktops
{
    [DllImport("advapi32.dll")] static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, int filter, Microsoft.Win32.SafeHandles.SafeWaitHandle ev, bool async);
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    public static int Count = 1, Current;
    static byte[] Ids; // 16 bytes per desktop, in order
    public static event Action Changed; // worker thread
    static Microsoft.Win32.RegistryKey key;
    static readonly AutoResetEvent signal = new AutoResetEvent(false);

    public static void Start()
    {
        Read();
        try
        {
            key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key == null) return;
            Arm();
            ThreadPool.RegisterWaitForSingleObject(signal, (s, t) => { Read(); Arm(); var c = Changed; if (c != null) c(); }, null, -1, false);
        }
        catch (Exception) { } // no record: the switcher just stays hidden
    }

    static void Arm() { RegNotifyChangeKeyValue(key.Handle, false, 4, signal.SafeWaitHandle, true); } // REG_NOTIFY_CHANGE_LAST_SET

    static void Read()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
        {
            var ids = k == null ? null : k.GetValue("VirtualDesktopIDs") as byte[];
            var cur = k == null ? null : k.GetValue("CurrentVirtualDesktop") as byte[];
            Count = ids == null ? 1 : Math.Max(1, ids.Length / 16);
            Ids = ids;
            Current = Index(ids, cur);
        }
    }

    public static int Index(byte[] ids, byte[] cur)
    {
        if (ids == null || cur == null || cur.Length != 16) return 0;
        for (int i = 0; i + 16 <= ids.Length; i += 16)
        {
            bool same = true;
            for (int j = 0; j < 16 && same; j++) same = ids[i + j] == cur[j];
            if (same) return i / 16;
        }
        return 0;
    }

    // The app windows on desktop i (title -> handle), front to back. Windows on other desktops are shell-cloaked,
    // so visibility alone can't tell; the documented IVirtualDesktopManager says which desktop each one is on.
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr h, out int on);
        [PreserveSig] int GetWindowDesktopId(IntPtr h, out Guid id);
    }
    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")] class VirtualDesktopManager { }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    static IVirtualDesktopManager vdm;

    public static List<KeyValuePair<IntPtr, string>> Windows(int i)
    {
        var list = new List<KeyValuePair<IntPtr, string>>();
        var ids = Ids;
        if (ids == null || (i + 1) * 16 > ids.Length) return list;
        var g16 = new byte[16];
        Array.Copy(ids, i * 16, g16, 0, 16);
        var want = new Guid(g16);
        try { if (vdm == null) vdm = (IVirtualDesktopManager)new VirtualDesktopManager(); } catch (Exception) { return list; }
        var sb = new System.Text.StringBuilder(256);
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero || (GetWindowLong(h, -20) & 0x80) != 0) return true; // owned windows and tool windows aren't apps
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && (cloaked & ~2) != 0) return true; // cloaked by the app itself (suspended); 2 = shell, i.e. another desktop
            sb.Length = 0;
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            Guid d;
            if (vdm.GetWindowDesktopId(h, out d) == 0 && d == want && list.Count < 12) list.Add(new KeyValuePair<IntPtr, string>(h, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Brings a window forward; Windows switches to its desktop on the way, as Alt+Tab does.
    public static void Show(int i, IntPtr h) { if (i != Current) Go(i); SwitchToThisWindow(h, true); }

    // Moves to desktop i the way Windows does it: Ctrl+Win+Left/Right, one step per desktop.
    public static void Go(int i)
    {
        int d = i - Current;
        for (int n = 0; n < Math.Abs(d); n++) Native.Win(d > 0 ? System.Windows.Forms.Keys.Right : System.Windows.Forms.Keys.Left, true);
    }
}
