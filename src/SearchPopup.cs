using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;

// Search box under the centre pill: Enter opens the chosen engine or AI chat in the default browser.
class SearchPopup : Glass
{
    public static readonly string[] Names = { "Google", "Bing", "DuckDuckGo", "Claude", "ChatGPT", "Gemini", "Perplexity", "Custom" };
    static readonly string[] Urls =
    {
        "https://www.google.com/search?q=%s", "https://www.bing.com/search?q=%s", "https://duckduckgo.com/?q=%s",
        "https://claude.ai/new?q=%s", "https://chatgpt.com/?q=%s", "https://gemini.google.com/app?q=%s",
        "https://www.perplexity.ai/search?q=%s", null
    };

    public static string Url(Config c, string q)
    {
        string t = c.SearchEngine < 7 ? Urls[c.SearchEngine] : c.SearchUrl;
        if (string.IsNullOrEmpty(t)) t = Urls[0]; // custom chosen but no searchUrl in topbar.ini
        return t.Replace("%s", Uri.EscapeDataString(q));
    }

    readonly Config cfg;
    readonly TextField field;

    // Slim Spotlight-style field. The engine is named in the placeholder; Up/Down switches it.
    public SearchPopup(Config c, float s) : base(s)
    {
        cfg = c;
        float W = 560 * S, pad = 6 * S, h = 44 * S;
        field = Add(new TextField { Icon = G.Search, Flat = true, Font = Theme.Body, Tip = "Up/Down switches the search engine", R = new RectangleF(pad + 6 * S, pad, W - 2 * pad - 12 * S, h), Submit = Go, Cycle = Step, Changed = Results });
        System.Threading.ThreadPool.QueueUserWorkItem(_ => Apps()); // index Start menu apps in the background
        Corner = 16;
        Size = new Size((int)W, (int)(h + 2 * pad));
        ShowEngine(c.SearchEngine);
    }

    int Count { get { return string.IsNullOrEmpty(cfg.SearchUrl) ? 7 : 8; } } // custom only once configured

    void Step(int d) { Pick((cfg.SearchEngine + d + Count) % Count); }

    void ShowEngine(int i) { field.Placeholder = "Search with " + Names[i]; Invalidate(); }

    protected override void OnShown(EventArgs e) { base.OnShown(e); FocusOn(field); }

    void Pick(int i)
    {
        cfg.SearchEngine = Math.Min(i, Count - 1);
        cfg.Apply();
        ShowEngine(cfg.SearchEngine);
        FocusOn(field);
    }

    void Go(string q)
    {
        q = q.Trim();
        if (q.Length == 0) return;
        if (top != null) { top(); return; } // Enter takes the first result: an answer, a conversion, an app
        Close();
        Shell.Open(Url(cfg, q));
    }

    // ---- quick answers under the field: maths, unit conversions, apps to open
    Action top;
    void Results()
    {
        Items.RemoveAll(w => w is MenuRow || w is IconBtn);
        top = null;
        var admin = new Dictionary<MenuRow, string>(); // app rows -> shortcut, for their "Run as administrator" button
        string q = field.Value.ToString().Trim();
        var rows = new List<MenuRow>();
        double? v = Calc(q.TrimStart('='));
        if (v != null && Regex.IsMatch(q, @"[-+*/^%(]")) { string a = Format(v.Value); rows.Add(new MenuRow { Icon = "\uE8EF", Text = "= " + a, Sub = "Enter copies the answer", Compact = true, Click = () => Copy(a) }); }
        string conv = Convert(q);
        if (conv != null) rows.Add(new MenuRow { Icon = "\uE8EF", Text = conv, Sub = "Enter copies it", Compact = true, Click = () => Copy(conv.Substring(conv.IndexOf('=') + 1).Trim()) });
        if (q.Length >= 2)
            foreach (var app in Match(q, 4))
            {
                var path = app.Value;
                var row = new MenuRow { Icon = "\uE8E1", Img = AppIcon(path), Text = app.Key, Sub = "Open app", Compact = true, Click = () => { Close(); Shell.Open(path); } };
                rows.Add(row);
                admin[row] = path;
            }
        if (rows.Count > 0) top = rows[0].Click;
        if (q.Length > 0) rows.Add(new MenuRow { Icon = G.Search, Text = "Search " + Names[cfg.SearchEngine] + " for \u201c" + (q.Length > 40 ? q.Substring(0, 39) + "…" : q) + "\u201d", Compact = true, Click = () => { Close(); Shell.Open(Url(cfg, q)); } });
        float pad = 6 * S, h = 44 * S, y = pad + h + 4 * S, rh = 44 * S;
        float x0 = pad + 4 * S, rw = Width - 2 * pad - 8 * S, bs = 36 * S;
        foreach (var r in rows)
        {
            string lnk;
            bool app = admin.TryGetValue(r, out lnk);
            r.R = new RectangleF(x0, y, app ? rw - bs - 4 * S : rw, rh);
            Add(r);
            if (app) Add(new IconBtn { Icon = "", Tip = "Run as administrator", R = new RectangleF(x0 + rw - bs, y + (rh - bs) / 2, bs, bs), Click = () => RunAsAdmin(lnk) }); // EA18: shield
            y += rh;
        }
        Place(Width, (int)(y + (rows.Count > 0 ? pad : 0)));
        Invalidate();
    }

