using System;
using System.Drawing;
using System.Windows.Forms;

// Settings, Windows 11 style: a sidebar of topics and one page at a time. Every change applies live and is
// saved to topbar.ini. The window reopens on the page you used last.
class SettingsWindow : Glass
{
    static readonly int[] Heights = { 26, 30, 36 };
    static readonly string[] Pages = { "Appearance", "Layout", "Clock", "Media", "Search", "Widgets", "System" };
    static readonly string[] PageIcons = { G.Theme, G.Tiles, G.ClockIcon, G.Music, G.Search, G.Brightness, G.Settings };
    static int page;
    readonly Config c;
    readonly TopBar bar;
    float x, w, y, segTop;
    Segment seg;

    public SettingsWindow(TopBar bar, float s) : base(s)
    {
        Tool = false;
        CloseOnDeactivate = false;
        ShowInTaskbar = true;
        TopMost = false;
        Text = "UpBar settings";
        Icon = TopBar.AppIcon;
        Tint = 215;
        this.bar = bar;
        c = bar.Cfg;
        int W = (int)(760 * S), H = (int)(700 * S);
        var wa = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(wa.X + (wa.Width - W) / 2, wa.Y + Math.Max(0, (wa.Height - H) / 2), W, Math.Min(H, wa.Height));
        Build();
    }

