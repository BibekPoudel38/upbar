using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Theme
{
    // Text fonts: Segoe UI Variable, or Montserrat from the fonts\ folder next to the exe (falls back to Segoe if missing).
    // Old Font objects are never disposed on a switch: open widgets may still hold them.
    static readonly PrivateFontCollection bundled = LoadBundled();
    public static Font Body, Bold, Small, SmallBold, Tiny, Title, Menu, Heading, Huge, Search, BarText, Clock;
    public static readonly Font Icon = new Font("Segoe Fluent Icons", 12f);
    public static readonly Font IconSmall = new Font("Segoe Fluent Icons", 8f);
    public static readonly Font IconMenu = new Font("Segoe Fluent Icons", 10f);
    public static readonly Font IconBig = new Font("Segoe Fluent Icons", 16f);
    public static readonly StringFormat Right = Native.Fmt(StringAlignment.Far, StringAlignment.Center);
    public static readonly StringFormat Left = Native.Fmt(StringAlignment.Near, StringAlignment.Center);
    public static readonly StringFormat Center = Native.Fmt(StringAlignment.Center, StringAlignment.Center);
    public static readonly StringFormat Top = Native.Fmt(StringAlignment.Near, StringAlignment.Far);
    public static readonly StringFormat Bottom = Native.Fmt(StringAlignment.Near, StringAlignment.Near);
    // Panels follow the Windows light/dark app theme and accent; Reload() runs when either changes.
    public static bool Light;
    public static Color Accent, OnAccent, Green, Fore, Base, Surface;
    public static Brush Text, Dim;
    public static readonly Color BarGreen = Color.FromArgb(120, 222, 130); // the bar itself is always dark
    static readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();

    static Theme() { SetFont(false); Reload(); }

    static PrivateFontCollection LoadBundled()
    {
        var c = new PrivateFontCollection();
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");
            if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.ttf")) c.AddFontFile(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return c;
    }

    // Static Montserrat files register as "Montserrat" and "Montserrat SemiBold".
    static FontFamily Bundled(string name)
    {
        foreach (var f in bundled.Families) if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    public static bool MontserratAvailable { get { return Bundled("Montserrat") != null; } }

    static Font Make(float size, bool semi, bool display, bool montserrat)
    {
        if (montserrat)
        {
            var fam = Bundled(semi ? "Montserrat SemiBold" : "Montserrat");
            if (fam != null) return new Font(fam, size * 0.93f); // Montserrat runs larger than Segoe at the same size
            fam = Bundled("Montserrat");
            if (fam != null) return new Font(fam, size * 0.93f, semi ? FontStyle.Bold : FontStyle.Regular);
        }
        return new Font(display ? (semi ? "Segoe UI Variable Display Semib" : "Segoe UI Variable Display") : (semi ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text"), size);
    }

    public static void SetFont(bool montserrat)
    {
        Body = Make(10.5f, false, false, montserrat);
        Bold = Make(10.5f, true, false, montserrat);
        Small = Make(9f, false, false, montserrat);
        SmallBold = Make(9.5f, true, false, montserrat);
        Tiny = Make(8f, true, false, montserrat);
        Title = Make(15f, true, true, montserrat);
        Menu = Make(9.5f, false, false, montserrat);
        Heading = Make(14f, true, true, montserrat);
        Huge = Make(34f, true, true, montserrat);
        Search = Make(15f, false, true, montserrat);
        BarText = Make(9.5f, true, false, montserrat);
        Clock = Make(10f, true, true, montserrat);
    }

    public static void Reload() { Reload(!Shell.Dark); }

    public static void Reload(bool light) // explicit light/dark: used by the style self-check
    {
        Light = light;
        Accent = AccentOverride.IsEmpty ? Native.Accent(Light) : AccentOverride; // override is for this app only
        OnAccent = Native.Luma(Accent) > 0.55f ? Color.FromArgb(20, 20, 24) : Color.White; // readable text on accent fills
        Palette(Glass.Style, Light, Accent, out Fore, out Base, out Surface);
        Green = Light ? Color.FromArgb(24, 128, 56) : BarGreen;
        Text = new SolidBrush(Fore);
        Dim = new SolidBrush(Color.FromArgb(Light ? 160 : 175, Fore));
    }

    // Colours for a style: text, window background, and raised surfaces (cards, controls) on it.
    public static void Palette(int style, bool light, Color accent, out Color fore, out Color bas, out Color surface)
    {
        fore = light ? Color.FromArgb(28, 27, 31) : Color.White;
        switch (style)
        {
            case 0: // Flat: solid greys, no gradients, no transparency
                bas = light ? Color.FromArgb(243, 243, 245) : Color.FromArgb(32, 32, 35);
                surface = light ? Color.FromArgb(230, 230, 234) : Color.FromArgb(46, 46, 50);
                break;
            case 1: // Material You: solid tonal surfaces tinted by the accent (M3 surface / surface-container)
                bas = Mix(light ? Color.FromArgb(252, 250, 252) : Color.FromArgb(20, 19, 24), accent, light ? 0.06f : 0.10f);
                surface = Mix(bas, accent, light ? 0.14f : 0.16f);
                break;
            case 3: // Neumorphism: one soft grey everything is pressed out of
                fore = light ? Color.FromArgb(52, 58, 70) : Color.FromArgb(222, 226, 234);
                bas = light ? Color.FromArgb(225, 229, 236) : Color.FromArgb(38, 41, 47);
                surface = light ? Color.FromArgb(233, 237, 244) : Color.FromArgb(46, 49, 56);
                break;
            case 5: // Paper: warm, solid, ink-like text (the texture is drawn on top)
                fore = light ? Color.FromArgb(52, 46, 40) : Color.FromArgb(236, 230, 220);
                bas = light ? Color.FromArgb(238, 231, 214) : Color.FromArgb(36, 33, 30);
                surface = light ? Color.FromArgb(249, 245, 235) : Color.FromArgb(50, 46, 42);
                break;
            case 6: // Clay: puffy pastel tinted by the accent
                fore = light ? Color.FromArgb(44, 38, 58) : Color.White;
                bas = Mix(light ? Color.FromArgb(250, 246, 255) : Color.FromArgb(36, 32, 46), accent, light ? 0.20f : 0.24f);
                surface = Mix(bas, Color.White, light ? 0.5f : 0.09f);
                break;
            case 7: // Brutalist: loud flat colour, black ink
                fore = light ? Color.FromArgb(12, 12, 12) : Color.White;
                bas = light ? Mix(Color.White, accent, 0.38f) : Color.FromArgb(22, 22, 22);
                surface = light ? Color.White : Color.FromArgb(40, 40, 40);
                break;
            case 8: // Aurora: near-black (or near-white) with accent light glowing behind
                bas = light ? Color.FromArgb(248, 248, 252) : Color.FromArgb(12, 12, 18);
                surface = light ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255);
                break;
            case 2: // Acrylic: like Windows' own menus, almost nothing over the material so its colour shows through
                bas = light ? Color.FromArgb(246, 246, 250) : Color.FromArgb(18, 18, 22);
                surface = light ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(12, 255, 255, 255);
                break;
            default: // Glass: translucent
                bas = light ? Color.FromArgb(246, 246, 250) : Color.FromArgb(18, 18, 22);
                surface = light ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(24, 255, 255, 255);
                break;
        }
    }

    // The same colour turned round the colour wheel (for Aurora's second glow).
    public static Color Hue(Color c, float degrees)
    {
        float h = ((c.GetHue() + degrees) % 360 + 360) % 360, s = Math.Max(0.55f, c.GetSaturation()), v = Math.Max(0.6f, Math.Max(c.R, Math.Max(c.G, c.B)) / 255f);
        float cc = v * s, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc, r, g, b;
        if (h < 60) { r = cc; g = x; b = 0; } else if (h < 120) { r = x; g = cc; b = 0; } else if (h < 180) { r = 0; g = cc; b = x; }
        else if (h < 240) { r = 0; g = x; b = cc; } else if (h < 300) { r = x; g = 0; b = cc; } else { r = cc; g = 0; b = x; }
        return Color.FromArgb(c.A, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    public static Color AccentOverride; // Empty = follow the Windows accent

    public static Color A(int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), Fore); }
    public static Color OnFill(float t) { return Mix(Fore, OnAccent, t); } // content colour as its surface fills with accent

    // Small cache so per-frame colours don't allocate a brush on every paint.
    public static Brush B(Color c)
    {
        SolidBrush b;
        if (brushes.TryGetValue(c.ToArgb(), out b)) return b;
        // Forget, don't Dispose: widgets may still hold a brush from here (e.g. the green "Charging" caption),
        // and painting with a disposed brush throws "Parameter is not valid". The GC frees them once unused.
        if (brushes.Count > 64) brushes.Clear();
        return brushes[c.ToArgb()] = new SolidBrush(c);
    }

    public static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }
}

// Rounded acrylic window that hosts painted widgets. Animation timer only runs while something moves.
class Glass : Form
{
    public readonly float S;
    public readonly List<Widget> Items = new List<Widget>();
    protected bool Tool = true, CloseOnDeactivate = true;
    protected int Tint = 150;
    // Styles. 2 Acrylic and 4 Glass are see-through: the compositor blurs what's behind (Acrylic: Windows' own material;
    // Glass: a plain blur with our frost and lit edge on top). Everything else is painted by us, opaque.
    // Saved settings store the index, so new styles go at the end (3 used to be Mica).
    public static readonly string[] StyleNames = { "Flat", "Material You", "Acrylic", "Neumorphism", "Glass", "Paper", "Clay", "Brutalist", "Aurora" };
    public static int Style = 2;
    public static bool Dwm { get { return Style == 2; } }
    public static bool Translucent { get { return Style == 2 || Style == 4; } }
    static int BackdropType { get { return Style == 2 || Style == 4 ? 3 : 1; } } // Glass panels: Acrylic's blur (the plain blur goes flat once DWM rounds the corners); the bar and widgets keep their clearer glass
    protected float Corner = 24; // logical px; small menus use less
    protected virtual int Material { get { return BackdropType; } }   // what the compositor draws behind the window
    protected virtual int MaterialTint { get { return 0; } }          // tint for Native.TintedBlur
    protected virtual bool SystemCorners { get { return Translucent; } } // true: DWM rounds (8px, with shadow), which also clips the blur; false: our region
    protected virtual void PaintMaterial(Graphics g, GraphicsPath card) { } // a window that draws its own material paints it here
    protected virtual string MaterialKey { get { return ""; } }           // changes whenever PaintMaterial would draw something else
    // Open and close: the window fades in while settling from slightly smaller and higher (top-centre, as if it came
    // out of the bar); closing runs it back, quicker. Only runs for those few frames.
    protected virtual bool Animated { get { return true; } }
    float appear = 1, appearFrom;   // 0 = gone, 1 = fully shown
    bool vanishing, closeDone;
    Action afterVanish;
    readonly System.Diagnostics.Stopwatch appearClock = new System.Diagnostics.Stopwatch();
    Bitmap frame;
    const float OpenMs = 180, CloseMs = 120;

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); if (Animated) appear = 0; } // before the first paint: no flash of the full window
    protected override void OnShown(EventArgs e) { base.OnShown(e); if (Animated) Appear(); }

    // Reusable windows (the control center) are hidden instead of destroyed, so opening again costs one copy of the
    // last frame instead of ~30 ms of building a new window and ~60 ms warming its first frame.
    protected virtual bool Reusable { get { return false; } }
    public void Dismiss() { if (Reusable) Vanish(Hide); else Close(); }
    protected virtual void OnReopen() { }
    bool shownOnce;
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        if (shownOnce) { if (Animated) { appear = 0; Appear(); } OnReopen(); }
        shownOnce = true;
    }

    // Builds the window and its first frame off screen (fonts, background), so the first real open is instant too.
    public void Warm()
    {
        var h = Handle;
        using (var b = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(b)) OnPaint(new PaintEventArgs(g, new Rectangle(0, 0, Width, Height)));
    }

    // A frame is only recomposed when something asked for it (Invalidate); Windows' own repaints (showing the window
    // again, uncovering it) just copy the last one.
    bool dirty = true, frameOnly;
    protected override void OnInvalidated(InvalidateEventArgs e) { if (!frameOnly) dirty = true; frameOnly = false; base.OnInvalidated(e); }

    // Plays the opening (from wherever it is now, so a reopen mid-close turns smoothly).
    protected void Appear()
    {
        if (!Animated) return;
        vanishing = false;
        appearFrom = appear;
        appearClock.Restart();
        StartAppear();
    }

    bool appearRunning, appearLoop;
    void StartAppear()
    {
        appearRunning = true;
        if (appearLoop) return; // already animating: it picks up the new direction
        appearLoop = true;
        BeginInvoke((Action)(() => Native.Vsync(() =>
        {
            if (appearRunning && !IsDisposed) { AppearStep(); if (!IsDisposed && Visible) Update(); }
            if (appearRunning && !IsDisposed) return true;
            appearLoop = false;
            return false;
        })));
    }

    // Plays the closing, then runs `then` (Close, or Hide for windows that are reused).
    protected void Vanish(Action then)
    {
        if (!Animated || !Visible) { then(); return; }
        if (vanishing) return;
        vanishing = true;
        afterVanish = then;
        appearFrom = appear;
        appearClock.Restart();
        StartAppear();
    }

    void AppearStep()
    {
        float t = Math.Min(1, (float)appearClock.Elapsed.TotalMilliseconds / (vanishing ? CloseMs * appearFrom : OpenMs));
        if (vanishing) appear = appearFrom * (1 - t) * (1 - t);                                   // ease in: leaves quickly
        else appear = appearFrom + (1 - appearFrom) * (1 - (1 - t) * (1 - t) * (1 - t));          // ease out: settles gently
        frameOnly = true; // only the fade/scale changes: reuse the composed frame
        Invalidate();
        if (t < 1) return;
        appearRunning = false;
        if (vanishing) { vanishing = false; var a = afterVanish; afterVanish = null; if (a != null) a(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!closeDone && Animated && Visible && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
        {
            e.Cancel = true; // close for real once the closing animation has played
            Vanish(() => { closeDone = true; Close(); });
            return;
        }
        base.OnFormClosing(e);
    }

    Bitmap bg;     // the window's background, painted once and copied in every frame
    string bgKey;
    // Paper texture, built once with a fixed seed so it never shimmers.
    internal static readonly TextureBrush PaperTex = MakePaper();

    // Brutalist cards leave room for their hard offset shadow.
    static RectangleF CardRect(RectangleF full, int style, float s) { return style == 7 ? new RectangleF(full.X, full.Y, full.Width - 5 * s, full.Height - 5 * s) : full; }

    // A style's window background (also used, small, by the style picker).
    public static void PaintCard(Graphics g, RectangleF full, float radius, int style, bool light, Color bas, Color accent, float s)
    {
        var card = CardRect(full, style, s);
        if (style == 7) // hard offset shadow: ink in light mode, the accent in dark mode
            using (var sp = Native.Round(new RectangleF(full.X + 5 * s, full.Y + 5 * s, card.Width, card.Height), radius))
                Native.Fill(g, sp, light ? Color.FromArgb(14, 14, 14) : accent);
        using (var p = Native.Round(card, radius))
        {
            Native.Fill(g, p, Color.FromArgb(style == 2 || style == 4 ? 0 : 255, bas));
            switch (style)
            {
                case 3: // neumorphism: light falls from the top-left, the surface swells toward it
                    using (var sw = new LinearGradientBrush(card, Color.FromArgb(light ? 90 : 14, 255, 255, 255), Color.FromArgb(light ? 26 : 70, 0, 0, 0), LinearGradientMode.ForwardDiagonal)) g.FillPath(sw, p);
                    break;
                case 4: // glassmorphism: frosted white over the blur, brighter toward a top-left light
                    using (var frost = new LinearGradientBrush(card, Color.FromArgb(light ? 70 : 22, 255, 255, 255), Color.FromArgb(light ? 20 : 2, 255, 255, 255), LinearGradientMode.ForwardDiagonal)) g.FillPath(frost, p);
                    break;
                case 5: // paper: mottled pulp, fibres and flecks, edges a touch darker like a real sheet
                    g.FillPath(PaperTex, p);
                    using (var vignette = new PathGradientBrush(p) { CenterColor = Color.FromArgb(0, 60, 40, 20), SurroundColors = new[] { Color.FromArgb(light ? 34 : 50, 60, 40, 20) }, FocusScales = new PointF(0.85f, 0.8f) })
                        g.FillPath(vignette, p);
                    break;
                case 6: // clay: a soft glow across the top, shade pooling at the bottom
                    var st = g.Save();
                    g.SetClip(p, CombineMode.Intersect);
                    using (var glow = new LinearGradientBrush(new RectangleF(card.X, card.Y - 1, card.Width, card.Height * 0.5f + 2), Color.FromArgb(light ? 130 : 46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                        g.FillRectangle(glow, card.X, card.Y, card.Width, card.Height * 0.5f);
                    using (var shade = new LinearGradientBrush(new RectangleF(card.X, card.Y + card.Height * 0.55f - 1, card.Width, card.Height * 0.45f + 2), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(light ? 34 : 80, 0, 0, 0), LinearGradientMode.Vertical))
                        g.FillRectangle(shade, card.X, card.Y + card.Height * 0.55f, card.Width, card.Height * 0.45f);
                    g.Restore(st);
                    break;
                case 8: // aurora: two soft glows, the accent and its neighbour on the colour wheel
                    var sa = g.Save();
                    g.SetClip(p, CombineMode.Intersect);
                    Glow(g, new RectangleF(card.X - card.Width * 0.25f, card.Y - card.Height * 0.55f, card.Width * 1.0f, card.Height * 1.3f), Color.FromArgb(light ? 110 : 150, accent));
                    Glow(g, new RectangleF(card.X + card.Width * 0.35f, card.Y + card.Height * 0.25f, card.Width * 0.95f, card.Height * 1.2f), Color.FromArgb(light ? 90 : 120, Theme.Hue(accent, 70)));
                    g.Restore(sa);
                    break;
            }
        }
    }

    static void Glow(Graphics g, RectangleF r, Color c)
    {
        using (var e = new GraphicsPath())
        {
            e.AddEllipse(r);
            using (var b = new PathGradientBrush(e) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } }) g.FillPath(b, e);
        }
    }

    // A style's outline.
    public static void PaintEdge(Graphics g, RectangleF full, float radius, int style, bool light, Color fore, float s)
    {
        var c = CardRect(full, style, s);
        var edge = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1.5f, c.Height - 1.5f);
        using (var p = Native.Round(edge, radius))
            switch (style)
            {
                case 4: // glass edge: bright where the light hits (top-left), fading round the far side
                    using (var rim = new LinearGradientBrush(edge, Color.FromArgb(light ? 230 : 150, 255, 255, 255), Color.FromArgb(light ? 70 : 18, 255, 255, 255), LinearGradientMode.ForwardDiagonal))
                    using (var pen = new Pen(rim, Math.Max(1, s))) g.DrawPath(pen, p);
                    break;
                case 3: // neumorphism: lit rim top-left, shaded rim bottom-right
                    var inset = new RectangleF(edge.X + s, edge.Y + s, edge.Width - 2 * s, edge.Height - 2 * s);
                    using (var ip = Native.Round(inset, Math.Max(1, radius - s)))
                    using (var rim = new LinearGradientBrush(inset, Color.FromArgb(light ? 230 : 40, 255, 255, 255), Color.FromArgb(light ? 50 : 150, 0, 0, 0), LinearGradientMode.ForwardDiagonal))
                    using (var pen = new Pen(rim, 2 * s)) g.DrawPath(pen, ip);
                    break;
                case 6: // clay: a thick soft rim, brighter on top
                    var ci = new RectangleF(edge.X + 1.5f * s, edge.Y + 1.5f * s, edge.Width - 3 * s, edge.Height - 3 * s);
                    using (var cp = Native.Round(ci, Math.Max(1, radius - 1.5f * s)))
                    using (var rim = new LinearGradientBrush(new RectangleF(ci.X, ci.Y - 1, ci.Width, ci.Height + 2), Color.FromArgb(light ? 150 : 50, 255, 255, 255), Color.FromArgb(light ? 10 : 4, 255, 255, 255), LinearGradientMode.Vertical))
                    using (var pen = new Pen(rim, 3 * s)) g.DrawPath(pen, cp);
                    break;
                case 7: // brutalist: thick ink outline
                    using (var pen = new Pen(light ? Color.FromArgb(14, 14, 14) : Color.White, 2.5f * s)) g.DrawPath(pen, p);
                    break;
                case 8: // aurora: a faint glassy hairline
                    using (var pen = new Pen(Color.FromArgb(light ? 30 : 40, light ? 0 : 255, light ? 0 : 255, light ? 0 : 255), Math.Max(1, s))) g.DrawPath(pen, p);
                    break;
                case 1:
                    break; // Material You: tonal surfaces need no outline
                default: // flat hairline (Flat, Acrylic, Paper)
                    using (var pen = new Pen(Color.FromArgb(light ? 28 : 34, fore), 1)) g.DrawPath(pen, p);
                    break;
            }
    }

    // A 256px paper tile: soft cloudy pulp (two octaves of smooth noise), long and short curved fibres in warm and
    // pale tones, a few ink-dark flecks, and a fine tooth. All faint: it reads as texture, never as a pattern.
    static TextureBrush MakePaper()
    {
        const int N = 256;
        var rnd = new Random(11);
        var b = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        Func<int, float[,]> lattice = cells => { var l = new float[cells + 1, cells + 1]; for (int y = 0; y < cells; y++) for (int x = 0; x < cells; x++) l[x, y] = (float)rnd.NextDouble(); for (int i = 0; i <= cells; i++) { l[cells, i] = l[0, i]; l[i, cells] = l[i, 0]; } return l; };
        var coarse = lattice(8);
        var fine = lattice(32);
        Func<float[,], int, int, int, float> smooth = (l, cells, x, y) =>
        {
            float fx = (float)x * cells / N, fy = (float)y * cells / N;
            int ix = (int)fx, iy = (int)fy;
            float tx = fx - ix, ty = fy - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty); // smoothstep: no grid seams
            float top = l[ix, iy] + (l[ix + 1, iy] - l[ix, iy]) * tx, bottom = l[ix, iy + 1] + (l[ix + 1, iy + 1] - l[ix, iy + 1]) * tx;
            return top + (bottom - top) * ty;
        };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float v = smooth(coarse, 8, x, y) * 0.65f + smooth(fine, 32, x, y) * 0.35f - 0.5f; // -0.5..0.5 cloudy pulp
                int tooth = rnd.Next(100); // fine grain on top
                if (tooth < 10) v += 0.35f; else if (tooth < 18) v -= 0.35f;
                b.SetPixel(x, y, v > 0 ? Color.FromArgb((int)Math.Min(40, v * 46), 255, 252, 240) : Color.FromArgb((int)Math.Min(40, -v * 40), 90, 64, 36));
            }
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < 150; i++) // fibres: gentle curves, drawn twice offset by a tile so they wrap seamlessly
            {
                float x = rnd.Next(N), y = rnd.Next(N), a = (float)(rnd.NextDouble() * Math.PI * 2), len = i < 20 ? 18 + rnd.Next(26) : 5 + rnd.Next(12);
                float bend = (float)(rnd.NextDouble() - 0.5) * len * 0.6f;
                var p1 = new PointF(x, y);
                var p3 = new PointF(x + len * (float)Math.Cos(a), y + len * (float)Math.Sin(a));
                var p2 = new PointF((p1.X + p3.X) / 2 - bend * (float)Math.Sin(a), (p1.Y + p3.Y) / 2 + bend * (float)Math.Cos(a));
                using (var pen = new Pen(rnd.Next(3) == 0 ? Color.FromArgb(22, 255, 250, 232) : Color.FromArgb(18, 110, 80, 44), rnd.Next(4) == 0 ? 1.4f : 0.8f))
                    for (int ox = -N; ox <= N; ox += N)
                        for (int oy = -N; oy <= N; oy += N)
                            g.DrawBezier(pen, p1.X + ox, p1.Y + oy, p2.X + ox, p2.Y + oy, p2.X + ox, p2.Y + oy, p3.X + ox, p3.Y + oy);
            }
            for (int i = 0; i < 26; i++) // flecks: tiny bits of darker pulp
            {
                float x = rnd.Next(N), y = rnd.Next(N), r = 0.5f + (float)rnd.NextDouble() * 0.9f;
                using (var fleck = new SolidBrush(Color.FromArgb(40 + rnd.Next(40), 70, 50, 30))) g.FillEllipse(fleck, x, y, r * 2, r * 2);
            }
        }
        return new TextureBrush(b);
    }

    public float Radius { get { return (SystemCorners ? 8 : CornerFor(Style, Corner)) * S; } }
    public static float CornerFor(int style, float corner) { return style == 1 || style == 3 ? corner + 4 : style == 4 || style == 8 ? corner : style == 6 ? corner + 10 : style == 7 ? Math.Min(corner, 8) : style == 5 ? Math.Min(corner, 10) : Math.Min(corner, 14); }
    protected RectangleF ScrollArea; // widgets with Scrolls = true move inside this area
    float scroll, scrollTarget, scrollVel, scrollMax;
    protected Rectangle AnchorRect;
    Bitmap buf;
    Widget hover, pressed, rightHit;
    int focus = -1;

    public Glass(float s)
    {
        S = s;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        tipWait.Tick += (a, b) => ShowTip();
        Deactivate += (a, b) => { if (CloseOnDeactivate) Dismiss(); };
        FormClosed += (a, b) => { tipWait.Dispose(); tips.Dispose(); if (buf != null) buf.Dispose(); if (frame != null) frame.Dispose(); if (bg != null) bg.Dispose(); };
    }

    public T Add<T>(T w) where T : Widget { w.Owner = this; Items.Add(w); return w; }
    protected void ClearItems() { Items.Clear(); hover = pressed = null; focus = -1; }
    protected bool Pressing { get { return pressed != null; } }
    // Hover/press easing of the controls inside, in step with the display (see Native.Vsync).
    bool stepping;
    public void Kick()
    {
        if (IsDisposed || stepping || !IsHandleCreated) return; // no window yet: its first paint shows the end state
        stepping = true;
        BeginInvoke((Action)(() => Native.Vsync(() =>
        {
            bool more = !IsDisposed && Step();
            if (more && Visible) Update();
            if (!more) stepping = false;
            return more;
        })));
    }

    // Starts an OLE file drag (to Explorer, browsers, mail). Mouse-up is eaten by the drag loop, so end the press here.
    public void DragOut(string[] files)
    {
        if (pressed != null) { pressed.Pressed = false; pressed = null; }
        Kick();
        bool keep = CloseOnDeactivate;
        CloseOnDeactivate = false;
        DoDragDrop(new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy);
        CloseOnDeactivate = keep;
    }

    // Hover tile: shown under the anchor without taking focus or closing on deactivate; its owner closes it.
    bool quiet;
    protected override bool ShowWithoutActivation { get { return quiet; } }
    public void ShowQuiet(Rectangle anchor)
    {
        quiet = true;
        CloseOnDeactivate = false;
        if (IsHandleCreated) SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) | 0x08000000); // built early (warmed): CreateParams has run already
        AnchorRect = anchor;
        Place(Width, Height);
        Show();
    }

    // The hover tile was clicked into: from here it behaves like a popup opened by click (focus, closes on deactivate).
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int v);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    public void Promote()
    {
        quiet = false;
        SetWindowLong(Handle, -20, GetWindowLong(Handle, -20) & ~0x08000000);
        Activate();
        CloseOnDeactivate = true; // after Activate: no deactivate from the switch can close it
    }

    public void ShowAt(Rectangle anchor)
    {
        AnchorRect = anchor;
        Place(Width, Height);
        Show();
        Activate();
    }

    // Hangs below the anchor, aligned with its left edge (or centred for the search bar) and kept on screen.
    protected void Place(int w, int h)
    {
        var wa = Screen.FromRectangle(AnchorRect).WorkingArea;
        float ac = AnchorRect.X + AnchorRect.Width / 2f, mid = wa.X + wa.Width / 2f;
        bool center = Math.Abs(ac - mid) < wa.Width / 6f && AnchorRect.Width > 1;
        int x = center ? (int)(ac - w / 2f) : AnchorRect.Left;
        x = Math.Max(wa.Left + 6, Math.Min(x, wa.Right - w - 6));
        int y = AnchorRect.Bottom;
        if (y + h > wa.Bottom - 6 && AnchorRect.Top - h >= wa.Top) y = Math.Min(AnchorRect.Top, wa.Bottom) - h; // no room below: open upward
        var to = new Rectangle(x, y, w, Math.Min(h, wa.Bottom - y - 6));
        if (!Visible || !Animated || Math.Abs(to.Height - Height) < 4 || to.Width != Width) { glideTo = Bounds = to; glideClock.Reset(); return; }
        glideFrom = Bounds; glideTo = to; glideClock.Restart(); // e.g. a tile's details opening
        if (!gliding) { gliding = true; BeginInvoke((Action)(() => Native.Vsync(() => { if (!IsDisposed && GlideStep()) return true; gliding = false; return false; }))); }
    }

    readonly System.Diagnostics.Stopwatch glideClock = new System.Diagnostics.Stopwatch();
    Rectangle glideFrom, glideTo;
    bool gliding;
    bool GlideStep()
    {
        if (!glideClock.IsRunning) return false; // placed directly meanwhile: nothing left to glide
        float t = Math.Min(1, (float)glideClock.Elapsed.TotalMilliseconds / 170f), e = 1 - (1 - t) * (1 - t) * (1 - t); // ease out
        Bounds = new Rectangle(glideTo.X, (int)(glideFrom.Y + (glideTo.Y - glideFrom.Y) * e), glideTo.Width, (int)(glideFrom.Height + (glideTo.Height - glideFrom.Height) * e));
        Update();
        return t < 1;
    }

    protected override CreateParams CreateParams
    {
        get { var c = base.CreateParams; if (Tool) c.ExStyle |= 0x80; if (quiet) c.ExStyle |= 0x08000000; return c; } // TOOLWINDOW, NOACTIVATE while a hover tile
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.Backdrop(Handle, Material, !Theme.Light, SystemCorners, MaterialTint); Native.ActiveFrame(Handle, NeedsActive); }
    bool NeedsActive { get { return Material == 2 || Material == 3; } } // a system backdrop (Acrylic) shows only on "active" windows
    protected override void WndProc(ref Message m) { if (NeedsActive) Native.StayActive(ref m); base.WndProc(ref m); }
    // Theme or style changed while open: backdrop, corner shape and buffer all follow, not only the colours.
    public void ApplyTheme() { if (IsHandleCreated) { Native.Backdrop(Handle, Material, !Theme.Light, SystemCorners, MaterialTint); Native.ActiveFrame(Handle, NeedsActive); OnResize(EventArgs.Empty); } Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int d = (int)(2 * Radius);
        Native.SetWindowRgn(Handle, SystemCorners ? IntPtr.Zero : Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, d, d), true); // region only clips our pixels, not acrylic
        if (buf != null) { buf.Dispose(); buf = null; }
        if (bg != null) { bg.Dispose(); bg = null; }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (buf == null) { buf = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); buf.SetResolution(96 * S, 96 * S); dirty = true; }
        if (dirty) Compose();
        Present(e.Graphics);
    }

    void Compose()
    {
        dirty = false;
        using (var g = Graphics.FromImage(buf))
        {
            var full = new RectangleF(0, 0, Width, Height);
            string key = Style + "|" + Theme.Light + "|" + Theme.Base.ToArgb() + "|" + Theme.Accent.ToArgb() + "|" + Radius + "|" + MaterialKey;
            if (bg == null || key != bgKey) // textures, gradients, glows and blurred wallpaper are costly: draw them only when they change
            {
                if (bg == null) { bg = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb); bg.SetResolution(96 * S, 96 * S); }
                bgKey = key;
                using (var bgg = Graphics.FromImage(bg))
                {
                    bgg.Clear(Color.Transparent);
                    bgg.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var p = Native.Round(full, Radius)) PaintMaterial(bgg, p);
                    PaintCard(bgg, full, Radius, Style, Theme.Light, Theme.Base, Theme.Accent, S);
                }
            }
            g.CompositingMode = CompositingMode.SourceCopy; // straight copy, no blending: the cheapest way to start the frame
            g.DrawImageUnscaled(bg, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; // ClearType needs an opaque surface
            foreach (var w in Items)
            {
                if (!w.Visible) continue;
                if (!w.Scrolls) { w.Paint(g); continue; }
                var st = g.Save();
                g.SetClip(ScrollArea, CombineMode.Intersect);
                g.TranslateTransform(0, -scroll);
                w.Paint(g);
                g.Restore(st);
            }
            if (scrollMax > 0) // slim scrollbar
            {
                float h = Math.Max(24 * S, ScrollArea.Height * ScrollArea.Height / (ScrollArea.Height + scrollMax)), y = ScrollArea.Y + (ScrollArea.Height - h) * scroll / scrollMax;
                using (var p = Native.Round(new RectangleF(ScrollArea.Right - 6 * S, y, 4 * S, h), 2 * S)) Native.Fill(g, p, Theme.A(80));
            }
            PaintEdge(g, full, Radius, Style, Theme.Light, Theme.Fore, S);
        }
    }

    void Present(Graphics target)
    {
        if (appear >= 0.998f) { Native.Blit(target, buf); return; }
        float e = Math.Max(0, appear);
        int dy = (int)Math.Round(-16 * S * (1 - e));
        Native.BlitFade(target, buf, e, dy, ref fadeScratch);
    }
    int[] fadeScratch;

    Widget Hit(Point p)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            var w = Items[i];
            if (!w.Visible || !w.Interactive) continue;
            if (w.Scrolls ? ScrollArea.Contains(p) && w.R.Contains(p.X, p.Y + scroll) : w.R.Contains(p)) return w;
        }
        return null;
    }

    // Scrolled widgets live in content coordinates.
    Point Local(Widget w, Point p) { return w != null && w.Scrolls ? new Point(p.X, p.Y + (int)scroll) : p; }

    protected void SetScroll(RectangleF area, float contentBottom)
    {
        ScrollArea = area;
        scrollMax = Math.Max(0, contentBottom - area.Bottom);
        scroll = Math.Max(0, Math.Min(scroll, scrollMax));
        scrollTarget = Math.Max(0, Math.Min(scrollTarget, scrollMax));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (scrollMax <= 0) return;
        scrollTarget = Math.Max(0, Math.Min(scrollMax, scrollTarget - e.Delta / 120f * 64 * S));
        Kick();
    }

    void SetHover(Widget w)
    {
        if (w == hover) return;
        if (hover != null) hover.Hover = false;
        hover = w;
        if (w != null) w.Hover = true;
        Kick();
        tips.Hide(this);
        tipWait.Stop();
        if (w != null && w.Tip != null) tipWait.Start();
    }

    readonly ToolTip tips = new ToolTip();
    readonly Timer tipWait = new Timer { Interval = 500 };

    void ShowTip()
    {
        tipWait.Stop();
        if (hover == null || hover.Tip == null || !Visible) return;
        tips.Show(hover.Tip, this, (int)hover.R.X, (int)(hover.R.Bottom - (hover.Scrolls ? scroll : 0) + 4 * S), 4000);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (pressed != null) { pressed.Drag(Local(pressed, e.Location)); Invalidate(); return; }
        SetHover(Hit(e.Location));
    }
    protected override void OnMouseLeave(EventArgs e) { if (pressed == null) SetHover(null); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { rightHit = Hit(e.Location); return; }
        pressed = Hit(e.Location);
        if (pressed == null) return;
        pressed.Pressed = true;
        pressed.Down(Local(pressed, e.Location));
        Kick();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { var h = Hit(e.Location); if (h != null && h == rightHit) h.Context(PointToScreen(e.Location)); return; }
        var p = pressed;
        pressed = null;
        if (p == null) return;
        p.Pressed = false;
        Kick();
        var lp = Local(p, e.Location);
        p.Up(lp, p.R.Contains(lp));
    }

    // Keyboard: Tab/arrows move focus, Enter/Space activate, Left/Right adjust, Esc closes.
    protected override bool ProcessCmdKey(ref Message m, Keys k)
    {
        var f = focus >= 0 && focus < Items.Count ? Items[focus] : null;
        var tf = f as TextField;
        if (tf != null) // typing: only editing keys are ours, the rest become characters (OnKeyPress)
        {
            switch (k)
            {
                case Keys.Escape: Dismiss(); return true;
                case Keys.Enter: tf.Activate(); return true;
                case Keys.Up: case Keys.Down: if (tf.Cycle != null) tf.Cycle(k == Keys.Down ? 1 : -1); return true;
                case Keys.Tab: MoveFocus(1); return true;
                case Keys.Shift | Keys.Tab: MoveFocus(-1); return true;
            }
            return tf.Edit(k) || base.ProcessCmdKey(ref m, k);
        }
        switch (k)
        {
            case Keys.Escape: Dismiss(); return true;
            case Keys.Tab: case Keys.Down: MoveFocus(1); return true;
            case Keys.Shift | Keys.Tab: case Keys.Up: MoveFocus(-1); return true;
            case Keys.Enter: case Keys.Space: if (f != null) f.Activate(); return true;
            case Keys.Left: case Keys.Right: if (f != null) { f.Key(k); Invalidate(); } return true;
        }
        return base.ProcessCmdKey(ref m, k);
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        var tf = focus >= 0 && focus < Items.Count ? Items[focus] as TextField : null;
        if (tf != null && !char.IsControl(e.KeyChar)) { tf.Type(e.KeyChar); e.Handled = true; }
        base.OnKeyPress(e);
    }

    public void FocusOn(Widget w)
    {
        if (focus >= 0 && focus < Items.Count) Items[focus].Focused = false;
        focus = Items.IndexOf(w);
        if (focus >= 0) w.Focused = true;
        Kick();
    }

    void MoveFocus(int d)
    {
        int n = Items.Count, j = focus < 0 ? (d > 0 ? -1 : 0) : focus;
        for (int i = 0; i < n; i++)
        {
            j = (j + d + n) % n;
            if (Items[j].Visible && Items[j].Interactive) break;
        }
        if (focus >= 0 && focus < n) Items[focus].Focused = false;
        focus = j;
        Items[j].Focused = true;
        Kick();
    }

    bool Step()
    {
        bool moving = Native.Spring(ref scroll, ref scrollVel, scrollTarget, 380f, 34f, 0.2f);
        foreach (var w in Items) if (w.Animate()) moving = true;
        Invalidate();
        return moving;
    }
}