    void RunAsAdmin(string lnk)
    {
        Close();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lnk) { Verb = "runas", UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { } // UAC prompt declined
    }

    // 32 px shell icons, kept per shortcut for the session (a few KB each, only for apps that were shown).
    static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    static Image AppIcon(string lnk)
    {
        Image i;
        if (!icons.TryGetValue(lnk, out i)) { try { i = Squircles.ShellIcon(lnk, 32); } catch (Exception) { } icons[lnk] = i; }
        return i;
    }

    void Copy(string s) { try { System.Windows.Forms.Clipboard.SetText(s); } catch (System.Runtime.InteropServices.ExternalException) { } Close(); }

    static string Format(double v) { return Math.Abs(v) >= 1e15 || (Math.Abs(v) < 1e-6 && v != 0) ? v.ToString("G6") : Math.Round(v, 8).ToString("#,0.########"); }

    // Arithmetic only (+ - * / ^ %, brackets, decimals), parsed by hand: nothing is ever run as code.
    public static double? Calc(string s)
    {
        if (string.IsNullOrEmpty(s) || !Regex.IsMatch(s, @"^[\d\s.+\-*/^%()x×÷,]+$") || !Regex.IsMatch(s, @"\d")) return null;
        s = s.Replace(" ", "").Replace(",", "").Replace("x", "*").Replace("×", "*").Replace("÷", "/");
        int i = 0;
        try { double v = Sum(s, ref i); return i == s.Length && !double.IsNaN(v) && !double.IsInfinity(v) ? v : (double?)null; }
        catch (FormatException) { return null; }
    }
    static double Sum(string s, ref int i) { double v = Product(s, ref i); while (i < s.Length && (s[i] == '+' || s[i] == '-')) { char o = s[i++]; double r = Product(s, ref i); v = o == '+' ? v + r : v - r; } return v; }
    static double Product(string s, ref int i) { double v = Power(s, ref i); while (i < s.Length && (s[i] == '*' || s[i] == '/' || s[i] == '%')) { char o = s[i++]; double r = Power(s, ref i); v = o == '*' ? v * r : o == '/' ? v / r : v % r; } return v; }
    static double Power(string s, ref int i) { double v = Unary(s, ref i); if (i < s.Length && s[i] == '^') { i++; v = Math.Pow(v, Power(s, ref i)); } return v; }
    static double Unary(string s, ref int i)
    {
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { bool neg = s[i++] == '-'; double u = Unary(s, ref i); return neg ? -u : u; }
        if (i < s.Length && s[i] == '(') { i++; double v = Sum(s, ref i); if (i >= s.Length || s[i] != ')') throw new FormatException(); i++; return v; }
        int st = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (st == i) throw new FormatException();
        return double.Parse(s.Substring(st, i - st), System.Globalization.CultureInfo.InvariantCulture);
    }