    void Build()
    {
        ClearItems();
        float pad = 16 * S, side = 200 * S;

        // Sidebar
        Add(new Caption { Text = "Settings", Font = Theme.Title, Strong = true, R = new RectangleF(pad + 10 * S, pad, side - 2 * pad, 40 * S) });
        float ny = pad + 56 * S;
        for (int i = 0; i < Pages.Length; i++)
        {
            int idx = i;
            Add(new MenuRow { Icon = PageIcons[i], Text = Pages[i], Selected = i == page, R = new RectangleF(pad, ny, side - pad - 8 * S, 40 * S), Click = () => { page = idx; Build(); } });
            ny += 42 * S;
        }

        // Page
        x = side + 8 * S;
        w = Width - x - pad;
        y = pad;
        Add(new Caption { Text = Pages[page], Font = Theme.Title, Strong = true, R = new RectangleF(x + 6 * S, y, w - 56 * S, 40 * S) });
        Add(new IconBtn { Icon = G.Close, Tip = "Close", R = new RectangleF(Width - pad - 40 * S, y, 40 * S, 40 * S), Click = Close });
        y += 56 * S;
        Begin();
        var lcr = new[] { "Left", "Center", "Right" };
        switch (page)
        {
            case 0:
                Add(new Caption { Text = "Style", Font = Theme.Body, Strong = true, R = Line(30) });
                Add(new StyleGrid { Index = c.GlassStyle, R = Line(StyleGrid.Rows * 104), Changed = i => { c.GlassStyle = i; c.Apply(); } });
                Add(new SwatchRow { Text = "Accent colour", Value = c.AccentColor, Tip = "Only changes the top bar; Windows keeps its own accent", R = Line(52), Changed = col => { c.AccentColor = col; c.Apply(); } });
                Add(new Segmented { Text = "Font", Options = new[] { "Segoe UI", "Montserrat" }, Index = c.Font, Tip = Theme.MontserratAvailable ? null : "Montserrat needs its font files in the fonts folder; until then Segoe UI is used", R = Line(52),
                                    Changed = i => { c.Font = i; c.Apply(); BeginInvoke((Action)bar.ReopenSettings); } }); // this window's own text is built once
                Add(new Segmented { Text = "Cursor", Options = CustomCursors.Names, Index = c.CursorStyle, Tip = "Hardware Windows cursor theme (0% CPU); Accent, Minimal and Neon follow your accent colour", R = Line(52),
                                    Changed = i => { c.CursorStyle = i; c.Apply(); CustomCursors.Apply(i, Theme.Accent); } });
                Add(new Caption { Text = "Bar transparency", Font = Theme.Body, Strong = true, R = Line(30) });
                Add(new Slider { Value = c.Transparency / 100f, R = Line(40), Changed = v => { c.Transparency = (int)Math.Round(v * 100); c.Apply(); } });
                AddToggle("Floating bar (docks when a window is maximized)", c.Floating, v => { c.Floating = v; c.Apply(); });
                Add(new Segmented { Text = "Bar height", Options = new[] { "Compact", "Default", "Tall" }, Index = Math.Max(0, Array.IndexOf(Heights, c.Height)), R = Line(52), Changed = i => { c.Height = Heights[i]; c.Apply(); } });
                break;
            case 1:
                Add(new Segmented { Text = "Activities", Options = new[] { "Left", "Center", "Right", "Hidden" }, Index = c.ActivitiesPos, R = Line(52), Changed = i => { c.ActivitiesPos = i; c.Apply(); } });
                Add(new Segmented { Text = "Status icons", Options = lcr, Index = c.StatusPos, R = Line(52), Changed = i => { c.StatusPos = i; c.Apply(); } });
                Add(new Segmented { Text = "Clock", Options = lcr, Index = c.ClockPos, R = Line(52), Changed = i => { c.ClockPos = i; c.Apply(); } });
                AddToggle("File shelf next to Activities", c.ShowShelf, v => { c.ShowShelf = v; c.Apply(); });
                AddToggle("Clipboard history next to Activities", c.ShowClipboard, v => { c.ShowClipboard = v; c.Apply(); });
                AddToggle("Notes next to the clipboard (Alt+N)", c.ShowNotes, v => { c.ShowNotes = v; c.Apply(); });
                AddToggle("Connected devices pill", c.ShowDevices, v => { c.ShowDevices = v; c.Apply(); });
                AddToggle("Traffic lights for maximized windows (close, minimize, restore)", c.WinControls, v => { c.WinControls = v; c.Apply(); });
                AddToggle("Transparent when no window is maximized", c.ClearBar, v => { c.ClearBar = v; c.Apply(); });
                AddToggle("Match the app's colour when a window is maximized", c.MatchApp, v => { c.MatchApp = v; c.Apply(); });
                break;
            case 2:
                AddToggle("24-hour time", c.Clock24, v => { c.Clock24 = v; c.Apply(); });
                AddToggle("Show date", c.ShowDate, v => { c.ShowDate = v; c.Apply(); });
                break;
            case 3:
                AddToggle("Show media player", c.ShowMedia, v => { c.ShowMedia = v; c.Apply(); });
                AddToggle("Animate visualizer", c.MediaAnim, v => { c.MediaAnim = v; c.Apply(); });
                break;
            case 4:
                AddToggle("Search bar in the center", c.ShowSearch, v => { c.ShowSearch = v; c.Apply(); });
                Add(new ChoiceRow { Text = "Search with", Options = SearchPopup.Names, Index = c.SearchEngine, R = Line(52), Changed = i => { c.SearchEngine = i; c.Apply(); } });
                break;
            case 5:
                var titles = new[] { "Clock", "Weather", "Calendar", "System (CPU, memory, battery)", "App grid", "Connected devices", "Now playing", "Notes" };
                for (int i = 0; i < titles.Length; i++) { string n = TopBar.WidgetNames[i]; AddToggle(titles[i], c.WidgetOn(n), v => { c.SetW(n, "on", v ? 1 : 0); c.Apply(); }); }
                AddToggle("Lock widgets in place", c.LockWidgets, v => { c.LockWidgets = v; c.Apply(); });
                Add(new TextField { Value = new System.Text.StringBuilder(c.City), Icon = G.Search, Placeholder = "Weather city, then Enter", Tip = "Sent to Open-Meteo to look up the place", R = Line(52),
                                    Submit = v => { c.City = Config.CleanCity(v); c.Apply(); } });
                Add(new Segmented { Text = "Temperature", Options = new[] { "°C", "°F" }, Index = c.Fahrenheit ? 1 : 0, R = Line(52), Changed = i => { c.Fahrenheit = i == 1; c.Apply(); } });
                Add(new Caption { Text = "Drag widgets anywhere; right-click one for its style and size.", R = Line(30) });
                break;
            default:
                AddToggle("Show battery percentage", c.ShowPercent, v => { c.ShowPercent = v; c.Apply(); });
                AddToggle("Replace Windows' volume and brightness pop-up", c.VolumeOsd, v => { c.VolumeOsd = v; c.Apply(); });
                AddToggle("Smart suggestions in the bar", c.SmartHints, v => { c.SmartHints = v; c.Apply(); });
                Add(new Segmented { Text = "Phone desktop mode size", Options = new[] { "Smaller", "Like Windows", "Larger" }, Index = c.PhoneScale, Tip = "How big things look on your phone's desktop. Applies the next time you open it.", R = Line(52), Changed = i => { c.PhoneScale = i; c.Apply(); } });
                Add(new MenuRow { Icon = "", Text = "Back up settings, app grid and notes…", Compact = true, R = Line(40), Click = SaveBackup });
                Add(new MenuRow { Icon = G.Restart, Text = "Restore from a backup…", Compact = true, R = Line(40), Click = RestoreBackup });
                AddToggle("Start with Windows", Config.StartWithWindows, v => Config.StartWithWindows = v);
                Add(new Segmented { Text = "App icons", Options = new[] { "Original", "Squircle" }, Index = Squircles.Applied ? 1 : 0, Tip = "Tinted squircles for shortcuts on your desktop and in your Start menu. Original puts every icon back.", R = Line(52),
                                    Changed = i => { Cursor = Cursors.WaitCursor; if (i == 1) Squircles.Apply(); else Squircles.Undo(); Cursor = Cursors.Default; } });
                Add(new MenuRow { Icon = G.Restart, Text = "Update squircles for new shortcuts", Compact = true, R = Line(40), Click = () => { Cursor = Cursors.WaitCursor; Squircles.Apply(); Cursor = Cursors.Default; } });
                if (App.Packaged) Add(new MenuRow { Icon = "\uE895", Text = "Updates come from the Microsoft Store", Compact = true, R = Line(40), Click = () => Shell.Open("ms-windows-store://downloadsandupdates") }); // Store policy: no self-updating
                else Add(new MenuRow { Icon = "\uE895", Text = Updater.Status, Compact = true, R = Line(40), Click = CheckUpdate });
                break;
        }
        End();
        Invalidate();
    }