abstract class Widget
{
    public RectangleF R;
    public bool Visible = true, Hover, Pressed, Focused;
    public Glass Owner;
    public object Tag;
    public string Tip; // tooltip shown after a short hover
    public bool Scrolls; // lives inside the owner's ScrollArea
    protected float hoverA, pressA, hoverVel, pressVel;
    public virtual bool Interactive { get { return true; } }
    protected float S { get { return Owner.S; } }

    public virtual bool Animate()
    {
        bool a = Native.Spring(ref hoverA, ref hoverVel, Hover || Focused ? 1 : 0, 340f, 30f);
        bool b = Native.Spring(ref pressA, ref pressVel, Pressed ? 1 : 0, 480f, 32f);
        hoverA = Math.Max(0, Math.Min(1, hoverA));
        pressA = Math.Max(0, Math.Min(1, pressA));
        return a | b;
    }
    public abstract void Paint(Graphics g);
    public virtual void Down(Point p) { }
    public virtual void Drag(Point p) { }
    public virtual void Up(Point p, bool inside) { if (inside) Activate(); }
    public virtual void Activate() { }
    public virtual void Key(Keys k) { }
    public virtual void Context(Point screen) { } // right click

    // M3 Expressive shape morph: pressed shapes square off their corners.
    protected float Morph(float rest) { return rest + (6 * S - rest) * pressA; }
    protected void Overlay(Graphics g, GraphicsPath p)
    {
        int a = (int)(hoverA * 18 + pressA * 22);
        if (a > 0) Native.Fill(g, p, Theme.A(a));
        if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawPath(pen, p);
    }
}

