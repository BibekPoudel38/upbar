using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class ClipItem
{
    public string Text;
    public string[] Files;
    public byte[] Png;   // images are kept PNG-compressed; a full screenshot as a bitmap would be ~15 MB
    public Image Thumb;
    public Size ImgSize;
    public bool Pinned;
    public DateTime When = DateTime.Now;
    public object WinItem; // set when the entry comes from Windows clipboard history
    public bool PinCopy;   // a saved pin shown at the top of the list

    public string Key
    {
        get
        {
            if (Text != null) return "t:" + Text;
            if (Files != null) return "f:" + string.Join("|", Files);
            long h = Png.Length;
            for (int i = 0; i < Png.Length; i += 997) h = h * 31 + Png[i];
            return "i:" + h;
        }
    }
}

// Clipboard history, fed by WM_CLIPBOARDUPDATE (no polling). In memory only: never written to disk,
// gone when the bar quits, and it skips anything apps (e.g. password managers) mark as private.
static class Clip
{
    public static readonly List<ClipItem> Items = new List<ClipItem>();
    public static event Action Changed;
    const int MaxUnpinned = 12, MaxImages = 3;

    // false = the clipboard was busy; the caller retries shortly.
    public static bool Capture()
    {
        if (Sys.WinHistoryOn) return true; // Windows keeps the history; we only fill in when it is turned off
        IDataObject d;
        try { d = Clipboard.GetDataObject(); }
        catch (ExternalException) { return false; }
        if (d == null) return true;
        try
        {
            if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || d.GetDataPresent("Clipboard Viewer Ignore")) return true;
            var allow = d.GetData("CanIncludeInClipboardHistory") as MemoryStream;
            if (allow != null && allow.Length >= 4) { var b = new byte[4]; allow.Read(b, 0, 4); if (BitConverter.ToInt32(b, 0) == 0) return true; }

            ClipItem it = null;
            if (d.GetDataPresent(DataFormats.FileDrop))
            {
                var f = d.GetData(DataFormats.FileDrop) as string[];
                if (f != null && f.Length > 0) it = new ClipItem { Files = f };
            }
            else if (d.GetDataPresent(DataFormats.UnicodeText))
            {
                var t = d.GetData(DataFormats.UnicodeText) as string;
                if (!string.IsNullOrWhiteSpace(t)) it = new ClipItem { Text = t };
            }
            else if (d.GetDataPresent(DataFormats.Bitmap))
            {
                var img = d.GetData(DataFormats.Bitmap) as Image;
                if (img != null) using (img) it = FromImage(img);
            }
            if (it == null) return true;
            Merge(Items, it);
        }
        catch (ExternalException) { return false; }
        var c = Changed;
        if (c != null) c();
        return true;
    }

    static ClipItem FromImage(Image img)
    {
        var it = new ClipItem { ImgSize = img.Size };
        using (var ms = new MemoryStream()) { img.Save(ms, ImageFormat.Png); it.Png = ms.ToArray(); }
        float k = 96f / Math.Max(img.Width, img.Height);
        var th = new Bitmap(Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
        using (var g = Graphics.FromImage(th)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, th.Width, th.Height); }
        it.Thumb = th;
        return it;
    }

    // Newest first; a repeat copy moves the old entry to the top (keeping its pin). Trims old unpinned items.
    public static void Merge(List<ClipItem> list, ClipItem it)
    {
        string key = it.Key;
        int i = list.FindIndex(x => x.Key == key);
        if (i >= 0) { it.Pinned = list[i].Pinned; Drop(list, i); }
        list.Insert(0, it);
        int unpinned = 0, images = 0;
        for (int j = 0; j < list.Count; j++)
        {
            if (list[j].Pinned) continue;
            bool img = list[j].Png != null;
            if (++unpinned > MaxUnpinned || (img && ++images > MaxImages)) { Drop(list, j); j--; unpinned--; }
        }
    }

    static void Drop(List<ClipItem> list, int i)
    {
        if (list[i].Thumb != null) list[i].Thumb.Dispose();
        list.RemoveAt(i);
    }

    public static void Restore(ClipItem it)
    {
        try
        {
            if (it.Text != null) Clipboard.SetText(it.Text, TextDataFormat.UnicodeText);
            else if (it.Files != null) { var l = new StringCollection(); l.AddRange(it.Files); Clipboard.SetFileDropList(l); }
            else using (var ms = new MemoryStream(it.Png)) using (var img = Image.FromStream(ms)) Clipboard.SetImage(img);
        }
        catch (ExternalException) { } // clipboard busy
    }

    public static void Remove(ClipItem it) { int i = Items.IndexOf(it); if (i >= 0) Drop(Items, i); Raise(); }
    public static void TogglePin(ClipItem it) { it.Pinned = !it.Pinned; Raise(); }
    public static void Clear() { for (int i = Items.Count - 1; i >= 0; i--) if (!Items[i].Pinned) Drop(Items, i); Raise(); }
    public static void Raise() { var c = Changed; if (c != null) c(); }

    // Pins: text you chose to keep, saved in clippins.txt next to Upbar (one per line, base64 so any text fits)
    // and shown at the top whichever history is in use, Windows' or ours.
    static string PinFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clippins.txt"); } }
    public static readonly List<string> Pins = LoadPins();
    static List<string> LoadPins()
    {
        var l = new List<string>();
        try { if (File.Exists(PinFile)) foreach (var line in File.ReadAllLines(PinFile)) try { l.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line))); } catch (FormatException) { } }
        catch (IOException) { }
        return l;
    }
    public static void ReloadPins() { Pins.Clear(); Pins.AddRange(LoadPins()); Raise(); } // after a restore
    public static bool IsPinned(string text) { return text != null && Pins.Contains(text); }
    public static void TogglePinText(string text)
    {
        if (text == null) return;
        if (!Pins.Remove(text)) { Pins.Insert(0, text); if (Pins.Count > 50) Pins.RemoveAt(Pins.Count - 1); }
        try { File.WriteAllLines(PinFile, Pins.ConvertAll(p => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p)))); }
        catch (IOException) { } // couldn't save: kept for this session
        catch (UnauthorizedAccessException) { }
        Raise();
    }

    public static string Title(ClipItem it)
    {
        if (it.Text != null)
        {
            var t = it.Text.Trim().Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return t.Length > 90 ? t.Substring(0, 90) + "…" : t;
        }
        if (it.Files != null) return it.Files.Length == 1 ? Path.GetFileName(it.Files[0].TrimEnd('\\')) : it.Files.Length + " files";
        return "Image";
    }

    public static string Sub(ClipItem it)
    {
        var age = DateTime.Now - it.When;
        string ago = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? (int)age.TotalMinutes + " min ago" : age.TotalDays < 1 ? (int)age.TotalHours + " h ago" : it.When.ToString("MMM d");
        string kind = it.Text != null ? it.Text.Length + " characters" : it.Files != null ? "Files" : it.ImgSize.Width > 0 ? it.ImgSize.Width + " × " + it.ImgSize.Height : "Image";
        return kind + " · " + ago;
    }
}