    // "10 km in mi", "70 kg to lb", "100 f to c"
    static readonly Dictionary<string, double> Units = new Dictionary<string, double>
    {
        { "mm", 0.001 }, { "cm", 0.01 }, { "m", 1 }, { "km", 1000 }, { "in", 0.0254 }, { "ft", 0.3048 }, { "yd", 0.9144 }, { "mi", 1609.344 },
        { "mg", 1e-6 }, { "g", 0.001 }, { "kg", 1 }, { "oz", 0.028349523125 }, { "lb", 0.45359237 },
        { "ml", 0.001 }, { "l", 1 }, { "gal", 3.785411784 }, { "kb", 1e3 }, { "mb", 1e6 }, { "gb", 1e9 }, { "tb", 1e12 }
    };
    static readonly string[][] Kinds = { new[] { "mm", "cm", "m", "km", "in", "ft", "yd", "mi" }, new[] { "mg", "g", "kg", "oz", "lb" }, new[] { "ml", "l", "gal" }, new[] { "kb", "mb", "gb", "tb" } };
    public static string Convert(string q)
    {
        var m = Regex.Match(q.ToLowerInvariant(), @"^(-?[\d.]+)\s*(°?[a-z]+)\s+(?:in|to)\s+(°?[a-z]+)$");
        double n;
        if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n)) return null;
        string a = m.Groups[2].Value.TrimStart('°'), b = m.Groups[3].Value.TrimStart('°');
        if ("cfk".Contains(a) && "cfk".Contains(b) && a.Length == 1 && b.Length == 1) // temperatures
        {
            double c = a == "c" ? n : a == "f" ? (n - 32) * 5 / 9 : n - 273.15, r = b == "c" ? c : b == "f" ? c * 9 / 5 + 32 : c + 273.15;
            return n + "°" + a.ToUpper() + " = " + Format(Math.Round(r, 2)) + "°" + b.ToUpper();
        }
        foreach (var k in Kinds)
            if (Array.IndexOf(k, a) >= 0 && Array.IndexOf(k, b) >= 0) return n + " " + a + " = " + Format(Math.Round(n * Units[a] / Units[b], 6)) + " " + b;
        return null;
    }

    // Start menu apps: name -> shortcut, read once in the background and kept for 10 minutes.
    static List<KeyValuePair<string, string>> apps = new List<KeyValuePair<string, string>>();
    static DateTime appsAt;
    static void Apps()
    {
        if ((DateTime.Now - appsAt).TotalMinutes < 10 && apps.Count > 0) return;
        var list = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) })
        {
            try
            {
                foreach (var f in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    if (n.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0 || !seen.Add(n)) continue;
                    list.Add(new KeyValuePair<string, string>(n, f));
                }
            }
            catch (Exception) { } // a folder we can't read
        }
        apps = list;
        appsAt = DateTime.Now;
    }

    // Names starting with the query first, then names with a word starting with it.
    public static List<KeyValuePair<string, string>> Match(string q, int max)
    {
        var a = apps;
        var outp = new List<KeyValuePair<string, string>>();
        foreach (var p in a) if (p.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase) && outp.Count < max) outp.Add(p);
        foreach (var p in a) if (outp.Count < max && !outp.Contains(p) && p.Key.IndexOf(" " + q, StringComparison.OrdinalIgnoreCase) >= 0) outp.Add(p);
        return outp;
    }
}

// The phone's apps, to open on its desktop-mode screen (the phone's own taskbar can't launch there: see Health.Desktop).
// Type to filter; Enter or a click opens the first/clicked app and closes, so the desktop button reopens it for the next.
class PhoneAppsPopup : Glass
{
    const int MaxRows = 8; // shortcut: no scrolling; typing narrows 160-odd apps fast enough
    readonly List<KeyValuePair<string, string>> apps;
    readonly TextField field;
    readonly Action<string> open; // package -> opens it (the bar runs it on a worker)
    Action top;

    public PhoneAppsPopup(float s, string phone, List<KeyValuePair<string, string>> apps, Action<string> open) : base(s)
    {
        this.apps = apps;
        this.open = open;
        float W = 380 * S, pad = 6 * S, h = 44 * S;
        field = Add(new TextField { Icon = G.Search, Flat = true, Font = Theme.Body, Placeholder = "Open on " + phone + " desktop", R = new RectangleF(pad + 6 * S, pad, W - 2 * pad - 12 * S, h), Submit = q => { if (top != null) top(); }, Changed = Results });
        Corner = 16;
        Size = new Size((int)W, (int)(h + 2 * pad));
        Results();
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); FocusOn(field); }

    void Results()
    {
        Items.RemoveAll(w => w is MenuRow);
        top = null;
        string q = field.Value.ToString().Trim();
        var rows = new List<MenuRow>();
        foreach (var a in apps) // names starting with the query first, then any word starting with it
            if (rows.Count < MaxRows && (q.Length == 0 || a.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase))) rows.Add(Row(a));
        if (q.Length > 0)
            foreach (var a in apps)
                if (rows.Count < MaxRows && !a.Key.StartsWith(q, StringComparison.OrdinalIgnoreCase) && a.Key.IndexOf(" " + q, StringComparison.OrdinalIgnoreCase) >= 0) rows.Add(Row(a));
        if (rows.Count > 0) top = rows[0].Click;
        float pad = 6 * S, y = pad + 44 * S + 4 * S, rh = 38 * S;
        if (rows.Count == 0) rows.Add(new MenuRow { Text = apps.Count == 0 ? "Couldn't read the phone's apps" : "No app called “" + q + "”", Compact = true });
        foreach (var r in rows) { r.R = new RectangleF(pad + 4 * S, y, Width - 2 * pad - 8 * S, rh); Add(r); y += rh; }
        Place(Width, (int)(y + pad));
        Invalidate();
    }

    MenuRow Row(KeyValuePair<string, string> a)
    {
        string pkg = a.Value;
        return new MenuRow { Icon = G.Phone, Text = a.Key, Compact = true, Click = () => { Close(); open(pkg); } };
    }
}