class Caption : Widget
{
    public string Text, Icon;
    public Font Font = Theme.Small;
    public Brush Brush;      // null: Theme.Dim, or Theme.Text when Strong
    public bool Strong;
    public Color IconColor;  // empty: Theme.Fore
    public override bool Interactive { get { return false; } }
    public override void Paint(Graphics g)
    {
        float x = R.X;
        if (Icon != null)
        {
            g.DrawString(Icon, Theme.Icon, Theme.B(IconColor.IsEmpty ? Theme.Fore : IconColor), new RectangleF(x, R.Y, 24 * S, R.Height), Theme.Left);
            x += 30 * S;
        }
        g.DrawString(Text, Font, Brush ?? (Strong ? Theme.Text : Theme.Dim), new RectangleF(x, R.Y, R.Right - x, R.Height), Theme.Left);
    }
}

// Tonal surface behind a group of rows.
class Segment : Widget
{
    public override bool Interactive { get { return false; } }
    public override void Paint(Graphics g) { using (var p = Native.Round(R, 20 * S)) { Native.Fill(g, p, Theme.Surface); Native.Gloss(g, p, R, S, 0.45f); } }
}

class MenuRow : Widget
{
    public string Icon, Text, Sub;
    public Image Img; // drawn in place of the Icon glyph (an app's own icon)
    public bool Check, Compact, Selected; // Compact: menu-sized row; Selected: current page in a sidebar
    public Action Click;
    public override void Activate() { if (Click != null) Click(); }
    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph((Compact ? 8 : 16) * S)))
        {
            if (Selected) Native.Fill(g, p, Theme.Surface);
            if (hoverA > 0) Native.Fill(g, p, Color.FromArgb((int)(hoverA * 150), Theme.Accent));
            Overlay(g, p);
        }
        float slide = (hoverA * 3.5f - pressA * 1.5f) * S;
        float ix = (Compact ? 10 : 14) * S + slide, tx = (Compact ? 36 : 48) * S + slide;
        if (Selected) using (var p = Native.Round(new RectangleF(R.X + 2 * S, R.Y + R.Height * 0.3f, 3 * S, R.Height * 0.4f), 1.5f * S)) Native.Fill(g, p, Theme.Accent); // Windows-style page marker
        if (Img != null) { float sz = 22 * S; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(Img, R.X + ix - S, R.Y + (R.Height - sz) / 2, sz, sz); }
        else if (!string.IsNullOrEmpty(Icon)) g.DrawString(Icon, Compact ? Theme.IconMenu : Theme.Icon, Theme.Text, new RectangleF(R.X + ix, R.Y, 24 * S, R.Height), Theme.Left);
        var t = new RectangleF(R.X + tx, R.Y, R.Width - tx - (Check ? 36 : 8) * S, R.Height);
        if (Sub == null) g.DrawString(Text, Compact ? Theme.Menu : Theme.Body, Theme.Text, t, Theme.Left);
        else
        {
            g.DrawString(Text, Theme.Body, Theme.Text, new RectangleF(t.X, t.Y, t.Width, t.Height / 2 + S), Theme.Top);
            g.DrawString(Sub, Theme.Small, Theme.Dim, new RectangleF(t.X, t.Y + t.Height / 2 + 2 * S, t.Width, t.Height / 2), Theme.Bottom);
        }
        if (Check) g.DrawString(G.Check, Theme.Icon, Theme.B(Theme.Accent), new RectangleF(R.Right - 36 * S, R.Y, 24 * S, R.Height), Theme.Center);
    }
}

