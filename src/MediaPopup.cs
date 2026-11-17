using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Expanded "live activity" for the media pill, styled like every other panel; the play button morphs
// between a squircle and a circle (M3 Expressive).
class MediaPopup : Glass
{
    readonly Timer tick = new Timer();
    readonly Action onMedia;
    readonly PlayBtn play;
    readonly Caption title, artist, app;

    public MediaPopup(float s) : base(s)
    {
        float W = 400 * S, p = 20 * S, art = 72 * S;
        Add(new ArtView { R = new RectangleF(p, p, art, art) });
        float tx = p + art + 16 * S, tw = W - tx - p - 80 * S;
        app = Add(new Caption { Font = Theme.Small, R = new RectangleF(tx, p, tw, 20 * S) });
        title = Add(new Caption { Font = Theme.Heading, Strong = true, R = new RectangleF(tx, p + 20 * S, tw, 30 * S) });
        artist = Add(new Caption { Font = Theme.Body, R = new RectangleF(tx, p + 50 * S, tw, 22 * S) });
        play = Add(new PlayBtn { R = new RectangleF(W - p - 64 * S, p + 4 * S, 64 * S, 64 * S) });
        float by = p + art + 14 * S;
        Add(new Ghost { Icon = G.Prev, R = new RectangleF(10 * S, by, 40 * S, 40 * S), Click = Media.Prev });
        Add(new Wave { R = new RectangleF(58 * S, by, W - 116 * S, 56 * S) });
        Add(new Ghost { Icon = G.Next, R = new RectangleF(W - 50 * S, by, 40 * S, 40 * S), Click = Media.Next });
        Size = new Size((int)W, (int)(by + 56 * S + 6 * S));
        tick.Interval = 33; // only while open and playing: moves the wave and the clock
        tick.Tick += (a, b) => Invalidate();
        onMedia = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Sync); } catch (InvalidOperationException) { } };
        Media.Changed += onMedia;
        FormClosed += (a, b) => { Media.Changed -= onMedia; tick.Dispose(); };
        Sync();
    }

    void Sync()
    {
        if (!Media.Has) { if (IsHandleCreated) Close(); return; } // closing inside the constructor would make the click do nothing
        app.Text = Media.App;
        title.Text = Media.Title;
        artist.Text = Media.Artist;
        play.On = Media.Playing;
        tick.Enabled = Media.Playing;
        Kick();
    }
}

class ArtView : Widget
{
    public override bool Interactive { get { return false; } }
    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, 20 * S)) Cover(g, p, R, Media.Art);
        if (Media.Art == null) g.DrawString(G.Music, Theme.IconBig, Theme.Text, R, Theme.Center);
    }

    public static void Cover(Graphics g, GraphicsPath p, RectangleF r, Image img)
    {
        if (img == null) { Native.Fill(g, p, Theme.Surface); return; }
        using (var tb = new TextureBrush(img))
        {
            tb.TranslateTransform(r.X, r.Y);
            tb.ScaleTransform(r.Width / img.Width, r.Height / img.Height);
            g.FillPath(tb, p);
        }
    }
}

// Icon-only control for the card (no surface behind it).
class Ghost : Widget
{
    public string Icon;
    public Action Click;
    public override void Activate() { if (Click != null) Click(); }
    public override void Paint(Graphics g)
    {
        int a = (int)(hoverA * 34 + pressA * 30);
        if (a > 0) g.FillEllipse(Theme.B(Theme.A(a)), R);
        if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawEllipse(pen, R);
        g.DrawString(Icon, Theme.Icon, Theme.Text, R, Theme.Center);
    }
}

// M3 Expressive wavy progress: the played part ripples while playing and flattens when paused. Click to seek.
class Wave : Widget
{
    float amp = -1;
    public override bool Animate() { bool b = base.Animate(); return Native.Ease(ref amp, Media.Playing ? 1 : 0, 0.15f) | b; }
    public override void Up(Point p, bool inside)
    {
        double dur = Media.Duration.TotalSeconds;
        if (inside && dur > 0) Media.Seek(Math.Max(0, Math.Min(1, (p.X - R.X) / R.Width)) * dur);
    }
    public override void Paint(Graphics g)
    {
        if (amp < 0) amp = Media.Playing ? 1 : 0;
        double dur = Media.Duration.TotalSeconds, pos = Media.Now.TotalSeconds;
        float f = dur > 0 ? (float)Math.Min(1, pos / dur) : 0;
        float x0 = R.X + 4 * S, x1 = R.Right - 4 * S, cy = R.Y + 20 * S, hx = x0 + (x1 - x0) * f, a = 3.5f * S * amp, phase = Environment.TickCount / 140f;
        var tone = Theme.Accent;
        if (hx - x0 > 6 * S)
        {
            var pts = new List<PointF>();
            for (float x = x0; x <= hx - 6 * S; x += 2 * S) pts.Add(new PointF(x, cy + a * (float)Math.Sin((x - x0) / (5 * S) - phase)));
            if (pts.Count > 1) using (var pen = new Pen(tone, 4 * S) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLines(pen, pts.ToArray());
        }
        if (x1 - hx > 6 * S) using (var pen = new Pen(Theme.A(60), 4 * S) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, hx + 6 * S, cy, x1, cy);
        using (var p = Native.Round(new RectangleF(hx - 2.5f * S, cy - 11 * S, 5 * S, 22 * S), 2.5f * S)) Native.Fill(g, p, Theme.Fore);
        if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) using (var p = Native.Round(new RectangleF(hx - 9 * S, cy - 15 * S, 18 * S, 30 * S), 9 * S)) g.DrawPath(pen, p);
        if (dur <= 0) return;
        var tr = new RectangleF(x0, cy + 14 * S, x1 - x0, R.Bottom - cy - 14 * S);
        g.DrawString(Fmt(pos), Theme.Small, Theme.Dim, tr, Theme.Left);
        g.DrawString(Fmt(dur), Theme.Small, Theme.Dim, tr, Theme.Right);
    }
    public override void Key(Keys k)
    {
        double dur = Media.Duration.TotalSeconds;
        if (dur > 0) Media.Seek(Math.Max(0, Math.Min(dur, Media.Now.TotalSeconds + (k == Keys.Right ? 10 : -10))));
    }
    static string Fmt(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }
}

// Squircle when paused, a full circle while playing (M3 Expressive shape morph), in the accent colour.
class PlayBtn : Widget
{
    public bool On;
    float onA = -1;
    public override void Activate() { Media.Toggle(); On = Media.Playing; Owner.Kick(); }
    public override bool Animate() { bool b = base.Animate(); return Native.Ease(ref onA, On ? 1 : 0, 0.2f) | b; }
    public override void Paint(Graphics g)
    {
        if (onA < 0) onA = On ? 1 : 0;
        float rad = 20 * S + (R.Height / 2 - 20 * S) * onA;
        var tone = Theme.Accent;
        using (var p = Native.Round(R, rad - (rad - 10 * S) * pressA))
        {
            Native.Fill(g, p, tone);
            Native.Gloss(g, p, R, S, 0.9f);
            int a = (int)(hoverA * 30 + pressA * 30);
            if (a > 0) Native.Fill(g, p, Color.FromArgb(a, 255, 255, 255));
            if (Focused) using (var pen = new Pen(Theme.Fore, 2 * S)) g.DrawPath(pen, p);
        }
        g.DrawString(On ? G.Pause : G.Play, Theme.IconBig, Theme.B(Theme.OnAccent), R, Theme.Center);
    }
}
