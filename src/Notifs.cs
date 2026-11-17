using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

// Side panel from the clock: large time and date, Do not disturb, and the notifications in Action Center.
class NotifPopup : Glass
{
    const int MaxShown = 6; // shortcut: no scrolling; "+N more" opens Windows' notification center
    readonly TopBar bar;
    List<Sys.Note> notes;

    public NotifPopup(TopBar bar, float s) : base(s) { this.bar = bar; Build(); }

    void Ui(Action a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { } }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ThreadPool.QueueUserWorkItem(_ => { var n = Sys.Notes(); Ui(() => { notes = n; Build(); }); });
    }

    void Build()
    {
        ClearItems();
        float W = 380 * S, pad = 16 * S, x = pad, w = W - 2 * pad, y = pad;
        var now = DateTime.Now;
        Add(new Caption { Text = now.ToString(bar.Cfg.Clock24 ? "HH:mm" : "h:mm"), Font = Theme.Huge, Strong = true, R = new RectangleF(x + 4 * S, y, w, 58 * S) });
        y += 58 * S;
        Add(new Caption { Text = now.ToString("dddd, MMMM d"), Font = Theme.Body, R = new RectangleF(x + 6 * S, y, w, 24 * S) });
        y += 36 * S;

        Add(new Segment { R = new RectangleF(x, y, w, 56 * S) });
        Add(new MenuRow { Icon = G.Sleep, Text = "Do not disturb", Sub = Sys.FocusActive ? "Focus on" : "Switch it in Windows' notification centre", Compact = true, Tip = "Windows only lets its own apps switch this", R = new RectangleF(x + 16 * S, y + 4 * S, w - 32 * S, 48 * S), Click = () => { Close(); Native.Win(System.Windows.Forms.Keys.N); } });
        y += 56 * S + 16 * S;

        Add(new Caption { Text = "Notifications", R = new RectangleF(x + 6 * S, y, w - 60 * S, 28 * S) });
        if (notes != null && notes.Count > 0)
            Add(new IconBtn { Icon = G.Delete, Tip = "Clear all", R = new RectangleF(x + w - 32 * S, y - 2 * S, 32 * S, 32 * S), Click = () => { Sys.ClearNotes(notes); notes.Clear(); Build(); } });
        y += 34 * S;

        if (notes == null || notes.Count == 0)
        {
            Add(new Segment { R = new RectangleF(x, y, w, 64 * S) });
            Add(new Caption { Text = notes == null ? "Loading…" : "No new notifications", R = new RectangleF(x + 20 * S, y, w - 40 * S, 64 * S) });
            y += 64 * S;
        }
        else
        {
            int n = Math.Min(notes.Count, MaxShown);
            float rh = 72 * S;
            Add(new Segment { R = new RectangleF(x, y, w, n * rh + 8 * S) });
            y += 4 * S;
            for (int i = 0; i < n; i++)
            {
                var note = notes[i];
                Add(new NoteRow { N = note, R = new RectangleF(x + 4 * S, y, w - 8 * S, rh), Dismiss = () => { Sys.Dismiss(note.Id); notes.Remove(note); Build(); } });
                y += rh;
            }
            y += 4 * S;
            if (notes.Count > MaxShown)
            {
                y += 8 * S;
                Add(new MenuRow { Icon = G.Open, Text = "+" + (notes.Count - MaxShown) + " more in Windows", Compact = true, R = new RectangleF(x, y, w, 36 * S), Click = () => { Close(); Native.Win(Keys.N); } });
                y += 36 * S;
            }
        }
        y += pad;
        Place((int)W, (int)y);
        Invalidate();
    }
}

// One notification: app and time, title, first line of the body. Click opens the app that sent it
// (Sys.OpenNote); X dismisses it.
class NoteRow : Widget
{
    public Sys.Note N;
    public Action Dismiss;
    RectangleF X { get { return new RectangleF(R.Right - 38 * S, R.Y + 6 * S, 30 * S, 30 * S); } }

    public override void Up(Point p, bool inside)
    {
        if (!inside) return;
        if (X.Contains(p)) Dismiss(); else Activate();
    }
    public override void Activate() { Owner.Close(); Sys.OpenNote(N); }
    public override void Key(Keys k) { if (k == Keys.Delete) Dismiss(); }

    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph(16 * S)))
        {
            if (hoverA > 0) Native.Fill(g, p, Theme.A((int)(hoverA * 22)));
            Overlay(g, p);
        }
        float x = R.X + 14 * S, w = R.Width - 28 * S - 30 * S;
        var age = DateTime.Now - N.When;
        string when = age.TotalMinutes < 1 ? "now" : age.TotalHours < 1 ? (int)age.TotalMinutes + " min" : age.TotalDays < 1 ? N.When.ToString("t") : N.When.ToString("MMM d");
        g.DrawString(N.App + "  ·  " + when, Theme.Small, Theme.Dim, new RectangleF(x, R.Y + 8 * S, w, 18 * S), Theme.Left);
        g.DrawString(N.Title, Theme.SmallBold, Theme.Text, new RectangleF(x, R.Y + 27 * S, w, 20 * S), Theme.Left);
        g.DrawString(N.Body, Theme.Small, Theme.Dim, new RectangleF(x, R.Y + 47 * S, w, 18 * S), Theme.Left);
        if (hoverA > 0) g.DrawString(G.Close, Theme.IconSmall, Theme.B(Theme.A((int)(hoverA * 220))), X, Theme.Center);
    }
}