class IconBtn : Widget
{
    public string Icon, Icon2; // Icon2: small trailing glyph (e.g. a chevron)
    public bool On;
    public Action Click;
    float onA = -1;
    public override void Activate() { if (Click != null) Click(); }
    public override bool Animate() { bool b = base.Animate(); return Native.Ease(ref onA, On ? 1 : 0, 0.25f) | b; }
    public override void Paint(Graphics g)
    {
        if (onA < 0) onA = On ? 1 : 0;
        using (var p = Native.Round(R, Morph(R.Height / 2)))
        {
            Native.Fill(g, p, Theme.Mix(Theme.Surface, Color.FromArgb(225, Theme.Accent), onA));
            Native.Gloss(g, p, R, S, 0.5f + 0.4f * onA);
            Overlay(g, p);
        }
        var fg = Theme.B(Theme.OnFill(onA));
        if (Icon2 == null) { g.DrawString(Icon, Theme.Icon, fg, R, Theme.Center); return; }
        g.DrawString(Icon, Theme.Icon, fg, new RectangleF(R.X + 12 * S, R.Y, 20 * S, R.Height), Theme.Left);
        g.DrawString(Icon2, Theme.IconSmall, fg, new RectangleF(R.Right - 24 * S, R.Y, 14 * S, R.Height), Theme.Left);
    }
}

