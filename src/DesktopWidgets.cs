using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

/// A widget pinned to the desktop: always at the bottom of the window stack, never takes focus. Drag it anywhere
// (unless widgets are locked); right-click for style, size, lock and remove. Sizes are home-screen grid cells
// (rows x columns); each style draws a fixed design that is scaled to fit its cells. Animations only run while
// the mouse is over it.
abstract class DesktopWidget : Glass
{
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public static readonly string[] Sizes = { "1×1", "1×2", "2×2", "2×4", "4×4" }; // rows × columns
    static readonly int[] Rows = { 1, 1, 2, 2, 4 }, Cols = { 1, 2, 2, 4, 4 };
    const float Cell = 100, Gap = 12; // logical px: grid pitch, and the space left between neighbouring widgets

    // Widgets draw their material through a blur accent so they keep their own big rounded corners and cast no shadow.
    protected override bool SystemCorners { get { return false; } }
    // Widgets sit on the desktop, so the only thing behind them is the wallpaper: for the see-through styles they draw
    // a blurred copy of it themselves. Exact rounded corners, no shadow, and no compositor blur needed.
    static bool SeeThrough { get { return Translucent; } }
    protected override int Material { get { return SeeThrough ? 1 : base.Material; } } // 1 = no system backdrop
    protected override string MaterialKey { get { return SeeThrough ? Left + "," + Top + "," + wallVersion : ""; } } // the wallpaper behind it
    static int wallVersion;
    static Bitmap wall;            // the primary screen's wallpaper at 1/24 size: drawn back up, it is a smooth blur
    static Rectangle wallScreen;
    const int WallShrink = 24;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(int action, int size, System.Text.StringBuilder buf, int ini);

    // How bright the wallpaper is under a screen rectangle (0 dark .. 1 bright), from the small cached copy.
    public static float WallLuma(Rectangle r)
    {
        var img = Wallpaper();
        int x0 = Math.Max(0, (r.Left - wallScreen.X) / WallShrink), x1 = Math.Min(img.Width - 1, (r.Right - wallScreen.X) / WallShrink);
        int y0 = Math.Max(0, (r.Top - wallScreen.Y) / WallShrink), y1 = Math.Min(img.Height - 1, Math.Max(y0, (r.Bottom - wallScreen.Y) / WallShrink));
        float sum = 0; int n = 0;
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { sum += Native.Luma(img.GetPixel(x, y)); n++; }
        return n == 0 ? 0 : sum / n;
    }
    public static int WallVersion { get { return wallVersion; } }

    public static void ReloadWallpaper() { if (wall != null) { wall.Dispose(); wall = null; } wallVersion++; }

