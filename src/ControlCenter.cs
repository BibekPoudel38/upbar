using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Quick settings panel: battery + power menu, sliders, toggle tiles (with Android-style detail lists),
// hidden tray icons, edit and settings.
class ControlCenter : Glass
{
    // Toggles: its Click switches something on/off (with Expands too, the tile splits: icon toggles, name opens details).
    // OffByDefault: starts hidden; added from edit mode.
    class Def { public string Key, Icon, Title; public Func<string> Sub, IconOf; public Func<bool> On; public Action Click; public bool Link, Expands, SlashOff, Toggles, OffByDefault; }
    static int flightOn = -2;            // 1 all radios off, 0 not, -1 no radios
    static Sys.Hotspot hot;              // null until read
    static string hotNote;               // why the hotspot couldn't switch, shown briefly
    static string dbNote;                // why the sound meter couldn't start
    readonly IntPtr userWindow = GetForegroundWindow(); // the app you were in: the keyboard picker switches it
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    static int wifiOn = -2, btOn = -2, bright = -2; // -2 unknown yet, -1 unavailable
    static string ssid, expanded;                    // expanded: key of the tile whose details are open (kept between openings)
    static List<Sys.WifiNet> nets;
    static List<Sys.BtDev> bts;
    static readonly Dictionary<string, string> netStatus = new Dictionary<string, string>();
    readonly TopBar bar;
    readonly List<Def> defs;
    readonly List<Shell.TrayApp> tray = new List<Shell.TrayApp>();
    readonly StringBuilder password = new StringBuilder(); // outlives rebuilds while typing
    bool powerOpen, editing;
    string sig, pwFor;
    Slider brightSlider;