// App icon from the tray overflow.
class TrayIcon : Widget
{
    public Image Img;
    public Action Click;
    public Action<Point> Menu;
    public override void Activate() { if (Click != null) Click(); }
    public override void Context(Point screen) { if (Menu != null) Menu(screen); }
    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph(R.Height / 2))) Overlay(g, p);
        float sz = 20 * S;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(Img, R.X + (R.Width - sz) / 2, R.Y + (R.Height - sz) / 2, sz, sz);
    }
}

// GNOME-style quick toggle pill.
class Tile : Widget
{
    public string Icon, Title, Sub;
    public bool On, Link, Editing, Dimmed, Expanded; // Link: opens Windows settings instead of toggling
    public int Size = 1;                           // 0: 1x1 icon only, 1: 1x2, 2: 2x2
    public bool Slash;                             // crossed-out icon (camera blocked); Fluent has no camera-off glyph
    public Action Click, Expand, Resize;           // Expand: detail list; Resize: edit mode cycles the size
    public bool Split;                             // Pixel-style: the icon pad toggles, the rest of the tile opens the details
    RectangleF Chevron { get { return new RectangleF(R.Right - 42 * S, R.Y, 42 * S, R.Height); } }
    public RectangleF IconPad
    {
        get
        {
            if (Size == 2) return new RectangleF(R.X + 10 * S, R.Y + 10 * S, 60 * S, 50 * S);
            float h = R.Height - 10 * S;
            return new RectangleF(R.X + 5 * S, R.Y + 5 * S, h * 1.25f, h);
        }
    }
    RectangleF Eye { get { return Size == 0 ? new RectangleF(R.Right - 26 * S, R.Y, 26 * S, 26 * S) : new RectangleF(R.Right - 36 * S, R.Y, 36 * S, R.Height); } }
    public override void Up(Point p, bool inside)
    {
        if (!inside) return;
        if (Editing) { if (Resize == null || Eye.Contains(p)) Activate(); else Resize(); return; }
        if (Split && Size > 0) { if (IconPad.Contains(p)) Activate(); else Expand(); return; } // icon: toggle; name: details
        if (Expand != null && Click == null) { Expand(); return; }                                // details only (sound output, keyboard)
        Activate();
    }
    public override void Context(Point screen) { if (Expand != null && !Editing) Expand(); }
    public override void Key(Keys k) { if (k == Keys.Right && Expand != null && !Editing) Expand(); }
    float onA = -1;
    public override void Activate() { if (Click != null) Click(); }
    public override bool Animate() { bool b = base.Animate(); return Native.Ease(ref onA, On ? 1 : 0, 0.25f) | b; }
    public override void Paint(Graphics g)
    {
        if (onA < 0) onA = On ? 1 : 0;
        if (Split && Size > 0 && !Editing) { PaintSplit(g); return; }
        using (var p = Native.Round(R, Morph(Size == 2 ? 26 * S : R.Height / 2)))
        {
            Native.Fill(g, p, Theme.Mix(Theme.Surface, Color.FromArgb(225, Theme.Accent), onA));
            Native.Gloss(g, p, R, S, 0.5f + 0.4f * onA);
            Overlay(g, p);
        }
        var fg = Theme.B(Theme.OnFill(onA));
        var dim = Theme.B(Color.FromArgb(Theme.Light ? 160 : 175, Theme.OnFill(onA)));
        RectangleF ir = Size == 0 ? R : Size == 2 ? new RectangleF(R.X + 18 * S, R.Y + 14 * S, 32 * S, 32 * S) : new RectangleF(R.X + 16 * S, R.Y, 24 * S, R.Height);
        if (Slash)
        {
            float cx = Size == 0 ? R.X + R.Width / 2 : ir.X + (Size == 2 ? 14 : 10) * S, cy = Size == 2 ? ir.Y + 14 * S : R.Y + R.Height / 2, k = (Size == 2 ? 12 : 9) * S;
            using (var pen = new Pen(Theme.OnFill(onA), 1.8f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
        }
        if (Size == 0) g.DrawString(Icon, Theme.Icon, fg, R, Theme.Center); // 1x1: icon only, name in the tooltip
        else if (Size == 2)
        {
            g.DrawString(Icon, Theme.IconBig, fg, new RectangleF(R.X + 18 * S, R.Y + 14 * S, 32 * S, 32 * S), Theme.Left);
            float x2 = R.X + 18 * S, w2 = R.Right - x2 - (Expand != null ? 46 : 18) * S;
            g.DrawString(Title, Theme.Bold, fg, new RectangleF(x2, R.Bottom - 52 * S, w2, 24 * S), Theme.Left);
            g.DrawString(Sub ?? "", Theme.Small, dim, new RectangleF(x2, R.Bottom - 30 * S, w2, 20 * S), Theme.Left);
        }
        else
        {
            g.DrawString(Icon, Theme.Icon, fg, new RectangleF(R.X + 16 * S, R.Y, 24 * S, R.Height), Theme.Left);
            float x = R.X + 46 * S, w = R.Right - x - 26 * S;
            g.DrawString(Title, Theme.SmallBold, fg, new RectangleF(x, R.Y, w, R.Height / 2 + S), Theme.Top);
            g.DrawString(Sub ?? "", Theme.Small, dim, new RectangleF(x, R.Y + R.Height / 2 + 2 * S, w, R.Height / 2), Theme.Bottom);
        }
        string badge = Editing ? (Dimmed ? G.Hidden : G.Visible) : Link ? G.Open : null;
        if (Expand != null && !Editing && Size > 0)
        {
            var c = Chevron;
            if (Click != null) using (var pen = new Pen(Theme.B(Color.FromArgb(60, Theme.OnFill(onA))), Math.Max(1, S))) g.DrawLine(pen, c.X, c.Y + c.Height * 0.28f, c.X, c.Bottom - c.Height * 0.28f); // a divider only when the two halves do different things
            g.DrawString(Expanded ? G.Up : G.ChevronRight, Theme.IconSmall, fg, c, Theme.Center);
        }
        else if (badge != null && (Size > 0 || Editing)) g.DrawString(badge, Theme.IconSmall, Theme.Dim, Size == 0 ? Eye : new RectangleF(R.Right - 24 * S, R.Y, 14 * S, R.Height), Size == 0 ? Theme.Center : Theme.Left);
        if (Dimmed) using (var p = Native.Round(R, Math.Min(R.Height, R.Width) / 2)) Native.Fill(g, p, Color.FromArgb(120, Theme.Base));
    }

    // Split tile: a quiet body with the name (tap = details), and an icon pad that fills with the accent when on (tap = toggle).
    void PaintSplit(Graphics g)
    {
        using (var p = Native.Round(R, Size == 2 ? 26 * S : R.Height / 2))
        {
            Native.Fill(g, p, Theme.Surface);
            Native.Gloss(g, p, R, S, 0.5f);
            if (Hover && !Pressed) Native.Fill(g, p, Theme.A(10));
        }
        var pad = IconPad;
        using (var p = Native.Round(pad, Morph(Size == 2 ? 20 * S : pad.Height / 2)))
        {
            Native.Fill(g, p, Theme.Mix(Theme.Mix(Theme.Surface, Theme.Fore, 0.08f), Color.FromArgb(225, Theme.Accent), onA));
            Native.Gloss(g, p, pad, S, 0.4f + 0.4f * onA);
        }
        var onPad = Theme.B(Theme.OnFill(onA));
        if (Slash) { float cx = pad.X + pad.Width / 2, cy = pad.Y + pad.Height / 2, k = 9 * S; using (var pen = new Pen(Theme.OnFill(onA), 1.8f * S) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k); }
        g.DrawString(Icon, Size == 2 ? Theme.IconBig : Theme.Icon, onPad, pad, Theme.Center);
        float x = Size == 2 ? R.X + 18 * S : pad.Right + 12 * S, w = R.Right - x - 30 * S;
        if (Size == 2)
        {
            g.DrawString(Title, Theme.Bold, Theme.Text, new RectangleF(x, R.Bottom - 52 * S, w, 24 * S), Theme.Left);
            g.DrawString(Sub ?? "", Theme.Small, Theme.Dim, new RectangleF(x, R.Bottom - 30 * S, w, 20 * S), Theme.Left);
        }
        else
        {
            g.DrawString(Title, Theme.SmallBold, Theme.Text, new RectangleF(x, R.Y, w, R.Height / 2 + S), Theme.Top);
            g.DrawString(Sub ?? "", Theme.Small, Theme.Dim, new RectangleF(x, R.Y + R.Height / 2 + 2 * S, w, R.Height / 2), Theme.Bottom);
        }
        g.DrawString(Expanded ? G.Up : G.ChevronRight, Theme.IconSmall, Theme.Dim, new RectangleF(R.Right - 28 * S, Size == 2 ? R.Bottom - 40 * S : R.Y, 20 * S, Size == 2 ? 20 * S : R.Height), Theme.Center);
    }
}

// M3 Expressive slider: thick track, gap around a narrow bar handle.
class Slider : Widget
{
    public string Icon;
    public float Value;
    public Action<float> Changed;
    public Action IconClick;
    bool dragging, iconDown;
    float TrackX { get { return Icon == null ? R.X : R.X + 48 * S; } }

    public override void Activate() { if (IconClick != null) IconClick(); }
    public override void Key(Keys k) { Set(Value + (k == Keys.Right ? 0.05f : -0.05f)); }
    public override void Down(Point p)
    {
        if (Icon != null && p.X < TrackX - 4 * S) { iconDown = true; return; }
        dragging = true;
        SetX(p.X);
    }
    public override void Drag(Point p) { if (dragging) SetX(p.X); }
    public override void Up(Point p, bool inside)
    {
        if (iconDown && inside && IconClick != null) IconClick();
        iconDown = dragging = false;
    }
    void SetX(float x) { Set((x - TrackX - 2 * S) / (R.Right - TrackX - 4 * S)); }
    void Set(float v)
    {
        Value = Math.Max(0, Math.Min(1, v));
        if (Changed != null) Changed(Value);
        Owner.Invalidate();
    }

    public override void Paint(Graphics g)
    {
        if (Icon != null)
        {
            var ir = new RectangleF(R.X, R.Y + (R.Height - 40 * S) / 2, 40 * S, 40 * S);
            if (hoverA > 0 && IconClick != null) using (var p = Native.Round(ir, 20 * S)) Native.Fill(g, p, Theme.A((int)(hoverA * 18)));
            g.DrawString(Icon, Theme.Icon, Theme.Text, ir, Theme.Center);
        }
        float x0 = TrackX, x1 = R.Right, cy = R.Y + R.Height / 2, th = 16 * S, gap = 6 * S, hh = 32 * S;
        float hw = 4 * S * (1 - 0.4f * pressA), hx = x0 + 2 * S + (x1 - x0 - 4 * S) * Value;
        if (hx - gap - x0 > 1)
        {
            var ar = new RectangleF(x0, cy - th / 2, hx - gap - x0, th);
            using (var p = Native.Round(ar, th / 2, 3 * S, 3 * S, th / 2)) { Native.Fill(g, p, Theme.Accent); Native.Gloss(g, p, ar, S, 0.7f); }
        }
        if (x1 - hx - gap > 1)
            using (var p = Native.Round(new RectangleF(hx + gap, cy - th / 2, x1 - hx - gap, th), 3 * S, th / 2, th / 2, 3 * S)) Native.Fill(g, p, Theme.A(45));
        // Value readout: at the far end of the track, or inside the filled part once the handle gets close.
        string num = ((int)Math.Round(Value * 100)).ToString();
        if (hx < x1 - 48 * S) g.DrawString(num, Theme.Tiny, Theme.Dim, new RectangleF(x1 - 44 * S, cy - th, 36 * S, th * 2), Theme.Right);
        else if (hx - x0 > 48 * S) g.DrawString(num, Theme.Tiny, Theme.B(Theme.OnAccent), new RectangleF(x0 + 9 * S, cy - th, 36 * S, th * 2), Theme.Left);
        using (var p = Native.Round(new RectangleF(hx - hw / 2, cy - hh / 2, hw, hh), hw / 2)) Native.Fill(g, p, Theme.Fore);
        if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) using (var p = Native.Round(new RectangleF(hx - 8 * S, cy - hh / 2 - 4 * S, 16 * S, hh + 8 * S), 8 * S)) g.DrawPath(pen, p);
    }
}

// Settings row: label + M3 switch.
class Toggle : Widget
{
    public string Text;
    public bool On;
    public Action<bool> Changed;
    float onA = -1, onVel;
    public override void Activate() { On = !On; if (Changed != null) Changed(On); Owner.Kick(); }
    public override bool Animate() { bool b = base.Animate(); return Native.Spring(ref onA, ref onVel, On ? 1 : 0, 360f, 24f) | b; }
    public override void Paint(Graphics g)
    {
        if (onA < 0) onA = On ? 1 : 0;
        float clamped = Math.Max(0, Math.Min(1, onA));
        g.DrawString(Text, Theme.Body, Theme.Text, R, Theme.Left);
        float sw = 52 * S, sh = 32 * S, sx = R.Right - sw, sy = R.Y + (R.Height - sh) / 2;
        var track = new RectangleF(sx, sy, sw, sh);
        using (var p = Native.Round(track, sh / 2))
        {
            Native.Fill(g, p, Theme.Mix(Theme.A(30), Color.FromArgb(225, Theme.Accent), clamped));
            Native.Gloss(g, p, track, S, 0.4f + 0.4f * clamped);
            using (var pen = new Pen(Theme.A((int)(140 * (1 - clamped))), 2 * S)) g.DrawPath(pen, p);
            if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawPath(pen, p);
        }
        float d = 16 * S + 8 * S * clamped + 4 * S * pressA, stretch = Math.Min(8 * S, Math.Abs(onVel) * 1.8f * S);
        float tw = d + stretch, cx = sx + 16 * S + (sw - 32 * S) * onA, cy = sy + sh / 2;
        if (hoverA > 0) using (var b = new SolidBrush(Theme.A((int)(hoverA * 28)))) g.FillEllipse(b, cx - 20 * S, cy - 20 * S, 40 * S, 40 * S);
        using (var p = Native.Round(new RectangleF(cx - tw / 2, cy - d / 2, tw, d), d / 2))
            Native.Fill(g, p, Theme.Mix(Theme.A(Theme.Light ? 150 : 190), Color.White, clamped));
    }
}

// Settings row: label + connected button group with a spring-sliding active pill.
class Segmented : Widget
{
    public string Text;
    public string[] Options;
    public int Index;
    public Action<int> Changed;
    float[] selA;
    RectangleF[] opt;
    float pillX = -1, pillW, pillXVel, pillWVel;