    // shortcut: primary monitor's wallpaper only, and "fill" placement for anything but Fit/Stretch; per-monitor wallpapers would need IDesktopWallpaper.
    static Bitmap Wallpaper()
    {
        if (wall != null) return wall;
        wallScreen = Screen.PrimaryScreen.Bounds;
        wall = new Bitmap(Math.Max(1, wallScreen.Width / WallShrink), Math.Max(1, wallScreen.Height / WallShrink), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(wall))
        {
            g.Clear(Color.FromArgb(24, 26, 30));
            var path = new System.Text.StringBuilder(520);
            SystemParametersInfo(0x73, path.Capacity, path, 0); // SPI_GETDESKWALLPAPER
            try
            {
                using (var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path.ToString())))) // no file lock
                {
                    string mode = "10";
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop")) if (k != null) mode = (k.GetValue("WallpaperStyle") as string) ?? "10";
                    float sw = wall.Width, sh = wall.Height, iw = img.Width, ih = img.Height;
                    float scale = mode == "6" ? Math.Min(sw / iw, sh / ih) : Math.Max(sw / iw, sh / ih); // 6 Fit, else Fill
                    var dst = mode == "2" ? new RectangleF(0, 0, sw, sh) : new RectangleF((sw - iw * scale) / 2, (sh - ih * scale) / 2, iw * scale, ih * scale); // 2 Stretch
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; // averages while shrinking: the first half of the blur
                    g.DrawImage(img, dst);
                }
            }
            catch (Exception) { } // no wallpaper file (solid colour background): the plain fill stays
        }
        return wall;
    }

    protected override void PaintMaterial(Graphics g, GraphicsPath card)
    {
        if (!SeeThrough) return;
        var img = Wallpaper();
        var st = g.Save();
        g.SetClip(card, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear; // smooth enlargement: the second half of the blur
        var src = new RectangleF((Left - wallScreen.X) / (float)WallShrink, (Top - wallScreen.Y) / (float)WallShrink, Width / (float)WallShrink, Height / (float)WallShrink);
        using (var ia = new System.Drawing.Imaging.ImageAttributes())
        {
            ia.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
            g.DrawImage(img, new Rectangle(0, 0, Width, Height), src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        g.Restore(st);
        // Acrylic: a luminous veil like Windows menus; Glass: the frost comes after.
        if (Style == 2) Native.Fill(g, card, Theme.Light ? Color.FromArgb(150, 243, 243, 243) : Color.FromArgb(150, 30, 30, 32));
        else if (Style == 4) Native.Fill(g, card, Theme.Light ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0)); // keeps text readable on bright wallpapers
    }

    // Grid snapping: widgets line up on one grid that starts at the work area's corner.
    public static int Snap(int v, int origin, float pitch) { return origin + (int)Math.Round((v - origin) / pitch) * (int)Math.Round(pitch); }
    Point Snapped(Point p)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int pad = (int)(Gap / 2 * S);
        return new Point(Snap(p.X, wa.X + pad, Cell * S), Snap(p.Y, wa.Y + pad, Cell * S));
    }
    public readonly Config Cfg;
    public readonly string Key;
    public readonly int Look, SizeIndex;
    readonly SizeF design;
    readonly float k, ox, oy;         // design -> window scale and centring offset
    public float Phase;               // animation clock in seconds; 0 when idle
    bool looping;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    double lastTick;
    DateTime animUntil;

    protected DesktopWidget(float s, Config c, string key, SizeF design, Point fallback) : base(s)
    {
        CloseOnDeactivate = false;
        TopMost = false;
        Cfg = c;
        Key = key;
        this.design = design;
        Look = c.W(key, "style", 0);
        SizeIndex = Fit(design, c.W(key, "size", -1));
        var size = new Size((int)((Cols[SizeIndex] * Cell - Gap) * S), (int)((Rows[SizeIndex] * Cell - Gap) * S)); // cells minus the gap: neighbours never touch
        k = Math.Min(size.Width / (design.Width * S), size.Height / (design.Height * S));
        ox = (size.Width - design.Width * S * k) / 2;
        oy = (size.Height - design.Height * S * k) / 2;
        int x = c.W(key, "x", int.MinValue), y = c.W(key, "y", int.MinValue);
        var spot = Snapped(x == int.MinValue ? fallback : new Point(x, y));
        var screen = Screen.FromPoint(spot).Bounds; // a bigger size (or a removed monitor) must not leave it off screen
        spot.X = Math.Max(screen.Left, Math.Min(spot.X, screen.Right - size.Width));
        spot.Y = Math.Max(screen.Top, Math.Min(spot.Y, screen.Bottom - size.Height));
        Bounds = new Rectangle(spot, size);
        Add(new View());

    }

    // A grid size suits a design when its shape is close and the content stays readable.
    public static bool Allowed(SizeF d, int i)
    {
        float scale = Math.Min(Cols[i] * Cell / d.Width, Rows[i] * Cell / d.Height), shape = d.Width / d.Height / ((float)Cols[i] / Rows[i]);
        return scale >= 0.45f && shape > 0.74f && shape < 1.36f;
    }

    // The wanted size if it suits, else the suitable size closest to it (or to the design's natural size).
    public static int Fit(SizeF d, int want)
    {
        if (want >= 0 && want < Sizes.Length && Allowed(d, want)) return want;
        int best = Sizes.Length - 1; double score = double.MaxValue;
        for (int i = 0; i < Sizes.Length; i++)
        {
            if (!Allowed(d, i)) continue;
            double sc = Math.Abs(Math.Log(Math.Min(Cols[i] * Cell / d.Width, Rows[i] * Cell / d.Height))) + (want >= 0 ? Math.Abs(i - want) : 0);
            if (sc < score) { score = sc; best = i; }
        }
        return best;
    }

    public abstract string Title { get; }
    public virtual string[] Styles { get { return null; } }
    public abstract void Draw(Graphics g, float w, float h); // design-unit canvas of w x h
    protected override bool Animated { get { return false; } } // widgets are rebuilt in place when restyled: no fade
    protected virtual bool HoverAnimates { get { return false; } }
    protected virtual bool ClientClicks { get { return false; } } // true: clicks reach the widget; a drag starts after a short move
    protected virtual void AddMenuItems(MenuPopup m, Point client) { }

    // Window point -> design canvas point (for widgets that take clicks).
    protected PointF ToCanvas(Point p)
    {
        return new PointF((p.X - ox) / k, (p.Y - oy) / k);
    }

    // Runs the widget's animation clock for ms milliseconds, one frame per display refresh.
    public void Animate(int ms)
    {
        animUntil = DateTime.Now.AddMilliseconds(ms);
        if (looping || IsDisposed || !IsHandleCreated) return;
        looping = true;
        lastTick = clock.Elapsed.TotalSeconds;
        BeginInvoke((Action)(() => Native.Vsync(() =>
        {
            if (IsDisposed) { looping = false; return false; }
            double t = clock.Elapsed.TotalSeconds;
            Phase += (float)Math.Min(0.05, t - lastTick);
            lastTick = t;
            bool more = DateTime.Now <= animUntil;
            if (!more) { Phase = 0; looping = false; }
            Invalidate();
            if (Visible) Update();
            return more;
        })));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000;   // NOACTIVATE: clicks never pull it above your windows
            p.Style &= ~0x00010000;    // no maximize box: double-clicks and snapping never maximize it
            p.Parent = FindWindow("Progman", null); // owned by the desktop, so Show desktop (Win+D, 3-finger swipe down) keeps it
            return p;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x46) // WM_WINDOWPOSCHANGING: whatever happens, stay at the bottom (on the desktop)
        {
            var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
            wp.after = (IntPtr)1; // HWND_BOTTOM
            wp.flags &= ~0x0004u; // drop SWP_NOZORDER
            Marshal.StructureToPtr(wp, m.LParam, false);
        }
        if (m.Msg == 0x216) // WM_MOVING: snap to the grid while dragging
        {
            var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            var p = Snapped(new Point(r.l, r.t));
            r = new Native.RECT { l = p.X, t = p.Y, r = p.X + (r.r - r.l), b = p.Y + (r.b - r.t) };
            Marshal.StructureToPtr(r, m.LParam, false);
            m.Result = (IntPtr)1;
            if (SeeThrough) Invalidate(); // the wallpaper behind it changes as it moves
            return;
        }
        if (m.Msg == 0xA3) return; // WM_NCLBUTTONDBLCLK on the drag area would maximize
        if (m.Msg == 0x112 && ((int)m.WParam & 0xFFF0) == 0xF030) return; // SC_MAXIMIZE
        if (m.Msg == 0xA5 || m.Msg == 0x7B) // right-click on the drag area: our menu, not the system menu
        {
            if (m.Msg == 0xA5) ShowMenu(Cursor.Position);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == 0x84 && (int)m.Result == 1 && !Cfg.LockWidgets && !ClientClicks) m.Result = (IntPtr)2; // drag from anywhere
        if ((m.Msg == 0xA0 || m.Msg == 0x200) && HoverAnimates) Animate(1500); // mouse over: only widgets with a hover animation spend frames
        if (m.Msg == 0x232) { Cfg.SetW(Key, "x", Left); Cfg.SetW(Key, "y", Top); Cfg.Apply(); } // WM_EXITSIZEMOVE: remember the spot
    }

    Point downAt;
    bool down;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { down = true; downAt = e.Location; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!down || Cfg.LockWidgets || Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) < 6 * S) return;
        down = false;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // hand over to the system move loop, as if the caption was grabbed
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool click = down;
        down = false;
        if (e.Button == MouseButtons.Right) { ShowMenu(Cursor.Position, e.Location); return; } // client-area right-click
        base.OnMouseUp(e);
        if (click && e.Button == MouseButtons.Left) Clicked(e.Location);
    }

    protected virtual void Clicked(Point client) { }

    void ShowMenu(Point at) { ShowMenu(at, PointToClient(at)); }

    void ShowMenu(Point at, Point client)
    {
        var m = new MenuPopup(S, Title);
        AddMenuItems(m, client);
        if (Styles != null)
            for (int i = 0; i < Styles.Length; i++) { int v = i; m.Item(i == Look ? G.Check : "", Styles[i], () => Set("style", v)); }
        for (int i = 0; i < Sizes.Length; i++)
            if (Allowed(design, i)) { int v = i; m.Item(i == SizeIndex ? G.Check : "", "Size " + Sizes[i], () => Set("size", v)); }
        m.Item(G.Lock, Cfg.LockWidgets ? "Unlock widgets" : "Lock widgets in place", () => { Cfg.LockWidgets = !Cfg.LockWidgets; Cfg.Apply(); });
        m.Item(G.Close, "Remove widget", () => Set("on", 0));
        m.Open(at);
    }

    void Set(string prop, int v) { Cfg.SetW(Key, prop, v); Cfg.Apply(); } // the bar rebuilds the widget from the new settings

    class View : Widget
    {
        public override bool Interactive { get { return false; } }
        public override void Paint(Graphics g)
        {
            var w = (DesktopWidget)Owner;
            var st = g.Save();
            g.TranslateTransform(w.ox, w.oy);
            g.ScaleTransform(w.k, w.k);
            var d = w.design;
            w.Draw(g, d.Width * w.S, d.Height * w.S);
            g.Restore(st);
        }
    }

    // Helpers shared by the widgets (all sizes in design units).
    protected static Font F(float pt, bool bold) { return new Font(Theme.Huge.FontFamily, pt, bold ? FontStyle.Bold : FontStyle.Regular); }
    protected static void T(Graphics g, string s, Font f, Brush b, float x, float y, float w, float h, StringFormat fmt)
    {
        float need = f.GetHeight(g) * 1.15f; // the shared formats skip lines taller than the box, so grow it around its centre (with slack for rounding)
        if (need > h) { y -= (need - h) / 2; h = need; }
        g.DrawString(s, f, b, new RectangleF(x, y, w, h), fmt);
    }
}

class ClockWidget : DesktopWidget
{
    static readonly string[] styles = { "Digital", "Analog", "Stacked", "Slim", "Tall", "Minimal", "Words", "Orbit" };
    static readonly Font Digits = F(52, true), Stack = F(66, true), WordsBold = F(26, true);
    // Slim faces: Bahnschrift Light Condensed ships with Windows 10/11 (falls back to a light Segoe if missing).
    static readonly Font SlimBig = Slim(72), SlimTall = Slim(84), SlimMid = Slim(40), WordsLight = new Font("Segoe UI Variable Display Light", 26);
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();