    public ControlCenter(TopBar bar, float s) : base(s)
    {
        this.bar = bar;
        defs = new List<Def>
        {
            new Def { Key = "wifi", Icon = G.Wifi, Title = "Wi-Fi", Expands = true, Toggles = true, On = () => wifiOn == 1, Sub = () => RadioSub(wifiOn, ssid ?? "Not connected"), Click = () => ToggleRadio(true) },
            new Def { Key = "bt", Icon = G.Bluetooth, Title = "Bluetooth", Expands = true, Toggles = true, On = () => btOn == 1,
                      Sub = () => { var c = bts == null ? null : bts.Find(d => d.Connected); var b = c == null ? null : Health.Last.Find(bt => string.Equals(bt.Name, Health.CleanName(c.Name), StringComparison.OrdinalIgnoreCase) && bt.Pct >= 0); return RadioSub(btOn, c != null ? c.Name + (b != null ? " · " + b.Pct + "%" : "") : "On"); }, Click = () => ToggleRadio(false) },
            new Def { Key = "cast", Icon = G.Cast, Title = "Screen cast", Sub = () => "Wireless display", Link = true, Click = Do(() => Native.Win(Keys.K)) },
            new Def { Key = "modes", Icon = G.Speed, Title = "Power mode", Expands = true, Toggles = true, On = () => Shell.PowerMode == 2,
                      Sub = () => { int m = Shell.PowerMode; return m == -2 ? "…" : m < 0 ? "Unavailable" : Shell.ModeNames[m]; },
                      Click = () => { int m = Shell.PowerMode; if (m >= 0) Shell.PowerMode = (m + 1) % 3; RefreshTiles(); if (expanded == "modes") Build(); } },
            new Def { Key = "share", Icon = G.Share, Title = "Quick Share", Sub = () => "Nearby sharing", Link = true, Click = Do(() => Shell.Open("ms-settings:crossdevice")) },
            new Def { Key = "output", Icon = G.Speaker, Title = "Sound output", Expands = true, On = () => expanded == "output", Sub = () => Audio.Out == null ? "No device" : Audio.Out.Name, Click = () => Expand("output") },
            new Def { Key = "mic", Icon = G.Mic, IconOf = () => Audio.Mic != null && !Audio.Mic.Muted ? G.Mic : G.MicOff, Title = "Microphone", Toggles = true, On = () => Audio.Mic != null && !Audio.Mic.Muted,
                      Sub = () => Audio.Mic == null ? "No mic" : Audio.Mic.Muted ? "Muted" : "On",
                      Click = () => { if (Audio.Mic != null) Audio.Mic.Muted = !Audio.Mic.Muted; RefreshTiles(); } },
            new Def { Key = "camera", Icon = G.Camera, SlashOff = true, Title = "Camera", Toggles = true, On = () => Shell.CameraAllowed, Sub = () => Shell.CameraAllowed ? "Apps allowed" : "Blocked",
                      Click = () => { Shell.CameraAllowed = !Shell.CameraAllowed; RefreshTiles(); } },
            new Def { Key = "dark", Icon = G.Theme, Title = "Dark theme", Toggles = true, On = () => Shell.Dark, Sub = () => Shell.Dark ? "On" : "Off",
                      Click = () => { Shell.Dark = !Shell.Dark; RefreshTiles(); } },
            // Windows lets only Store apps switch Do not disturb (or start Focus), so this opens the notification centre,
            // where its bell sits at the top. A Focus session that is running still shows as on.
            new Def { Key = "dnd", Icon = G.Sleep, Title = "Do not disturb", Link = true, On = () => Sys.FocusActive, Sub = () => Sys.FocusActive ? "Focus on" : "Notification centre",
                      Click = Do(() => Native.Win(Keys.N)) },
            new Def { Key = "night", Icon = G.NightLight, Title = "Night light", Sub = () => "Settings", Link = true, Click = Do(() => Shell.Open("ms-settings:nightlight")) },
            new Def { Key = "flight", Icon = "\uE709", Title = "Flight mode", Toggles = true, On = () => flightOn == 1,
                      Sub = () => flightOn == -2 ? "…" : flightOn < 0 ? "No radios" : flightOn == 1 ? "All radios off" : "Off", Click = ToggleFlight },
            new Def { Key = "hotspot", Icon = "\uE88A", Title = "Mobile hotspot", Toggles = true, Expands = true, On = () => hot != null && hot.State == 1,
                      Sub = () => hotNote ?? (hot == null || hot.State == -2 ? "…" : hot.State < 0 ? "Unavailable" : hot.State == 1 ? hot.Clients + (hot.Clients == 1 ? " device" : " devices") : "Off"), Click = ToggleHotspot },
            new Def { Key = "db", Icon = "\uE767", Title = "Sound level", Toggles = true, Expands = true, On = () => DbMeter.On, Click = ToggleDb,
                      Sub = () => dbNote ?? (DbMeter.On ? (int)Math.Round(DbMeter.Level) + " dB" + (DbMeter.Loud ? " · loud" : "") : "Off") },
            new Def { Key = "keyboard", Icon = G.Keyboard, Title = "Keyboard", Expands = true, Sub = () => KeyboardName(Shell.KeyboardOf(userWindow)), Click = () => Expand("keyboard") },
            new Def { Key = "location", Icon = "\uE81D", Title = "Location", Toggles = true, SlashOff = true, OffByDefault = true, On = () => Shell.LocationAllowed, Sub = () => Shell.LocationAllowed ? "Apps allowed" : "Blocked",
                      Click = () => { Shell.LocationAllowed = !Shell.LocationAllowed; RefreshTiles(); } },
            new Def { Key = "snip", Icon = "\uE7A8", Title = "Screen snip", Sub = () => "Win+Shift+S", OffByDefault = true, Click = Do(() => Shell.Open("ms-screenclip:")) },
            new Def { Key = "project", Icon = "\uE7F4", Title = "Project", Sub = () => "Second screen", OffByDefault = true, Click = Do(() => Native.Win(Keys.P)) },
            new Def { Key = "captions", Icon = "\uE7F0", Title = "Live captions", Sub = () => "Win+Ctrl+L", OffByDefault = true, Click = Do(() => Native.Win(Keys.L, true)) },
            new Def { Key = "saver", Icon = G.Battery(40, false), Title = "Energy saver", Sub = () => "Settings", Link = true, OffByDefault = true, Click = Do(() => Shell.Open("ms-settings:batterysaver")) },
        };
        DbMeter.Changed += DbChanged;
        FormClosed += (a, b) => { DbMeter.Changed -= DbChanged; foreach (var t in tray) t.Icon.Dispose(); password.Length = 0; };
        Build();
    }

    static string RadioSub(int state, string on) { return state == -2 ? "…" : state < 0 ? "Unavailable" : state == 1 ? on : "Off"; }

    // Runs the action, then closes the panel (activating another window needs us still in the foreground).
    Action Do(Action a) { return () => { a(); Dismiss(); }; }

    void Ui(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { } }

    protected override bool Reusable { get { return true; } }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Fresh(); }
    protected override void OnReopen() { BeginInvoke((Action)(() => { Sync(); RefreshTiles(); Fresh(); })); } // show the last frame at once; catch up right after

    // Live states are read in the background each time the panel opens; tiles update as answers arrive.
    void Fresh()
    {
        ThreadPool.QueueUserWorkItem(_ => { Shell.ReadPowerMode(); Ui(() => { RefreshTiles(); if (expanded == "modes") Build(); }); }); // slow query
        ThreadPool.QueueUserWorkItem(_ => { int f = Sys.Flight(); var h = Sys.HotspotNow(); Ui(() => { flightOn = f; hot = h; RefreshTiles(); if (expanded == "hotspot") Build(); }); });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int w = Sys.GetRadio(true), b = Sys.GetRadio(false);
            string id = Sys.Ssid();
            var devs = b == 1 ? Sys.BtDevices() : null;
            Ui(() => { wifiOn = w; btOn = b; ssid = id; if (devs != null) bts = devs; RefreshTiles(); if (expanded == "bt") Build(); });
        });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int v = Sys.Brightness();
            Ui(() =>
            {
                bool had = bright >= 0;
                bright = v;
                if (had != (v >= 0)) Build();
                else if (brightSlider != null && !brightSlider.Pressed) { brightSlider.Value = v / 100f; Invalidate(); }
            });
        });
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var t = Shell.HiddenTray();
            Ui(() =>
            {
                if (string.Join("|", t.ConvertAll(a => a.Title)) == string.Join("|", tray.ConvertAll(a => a.Title))) { foreach (var n in t) n.Icon.Dispose(); return; } // same apps: no rebuild mid-animation
                foreach (var old in tray) old.Icon.Dispose();
                tray.Clear(); tray.AddRange(t); Build();
            });
        });
        if (expanded != null) Fetch(expanded);
    }

    void Expand(string key)
    {
        expanded = expanded == key ? null : key;
        pwFor = null;
        if (expanded != null) Fetch(expanded);
        Build();
    }

    // Detail lists load off the UI thread; the panel shows "Scanning…" until they arrive.
    void Fetch(string key)
    {
        if (key == "wifi") ThreadPool.QueueUserWorkItem(_ => { var l = Sys.WifiScan(); Ui(() => { nets = l; if (expanded == "wifi") Build(); }); });
        if (key == "bt") ThreadPool.QueueUserWorkItem(_ => { var l = Sys.BtDevices(); Ui(() => { bts = l; if (expanded == "bt") Build(); }); });
    }

    void ToggleDb()
    {
        if (DbMeter.On) DbMeter.Stop(); else dbNote = DbMeter.Start();
        RefreshTiles();
        if (expanded == "db") Build();
    }

    // Live meter: only the sound tile's text and the gauge change, so just those are refreshed (20 times a second).
    void DbChanged()
    {
        if (!Visible) return; // hidden between opens: nothing to show
        foreach (var w in Items)
        {
            var t = w as Tile;
            var d = t == null ? null : t.Tag as Def;
            if (d != null && d.Key == "db") { t.Sub = d.Sub(); t.On = DbMeter.On; }
        }
        Invalidate();
    }

    void ToggleFlight()
    {
        if (flightOn < 0 && flightOn != -2) { Do(() => Shell.Open("ms-settings:network-airplanemode"))(); return; }
        bool on = flightOn != 1;
        flightOn = on ? 1 : 0; // optimistic, confirmed below
        RefreshTiles();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Sys.SetFlight(on);
            Thread.Sleep(800);
            int f = Sys.Flight(), w = Sys.GetRadio(true), b = Sys.GetRadio(false);
            Ui(() => { flightOn = f; wifiOn = w; btOn = b; RefreshTiles(); });
        });
    }

    void ToggleHotspot()
    {
        if (hot == null || hot.State == -2) return;
        if (hot.State < 0) { Do(() => Shell.Open("ms-settings:network-mobilehotspot"))(); return; }
        bool on = hot.State != 1;
        hotNote = on ? "Turning on…" : "Turning off…";
        RefreshTiles();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string err = Sys.SetHotspot(on);
            var h = Sys.HotspotNow();
            Ui(() => { hot = h; hotNote = err; RefreshTiles(); if (expanded == "hotspot") Build(); });
        });
    }

    static string KeyboardName(IntPtr layout)
    {
        foreach (InputLanguage l in InputLanguage.InstalledInputLanguages) if (l.Handle == layout) return l.LayoutName;
        return InputLanguage.CurrentInputLanguage.LayoutName;
    }

    void ToggleRadio(bool wifi)
    {
        int cur = wifi ? wifiOn : btOn;
        if (cur < 0) { Do(() => Shell.Open(wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth"))(); return; }
        bool on = cur != 1;
        if (wifi) wifiOn = on ? 1 : 0; else btOn = on ? 1 : 0; // optimistic, confirmed below
        RefreshTiles();
        if (expanded == (wifi ? "wifi" : "bt")) Build();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Sys.SetRadio(wifi, on);
            Thread.Sleep(1500); // let Wi-Fi associate before reading the SSID
            int w = Sys.GetRadio(true), b = Sys.GetRadio(false);
            string id = Sys.Ssid();
            Ui(() => { wifiOn = w; btOn = b; ssid = id; RefreshTiles(); if (expanded != null) Fetch(expanded); });
        });
    }

    // Saved networks connect directly; a secured new one asks for its password inline.
    void Connect(Sys.WifiNet n, string pw)
    {
        if (n.Connected) return;
        netStatus[n.Ssid] = "Connecting…";
        if (pw != null) { pwFor = null; password.Length = 0; }
        Build();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool needPw;
            string err = Sys.WifiConnect(n, pw, out needPw);
            var list = err == null ? Sys.WifiScan() : null;
            string id = Sys.Ssid();
            Ui(() =>
            {
                if (err == null) { netStatus.Remove(n.Ssid); nets = list; ssid = id; }
                else { netStatus[n.Ssid] = err; if (needPw) pwFor = n.Ssid; }
                RefreshTiles();
                Build();
            });
        });
    }

    void RefreshTiles()
    {
        bool changed = false;
        foreach (var w in Items)
        {
            var t = w as Tile;
            var d = t == null ? null : t.Tag as Def;
            if (d == null) continue;
            bool on = d.On != null && d.On();
            string sub = d.Sub(), icon = d.IconOf != null ? d.IconOf() : t.Icon;
            if (on == t.On && sub == t.Sub && icon == t.Icon) continue;
            t.On = on; t.Sub = sub; t.Icon = icon;
            t.Slash = d.SlashOff && !on;
            changed = true;
        }
        if (changed) Kick(); // redraw only when a tile really changed
    }

    float Group(float x, float y, float w, float rowH, Widget[] rows)
    {
        if (rows.Length == 0) return y;
        Add(new Segment { R = new RectangleF(x, y, w, rows.Length * rowH + 8 * S) });
        y += 4 * S;
        foreach (var r in rows) { r.R = new RectangleF(x + 4 * S, y, w - 8 * S, rowH); Add(r); y += rowH; }
        return y + 4 * S + 10 * S;
    }

    // Rows laid out in `cols` columns on one surface.
    float Grid(float x, float y, float w, float rowH, int cols, Widget[] items)
    {
        int rows = (items.Length + cols - 1) / cols;
        Add(new Segment { R = new RectangleF(x, y, w, rows * rowH + 8 * S) });
        float cw = (w - 8 * S) / cols;
        for (int i = 0; i < items.Length; i++)
        {
            items[i].R = new RectangleF(x + 4 * S + (i % cols) * cw, y + 4 * S + (i / cols) * rowH, cw, rowH);
            Add(items[i]);
        }
        return y + rows * rowH + 8 * S + 10 * S;
    }

    static Caption Info(string text) { return new Caption { Text = text }; }
    static string Bars(int b) { return b >= 4 ? G.Wifi : b == 3 ? G.Wifi3 : b == 2 ? G.Wifi2 : G.Wifi1; }

    float Detail(float x, float y, float w)
    {
        var rows = new List<Widget>();
        TextField field = null;
        if (expanded == "wifi")
        {
            if (wifiOn == 0) rows.Add(Info("Wi-Fi is off"));
            else if (nets == null) rows.Add(Info("Scanning…"));
            else
            {
                if (nets.Count == 0) rows.Add(Info("No networks found"));
                foreach (var n in nets.GetRange(0, Math.Min(nets.Count, 8))) // strongest 8; the rest are in Wi-Fi settings
                {
                    var net = n;
                    string st;
                    netStatus.TryGetValue(n.Ssid, out st);
                    rows.Add(new MenuRow { Icon = Bars(n.Bars), Text = n.Ssid, Sub = st ?? (n.Connected ? "Connected" : n.Secured ? "Secured" : "Open"), Check = n.Connected, Click = () => Connect(net, null) });
                    if (pwFor == n.Ssid)
                        rows.Add(field = new TextField { Value = password, Secret = true, Placeholder = "Password, then Enter", Submit = pw => { if (pw.Length > 0) Connect(net, pw); } });
                }
                if (nets.Exists(n => n.Connected))
                    rows.Add(new MenuRow { Icon = G.Close, Text = "Disconnect", Click = () => ThreadPool.QueueUserWorkItem(_ => { Sys.WifiDisconnect(); var l = Sys.WifiScan(); string id = Sys.Ssid(); Ui(() => { nets = l; ssid = id; RefreshTiles(); Build(); }); }) });
            }
            rows.Add(new MenuRow { Icon = G.Settings, Text = "Wi-Fi settings", Click = Do(() => Shell.Open("ms-settings:network-wifi")) });
        }
        else if (expanded == "bt")
        {
            if (btOn == 0) rows.Add(Info("Bluetooth is off"));
            else if (bts == null) rows.Add(Info("Looking for paired devices…"));
            else
            {
                if (bts.Count == 0) rows.Add(Info("No paired devices"));
                // shortcut: Windows has no public API to connect paired audio/phones, so a device opens Bluetooth settings.
                foreach (var d in bts)
                {
                    var b = d.Connected ? Health.Last.Find(bt => string.Equals(bt.Name, Health.CleanName(d.Name), StringComparison.OrdinalIgnoreCase) && bt.Pct >= 0) : null;
                    rows.Add(new MenuRow { Icon = d.Kind == "AudioVideo" ? G.Headphones : d.Kind == "Phone" ? G.Phone : d.Kind == "Mouse" ? G.Mouse : d.Kind == "Keyboard" ? G.Keyboard : d.Kind == "Gamepad" ? G.Gamepad : G.Bluetooth, Text = d.Name, Sub = d.Connected ? (b != null ? "Connected · " + b.Pct + "%" : "Connected") : "Not connected", Check = d.Connected, Click = Do(() => Shell.Open("ms-settings:bluetooth")) });
                }
            }
            rows.Add(new MenuRow { Icon = G.Settings, Text = "Bluetooth settings", Click = Do(() => Shell.Open("ms-settings:bluetooth")) });
        }
        else if (expanded == "modes")
        {
            int cur = Shell.PowerMode;
            if (cur < 0) rows.Add(Info("Not available with the current power plan"));
            else
            {
                string[] subs = { "Longer battery life", "Recommended", "Fastest, uses more power" };
                for (int i = 0; i < 3; i++)
                {
                    int m = i;
                    rows.Add(new MenuRow { Icon = G.Speed, Text = Shell.ModeNames[i], Sub = subs[i], Check = i == cur, Click = () => { Shell.PowerMode = m; RefreshTiles(); Build(); } });
                }
            }
        }
        else if (expanded == "db")
        {
            rows.Add(new DbGauge());
            rows.Add(Info("An estimate from your microphone, not a calibrated reading"));
            foreach (int v in new[] { 70, 80, 85, 90, 100 })
            {
                int lv = v;
                rows.Add(new MenuRow { Icon = "\uE7E7", Text = "Alert above " + v + " dB", Sub = v == 85 ? "Long exposure starts to harm hearing" : null, Check = DbMeter.Alert == v,
                                       Click = () => { DbMeter.Alert = lv; bar.Cfg.DbAlert = lv; bar.Cfg.Apply(); Build(); } });
            }
        }
        else if (expanded == "keyboard")
        {
            var cur = Shell.KeyboardOf(userWindow);
            foreach (InputLanguage l in InputLanguage.InstalledInputLanguages)
            {
                var lang = l;
                rows.Add(new MenuRow { Icon = G.Keyboard, Text = l.LayoutName, Sub = l.Culture.DisplayName, Check = l.Handle == cur, Click = Do(() => Shell.UseKeyboard(userWindow, lang.Handle)) });
            }
            rows.Add(new MenuRow { Icon = G.Settings, Text = "Language & keyboards", Click = Do(() => Shell.Open("ms-settings:regionlanguage")) });
        }
        else if (expanded == "hotspot")
        {
            if (hot == null || hot.State == -2) rows.Add(Info("Checking…"));
            else if (hot.State < 0) rows.Add(Info("This connection can't be shared"));
            else
            {
                rows.Add(new MenuRow { Icon = "\uE88A", Text = hot.Name ?? "Hotspot", Sub = hot.State == 1 ? "On" : "Off", Check = hot.State == 1, Click = ToggleHotspot });
                if (hot.State == 1) rows.Add(Info(hot.Clients + (hot.Clients == 1 ? " device connected" : " devices connected")));
            }
            rows.Add(new MenuRow { Icon = G.Settings, Text = "Hotspot settings", Click = Do(() => Shell.Open("ms-settings:network-mobilehotspot")) });
        }
        else if (expanded == "output")
        {
            string curId = Audio.Out == null ? null : Audio.Out.Id;
            foreach (var e in Audio.Outputs())
            {
                var dev = e;
                rows.Add(new MenuRow { Icon = e.Headphones ? G.Headphones : G.Speaker, Text = e.Name, Check = e.Id == curId, Click = () => { Audio.SetDefault(dev.Id); bar.RefreshStatus(); Build(); } });
            }
            var apps = AppVolume.List(6);
            if (apps.Count > 0) rows.Add(Info("Apps"));
            foreach (var a in apps) // each app's own level; the icon mutes it
            {
                var app = a;
                Slider s = null;
                s = new Slider { Icon = app.Muted ? G.Mute : G.Volume(app.Level, false), Value = app.Level, Tip = app.Name + " (click the icon to mute)",
                                 Changed = v => { app.Level = v; if (app.Muted && v > 0) app.Muted = false; s.Icon = G.Volume(v, false); } };
                s.IconClick = () => { app.Muted = !app.Muted; s.Icon = app.Muted ? G.Mute : G.Volume(app.Level, false); Invalidate(); };
                rows.Add(new Caption { Text = app.Name });
                rows.Add(s);
            }
            rows.Add(new MenuRow { Icon = G.Settings, Text = "Sound settings", Click = Do(() => Shell.Open("ms-settings:sound")) });
        }
        y = Group(x, y, w, 48 * S, rows.ToArray());
        foreach (var r in rows) { var c = r as Caption; if (c != null) c.R = new RectangleF(c.R.X + 16 * S, c.R.Y, c.R.Width - 16 * S, c.R.Height); }
        if (field != null) FocusOn(field);
        return y;
    }

    void ShowAppMenu(Shell.TrayApp app, Point at)
    {
        var m = new MenuPopup(S, app.Title);
        m.Item(G.Open, "Open", () => { Shell.Activate(app); Dismiss(); });
        m.Item(G.Close, "Quit", () => { Shell.Quit(app); tray.Remove(app); app.Icon.Dispose(); Build(); });
        CloseOnDeactivate = false; // the menu takes focus; keep the panel open behind it
        m.FormClosed += (a, b) => { if (!IsDisposed) { CloseOnDeactivate = true; Activate(); } };
        m.Open(at);
    }

    void Build()
    {
        ClearItems();
        float W = 380 * S, pad = 14 * S, x = pad, w = W - 2 * pad, y = pad, b = 36 * S;

        // Header: battery on the left, power menu on the right.
        if (bar.BatteryPct >= 0)
            Add(new Caption
            {
                Icon = G.Battery(bar.BatteryPct, bar.Charging), IconColor = bar.Charging ? Theme.Green : TopBar.LowBattery(bar.BatteryPct, false, Theme.Light),
                Text = bar.BatteryPct + "%" + (bar.Charging ? "  ·  Charging" : ""), Font = Theme.Bold, Brush = bar.Charging ? Theme.B(Theme.Green) : TopBar.LowBattery(bar.BatteryPct, false, Theme.Light).IsEmpty ? Theme.Text : Theme.B(TopBar.LowBattery(bar.BatteryPct, false, Theme.Light)),
                R = new RectangleF(x + 6 * S, y, w - 90 * S, b)
            });
        Add(new IconBtn { Icon = G.Power, Icon2 = powerOpen ? G.Up : G.Down, On = powerOpen, Tip = "Power", R = new RectangleF(x + w - 64 * S, y, 64 * S, b), Click = () => { powerOpen = !powerOpen; Build(); } });
        Add(new IconBtn { Icon = G.Lock, Tip = "Lock", R = new RectangleF(x + w - 64 * S - 6 * S - b, y, b, b), Click = Do(() => Native.LockWorkStation()) });
        y += b + 10 * S;

        if (powerOpen)
            y = Grid(x, y, w, 42 * S, 2, new Widget[]
            {
                new MenuRow { Icon = G.Sleep, Text = "Sleep", Compact = true, Click = Do(() => Application.SetSuspendState(PowerState.Suspend, false, false)) },
                new MenuRow { Icon = G.SignOut, Text = "Sign out", Compact = true, Click = Do(() => System.Diagnostics.Process.Start("shutdown.exe", "/l")) },
                // No /f: apps with unsaved work still get to prompt.
                new MenuRow { Icon = G.Restart, Text = "Restart", Compact = true, Click = Do(() => System.Diagnostics.Process.Start("shutdown.exe", "/r /t 0")) },
                new MenuRow { Icon = G.Power, Text = "Shut down", Compact = true, Click = Do(() => System.Diagnostics.Process.Start("shutdown.exe", "/s /t 0")) },
                new MenuRow { Icon = G.Close, Text = "Quit top bar", Compact = true, Click = Do(bar.Close) },
            });

        // Sliders; the volume row ends in a button that opens the output list.
        var rows = new List<Widget>();
        brightSlider = null;
        if (bright >= 0)
            rows.Add(brightSlider = new Slider { Icon = G.Brightness, Tip = "Brightness", Value = bright / 100f, Changed = v => { bright = (int)Math.Round(v * 100); Sys.SetBrightness(bright); } });
        Slider vol = null;
        if (Audio.Out != null)
        {
            var o = Audio.Out;
            vol = new Slider { Icon = G.Volume(o.Volume, o.Muted), Value = o.Volume, Tip = "Volume (click the icon to mute)" };
            vol.Changed = v => { o.Volume = v; if (o.Muted && v > 0) o.Muted = false; vol.Icon = G.Volume(v, o.Muted); };
            vol.IconClick = () => { o.Muted = !o.Muted; vol.Icon = G.Volume(o.Volume, o.Muted); Invalidate(); };
            rows.Add(vol);
        }
        y = Group(x, y, w, 44 * S, rows.ToArray());
        if (vol != null)
        {
            vol.R.Width -= 44 * S;
            Add(new IconBtn { Icon = G.Speaker, Tip = "Sound output", On = expanded == "output", R = new RectangleF(vol.R.Right + 6 * S, vol.R.Y + 4 * S, 36 * S, 36 * S), Click = () => Expand("output") });
        }

        // Tiles on a 4-column grid: 1x1 = 1 cell, 1x2 = 2 cells, 2x2 = 2 cells by 2 rows. An expanded tile's
        // details open full width under the last row it touches.
        if (editing) { Add(new Caption { Text = "Click a tile to resize it, the eye to hide it", R = new RectangleF(x + 6 * S, y, w, 22 * S) }); y += 28 * S; }
        float gap = 6 * S, th = 52 * S, cw = (w - 3 * gap) / 4;
        var occ = new List<bool[]>();
        var placed = new List<KeyValuePair<Tile, int[]>>(); // r, c, rowSpan, colSpan
        foreach (var d in defs)
        {
            bool hidden = bar.Cfg.IsTileHidden(d.Key, d.OffByDefault);
            if (hidden && !editing) continue;
            int size = bar.Cfg.TileSize(d.Key), cs = size == 0 ? 1 : 2, rs = size == 2 ? 2 : 1, r = 0, c = 0;
            for (bool found = false; !found; )
            {
                while (occ.Count < r + rs) occ.Add(new bool[4]);
                for (c = 0; c + cs <= 4 && !found; c++)
                {
                    found = true;
                    for (int i = r; i < r + rs && found; i++) for (int j = c; j < c + cs; j++) if (occ[i][j]) { found = false; break; }
                    if (found) break;
                }
                if (!found) r++;
            }
            for (int i = r; i < r + rs; i++) for (int j = c; j < c + cs; j++) occ[i][j] = true;
            var def = d;
            var t = new Tile { Icon = d.Icon, Title = d.Title, Tip = d.Title, Link = d.Link, Tag = d, Size = size, Editing = editing, Dimmed = editing && hidden, Expanded = expanded == d.Key };
            t.Click = () => { if (editing) { bar.Cfg.ToggleTile(def.Key, def.OffByDefault); Build(); } else def.Click(); };
            t.Split = d.Toggles && d.Expands; // Pixel style: icon toggles, name opens the details
            t.Resize = () => { bar.Cfg.CycleTileSize(def.Key); Build(); };
            if (d.Expands) t.Expand = () => Expand(def.Key);
            if (d.Expands && !d.Toggles && !editing) t.Click = null; // the whole tile opens its details
            placed.Add(new KeyValuePair<Tile, int[]>(t, new[] { r, c, rs, cs }));
        }
        int after = -1; // grid row after which the detail list goes
        foreach (var kv in placed) if (kv.Key.Expanded && !editing) after = kv.Value[0] + kv.Value[2] - 1;
        for (bool moved = after >= 0; moved; ) // never split a 2x2 tile around the detail list
        {
            moved = false;
            foreach (var kv in placed) if (kv.Value[0] <= after && kv.Value[0] + kv.Value[2] - 1 > after) { after = kv.Value[0] + kv.Value[2] - 1; moved = true; }
        }
        var rowY = new float[occ.Count];
        for (int i = 0; i < occ.Count; i++)
        {
            rowY[i] = y;
            y += th + gap;
            if (i == after) y = Detail(x, y + 2 * S, w);
        }
        foreach (var kv in placed)
        {
            int[] p = kv.Value;
            kv.Key.R = new RectangleF(x + p[1] * (cw + gap), rowY[p[0]], p[3] * cw + (p[3] - 1) * gap, p[2] * th + (p[2] - 1) * gap);
            Add(kv.Key);
        }
        RefreshTiles();
        y += 6 * S;

        // Bottom: hidden tray icons (right click = our menu), then edit and settings.
        float bx = x + w - b;
        Add(new IconBtn { Icon = G.Settings, Tip = "Windows Settings", R = new RectangleF(bx, y, b, b), Click = Do(() => Shell.Open("ms-settings:")) }); // bar settings: right-click the bar
        Add(new IconBtn { Icon = editing ? G.Check : G.Edit, Tip = editing ? "Done" : "Edit tiles", On = editing, R = new RectangleF(bx - b - 6 * S, y, b, b), Click = () => { editing = !editing; Build(); } });
        float tx = x;
        int fit = (int)((bx - b - 12 * S - x - b) / (b + 2 * S)); // leave room for the overflow button
        for (int i = 0; i < tray.Count && i < fit; i++)
        {
            var app = tray[i];
            Add(new TrayIcon { Img = app.Icon, Tip = app.Title, R = new RectangleF(tx, y, b, b), Click = Do(() => Shell.Activate(app)), Menu = at => ShowAppMenu(app, at) });
            tx += b + 2 * S;
        }
        Add(new IconBtn { Icon = G.Up, Tip = "All hidden icons", R = new RectangleF(tx, y, b, b), Click = Do(Shell.OpenOverflow) }); // Windows' full hidden-icons flyout
        y += b + pad;

        sig = Signature();
        Place((int)W, (int)y);
        Invalidate();
    }

    string Signature()
    {
        var o = Audio.Out;
        return bar.BatteryPct + "|" + bar.Charging + "|" + string.Join(",", bar.Peripherals.ConvertAll(p => p.Name + ":" + p.Sub)) + "|" +
               (o == null ? "" : o.Id + Math.Round(o.Volume, 2) + o.Muted);
    }

    // Called by the bar when system state changes; rebuild unless the user is mid-drag or typing a password.
    public void Sync() { if (!Pressing && pwFor == null && Signature() != sig) Build(); }
}