    void Select(int i) { if (i == Index) return; Index = i; if (Changed != null) Changed(i); Owner.Kick(); }
    public override void Key(Keys k) { Select((Index + (k == Keys.Right ? 1 : Options.Length - 1)) % Options.Length); }
    public override void Up(Point p, bool inside)
    {
        if (opt == null) return;
        for (int i = 0; i < opt.Length; i++) if (opt[i].Contains(p)) Select(i);
    }
    public override bool Animate()
    {
        bool b = base.Animate();
        if (selA != null) for (int i = 0; i < selA.Length; i++) b |= Native.Ease(ref selA[i], i == Index ? 1 : 0, 0.25f);
        if (opt != null && Index >= 0 && Index < opt.Length && opt[Index].Width > 0)
        {
            b |= Native.Spring(ref pillX, ref pillXVel, opt[Index].X, 380f, 28f, 0.15f);
            b |= Native.Spring(ref pillW, ref pillWVel, opt[Index].Width, 380f, 28f, 0.15f);
        }
        return b;
    }
    public override void Paint(Graphics g)
    {
        int n = Options.Length;
        if (selA == null) { selA = new float[n]; selA[Index] = 1; opt = new RectangleF[n]; }
        g.DrawString(Text, Theme.Body, Theme.Text, R, Theme.Left);
        float h = 36 * S, gap = 2 * S, y = R.Y + (R.Height - h) / 2, total = -gap;
        var ws = new float[n];
        for (int i = 0; i < n; i++) { ws[i] = g.MeasureString(Options[i], Theme.SmallBold, 999, Theme.Center).Width + 28 * S; total += ws[i] + gap; }
        float x = R.Right - total;
        for (int i = 0; i < n; i++)
        {
            opt[i] = new RectangleF(x, y, ws[i], h);
            float full = h / 2, inner = 6 * S;
            using (var p = Native.Round(opt[i], i == 0 ? full : inner, i == n - 1 ? full : inner, i == n - 1 ? full : inner, i == 0 ? full : inner))
            {
                Native.Fill(g, p, Theme.Surface);
                Native.Gloss(g, p, opt[i], S, 0.45f);
            }
            x += ws[i] + gap;
        }
        if (pillX < 0 && Index >= 0 && Index < n) { pillX = opt[Index].X; pillW = opt[Index].Width; }
        if (pillW > 0)
        {
            float stretch = Math.Min(10 * S, Math.Abs(pillXVel) * 0.6f * S);
            var pr = new RectangleF(pillX - stretch / 2, y, pillW + stretch, h);
            using (var p = Native.Round(pr, h / 2))
            {
                Native.Fill(g, p, Color.FromArgb(230, Theme.Accent));
                Native.Gloss(g, p, pr, S, 0.85f);
                if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawPath(pen, p);
            }
        }
        for (int i = 0; i < n; i++)
            g.DrawString(Options[i], Theme.SmallBold, Theme.B(Theme.OnFill(Math.Max(0, Math.Min(1, selA[i])))), opt[i], Theme.Center);
    }
}

// Text entry painted like everything else (a real TextBox would vanish on the per-pixel-alpha surface).
class TextField : Widget
{
    public StringBuilder Value = new StringBuilder();
    public string Placeholder, Icon;
    public bool Secret, Flat;          // Flat: no field chrome, the panel itself is the field (search)
    public Font Font = Theme.Body;
    public Action<string> Submit;
    public Action<int> Cycle;          // Up/Down
    public Action Changed;             // after every edit (live results)