    void CheckUpdate()
    {
        Action refresh = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Build); } catch (InvalidOperationException) { } };
        if (Updater.LatestTag != null) Updater.Install(refresh);
        else Updater.Check(refresh);
    }

    void SaveBackup()
    {
        using (var d = new SaveFileDialog { Title = "Back up UpBar", Filter = "UpBar backup|*.zip", FileName = "UpBar backup " + DateTime.Now.ToString("yyyy-MM-dd") + ".zip" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try { int n = Backup.Save(d.FileName); MessageBox.Show(this, "Saved " + n + " files to " + d.FileName, "UpBar"); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't save the backup: " + e.Message, "UpBar"); }
        }
    }

    void RestoreBackup()
    {
        using (var d = new OpenFileDialog { Title = "Restore UpBar", Filter = "UpBar backup|*.zip" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            int n;
            try { n = Backup.Restore(d.FileName); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't read that backup: " + e.Message, "UpBar"); return; }
            bar.Restored();
            MessageBox.Show(this, "Restored " + n + " files. Your previous settings are in topbar.ini.before-restore.", "UpBar");
            BeginInvoke((Action)bar.ReopenSettings); // this window shows the restored settings
        }
    }

    void Begin() { segTop = y; seg = Add(new Segment()); y += 6 * S; }

    RectangleF Line(float h) { var r = new RectangleF(x + 16 * S, y, w - 32 * S, h * S); y += h * S; return r; }

    void End() { y += 6 * S; seg.R = new RectangleF(x, segTop, w, y - segTop); }

    void AddToggle(string text, bool on, Action<bool> set) { Add(new Toggle { Text = text, On = on, R = Line(48), Changed = set }); }

    // Drag the window by its header strip.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x84 || (int)m.Result != 1) return; // WM_NCHITTEST on client area
        long lp = m.LParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        if (p.Y < 64 * S && p.X < Width - 72 * S) m.Result = (IntPtr)2; // HTCAPTION
    }
}
