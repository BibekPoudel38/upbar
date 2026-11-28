using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

// Where Upbar keeps its files. From GitHub: next to Upbar.exe, as always. From the Microsoft Store the install
// folder is read-only, so settings, notes and the rest live in %LOCALAPPDATA%/Upbar instead (the manifest turns off
// write virtualization, so that is the real folder Explorer and Windows see, which squircle icons and cursors need).
static class App
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);

    public static readonly bool Packaged = HasPackage();
    static bool HasPackage()
    {
        try { int n = 0; return GetCurrentPackageFullName(ref n, null) != 15700; } // APPMODEL_ERROR_NO_PACKAGE
        catch (EntryPointNotFoundException) { return false; } // Windows 7
    }

    static string data;
    public static string Data
    {
        get
        {
            if (data != null) return data;
            string d = Packaged ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Upbar") : AppDomain.CurrentDomain.BaseDirectory;
            try { Directory.CreateDirectory(d); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return data = d;
        }
    }
    public static string File(string name) { return Path.Combine(Data, name); }
}

// Bar preferences, stored as key=value in topbar.ini (see App.Data).
class Config
{
    public int Transparency = 0;                              // 0..100 % see-through
    public int Height = 30;                                   // logical px
    public int ActivitiesPos = 0, StatusPos = 2, ClockPos = 2; // 0 left, 1 center, 2 right, 3 hidden
    public bool Clock24, ShowDate = true, ShowPercent = true, ShowMedia = true, MediaAnim = true, Blur, ShowShelf = true, ShowClipboard = true, Floating, ShowSearch = true;
    public int SearchEngine, GlassStyle = 2, Font, CursorStyle; // SearchEngine: index into SearchPopup.Names (7 = custom); GlassStyle: index into Glass.StyleNames; Font: 0 Segoe UI, 1 Montserrat; CursorStyle: 0 Windows, 1..4 custom
    public string Accent = "";                    // "#RRGGBB" overrides the accent inside this app only; "" follows Windows

    public System.Drawing.Color AccentColor
    {
        get { return Accent.Length == 7 ? System.Drawing.ColorTranslator.FromHtml(Accent) : System.Drawing.Color.Empty; }
        set { Accent = value.IsEmpty ? "" : string.Format("#{0:X2}{1:X2}{2:X2}", value.R, value.G, value.B); }
    }
    public bool Fahrenheit, LockWidgets, ClearBar, VolumeOsd, SmartHints = true, MatchApp = true, ShowNotes = true, ShowDevices = true, WinControls; // traffic lights: off unless switched on
    public int OsdStyle, BrightStyle, OsdPos;                 // volume / brightness pop-up: style 0 ring, 1 bar, 2 vertical; OsdPos: VolumeOsd.Positions
    public int PhoneScale = 1;                                // phone desktop mode: 0 smaller, 1 like Windows, 2 larger
    public int DbAlert = 85;                                  // sound level meter: dB that counts as loud                      // desktop widgets
    public string City = "";                                  // weather location, sent to Open-Meteo's search
    // Per-widget settings as "name.prop" -> value, stored as widget.<name>.<prop>=<int> (props: on, style, size, x, y).
    readonly System.Collections.Generic.SortedDictionary<string, int> widgets = new System.Collections.Generic.SortedDictionary<string, int>();
    static readonly string[] WidgetProps = { "on", "style", "size", "x", "y" };

    public int W(string name, string prop, int fallback) { int v; return widgets.TryGetValue(name + "." + prop, out v) ? v : fallback; }
    public void SetW(string name, string prop, int v) { widgets[name + "." + prop] = v; }
    public bool WidgetOn(string name) { return W(name, "on", 0) != 0; }

    void ParseWidget(string k, int v)
    {
        var p = k.Split('.');
        if (p.Length != 3 || p[1].Length == 0 || p[1].Length > 20 || !System.Text.RegularExpressions.Regex.IsMatch(p[1], "^[a-z]+$") || Array.IndexOf(WidgetProps, p[2]) < 0) return;
        if (p[2] == "style") v = Clamp(v, 0, 9); // each widget treats an unknown style as its first
        if (p[2] == "size") v = Clamp(v, 0, 4);
        if (p[2] == "on") v = v != 0 ? 1 : 0;
        SetW(p[1], p[2], v);
    }