    static Font Slim(float pt)
    {
        var f = new Font("Bahnschrift Light Condensed", pt);
        if (f.Name == "Bahnschrift Light Condensed") return f;
        f.Dispose();
        return new Font("Segoe UI Variable Display Light", pt);
    }

    public ClockWidget(float s, Config c, Point at) : base(s, c, "clock", Design(c.W("clock", "style", 0)), at)
    {
        tick.Tick += (a, b) => Retime();
        FormClosed += (a, b) => tick.Dispose();
        Retime();
        tick.Start();
    }

    static SizeF Design(int style)
    {
        switch (style)
        {
            case 1: case 5: return new SizeF(210, 210);
            case 2: return new SizeF(230, 240);
            case 3: return new SizeF(340, 170);
            case 4: return new SizeF(200, 256);
            case 6: return new SizeF(230, 230);
            case 7: return new SizeF(220, 220);
            default: return new SizeF(340, 150);
        }
    }
    public override string Title { get { return "Clock"; } }
    public override string[] Styles { get { return styles; } }
    bool Seconds { get { return Look == 1 || Look == 5 || Look == 7; } }

    public void Retime()
    {
        var now = DateTime.Now;
        tick.Interval = Seconds ? 1000 - now.Millisecond + 10 : 60000 - now.Second * 1000 - now.Millisecond + 50; // faces with seconds: each second; others: each minute
        Invalidate();
    }

    public override void Draw(Graphics g, float w, float h)
    {
        var now = DateTime.Now;
        float u = S;
        string hh = now.ToString(Cfg.Clock24 ? "HH" : (Look == 2 || Look == 4 ? "hh" : "%h")), mm = now.ToString("mm"); // stacked faces: always two-digit hours
        switch (Look)
        {
            case 1: Analog(g, w, h, now); return;
            case 2:
                T(g, hh, Stack, Theme.Text, 22 * u, 4 * u, w, 100 * u, Theme.Left);
                T(g, mm, Stack, Theme.B(Theme.Accent), 22 * u, 92 * u, w, 100 * u, Theme.Left);
                T(g, now.ToString("ddd, MMM d"), Theme.Body, Theme.Dim, 26 * u, 196 * u, w, 26 * u, Theme.Left);
                return;
            case 3: // Slim: tall condensed numerals, quiet date underneath
                Stretched(g, hh + ":" + mm, SlimBig, Theme.Text, 20 * u, 20 * u, w - 40 * u, 116 * u, 1.3f, Theme.Left);
                T(g, now.ToString("dddd, MMMM d").ToUpper(), Theme.SmallBold, Theme.Dim, 24 * u, 138 * u, w - 110 * u, 24 * u, Theme.Left);
                if (!Cfg.Clock24) T(g, now.ToString("tt"), Theme.SmallBold, Theme.B(Theme.Accent), w - 90 * u, 138 * u, 66 * u, 24 * u, Theme.Right);
                return;
            case 4: // Tall: hours over minutes in the slim face
                Stretched(g, hh, SlimTall, Theme.Text, 16 * u, 22 * u, w - 32 * u, 112 * u, 1.25f, Theme.Left);
                Stretched(g, mm, SlimTall, Theme.B(Theme.Accent), 16 * u, 126 * u, w - 32 * u, 112 * u, 1.25f, Theme.Left);
                T(g, now.ToString("ddd").ToUpper(), Theme.SmallBold, Theme.Dim, w - 80 * u, 22 * u, 60 * u, 20 * u, Theme.Right);
                T(g, now.ToString("%d"), Theme.Title, Theme.Text, w - 80 * u, 42 * u, 60 * u, 30 * u, Theme.Right);
                return;
            case 5: Minimal(g, w, h, now); return;
            case 6: // Words: "nine / forty / five"
                var lines = Spoken(now.Hour, now.Minute, Cfg.Clock24);
                for (int i = 0; i < lines.Length; i++)
                    T(g, lines[i], i == 0 ? WordsBold : WordsLight, i == 0 ? Theme.Text : Theme.B(Theme.Accent), 22 * u, (20 + i * 52) * u, w - 40 * u, 50 * u, Theme.Left);
                T(g, now.ToString("ddd, MMM d"), Theme.Body, Theme.Dim, 24 * u, 190 * u, w - 40 * u, 24 * u, Theme.Left);
                return;
            case 7: Orbit(g, w, h, now, hh + ":" + mm); return;
        }
        T(g, hh + ":" + mm, Digits, Theme.Text, 22 * u, 10 * u, w - 44 * u, 82 * u, Theme.Left);
        if (!Cfg.Clock24) T(g, now.ToString("tt"), Theme.SmallBold, Theme.B(Theme.Accent), w - 70 * u, 30 * u, 50 * u, 20 * u, Theme.Right);
        T(g, now.ToString("dddd, MMMM d"), Theme.Body, Theme.Dim, 26 * u, 92 * u, w - 52 * u, 24 * u, Theme.Left);
        // Thin day-progress line: how much of today has gone by.
        float x0 = 26 * u, x1 = w - 26 * u, y = 126 * u, f = (float)now.TimeOfDay.TotalMinutes / 1440f;
        using (var p = Native.Round(new RectangleF(x0, y, x1 - x0, 5 * u), 2.5f * u)) Native.Fill(g, p, Theme.A(40));
        using (var p = Native.Round(new RectangleF(x0, y, Math.Max(5 * u, (x1 - x0) * f), 5 * u), 2.5f * u)) Native.Fill(g, p, Theme.Accent);
    }

    // Text stretched vertically (taller than the font draws it), aligned by fmt around the box centre line.
    static void Stretched(Graphics g, string s, Font f, Brush b, float x, float y, float w, float h, float k, StringFormat fmt)
    {
        var st = g.Save();
        g.TranslateTransform(x, y + h / 2);
        g.ScaleTransform(1, k);
        T(g, s, f, b, 0, -h / 2 / k, w, h / k, fmt);
        g.Restore(st);
    }

