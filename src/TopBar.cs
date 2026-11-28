using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Periph { public string Icon, Name, Sub; public int Pct = -1; public bool Charging; public Action Open; }

// The app bar itself: reserves the top of the screen and paints its pills on acrylic.
class TopBar : Form
{
    class Btn
    {
        public readonly List<string> Icons = new List<string>();
        public readonly List<Color> Colors = new List<Color>();
        public readonly List<string> Labels = new List<string>();
        public string Text;
        public Color TextColor = Color.White;
        public Font Font;
        public Action Click;
        public int Pos; // 0 left, 1 center, 2 right, 3 hidden
        public RectangleF R;
        public int BatteryIcon = -1;
        public string Tip;
        public float HoverA, HoverVel, PressA, PressVel, DropA, DropVel; // 0..1, spring-eased while hovered/pressed/drop-targeted
        public Image Img;    // a picture before the icons (a notification's app logo)
        public float ShowA = -1, ShowVel; // 0..1 how far it has grown in (spring-eased); -1 before the first layout
        public float TargetX = -1, TargetW, XVel, WVel;
        public int LastPos = 3;  // where it sat while visible, so it can shrink away in place
    }

    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1;
    public readonly Config Cfg = Config.Load();
    public int BatteryPct = -1;
    public bool Charging;
    public List<Periph> Peripherals = new List<Periph>();
    readonly uint cbMsg = Native.RegisterWindowMessage("ClaudeTopBar");
    readonly float S;
    int barH;
    readonly Font iconFont = new Font("Segoe Fluent Icons", 10.5f);
    readonly Btn lights, activities, spaces, shelf, clip, notesBtn, media, search, hint, phone, devices, privacy, status, clock;
    NotesWindow notesWin;
    IntPtr maxWin; // the front-most maximized window: the traffic lights act on it
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    ControlCenter cc; // created once, warmed up after start-up, hidden between opens
    readonly HashSet<Glass> wired = new HashSet<Glass>();
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    string hintKey;                        // the suggestion on show (null: none)
    string tileText, tileSub;              // what the hover tile under the chip says
    Glass tile;
    readonly Timer tileWatch = new Timer { Interval = 120 }; // runs only while the tile is down: lifts it once the pointer leaves chip and tile
    Sys.Note note;                         // a notification that just arrived, shown in the chip for a few seconds
    string link;                           // a web link just copied: the chip offers to open it
    bool loudShown;                        // the sound meter's "loud" alert is on show
    DateTime loudAt;
    List<ClockApp.Item> clockItems = new List<ClockApp.Item>(); // Clock app timers/alarms, re-read every 15 s off the UI thread
    System.Threading.Timer clockPoll, meetingPoll;
    Meetings.Item meeting; // the next meeting, re-read every minute
    System.Threading.Timer healthPoll, recPoll;
    HashSet<string> battSeen;          // Bluetooth devices with a battery at the last reading (null before the first)
    List<Health.Batt> batts = new List<Health.Batt>();
    string hpChip, lowDisk, recorder;  // "Rockerz · 70%" just connected; a drive running low; a screen recorder running
    DateTime hpUntil;
    bool restartPending;
    string doneFile, lastDone;             // a download that just finished, shown until doneUntil
    DateTime doneUntil;
    readonly Timer live = new Timer { Interval = 1000 }; // ticks only while a countdown or a download is on show
    readonly Timer noteTimer = new Timer { Interval = 6000 }; // how long a passing chip (notification, copied link) stays
    readonly HashSet<string> dismissed = new HashSet<string>(); // right-clicked away; back once the situation clears
    readonly List<Btn> btns;
    readonly Timer minute = new Timer(), anim = new Timer(), eq = new Timer(), debounce = new Timer(), soon = new Timer(), maxCheck = new Timer(), clipRetry = new Timer();
    readonly List<IntPtr> hooks = new List<IntPtr>();
    Native.WinEventProc winProc; // kept in a field so the GC cannot collect the callback
    bool maxPresent;
    Btn hover, pressedBtn, dropBtn, popupOwner;
    float spaceCur = -1, spaceVel;
    float spotX = -1, spotW, spotA, spotXVel, spotWVel, spotAVel;
    readonly NotifyIcon tray;
    bool hidden;
    int fontNow;
    readonly ToolTip tips = new ToolTip();
    readonly Timer tipWait = new Timer { Interval = 500 };
    Glass popup;
    DateTime popupClosed;
    SettingsWindow settings;
    Bitmap buf;
    float plugT = 1, mediaTitleW;
    int eqPhase, net;
    bool ready, dropping;