    // Old single-purpose keys from the first widget version: clockAt=x,y and weatherAt=x,y.
    void ParseOldSpot(string name, string v)
    {
        var p = v.Split(',');
        int x, y;
        if (p.Length == 2 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y) && (x != 0 || y != 0)) { SetW(name, "x", x); SetW(name, "y", y); }
    }
    public string SearchUrl = "";                             // custom template with %s, http(s) only
    public string HiddenTiles = "";
    public string TileSizes = "";                             // "key:size,..." 0 = 1x1, 1 = 1x2 (default), 2 = 2x2                           // comma-separated control center tile keys
    public event Action Changed;

    public int TileSize(string key)
    {
        foreach (var kv in TileSizes.Split(',')) { var p = kv.Split(':'); int n; if (p.Length == 2 && p[0] == key && int.TryParse(p[1], out n)) return Clamp(n, 0, 2); }
        return 1;
    }

    public void CycleTileSize(string key)
    {
        int next = new[] { 2, 0, 1 }[TileSize(key)]; // 1x2 -> 2x2 -> 1x1 -> 1x2
        var list = new System.Collections.Generic.List<string>(TileSizes.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        list.RemoveAll(kv => kv.StartsWith(key + ":"));
        if (next != 1) list.Add(key + ":" + next);
        TileSizes = string.Join(",", list);
        Apply();
    }

    public bool IsTileHidden(string key) { return IsTileHidden(key, false); }

    // Tiles that start hidden (offByDefault) are listed as "+key" once you add them; the others as "key" once removed.
    public bool IsTileHidden(string key, bool offByDefault)
    {
        var list = HiddenTiles.Split(',');
        return offByDefault ? Array.IndexOf(list, "+" + key) < 0 : Array.IndexOf(list, key) >= 0;
    }

    public void ToggleTile(string key, bool offByDefault) { ToggleTile(offByDefault ? "+" + key : key); }

    public void ToggleTile(string key)
    {
        var list = new System.Collections.Generic.List<string>(HiddenTiles.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        if (!list.Remove(key)) list.Add(key);
        HiddenTiles = string.Join(",", list);
        Apply();
    }

    static string FilePath { get { return App.File("topbar.ini"); } }

    public static Config Load()
    {
        var c = new Config();
        try { if (File.Exists(FilePath)) c.Parse(File.ReadAllText(FilePath)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return c;
    }

    // Hand-edited file: ignore unknown keys and bad values, clamp ranges.
    public void Parse(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            string k = kv[0].Trim(), v = kv[1].Trim();
            int n; bool b;
            bool isInt = int.TryParse(v, out n), isBool = bool.TryParse(v, out b);
            switch (k)
            {
                case "transparency": if (isInt) Transparency = Clamp(n, 0, 100); break;
                case "height": if (isInt) Height = Clamp(n, 22, 48); break;
                case "activities": if (isInt) ActivitiesPos = Clamp(n, 0, 3); break;
                case "status": if (isInt) StatusPos = Clamp(n, 0, 2); break;
                case "clock": if (isInt) ClockPos = Clamp(n, 0, 2); break;
                case "clock24": if (isBool) Clock24 = b; break;
                case "showDate": if (isBool) ShowDate = b; break;
                case "showPercent": if (isBool) ShowPercent = b; break;
                case "showMedia": if (isBool) ShowMedia = b; break;
                case "mediaAnim": if (isBool) MediaAnim = b; break;
                case "blur": if (isBool) Blur = b; break;
                case "showShelf": if (isBool) ShowShelf = b; break;
                case "showClipboard": if (isBool) ShowClipboard = b; break;
                case "floating": if (isBool) Floating = b; break;
                case "showSearch": if (isBool) ShowSearch = b; break;
                case "font": if (isInt) Font = Clamp(n, 0, 1); break;
                case "widgetClock": if (isBool) SetW("clock", "on", b ? 1 : 0); break;
                case "widgetWeather": if (isBool) SetW("weather", "on", b ? 1 : 0); break;
                case "lockWidgets": if (isBool) LockWidgets = b; break;
                case "clearBar": if (isBool) ClearBar = b; break;
                case "volumeOsd": if (isBool) VolumeOsd = b; break;
                case "smartHints": if (isBool) SmartHints = b; break;
                case "matchApp": if (isBool) MatchApp = b; break;
                case "showNotes": if (isBool) ShowNotes = b; break;
                case "showDevices": if (isBool) ShowDevices = b; break;
                case "winControls": if (isBool) WinControls = b; break;
                case "dbAlert": if (isInt) DbAlert = Clamp(n, 40, 120); break;
                case "osdStyle": if (isInt) OsdStyle = Clamp(n, 0, 2); break;
                case "brightStyle": if (isInt) BrightStyle = Clamp(n, 0, 2); break;
                case "osdPos": if (isInt) OsdPos = Clamp(n, 0, 3); break;
                case "phoneScale": if (isInt) PhoneScale = Clamp(n, 0, 2); break;
                case "cursor": if (isInt) CursorStyle = Clamp(n, 0, 4); break;
                case "fahrenheit": if (isBool) Fahrenheit = b; break;
                case "city": City = CleanCity(v); break;
                case "clockAt": ParseOldSpot("clock", v); break;
                case "weatherAt": ParseOldSpot("weather", v); break;
                case "accent": if (v.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(v, "^#[0-9A-Fa-f]{6}$")) Accent = v; break;
                case "glass": if (isInt) GlassStyle = Clamp(n, 0, Glass.StyleNames.Length - 1); break;
                case "searchEngine": if (isInt) SearchEngine = Clamp(n, 0, 7); break;
                // Only web addresses: this string is handed to the shell, so never let it name a program.
                case "searchUrl": if ((v.StartsWith("https://") || v.StartsWith("http://")) && v.Contains("%s")) SearchUrl = v; break;
                case "tileSizes": TileSizes = new string(Array.FindAll(v.ToCharArray(), ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '2') || ch == ',' || ch == ':')); break;
                case "hiddenTiles": HiddenTiles = new string(Array.FindAll(v.ToCharArray(), ch => (ch >= 'a' && ch <= 'z') || ch == ',' || ch == '+')); break;
                default: if (k.StartsWith("widget.") && isInt) ParseWidget(k, n); break;
            }
        }
    }

    public string Serialize()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in widgets) sb.Append("widget.").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
        return string.Format("transparency={0}\nheight={1}\nactivities={2}\nstatus={3}\nclock={4}\nclock24={5}\nshowDate={6}\nshowPercent={7}\nshowMedia={8}\nmediaAnim={9}\nhiddenTiles={10}\nblur={11}\nshowShelf={12}\nshowClipboard={13}\nfloating={14}\nshowSearch={15}\nsearchEngine={16}\nsearchUrl={17}\nglass={18}\ntileSizes={19}\nfont={20}\naccent={21}\nfahrenheit={22}\ncity={23}\nlockWidgets={24}\nclearBar={25}\nvolumeOsd={26}\nsmartHints={27}\nmatchApp={28}\nshowNotes={29}\ndbAlert={30}\nwinControls={31}\nphoneScale={32}\nshowDevices={33}\ncursor={34}\nosdStyle={35}\nbrightStyle={36}\nosdPos={37}\n",
            Transparency, Height, ActivitiesPos, StatusPos, ClockPos, Clock24, ShowDate, ShowPercent, ShowMedia, MediaAnim, HiddenTiles, Blur, ShowShelf, ShowClipboard, Floating, ShowSearch, SearchEngine, SearchUrl, GlassStyle, TileSizes, Font, Accent,
            Fahrenheit, City, LockWidgets, ClearBar, VolumeOsd, SmartHints, MatchApp, ShowNotes, DbAlert, WinControls, PhoneScale, ShowDevices, CursorStyle, OsdStyle, BrightStyle, OsdPos) + sb;
    }

    // One line in an ini file: no control characters, bounded length.
    public static string CleanCity(string v)
    {
        var s = new string(Array.FindAll(v.ToCharArray(), ch => !char.IsControl(ch))).Trim();
        return s.Length > 60 ? s.Substring(0, 60) : s;
    }

    public void Apply()
    {
        try { File.WriteAllText(FilePath, Serialize()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Changed != null) Changed();
    }

    static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Upbar", OldRunName = "ClaudeTopBar";

    // From the Store, start-up is the package's StartupTask (AppxManifest.xml), switched through WinRT; a Run key
    // pointing into the package folder wouldn't start it with its identity. From GitHub: the Run key, as before.
    const string TaskId = "UpbarStartup";
    public static bool StartWithWindows
    {
        get
        {
            if (App.Packaged)
                try { var st = Sys.Wait(Windows.ApplicationModel.StartupTask.GetAsync(TaskId)).State; return st == Windows.ApplicationModel.StartupTaskState.Enabled || st == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy; }
                catch (Exception) { return false; }
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && (k.GetValue(RunName) != null || k.GetValue(OldRunName) != null); }
        set
        {
            if (App.Packaged)
            {
                try { var t = Sys.Wait(Windows.ApplicationModel.StartupTask.GetAsync(TaskId)); if (value) Sys.Wait(t.RequestEnableAsync()); else t.Disable(); }
                catch (Exception) { } // turned off by the user in Task Manager › Startup apps: only they can turn it back on there
                return;
            }
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                k.DeleteValue(OldRunName, false); // the entry from before the rename
                if (value) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }
    }
}