    static readonly string[] Ones = { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
    static readonly string[] Tens = { "", "", "twenty", "thirty", "forty", "fifty" };

    // The time as spoken: hour on the first line, then minutes ("oh five", "o'clock", "forty / five").
    public static string[] Spoken(int hour, int minute, bool h24)
    {
        int hr = h24 ? hour : (hour % 12 == 0 ? 12 : hour % 12);
        string hw = hr == 0 ? "zero" : hr < 20 ? Ones[hr] : Tens[hr / 10] + (hr % 10 > 0 ? "-" + Ones[hr % 10] : "");
        if (minute == 0) return new[] { hw, "o'clock" };
        if (minute < 10) return new[] { hw, "oh", Ones[minute] };
        if (minute < 20) return new[] { hw, Ones[minute] };
        return minute % 10 == 0 ? new[] { hw, Tens[minute / 10] } : new[] { hw, Tens[minute / 10], Ones[minute % 10] };
    }

    void Analog(Graphics g, float w, float h, DateTime now)
    {
        float u = S, cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 18 * u;
        g.FillEllipse(Theme.B(Theme.Surface), cx - r, cy - r, 2 * r, 2 * r);
        for (int i = 0; i < 12; i++)
        {
            double a = i * Math.PI / 6;
            float d = i % 3 == 0 ? 4.5f * u : 2.5f * u, rr = r - 12 * u;
            g.FillEllipse(i % 3 == 0 ? Theme.Text : Theme.Dim, cx + rr * (float)Math.Sin(a) - d / 2, cy - rr * (float)Math.Cos(a) - d / 2, d, d);
        }
        Hand(g, cx, cy, (now.Hour % 12 + now.Minute / 60.0) * 30, r * 0.5f, 6 * u, Theme.Fore);
        Hand(g, cx, cy, (now.Minute + now.Second / 60.0) * 6, r * 0.75f, 4.5f * u, Theme.Fore);
        Hand(g, cx, cy, now.Second * 6, r * 0.82f, 2 * u, Theme.Accent);
        g.FillEllipse(Theme.B(Theme.Accent), cx - 5 * u, cy - 5 * u, 10 * u, 10 * u);
        T(g, now.ToString("ddd d"), Theme.SmallBold, Theme.Dim, cx - 50 * u, cy + r * 0.32f, 100 * u, 20 * u, Theme.Center);
    }

    // Minimal: hairline ring, four ticks, slim hands; the second is a dot gliding round the ring.
    void Minimal(Graphics g, float w, float h, DateTime now)
    {
        float u = S, cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 20 * u;
        using (var ring = new Pen(Theme.A(60), 1.5f * u)) g.DrawEllipse(ring, cx - r, cy - r, 2 * r, 2 * r);
        for (int i = 0; i < 4; i++)
        {
            double a = i * Math.PI / 2;
            using (var t = new Pen(Theme.A(150), 2 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(t, cx + (r - 12 * u) * (float)Math.Sin(a), cy - (r - 12 * u) * (float)Math.Cos(a), cx + (r - 4 * u) * (float)Math.Sin(a), cy - (r - 4 * u) * (float)Math.Cos(a));
        }
        Hand(g, cx, cy, (now.Hour % 12 + now.Minute / 60.0) * 30, r * 0.5f, 3 * u, Theme.Fore);
        Hand(g, cx, cy, (now.Minute + now.Second / 60.0) * 6, r * 0.8f, 2 * u, Theme.Fore);
        double sa = now.Second * Math.PI / 30;
        float d = 8 * u;
        g.FillEllipse(Theme.B(Theme.Accent), cx + r * (float)Math.Sin(sa) - d / 2, cy - r * (float)Math.Cos(sa) - d / 2, d, d);
        g.FillEllipse(Theme.Text, cx - 3 * u, cy - 3 * u, 6 * u, 6 * u);
    }

    // Orbit: 60 dots fill up with the seconds around the time in the slim face.
    void Orbit(Graphics g, float w, float h, DateTime now, string time)
    {
        float u = S, cx = w / 2, cy = h / 2, r = Math.Min(w, h) / 2 - 18 * u;
        for (int i = 0; i < 60; i++)
        {
            double a = i * Math.PI / 30;
            float d = (i % 5 == 0 ? 6 : 4) * u;
            g.FillEllipse(i <= now.Second ? Theme.B(Theme.Accent) : Theme.B(Theme.A(40)), cx + r * (float)Math.Sin(a) - d / 2, cy - r * (float)Math.Cos(a) - d / 2, d, d);
        }
        Stretched(g, time, SlimMid, Theme.Text, 0, cy - 44 * u, w, 70 * u, 1.25f, Theme.Center);
        T(g, now.ToString("ddd d MMM").ToUpper(), Theme.SmallBold, Theme.Dim, 0, cy + 30 * u, w, 20 * u, Theme.Center);
    }

    static void Hand(Graphics g, float cx, float cy, double deg, float len, float width, Color c)
    {
        double a = deg * Math.PI / 180;
        using (var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, cx, cy, cx + len * (float)Math.Sin(a), cy - len * (float)Math.Cos(a));
    }
}

// Current conditions + 3 days from Open-Meteo (free, no account), refreshed every 30 minutes,
// with Microsoft's Fluent 3D weather icons from the weather\ folder.
class WeatherWidget : DesktopWidget
{
    static readonly string[] styles = { "Detailed", "Compact", "Big" };
    static readonly Font Temp = F(40, true), TempBig = F(54, true), TempSmall = F(30, true);
    static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
    public class Day { public DateTime Date; public int Code; public double Max, Min; }
    double now = double.NaN, hi, lo;
    int code = -1;
    bool day = true;
    string status = "Loading…", city = "";
    List<Day> days = new List<Day>();
    readonly System.Windows.Forms.Timer every = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };

    public WeatherWidget(float s, Config c, Point at) : base(s, c, "weather", Design(c.W("weather", "style", 0)), at)
    {
        every.Tick += (a, b) => Fetch();
        FormClosed += (a, b) => every.Dispose();
        every.Start();
        Fetch();
    }

    static SizeF Design(int style) { return style == 1 ? new SizeF(240, 110) : style == 2 ? new SizeF(260, 280) : new SizeF(344, 172); }
    public override string Title { get { return "Weather"; } }
    protected override bool HoverAnimates { get { return true; } } // the icon floats
    public override string[] Styles { get { return styles; } }

    void Fetch()
    {
        string want = Cfg.City;
        bool f = Cfg.Fahrenheit;
        city = want;
        if (want.Length == 0) { status = "Set a city in Settings → Widgets"; Invalidate(); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string st = null, name = want;
            double n = double.NaN, h = 0, l = 0; int cd = -1; bool d = true; var list = new List<Day>();
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var json = new JavaScriptSerializer();
                using (var web = new WebClient { Encoding = System.Text.Encoding.UTF8 })
                {
                    var geo = (Dictionary<string, object>)json.DeserializeObject(web.DownloadString("https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(want)));
                    var hits = geo.ContainsKey("results") ? geo["results"] as object[] : null;
                    if (hits == null || hits.Length == 0) st = "City not found: " + want;
                    else
                    {
                        var place = (Dictionary<string, object>)hits[0];
                        name = place["name"].ToString();
                        string url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=4{2}",
                            Convert.ToDouble(place["latitude"]), Convert.ToDouble(place["longitude"]), f ? "&temperature_unit=fahrenheit" : "");
                        var w = (Dictionary<string, object>)json.DeserializeObject(web.DownloadString(url));
                        var cur = (Dictionary<string, object>)w["current"];
                        n = Convert.ToDouble(cur["temperature_2m"]);
                        cd = Convert.ToInt32(cur["weather_code"]);
                        d = Convert.ToInt32(cur["is_day"]) == 1;
                        var dd = (Dictionary<string, object>)w["daily"];
                        var dates = (object[])dd["time"]; var codes = (object[])dd["weather_code"]; var max = (object[])dd["temperature_2m_max"]; var min = (object[])dd["temperature_2m_min"];
                        h = Convert.ToDouble(max[0]); l = Convert.ToDouble(min[0]);
                        for (int i = 1; i < dates.Length; i++)
                            list.Add(new Day { Date = DateTime.Parse((string)dates[i], System.Globalization.CultureInfo.InvariantCulture), Code = Convert.ToInt32(codes[i]), Max = Convert.ToDouble(max[i]), Min = Convert.ToDouble(min[i]) });
                    }
                }
            }
            catch (WebException) { st = "Offline: weather unavailable"; }
            catch (Exception) { st = "Couldn't read the weather"; } // unexpected reply shape
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { status = st; city = name; if (st == null) { now = n; code = cd; day = d; hi = h; lo = l; days = list; Animate(1500); } Invalidate(); }));
            }
            catch (InvalidOperationException) { }
        });
    }

    // WMO weather code -> icon file in weather\ (Fluent Emoji 3D).
    public static string IconName(int code, bool day)
    {
        if (code == 0) return day ? "clear-day" : "clear-night";
        if (code <= 2) return day ? "partly-cloudy" : "clear-night";
        if (code == 3) return "cloudy";
        if (code == 45 || code == 48) return "fog";
        if (code >= 71 && code <= 77 || code == 85 || code == 86) return "snow";
        if (code >= 95) return "thunder";
        if (code >= 80 && code <= 82) return day ? "showers" : "rain";
        if (code >= 51 && code <= 67) return "rain";
        return "cloudy";
    }

    static Image IconImage(string name)
    {
        Image img;
        if (icons.TryGetValue(name, out img)) return img;
        string f = Path.Combine(Path.GetDirectoryName(typeof(WeatherWidget).Assembly.Location), "weather", name + ".png");
        try { img = File.Exists(f) ? Image.FromFile(f) : null; } catch (OutOfMemoryException) { img = null; } // not a valid image
        return icons[name] = img;
    }

    // Glyph fallback when an icon file is missing.
    public static string Glyph(int code)
    {
        if (code == 0) return "☀";
        if (code <= 2) return "⛅";
        if (code == 3) return "☁";
        if (code == 45 || code == 48) return "≈";
        if (code >= 71 && code <= 77 || code == 85 || code == 86) return "❄";
        if (code >= 95) return "⛈";
        if (code >= 51 && code <= 82) return "⛆";
        return "☁";
    }

    public static string Label(int code)
    {
        if (code == 0) return "Clear";
        if (code <= 2) return "Partly cloudy";
        if (code == 3) return "Cloudy";
        if (code == 45 || code == 48) return "Fog";
        if (code >= 51 && code <= 57) return "Drizzle";
        if (code >= 61 && code <= 67) return "Rain";
        if (code >= 71 && code <= 77) return "Snow";
        if (code >= 80 && code <= 82) return "Showers";
        if (code == 85 || code == 86) return "Snow showers";
        if (code >= 95) return "Thunderstorm";
        return "—";
    }

    // The icon floats and breathes while animating.
    void DrawIcon(Graphics g, int c, bool d, float x, float y, float size, bool alive)
    {
        float bob = alive ? (float)Math.Sin(Phase * 2.6) * size * 0.04f : 0, k = alive ? 1 + 0.035f * (float)Math.Sin(Phase * 3.1) : 1;
        float s2 = size * k, ox = x + (size - s2) / 2, oy = y + (size - s2) / 2 + bob;
        var img = IconImage(IconName(c, d));
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (img != null) g.DrawImage(img, ox, oy, s2, s2);
        else using (var f = new Font("Segoe UI Symbol", size * 0.35f)) g.DrawString(Glyph(c), f, Theme.Text, new RectangleF(x, y, size, size), Theme.Center);
    }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S, x = 22 * u;
        if (status != null)
        {
            T(g, city.Length > 0 ? city : "Weather", Theme.Small, Theme.Dim, x, 14 * u, w - 2 * x, 20 * u, Theme.Left);
            T(g, status, Theme.Body, Theme.Text, x, 40 * u, w - 2 * x, 50 * u, Theme.Left);
            return;
        }
        string t = Math.Round(now) + "°", hl = "H " + Math.Round(hi) + "°  L " + Math.Round(lo) + "°";
        if (Look == 1) // Compact
        {
            DrawIcon(g, code, day, 12 * u, (h - 80 * u) / 2, 80 * u, true);
            T(g, t, TempSmall, Theme.Text, 98 * u, 18 * u, w - 110 * u, 44 * u, Theme.Left);
            T(g, Label(code), Theme.Small, Theme.Dim, 100 * u, 62 * u, w - 110 * u, 18 * u, Theme.Left);
            T(g, city, Theme.Small, Theme.Dim, 100 * u, 80 * u, w - 110 * u, 18 * u, Theme.Left);
            return;
        }
        if (Look == 2) // Big
        {
            DrawIcon(g, code, day, (w - 150 * u) / 2, 14 * u, 150 * u, true);
            T(g, t, TempBig, Theme.Text, 0, 160 * u, w, 70 * u, Theme.Center);
            T(g, Label(code) + "  ·  " + city, Theme.Small, Theme.Dim, 12 * u, 226 * u, w - 24 * u, 20 * u, Theme.Center);
            T(g, hl, Theme.SmallBold, Theme.Text, 0, 248 * u, w, 20 * u, Theme.Center);
            return;
        }
        // Detailed (2:1): now on the left, the next three days stacked on the right
        T(g, city, Theme.Small, Theme.Dim, 18 * u, 12 * u, 180 * u, 20 * u, Theme.Left);
        DrawIcon(g, code, day, 8 * u, 32 * u, 88 * u, true);
        T(g, t, Temp, Theme.Text, 98 * u, 38 * u, 110 * u, 56 * u, Theme.Left);
        T(g, Label(code), Theme.Small, Theme.Dim, 18 * u, 124 * u, 180 * u, 18 * u, Theme.Left);
        T(g, hl, Theme.SmallBold, Theme.Text, 18 * u, 144 * u, 180 * u, 18 * u, Theme.Left);
        using (var pen = new Pen(Theme.A(30), 1 * u)) g.DrawLine(pen, 206 * u, 20 * u, 206 * u, h - 20 * u);
        for (int i = 0; i < Math.Min(3, days.Count); i++)
        {
            var dd = days[i];
            float y = 14 * u + i * 50 * u;
            T(g, dd.Date.ToString("ddd"), Theme.SmallBold, Theme.Text, 216 * u, y, 44 * u, 44 * u, Theme.Left);
            DrawIcon(g, dd.Code, true, 254 * u, y + 5 * u, 34 * u, false);
            T(g, Math.Round(dd.Max) + "° " + Math.Round(dd.Min) + "°", Theme.Small, Theme.Dim, 284 * u, y, w - 294 * u, 44 * u, Theme.Right);
        }
    }
}

