using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// File shelf: a saved list of file paths (references, not copies) in shelf.txt next to the exe.
static class Shelf
{
    static string FilePath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shelf.txt"); } }
    public static readonly List<string> Items = Load();
    public static event Action Changed;

    static List<string> Load()
    {
        try { if (File.Exists(FilePath)) return Merge(new List<string>(), File.ReadAllLines(FilePath)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new List<string>();
    }

    // Appends new paths, skipping blanks and case-insensitive duplicates.
    public static List<string> Merge(List<string> list, IEnumerable<string> add)
    {
        foreach (var p in add)
        {
            var t = p == null ? "" : p.Trim();
            if (t.Length == 0 || list.Exists(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(t);
        }
        return list;
    }

    public static void Add(IEnumerable<string> paths) { Merge(Items, paths); Save(); }
    public static void Remove(string p) { Items.Remove(p); Save(); }
    public static void Clear() { Items.Clear(); Save(); }

    static void Save()
    {
        try { File.WriteAllLines(FilePath, Items); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var c = Changed;
        if (c != null) c();
    }

    public static string[] Existing() { return Items.FindAll(p => File.Exists(p) || Directory.Exists(p)).ToArray(); }

    public static string Describe(string p)
    {
        string dir = Path.GetFileName(Path.GetDirectoryName(p) ?? "") ?? "";
        if (Directory.Exists(p)) return "Folder · " + dir;
        if (!File.Exists(p)) return "Missing: moved or deleted";
        long n = new FileInfo(p).Length;
        string size = n < 1024 ? n + " B" : n < 1048576 ? (n / 1024.0).ToString("0") + " KB" : (n / 1048576.0).ToString("0.0") + " MB";
        return size + " · " + dir;
    }
}

class ShelfPopup : Glass
{
    readonly List<Image> icons = new List<Image>();
    readonly Action onChange;
    const int MaxRows = 12; // shortcut: no scrolling; past this, "Drag all" / "Copy all" still include every file

    public ShelfPopup(float s) : base(s)
    {
        AllowDrop = true;
        DragEnter += (a, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (a, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) Shelf.Add(f); };
        onChange = () => { if (!IsDisposed) Build(); };
        Shelf.Changed += onChange;
        FormClosed += (a, b) => { Shelf.Changed -= onChange; DisposeIcons(); };
        Build();
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr ctx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // Windows' cached thumbnail (THUMBNAILONLY: null when the file type has none, so it falls back to its icon).
    static Image Thumbnail(string path, int px)
    {
        try
        {
            IShellItemImageFactory f;
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
            IntPtr h;
            if (f.GetImage(new SIZE { cx = px, cy = px }, 0x8, out h) != 0 || h == IntPtr.Zero) return null;
            try { return Image.FromHbitmap(h); }
            finally { DeleteObject(h); }
        }
        catch (Exception) { return null; } // missing file, no shell handler
    }

    void DisposeIcons() { foreach (var i in icons) i.Dispose(); icons.Clear(); }

    void AddFiles()
    {
        CloseOnDeactivate = false; // the dialog takes focus
        using (var d = new OpenFileDialog { Multiselect = true, Title = "Add files to shelf" })
            if (d.ShowDialog(this) == DialogResult.OK) Shelf.Add(d.FileNames);
        CloseOnDeactivate = true;
        Activate();
    }

    void Build()
    {
        ClearItems();
        DisposeIcons();
        float W = 380 * S, pad = 14 * S, x = pad, w = W - 2 * pad, y = pad, b = 36 * S;
        var items = Shelf.Items;

        Add(new Caption { Icon = G.Folder, Text = "File shelf" + (items.Count > 0 ? "  ·  " + items.Count : ""), Font = Theme.Bold, Strong = true, R = new RectangleF(x + 6 * S, y, w - 100 * S, b) });
        Add(new IconBtn { Icon = G.Add, R = new RectangleF(x + w - b, y, b, b), Click = AddFiles });
        if (items.Count > 0) Add(new IconBtn { Icon = G.Delete, R = new RectangleF(x + w - 2 * b - 6 * S, y, b, b), Click = Shelf.Clear });
        y += b + 10 * S;

        if (items.Count == 0)
        {
            float h = 120 * S;
            Add(new Segment { R = new RectangleF(x, y, w, h) });
            Add(new Caption { Text = "Drop files here or on the bar", Font = Theme.Bold, Strong = true, R = new RectangleF(x + 24 * S, y + 32 * S, w - 48 * S, 26 * S) });
            Add(new Caption { Text = "Drag them out into uploads, mail or folders", R = new RectangleF(x + 24 * S, y + 60 * S, w - 48 * S, 22 * S) });
            y += h + pad;
        }
        else
        {
            int n = Math.Min(items.Count, MaxRows);
            float rh = 50 * S;
            Add(new Segment { R = new RectangleF(x, y, w, n * rh + 8 * S) });
            y += 4 * S;
            for (int i = 0; i < n; i++)
            {
                string p = items[i];
                Image ic = null;
                bool thumb = false;
                if (File.Exists(p)) ic = Thumbnail(p, (int)(64 * S)); // images, PDFs, videos: the preview Explorer shows
                if (ic != null) thumb = true;
                else try { if (File.Exists(p)) using (var icon = Icon.ExtractAssociatedIcon(p)) ic = icon.ToBitmap(); }
                     catch (ArgumentException) { }
                if (ic != null) icons.Add(ic);
                Add(new FileRow { Path = p, Img = ic, Thumb = thumb, Sub = Shelf.Describe(p), Missing = !File.Exists(p) && !Directory.Exists(p), R = new RectangleF(x + 4 * S, y, w - 8 * S, rh) });
                y += rh;
            }
            y += 4 * S + 10 * S;
            if (items.Count > MaxRows)
            {
                Add(new Caption { Text = "+" + (items.Count - MaxRows) + " more (included in Drag all)", R = new RectangleF(x + 6 * S, y, w, 20 * S) });
                y += 26 * S;
            }
            Add(new DragChip { R = new RectangleF(x, y, w, 44 * S) });
            y += 44 * S + pad;
        }
        Place((int)W, (int)y);
        Invalidate();
    }
}

// One shelf file: click opens, drag copies it out, the X removes it from the shelf (not from disk).
class FileRow : Widget
{
    public bool Thumb; // Img is a preview rather than an icon
    public string Path, Sub;
    public Image Img;
    public bool Missing;
    Point down;
    bool dragged;

    RectangleF X { get { return new RectangleF(R.Right - 40 * S, R.Y + (R.Height - 32 * S) / 2, 32 * S, 32 * S); } }

    public override void Down(Point p) { down = p; dragged = false; }
    public override void Drag(Point p)
    {
        if (dragged || Missing || Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 6 * S) return;
        dragged = true;
        Owner.DragOut(new[] { Path });
    }
    public override void Up(Point p, bool inside)
    {
        if (dragged || !inside) return;
        if (X.Contains(p)) Shelf.Remove(Path); else Activate();
    }
    public override void Activate() { if (!Missing) Shell.Open(Path); }
    public override void Key(Keys k) { if (k == Keys.Delete) Shelf.Remove(Path); }

    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph(16 * S)))
        {
            if (hoverA > 0) Native.Fill(g, p, Color.FromArgb((int)(hoverA * 150), Theme.Accent));
            Overlay(g, p);
        }
        float ix = R.X + 12 * S, sz = 24 * S;
        if (Img != null && Thumb)
        {
            // Preview: centre-cropped to fill a 32px rounded square, like Explorer's thumbnails.
            float box = 32 * S;
            var r = new RectangleF(R.X + 8 * S, R.Y + (R.Height - box) / 2, box, box);
            int side = Math.Min(Img.Width, Img.Height);
            var st = g.Save();
            using (var p = Native.Round(r, 6 * S)) g.SetClip(p, System.Drawing.Drawing2D.CombineMode.Intersect);
            g.DrawImage(Img, r, new RectangleF((Img.Width - side) / 2f, (Img.Height - side) / 2f, side, side), GraphicsUnit.Pixel);
            g.Restore(st);
        }
        else if (Img != null) g.DrawImage(Img, ix, R.Y + (R.Height - sz) / 2, sz, sz);
        else g.DrawString(Directory.Exists(Path) ? G.Folder : G.File, Theme.Icon, Missing ? Theme.Dim : Theme.Text, new RectangleF(ix, R.Y, sz, R.Height), Theme.Center);
        float tx = R.X + 48 * S, tw = R.Width - 48 * S - 46 * S;
        g.DrawString(System.IO.Path.GetFileName(Path.TrimEnd('\\')), Theme.Body, Missing ? Theme.Dim : Theme.Text, new RectangleF(tx, R.Y, tw, R.Height / 2 + S), Theme.Top);
        g.DrawString(Sub, Theme.Small, Theme.Dim, new RectangleF(tx, R.Y + R.Height / 2 + 2 * S, tw, R.Height / 2), Theme.Bottom);
        if (hoverA > 0) using (var b = new SolidBrush(Theme.A((int)(hoverA * 230)))) g.DrawString(G.Close, Theme.IconSmall, b, X, Theme.Center);
    }
}

// Full-width pill: drag it to drop every shelf file at once; click copies them to the clipboard.
class DragChip : Widget
{
    Point down;
    bool dragged;
    float copiedA;
    public override void Down(Point p) { down = p; dragged = false; }
    public override void Drag(Point p)
    {
        if (dragged || Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 6 * S) return;
        var files = Shelf.Existing();
        if (files.Length == 0) return;
        dragged = true;
        Owner.DragOut(files);
    }
    public override void Up(Point p, bool inside) { if (!dragged && inside) Activate(); }
    public override void Activate()
    {
        var files = Shelf.Existing();
        if (files.Length == 0) return;
        var list = new StringCollection();
        list.AddRange(files);
        try { Clipboard.SetFileDropList(list); copiedA = 1; Owner.Kick(); }
        catch (System.Runtime.InteropServices.ExternalException) { } // clipboard busy
    }
    public override bool Animate() { bool b = base.Animate(); return Native.Ease(ref copiedA, 0, 0.02f) | b; }
    public override void Paint(Graphics g)
    {
        using (var p = Native.Round(R, Morph(R.Height / 2)))
        {
            Native.Fill(g, p, Theme.Accent);
            Native.Gloss(g, p, R, S, 0.8f);
            Overlay(g, p);
        }
        string text = copiedA > 0.05f ? "Copied, paste anywhere" : "Drag all " + Shelf.Existing().Length + " files  ·  click to copy";
        g.DrawString(copiedA > 0.05f ? G.Check : G.Copy, Theme.Icon, Theme.B(Theme.OnAccent), new RectangleF(R.X + 18 * S, R.Y, 24 * S, R.Height), Theme.Left);
        g.DrawString(text, Theme.SmallBold, Theme.B(Theme.OnAccent), new RectangleF(R.X + 50 * S, R.Y, R.Width - 64 * S, R.Height), Theme.Left);
    }
}