    TopBar()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiY / 96;
        barH = (int)(Cfg.Height * S);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        activities = new Btn { Text = "Activities", Font = Theme.BarText, Click = () => Native.Win(Keys.Tab) };
        media = new Btn { Font = Theme.BarText };
        media.Click = () => TogglePopup(media, () => new MediaPopup(S));
        status = new Btn { Font = Theme.BarText };
        status.Click = () => TogglePopup(status, () => { if (cc == null || cc.IsDisposed) cc = new ControlCenter(this, S); return cc; }); // one panel, kept hidden between opens
        clock = new Btn { Font = Theme.Clock, Tip = "Notifications" };
        clock.Click = () => TogglePopup(clock, () => new NotifPopup(this, S));
        shelf = new Btn { Font = Theme.BarText };
        shelf.Click = () => TogglePopup(shelf, () => new ShelfPopup(S));
        clip = new Btn { Font = Theme.BarText };
        clip.Click = () => TogglePopup(clip, () => new ClipPopup(S));
        spaces = new Btn { Font = Theme.BarText, Tip = "Desktops: click one to switch" };
        lights = new Btn { Font = Theme.BarText, Tip = "Close, minimize or restore the maximized window" };
        notesBtn = new Btn { Font = Theme.BarText, Tip = "Notes (Alt+N)" };
        notesBtn.Icons.Add("\uE70B"); // quick note
        notesBtn.Colors.Add(Color.White);
        notesBtn.Click = () => OpenNotes(false);
        NoteWidget.OpenEditor = p => { OpenNotes(false); if (p != null) notesWin.OpenNote(p); };
        search = new Btn { Text = "Search", Font = Theme.BarText, TextColor = Color.FromArgb(185, 255, 255, 255) };
        search.Icons.Add(G.Search);
        search.Colors.Add(Color.FromArgb(185, 255, 255, 255));
        search.Click = () => TogglePopup(search, () => new SearchPopup(Cfg, S));
        privacy = new Btn { Font = Theme.BarText, Tip = "In use: click to see which apps" };
        privacy.Click = ShowPrivacy;
        phone = new Btn { Font = Theme.BarText, Tip = "Phone: notifications, screen and files" };
        phone.Click = ShowPhone;
        devices = new Btn { Font = Theme.BarText, Tip = "Connected devices" };
        devices.Click = ShowDevices;
        hint = new Btn { Font = Theme.BarText, Tip = "Suggestion: click to do it, right-click to dismiss" };
        hint.Click = () => { if (hintKey == "note") { var n = note; note = null; noteTimer.Stop(); Sys.OpenNote(n); RefreshStatus(); } else if (hintKey == "link") { Shell.Open(link); link = null; noteTimer.Stop(); RefreshStatus(); }
            else if (hintKey == "hp" || hintKey == "hplow") Shell.Open("ms-settings:bluetooth");
            else if (hintKey == "update") Shell.Open("ms-settings:windowsupdate");
            else if (hintKey == "disk") Shell.Open("ms-settings:storagesense");
            else if (hintKey == "rec") { dismissed.Add("rec"); RefreshStatus(); }
            else if (hintKey == "meeting") Shell.Open(meeting.Link ?? "outlookcal:"); // join, or open the calendar
            else if (hintKey == "loud") status.Click(); // the control center, where the meter lives
            else if (hintKey == "clock") Shell.Open("ms-clock:");
            else if (hintKey == "download" && Downloads.Active != null) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(Downloads.Active) + "\"");
            else if (hintKey == "done") { Process.Start("explorer.exe", "/select,\"" + doneFile + "\""); doneUntil = DateTime.MinValue; RefreshStatus(); } // shows the file; never runs it
            else DoHint(hintKey); };
        noteTimer.Tick += (s, e) => { noteTimer.Stop(); note = null; link = null; RefreshStatus(); };
        live.Tick += (s, e) => RefreshStatus();
        DbMeter.Alert = Cfg.DbAlert;
        DbMeter.Changed += () => { if (DbMeter.Loud != loudShown || DbMeter.Loud && (DateTime.Now - loudAt).TotalSeconds >= 1) { loudShown = DbMeter.Loud; loudAt = DateTime.Now; RefreshStatus(); } }; // chip: on/off at once, the number once a second
        Sys.Arrived += n => { try { BeginInvoke((Action)(() => { if (Sys.FocusActive) return; note = n; link = null; noteTimer.Stop(); noteTimer.Interval = 6000; noteTimer.Start(); RefreshStatus(); })); } catch (InvalidOperationException) { } };
        btns = new List<Btn> { lights, activities, spaces, shelf, clip, notesBtn, media, search, hint, phone, devices, privacy, status, clock }; // visual order within each side

        // Tray icon (Windows files it under "Show hidden icons"): brings the bar back after a double-click hid it.
        tray = new NotifyIcon { Text = "UpBar", Icon = TrayIcon(), Visible = true };
        tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) SetHidden(!hidden); else TrayMenu(); };
        activities.Tip = "Activities (Task view)";
        shelf.Tip = "File shelf: drop files here";
        clip.Tip = "Clipboard history";
        media.Tip = "Now playing";
        search.Tip = "Search the web or ask AI";
        status.Tip = "Quick settings";
        tipWait.Tick += (s, e) => { tipWait.Stop(); if (hover != null && hover.Tip != null) tips.Show(hover.Tip, this, (int)hover.R.X, Height + (int)(4 * S), 4000); };

        // Drag & drop onto bar pills: File shelf, Clipboard, Notes, Search bar, or Phone.
        AllowDrop = true;
        DragEnter += OnBarDrag;
        DragOver += OnBarDrag;
        DragLeave += (s, e) => { dropping = false; dropBtn = null; KickBar(); Invalidate(); };
        DragDrop += OnBarDrop;

        minute.Tick += (s, e) => { Rescan(); RefreshStatus(); };
        anim.Interval = 15;
        anim.Tick += (s, e) => { anim.Stop(); KickBar(); };
        eq.Interval = 150; // visualizer: ~7 fps, only while something plays
        eq.Tick += (s, e) => { eqPhase++; Invalidate(); };
        debounce.Interval = 400;
        debounce.Tick += (s, e) => { debounce.Stop(); Audio.Reset(); Rescan(); RefreshStatus(); System.Threading.ThreadPool.QueueUserWorkItem(_ => CheckHealth()); }; // a device came or went: batteries too
        soon.Interval = 60; // volume drags and media timeline updates arrive in bursts: repaint at most ~16 times a second
        soon.Tick += (s, e) => { soon.Stop(); RefreshStatus(); };

        Action later = () => Ui(() => soon.Start());
        Audio.Changed += later;
        Media.Changed += later;
        Shelf.Changed += () => Ui(RefreshStatus);
        Privacy.Changed += () => Ui(RefreshStatus);
        Privacy.Start();
        Clip.Changed += () => Ui(RefreshStatus);
        maxCheck.Interval = 150; // window events come in bursts; check maximized state once they settle
        maxCheck.Tick += (s, e) => { maxCheck.Stop(); FullscreenCheck(); var top = Native.TopMaximizedOnPrimary(Handle); bool m = top != IntPtr.Zero; if (m != maxPresent) { maxPresent = m; ApplyShape(); } if (top != maxWin) { maxWin = top; RefreshStatus(); } SampleApp(); };
        resample.Tick += (s, e) => SampleApp(); // the app may change colour (a new tab, dark mode) without any window event
        clipRetry.Interval = 120; // another app still holds the clipboard open
        clipRetry.Tick += (s, e) => { clipRetry.Stop(); Clip.Capture(); };
        NetworkChange.NetworkAddressChanged += (s, e) => Ui(() => { Rescan(); RefreshStatus(); });
        Cfg.Changed += () => { barH = (int)(Cfg.Height * S); if (Cfg.ShowMedia) Media.Start(); if (Cfg.ShowDevices && Peripherals.Count == 0) Peripherals = FindPeripherals(); DockIfChanged(); RefreshStatus(); };

        Glass.Style = Cfg.GlassStyle;
        fontNow = Cfg.Font;
        Theme.SetFont(fontNow == 1);
        Theme.AccentOverride = Cfg.AccentColor;
        Theme.Reload(); // Theme may have initialised before the saved style was known
        if (Cfg.CursorStyle > 0) CustomCursors.Apply(Cfg.CursorStyle, Theme.Accent);
        foreach (var b in btns) b.Font = b == clock ? Theme.Clock : Theme.BarText;
        Cfg.Changed += () =>
        {
            if (Glass.Style != Cfg.GlassStyle || fontNow != Cfg.Font || Theme.AccentOverride != Cfg.AccentColor)
            {
                Glass.Style = Cfg.GlassStyle;
                Theme.AccentOverride = Cfg.AccentColor;
                if (fontNow != Cfg.Font) { fontNow = Cfg.Font; Theme.SetFont(fontNow == 1); foreach (var b in btns) b.Font = b == clock ? Theme.Clock : Theme.BarText; }
                Theme.Reload();
                if (Cfg.CursorStyle >= 2) CustomCursors.Apply(Cfg.CursorStyle, Theme.Accent);
                if (popup != null) popup.ApplyTheme();
                if (cc != null && popup != cc) { cc.Close(); cc = null; } // hidden panel: rebuilt with the new look on next open
                if (settings != null) settings.ApplyTheme();
                ThemeWidgets();
            }
            Hooks(Cfg.Floating || Cfg.ClearBar || Cfg.MatchApp || Cfg.WinControls);
            ApplyShape();
            SyncWidgets();
            VolumeKeys.VolStyle = Cfg.OsdStyle; VolumeKeys.BrightStyle = Cfg.BrightStyle; VolumeKeys.Pos = Cfg.OsdPos; VolumeKeys.Enable(Cfg.VolumeOsd, S, this);
        };
        Audio.Reset();
        if (Cfg.ShowMedia) Media.Start();
        Rescan();
        RefreshStatus();
        ready = true;
        minute.Start();
    }

    void Ui(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { } }

    // Which suggestion fits the moment, most urgent first (null: nothing worth saying).
    public static string Suggest(int batteryPct, bool charging, bool muted, bool playing, bool offline)
    {
        if (batteryPct >= 0 && batteryPct < 30 && !charging) return "charge";
        if (muted && playing) return "muted";   // something is playing but nothing can be heard
        if (offline) return "offline";
        if (charging && batteryPct >= 100) return "full";
        return null;
    }

    // Opens the notes editor (created on demand, freed when closed). From the hotkey while it's already in front: a new note.
    void OpenNotes(bool fromHotkey)
    {
        if (notesWin == null || notesWin.IsDisposed) { notesWin = new NotesWindow(S); notesWin.FormClosed += (s, e) => { notesWin = null; Native.Trim(); }; }
        else if (fromHotkey && notesWin.ContainsFocus) { notesWin.NewNote(); return; }
        notesWin.Show();
        if (notesWin.WindowState == FormWindowState.Minimized) notesWin.WindowState = FormWindowState.Normal;
        notesWin.Activate();
    }

    // Worker thread: reads device batteries, disk space and update state, then hands them to the UI thread.
    void CheckHealth()
    {
        var b = Health.Batteries();
        GC.Collect(); // the scan builds an object for each of a few hundred devices: hand that memory back now, not later
        Native.Trim();
        string disk = Health.LowDisk();
        bool restart = Health.RestartPending();
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (battSeen != null)
                    foreach (var x in b) if (!battSeen.Contains(x.Name)) { hpChip = x.Name + (x.Pct >= 0 ? " · " + x.Pct + "%" : " · Connected"); hpUntil = DateTime.Now.AddSeconds(6); } // just connected
                battSeen = new HashSet<string>(b.ConvertAll(x => x.Name));
                batts = b; lowDisk = disk; restartPending = restart;
                Peripherals = FindPeripherals();
                Health.Publish(b);
                RefreshStatus();
            }));
        }
        catch (InvalidOperationException) { } // the bar is closing
    }

    static string Short(string s) { return s.Length > 28 ? s.Substring(0, 27) + "…" : s; }

    // Copied text that is a single web address: offer to open it for a few seconds.
    void CopiedLink()
    {
        string text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (ExternalException) { return; } // clipboard busy: not worth retrying for a hint
        string url = WebLink(text);
        if (url == null) return;
        link = url;
        note = null;
        noteTimer.Stop();
        noteTimer.Interval = 8000;
        noteTimer.Start();
        RefreshStatus();
    }

    // Only plain http(s) addresses: this string is handed to the shell, so it must never name a program or file.
    public static string WebLink(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = text.Trim();
        Uri u;
        if (text.Length > 2048 || text.IndexOfAny(new[] { ' ', '\n', '\r', '\t', '"' }) >= 0 || !Uri.TryCreate(text, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
        return u.AbsoluteUri;
    }

    static string LinkHost(string url) { Uri u; return Uri.TryCreate(url, UriKind.Absolute, out u) ? (u.Host.StartsWith("www.") ? u.Host.Substring(4) : u.Host) : ""; }

    void DoHint(string key)
    {
        if (key == "charge") Shell.Open("ms-settings:batterysaver");
        else if (key == "muted" && Audio.Out != null) Audio.Out.Muted = false;
        else if (key == "offline") Shell.Open("ms-settings:network-wifi");
        else if (key == "full") dismissed.Add(key); // nothing to do: acknowledging it is enough
        RefreshStatus();
    }

    // Battery warning colour: orange below 30 %, red below 20 %; Empty when charging or fine.
    public static Color LowBattery(int pct, bool charging, bool light)
    {
        if (charging || pct < 0 || pct >= 30) return Color.Empty;
        if (pct < 20) return light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 99);
        return light ? Color.FromArgb(194, 94, 0) : Color.FromArgb(255, 152, 0);
    }

    public void RefreshStatus()
    {
        var now = DateTime.Now;
        clock.Text = (Cfg.ShowDate ? now.ToString("ddd MMM d   ") : "") + now.ToString(Cfg.Clock24 ? "HH:mm" : "h:mm tt");

        var p = SystemInformation.PowerStatus;
        bool hasBatt = !p.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        bool chg = hasBatt && p.PowerLineStatus == PowerLineStatus.Online;
        if (ready && chg && !Charging) { plugT = 0; anim.Start(); } // just plugged in
        BatteryPct = hasBatt ? Math.Max(0, Math.Min(100, (int)Math.Round(p.BatteryLifePercent * 100))) : -1;
        Charging = chg;

        status.Icons.Clear();
        status.Colors.Clear();
        status.BatteryIcon = -1;
        if (Sys.FocusActive) AddIcon(G.Sleep, Color.White); // do not disturb
        AddIcon(net == 2 ? G.Ethernet : G.Wifi, net == 0 ? Color.FromArgb(110, 255, 255, 255) : Color.White);
        AddIcon(Audio.Out == null ? G.Mute : G.Volume(Audio.Out.Volume, Audio.Out.Muted), Color.White);
        var low = LowBattery(BatteryPct, chg, false);
        if (hasBatt) { status.BatteryIcon = status.Icons.Count; AddIcon(G.Battery(BatteryPct, chg), chg ? Theme.BarGreen : low.IsEmpty ? Color.White : low); }
        status.Text = hasBatt && Cfg.ShowPercent ? BatteryPct + "%" : null;
        status.TextColor = chg ? Theme.BarGreen : low.IsEmpty ? Color.White : low;

        media.Pos = Media.Has && Cfg.ShowMedia ? (Cfg.ActivitiesPos == 3 ? 0 : Cfg.ActivitiesPos) : 3; // sits right after Activities
        media.Text = Media.Title + (Media.Artist.Length > 0 ? "  ·  " + Media.Artist : "");
        bool eqOn = media.Pos < 3 && Media.Playing && Cfg.MediaAnim;
        if (eq.Enabled != eqOn) eq.Enabled = eqOn;
        shelf.Pos = Cfg.ShowShelf ? (Cfg.ActivitiesPos == 3 ? 0 : Cfg.ActivitiesPos) : 3;
        shelf.Icons.Clear();
        shelf.Colors.Clear();
        shelf.Icons.Add(G.Folder);
        shelf.Colors.Add(Color.White);
        shelf.Text = Shelf.Items.Count > 0 ? Shelf.Items.Count.ToString() : null;
        clip.Pos = Cfg.ShowClipboard ? (Cfg.ActivitiesPos == 3 ? 0 : Cfg.ActivitiesPos) : 3;
        if (clip.Icons.Count == 0) { clip.Icons.Add(G.Paste); clip.Colors.Add(Color.White); }
        lights.Pos = Cfg.WinControls && maxWin != IntPtr.Zero ? 0 : 3; // only while a window is maximized
        spaces.Pos = Desktops.Count >= 2 ? (Cfg.ActivitiesPos == 3 ? 0 : Cfg.ActivitiesPos) : 3; // shown once there is more than one desktop
        notesBtn.Pos = Cfg.ShowNotes ? (Cfg.ActivitiesPos == 3 ? 0 : Cfg.ActivitiesPos) : 3; // right after the clipboard
        search.Pos = Cfg.ShowSearch ? 1 : 3;
        activities.Pos = Cfg.ActivitiesPos;
        status.Pos = Cfg.StatusPos;
        var ps = Health.Phone; // over adb: battery and signal; a USB phone without debugging shows just its icon
        phone.Icons.Clear();
        phone.Colors.Clear();
        phone.Text = null;
        if (ps != null || usbPhone != null)
        {
            phone.Icons.Add(G.Phone); phone.Colors.Add(Color.White);
            if (ps != null && ps.Signal >= 0) { phone.Icons.Add(((char)(0xE86C + ps.Signal)).ToString()); phone.Colors.Add(Color.White); } // 1..5 bars for level 0..4
            if (ps != null && ps.Battery >= 0)
            {
                phone.Icons.Add(G.Battery(ps.Battery, ps.Charging)); phone.Colors.Add(ps.Charging ? Theme.BarGreen : ps.Battery <= 15 ? Color.FromArgb(255, 180, 171) : Color.White);
                phone.Text = ps.Battery + "%";
            }
        }
        phone.Pos = ps != null || usbPhone != null ? Cfg.StatusPos : 3;
        devices.Icons.Clear();
        devices.Colors.Clear();
        devices.Labels.Clear();
        foreach (var d in Peripherals)
        {
            if (devices.Icons.Count >= 3) break;
            var lowD = LowBattery(d.Pct, d.Charging, false);
            Color dc = d.Charging ? Theme.BarGreen : !lowD.IsEmpty ? lowD : Color.White;
            devices.Icons.Add(d.Icon);
            devices.Colors.Add(dc);
            devices.Labels.Add(d.Pct >= 0 ? (d.Charging ? "⚡" : "") + d.Pct + "%" : null);
        }
        devices.Text = Peripherals.Count > 3 ? "+" + (Peripherals.Count - 3) : null; // more than 3: collapsed, click lists them all
        devices.Tip = Peripherals.Count > 0 ? string.Join("\n", Peripherals.ConvertAll(d => d.Name + " · " + d.Sub)) : "Connected devices";
        devices.Pos = Cfg.ShowDevices && Peripherals.Count > 0 ? Cfg.StatusPos : 3;
        privacy.Icons.Clear();
        privacy.Colors.Clear();
        var ink = Color.FromArgb(12, 40, 22); // dark icons on the green pill, like Android
        if (Privacy.Cam) { privacy.Icons.Add(G.Camera); privacy.Colors.Add(ink); }
        if (Privacy.Mic) { privacy.Icons.Add(G.Mic); privacy.Colors.Add(ink); }
        privacy.Pos = privacy.Icons.Count > 0 ? Cfg.StatusPos : 3;
        clock.Pos = Cfg.ClockPos;

        // Smart suggestion: at most one, the most useful for what's happening right now.
        if (Downloads.Finished != null && Downloads.Finished != lastDone) { lastDone = doneFile = Downloads.Finished; doneUntil = now.AddSeconds(8); }
        var due = ClockApp.Pick(clockItems, now);
        hintKey = DbMeter.Loud ? "loud" : note != null ? "note" : link != null ? "link" : meeting != null && meeting.Start > now.AddMinutes(-10) ? "meeting" : recorder != null ? "rec" : due != null ? "clock" : Downloads.Active != null ? "download" : now < doneUntil ? "done" : now < hpUntil ? "hp" : batts.Exists(b => b.Pct >= 0 && b.Pct <= 15) ? "hplow" : restartPending ? "update" : lowDisk != null ? "disk" : Cfg.SmartHints ? Suggest(hasBatt ? BatteryPct : -1, chg, Audio.Out != null && Audio.Out.Muted, Media.Playing, net == 0) : null; // a fresh notification goes first
        if (hintKey != "note" && hintKey != "link" && hintKey != "done") dismissed.RemoveWhere(k => k != hintKey); // a dismissed suggestion may return once its situation has cleared
        if (hintKey != null && dismissed.Contains(hintKey)) hintKey = null;
        hint.Icons.Clear();
        hint.Colors.Clear();
        hint.Pos = hintKey == null ? 3 : Cfg.StatusPos;
        tileText = tileSub = null;
        if (hintKey != null)
        {
            Color c = hintKey == "loud" || hintKey == "rec" ? LowBattery(5, false, false) : hintKey == "hplow" || hintKey == "disk" ? LowBattery(25, false, false) : hintKey == "full" ? Theme.BarGreen : hintKey == "charge" ? LowBattery(BatteryPct, false, false) : Color.White;
            hint.Img = hintKey == "note" ? note.Logo : null; // the app's own logo; the bell stands in when there is none
            if (hint.Img == null) hint.Icons.Add(hintKey == "rec" ? "\uE7C8" : hintKey == "hp" || hintKey == "hplow" ? G.Headphones : hintKey == "update" ? "\uE895" : hintKey == "disk" ? "\uEDA2" : hintKey == "meeting" ? "\uE787" : hintKey == "loud" ? "\uE767" : hintKey == "clock" ? (due.Timer ? "\uE916" : G.ClockIcon) : hintKey == "download" ? "\uE896" : hintKey == "done" ? "\uE73E" : hintKey == "link" ? "\uE71B" : hintKey == "note" ? "\uEA8F" : hintKey == "charge" || hintKey == "full" ? G.Battery(BatteryPct, true) : hintKey == "muted" ? G.Mute : G.Wifi); // EA8F: bell
            if (hint.Img == null) hint.Colors.Add(c);
            string title = note == null ? "" : note.Title.Length > 42 ? note.Title.Substring(0, 41) + "…" : note.Title; // just the title, kept short
            string dl = Downloads.Active == null ? "" : Short(Downloads.Name(Downloads.Active)) + " · " + (Downloads.Size(Downloads.Active) / 1048576.0).ToString("0.#") + " MB";
            var lowB = batts.Find(b => b.Pct >= 0 && b.Pct <= 15);
            hint.Text = hintKey == "rec" ? recorder + " · screen recorder open" : hintKey == "hp" ? hpChip : hintKey == "hplow" ? lowB.Name + " battery " + lowB.Pct + "%" : hintKey == "update" ? "Restart to finish updates" : hintKey == "disk" ? lowDisk + " · Clean up"
                      : hintKey == "meeting" ? Meetings.Label(meeting, now) : hintKey == "loud" ? "Loud · " + (int)Math.Round(DbMeter.Level) + " dB" : hintKey == "clock" ? (due.Timer ? ClockApp.Left(due.When - now) + (due.Label.Length > 0 ? " · " + due.Label : "") : "Alarm " + due.When.ToString("t"))
                      : hintKey == "download" ? "Downloading " + dl : hintKey == "done" ? "Downloaded " + Short(Path.GetFileName(doneFile)) + " · Show"
                      : hintKey == "link" ? "Open link · " + LinkHost(link) : hintKey == "note" ? title : hintKey == "charge" ? "Connect charger" : hintKey == "full" ? "Fully charged, you can unplug" : hintKey == "muted" ? "Sound is muted" : "No internet";
            hint.TextColor = c;
            // The tile that drops down while the chip is hovered: the uncut text, then the note's body or what a click does.
            string full = hintKey == "note" ? note.Title : hintKey == "link" ? "Link · " + link : hintKey == "done" ? "Downloaded " + Path.GetFileName(doneFile)
                        : hintKey == "download" ? "Downloading " + Downloads.Name(Downloads.Active) + " · " + (Downloads.Size(Downloads.Active) / 1048576.0).ToString("0.#") + " MB" : hintKey == "disk" ? lowDisk : hint.Text;
            string act = hintKey == "note" || hintKey == "link" ? "Open" : hintKey == "done" ? "Show in folder" : hintKey == "download" ? "Show folder" : hintKey == "rec" || hintKey == "full" ? "Click to hide"
                       : hintKey == "hp" || hintKey == "hplow" ? "Bluetooth settings" : hintKey == "update" ? "Open Windows Update" : hintKey == "disk" ? "Open storage settings"
                       : hintKey == "meeting" ? (meeting.Link != null ? "Join" : "Open calendar") : hintKey == "loud" ? "Sound meter" : hintKey == "clock" ? "Open Clock"
                       : hintKey == "charge" ? "Battery settings" : hintKey == "muted" ? "Click to unmute" : hintKey == "offline" ? "Wi-Fi settings" : null;
            tileText = Cut(full, 80);
            tileSub = hintKey == "note" && !string.IsNullOrEmpty(note.Body) ? Cut(note.Body, 80) : act;
        }

        bool tick = hintKey == "clock" && due.Timer || hintKey == "download" || now < doneUntil; // a live countdown, a growing download, or a "done" to expire
        if (live.Enabled != tick) live.Enabled = tick;

        LayoutBtns();
        KickBar();
        Invalidate();
        var cc = popup as ControlCenter;
        if (cc != null) cc.Sync();
        minute.Interval = 60000 - now.Second * 1000 - now.Millisecond + 50; // on the minute
    }

    // Slow probes (network adapters, drives): only on network/device change and the minute tick, never per volume step.
    void Rescan()
    {
        net = NetKind();
        Peripherals = FindPeripherals();
        if (adbBusy) return;
        adbBusy = true; // adb takes ~100 ms+ (more if it must start its server): ask on a worker, list again only if it changed
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            bool changed = Health.RefreshAdb() | Health.ReadPhone();
            var ps = Health.Phone;
            if (ps != null) Health.PhoneApps(ps.Serial); // cached once per phone so notification app names are ready
            adbBusy = false;
            if (changed) Ui(RefreshStatus);
        }); // every minute: battery and signal
    }
    volatile bool adbBusy;

    void AddIcon(string g, Color c) { status.Icons.Add(g); status.Colors.Add(c); }

    // Headphones on the default output, connected Bluetooth devices, game controllers, and removable USB drives.
    List<Periph> FindPeripherals()
    {
        usbPhone = null; // phones have their own pill
        foreach (var ph in Health.UsbPhones()) { usbPhone = ph.Key; break; }
        var list = new List<Periph>();
        if (!Cfg.ShowDevices) return list;
        if (Audio.Out != null && Audio.Out.Headphones)
            list.Add(new Periph { Icon = G.Headphones, Name = Audio.Out.Name, Sub = "Audio output", Open = () => Shell.Open("ms-settings:sound") });
        foreach (var b in batts)
        {
            var ex = list.Find(p => p.Name.IndexOf(b.Name, StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(p.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            string sub = b.Pct >= 0 ? b.Pct + "% battery" + (b.Charging ? " · charging" : "") : "Connected";
            if (ex != null) { ex.Pct = b.Pct; ex.Charging = b.Charging; if (b.Pct >= 0) ex.Sub = sub; continue; }
            bool isMouse = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Mouse|ProtoArc|MX|DeathAdder|Basilisk|G Pro", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool isKey = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Keyboard|Keychron|K380|MX Keys", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool isPhone = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Pixel|Phone|Galaxy|iPhone", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            list.Add(new Periph { Icon = isMouse ? G.Mouse : isKey ? G.Keyboard : isPhone ? G.Phone : G.Headphones, Name = b.Name, Sub = sub, Pct = b.Pct, Charging = b.Charging, Open = () => Shell.Open("ms-settings:bluetooth") });
        }
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                string root = d.Name, label = d.VolumeLabel;
                list.Add(new Periph { Icon = G.Usb, Name = (label.Length > 0 ? label : "USB drive") + " (" + root.TrimEnd('\\') + ")", Sub = "Removable storage", Open = () => Process.Start("explorer.exe", root) });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        int pads = Native.GamepadCount();
        if (pads > 0) list.Add(new Periph { Icon = G.Gamepad, Name = pads == 1 ? "Game controller" : pads + " game controllers", Sub = "Connected", Open = () => Shell.Open("ms-settings:devices") });
        return list;
    }

    string usbPhone; // a phone plugged in over USB, debugging or not

    Point Below(Btn b) { return PointToScreen(new Point((int)b.R.X, barH + (int)(6 * S))); }

    // The phone pill's panel: opens immediately under the pill; notifications refresh in the background (~1 s).
    volatile bool phoneBusy;
    void ShowPhone()
    {
        var ps0 = Health.Phone;
        if (ps0 == null)
        {
            TogglePopup(phone, () =>
            {
                var m = new MenuPopup(S, usbPhone);
                m.Item(G.Folder, "Open the phone's files", "File Explorer", () => Process.Start("explorer.exe", "shell:MyComputerFolder"));
                m.Item(G.Cast, "Screen, notifications and files", "Turn on USB debugging to use them here", () => PhoneSetup("Set up your phone", false));
                return m.LayoutMenu();
            });
            return;
        }
        MenuPopup panel = null;
        TogglePopup(phone, () =>
        {
            panel = new MenuPopup(S, ps0.Model);
            var cached = Health.LastNotesSerial == ps0.Serial ? Health.LastNotes : null;
            FillPhonePanel(panel, ps0, cached);
            return panel.LayoutMenu();
        });
        if (panel == null || phoneBusy) return;
        phoneBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            var ps = Health.Phone ?? ps0;
            var notes = Health.PhoneNotes(ps.Serial);
            bool changed = Health.ReadPhone();
            if (Health.Phone != null) ps = Health.Phone;
            Health.PhoneApps(ps.Serial);
            phoneBusy = false;
            Ui(() =>
            {
                if (changed) RefreshStatus();
                if (panel == null || panel.IsDisposed || !panel.Visible || popup != panel) return;
                panel.ClearRows();
                FillPhonePanel(panel, ps, notes);
                panel.LayoutMenu();
            });
        });
    }

    void FillPhonePanel(MenuPopup m, Health.PhoneState ps, List<Health.PhoneNote> notes)
    {
        const int MaxNotes = 8; // shortcut: no scrolling; the rest are on the phone (the last row mirrors it)
        string model = ps.Model, serial = ps.Serial;
        string state = ps.Battery >= 0 ? ps.Battery + "%" + (ps.Charging ? " · charging" : "") : "Connected";
        m.Item(G.Phone, state, (ps.Wireless ? "Wireless debugging" : "USB") + (ps.Signal >= 0 ? " · signal " + ps.Signal + " of 4" : ""), () => Mirror(model));
        m.Button(G.Cast, "Mirror the phone's screen", () => Mirror(model));
        m.Button("\uE7F4", "Desktop mode: the phone's desktop on a PC-sized screen (click again to search its apps)", () => DesktopMode(model));
        m.Button("\uE724", "Send files to the phone's Download folder", () => SendFiles(serial));
        if (notes == null) { m.Item("\uEA8F", "Reading notifications…", null, () => { }); return; }
        if (notes.Count == 0) m.Item("\uEA8F", "No notifications", null, () => { });
        for (int i = 0; i < notes.Count && i < MaxNotes; i++)
        {
            var n = notes[i];
            var age = DateTime.Now - n.When;
            string when = age.TotalMinutes < 1 ? "now" : age.TotalHours < 1 ? (int)age.TotalMinutes + " min" : age.TotalDays < 1 ? n.When.ToString("t") : n.When.ToString("MMM d");
            string pkg = n.Pkg, app = Health.AppName(serial, pkg);
            m.Item("\uEA8F", Cut(app + " · " + n.Title, 52), Cut((n.Text ?? "") + "  ·  " + when, 60), () => OpenPhoneApp(serial, model, pkg)); // EA8F: bell
        }
        if (notes.Count > MaxNotes) m.Item(G.Cast, "+" + (notes.Count - MaxNotes) + " more on the phone", null, () => Mirror(model));
    }

    static string Cut(string t, int n) { t = t.Replace('\n', ' '); return t.Length <= n ? t : t.Substring(0, n - 1) + "…"; }

    // A phone notification opens its app (on the phone's desktop when that's open, else on the phone) and shows the screen.
    // shortcut: opens the app, not the notification's own target; adb can't fire another app's notification without root.
    void OpenPhoneApp(string serial, string model, string pkg)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Health.DesktopDisplay >= 0 && Health.DesktopSerial == serial) { Health.LaunchOnDesktop(pkg); return; }
            Health.LaunchApp(serial, pkg, -1);
            Health.RefreshAdb();
            if (!Health.Mirror(model)) Ui(() => PhoneSetup(Health.Scrcpy() == null ? "scrcpy isn't installed" : "The phone isn't reachable over USB or wireless debugging", false));
        });
    }

    void SendFiles(string serial)
    {
        string[] files;
        using (var d = new OpenFileDialog { Title = "Send to phone", Multiselect = true })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            files = d.FileNames;
        }
        PushFilesToPhone(serial, files);
    }

    void PushFilesToPhone(string serial, string[] files)
    {
        if (files == null || files.Length == 0) return;
        string what = files.Length == 1 ? Path.GetFileName(files[0]) : files.Length + " files";
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            string why = Health.Push(serial, files);
            Ui(() =>
            {
                var m = new MenuPopup(S, why == null ? "Sent to your phone" : "Couldn't send " + what);
                m.Item(why == null ? "\uE73E" : "\uE783", why == null ? what : why, why == null ? "In Downloads · click to open on phone" : null,
                       () => { if (why == null) System.Threading.ThreadPool.QueueUserWorkItem(__ => { Health.OpenDownloads(serial); Mirror(null); }); }); // check mark / error
                m.Open(Below(phone));
            });
        });
    }

    Btn ResolveDropTarget(IDataObject data, Point clientPt)
    {
        bool hasFiles = data.GetDataPresent(DataFormats.FileDrop);
        bool hasText = data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);
        if (!hasFiles && !hasText) return null;
        var hit = Hit(clientPt);
        if (hit == shelf && hasFiles) return shelf;
        if (hit == clip && (hasText || hasFiles)) return clip;
        if (hit == notesBtn && (hasText || hasFiles)) return notesBtn;
        if (hit == search && (hasText || hasFiles)) return search;
        if (hit == phone && hasFiles) return phone;
        if (hit == null && hasFiles && shelf.Pos < 3) return shelf; // dropping files on empty bar space shelves them
        return null;
    }

    void OnBarDrag(object sender, DragEventArgs e)
    {
        var target = ResolveDropTarget(e.Data, PointToClient(new Point(e.X, e.Y)));
        e.Effect = target != null ? DragDropEffects.Copy : DragDropEffects.None;
        dropping = target != null;
        if (target != dropBtn) { dropBtn = target; KickBar(); Invalidate(); }
    }

    static string DraggedText(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText)) return data.GetData(DataFormats.UnicodeText) as string;
        if (data.GetDataPresent(DataFormats.Text)) return data.GetData(DataFormats.Text) as string;
        return null;
    }

    void OnBarDrop(object sender, DragEventArgs e)
    {
        var target = ResolveDropTarget(e.Data, PointToClient(new Point(e.X, e.Y))) ?? dropBtn;
        dropping = false;
        dropBtn = null;
        KickBar();
        Invalidate();
        if (target == null) return;
        var files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
        string text = DraggedText(e.Data);
        if (target == shelf && files != null)
        {
            Shelf.Add(files);
        }
        else if (target == clip)
        {
            try
            {
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text, TextDataFormat.UnicodeText);
                else if (files != null && files.Length > 0)
                {
                    var col = new System.Collections.Specialized.StringCollection();
                    col.AddRange(files);
                    Clipboard.SetFileDropList(col);
                }
                Clip.Capture();
            }
            catch (ExternalException) { }
        }
        else if (target == notesBtn)
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text.Trim());
            else if (files != null)
                foreach (var f in files)
                {
                    try
                    {
                        if (NoteStore.IsImage(f)) sb.AppendLine("![](" + NoteStore.AddImage(f) + ")");
                        else if (File.Exists(f) && new FileInfo(f).Length < 256 * 1024) sb.AppendLine(File.ReadAllText(f));
                        else sb.AppendLine("- " + Path.GetFileName(f));
                    }
                    catch (Exception) { sb.AppendLine("- " + Path.GetFileName(f)); }
                }
            string body = sb.ToString().Trim();
            if (body.Length > 0)
            {
                string np = NoteStore.NewPath();
                NoteStore.Save(np, body);
                OpenNotes(false);
                if (notesWin != null) notesWin.OpenNote(np);
            }
        }
        else if (target == search)
        {
            string q = !string.IsNullOrWhiteSpace(text) ? text.Trim() : (files != null && files.Length > 0 ? Path.GetFileNameWithoutExtension(files[0]) : "");
            if (q.Length > 0) Shell.Open(WebLink(q) ?? SearchPopup.Url(Cfg, q));
        }
        else if (target == phone && files != null)
        {
            var ps = Health.Phone;
            if (ps != null) PushFilesToPhone(ps.Serial, files);
            else PhoneSetup("Turn on USB debugging to send files here", false);
        }
    }

    void ShowDevices()
    {
        TogglePopup(devices, () =>
        {
            var m = new MenuPopup(S, "Connected devices");
            foreach (var d in Peripherals)
            {
                var dev = d;
                m.Item(d.Icon, d.Name, d.Sub, () => dev.Open());
            }
            m.Item(G.Hidden, "Hide connected devices pill", "Turn back on in Settings › Layout", () => { Cfg.ShowDevices = false; Cfg.Apply(); });
            return m.LayoutMenu();
        });
    }

    // Both wait on adb, so they run on a worker; a phone that can't do it gets the setup steps instead.
    void Mirror(string model) { System.Threading.ThreadPool.QueueUserWorkItem(_ => { Health.RefreshAdb(); if (!Health.Mirror(model)) Ui(() => PhoneSetup(Health.Scrcpy() == null ? "scrcpy isn't installed" : "The phone isn't reachable over USB or wireless debugging", false)); }); }
    void DesktopMode(string model)
    {
        // Sized like this PC's screen and scaled like Windows (Android: 160 dpi = 100%), times the chosen size.
        var b = Screen.PrimaryScreen.Bounds; // physical pixels: the bar is DPI aware
        float k = Math.Min(1, 2560f / Math.Max(b.Width, b.Height)); // cap at 1440p: phone video encoders struggle above; same look, fewer pixels
        int w = (int)(b.Width * k) / 8 * 8, h = (int)(b.Height * k) / 8 * 8, dpi = (int)Math.Round(160 * S * k * new[] { 0.8f, 1f, 1.25f }[Cfg.PhoneScale]);
        string size = w + "x" + h + "/" + dpi;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            bool wasOpen = Health.DesktopDisplay >= 0;
            string why = Health.Desktop(model, size);
            if (why != null) { Ui(() => { if (why.StartsWith("Restart")) RestartNote(why); else PhoneSetup(why, true); }); return; }
            if (!wasOpen) { System.Threading.ThreadPool.QueueUserWorkItem(__ => Health.PhoneApps(Health.DesktopSerial)); return; } // opening it is enough; read the apps now for a later click
            var apps = Health.PhoneApps(Health.DesktopSerial); // already open: the button searches the phone's apps (the taskbar works too)
            Ui(() =>
            {
                var p = new PhoneAppsPopup(S, model, apps, pkg => System.Threading.ThreadPool.QueueUserWorkItem(__ => Health.LaunchOnDesktop(pkg)));
                p.ShowAt(new Rectangle(Below(phone), new Size(1, 1)));
            });
        });
    }

    // The settings are in; the phone reads them at start-up. Nothing else to do here.
    void RestartNote(string why)
    {
        var m = new MenuPopup(S, "Desktop mode");
        m.Item("", why, "Then click the desktop button again", () => { }); // E777: update/restart
        m.Open(Below(phone));
    }

    // How to make any Android phone mirrorable (and desktop-ready). Steps are information only: clicking just closes.
    void PhoneSetup(string why, bool desktop)
    {
        var m = new MenuPopup(S, why);
        Action none = () => { };
        if (Health.Scrcpy() == null) m.Item("", "Install scrcpy on this PC", "winget install Genymobile.scrcpy", none); // E896: download
        m.Item(null, "1. Turn on Developer options", "Settings › About phone › tap Build number 7 times", none);
        m.Item(null, "2. Turn on USB debugging", "Developer options › USB debugging (or Wireless debugging)", none);
        m.Item(null, "3. Allow this PC", "Plug in and accept “Allow USB debugging” on the phone", none);
        if (desktop)
        {
            m.Item(null, "4. Android 10 or newer", "Android 16+ shows the full desktop with a taskbar", none);
            m.Item(null, "5. Full desktop on Pixel (optional)", "Developer options › Enable desktop experience features, restart", none);
        }
        m.Open(Below(phone));
    }

    // Double-click empty bar space hides it (and gives the reserved screen space back); the tray icon restores it.
    // Detected here rather than via OnMouseDoubleClick, which this no-activate tool window never receives.
    DateTime lastEmptyClick;
    void EmptyClick()
    {
        var now = DateTime.Now;
        if ((now - lastEmptyClick).TotalMilliseconds <= SystemInformation.DoubleClickTime) { lastEmptyClick = DateTime.MinValue; SetHidden(true); }
        else lastEmptyClick = now;
    }

    void SetHidden(bool h)
    {
        if (h == hidden) return;
        hidden = h;
        if (popup != null) popup.Close();
        var d = Data();
        if (h) { Native.SHAppBarMessage(ABM_REMOVE, ref d); Hide(); }
        else { Show(); Native.SHAppBarMessage(ABM_NEW, ref d); DockBar(); }
    }

    void BarMenu(Point at)
    {
        if (popup != null) popup.Close();
        var m = new MenuPopup(S, null);
        m.Item(G.Settings, "UpBar settings", OpenSettings);
        m.Item(G.Hidden, "Hide bar", () => SetHidden(true));
        m.Item(G.Close, "Quit top bar", Close);
        m.Open(at);
    }

    void TrayMenu()
    {
        var m = new MenuPopup(S, null);
        m.Item(hidden ? G.Visible : G.Hidden, hidden ? "Show bar" : "Hide bar", () => SetHidden(!hidden));
        m.Item(G.Settings, "Settings", OpenSettings);
        m.Item(G.Close, "Quit top bar", Close);
        m.Open(Cursor.Position);
    }

    // A tiny bar-on-a-screen glyph for the tray, drawn once.
    static Icon appIcon;
    // The Upbar monogram for the tray and window title bars (the .exe carries the same art from upbar.ico).
    public static Icon AppIcon { get { if (appIcon == null) using (var b = Squircles.Monogram(32)) appIcon = Icon.FromHandle(b.GetHicon()); return appIcon; } } // shortcut: one HICON for the whole process, never freed
    static Icon TrayIcon() { return AppIcon; }

    static int NetKind() // 0 offline, 1 Wi-Fi, 2 wired
    {
        int kind = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return 1;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet) kind = 2;
            }
        }
        catch (NetworkInformationException) { }
        return kind;
    }

    float Measure(Graphics g, Btn b)
    {
        if (b == media)
        {
            mediaTitleW = Math.Min(g.MeasureString(b.Text, Theme.BarText, 9999, Theme.Left).Width + 2, 220 * S);
            return 8 * S + 20 * S + 8 * S + mediaTitleW + (eq.Enabled ? 23 * S : 0) + 12 * S;
        }
        if (b == search) return 260 * S; // reads as a field, not a button
        if (b == spaces) return 20 * S + Desktops.Count * 14 * S; // one dot per desktop
        if (b == lights) return 12 * S + 3 * 21 * S;
        float w = 2 * 12 * S + (b.Img != null ? 16 * S + 8 * S : 0);
        for (int i = 0; i < b.Icons.Count; i++)
        {
            w += g.MeasureString(b.Icons[i], iconFont, 999, Theme.Left).Width + 10 * S;
            if (i < b.Labels.Count && !string.IsNullOrEmpty(b.Labels[i]))
                w += 3 * S + g.MeasureString(b.Labels[i], Theme.Tiny, 999, Theme.Left).Width;
        }
        if (!string.IsNullOrEmpty(b.Text)) w += g.MeasureString(b.Text, b.Font, 999, Theme.Left).Width;
        else if (b.Icons.Count > 0) w -= 10 * S;
        return w;
    }

    void LayoutBtns()
    {
        if (Width == 0) return;
        float m = (FloatNow ? 6 : 4) * S, gap = 2 * S, y = 3 * S, h = barH - 6 * S;
        var widths = new Dictionary<Btn, float>();
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            foreach (var b in btns) { widths[b] = Measure(g, b); b.R = RectangleF.Empty; }
        foreach (var b in btns)
        {
            if (b.ShowA < 0 || !ready) b.ShowA = b.Pos < 3 ? 1 : 0; // first layout: no animation
            if (b.Pos < 3) b.LastPos = b.Pos;
            widths[b] *= Math.Max(0, b.ShowA); // growing in / shrinking out
            if (b.ShowA <= 0) b.TargetX = -1;
        }
        for (int pos = 0; pos < 3; pos++)
        {
            float total = -gap;
            foreach (var b in btns) if (b.LastPos == pos && b.ShowA > 0) total += widths[b] + gap * Math.Min(1, b.ShowA);
            if (total < 0) continue;
            float x = pos == 0 ? m : pos == 1 ? (Width - total) / 2 : Width - m - total;
            foreach (var b in btns)
                if (b.LastPos == pos && b.ShowA > 0)
                {
                    float w = widths[b];
                    if (b.TargetX < 0 || !ready) { b.TargetX = x; b.TargetW = w; b.R = new RectangleF(x, y, w, h); }
                    else { b.TargetX = x; b.TargetW = w; if (b.R.Height == 0) b.R = new RectangleF(x, y, w, h); else b.R = new RectangleF(b.R.X, y, b.R.Width, h); }
                    x += w + gap * Math.Min(1, b.ShowA);
                }
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) Shape(); LayoutBtns(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (buf == null || buf.Width != Width || buf.Height != Height)
        {
            if (buf != null) buf.Dispose();
            buf = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            buf.SetResolution(96 * S, 96 * S);
        }
        using (var g = Graphics.FromImage(buf))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var body = new RectangleF(0, 0, Width, Height);
            float rad = FloatNow ? 8 * S : 0; // acrylic only takes DWM's own rounded corners
            g.SmoothingMode = rad > 0 ? SmoothingMode.AntiAlias : SmoothingMode.None; // square bar: smoothing would leave its edge rows half see-through (a faint line)
            using (var p = Native.RoundExact(body, rad))
            {
                var barColor = Glass.Style == 1 ? Theme.Mix(Color.FromArgb(18, 17, 22), Theme.Accent, 0.12f) : Glass.Style == 0 ? Color.FromArgb(24, 24, 27) : Color.FromArgb(16, 16, 18);
                if (!appColor.IsEmpty) Native.Fill(g, p, appColor); // matching the maximized app
                else if (ClearNow)
                {
                    Native.Fill(g, p, Color.FromArgb((int)(240 * (100 - Cfg.Transparency) / 100f), barColor)); // the transparency slider: 100 % = fully clear
                    var shade = InkDark ? Color.White : Color.Black; // dark ink on a bright wallpaper gets a soft light fade behind it instead
                    using (var scrim = new LinearGradientBrush(body, Color.FromArgb(70, shade), Color.FromArgb(0, shade), 90f)) g.FillPath(scrim, p); // keeps the text readable
                }
                else Native.Fill(g, p, Color.FromArgb(Glass.Translucent ? (int)(40 + 200 * (100 - Cfg.Transparency) / 100f) : 255, barColor)); // tint over real blur, or solid
                if (FloatNow) { var edge = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f); using (var pe = Native.RoundExact(edge, rad)) Native.Gloss(g, pe, edge, S, 0.6f); }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (spotA > 0.01f && spotW > 2) // liquid sliding hover capsule
            {
                var sr = new RectangleF(spotX, 3 * S, spotW, barH - 6 * S);
                int sa = (int)(34 * Math.Min(1f, spotA));
                using (var sp = Native.Round(sr, sr.Height / 2)) { Native.Fill(g, sp, Ink(Color.FromArgb(sa, 255, 255, 255))); Native.Gloss(g, sp, sr, S, sa / 55f * 0.7f); }
            }
            foreach (var b in btns)
            {
                if (b.R.Width < 1) continue;
                float da = Math.Max(0, Math.Min(1, b.DropA));
                var clipSt = b.ShowA < 0.99f || b.PressA > 0.005f || da > 0.005f ? g.Save() : null;
                if (b.ShowA < 0.99f) g.SetClip(b.R, CombineMode.Intersect); // mid-grow: reveal, don't squash
                if (b.PressA > 0.005f || da > 0.005f)
                {
                    float sc = 1f - 0.06f * b.PressA + 0.05f * da, cx = b.R.X + b.R.Width / 2f, cy = b.R.Y + b.R.Height / 2f;
                    g.TranslateTransform(cx, cy);
                    g.ScaleTransform(sc, sc);
                    g.TranslateTransform(-cx, -cy);
                }
                int a = b == popupOwner && popup != null ? 55 : (int)(18 * Math.Max(0, Math.Min(1, b.HoverA)));
                if (da > 0.005f)
                {
                    using (var p = Native.Round(b.R, b.R.Height / 2))
                    {
                        if (b == search) Native.Fill(g, p, Ink(Color.FromArgb(20 + a / 2, 255, 255, 255)));
                        Native.Fill(g, p, Color.FromArgb((int)(245 * da), Theme.Accent));
                        Native.Gloss(g, p, b.R, S, 0.85f * da);
                    }
                }
                else if (b == privacy) using (var p = Native.Round(b.R, b.R.Height / 2)) Native.Fill(g, p, Color.FromArgb(61, 220, 132)); // Android privacy green
                else if (b == hint) using (var p = Native.Round(b.R, b.R.Height / 2)) Native.Fill(g, p, Color.FromArgb(44 + (int)(26 * Math.Max(0, Math.Min(1, b.HoverA))), b.TextColor)); // tinted chip in the suggestion's colour
                else if (b == search) using (var p = Native.Round(b.R, b.R.Height / 2)) Native.Fill(g, p, Ink(Color.FromArgb(20 + a / 2, 255, 255, 255))); // flat field: no sheen or rim
                else if (b == popupOwner && popup != null && b != lights) using (var p = Native.Round(b.R, b.R.Height / 2)) { Native.Fill(g, p, Ink(Color.FromArgb(55, 255, 255, 255))); Native.Gloss(g, p, b.R, S, 0.7f); } // open popup pill
                if (b == media) PaintMedia(g, b); else if (b == spaces) PaintSpaces(g, b); else if (b == lights) PaintLights(g, b); else PaintBtn(g, b);
                if (clipSt != null) g.Restore(clipSt);
            }
        }
        Native.Blit(e.Graphics, buf);
    }

    // Traffic lights, macOS-style: close, minimize, restore; their symbols show while the pointer is over them.
    static readonly Color[] LightColors = { Color.FromArgb(255, 95, 87), Color.FromArgb(254, 188, 46), Color.FromArgb(40, 200, 64) };
    static readonly string[] LightGlyphs = { "\uE8BB", "\uE921", "\uE923" }; // close, minimize, restore
    readonly Font lightFont = new Font("Segoe Fluent Icons", 5.5f);
    void PaintLights(Graphics g, Btn b)
    {
        float d = 12 * S, cy = b.R.Y + b.R.Height / 2;
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(b.R.X + 9 * S + i * 21 * S, cy - d / 2, d, d);
            using (var br = new SolidBrush(LightColors[i])) g.FillEllipse(br, r);
            if (b.HoverA > 0) using (var ink = new SolidBrush(Color.FromArgb((int)(170 * b.HoverA), 60, 20, 10))) g.DrawString(LightGlyphs[i], lightFont, ink, r, Theme.Center);
        }
    }

    // Desktop dots, GNOME-style: the current desktop is a wider pill that glides and stretches across dots.
    void PaintSpaces(Graphics g, Btn b)
    {
        if (spaceCur < 0) spaceCur = Desktops.Current;
        float cy = b.R.Y + b.R.Height / 2, x0 = b.R.X + 10 * S;
        for (int i = 0; i < Desktops.Count; i++)
        {
            var r = new RectangleF(x0 + i * 14 * S + 3 * S, cy - 3 * S, 6 * S, 6 * S);
            using (var p = Native.Round(r, 3 * S)) Native.Fill(g, p, Ink(Color.FromArgb(110, 255, 255, 255)));
        }
        float stretch = Math.Min(8 * S, Math.Abs(spaceVel) * 1.2f * S);
        float px = x0 + spaceCur * 14 * S - stretch / 2, pw = 12 * S + stretch;
        var curR = new RectangleF(px, cy - 3 * S, pw, 6 * S);
        using (var p = Native.Round(curR, 3 * S)) Native.Fill(g, p, Ink(Color.White));
    }

    void PaintBtn(Graphics g, Btn b)
    {
        float x = b.R.X + 12 * S;
        if (b.Img != null) // app logo, rounded like a small app icon
        {
            float sz = 16 * S;
            var r = new RectangleF(x, b.R.Y + (b.R.Height - sz) / 2, sz, sz);
            var st = g.Save();
            using (var clip = Native.Round(r, 4 * S)) g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(b.Img, r);
            g.Restore(st);
            x += sz + 8 * S;
        }
        float da = Math.Max(0, Math.Min(1, b.DropA));
        for (int i = 0; i < b.Icons.Count; i++)
        {
            string s = b.Icons[i];
            float w = g.MeasureString(s, iconFont, 999, Theme.Left).Width;
            var r = new RectangleF(x, b.R.Y, w + 2, b.R.Height);
            var col = Theme.Mix(Ink(b.Colors[i]), Theme.OnAccent, da);
            if (i == b.BatteryIcon && plugT < 1)
            {
                // Plug-in animation: the charging battery fills up from empty with a springy pop.
                float t = plugT, fill = 1 - (1 - t) * (1 - t) * (1 - t), pop = 1 + 0.45f * (float)Math.Sin(Math.PI * Math.Min(1, t * 1.6f));
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                var st = g.Save();
                g.TranslateTransform(cx, cy);
                g.ScaleTransform(pop, pop);
                g.TranslateTransform(-cx, -cy);
                using (var br = new SolidBrush(Ink(Theme.BarGreen))) g.DrawString(G.Battery((int)(BatteryPct * fill), true), iconFont, br, r, Theme.Left);
                g.Restore(st);
            }
            else using (var br = new SolidBrush(col)) g.DrawString(s, iconFont, br, r, Theme.Left);
            x += w;
            if (i < b.Labels.Count && !string.IsNullOrEmpty(b.Labels[i]))
            {
                x += 3 * S;
                float lw = g.MeasureString(b.Labels[i], Theme.Tiny, 999, Theme.Left).Width;
                using (var br = new SolidBrush(col)) g.DrawString(b.Labels[i], Theme.Tiny, br, new RectangleF(x, b.R.Y, lw + 2, b.R.Height), Theme.Left);
                x += lw;
            }
            x += 10 * S;
        }
        if (!string.IsNullOrEmpty(b.Text))
            using (var br = new SolidBrush(Theme.Mix(Ink(b.TextColor), Theme.OnAccent, da))) g.DrawString(b.Text, b.Font, br, new RectangleF(x, b.R.Y, b.R.Right - x, b.R.Height), Theme.Left);
    }

    // Live activity: cover, title, and a small visualizer while playing.
    void PaintMedia(Graphics g, Btn b)
    {
        float sz = 20 * S, ax = b.R.X + 8 * S, ay = b.R.Y + (b.R.Height - sz) / 2;
        var ar = new RectangleF(ax, ay, sz, sz);
        using (var p = Native.Round(ar, 5 * S)) ArtView.Cover(g, p, ar, Media.Art);
        if (Media.Art == null) using (var ink = new SolidBrush(Ink(Color.White))) g.DrawString(G.Music, Theme.IconSmall, ink, ar, Theme.Center);
        float tx = ax + sz + 8 * S;
        using (var ink = new SolidBrush(Ink(Color.White))) g.DrawString(b.Text, Theme.BarText, ink, new RectangleF(tx, b.R.Y, mediaTitleW, b.R.Height), Theme.Left);
        if (!eq.Enabled) return;
        float ex = tx + mediaTitleW + 8 * S, cy = b.R.Y + b.R.Height / 2;
        for (int i = 0; i < 3; i++)
        {
            float h = (4 + 8 * Math.Abs((float)Math.Sin(eqPhase * 0.9 + i * 1.7))) * S;
            using (var p = Native.Round(new RectangleF(ex + i * 5 * S, cy - h / 2, 3 * S, h), 1.5f * S)) Native.Fill(g, p, Ink(Theme.Accent));
        }
    }

    // Hovering the suggestion chip drops a tile below it with the whole story; click it to act, as with the chip.
    // Hovering some pills drops a tile below them (no focus taken): the chip's whole story, the song with its
    // controls, device batteries, or the windows on a desktop. Built once per hover; rebuilt when its key changes.
    Btn tileOwner, tileHeld; // tileHeld: its popup was just clicked shut; no tile until the pointer leaves it
    string tileKey;
    void HoverTile(Btn h, int x)
    {
        if (h != tileHeld) tileHeld = null;
        if (h == null || popup != null || h == tileHeld) return;
        if (h == hint && hintKey != null && tileText != null)
            ShowTile(h, "hint|" + tileText, h.R, t => t.Item(hint.Icons.Count > 0 ? hint.Icons[0] : "", tileText, tileSub, () => { if (hint.Click != null) hint.Click(); }));
        else if (h == media && Media.Has)
            ShowTile(h, "media", h.R, () => new MediaPopup(S)); // the same panel a click opens
        else if (h == devices && Peripherals.Count > 0)
            ShowTile(h, "dev", h.R, t => { foreach (var d in Peripherals) { var dev = d; t.Item(d.Icon, d.Name, d.Sub, () => dev.Open()); } });
        else if (h == spaces)
        {
            int i = Math.Max(0, Math.Min(Desktops.Count - 1, (int)((x - h.R.X - 10 * S) / (14 * S)))); // same maths as the click
            var dot = new RectangleF(h.R.X + 10 * S + i * 14 * S - 6 * S, h.R.Y, 26 * S, h.R.Height);
            ShowTile(h, "space" + i, dot, t =>
            {
                var wins = Desktops.Windows(i);
                foreach (var w in wins) { var hw = w.Key; int to = i; t.Item("", Cut(w.Value, 50), () => Desktops.Show(to, hw)); } // E737: window
                if (wins.Count == 0) t.Item("", "No windows", () => Desktops.Go(i));
            }, "Desktop " + (i + 1) + (i == Desktops.Current ? " · current" : ""));
        }
    }

    void ShowTile(Btn owner, string key, RectangleF anchor, Action<HintTile> fill, string title = null)
    {
        ShowTile(owner, key, anchor, () => { var t = new HintTile(S, title); fill(t); return t.LayoutMenu(); });
    }

    void ShowTile(Btn owner, string key, RectangleF anchor, Func<Glass> make)
    {
        if (tile != null && tile.Visible && tileKey == key) return;
        CloseTile();
        tile = make();
        if (tile.IsDisposed) { tile = null; return; } // e.g. the media panel closes itself when the music has gone
        tileOwner = owner;
        tileKey = key;
        tile.ShowQuiet(new Rectangle(PointToScreen(new Point((int)anchor.X, 0)), new Size((int)anchor.Width, barH + (int)(6 * S))));
        if (tileWatch.Tag == null) { tileWatch.Tag = true; tileWatch.Tick += (s, e) => TileCheck(); }
        tileWatch.Start();
    }

    void CloseTile()
    {
        tileWatch.Stop();
        if (tile != null && !tile.IsDisposed) tile.Close();
        tile = null;
        tileKey = null;
    }

    void TileCheck()
    {
        if (tile == null || tile.IsDisposed || !tile.Visible) { tileWatch.Stop(); return; }
        var p = Cursor.Position;
        var chip = RectangleToScreen(Rectangle.Round(tileOwner.R));
        chip.Height = barH + (int)(8 * S); // the gap down to the tile counts as the pill
        if (tileOwner.Pos < 3 && popup == null && (chip.Contains(p) || tile.Bounds.Contains(p))) return;
        CloseTile();
    }

    // A full-screen window on the primary screen hides the bar (and anything hanging from it); it returns on exit.
    bool hiddenForFullscreen;
    void FullscreenCheck()
    {
        bool fs = Native.FullscreenOnPrimary(Handle);
        if (fs == hiddenForFullscreen) return;
        hiddenForFullscreen = fs;
        if (fs)
        {
            CloseTile();
            if (popup != null) popup.Dismiss();
            Visible = false;
        }
        else
        {
            Visible = true; // NOACTIVATE: comes back without taking focus
            TopMost = true;
            DockBar();
        }
    }

    Btn Hit(Point pt)
    {
        foreach (var b in btns) if (b.Pos < 3 && b.R.Contains(pt)) return b;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = Hit(e.Location);
        HoverTile(h, e.X); // before the early return: moving between workspace dots changes the tile
        if (h == hover) return;
        hover = h;
        KickBar();
        tips.Hide(this);
        tipWait.Stop();
        if (h != null && h.Tip != null && popup == null && (tile == null || tileOwner != h)) tipWait.Start(); // the tile says more than the tooltip
    }
    protected override void OnMouseLeave(EventArgs e) { hover = null; pressedBtn = null; KickBar(); tipWait.Stop(); tips.Hide(this); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { pressedBtn = Hit(e.Location); if (pressedBtn != null) KickBar(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (pressedBtn != null) { pressedBtn = null; KickBar(); }
        var b = Hit(e.Location);
        if (b == lights && e.Button == MouseButtons.Left && maxWin != IntPtr.Zero)
        {
            int i = Math.Max(0, Math.Min(2, (int)((e.X - b.R.X - 4 * S) / (21 * S))));
            PostMessage(maxWin, 0x0112, (IntPtr)(i == 0 ? 0xF060 : i == 1 ? 0xF020 : 0xF120), IntPtr.Zero); // WM_SYSCOMMAND: SC_CLOSE / SC_MINIMIZE / SC_RESTORE
            return;
        }
        if (b == spaces && e.Button == MouseButtons.Left) { Desktops.Go(Math.Max(0, Math.Min(Desktops.Count - 1, (int)((e.X - b.R.X - 10 * S) / (14 * S))))); return; }
        if (b == hint && e.Button == MouseButtons.Right) { if (hintKey == "note" || hintKey == "link") { note = null; link = null; noteTimer.Stop(); } else if (hintKey == "done") doneUntil = DateTime.MinValue; else dismissed.Add(hintKey); RefreshStatus(); return; } // dismiss the chip
        if (b != null) { if (b.Click != null) b.Click(); }
        else if (e.Button == MouseButtons.Left) EmptyClick();
        if (e.Button == MouseButtons.Right) BarMenu(PointToScreen(e.Location)); // right-click anywhere on the bar
    }

    // One popup at a time; clicking its own pill again closes it.
    void TogglePopup(Btn owner, Func<Glass> make)
    {
        Glass keep = null; // a click on a pill whose own panel is already hovered open keeps that panel
        if (popup == null && tile != null && tile.Visible && tileOwner == owner && !(tile is HintTile)) { keep = tile; tile = null; tileKey = null; tileWatch.Stop(); }
        CloseTile(); // the popup takes over from the hover tile
        if (popup != null) { bool same = popupOwner == owner; var was = popup; was.Dismiss(); if (popup == was) { popup = null; popupClosed = DateTime.Now; } if (same) { tileHeld = owner; return; } }
        else if (popupOwner == owner && (DateTime.Now - popupClosed).TotalMilliseconds < 300) return; // that click is what closed it
        var p = keep ?? make();
        if (p.IsDisposed) return;
        popup = p;
        popupOwner = owner;
        if (wired.Add(p)) // once per window: a reused panel would otherwise collect handlers on every open
        {
            p.FormClosed += (s, e) => { wired.Remove(p); if (popup == p) { popup = null; popupClosed = DateTime.Now; } Invalidate(); Native.Trim(); };
            p.VisibleChanged += (s, e) => { if (!p.Visible && popup == p) { popup = null; popupClosed = DateTime.Now; Invalidate(); } };
        }
        Invalidate();
        var r = owner.R;
        if (keep != null) { keep.Promote(); return; } // already in place
        p.ShowAt(new Rectangle(PointToScreen(new Point((int)r.X, 0)), new Size((int)r.Width, barH + (int)(6 * S))));
    }

    void ShowPrivacy()
    {
        TogglePopup(privacy, () =>
        {
            var m = new MenuPopup(S, "Using now");
            foreach (var who in Privacy.Who) m.Item("", who, () => { });
            m.Item(G.Settings, "Privacy settings", () => Shell.Open("ms-settings:privacy"));
            return m.LayoutMenu();
        });
    }

    // After a restore: read the restored settings and rebuild every widget (the app grid re-reads apps.txt).
    public void Restored()
    {
        try { Cfg.Parse(File.ReadAllText(App.File("topbar.ini"))); } catch (IOException) { }
        for (int i = 0; i < widgets.Length; i++) { if (widgets[i] != null) { var w = widgets[i]; widgets[i] = null; w.Close(); } widgetSig[i] = null; }
        DbMeter.Alert = Cfg.DbAlert;
        Clip.ReloadPins();
        Cfg.Apply(); // saves and tells everything to follow the restored settings
    }

    public void ReopenSettings()
    {
        if (settings == null) return;
        var at = settings.Location;
        settings.Close();
        OpenSettings();
        settings.Location = at;
    }

    public static readonly string[] WidgetNames = { "clock", "weather", "calendar", "system", "apps", "devices", "media", "note" };
    readonly DesktopWidget[] widgets = new DesktopWidget[8];
    readonly string[] widgetSig = new string[8];

    // Desktop widgets follow the settings: created, closed, or rebuilt when their style, size or data source changes.
    void SyncWidgets()
    {
        var wa = Screen.PrimaryScreen.WorkingArea;
        for (int i = 0; i < WidgetNames.Length; i++)
        {
            string n = WidgetNames[i];
            string sig = Cfg.WidgetOn(n) ? Cfg.W(n, "style", 0) + "|" + Cfg.W(n, "size", 1) + (n == "weather" ? "|" + Cfg.City + "|" + Cfg.Fahrenheit : n == "apps" ? "|" + Theme.Accent.ToArgb() : "") : null;
            if (widgets[i] != null && sig == widgetSig[i]) { widgets[i].Invalidate(); continue; } // e.g. 24-hour time changed
            if (widgets[i] != null) { var old = widgets[i]; widgets[i] = null; old.Close(); }
            widgetSig[i] = sig;
            if (sig == null) continue;
            var at = new Point(wa.X + (int)(new[] { 40, 40, 460, 460, 40, 880, 880, 880 }[i] * S), wa.Y + (int)(new[] { 40, 260, 40, 460, 480, 40, 260, 480 }[i] * S)); // first-time spots: three columns
            DesktopWidget w = i == 0 ? new ClockWidget(S, Cfg, at) : i == 1 ? new WeatherWidget(S, Cfg, at) : i == 2 ? (DesktopWidget)new CalendarWidget(S, Cfg, at) : i == 3 ? (DesktopWidget)new SystemWidget(S, Cfg, at) : i == 4 ? (DesktopWidget)new AppsWidget(S, Cfg, at) : i == 5 ? (DesktopWidget)new DevicesWidget(S, Cfg, at) : i == 6 ? (DesktopWidget)new MediaWidget(S, Cfg, at) : new NoteWidget(S, Cfg, at);
            int idx = i;
            w.FormClosed += (s, e) => { if (widgets[idx] == w) widgets[idx] = null; };
            widgets[i] = w;
            w.Show();
        }
    }

    void ThemeWidgets() { foreach (var w in widgets) if (w != null) w.ApplyTheme(); SyncWidgets(); } // accent change rebuilds Material You app icons

    public void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsWindow(this, S);
            settings.FormClosed += (s, e) => { settings = null; Native.Trim(); };
        }
        settings.Show();
        settings.Activate();
    }

    protected override CreateParams CreateParams
    {
        get { var c = base.CreateParams; c.ExStyle |= 0x80 | 0x08000000; return c; } // TOOLWINDOW (no Alt-Tab), NOACTIVATE
    }

    Native.APPBARDATA Data() { return new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = Handle, cbMsg = cbMsg, edge = ABE_TOP }; }

    // Floating = inset pill with even padding; it docks flush as soon as any window is maximized.
    bool FloatNow { get { return Cfg.Floating && !maxPresent; } }
    bool ClearNow { get { return Cfg.ClearBar && !maxPresent; } } // see-through over the wallpaper; solid again once a window is maximized
    int Pad { get { return FloatNow ? (int)(8 * S) : 0; } }

    // shortcut: primary monitor only; loop Screen.AllScreens if you want a bar per monitor.
    // Re-reserving screen space makes Windows re-lay-out every maximized window (visible as a jitter), so routine
    // updates (a window minimized, a setting saved) only re-dock when the bar's height or inset really changed.
    int dockedReserve = -1, dockedPad = -1;
    void DockIfChanged() { if (Pad != dockedPad || barH + 2 * Pad != dockedReserve) DockBar(); }

    void DockBar()
    {
        if (!IsHandleCreated || hidden) return;
        var s = Screen.PrimaryScreen.Bounds;
        int pad = Pad, reserve = barH + 2 * pad;
        dockedPad = pad;
        dockedReserve = reserve;
        var d = Data();
        d.rc = new Native.RECT { l = s.Left, t = s.Top, r = s.Right, b = s.Top + reserve };
        Native.SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.b = d.rc.t + reserve;
        Native.SHAppBarMessage(ABM_SETPOS, ref d); // windows get re-maximized into the new work area
        Bounds = Rectangle.FromLTRB(d.rc.l + pad, d.rc.t + pad, d.rc.r - pad, d.rc.t + pad + barH);
        Shape();
    }

    void Shape()
    {
        Native.SetWindowRgn(Handle, IntPtr.Zero, true); // acrylic ignores regions; DWM rounds the floating bar
    }

    // Match the app: while a window is maximized, the bar takes the colour of the strip just below it (the app's title
    // bar or toolbar), and its white text and icons turn dark on light colours. One pixel row read, every 2 s at most.
    Color appColor = Color.Empty;
    readonly Timer resample = new Timer { Interval = 2000 };
    bool InkDark { get { return !appColor.IsEmpty ? Native.Luma(appColor) > 0.6f : ClearNow && ClearLuma() > 0.6f; } }

    // Transparent bar: what its background really looks like = the wallpaper behind it, mixed with the bar's own tint.
    float wallLuma = -1; int wallSeen = -1; Rectangle wallFor;
    float ClearLuma()
    {
        if (wallSeen != DesktopWidget.WallVersion || wallFor != Bounds) { wallSeen = DesktopWidget.WallVersion; wallFor = Bounds; wallLuma = DesktopWidget.WallLuma(Bounds); }
        float tint = 240 * (100 - Cfg.Transparency) / 100f / 255f;
        return wallLuma * (1 - tint) + 0.06f * tint;
    }
    // On a light bar: white becomes near-black, and bright status colours (charging green, battery orange/red) get a
    // deeper shade so they keep their contrast. Dark colours (e.g. the privacy pill's icons) are left alone.
    Color Ink(Color c)
    {
        if (!InkDark) return c;
        if (c.R == 255 && c.G == 255 && c.B == 255) return Color.FromArgb(c.A, 28, 27, 31);
        return Native.Luma(c) > 0.45f ? Color.FromArgb(c.A, c.R * 55 / 100, c.G * 55 / 100, c.B * 55 / 100) : c;
    }

    void SampleApp()
    {
        bool on = Cfg.MatchApp && maxPresent && !hidden;
        if (resample.Enabled != on) resample.Enabled = on;
        var c = Color.Empty;
        if (on)
        {
            var r = Bounds;
            var row = new List<Color>();
            try
            {
                using (var b = new Bitmap(r.Width, 1))
                {
                    using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Left, r.Bottom + 2, 0, 0, new Size(r.Width, 1));
                    for (int x = 4; x < r.Width - 4; x += 6) row.Add(b.GetPixel(x, 0));
                }
            }
            catch (System.ComponentModel.Win32Exception) { } // secure desktop / locked screen: nothing to read
            c = Dominant(row);
        }
        if (c.ToArgb() != appColor.ToArgb()) { appColor = c; Invalidate(); }
    }

    // The most common colour in a row of pixels (near shades counted together), so titles and icons don't win.
    public static Color Dominant(List<Color> row)
    {
        var count = new Dictionary<int, int>();
        var sample = new Dictionary<int, Color>();
        int best = -1, bestN = 0;
        foreach (var c in row)
        {
            int k = (c.R >> 3) << 10 | (c.G >> 3) << 5 | c.B >> 3, n;
            count.TryGetValue(k, out n);
            count[k] = ++n;
            if (!sample.ContainsKey(k)) sample[k] = c;
            if (n > bestN) { bestN = n; best = k; }
        }
        return best < 0 || bestN * 3 < row.Count ? Color.Empty : Color.FromArgb(255, sample[best]); // no clear majority (a photo, a busy page): keep the bar's own colour
    }

    bool barLoop;
    readonly System.Diagnostics.Stopwatch barClock = new System.Diagnostics.Stopwatch();

    void KickBar()
    {
        if (barLoop || IsDisposed || !IsHandleCreated) return;
        barLoop = true;
        barClock.Restart();
        BeginInvoke((Action)(() => Native.Vsync(() =>
        {
            if (IsDisposed) { barLoop = false; return false; }
            bool m = BarStep();
            if (Visible) Update();
            if (!m) barLoop = false;
            return m;
        })));
    }

    // One step of the bar's 240 Hz spring animations (plug-in pop, pills growing/gliding, liquid hover capsule, press bounce); false once all have settled.
    bool BarStep()
    {
        float k = Math.Min(4, (float)barClock.Elapsed.TotalMilliseconds / 15f);
        barClock.Restart();
        bool more = false;
        if (plugT < 1) { plugT = Math.Min(1, plugT + 0.012f * k); more = plugT < 1; }
        if (spaceCur < 0) spaceCur = Desktops.Current;
        if (Native.Spring(ref spaceCur, ref spaceVel, Desktops.Current, 360f, 26f, 0.005f)) more = true;
        bool grow = false;
        foreach (var b in btns) // pills appearing or leaving grow and shrink with a spring
        {
            float want = b.Pos < 3 ? 1 : 0;
            if (b.ShowA == want && b.ShowVel == 0) continue;
            if (Native.Spring(ref b.ShowA, ref b.ShowVel, want, 340f, 28f, 0.003f)) more = true;
            grow = true;
        }
        if (grow) LayoutBtns();
        foreach (var b in btns) // pills glide horizontally to their new positions and widths
        {
            if (b.ShowA <= 0 || b.TargetX < 0) continue;
            float rx = b.R.X, rw = b.R.Width;
            bool mx = Native.Spring(ref rx, ref b.XVel, b.TargetX, 420f, 32f, 0.15f);
            bool mw = Native.Spring(ref rw, ref b.WVel, b.TargetW, 420f, 32f, 0.15f);
            if (mx || mw) { b.R = new RectangleF(rx, b.R.Y, rw, b.R.Height); more = true; }
        }
        var spotTarget = hover != null && hover != lights && hover != privacy && hover != hint && hover != search && hover.R.Width > 1 ? hover : null;
        if (spotTarget != null)
        {
            if (spotA <= 0.02f || spotX < 0) { spotX = spotTarget.R.X; spotW = spotTarget.R.Width; spotXVel = spotWVel = 0; }
            else
            {
                if (Native.Spring(ref spotX, ref spotXVel, spotTarget.R.X, 420f, 30f, 0.2f)) more = true;
                if (Native.Spring(ref spotW, ref spotWVel, spotTarget.R.Width, 420f, 30f, 0.2f)) more = true;
            }
        }
        if (Native.Spring(ref spotA, ref spotAVel, spotTarget != null ? 1f : 0f, 360f, 28f, 0.005f)) more = true;
        foreach (var b in btns) // hover highlights and press bounce
        {
            float targetH = b == hover ? 1 : 0;
            if (b.HoverA != targetH || b.HoverVel != 0)
                if (Native.Spring(ref b.HoverA, ref b.HoverVel, targetH, 360f, 28f, 0.004f)) more = true;
            float targetP = b == pressedBtn ? 1 : 0;
            if (b.PressA != targetP || b.PressVel != 0)
                if (Native.Spring(ref b.PressA, ref b.PressVel, targetP, 520f, 26f, 0.004f)) more = true;
            float targetD = b == dropBtn ? 1 : 0;
            if (b.DropA != targetD || b.DropVel != 0)
                if (Native.Spring(ref b.DropA, ref b.DropVel, targetD, 420f, 28f, 0.004f)) more = true;
        }
        Invalidate();
        return more;
    }

    string backdropNow;
    void ApplyShape()
    {
        if (!IsHandleCreated) return;
        int type = ClearNow ? 1 : Glass.Style == 4 ? Native.ClearBlur : Glass.Dwm ? 3 : 1;
        string look = type + "|" + FloatNow;
        if (look != backdropNow) { backdropNow = look; Native.Backdrop(Handle, type, true, FloatNow); Native.ActiveFrame(Handle, type == 3); } // acrylic for the Windows styles; docked bar keeps square corners
        DockIfChanged();
        LayoutBtns();
        Invalidate();
    }

    // Window events (out of process, delivered on this thread) feed the maximize check; only while floating is on.
    void Hooks(bool on)
    {
        if (on == hooks.Count > 0) return;
        if (!on)
        {
            foreach (var h in hooks) Native.UnhookWinEvent(h);
            hooks.Clear();
            maxPresent = false;
            return;
        }
        winProc = (hk, ev, hwnd, obj, child, th, time) => { if (obj == 0 && child == 0) { maxCheck.Stop(); maxCheck.Start(); } };
        foreach (var range in new[] { new uint[] { 0x0003, 0x0003 }, new uint[] { 0x0016, 0x0017 }, new uint[] { 0x8001, 0x8003 }, new uint[] { 0x800B, 0x800B } })
            hooks.Add(Native.SetWinEventHook(range[0], range[1], IntPtr.Zero, winProc, 0, 0, 2)); // foreground, minimize, destroy/show/hide, move/resize
        maxWin = Native.TopMaximizedOnPrimary(Handle);
        maxPresent = maxWin != IntPtr.Zero;
    }

    // Always on: a window coming to the front or finishing a move/resize is nudged out from under the bar.
    Native.WinEventProc clearProc;
    readonly List<IntPtr> clearHooks = new List<IntPtr>();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, 1, 0x1 | 0x4000, (uint)Keys.N); // Alt+N from anywhere (MOD_ALT | MOD_NOREPEAT); quietly unavailable if another app owns it
        clearProc = (hk, ev, hwnd, obj, child, th, time) => { if (obj == 0 && child == 0 && !hidden) Native.KeepClear(hwnd, Handle); };
        clearHooks.Add(Native.SetWinEventHook(0x000B, 0x000B, IntPtr.Zero, clearProc, 0, 0, 2)); // EVENT_SYSTEM_MOVESIZEEND: only when finishing a manual window drag
        Hooks(Cfg.Floating || Cfg.ClearBar || Cfg.MatchApp || Cfg.WinControls);
        Native.Backdrop(Handle, Glass.Style == 4 ? Native.ClearBlur : Glass.Dwm ? 3 : 1, true, FloatNow);
        Native.ActiveFrame(Handle, Glass.Dwm);
        var d = Data();
        Native.SHAppBarMessage(ABM_NEW, ref d); // reserves screen space so maximized windows sit below the bar
        DockBar();
        Native.AddClipboardFormatListener(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var warm = new Timer { Interval = 1500 }; // once the bar has settled: build the control center off screen
        warm.Tick += (s, a) => { warm.Dispose(); if (cc == null) { cc = new ControlCenter(this, S); cc.Warm(); } };
        warm.Start();
        Downloads.Changed += () => { try { BeginInvoke((Action)RefreshStatus); } catch (InvalidOperationException) { } };
        Downloads.Start();
        Desktops.Changed += () => { try { BeginInvoke((Action)RefreshStatus); } catch (InvalidOperationException) { } };
        Desktops.Start();
        healthPoll = new System.Threading.Timer(_ => CheckHealth(), null, 0, 600000); // device batteries, disk space, pending restart: every 10 min (and on device changes)
        Health.WatchBluetooth(() => Ui(() => { debounce.Stop(); debounce.Start(); })); // real-time Bluetooth connect/disconnect
        recPoll = new System.Threading.Timer(_ => { var r = Health.Recorder(); if (r != recorder) try { BeginInvoke((Action)(() => { recorder = r; RefreshStatus(); })); } catch (InvalidOperationException) { } }, null, 5000, 5000);
        meetingPoll = new System.Threading.Timer(_ => { var m = Meetings.Next(); try { BeginInvoke((Action)(() => { meeting = m; RefreshStatus(); })); } catch (InvalidOperationException) { } }, null, 3000, 60000);
        clockPoll = new System.Threading.Timer(_ => { var l = ClockApp.Upcoming(); try { BeginInvoke((Action)(() => { clockItems = l; RefreshStatus(); })); } catch (InvalidOperationException) { } }, null, 0, 15000);
        VolumeKeys.SuppressBrightOsd = () => popup is ControlCenter;
        Sys.WatchNotes(); SyncWidgets(); VolumeKeys.VolStyle = Cfg.OsdStyle; VolumeKeys.BrightStyle = Cfg.BrightStyle; VolumeKeys.Pos = Cfg.OsdPos; VolumeKeys.Enable(Cfg.VolumeOsd, S, this); Native.Trim();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var d = Data();
        Native.SHAppBarMessage(ABM_REMOVE, ref d);
        Native.RemoveClipboardFormatListener(Handle);
        tray.Visible = false; // otherwise a ghost icon stays until the mouse passes over it
        tray.Dispose();
        Hooks(false);
        VolumeKeys.Enable(false, S, null);
        DbMeter.Stop(); // let go of the microphone
        UnregisterHotKey(Handle, 1);
        foreach (var h in clearHooks) Native.UnhookWinEvent(h);
        base.OnFormClosing(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (Glass.Dwm) Native.StayActive(ref m); // only Acrylic needs the "active" frame; otherwise it brings a faint line along the top edge
        if (m.Msg == cbMsg)
        {
            if ((int)m.WParam == 1) DockBar();                        // ABN_POSCHANGED
            if ((int)m.WParam == 2) { TopMost = m.LParam == IntPtr.Zero; FullscreenCheck(); } // ABN_FULLSCREENAPP: get out of the way
        }
        if (m.Msg == 0x7E) DockBar();                                              // WM_DISPLAYCHANGE
        if (m.Msg == 0x218 && (int)m.WParam == 0xA) RefreshStatus();               // WM_POWERBROADCAST / power status change
        if (m.Msg == 0x219) { debounce.Stop(); debounce.Start(); }                 // WM_DEVICECHANGE: coalesce bursts
        if (m.Msg == 0x0312 && (int)m.WParam == 1) OpenNotes(true);                // WM_HOTKEY: Alt+N
        if (m.Msg == 0x031D && !Clip.Capture()) clipRetry.Start();                 // WM_CLIPBOARDUPDATE
        if (m.Msg == 0x031D && Cfg.SmartHints) CopiedLink();
        if (m.Msg == 0x1A && (int)m.WParam == 0x14) { DesktopWidget.ReloadWallpaper(); ThemeWidgets(); Invalidate(); } // SPI_SETDESKWALLPAPER: widgets redraw their blurred wallpaper
        if (m.Msg == 0x1A && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet") // light/dark or accent changed
        {
            Theme.Reload();
            if (Cfg.CursorStyle >= 2) CustomCursors.Apply(Cfg.CursorStyle, Theme.Accent);
            var g = popup;
            if (g != null) g.ApplyTheme();
            if (settings != null) settings.ApplyTheme();
            ThemeWidgets();
        }
        base.WndProc(ref m);
    }

    // The package logos at 100/150/200/400 % scale (makepri picks per display), plus the unplated taskbar icons.
    static void StoreAssets(string dir)
    {
        Directory.CreateDirectory(dir);
        var sizes = new[] { new { Name = "Square44x44Logo", Px = 44 }, new { Name = "Square150x150Logo", Px = 150 }, new { Name = "StoreLogo", Px = 50 } };
        foreach (var a in sizes)
            foreach (int scale in new[] { 100, 150, 200, 400 })
                using (var b = Squircles.Monogram(a.Px * scale / 100)) b.Save(Path.Combine(dir, a.Name + ".scale-" + scale + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        foreach (int t in new[] { 16, 24, 32, 48, 256 })
            using (var b = Squircles.Monogram(t)) b.Save(Path.Combine(dir, "Square44x44Logo.targetsize-" + t + "_altform-unplated.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest.Run();
        // One-shot icon runs, meant for an elevated prompt so all-users shortcuts are included too.
        if (args.Length > 0 && args[0] == "--squircles") { Squircles.Apply(); return 0; }
        if (args.Length > 0 && args[0] == "--squircles-undo") { Squircles.Undo(); return 0; }
        if (args.Length > 1 && args[0] == "--make-store-assets") { StoreAssets(args[1]); return 0; } // Microsoft Store package logos
        if (args.Length > 1 && args[0] == "--make-icon") { using (var art = Squircles.Monogram(256)) Squircles.WriteIco(art, args[1]); return 0; } // writes upbar.ico for the build
        if (!Native.EnsureDefaultDesktop(Application.ExecutablePath)) return 0;
        bool fresh;
        using (new System.Threading.Mutex(true, "ClaudeTopBar.Single", out fresh))
        {
            if (!fresh) return 0; // already running: a second app bar would stack under the first
            if (!App.Packaged) Updater.Cleanup();
            try { if (!App.Packaged && Config.StartWithWindows) Config.StartWithWindows = true; } catch (Exception) { } // re-point Start with Windows at this exe (it was renamed to Upbar)
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            // A shell bar must never stop at a crash dialog: log the error and keep running.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                try { File.AppendAllText(App.File("topbar.log"), DateTime.Now + "  " + e.Exception + Environment.NewLine); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            };
            Application.Run(new TopBar());
        }
        return 0;
    }
}

static class SelfTest
{
    // topbar.exe --selftest ; exit code 0 = pass
    public static int Run()
    {
        int fails = 0;
        var c = new Config();
        c.Parse("transparency=250\r\nheight=abc\nclock=1\nclock24=true\nbogus\nshowDate=False\nshowDevices=False\nhiddenTiles=wifi,BT;x,night");
        if (!(c.Transparency == 100 && c.Height == 30 && c.ClockPos == 1 && c.Clock24 && !c.ShowDate && !c.ShowDevices && c.HiddenTiles == "wifi,x,night")) fails++;
        var d = new Config();
        d.Parse(c.Serialize());
        if (d.Serialize() != c.Serialize()) fails++;
        if (!(c.IsTileHidden("wifi") && c.IsTileHidden("night") && !c.IsTileHidden("bt"))) fails++;
        var shelf = Shelf.Merge(new List<string> { @"C:\a.pdf" }, new[] { @"c:\A.PDF", " ", null, @"C:\b.docx " });
        if (!(shelf.Count == 2 && shelf[1] == @"C:\b.docx")) fails++;
        var items = new List<ClipItem>();
        Clip.Merge(items, new ClipItem { Text = "a" });
        Clip.Merge(items, new ClipItem { Text = "b" });
        Clip.Merge(items, new ClipItem { Text = "a" }); // re-copy moves to the top instead of duplicating
        if (!(items.Count == 2 && items[0].Text == "a")) fails++;
        // A brush handed out by the cache must stay usable after the cache overflows (theme switch crash).
        var held = Theme.B(Color.FromArgb(1, 2, 3));
        for (int i = 0; i < 200; i++) Theme.B(Color.FromArgb(i, 10, 20));
        try { using (var bmp = new Bitmap(2, 2)) using (var g = Graphics.FromImage(bmp)) g.FillRectangle(held, 0, 0, 2, 2); }
        catch (ArgumentException) { fails++; }
        // Squircle icon file: valid ICONDIR with 5 PNG entries, and a pastel (light) tint.
        string tmp = Path.Combine(Path.GetTempPath(), "topbar-selftest.ico");
        using (var art = new Bitmap(256, 256)) Squircles.WriteIco(art, tmp);
        var head = File.ReadAllBytes(tmp);
        File.Delete(tmp);
        // type icon, 5 images, the 256px entry (after 6 + 5*16 header bytes) is PNG, the 64px one a 40-byte DIB header
        int second = BitConverter.ToInt32(head, 6 + 16 + 12);
        if (!(head[2] == 1 && head[4] == 5 && head[86] == 0x89 && head[87] == (byte)'P' && BitConverter.ToInt32(head, second) == 40 && BitConverter.ToInt32(head, second + 8) == 128)) fails++;
        string tmpCur = Path.Combine(Path.GetTempPath(), "topbar-selftest.cur");
        using (var art = new Bitmap(128, 128)) CustomCursors.WriteCur(art, 16, 12, tmpCur);
        var curHead = File.ReadAllBytes(tmpCur);
        File.Delete(tmpCur);
        // type cursor (2), 2 sizes (32 and 48), hotspot scaled to (4, 3) on the 32px entry
        if (!(curHead[2] == 2 && curHead[4] == 2 && curHead[6] == 32 && curHead[10] == 4 && curHead[12] == 3)) fails++;
        using (var red = new Bitmap(8, 8)) { using (var g = Graphics.FromImage(red)) g.Clear(Color.Red); var t = Squircles.Tint(red); if (!(t.R == 255 && t.G > 190 && t.B > 190)) fails++; }
        using (var white = new Bitmap(8, 8)) { using (var g = Graphics.FromImage(white)) g.Clear(Color.White); if (Squircles.Tint(white).R > 80) fails++; } // white logo gets a dark squircle
        // Window under the bar: moved to the work area top, shrunk so its visible bottom stays on screen; others untouched.
        var work = Rectangle.FromLTRB(0, 39, 2560, 1600);
        var moved = Native.Clear(Rectangle.FromLTRB(-7, 0, 1607, 1609), Rectangle.FromLTRB(0, 0, 1600, 1600), work);
        if (!(moved.Top == 39 && moved.Bottom == 1609)) fails++;
        if (!Native.Clear(Rectangle.FromLTRB(0, 100, 800, 700), Rectangle.FromLTRB(0, 100, 800, 700), work).IsEmpty) fails++;
        if (!(WeatherWidget.Glyph(0) == "☀" && WeatherWidget.Glyph(63) == "⛆" && WeatherWidget.Glyph(75) == "❄" && WeatherWidget.Glyph(95) == "⛈" && WeatherWidget.Label(2) == "Partly cloudy")) fails++;
        var wc = new Config();
        wc.Parse("city=New\tYork\nclockAt=12,-30\nweatherAt=oops\nfahrenheit=true\nwidgetClock=True\nwidget.system.size=9\nwidget.Bad.on=1\nwidget.calendar.color=3\nwidget.calendar.on=5");
        if (!(wc.City == "NewYork" && wc.W("clock", "x", 0) == 12 && wc.W("clock", "y", 0) == -30 && wc.W("weather", "x", -1) == -1 && wc.Fahrenheit
              && wc.WidgetOn("clock") && wc.W("system", "size", 0) == 4 && wc.W("bad", "on", 0) == 0 && wc.W("calendar", "on", 0) == 1 && wc.W("calendar", "color", -1) == -1)) fails++;
        var rt = new Config(); rt.Parse(wc.Serialize()); // round trip keeps widget settings
        if (!(rt.WidgetOn("clock") && rt.W("clock", "y", 0) == -30 && rt.W("system", "size", 0) == 4)) fails++;
        // Grid sizes: wide designs only get wide sizes, unreadably small sizes are skipped, a bad wish falls back to a fit.
        var wide = new SizeF(344, 172);
        if (!(DesktopWidget.Allowed(wide, 1) && DesktopWidget.Allowed(wide, 3) && !DesktopWidget.Allowed(wide, 2) && !DesktopWidget.Allowed(new SizeF(260, 280), 0)
              && DesktopWidget.Fit(wide, 2) == 3 && DesktopWidget.Fit(new SizeF(300, 290), -1) == 4 && DesktopWidget.Fit(new SizeF(210, 210), 0) == 0)) fails++;
        if (!(string.Join(" ", ClockWidget.Spoken(21, 45, false)) == "nine forty five" && string.Join(" ", ClockWidget.Spoken(9, 0, false)) == "nine o'clock"
              && string.Join(" ", ClockWidget.Spoken(0, 7, false)) == "twelve oh seven" && string.Join(" ", ClockWidget.Spoken(23, 15, true)) == "twenty-three fifteen"
              && string.Join(" ", ClockWidget.Spoken(12, 30, false)) == "twelve thirty")) fails++;
        if (!(DesktopWidget.Snap(0, 6, 150) == 6 && DesktopWidget.Snap(80, 6, 150) == 6 && DesktopWidget.Snap(82, 6, 150) == 156 && DesktopWidget.Snap(-100, 6, 150) == -144)) fails++; // grid snapping
        if (!(TopBar.LowBattery(50, false, false).IsEmpty && TopBar.LowBattery(15, true, false).IsEmpty && !TopBar.LowBattery(25, false, false).IsEmpty && TopBar.LowBattery(30, false, false).IsEmpty && TopBar.LowBattery(25, false, false).G > 120
              && TopBar.LowBattery(8, false, false).R == 255 && TopBar.LowBattery(8, false, false).G < 120)) fails++; // low battery colours
        if (!(Math.Abs(VolumeKeys.Step(0.5f, 0xAF) - 0.52f) < 0.001f && VolumeKeys.Step(1f, 0xAF) == 1 && VolumeKeys.Step(0.01f, 0xAE) == 0 && Math.Abs(VolumeKeys.Step(0.33f, 0xAE) - 0.32f) < 0.001f)) fails++; // volume steps
        { var oc = new Config(); oc.Parse("osdStyle=9\nbrightStyle=1\nosdPos=3"); var rc = new Config(); rc.Parse(oc.Serialize()); if (rc.OsdStyle != 2 || rc.BrightStyle != 1 || rc.OsdPos != 3) fails++; } // pop-up styles and position: clamped and saved
        if (Native.StyleRound.Length != Glass.StyleNames.Length) fails++; // a roundness for every style
        if (VolumeOsd.Spot(new Rectangle(0, 0, 1000, 800), new Size(100, 200), 3, 1) != new Point(876, 300) || VolumeOsd.Spot(new Rectangle(0, 0, 1000, 800), new Size(100, 200), 0, 1) != new Point(450, 552)) fails++; // pop-up placement
        if (!(TopBar.Suggest(25, false, false, false, false) == "charge" && TopBar.Suggest(25, true, false, false, false) == null && TopBar.Suggest(80, false, true, true, true) == "muted"
              && TopBar.Suggest(80, false, true, false, true) == "offline" && TopBar.Suggest(100, true, false, false, false) == "full" && TopBar.Suggest(-1, false, false, false, false) == null)) fails++; // suggestions
        if (!(TopBar.WebLink(" https://example.com/a?b=1 ") == "https://example.com/a?b=1" && TopBar.WebLink("file:///C:/x.exe") == null && TopBar.WebLink("calc.exe") == null
              && TopBar.WebLink("https://a.com b") == null && TopBar.WebLink("javascript:alert(1)") == null && TopBar.WebLink(null) == null)) fails++; // copied links
        var now0 = new DateTime(2026, 1, 1, 9, 0, 0);
        var sched = new List<ClockApp.Item> { new ClockApp.Item { When = now0.AddHours(2), Timer = false }, new ClockApp.Item { When = now0.AddMinutes(5), Timer = true, Label = "5 min" }, new ClockApp.Item { When = now0.AddMinutes(-1), Timer = true } };
        if (!(ClockApp.Pick(sched, now0).Label == "5 min" && ClockApp.Pick(sched.GetRange(0, 1), now0) != null && ClockApp.Pick(new List<ClockApp.Item> { new ClockApp.Item { When = now0.AddHours(13) } }, now0) == null
              && ClockApp.Left(TimeSpan.FromSeconds(247)) == "4:07" && ClockApp.Left(TimeSpan.FromSeconds(3750)) == "1:02:30")) fails++; // clock chip
        if (!(Downloads.IsPartial(@"C:\d\a.zip.crdownload") && !Downloads.IsPartial(@"C:\d\a.zip") && Downloads.Name(@"C:\d\a.zip.part") == "a.zip" && Downloads.Name(@"C:\d\Unconfirmed 42.crdownload") == "file")) fails++; // downloads
        var titleRow = new List<Color>();
        for (int i = 0; i < 20; i++) titleRow.Add(i % 5 == 0 ? Color.Black : Color.FromArgb(243, 243, 243)); // a light title bar with some dark text
        if (!(TopBar.Dominant(titleRow).ToArgb() == Color.FromArgb(243, 243, 243).ToArgb() && TopBar.Dominant(new List<Color>()).IsEmpty)) fails++; // match the app
        if (!(NoteStore.TitleOf("\n\n## Trip **plan**\nbody") == "Trip plan" && NoteStore.TitleOf("  \nfirst line\n# later") == "first line" && NoteStore.TitleOf(" \n ") == "Untitled"
              && NoteStore.TitleOf("[Doc](https://x.y) notes") == "Doc notes")) fails++; // note titles
        var tbl = Md.FormatTable(new List<string> { "|a|bbbb|", "|:-|-:|", "|ccc|d|" });
        if (!(tbl[0] == "| a   | bbbb |" && tbl[1] == "| :-- | ---: |" && tbl[2] == "| ccc | d    |")) fails++; // table lining up
        if (!(NoteStore.Resolve("../topbar.ini") == null && NoteStore.Resolve("C:\\Windows\\win.ini") == null && NoteStore.Resolve("https://x/y.png") == null)) fails++; // note images stay in the notes folder
        var runs = Md.Runs("a **b** `c` [d](https://e)");
        if (!(runs.Count == 6 && runs[1].Bold && runs[3].Code && runs[5].Url == "https://e")) fails++; // inline markdown
        var tc = new Config();
        tc.Parse("hiddenTiles=night,+snip");
        if (!(tc.IsTileHidden("night", false) && !tc.IsTileHidden("snip", true) && tc.IsTileHidden("captions", true) && !tc.IsTileHidden("wifi", false))) fails++; // tiles: removable, and addable when hidden by default
        if (!(Math.Abs(DbMeter.ToDb(32768) - 100) < 0.01 && DbMeter.ToDb(0) == 20 && Math.Abs(DbMeter.ToDb(3277) - 80) < 0.1 && DbMeter.ToDb(1e9) == 120
              && Math.Abs(DbMeter.Rms(new short[] { 3, 4 }, 2) - Math.Sqrt(12.5)) < 1e-9 && DbMeter.Rms(new short[0], 0) == 0)) fails++; // sound level maths
        var mt = new Meetings.Item { Subject = "Standup", Start = new DateTime(2026, 1, 1, 9, 5, 0), Link = "https://teams.example/x" };
        if (!(Meetings.Label(mt, new DateTime(2026, 1, 1, 9, 0, 0)) == "Standup in 5 min · Join" && Meetings.Label(mt, new DateTime(2026, 1, 1, 9, 6, 0)) == "Standup now · Join")) fails++; // meeting chip
        if (!(Backup.IsAllowed("topbar.ini") && Backup.IsAllowed("notes/Trip.md") && Backup.IsAllowed("notes/images/a.png") && !Backup.IsAllowed("../evil.exe")
              && !Backup.IsAllowed("notes/../../x.md") && !Backup.IsAllowed("Upbar.exe") && !Backup.IsAllowed("notes\\x.md") && !Backup.IsAllowed("C:/x.ini") && !Backup.IsAllowed("notes/images/x.exe"))) fails++; // restore writes only known files
        var ids = new byte[48]; ids[16] = 7; ids[32] = 9; var cur = new byte[16]; cur[0] = 9;
        if (!(Desktops.Index(ids, cur) == 2 && Desktops.Index(ids, new byte[16]) == 0 && Desktops.Index(null, cur) == 0 && Health.CleanName("Rockerz 512 ANC Hands-Free AG") == "Rockerz 512 ANC" && Health.CleanName("Pixel 9 Pro XL Hands-Free HF") == "Pixel 9 Pro XL")) fails++; // desktops and device names
        if (!(SearchPopup.Calc("2+3*4") == 14 && SearchPopup.Calc("(2+3)*4") == 20 && SearchPopup.Calc("2^3^2") == 512 && SearchPopup.Calc("-5+2") == -3 && SearchPopup.Calc("10/4") == 2.5
              && SearchPopup.Calc("hello") == null && SearchPopup.Calc("2+") == null && SearchPopup.Calc("1/0") == null && SearchPopup.Calc("3x4") == 12
              && SearchPopup.Convert("10 km in mi") == "10 km = 6.213712 mi" && SearchPopup.Convert("100 f to c") == "100°F = 37.78°C" && SearchPopup.Convert("5 kg to mi") == null)) fails++; // quick search answers
        var apps = AppsWidget.Parse("C:\\a.lnk\r\nnotes.txt\n\nC:\\b|c.exe\nD:\\x y\\z.exe");
        var store = AppsWidget.Parse("shell:AppsFolder\\Claude_pzs8sxrjxfjjc!Claude\nshell:AppsFolder\\x;calc&rm");
        if (!(store[0] == "shell:AppsFolder\\Claude_pzs8sxrjxfjjc!Claude" && store[1] == "")) fails++;
        if (!(apps.Length == 12 && apps[0] == "C:\\a.lnk" && apps[1] == "" && apps[3] == "" && apps[4] == "D:\\x y\\z.exe" && apps[11] == "")) fails++;
        if (!(WeatherWidget.IconName(0, false) == "clear-night" && WeatherWidget.IconName(81, true) == "showers" && WeatherWidget.IconName(73, true) == "snow" && WeatherWidget.IconName(48, true) == "fog")) fails++;
        var ac = new Config();
        ac.Parse("accent=#00ff7f");
        if (ac.AccentColor.ToArgb() != Color.FromArgb(0, 255, 127).ToArgb()) fails++;
        ac.Parse("accent=red; calc"); // invalid: ignored, keeps the previous value
        ac.AccentColor = Color.Empty;
        if (ac.Accent != "") fails++;
        var sc = new Config();
        if (SearchPopup.Url(sc, "a b&c") != "https://www.google.com/search?q=a%20b%26c") fails++;
        sc.Parse("searchEngine=7\nsearchUrl=calc.exe %s"); // not a web address: must be ignored
        if (sc.SearchUrl != "" || !SearchPopup.Url(sc, "x").StartsWith("https://www.google.com/")) fails++;
        if (Native.DeviceKey(@"\\?\HID#VID_046D&PID_C52B&MI_01&Col02#7&1&0&0001#{guid}") != "VID_046D&PID_C52B"
            || Native.DeviceKey(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002046d_PID&b023&Col01#8&2") != "VID&0002046D_PID&B023"
            || Native.DeviceKey(@"\\?\ACPI#MSF0001#4&1d401fb5&0#{guid}") != null) fails++;
        if (!(Native.Covers(new Rectangle(0, 0, 2560, 1600), new Rectangle(0, 0, 2560, 1600)) && !Native.Covers(new Rectangle(-11, 34, 2582, 1577), new Rectangle(0, 0, 2560, 1600)))) fails++; // full screen vs maximized under the bar
        if (!(G.Battery(100, false) == "" && G.Battery(45, true) == "" && G.Battery(95, true) == "" && G.Volume(0.5f, false) == "")) fails++;
        if (!(Sys.BtKind("ProtoArc EM11 Pro", null, 15, 2) == "Mouse" && Sys.BtKind("Keychron K2", null, 15, 1) == "Keyboard" && Sys.BtKind("Rockerz", "AudioVideo", 0, 0) == "AudioVideo")) fails++;
        if (!(Updater.IsNewer("v1.1.0", "1.0.0") && Updater.IsNewer("2.0.0", "1.9.9") && !Updater.IsNewer("v1.0.0", "1.0.0") && !Updater.IsNewer("v0.9.0", "1.0.0"))) fails++;
        return fails;
    }
}