// This month at a glance; today in an accent circle that pulses on hover.
class CalendarWidget : DesktopWidget
{
    static readonly Font Month = F(20, true);
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();

    public CalendarWidget(float s, Config c, Point at) : base(s, c, "calendar", new SizeF(300, 290), at)
    {
        tick.Tick += (a, b) => Retime();
        FormClosed += (a, b) => tick.Dispose();
        Retime();
        tick.Start();
    }

    public override string Title { get { return "Calendar"; } }
    protected override bool HoverAnimates { get { return true; } } // today pulses

    void Retime()
    {
        var now = DateTime.Now;
        tick.Interval = (int)Math.Min(3600000, (now.Date.AddDays(1) - now).TotalMilliseconds + 500); // redraw just after midnight
        Invalidate();
    }

    public override void Draw(Graphics g, float w, float h)
    {
        var now = DateTime.Today;
        float u = S, x = 18 * u;
        T(g, now.ToString("MMMM"), Month, Theme.Text, x + 4 * u, 12 * u, w, 32 * u, Theme.Left);
        T(g, now.ToString("yyyy"), Theme.Body, Theme.B(Theme.Accent), w - 90 * u, 18 * u, 70 * u, 24 * u, Theme.Right);
        var fmt = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
        int first = (int)fmt.FirstDayOfWeek;
        float cw = (w - 2 * x) / 7, ch = 32 * u, top = 54 * u;
        for (int i = 0; i < 7; i++) T(g, fmt.ShortestDayNames[(first + i) % 7], Theme.SmallBold, Theme.Dim, x + i * cw, top, cw, 20 * u, Theme.Center);
        var start = new DateTime(now.Year, now.Month, 1);
        int offset = ((int)start.DayOfWeek - first + 7) % 7;
        var d = start.AddDays(-offset);
        for (int row = 0; row < 6; row++)
            for (int col = 0; col < 7; col++, d = d.AddDays(1))
            {
                var cell = new RectangleF(x + col * cw, top + 24 * u + row * ch, cw, ch);
                if (d == now)
                {
                    float r = Math.Min(cw, ch) * 0.46f * (1 + 0.08f * (float)Math.Sin(Phase * 4));
                    g.FillEllipse(Theme.B(Theme.Accent), cell.X + cell.Width / 2 - r, cell.Y + cell.Height / 2 - r, 2 * r, 2 * r);
                }
                T(g, d.Day.ToString(), d == now ? Theme.SmallBold : Theme.Small, d == now ? Theme.B(Theme.OnAccent) : d.Month == now.Month ? Theme.Text : Theme.B(Theme.A(70)), cell.X, cell.Y, cell.Width, cell.Height, Theme.Center);
            }
    }
}