// The live sound level: the reading, and a bar that turns orange near the alert level and red past it.
class DbGauge : Widget
{
    public override bool Interactive { get { return false; } }
    public override void Paint(Graphics g)
    {
        float lvl = DbMeter.Level, k = (lvl - 20) / 100f;
        bool on = DbMeter.On;
        Color c = !on ? Theme.A(60) : lvl >= DbMeter.Alert ? TopBar.LowBattery(5, false, Theme.Light) : lvl >= DbMeter.Alert - 15 ? TopBar.LowBattery(25, false, Theme.Light) : Theme.Accent;
        g.DrawString(on ? (int)Math.Round(lvl) + " dB" : "Off", Theme.Title, Theme.Text, new RectangleF(R.X + 14 * S, R.Y, 100 * S, R.Height), Theme.Left);
        var track = new RectangleF(R.X + 118 * S, R.Y + R.Height / 2 - 5 * S, R.Width - 134 * S, 10 * S);
        using (var p = Native.Round(track, 5 * S)) Native.Fill(g, p, Theme.A(30));
        if (on) using (var p = Native.Round(new RectangleF(track.X, track.Y, Math.Max(10 * S, track.Width * Math.Max(0, Math.Min(1, k))), track.Height), 5 * S)) Native.Fill(g, p, c);
        float ax = track.X + track.Width * (DbMeter.Alert - 20) / 100f, px = track.X + track.Width * Math.Max(0, (DbMeter.Peak - 20) / 100f);
        using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawLine(pen, ax, track.Y - 4 * S, ax, track.Bottom + 4 * S); // the alert level
        if (on && DbMeter.Peak > 20) using (var pen = new Pen(Color.FromArgb(150, Theme.Fore), 1.5f * S)) g.DrawLine(pen, px, track.Y, px, track.Bottom); // loudest so far
    }
}