class ClipPopup : Glass
{
    readonly Action onChange;

    public ClipPopup(float s) : base(s)
    {
        onChange = () => { if (!IsDisposed) Build(); };
        Clip.Changed += onChange;
        FormClosed += (a, b) => Clip.Changed -= onChange;
        Build();
    }

    void Build()
    {
        ClearItems();
        float W = 400 * S, pad = 14 * S, x = pad, w = W - 2 * pad, y = pad, b = 36 * S;
        var win = Sys.ClipHistory(); // the real Win+V history; null when it is turned off
        var items = new List<ClipItem>();
        foreach (var p in Clip.Pins) items.Add(new ClipItem { Text = p, Pinned = true, PinCopy = true }); // pins first
        foreach (var it in win ?? Clip.Items) if (!Clip.IsPinned(it.Text)) items.Add(it);
        Add(new Caption { Icon = G.Paste, Text = "Clipboard" + (items.Count > 0 ? "  ·  " + items.Count : "") + (win != null ? "  ·  Windows history" : ""), Font = Theme.Bold, Strong = true, R = new RectangleF(x + 6 * S, y, w - 60 * S, b) });
        // No bulk clear for Windows history: the API can't tell us which items are pinned, so we never risk them.
        // (Win+V's own "Clear all" keeps pins.) Single items can still be removed with their X.
        if (win == null && Clip.Items.Exists(i => !i.Pinned))
            Add(new IconBtn { Icon = G.Delete, Tip = "Clear history", R = new RectangleF(x + w - b, y, b, b), Click = Clip.Clear });
        y += b + 10 * S;

        if (items.Count == 0)
        {
            float h = 110 * S;
            Add(new Segment { R = new RectangleF(x, y, w, h) });
            Add(new Caption { Text = "Copy something and it shows up here", Font = Theme.Bold, Strong = true, R = new RectangleF(x + 24 * S, y + 28 * S, w - 48 * S, 26 * S) });
            Add(new Caption { Text = win != null ? "Uses your Windows clipboard history (Win+V)" : "Kept in memory only, cleared when the bar quits", R = new RectangleF(x + 24 * S, y + 56 * S, w - 48 * S, 22 * S) });
            y += h + pad;
        }
        else
        {
            // Up to 7 rows visible; the rest scroll (mouse wheel) inside the same surface.
            float rh = 52 * S, areaH = Math.Min(items.Count, 7) * rh, top = y + 4 * S, cy = top;
            Add(new Segment { R = new RectangleF(x, y, w, areaH + 8 * S) });
            foreach (var it in items)
            {
                Add(new ClipRow { Item = it, Scrolls = true, R = new RectangleF(x + 4 * S, cy, w - 8 * S, rh) });
                cy += rh;
            }
            SetScroll(new RectangleF(x, top, w, areaH), cy);
            y += areaH + 8 * S + pad;
        }
        Place((int)W, (int)y);
        Invalidate();
    }
}