// Battery, CPU and memory as rings that ease to new values. Samples every 3 seconds.
class SystemWidget : DesktopWidget
{
    [StructLayout(LayoutKind.Sequential)] struct MEMSTAT { public int len, load; public long total, avail, totalPage, availPage, totalVirt, availVirt, extVirt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    static readonly Font Value = F(15, true);
    readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 3000 };
    readonly float[] shown = new float[3], target = new float[3];
    long lastIdle, lastBusy;

    public SystemWidget(float s, Config c, Point at) : base(s, c, "system", new SizeF(340, 160), at)
    {
        tick.Tick += (a, b) => Sample();
        FormClosed += (a, b) => tick.Dispose();
        Sample();
        tick.Start();
    }

    public override string Title { get { return "System"; } }

    void Sample()
    {
        long idle, kernel, user;
        if (GetSystemTimes(out idle, out kernel, out user))
        {
            long busy = kernel + user - idle, di = idle - lastIdle, db = busy - lastBusy; // kernel time includes idle time
            if (lastBusy != 0 && di + db > 0) target[0] = 100f * db / (di + db);
            lastIdle = idle; lastBusy = busy;
        }
        var m = new MEMSTAT { len = Marshal.SizeOf(typeof(MEMSTAT)) };
        if (GlobalMemoryStatusEx(ref m)) target[1] = m.load;
        var p = SystemInformation.PowerStatus;
        target[2] = p.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery) ? -1 : p.BatteryLifePercent * 100;
        Invalidate(); // one redraw per sample: easing the rings cost ~18 frames every 3 s, i.e. nonstop animation
    }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S;
        string[] names = { "CPU", "Memory", "Battery" };
        int n = target[2] < 0 ? 2 : 3;
        float cw = w / n, r = 36 * u;
        for (int i = 0; i < n; i++)
        {
            shown[i] = target[i];
            float cx = cw * i + cw / 2, cy = 70 * u;
            using (var bg = new Pen(Theme.A(40), 9 * u)) g.DrawEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
            float sweep = Math.Max(1, 360 * shown[i] / 100f);
            Color c = i == 2 && SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? Theme.Green : Theme.Accent;
            using (var pen = new Pen(c, 9 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, -90, sweep);
            T(g, Math.Round(shown[i]) + "%", Value, Theme.Text, cx - r, cy - r, 2 * r, 2 * r, Theme.Center);
            T(g, names[i], Theme.SmallBold, Theme.Dim, cx - cw / 2, cy + r + 10 * u, cw, 20 * u, Theme.Center);
        }
    }
}

// Up to 12 apps in a 6 x 2 grid. Click an empty slot (or drop shortcuts on it) to add; right-click an app to change
// or remove it. The list lives in apps.txt next to the exe, one path per line (a blank line is an empty slot).
class AppsWidget : DesktopWidget
{
    const int N = 12;
    static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.txt"); } }
    readonly string[] apps = new string[N];
    readonly Bitmap[] icons = new Bitmap[N];
    readonly Bitmap[] sized = new Bitmap[N]; // each icon at its exact on-screen pixel size: still icons are a plain copy, not a resample
    int hot = -1, launched = -1;
    readonly float[] grow = new float[N], vel = new float[N]; // hover zoom per slot (0..1) driven by a spring
    readonly System.Diagnostics.Stopwatch frames = System.Diagnostics.Stopwatch.StartNew();
    double lastFrame;

    public AppsWidget(float s, Config c, Point at) : base(s, c, "apps", new SizeF(400, 200), at)
    {
        AllowDrop = true;
        string text = "";
        try { if (File.Exists(FilePath)) text = File.ReadAllText(FilePath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var list = Parse(text);
        for (int i = 0; i < N; i++) Put(i, list[i]);
        FormClosed += (a, b) => { foreach (var ic in icons) if (ic != null) ic.Dispose(); foreach (var ic in sized) if (ic != null) ic.Dispose(); };
    }

    static readonly string[] styles = { "App colours", "Material You" };
    public override string Title { get { return "Apps"; } }
    public override string[] Styles { get { return styles; } }
    protected override bool ClientClicks { get { return true; } }

    // Hand-editable file: keep only full paths without control characters; always N slots.
    public static string[] Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var r = new string[N];
        for (int i = 0; i < N; i++)
        {
            string l = i < lines.Length ? lines[i].Trim() : "";
            bool ok = l.Length > 3 && l.Length < 1024 && (IsStoreApp(l) || l.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(l));
            r[i] = ok ? l : "";
        }
        return r;
    }

    void Put(int i, string path)
    {
        apps[i] = path;
        if (icons[i] != null) { icons[i].Dispose(); icons[i] = null; }
        if (sized[i] != null) { sized[i].Dispose(); sized[i] = null; }
        if (path.Length == 0) return;
        try
        {
            using (var big = Squircles.Art(path, Look == 1 ? Theme.Mix(Theme.Accent, Color.White, 0.72f) : Color.Empty)) // squircle in the app's colour, or Material You (accent pastel)
            {
                if (big == null) return;
                icons[i] = new Bitmap(96, 96, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); // keep a small copy, not the 256px one
                using (var g = Graphics.FromImage(icons[i])) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(big, 0, 0, 96, 96); }
            }
        }
        catch (Exception) { } // missing file or no shell handler: drawn as a placeholder
    }