    // Caret and selection anchor as character indexes; xs: each index's x on screen, recorded by Paint for clicks.
    int caret, anchor;
    float scroll;
    float[] xs = new float[0];
    DateTime lastDown;

    int SelStart { get { return Math.Min(caret, anchor); } }
    int SelLen { get { return Math.Abs(caret - anchor); } }
    int At(float x) { int best = 0; for (int i = 1; i < xs.Length; i++) if (Math.Abs(xs[i] - x) < Math.Abs(xs[best] - x)) best = i; return best; }
    void Clamp() { caret = Math.Max(0, Math.Min(caret, Value.Length)); anchor = Math.Max(0, Math.Min(anchor, Value.Length)); }

    public override void Activate() { if (Submit != null) Submit(Value.ToString()); }
    public override void Down(Point p)
    {
        Owner.FocusOn(this);
        Clamp();
        if ((DateTime.Now - lastDown).TotalMilliseconds < SystemInformation.DoubleClickTime) { anchor = 0; caret = Value.Length; } // double-click: all
        else { caret = At(p.X); if ((Control.ModifierKeys & Keys.Shift) == 0) anchor = caret; }
        lastDown = DateTime.Now;
        Owner.Invalidate();
    }
    public override void Drag(Point p) { caret = At(p.X); }
    public override void Up(Point p, bool inside) { }

    // Replaces the selection (or inserts at the caret) with s.
    void Put(string s)
    {
        Clamp();
        int st = SelStart;
        Value.Remove(st, SelLen).Insert(st, s);
        caret = anchor = st + s.Length;
        Owner.Invalidate();
        if (Changed != null) Changed();
    }
    public void Type(char c) { Put(c.ToString()); }
    public void Back() { Clamp(); if (SelLen == 0 && caret > 0) anchor = caret - 1; Put(""); }
    public void Paste()
    {
        try { if (Clipboard.ContainsText()) Put(Clipboard.GetText().Replace("\r", "").Replace("\n", " ")); }
        catch (ExternalException) { } // clipboard busy
    }
    void Copy() { if (SelLen == 0 || Secret) return; try { Clipboard.SetText(Value.ToString(SelStart, SelLen)); } catch (ExternalException) { } }

    // Editing keys; false when the key is not one of them.
    public bool Edit(Keys k)
    {
        Clamp();
        bool shift = (k & Keys.Shift) != 0, ctrl = (k & Keys.Control) != 0;
        int to = -1;
        switch (k & Keys.KeyCode)
        {
            case Keys.A: if (!ctrl || shift) return false; anchor = 0; caret = Value.Length; break;
            case Keys.C: if (!ctrl || shift) return false; Copy(); return true;
            case Keys.X: if (!ctrl || shift) return false; if (Secret) return true; Copy(); Put(""); return true;
            case Keys.V: if (!ctrl || shift) return false; Paste(); return true;
            case Keys.Insert: if (shift) Paste(); else if (ctrl) Copy(); return true;
            case Keys.Back: if (ctrl && SelLen == 0) anchor = Word(caret, -1); Back(); return true;
            case Keys.Delete: if (SelLen == 0) anchor = ctrl ? Word(caret, 1) : Math.Min(caret + 1, Value.Length); Put(""); return true;
            case Keys.Left: to = SelLen > 0 && !shift ? SelStart : ctrl ? Word(caret, -1) : Math.Max(caret - 1, 0); break;
            case Keys.Right: to = SelLen > 0 && !shift ? SelStart + SelLen : ctrl ? Word(caret, 1) : Math.Min(caret + 1, Value.Length); break;
            case Keys.Home: to = 0; break;
            case Keys.End: to = Value.Length; break;
            default: return false;
        }
        if (to >= 0) { caret = to; if (!shift) anchor = caret; }
        Owner.Invalidate();
        return true;
    }

    // Next word boundary from i in direction d (Ctrl+arrows, Ctrl+Backspace).
    int Word(int i, int d)
    {
        string s = Value.ToString();
        if (d < 0) { while (i > 0 && s[i - 1] == ' ') i--; while (i > 0 && s[i - 1] != ' ') i--; }
        else { while (i < s.Length && s[i] != ' ') i++; while (i < s.Length && s[i] == ' ') i++; }
        return i;
    }

    public override void Paint(Graphics g)
    {
        var f = new RectangleF(R.X, R.Y + 5 * S, R.Width, R.Height - 10 * S);
        if (!Flat)
            using (var p = Native.Round(f, f.Height / 2))
            {
                Native.Fill(g, p, Theme.A(Theme.Light ? 14 : 22));
                Native.Gloss(g, p, f, S, 0.4f);
                using (var pen = new Pen(Focused ? Theme.Accent : Theme.A(50), (Focused ? 2 : 1) * S)) g.DrawPath(pen, p);
            }
        float x = f.X + (Flat ? 8 : 18) * S;
        if (Icon != null) { g.DrawString(Icon, Flat ? Theme.IconBig : Theme.Icon, Flat ? Theme.Text : Theme.Dim, new RectangleF(x, f.Y, 30 * S, f.Height), Theme.Left); x += (Flat ? 42 : 32) * S; }
        var tr = new RectangleF(x, f.Y, f.Right - x - 18 * S, f.Height);
        string text = Secret ? new string('\u2022', Value.Length) : Value.ToString();
        if (text.Length == 0) { xs = new[] { tr.X }; caret = anchor = 0; g.DrawString(Placeholder ?? "", Font, Theme.Dim, tr, Theme.Left); if (Focused) Caret(g, tr.X - 3 * S, f); return; }
        Clamp();
        var w = new float[text.Length + 1]; // x of each index relative to the text start
        for (int i = 1; i <= text.Length; i++) w[i] = g.MeasureString(text.Substring(0, i), Font, 99999, Theme.Left).Width;
        // scroll so the caret stays in view (long input)
        if (w[caret] - scroll > tr.Width) scroll = w[caret] - tr.Width;
        if (w[caret] < scroll) scroll = w[caret];
        scroll = Math.Max(0, Math.Min(scroll, w[text.Length] - tr.Width));
        xs = new float[w.Length];
        for (int i = 0; i < w.Length; i++) xs[i] = tr.X + w[i] - scroll;
        var clip = g.Clip;
        g.SetClip(new RectangleF(tr.X - 3 * S, f.Y, tr.Width + 6 * S, f.Height));
        if (Focused && SelLen > 0) using (var b = new SolidBrush(Color.FromArgb(110, Theme.Accent))) g.FillRectangle(b, xs[SelStart] + 2 * S, f.Y + f.Height * 0.2f, xs[SelStart + SelLen] - xs[SelStart], f.Height * 0.6f);
        g.DrawString(text, Font, Theme.Text, new RectangleF(tr.X - scroll, tr.Y, w[text.Length] + 20 * S, tr.Height), Theme.Left);
        if (Focused) Caret(g, xs[caret], f);
        g.Clip = clip;
    }

    void Caret(Graphics g, float x, RectangleF f)
    {
        using (var pen = new Pen(Theme.Accent, 2 * S)) g.DrawLine(pen, x + S, f.Y + f.Height * 0.25f, x + S, f.Bottom - f.Height * 0.25f);
    }
}

// Settings row: label + colour swatches, a "+" for any colour, and "Windows" to follow the system accent again.
class SwatchRow : Widget
{
    public static readonly Color[] Presets =
    {
        Color.FromArgb(0, 120, 212), Color.FromArgb(0, 153, 153), Color.FromArgb(16, 137, 62), Color.FromArgb(136, 23, 152),
        Color.FromArgb(227, 0, 140), Color.FromArgb(232, 17, 35), Color.FromArgb(247, 99, 12), Color.FromArgb(255, 185, 0),
    };
    public string Text;
    public Color Value;              // Empty = Windows accent
    public Action<Color> Changed;
    RectangleF[] dots = new RectangleF[0];
    RectangleF plus, reset;

    public override void Up(Point p, bool inside)
    {
        if (!inside) return;
        if (reset.Contains(p)) Set(Color.Empty);
        else if (plus.Contains(p))
            using (var d = new ColorDialog { FullOpen = true, Color = Value.IsEmpty ? Theme.Accent : Value })
                if (d.ShowDialog(Owner) == DialogResult.OK) Set(Color.FromArgb(255, d.Color));
        for (int i = 0; i < dots.Length; i++) if (dots[i].Contains(p)) Set(Presets[i]);
    }

    void Set(Color c) { Value = c; if (Changed != null) Changed(c); Owner.Invalidate(); }

    public override void Paint(Graphics g)
    {
        g.DrawString(Text, Theme.Body, Theme.Text, R, Theme.Left);
        float d = 24 * S, gap = 7 * S, cy = R.Y + R.Height / 2;
        float rw = g.MeasureString("Windows", Theme.SmallBold, 999, Theme.Center).Width + 24 * S;
        reset = new RectangleF(R.Right - rw, cy - 15 * S, rw, 30 * S);
        using (var p = Native.Round(reset, 15 * S)) Native.Fill(g, p, Value.IsEmpty ? Theme.Accent : Theme.Surface);
        g.DrawString("Windows", Theme.SmallBold, Value.IsEmpty ? Theme.B(Theme.OnAccent) : Theme.Text, reset, Theme.Center);
        float x = reset.X - gap - d;
        plus = new RectangleF(x, cy - d / 2, d, d);
        bool custom = !Value.IsEmpty && Array.IndexOf(Presets, Value) < 0;
        g.FillEllipse(custom ? Theme.B(Value) : Theme.B(Theme.Surface), plus);
        if (custom) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawEllipse(pen, plus.X - 3 * S, plus.Y - 3 * S, d + 6 * S, d + 6 * S);
        else g.DrawString(G.Add, Theme.IconSmall, Theme.Text, plus, Theme.Center);
        dots = new RectangleF[Presets.Length];
        for (int i = Presets.Length - 1; i >= 0; i--)
        {
            x -= gap + d;
            dots[i] = new RectangleF(x, cy - d / 2, d, d);
            g.FillEllipse(Theme.B(Presets[i]), dots[i]);
            if (Value == Presets[i]) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawEllipse(pen, x - 3 * S, cy - d / 2 - 3 * S, d + 6 * S, d + 6 * S);
        }
    }
}

// Settings row: label + current value; click lists the options in a menu.
// Style picker: one tile per style, each a small live preview of that look, drawn by the same code as the real
// windows. Click (or arrow keys) to choose.
class StyleGrid : Widget
{
    public int Index;
    public Action<int> Changed;
    RectangleF[] tiles;
    const int Cols = 5;
    Bitmap cache;
    string cacheKey;

