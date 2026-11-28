using System;
using System.Drawing;
using System.Windows.Forms;

// Our volume / brightness pop-up at the bottom centre of the screen. Compact: a ring, a bar or an Android-style
// vertical pill. The volume one has a button that expands it into a small mixer (the speakers plus each app's own
// level), which stays open while the pointer is on it.
class VolumeOsd : Glass
{
    public static readonly string[] Styles = { "Ring", "Bar", "Vertical" };
    static readonly Font BigIcon = new Font("Segoe Fluent Icons", 24f);
    const string More = "", Less = "";
    float level, shown = -1;
    bool muted, bright, expanded;
    int style;
    Slider master;
    readonly Timer hide = new Timer { Interval = 1500 };

    public VolumeOsd(float s) : base(s)
    {
        CloseOnDeactivate = false;
        hide.Tick += (a, b) =>
        {
            if (Pressing || Bounds.Contains(Cursor.Position)) return; // in use: look again on the next tick
            hide.Stop();
            Vanish(() => { Hide(); if (expanded) { expanded = false; Build(); } });
        };
        FormClosed += (a, b) => hide.Dispose();
        Build();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } } // NOACTIVATE

    // Compact size per style in logical px; the volume one also fits its expand button.
    public static Size CompactSize(int style, bool volume)
    {
        if (style == 1) return new Size(volume ? 372 : 332, 72);
        if (style == 2) return new Size(84, volume ? 324 : 276);
        return new Size(148, 148);
    }

    void Build()
    {
        ClearItems();
        master = null;
        if (!expanded)
        {
            var sz = CompactSize(style, !bright);
            Corner = style == 0 ? 28 : style == 1 ? 36 : 42;
            Size = new Size((int)(sz.Width * S), (int)(sz.Height * S));
            Add(new View { R = new RectangleF(0, 0, Width, Height) });
            if (!bright)
            {
                float b = 36 * S;
                var r = style == 1 ? new RectangleF(Width - b - 18 * S, (Height - b) / 2, b, b)
                      : style == 2 ? new RectangleF((Width - b) / 2, Height - b - 18 * S, b, b)
                      : new RectangleF(Width - b - 8 * S, 8 * S, b, b);
                Add(new IconBtn { Icon = More, Tip = "Volume for each app", R = r, Click = () => { expanded = true; Build(); Place(); } });
            }
        }
        else
        {
            Corner = 24;
            float w = 360 * S, pad = 16 * S, y = pad, iw = w - 2 * pad;
            Add(new Caption { Text = "Volume", Strong = true, Font = Theme.Bold, R = new RectangleF(pad + 4 * S, y, iw - 48 * S, 36 * S) });
            Add(new IconBtn { Icon = Less, Tip = "Collapse", R = new RectangleF(w - pad - 36 * S, y, 36 * S, 36 * S), Click = () => { expanded = false; Build(); Place(); } });
            y += 44 * S;
            var o = Audio.Out;
            if (o != null)
            {
                Add(new Caption { Text = o.Name, R = new RectangleF(pad + 4 * S, y, iw, 22 * S) });
                y += 22 * S;
                var m = master = Add(new Slider { Icon = o.Muted ? G.Mute : G.Volume(o.Volume, false), Value = o.Volume, Tip = "Click the icon to mute", R = new RectangleF(pad, y, iw, 48 * S) });
                m.Changed = v => { o.Volume = v; if (o.Muted && v > 0) o.Muted = false; level = v; muted = o.Muted; m.Icon = G.Volume(v, false); };
                m.IconClick = () => { o.Muted = !o.Muted; muted = o.Muted; m.Icon = o.Muted ? G.Mute : G.Volume(o.Volume, false); Invalidate(); };
                y += 52 * S;
            }
            var apps = AppVolume.List(6);
            if (apps.Count == 0) { Add(new Caption { Text = "No apps are playing sound", R = new RectangleF(pad + 4 * S, y, iw, 32 * S) }); y += 36 * S; }
            foreach (var a in apps) // each app's own level; the icon mutes it
            {
                var app = a;
                Add(new Caption { Text = app.Name, R = new RectangleF(pad + 4 * S, y, iw, 22 * S) });
                y += 22 * S;
                Slider s = null;
                s = Add(new Slider { Icon = app.Muted ? G.Mute : G.Volume(app.Level, false), Value = app.Level, Tip = "Click the icon to mute", R = new RectangleF(pad, y, iw, 44 * S),
                                     Changed = v => { app.Level = v; if (app.Muted && v > 0) app.Muted = false; s.Icon = G.Volume(v, false); } });
                s.IconClick = () => { app.Muted = !app.Muted; s.Icon = app.Muted ? G.Mute : G.Volume(app.Level, false); Invalidate(); };
                y += 48 * S;
            }
            Add(new MenuRow { Icon = G.Settings, Text = "Sound settings", Compact = true, R = new RectangleF(pad, y, iw, 40 * S), Click = () => Shell.Open("ms-settings:apps-volume") });
            y += 40 * S + pad;
            Size = new Size((int)w, (int)y);
        }
        if (IsHandleCreated) OnResize(EventArgs.Empty); // the corner can change without the size changing
        hide.Interval = expanded ? 4000 : 1500;
        if (Visible) { hide.Stop(); hide.Start(); }
        Invalidate();
    }

    // Where on the primary screen: bottom and top centred, left and right edges centred vertically (like Android).
    public static readonly string[] Positions = { "Bottom", "Top", "Left", "Right" };
    public static Point Spot(Rectangle wa, Size size, int pos, float s)
    {
        int cx = wa.Left + (wa.Width - size.Width) / 2, cy = wa.Top + (wa.Height - size.Height) / 2;
        if (pos == 1) return new Point(cx, wa.Top + (int)(16 * s));
        if (pos == 2) return new Point(wa.Left + (int)(24 * s), cy);
        if (pos == 3) return new Point(wa.Right - size.Width - (int)(24 * s), cy);
        return new Point(cx, wa.Bottom - size.Height - (int)(48 * s)); // grows upwards when expanded
    }

    void Place() { Location = Spot(Screen.PrimaryScreen.WorkingArea, Size, VolumeKeys.Pos, S); }

    public void Pop(float v, bool mute, bool isBrightness)
    {
        bool wasVisible = Visible;
        int st = isBrightness ? VolumeKeys.BrightStyle : VolumeKeys.VolStyle;
        if (!wasVisible || bright != isBrightness || shown < 0) shown = v;
        bool rebuild = !wasVisible || bright != isBrightness || st != style || (expanded && isBrightness);
        level = v;
        muted = mute;
        bright = isBrightness;
        style = st;
        if (isBrightness) expanded = false;
        if (rebuild) Build();
        Place();
        if (master != null) { master.Value = v; master.Icon = mute ? G.Mute : G.Volume(v, false); }
        if (!wasVisible) Show(); // the first Show plays the opening by itself
        else Appear();           // already up, or fading away: come back
        Kick();
        Invalidate();
        hide.Stop();
        hide.Start();
    }

    class View : Widget
    {
        public override bool Interactive { get { return false; } }
        public override bool Animate()
        {
            var o = (VolumeOsd)Owner;
            float s = o.shown;
            bool b = Native.Ease(ref s, !o.bright && o.muted ? 0 : o.level, 0.32f);
            o.shown = s;
            return b;
        }
        public override void Paint(Graphics g)
        {
            var o = (VolumeOsd)Owner;
            float s = S, w = o.Width, h = o.Height;
            bool off = !o.bright && o.muted;
            float val = off ? 0 : Math.Max(0, Math.Min(1, o.shown < 0 ? o.level : o.shown));
            string icon = o.bright ? G.Brightness : o.muted ? G.Mute : G.Volume(o.level, false);
            string label = off ? "Muted" : Math.Round(o.level * 100) + "%";
            var fg = off ? Theme.Dim : Theme.Text;
            if (o.style == 1) // bar: icon, track, value
            {
                float x0 = 64 * s, x1 = w - (o.bright ? 72 : 112) * s, cy = h / 2, th = 10 * s;
                g.DrawString(icon, BigIcon, fg, new RectangleF(14 * s, 0, 44 * s, h), Theme.Center);
                using (var p = Native.Round(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2)) Native.Fill(g, p, Theme.A(38));
                if (val > 0.005f) using (var p = Native.Round(new RectangleF(x0, cy - th / 2, Math.Max(th, (x1 - x0) * val), th), th / 2)) Native.Fill(g, p, Theme.Accent);
                g.DrawString(label, Theme.Bold, fg, new RectangleF(x1 + 6 * s, 0, 60 * s, h), Theme.Center);
            }
            else if (o.style == 2) // vertical pill that fills from the bottom, like Android
            {
                float tw = 48 * s, tx = (w - tw) / 2, ty = 44 * s, tb = h - (o.bright ? 16 : 66) * s, thh = tb - ty;
                g.DrawString(label, Theme.Bold, fg, new RectangleF(0, 10 * s, w, 28 * s), Theme.Center);
                using (var p = Native.Round(new RectangleF(tx, ty, tw, thh), tw / 2))
                {
                    Native.Fill(g, p, Theme.A(38));
                    if (val > 0.005f)
                    {
                        var clip = g.Clip;
                        g.SetClip(p);
                        using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, tx, tb - thh * val, tw, thh * val);
                        g.Clip = clip;
                    }
                }
                bool covered = val * thh > 36 * s;
                g.DrawString(icon, Theme.Icon, covered ? Theme.B(Theme.OnAccent) : fg, new RectangleF(tx, tb - tw, tw, tw), Theme.Center);
            }
            else // ring
            {
                float cx = w / 2f, cy = 62 * s, r = 38 * s, stroke = 7 * s;
                using (var bgPen = new Pen(Theme.A(38), stroke)) g.DrawEllipse(bgPen, cx - r, cy - r, 2 * r, 2 * r);
                if (val > 0.005f)
                    using (var fgPen = new Pen(Theme.Accent, stroke) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawArc(fgPen, cx - r, cy - r, 2 * r, 2 * r, -90f, Math.Max(4f, 360f * val));
                g.DrawString(icon, BigIcon, fg, new RectangleF(cx - r, cy - r, 2 * r, 2 * r), Theme.Center);
                g.DrawString(label, Theme.Bold, fg, new RectangleF(0, 112 * s, w, 24 * s), Theme.Center);
            }
        }
    }
}