    void Save()
    {
        try { File.WriteAllLines(FilePath, apps); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Invalidate();
    }

    float CellW { get { return (400 - 20) * S / 6; } }
    float CellH { get { return 76 * S; } }
    float GridTop { get { return 44 * S; } } // below the title bar

    int SlotAt(Point client)
    {
        var p = ToCanvas(client);
        int col = (int)Math.Floor((p.X - 10 * S) / CellW), row = (int)Math.Floor((p.Y - GridTop) / CellH);
        return col >= 0 && col < 6 && row >= 0 && row < 2 ? row * 6 + col : -1;
    }

    // Store apps have no shortcut file; they are kept as shell:AppsFolder\<app id> (e.g. Claude_pzs8sxrjxfjjc!Claude).
    const string Store = "shell:AppsFolder\\";
    static bool IsStoreApp(string l) { return l.StartsWith(Store, StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(l.Substring(Store.Length), @"^[A-Za-z0-9._-]+(![A-Za-z0-9._-]+)?$"); }

    static string AppName(string path)
    {
        if (!IsStoreApp(path)) return Path.GetFileNameWithoutExtension(path);
        string id = path.Substring(Store.Length);
        int cut = id.IndexOf('_');
        return cut > 0 ? id.Substring(0, cut) : id; // package name, e.g. "Claude"
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int s = SlotAt(e.Location);
        if (s != hot) { hot = s; Animate(300); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Animate(300); }

    protected override void Clicked(Point client)
    {
        int i = SlotAt(client);
        if (i < 0) return;
        if (apps[i].Length == 0) { Pick(i); return; }
        Shell.Open(apps[i]);
        launched = i;
        Animate(700);
    }

    void Pick(int i)
    {
        using (var d = new OpenFileDialog { Title = "Choose an app", Filter = "Apps and shortcuts|*.lnk;*.exe;*.url;*.appref-ms|All files|*.*", DereferenceLinks = false,
                                            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs") })
            if (d.ShowDialog() == DialogResult.OK) { Put(i, d.FileName); Save(); }
    }

    protected override void AddMenuItems(MenuPopup m, Point client)
    {
        int i = SlotAt(client);
        if (i < 0) return;
        if (apps[i].Length == 0) { m.Item(G.Add, "Add app here…", () => Pick(i)); return; }
        m.Item(G.Folder, "Change " + AppName(apps[i]) + "…", () => Pick(i));
        m.Item(G.Delete, "Remove " + AppName(apps[i]), () => { Put(i, ""); Save(); });
    }

    protected override void OnDragEnter(DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; }

    // Dropped files fill the slot under the pointer first, then the next empty slots.
    protected override void OnDragDrop(DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null) return;
        int at = SlotAt(PointToClient(new Point(e.X, e.Y)));
        foreach (var f in files)
        {
            int i = at >= 0 && apps[at].Length == 0 ? at : Array.IndexOf(apps, "");
            if (at >= 0 && i == at) at = -1;
            if (i < 0) break;
            Put(i, f);
        }
        Save();
    }

    // Copies icon i at its exact device size (made once per size) with no scaling in the way.
    void DrawStill(Graphics g, int i, float x, float y, float size)
    {
        var pts = new[] { new PointF(x, y), new PointF(x + size, y + size) };
        g.Transform.TransformPoints(pts);
        int px = (int)Math.Round(pts[1].X - pts[0].X);
        if (sized[i] == null || sized[i].Width != px)
        {
            if (sized[i] != null) sized[i].Dispose();
            sized[i] = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(sized[i])) { sg.InterpolationMode = InterpolationMode.HighQualityBicubic; sg.PixelOffsetMode = PixelOffsetMode.HighQuality; sg.DrawImage(icons[i], 0, 0, px, px); }
        }
        var st = g.Save();
        g.ResetTransform();
        g.DrawImageUnscaledAndClipped(sized[i], new Rectangle((int)Math.Round(pts[0].X), (int)Math.Round(pts[0].Y), px, px));
        g.Restore(st);
    }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S, icon = 56 * u;
        double now = frames.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.05, now - lastFrame);
        lastFrame = now;
        bool moving = false;
        for (int i = 0; i < N; i++) // critically damped spring: eases in and out with no jumps, whatever the frame timing
        {
            float target = i == hot ? 1 : 0, acc = 240 * (target - grow[i]) - 31 * vel[i];
            vel[i] += acc * dt;
            grow[i] += vel[i] * dt;
            if (Math.Abs(target - grow[i]) > 0.002f || Math.Abs(vel[i]) > 0.02f) moving = true;
            else { grow[i] = target; vel[i] = 0; }
        }
        if (moving) Animate(200); // keep frames coming until every icon has settled
        // Title bar: name on the left, how many apps are pinned on the right, a hairline underneath.
        int pinned = Array.FindAll(apps, a => a.Length > 0).Length;
        T(g, "Apps", Theme.Bold, Theme.Text, 20 * u, 10 * u, 200 * u, 24 * u, Theme.Left);
        T(g, pinned + " of " + N, Theme.Small, Theme.Dim, w - 140 * u, 10 * u, 120 * u, 24 * u, Theme.Right);
        using (var pen = new Pen(Theme.A(28), 1 * u)) g.DrawLine(pen, 20 * u, 40 * u, w - 20 * u, 40 * u);
        for (int i = 0; i < N; i++)
        {
            float cx = 10 * u + (i % 6) * CellW, cy = GridTop + (i / 6) * CellH;
            float k = i == launched && Phase > 0 ? 1 - 0.18f * (float)Math.Sin(Math.Min(Math.PI, Phase * 4.5)) : 1; // squish-and-pop on launch
            float sz = icon * k * (1 + 0.2f * grow[i]), ix = cx + (CellW - sz) / 2, iy = cy + (CellH - sz) / 2; // icon only, centred in its cell
            if (apps[i].Length == 0)
            {
                using (var pen = new Pen(Theme.A(i == hot ? 110 : 45), 1.5f * u) { DashStyle = DashStyle.Dash })
                using (var p = Native.Round(new RectangleF(ix + 4 * u, iy + 4 * u, sz - 8 * u, sz - 8 * u), 14 * u)) g.DrawPath(pen, p);
                g.DrawString(G.Add, Theme.Icon, i == hot ? Theme.Text : Theme.Dim, new RectangleF(ix, iy, sz, sz), Theme.Center);
                continue;
            }
            if (icons[i] != null && grow[i] == 0 && k == 1) DrawStill(g, i, ix, iy, sz);
            else if (icons[i] != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icons[i], ix, iy, sz, sz); } // only the moving icon is resampled
            else g.DrawString(G.File, Theme.IconBig, Theme.Text, new RectangleF(ix, iy, sz, sz), Theme.Center);
        }
    }
}

// Battery levels of this PC and the Bluetooth devices that report one (headphones, earbuds, phone).
class DevicesWidget : DesktopWidget
{
    readonly Action update;
    public DevicesWidget(float s, Config c, Point at) : base(s, c, "devices", new SizeF(344, 172), at)
    {
        update = () => Invalidate();
        Health.Updated += update; // the bar re-reads them on device changes and every 10 minutes
        FormClosed += (a, b) => Health.Updated -= update;
    }