    void Select(int i) { if (i == Index) return; Index = i; if (Changed != null) Changed(i); Owner.Kick(); }
    public override void Key(Keys k) { int n = Glass.StyleNames.Length; Select((Index + (k == Keys.Right ? 1 : k == Keys.Left ? n - 1 : 0)) % n); }
    public override void Up(Point p, bool inside) { if (tiles != null) for (int i = 0; i < tiles.Length; i++) if (tiles[i].Contains(p)) Select(i); }

    public static int Rows { get { return (Glass.StyleNames.Length + Cols - 1) / Cols; } }

    public override void Paint(Graphics g)
    {
        string key = Index + "|" + Theme.Light + "|" + Theme.Accent.ToArgb() + "|" + Glass.Style + "|" + R;
        if (cache == null || key != cacheKey) // nine little previews are costly to draw every frame
        {
            if (cache != null) cache.Dispose();
            cache = new Bitmap((int)Math.Ceiling(R.Width), (int)Math.Ceiling(R.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            cache.SetResolution(g.DpiX, g.DpiY);
            cacheKey = key;
            using (var cg = Graphics.FromImage(cache))
            {
                cg.SmoothingMode = SmoothingMode.AntiAlias;
                cg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                cg.TranslateTransform(-R.X, -R.Y);
                PaintTiles(cg);
            }
        }
        g.DrawImageUnscaled(cache, (int)R.X, (int)R.Y);
    }

    void PaintTiles(Graphics g)
    {
        int n = Glass.StyleNames.Length;
        if (tiles == null) tiles = new RectangleF[n];
        float gap = 12 * S, label = 24 * S, tw = (R.Width - gap * (Cols - 1)) / Cols, th = (R.Height - gap * (Rows - 1)) / Rows;
        for (int i = 0; i < n; i++)
        {
            var t = new RectangleF(R.X + (i % Cols) * (tw + gap), R.Y + (i / Cols) * (th + gap), tw, th);
            tiles[i] = t;
            var prev = new RectangleF(t.X + 3 * S, t.Y + 3 * S, t.Width - 6 * S, t.Height - label - 6 * S);
            Preview(g, prev, i);
            if (i == Index) using (var sel = Native.Round(new RectangleF(t.X, t.Y, t.Width, t.Height - label), 14 * S)) using (var pen = new Pen(Theme.Accent, 2.5f * S)) g.DrawPath(pen, sel);
            g.DrawString(Glass.StyleNames[i], i == Index ? Theme.SmallBold : Theme.Small, i == Index ? Theme.Text : Theme.Dim, new RectangleF(t.X, t.Bottom - label, t.Width, label), Theme.Center);
        }
    }

    // A tiny window in that style: a wallpaper behind the see-through ones, the card, an accent dot and two lines of "text".
    void Preview(Graphics g, RectangleF r, int style)
    {
        bool light = Theme.Light;
        Color fore, bas, surface;
        Theme.Palette(style, light, Theme.Accent, out fore, out bas, out surface);
        float rad = Glass.CornerFor(style, 24) * S * 0.45f;
        var st = g.Save();
        using (var clip = Native.Round(r, rad + 2 * S)) g.SetClip(clip, CombineMode.Intersect);
        if (style == 2 || style == 4) // a blurry stand-in wallpaper so the material reads
        {
            using (var wp = new LinearGradientBrush(r, Color.FromArgb(32, 110, 120), Color.FromArgb(70, 40, 110), LinearGradientMode.ForwardDiagonal)) g.FillRectangle(wp, r);
            using (var veil = new SolidBrush(style == 2 ? (light ? Color.FromArgb(150, 243, 243, 243) : Color.FromArgb(150, 30, 30, 32)) : Color.FromArgb(light ? 40 : 30, 0, 0, 0))) g.FillRectangle(veil, r);
        }
        g.Restore(st);
        Glass.PaintCard(g, r, rad, style, light, bas, Theme.Accent, S * 0.6f);
        Glass.PaintEdge(g, r, rad, style, light, fore, S * 0.6f);
        // Contents scale with the tile, so they always fit inside it.
        float u = r.Height / 100, x = r.X + 14 * u, dotD = 18 * u, lineH = 7 * u;
        using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, x, r.Y + 16 * u, dotD, dotD);
        using (var line = new SolidBrush(Color.FromArgb(200, fore))) using (var p = Native.Round(new RectangleF(x, r.Y + 50 * u, r.Width * 0.55f, lineH), lineH / 2)) g.FillPath(line, p);
        using (var line = new SolidBrush(Color.FromArgb(110, fore))) using (var p = Native.Round(new RectangleF(x, r.Y + 66 * u, r.Width * 0.38f, lineH), lineH / 2)) g.FillPath(line, p);
        using (var chip = new SolidBrush(surface)) using (var p = Native.Round(new RectangleF(r.Right - 44 * u, r.Y + 16 * u, 30 * u, dotD), dotD / 2)) g.FillPath(chip, p);
    }
}

class ChoiceRow : Widget
{
    public string Text;
    public string[] Options;
    public int Index;
    public Action<int> Changed;
    RectangleF pill;

    public override void Activate()
    {
        var m = new MenuPopup(S, null);
        for (int i = 0; i < Options.Length; i++)
        {
            int idx = i;
            m.Item(i == Index ? G.Check : "", Options[i], () => { Index = idx; if (Changed != null) Changed(idx); Owner.Invalidate(); });
        }
        m.Open(Owner.PointToScreen(new Point((int)pill.X, (int)pill.Bottom)));
    }

    public override void Paint(Graphics g)
    {
        g.DrawString(Text, Theme.Body, Theme.Text, R, Theme.Left);
        string v = Options[Math.Max(0, Math.Min(Index, Options.Length - 1))];
        float w = g.MeasureString(v, Theme.SmallBold, 999, Theme.Left).Width + 54 * S, h = 36 * S;
        pill = new RectangleF(R.Right - w, R.Y + (R.Height - h) / 2, w, h);
        using (var p = Native.Round(pill, Morph(h / 2)))
        {
            Native.Fill(g, p, Theme.Surface);
            Native.Gloss(g, p, pill, S, 0.45f);
            Overlay(g, p);
        }
        g.DrawString(v, Theme.SmallBold, Theme.Text, new RectangleF(pill.X + 16 * S, pill.Y, w - 40 * S, h), Theme.Left);
        g.DrawString(G.Down, Theme.IconSmall, Theme.Text, new RectangleF(pill.Right - 28 * S, pill.Y, 14 * S, h), Theme.Left);
    }
}

// Compact menu in the same glass style (right-click menus, pickers). Runs the action after closing.
class MenuPopup : Glass
{
    readonly List<MenuRow> rows = new List<MenuRow>();
    readonly string title;

    public MenuPopup(float s, string title) : base(s) { this.title = title; Corner = 12; }

    public void Item(string icon, string text, Action a) { Item(icon, text, null, a); }
    public void Item(string icon, string text, string sub, Action a) { rows.Add(new MenuRow { Icon = icon, Text = text, Sub = sub, Compact = true, Click = () => { Close(); a(); } }); }

    // A round icon button at the end of the last added row (e.g. mirror a phone).
    readonly Dictionary<MenuRow, List<IconBtn>> btns = new Dictionary<MenuRow, List<IconBtn>>();
    public void Button(string icon, string tip, Action a) { Button(icon, tip, a, false); }
    public IconBtn Button(string icon, string tip, Action a, bool keepOpen) // keepOpen: e.g. media controls
    {
        var r = rows[rows.Count - 1];
        if (!btns.ContainsKey(r)) btns[r] = new List<IconBtn>();
        var b = new IconBtn { Icon = icon, Tip = tip, Click = () => { if (!keepOpen) Close(); a(); } };
        btns[r].Add(b);
        return b;
    }

    public void Picture(Image img) { rows[rows.Count - 1].Img = img; } // the last row's picture in place of its glyph (album art)

    public void ClearRows() { rows.Clear(); btns.Clear(); ClearItems(); }

    // Sized to its longest label, tight padding and short rows.
    public MenuPopup LayoutMenu()
    {
        ClearItems();
        float pad = 5 * S, rh = 34 * S, y = pad, tw = 0, bs = 34 * S, gap = 4 * S;
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
        {
            foreach (var r in rows)
            {
                float lw = Math.Max(g.MeasureString(r.Text, r.Sub != null ? Theme.Body : Theme.Menu, 999, Theme.Left).Width, r.Sub != null ? g.MeasureString(r.Sub, Theme.Small, 999, Theme.Left).Width : 0);
                List<IconBtn> b;
                if (btns.TryGetValue(r, out b)) lw += b.Count * (bs + gap) + gap;
                tw = Math.Max(tw, lw);
            }
            if (title != null) tw = Math.Max(tw, g.MeasureString(title, Theme.Small, 999, Theme.Left).Width - 26 * S);
        }
        float W = Math.Max(150 * S, tw + 36 * S + 24 * S + 2 * pad);
        if (title != null) { Add(new Caption { Text = title, R = new RectangleF(pad + 10 * S, y, W - 2 * pad - 10 * S, 24 * S) }); y += 24 * S; }
        foreach (var r in rows)
        {
            float h = r.Sub != null ? 46 * S : rh, rw = W - 2 * pad;
            List<IconBtn> b;
            if (btns.TryGetValue(r, out b))
            {
                rw -= b.Count * (bs + gap);
                for (int i = 0; i < b.Count; i++) { b[i].R = new RectangleF(pad + rw + gap + i * (bs + gap), y + (h - bs) / 2, bs, bs); Add(b[i]); }
            }
            r.R = new RectangleF(pad, y, rw, h);
            Add(r);
            y += h;
        }
        int newW = (int)W, newH = (int)(y + pad);
        if (AnchorRect.Width > 0) { Place(newW, newH); Invalidate(); }
        else Size = new Size(newW, newH);
        return this;
    }

    public void Open(Point at)
    {
        LayoutMenu();
        ShowAt(new Rectangle(at, new Size(1, 1)));
    }
}

// A menu card for hover tiles (shown with ShowQuiet).
class HintTile : MenuPopup
{
    public HintTile(float s, string title) : base(s, title) { }
}