// One history entry: click copies it back and pastes into the app you came from; pin keeps it; X forgets it.
class ClipRow : Widget
{
    public ClipItem Item;
    RectangleF X { get { return new RectangleF(R.Right - 38 * S, R.Y + (R.Height - 30 * S) / 2, 30 * S, 30 * S); } }
    RectangleF PinR { get { return new RectangleF(R.Right - 70 * S, R.Y + (R.Height - 30 * S) / 2, 30 * S, 30 * S); } }

    public override void Up(Point p, bool inside)
    {
        if (!inside) return;
        if (X.Contains(p)) Forget();
        else if (PinR.Contains(p) && Item.Text != null) Clip.TogglePinText(Item.Text); // text pins are saved, from either history
        else if (PinR.Contains(p) && Item.WinItem == null) Clip.TogglePin(Item);
        else Activate();
    }
    public override void Activate() { if (Item.WinItem != null) Sys.ClipRestore(Item); else Clip.Restore(Item); Owner.Close(); Native.PasteSoon(); }
    public override void Key(Keys k) { if (k == Keys.Delete) Forget(); }
    void Forget() { if (Item.PinCopy) Clip.TogglePinText(Item.Text); else if (Item.WinItem != null) { Sys.ClipDelete(Item); Clip.Raise(); } else Clip.Remove(Item); }

    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph(16 * S)))
        {
            if (hoverA > 0) Native.Fill(g, p, Color.FromArgb((int)(hoverA * 150), Theme.Accent));
            Overlay(g, p);
        }
        var ir = new RectangleF(R.X + 10 * S, R.Y + (R.Height - 32 * S) / 2, 32 * S, 32 * S);
        if (Item.Thumb != null) using (var p = Native.Round(ir, 8 * S)) ArtView.Cover(g, p, ir, Item.Thumb);
        else g.DrawString(Item.Files != null ? G.Folder : G.Copy, Theme.Icon, Theme.Text, ir, Theme.Center);
        float tx = R.X + 52 * S, tw = R.Width - 52 * S - 78 * S;
        g.DrawString(Clip.Title(Item), Theme.Body, Theme.Text, new RectangleF(tx, R.Y, tw, R.Height / 2 + S), Theme.Top);
        g.DrawString(Clip.Sub(Item), Theme.Small, Theme.Dim, new RectangleF(tx, R.Y + R.Height / 2 + 2 * S, tw, R.Height / 2), Theme.Bottom);
        bool pinned = Item.Pinned || Clip.IsPinned(Item.Text);
        if ((Item.WinItem == null || Item.Text != null) && (pinned || hoverA > 0))
            g.DrawString(G.Pin, Theme.IconSmall, pinned ? Theme.B(Theme.Accent) : Theme.B(Theme.A((int)(hoverA * 200))), PinR, Theme.Center);
        if (hoverA > 0) g.DrawString(G.Close, Theme.IconSmall, Theme.B(Theme.A((int)(hoverA * 220))), X, Theme.Center);
    }
}