    public override string Title { get { return "Devices"; } }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S, x = 20 * u, y = 14 * u;
        T(g, "Devices", Theme.Bold, Theme.Text, x, y, w - 2 * x, 24 * u, Theme.Left);
        y += 32 * u;
        var rows = new List<Health.Batt>();
        var p = SystemInformation.PowerStatus;
        if (!p.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)) rows.Add(new Health.Batt { Name = "This PC", Pct = (int)Math.Round(p.BatteryLifePercent * 100), Charging = p.PowerLineStatus == PowerLineStatus.Online });
        rows.AddRange(Health.Last);
        if (rows.Count == 0) { T(g, "No battery devices connected", Theme.Small, Theme.Dim, x, y, w - 2 * x, 24 * u, Theme.Left); return; }
        float rh = (h - y - 12 * u) / Math.Max(3, Math.Min(4, rows.Count));
        foreach (var b in rows.GetRange(0, Math.Min(4, rows.Count)))
        {
            bool phone = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Pixel|Phone|Galaxy|iPhone", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool mouse = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Mouse|ProtoArc|MX|DeathAdder|Basilisk|G Pro", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool key = System.Text.RegularExpressions.Regex.IsMatch(b.Name, "Keyboard|Keychron|K380|MX Keys", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string icon = b.Name == "This PC" ? G.Battery(b.Pct, b.Charging) : phone ? G.Phone : mouse ? G.Mouse : key ? G.Keyboard : G.Headphones;
            Color low = TopBar.LowBattery(b.Pct, b.Charging, Theme.Light);
            Color c = b.Charging ? Theme.Green : !low.IsEmpty ? low : Theme.Accent;
            Brush icBrush = b.Charging ? Theme.B(Theme.Green) : !low.IsEmpty ? Theme.B(low) : Theme.Text;
            g.DrawString(icon, Theme.Icon, icBrush, new RectangleF(x, y, 26 * u, rh), Theme.Left);
            T(g, b.Name, Theme.Small, Theme.Text, x + 32 * u, y, w * 0.42f, rh, Theme.Left);
            if (b.Pct >= 0)
            {
                var track = new RectangleF(x + 32 * u + w * 0.42f, y + rh / 2 - 3 * u, w - x * 2 - 32 * u - w * 0.42f - 48 * u, 6 * u);
                using (var tp = Native.Round(track, 3 * u)) Native.Fill(g, tp, Theme.A(30));
                using (var fp = Native.Round(new RectangleF(track.X, track.Y, Math.Max(6 * u, track.Width * b.Pct / 100f), track.Height), 3 * u)) Native.Fill(g, fp, c);
                T(g, (b.Charging ? "⚡" : "") + b.Pct + "%", Theme.SmallBold, icBrush, w - x - 44 * u, y, 44 * u, rh, Theme.Right);
            }
            else T(g, "Connected", Theme.Small, Theme.Dim, w - x - 90 * u, y, 90 * u, rh, Theme.Right);
            y += rh;
        }
    }
}

// What's playing (any app using Windows media controls), with previous / play-pause / next.
class MediaWidget : DesktopWidget
{
    static readonly Font Big = F(15, true);
    readonly Action changed;
    public MediaWidget(float s, Config c, Point at) : base(s, c, "media", new SizeF(344, 172), at)
    {
        changed = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch (InvalidOperationException) { } };
        Media.Changed += changed;
        FormClosed += (a, b) => Media.Changed -= changed;
        Media.Start(); // no-op when the bar's media pill already started it
    }

    public override string Title { get { return "Now playing"; } }
    protected override bool ClientClicks { get { return true; } }

    RectangleF Btn(int i) { float u = S; return new RectangleF(160 * u + i * 52 * u, 112 * u, 44 * u, 44 * u); } // previous, play, next

    protected override void Clicked(Point client)
    {
        var p = ToCanvas(client);
        if (!Media.Has) return;
        if (Btn(0).Contains(p)) Media.Prev(); else if (Btn(1).Contains(p)) Media.Toggle(); else if (Btn(2).Contains(p)) Media.Next();
        Invalidate();
    }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S;
        var art = new RectangleF(16 * u, 16 * u, 130 * u, 140 * u);
        using (var p = Native.Round(art, 18 * u))
        {
            if (Media.Art != null) ArtView.Cover(g, p, art, Media.Art);
            else { Native.Fill(g, p, Theme.A(25)); g.DrawString(G.Music, Theme.IconBig, Theme.Dim, art, Theme.Center); }
        }
        if (!Media.Has) { T(g, "Nothing playing", Theme.Body, Theme.Dim, 162 * u, 40 * u, w - 176 * u, 30 * u, Theme.Left); return; }
        T(g, Media.Title, Big, Theme.Text, 162 * u, 22 * u, w - 176 * u, 30 * u, Theme.Left);
        T(g, Media.Artist.Length > 0 ? Media.Artist : Media.App, Theme.Small, Theme.Dim, 162 * u, 54 * u, w - 176 * u, 22 * u, Theme.Left);
        string[] icons = { "", Media.Playing ? "" : "", "" }; // previous, pause/play, next
        for (int i = 0; i < 3; i++)
        {
            var b = Btn(i);
            if (i == 1) using (var p = Native.Round(b, b.Height / 2)) Native.Fill(g, p, Theme.Accent);
            g.DrawString(icons[i], Theme.Icon, i == 1 ? Theme.B(Theme.OnAccent) : Theme.Text, b, Theme.Center);
        }
    }
}

// The newest note, as plain lines; click it to carry on in the editor.
class NoteWidget : DesktopWidget
{
    public static Action<string> OpenEditor; // set by the bar
    readonly FileSystemWatcher watch;
    readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 500 };
    string path, title = "Notes";
    readonly List<string> lines = new List<string>();

    public NoteWidget(float s, Config c, Point at) : base(s, c, "note", new SizeF(300, 290), at)
    {
        settle.Tick += (a, b) => { settle.Stop(); Reload(); };
        Reload();
        try
        {
            Directory.CreateDirectory(NoteStore.Dir);
            watch = new FileSystemWatcher(NoteStore.Dir, "*.md") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
            FileSystemEventHandler h = (a, e) => { try { BeginInvoke((Action)(() => { settle.Stop(); settle.Start(); })); } catch (InvalidOperationException) { } };
            watch.Changed += h; watch.Created += h; watch.Deleted += h;
            watch.Renamed += (a, e) => h(a, e);
            watch.EnableRaisingEvents = true; // follows edits as the editor saves them
        }
        catch (Exception) { watch = null; }
        FormClosed += (a, b) => { if (watch != null) watch.Dispose(); settle.Dispose(); };
    }

    public override string Title { get { return "Notes"; } }
    protected override bool ClientClicks { get { return true; } }
    protected override void Clicked(Point client) { if (OpenEditor != null) OpenEditor(path); }

    void Reload()
    {
        var all = NoteStore.All();
        lines.Clear();
        path = all.Count > 0 ? all[0].Path : null;
        title = all.Count > 0 ? all[0].Title : "No notes yet";
        if (path != null)
            try
            {
                bool skippedTitle = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || Md.Fence.IsMatch(l) || Md.IsTableRow(l) && Md.IsSeparator(Md.Cells(l))) continue;
                    var m = Md.Item.Match(l);
                    string text = m.Success ? (m.Groups[3].Success ? (m.Groups[3].Value.ToLower().Contains("x") ? "☑ " : "☐ ") : "• ") + m.Groups[4].Value : l.TrimStart('#', '>', ' ');
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)|[*_`~|]", mm => mm.Groups[1].Success ? mm.Groups[1].Value : mm.Value == "|" ? " " : "").Trim(); // marks vanish; table bars become spaces
                    if (!skippedTitle && text == title) { skippedTitle = true; continue; } // the title is shown above
                    if (text.Length > 0) lines.Add(text);
                    if (lines.Count >= 12) break;
                }
            }
            catch (IOException) { } // being saved right now: the next change event tries again
        Invalidate();
    }

    public override void Draw(Graphics g, float w, float h)
    {
        float u = S, x = 20 * u, y = 16 * u;
        T(g, title, Theme.Bold, Theme.Text, x, y, w - 2 * x - 24 * u, 26 * u, Theme.Left);
        g.DrawString("", Theme.IconSmall, Theme.Dim, new RectangleF(w - x - 18 * u, y, 18 * u, 26 * u), Theme.Center);
        y += 36 * u;
        float lh = 22 * u;
        foreach (var l in lines)
        {
            if (y + lh > h - 12 * u) break;
            T(g, l, Theme.Small, Theme.Dim, x, y, w - 2 * x, lh, Theme.Left);
            y += lh;
        }
        if (path == null) T(g, "Press Alt+N to write one", Theme.Small, Theme.Dim, x, y, w - 2 * x, lh, Theme.Left);
    }
}
