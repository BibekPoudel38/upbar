using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

// Notes: plain markdown files in notes\ next to the exe (images in notes\images\). Nothing leaves this folder:
// the preview shows only images stored inside it, opens only http(s) links, and never runs HTML or scripts.
static class NoteStore
{
    public class Note { public string Path, Title; public DateTime Modified; }
    public static string Dir { get { return Path.Combine(Path.GetDirectoryName(typeof(NoteStore).Assembly.Location), "notes"); } }
    public static string ImageDir { get { return Path.Combine(Dir, "images"); } }

    public static List<Note> All()
    {
        var list = new List<Note>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.GetFiles(Dir, "*.md"))
        {
            string head;
            try { using (var r = new StreamReader(f, Encoding.UTF8)) { var buf = new char[2048]; head = new string(buf, 0, r.Read(buf, 0, buf.Length)); } }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            list.Add(new Note { Path = f, Title = TitleOf(head), Modified = File.GetLastWriteTime(f) });
        }
        list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return list;
    }

    // The first line with any text (a heading's # marks and other markdown stripped).
    public static string TitleOf(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.Trim().TrimStart('#', '>', ' ').Trim();
            l = Regex.Replace(l, @"!?\[([^\]]*)\]\([^)]*\)|[*_`~]", m => m.Groups[1].Success ? m.Groups[1].Value : "").Trim();
            if (l.Length > 0) return l.Length > 80 ? l.Substring(0, 79) + "…" : l;
        }
        return "Untitled";
    }

    public static string NewPath()
    {
        Directory.CreateDirectory(Dir);
        string stem = DateTime.Now.ToString("yyyyMMdd-HHmmss"), p = Path.Combine(Dir, stem + ".md");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(Dir, stem + "-" + i + ".md");
        return p;
    }

    // Written to a temporary file first and swapped in, so a crash mid-save never leaves half a note.
    public static void Save(string path, string text)
    {
        Directory.CreateDirectory(Dir);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    public static void Recycle(string path) // to the Recycle Bin, so a deleted note can be restored
    {
        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
        catch (Exception) { } // already gone, or the user cancelled an error dialog
    }

    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };
    public static bool IsImage(string f) { return Array.IndexOf(ImageExts, Path.GetExtension(f).ToLowerInvariant()) >= 0; }

    public static string AddImage(string file)
    {
        Directory.CreateDirectory(ImageDir);
        string name = Unique(Path.GetFileNameWithoutExtension(file), Path.GetExtension(file).ToLowerInvariant());
        File.Copy(file, Path.Combine(ImageDir, name));
        return "images/" + name;
    }

    public static string AddImage(Image img)
    {
        Directory.CreateDirectory(ImageDir);
        string name = Unique("pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), ".png");
        img.Save(Path.Combine(ImageDir, name), System.Drawing.Imaging.ImageFormat.Png);
        return "images/" + name;
    }

    static string Unique(string stem, string ext)
    {
        stem = Regex.Replace(stem, @"[^\w\-]+", "-").Trim('-');
        if (stem.Length == 0) stem = "image";
        if (stem.Length > 40) stem = stem.Substring(0, 40);
        string n = stem + ext;
        for (int i = 2; File.Exists(Path.Combine(ImageDir, n)); i++) n = stem + "-" + i + ext;
        return n;
    }

    // A note's image path -> a file inside the notes folder, or null (../ tricks, absolute paths and web addresses refused).
    public static string Resolve(string rel)
    {
        try
        {
            if (rel.Contains(":")) return null;
            string full = Path.GetFullPath(Path.Combine(Dir, rel.Replace('/', '\\')));
            return full.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception) { return null; }
    }
}

// Markdown helpers shared by the editor and the preview.
static class Md
{
    public static bool IsTableRow(string l) { l = l.Trim(); return l.Length > 1 && l.StartsWith("|"); }

    public static string[] Cells(string row)
    {
        string t = row.Trim();
        if (t.StartsWith("|")) t = t.Substring(1);
        if (t.EndsWith("|")) t = t.Substring(0, t.Length - 1);
        var c = t.Split('|');
        for (int i = 0; i < c.Length; i++) c[i] = c[i].Trim();
        return c;
    }

    public static bool IsSeparator(string[] cells)
    {
        foreach (var c in cells) if (!Regex.IsMatch(c, @"^:?-+:?$")) return false;
        return cells.Length > 0;
    }

    // Lines up a pipe table: every column as wide as its widest cell, the separator row matching (alignment colons kept).
    public static List<string> FormatTable(List<string> rows)
    {
        var cells = rows.ConvertAll(Cells);
        int cols = 0;
        foreach (var c in cells) cols = Math.Max(cols, c.Length);
        var w = new int[cols];
        for (int i = 0; i < cols; i++) w[i] = 3;
        foreach (var c in cells) if (!IsSeparator(c)) for (int i = 0; i < c.Length; i++) w[i] = Math.Max(w[i], c[i].Length);
        var outp = new List<string>();
        foreach (var c in cells)
        {
            var parts = new string[cols];
            bool sep = IsSeparator(c);
            for (int i = 0; i < cols; i++)
            {
                string v = i < c.Length ? c[i] : "";
                if (sep)
                {
                    bool l = v.StartsWith(":"), r = v.EndsWith(":") && v.Length > 1;
                    parts[i] = (l ? ":" : "-") + new string('-', Math.Max(1, w[i] - 2)) + (r ? ":" : "-");
                }
                else parts[i] = v.PadRight(w[i]);
            }
            outp.Add("| " + string.Join(" | ", parts) + " |");
        }
        return outp;
    }

    // Inline runs: plain, **bold**, *italic*, `code`, ~~strike~~, [link](url), ![image](src).
    public class Run { public string Text, Url; public bool Bold, Italic, Code, Strike, Image; }
    static readonly Regex Inline = new Regex(@"\*\*(.+?)\*\*|__(.+?)__|(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])|(?<!\w)_(?=\S)(.+?)(?<=\S)_(?!\w)|`([^`]+)`|~~(.+?)~~|!\[([^\]]*)\]\(([^)\s]+)\)|\[([^\]]+)\]\(([^)\s]+)\)");

    public static List<Run> Runs(string s)
    {
        var list = new List<Run>();
        int at = 0;
        foreach (Match m in Inline.Matches(s))
        {
            if (m.Index > at) list.Add(new Run { Text = s.Substring(at, m.Index - at) });
            if (m.Groups[1].Success || m.Groups[2].Success) list.Add(new Run { Text = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, Bold = true });
            else if (m.Groups[3].Success || m.Groups[4].Success) list.Add(new Run { Text = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value, Italic = true });
            else if (m.Groups[5].Success) list.Add(new Run { Text = m.Groups[5].Value, Code = true });
            else if (m.Groups[6].Success) list.Add(new Run { Text = m.Groups[6].Value, Strike = true });
            else if (m.Groups[8].Success) list.Add(new Run { Text = m.Groups[7].Value, Url = m.Groups[8].Value, Image = true });
            else list.Add(new Run { Text = m.Groups[9].Value, Url = m.Groups[10].Value });
            at = m.Index + m.Length;
        }
        if (at < s.Length) list.Add(new Run { Text = s.Substring(at) });
        return list;
    }

    public static readonly Regex Heading = new Regex(@"^(#{1,6})\s+(.*)$"), Quote = new Regex(@"^\s*>\s?(.*)$"),
        Item = new Regex(@"^(\s*)([-*+]|\d+[.)])\s+(\[[ xX]\]\s+)?(.*)$"), Rule = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$"), Fence = new Regex(@"^\s*(```|~~~)");
}

// The rendered view of a note (Ctrl+E): headings, paragraphs with inline styles, lists and tasks, quotes, code,
// tables, rules and images. Drawn straight with GDI+; layout is redone only when the text or width changes.
class MarkdownView : Control
{
    class Op { public int Kind; public RectangleF R; public string Text; public Font Font; public Color Color; public string Url; public int Line = -1; public bool Done; public Image Img; } // Kind: 0 text, 1 box, 2 image, 3 check
    readonly List<Op> ops = new List<Op>();
    readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
    readonly Dictionary<string, float> widths = new Dictionary<string, float>();
    string text = "";
    float scroll, contentH;
    int laidFor = -1;
    public Action<int> ToggleTask; // line index of a task item whose box was clicked
    public Font Body, Bold, Italic, Mono, H1, H2, H3, H4;
    public Color Fore, Dim, Accent, Surface, Line;

    public MarkdownView() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    public void SetText(string t) { text = t ?? ""; laidFor = -1; scroll = 0; Invalidate(); }

    protected override void Dispose(bool disposing) { if (disposing) foreach (var i in images.Values) if (i != null) i.Dispose(); base.Dispose(disposing); }

    float W(Graphics g, string s, Font f)
    {
        string k = f.GetHashCode() + "\u0001" + s;
        float w;
        if (!widths.TryGetValue(k, out w)) { w = g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width; if (widths.Count > 20000) widths.Clear(); widths[k] = w; }
        return w;
    }

    Image Picture(string src)
    {
        Image img;
        if (images.TryGetValue(src, out img)) return img;
        string f = NoteStore.Resolve(src);
        try { img = f == null ? null : Image.FromStream(new MemoryStream(File.ReadAllBytes(f))); }
        catch (Exception) { img = null; } // not an image after all
        return images[src] = img;
    }

    // Lays out inline runs from x0 to x1 starting at y; returns the y below the last line.
    float Flow(Graphics g, List<Md.Run> runs, Font baseFont, Color color, float x0, float x1, float y, int lineIndex)
    {
        float lh = baseFont.GetHeight(g) * 1.45f, x = x0, space = W(g, "a a", baseFont) - W(g, "aa", baseFont);
        foreach (var r in runs)
        {
            if (r.Image) // inline image: on its own line, scaled to fit
            {
                var img = Picture(r.Url);
                if (img == null) { var miss = new Op { Kind = 0, Text = "[image: " + (r.Text.Length > 0 ? r.Text : r.Url) + "]", Font = Italic, Color = Dim, R = new RectangleF(x0, y, x1 - x0, lh) }; ops.Add(miss); y += lh; x = x0; continue; }
                float k = Math.Min(1f, Math.Min((x1 - x0) / img.Width, 520f / img.Height));
                if (x > x0) { y += lh; x = x0; }
                ops.Add(new Op { Kind = 2, Img = img, R = new RectangleF(x0, y + 4, img.Width * k, img.Height * k) });
                y += img.Height * k + 12;
                continue;
            }
            Font f = r.Code ? Mono : r.Bold && baseFont == Body ? Bold : r.Italic && baseFont == Body ? Italic : baseFont;
            Color c = r.Url != null ? Accent : r.Code ? Fore : color;
            foreach (var word in Regex.Split(r.Text, @"(?<= )"))
            {
                if (word.Length == 0) continue;
                string bare = word.TrimEnd(' ');
                float ww = W(g, bare, f) + (r.Code ? 6 : 0);
                if (x + ww > x1 && x > x0) { y += lh; x = x0; }
                if (r.Code) ops.Add(new Op { Kind = 1, Color = Surface, R = new RectangleF(x, y + lh * 0.12f, ww, lh * 0.78f) });
                ops.Add(new Op { Kind = 0, Text = bare, Font = r.Strike || r.Url != null ? Strikeish(f, r.Strike, r.Url != null) : f, Color = c, Url = r.Url, R = new RectangleF(x + (r.Code ? 3 : 0), y + (lh - f.GetHeight(g)) / 2, ww + 2, lh) });
                x += ww + (word.EndsWith(" ") ? space : 0);
            }
        }
        return y + lh;
    }

    readonly Dictionary<string, Font> styled = new Dictionary<string, Font>();
    Font Strikeish(Font f, bool strike, bool link)
    {
        string k = f.GetHashCode() + "|" + strike + link;
        Font s;
        if (!styled.TryGetValue(k, out s)) styled[k] = s = new Font(f, f.Style | (strike ? FontStyle.Strikeout : 0) | (link ? FontStyle.Underline : 0));
        return s;
    }

    void Layout(Graphics g)
    {
        ops.Clear();
        float pad = Math.Max(28, (Width - 760) / 2f), x0 = pad, x1 = Width - pad, y = 26;
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            Match m;
            if (l.Trim().Length == 0) { y += 8; continue; }
            if (Md.Fence.IsMatch(l)) // fenced code block
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !Md.Fence.IsMatch(lines[i]); i++) code.Add(lines[i]);
                float lh = Mono.GetHeight(g) * 1.35f, top = y + 4;
                ops.Add(new Op { Kind = 1, Color = Surface, R = new RectangleF(x0, top, x1 - x0, code.Count * lh + 20) });
                for (int k = 0; k < code.Count; k++) ops.Add(new Op { Kind = 0, Text = code[k], Font = Mono, Color = Fore, R = new RectangleF(x0 + 14, top + 10 + k * lh, x1 - x0 - 28, lh) });
                y = top + code.Count * lh + 30;
                continue;
            }
            if ((m = Md.Heading.Match(l)).Success)
            {
                int level = m.Groups[1].Length;
                var f = level == 1 ? H1 : level == 2 ? H2 : level == 3 ? H3 : H4;
                y += level <= 2 ? 14 : 8;
                y = Flow(g, Md.Runs(m.Groups[2].Value), f, Fore, x0, x1, y, i);
                if (level <= 2) { ops.Add(new Op { Kind = 1, Color = Line, R = new RectangleF(x0, y, x1 - x0, 1) }); y += 8; }
                continue;
            }
            if (Md.Rule.IsMatch(l)) { ops.Add(new Op { Kind = 1, Color = Line, R = new RectangleF(x0, y + 10, x1 - x0, 1) }); y += 22; continue; }
            if (Md.IsTableRow(l)) { y = Table(g, lines, ref i, x0, x1, y); continue; }
            if ((m = Md.Quote.Match(l)).Success)
            {
                var sb = new StringBuilder(m.Groups[1].Value);
                while (i + 1 < lines.Length && Md.Quote.IsMatch(lines[i + 1])) sb.Append(' ').Append(Md.Quote.Match(lines[++i]).Groups[1].Value);
                float top = y;
                y = Flow(g, Md.Runs(sb.ToString()), Italic, Dim, x0 + 18, x1, y + 2, i) + 4;
                ops.Add(new Op { Kind = 1, Color = Accent, R = new RectangleF(x0, top + 2, 3, y - top - 6) });
                continue;
            }
            if ((m = Md.Item.Match(l)).Success)
            {
                float indent = x0 + Math.Min(6, m.Groups[1].Value.Replace("\t", "    ").Length / 2) * 22 + 4;
                float lh = Body.GetHeight(g) * 1.45f;
                if (m.Groups[3].Success) // task
                {
                    bool done = m.Groups[3].Value.Trim().ToLower() == "[x]";
                    ops.Add(new Op { Kind = 3, Done = done, Line = i, Color = Accent, R = new RectangleF(indent, y + (lh - 15) / 2, 15, 15) });
                    y = Flow(g, Md.Runs(m.Groups[4].Value), Body, done ? Dim : Fore, indent + 24, x1, y, i);
                }
                else
                {
                    string mark = char.IsDigit(m.Groups[2].Value[0]) ? m.Groups[2].Value : "•";
                    ops.Add(new Op { Kind = 0, Text = mark, Font = Body, Color = Dim, R = new RectangleF(indent, y + (lh - Body.GetHeight(g)) / 2, 22, lh) });
                    y = Flow(g, Md.Runs(m.Groups[4].Value), Body, Fore, indent + 22, x1, y, i);
                }
                continue;
            }
            var para = new StringBuilder(l.Trim());
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !Starts(lines[i + 1])) para.Append(' ').Append(lines[++i].Trim());
            y = Flow(g, Md.Runs(para.ToString()), Body, Fore, x0, x1, y, i) + 6;
        }
        contentH = y + 40;
        scroll = Math.Max(0, Math.Min(scroll, contentH - Height));
    }

    static bool Starts(string l) { return Md.Fence.IsMatch(l) || Md.Heading.IsMatch(l) || Md.Rule.IsMatch(l) || Md.IsTableRow(l) || Md.Quote.IsMatch(l) || Md.Item.IsMatch(l); }

    float Table(Graphics g, string[] lines, ref int i, float x0, float x1, float y)
    {
        var rows = new List<string[]>();
        for (; i < lines.Length && Md.IsTableRow(lines[i]); i++) { var c = Md.Cells(lines[i]); if (!Md.IsSeparator(c)) rows.Add(c); }
        i--;
        int cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);
        if (cols == 0) return y;
        var w = new float[cols];
        for (int r = 0; r < rows.Count; r++) for (int c = 0; c < rows[r].Length; c++) w[c] = Math.Max(w[c], W(g, rows[r][c], r == 0 ? Bold : Body) + 24);
        float total = 0;
        foreach (var v in w) total += v;
        float k = total > x1 - x0 ? (x1 - x0) / total : 1, lh = Body.GetHeight(g) * 1.6f, top = y + 6;
        for (int r = 0; r < rows.Count; r++)
        {
            float x = x0, ry = top + r * lh;
            if (r == 0) ops.Add(new Op { Kind = 1, Color = Surface, R = new RectangleF(x0, ry, total * k, lh) });
            for (int c = 0; c < cols; c++)
            {
                string v = c < rows[r].Length ? rows[r][c] : "";
                ops.Add(new Op { Kind = 0, Text = Regex.Replace(v, @"\*\*|__|`", ""), Font = r == 0 ? Bold : Body, Color = Fore, R = new RectangleF(x + 12, ry + (lh - Body.GetHeight(g)) / 2, w[c] * k - 16, lh) });
                x += w[c] * k;
            }
            ops.Add(new Op { Kind = 1, Color = Line, R = new RectangleF(x0, ry + lh, total * k, 1) });
        }
        return top + rows.Count * lh + 14;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (laidFor != Width) { Layout(g); laidFor = Width; }
        g.TranslateTransform(0, -scroll);
        var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        foreach (var o in ops)
        {
            if (o.R.Bottom < scroll || o.R.Top > scroll + Height) continue; // off screen
            if (o.Kind == 0) using (var b = new SolidBrush(o.Color)) g.DrawString(o.Text, o.Font, b, o.R, fmt);
            else if (o.Kind == 1) using (var p = Native.Round(o.R, Math.Min(6, o.R.Height / 2))) Native.Fill(g, p, o.Color);
            else if (o.Kind == 2) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(o.Img, o.R); }
            else
            {
                using (var p = Native.Round(o.R, 4))
                    if (o.Done) { Native.Fill(g, p, o.Color); using (var pen = new Pen(Color.White, 2)) g.DrawLines(pen, new[] { new PointF(o.R.X + 3.5f, o.R.Y + 7.5f), new PointF(o.R.X + 6.5f, o.R.Y + 10.5f), new PointF(o.R.X + 11.5f, o.R.Y + 4.5f) }); }
                    else using (var pen = new Pen(Dim, 1.5f)) g.DrawPath(pen, p);
            }
        }
        g.ResetTransform();
        if (contentH > Height) // slim scrollbar
        {
            float h = Math.Max(30, Height * Height / contentH), y = (Height - h) * scroll / (contentH - Height);
            using (var p = Native.Round(new RectangleF(Width - 7, y + 2, 4, h - 4), 2)) Native.Fill(g, p, Color.FromArgb(90, Dim));
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); laidFor = -1; }
    protected override void OnMouseWheel(MouseEventArgs e) { scroll = Math.Max(0, Math.Min(scroll - e.Delta * 0.6f, contentH - Height)); Invalidate(); }

    Op HitOp(Point p)
    {
        var at = new PointF(p.X, p.Y + scroll);
        foreach (var o in ops) if ((o.Kind == 3 || o.Url != null && o.Kind == 0) && o.R.Contains(at)) return o;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = HitOp(e.Location) != null ? Cursors.Hand : Cursors.Default; }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var o = HitOp(e.Location);
        if (o == null) return;
        if (o.Kind == 3) { if (ToggleTask != null) ToggleTask(o.Line); return; }
        string url = TopBar.WebLink(o.Url);
        if (url != null) Shell.Open(url); // web links only; nothing local is ever opened from a note
    }
}

// The editor window: notes history on the left, the note on the right (markdown with live styling), a preview
// (Ctrl+E), focus full screen (F11). Every change is saved on its own a moment after you stop typing.
class NotesWindow : Form
{
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref Native.RECT r);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref Point p);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

    readonly float S;
    readonly Panel side = new Panel { Dock = DockStyle.Left }, tools = new Panel { Dock = DockStyle.Top };
    readonly TextBox search = new TextBox { BorderStyle = BorderStyle.None };
    readonly ListBox list = new ListBox { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
    readonly RichTextBox ed = new RichTextBox { BorderStyle = BorderStyle.None, DetectUrls = false, AcceptsTab = true, ScrollBars = RichTextBoxScrollBars.Vertical, EnableAutoDragDrop = false, AllowDrop = true, HideSelection = false };
    readonly MarkdownView view = new MarkdownView { Dock = DockStyle.Fill, Visible = false };
    readonly Label status = new Label { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleRight };
    readonly Timer saveTimer = new Timer { Interval = 700 }, styleTimer = new Timer { Interval = 120 };
    readonly ToolTip tips = new ToolTip();
    Panel searchBox;
    List<NoteStore.Note> notes = new List<NoteStore.Note>();
    string path;                 // the open note's file (null until it has any text)
    bool loading, styling, fullScreen;
    FormWindowState wasState;
    Rectangle wasBounds;
    Color bg, sideBg, fore, dim, accent, surface;
    Font body, bold, italic, boldItalic, mono, h1, h2, h3, h4;

    class Snap { public string Text; public int Caret; }
    readonly List<Snap> undo = new List<Snap>(), redo = new List<Snap>();
    Snap committed = new Snap { Text = "" };
    DateTime lastEdit;

    public NotesWindow(float s)
    {
        S = s;
        Text = "Notes · Upbar";
        Icon = TopBar.AppIcon;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(wa.Width - 80, (int)(1100 * S)), Math.Min(wa.Height - 80, (int)(720 * S)));
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
        KeyPreview = true;
        Colors();
        string family = "Segoe UI";
        body = new Font(family, 11f); bold = new Font(family, 11f, FontStyle.Bold); italic = new Font(family, 11f, FontStyle.Italic); boldItalic = new Font(family, 11f, FontStyle.Bold | FontStyle.Italic);
        h1 = new Font(family, 20f, FontStyle.Bold); h2 = new Font(family, 16f, FontStyle.Bold); h3 = new Font(family, 13.5f, FontStyle.Bold); h4 = new Font(family, 11.5f, FontStyle.Bold);
        mono = new Font(Installed("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 10f);

        side.Width = (int)(250 * S);
        side.Padding = new Padding((int)(10 * S));
        var top = new Panel { Dock = DockStyle.Top, Height = (int)(40 * S) };
        var newBtn = Tool("", "New note (Ctrl+N)", NewNote);
        newBtn.Dock = DockStyle.Right;
        search.Dock = DockStyle.Fill;
        search.Font = body;
        var searchBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding((int)(12 * S), (int)(10 * S), (int)(10 * S), (int)(6 * S)) };
        searchBox.Paint += (a, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var p = Native.Round(new RectangleF(2 * S, 3 * S, searchBox.Width - 6 * S, searchBox.Height - 6 * S), 8 * S)) Native.Fill(e.Graphics, p, surface); };
        this.searchBox = searchBox;
        searchBox.Controls.Add(search);
        top.Controls.Add(searchBox);
        top.Controls.Add(newBtn);
        list.Dock = DockStyle.Fill;
        list.ItemHeight = (int)(52 * S);
        list.DrawItem += DrawNote;
        list.SelectedIndexChanged += (a, b) => { if (!loading && list.SelectedIndex >= 0) Open(((NoteStore.Note)list.SelectedItem).Path); };
        search.TextChanged += (a, b) => Fill();
        side.Controls.Add(list);
        side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = (int)(8 * S) });
        side.Controls.Add(top);

        tools.Height = (int)(44 * S);
        tools.Padding = new Padding((int)(14 * S), (int)(6 * S), (int)(10 * S), (int)(6 * S));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        foreach (var b in new[] {
            Tool("", "Heading (Ctrl+1/2/3)", () => Heading(2)), Tool("", "Bold (Ctrl+B)", () => Wrap("**")), Tool("", "Italic (Ctrl+I)", () => Wrap("*")),
            Tool("", "Strikethrough", () => Wrap("~~")), Tool("", "Code", () => Wrap("`")), Tool("", "Link (Ctrl+K)", Link),
            Tool("", "Bulleted list", () => Prefix("- ")), Tool("", "Task list", () => Prefix("- [ ] ")), Tool("", "Table", InsertTable),
            Tool("", "Image", PickImage) }) left.Controls.Add(b);
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        right.Controls.Add(Tool("", "Full screen (F11)", ToggleFullScreen));
        right.Controls.Add(Tool("", "Preview (Ctrl+E)", TogglePreview));
        right.Controls.Add(Tool("", "Delete note (to the Recycle Bin)", DeleteNote));
        tools.Controls.Add(left);
        tools.Controls.Add(right);

        status.Height = (int)(24 * S);
        status.Padding = new Padding(0, 0, (int)(14 * S), 0);
        status.Font = new Font(family, 8.5f);
        ed.Dock = DockStyle.Fill;
        ed.Font = body;
        view.Body = body; view.Bold = bold; view.Italic = italic; view.Mono = mono; view.H1 = h1; view.H2 = h2; view.H3 = h3; view.H4 = h4;
        view.ToggleTask = ToggleTask;
        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(ed);
        main.Controls.Add(view);
        main.Controls.Add(status);
        main.Controls.Add(tools);
        Controls.Add(main);
        Controls.Add(side);
        ApplyColors();

        ed.TextChanged += Edited;
        ed.KeyDown += EdKey;
        ed.DragEnter += (a, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        ed.DragDrop += Dropped;
        ed.Resize += (a, b) => Margins();
        saveTimer.Tick += (a, b) => SaveNow();
        styleTimer.Tick += (a, b) => { styleTimer.Stop(); Restyle(0, int.MaxValue); };
        FormClosing += (a, b) => SaveNow();
        FormClosed += (a, b) => { saveTimer.Dispose(); styleTimer.Dispose(); tips.Dispose(); };

        notes = NoteStore.All();
        Fill();
        if (notes.Count > 0) Open(notes[0].Path); else NewNote();
    }

    static bool Installed(string family) { using (var f = new Font(family, 10f)) return f.Name == family; }

    void Colors()
    {
        bool light = Theme.Light;
        bg = light ? Color.FromArgb(251, 251, 252) : Color.FromArgb(24, 24, 28);
        sideBg = light ? Color.FromArgb(243, 243, 246) : Color.FromArgb(31, 31, 36);
        fore = light ? Color.FromArgb(30, 30, 34) : Color.FromArgb(232, 232, 236);
        dim = light ? Color.FromArgb(120, 120, 128) : Color.FromArgb(140, 140, 150);
        surface = light ? Color.FromArgb(236, 236, 240) : Color.FromArgb(42, 42, 48);
        accent = Theme.Accent;
    }

    void ApplyColors()
    {
        BackColor = bg;
        side.BackColor = list.BackColor = sideBg;
        search.BackColor = surface; search.ForeColor = fore;
        if (searchBox != null) searchBox.BackColor = sideBg;
        tools.BackColor = bg;
        foreach (Control c in tools.Controls) c.BackColor = bg;
        ed.BackColor = bg; ed.ForeColor = fore;
        view.BackColor = bg; view.Fore = fore; view.Dim = dim; view.Accent = accent; view.Surface = surface; view.Line = Color.FromArgb(50, dim);
        status.BackColor = bg; status.ForeColor = dim;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = Theme.Light ? 0 : 1, caption = sideBg.R | sideBg.G << 8 | sideBg.B << 16;
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);    // dark title bar in dark mode
        DwmSetWindowAttribute(Handle, 35, ref caption, 4); // title bar in the sidebar's colour: one surface
        SendMessage(ed.Handle, 0x452, IntPtr.Zero, IntPtr.Zero); // EM_SETUNDOLIMIT 0: our own undo (styling must not be undoable)
        var cue = Marshal.StringToHGlobalUni("Search notes");
        SendMessage(search.Handle, 0x1501, (IntPtr)1, cue); // EM_SETCUEBANNER: hint text in the empty search field
        Marshal.FreeHGlobal(cue);
    }

    Button Tool(string glyph, string tip, Action click)
    {
        var b = new Button { Text = glyph, Font = new Font("Segoe Fluent Icons", 11f), FlatStyle = FlatStyle.Flat, Size = new Size((int)(34 * S), (int)(32 * S)), Margin = new Padding((int)(2 * S), 0, 0, 0), Cursor = Cursors.Hand, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(Theme.Light ? 20 : 30, Theme.Light ? 0 : 255, Theme.Light ? 0 : 255, Theme.Light ? 0 : 255);
        b.ForeColor = Theme.Light ? Color.FromArgb(60, 60, 66) : Color.FromArgb(210, 210, 216);
        b.Click += (a, e) => { click(); if (!view.Visible) ed.Focus(); };
        tips.SetToolTip(b, tip);
        return b;
    }

    // ---- history list
    void Fill()
    {
        loading = true;
        string q = search.Text.Trim();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var n in notes) if (q.Length == 0 || n.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || Contains(n.Path, q)) list.Items.Add(n);
        for (int i = 0; i < list.Items.Count; i++) if (((NoteStore.Note)list.Items[i]).Path == path) list.SelectedIndex = i;
        list.EndUpdate();
        loading = false;
    }

    static bool Contains(string file, string q) { try { return File.ReadAllText(file).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0; } catch (Exception) { return false; } } // search inside notes too

    void DrawNote(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var n = (NoteStore.Note)list.Items[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var b = new SolidBrush(sideBg)) g.FillRectangle(b, e.Bounds);
        bool sel = n.Path == path;
        var r = new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 4, e.Bounds.Height - 6);
        if (sel) using (var p = Native.Round(r, 8 * S)) Native.Fill(g, p, Color.FromArgb(Theme.Light ? 34 : 60, accent));
        var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using (var b = new SolidBrush(fore)) g.DrawString(n.Title, sel ? bold : body, b, new RectangleF(r.X + 10 * S, r.Y + 6 * S, r.Width - 20 * S, 22 * S), fmt);
        using (var b = new SolidBrush(dim)) g.DrawString(When(n.Modified), status.Font, b, new RectangleF(r.X + 10 * S, r.Y + 26 * S, r.Width - 20 * S, 18 * S), fmt);
    }

    static string When(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "Just now";
        if (t.Date == DateTime.Today) return t.ToString("t");
        if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday, " + t.ToString("t");
        return t.ToString(t.Year == DateTime.Now.Year ? "MMM d" : "MMM d, yyyy");
    }

    // ---- open / new / save / delete
    public void OpenNote(string file) { Open(file); }

    void Open(string file)
    {
        SaveNow();
        string text;
        try { text = File.ReadAllText(file, Encoding.UTF8); }
        catch (Exception) { return; }
        path = file;
        Load(text);
    }

    public void NewNote()
    {
        SaveNow();
        path = null;
        Load("");
        list.Invalidate();
    }

    void Load(string text)
    {
        loading = true;
        ed.Text = text.Replace("\r\n", "\n");
        ed.SelectionStart = 0;
        loading = false;
        undo.Clear(); redo.Clear();
        committed = new Snap { Text = ed.Text };
        Restyle(0, int.MaxValue);
        if (view.Visible) view.SetText(ed.Text);
        status.Text = path == null ? "New note" : "Saved";
        list.Invalidate();
    }

    void SaveNow()
    {
        saveTimer.Stop();
        if (ed.IsDisposed) return;
        string text = ed.Text;
        if (path == null && text.Trim().Length == 0) return; // nothing written yet: no empty files
        try
        {
            if (path != null && text.Trim().Length == 0) { NoteStore.Recycle(path); path = null; } // emptied: it goes away (restorable from the Recycle Bin)
            else { if (path == null) path = NoteStore.NewPath(); NoteStore.Save(path, text); }
            status.Text = "Saved";
        }
        catch (Exception) { status.Text = "Couldn't save: will try again"; saveTimer.Start(); return; } // disk full, file locked: keep the text and retry
        notes = NoteStore.All();
        Fill();
    }

    void DeleteNote()
    {
        if (path == null) { NewNote(); return; }
        saveTimer.Stop();
        NoteStore.Recycle(path);
        path = null;
        notes = NoteStore.All();
        Fill();
        if (notes.Count > 0) Open(notes[0].Path); else NewNote();
    }

    // ---- editing
    void Edited(object s, EventArgs e)
    {
        if (loading || styling) return;
        string cur = ed.Text;
        int caret = ed.SelectionStart;
        bool boundary = (DateTime.Now - lastEdit).TotalMilliseconds > 800 || caret > 0 && caret <= cur.Length && (cur[caret - 1] == ' ' || cur[caret - 1] == '\n');
        if (boundary && cur != committed.Text) { undo.Add(committed); if (undo.Count > 300) undo.RemoveAt(0); committed = new Snap { Text = cur, Caret = caret }; redo.Clear(); }
        lastEdit = DateTime.Now;
        int line = ed.GetLineFromCharIndex(caret);
        string l = LineText(line);
        if (Md.Fence.IsMatch(l) || line > 0 && Md.Fence.IsMatch(LineText(line - 1))) { styleTimer.Stop(); styleTimer.Start(); } // a code fence changes everything below it
        else Restyle(Math.Max(0, line - 1), line + 1);
        status.Text = "Editing…";
        saveTimer.Stop();
        saveTimer.Start();
    }

    string LineText(int line) { var ls = ed.Lines; return line >= 0 && line < ls.Length ? ls[line] : ""; }

    void Restore(Snap s)
    {
        loading = true;
        ed.Text = s.Text;
        ed.SelectionStart = Math.Min(s.Caret, ed.TextLength);
        loading = false;
        Restyle(0, int.MaxValue);
        saveTimer.Stop(); saveTimer.Start();
    }

    void Undo()
    {
        string cur = ed.Text;
        if (cur != committed.Text) undo.Add(committed);
        if (undo.Count == 0) return;
        redo.Add(new Snap { Text = cur, Caret = ed.SelectionStart });
        var s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
        committed = s;
        Restore(s);
    }

    void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(new Snap { Text = ed.Text, Caret = ed.SelectionStart });
        var s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
        committed = s;
        Restore(s);
    }

    void Insert(string s) { ed.SelectedText = s; }

    void Wrap(string mark)
    {
        if (view.Visible) return;
        if (ed.SelectionLength > 0) { string t = ed.SelectedText; Insert(mark + t + mark); }
        else { int at = ed.SelectionStart; Insert(mark + mark); ed.SelectionStart = at + mark.Length; }
    }

    void Link()
    {
        if (view.Visible) return;
        string t = ed.SelectionLength > 0 ? ed.SelectedText : "link";
        int at = ed.SelectionStart;
        Insert("[" + t + "](https://)");
        ed.SelectionStart = at + t.Length + 3;
        ed.SelectionLength = 8;
    }

    // Puts (or toggles) a prefix at the start of the caret's line: list marks, task boxes, heading marks.
    void Prefix(string p)
    {
        if (view.Visible) return;
        int line = ed.GetLineFromCharIndex(ed.SelectionStart), start = ed.GetFirstCharIndexFromLine(line);
        string l = LineText(line);
        ed.Select(start, l.Length);
        Insert(l.StartsWith(p) ? l.Substring(p.Length) : p + Regex.Replace(l, @"^(#{1,6}\s+|[-*+]\s+(\[[ xX]\]\s+)?|\d+[.)]\s+)", ""));
    }

    void Heading(int level)
    {
        if (view.Visible) return;
        int line = ed.GetLineFromCharIndex(ed.SelectionStart), start = ed.GetFirstCharIndexFromLine(line);
        string l = LineText(line), mark = new string('#', level) + " ";
        var m = Md.Heading.Match(l);
        ed.Select(start, l.Length);
        Insert(m.Success && m.Groups[1].Length == level ? m.Groups[2].Value : mark + (m.Success ? m.Groups[2].Value : l));
    }

    void InsertTable()
    {
        if (view.Visible) return;
        var t = Md.FormatTable(new List<string> { "| Column | Column |", "| --- | --- |", "|  |  |" });
        int line = ed.GetLineFromCharIndex(ed.SelectionStart);
        string lead = LineText(line).Trim().Length > 0 ? "\n\n" : "";
        ed.SelectionStart = ed.GetFirstCharIndexFromLine(line) + LineText(line).Length;
        int at = ed.SelectionStart + lead.Length;
        Insert(lead + string.Join("\n", t) + "\n");
        ed.Select(at + 2, 6); // "Column", ready to type over
    }

    void PickImage()
    {
        using (var d = new OpenFileDialog { Title = "Add an image", Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp" })
            if (d.ShowDialog(this) == DialogResult.OK) AddImages(new[] { d.FileName }, ed.SelectionStart);
    }

    void AddImages(string[] files, int at)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            if (!NoteStore.IsImage(f)) continue;
            try { sb.Append("![").Append(Path.GetFileNameWithoutExtension(f)).Append("](").Append(NoteStore.AddImage(f)).Append(")\n"); }
            catch (Exception) { status.Text = "Couldn't copy " + Path.GetFileName(f); }
        }
        if (sb.Length == 0) return;
        ed.SelectionStart = at;
        ed.SelectionLength = 0;
        Insert((at > 0 && ed.Text[at - 1] != '\n' ? "\n" : "") + sb);
    }

    void Dropped(object s, DragEventArgs e)
    {
        int at = ed.GetCharIndexFromPosition(ed.PointToClient(new Point(e.X, e.Y)));
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files != null) { AddImages(files, at); return; }
        var text = e.Data.GetData(DataFormats.UnicodeText) as string;
        if (text != null) { ed.SelectionStart = at; ed.SelectionLength = 0; Insert(text.Replace("\r\n", "\n")); }
    }

    // Paste is always plain text (or an image saved into the notes folder), never rich formatting.
    void Paste()
    {
        try
        {
            if (Clipboard.ContainsImage()) { using (var img = Clipboard.GetImage()) Insert("![image](" + NoteStore.AddImage(img) + ")\n"); }
            else if (Clipboard.ContainsFileDropList()) { var f = new string[Clipboard.GetFileDropList().Count]; Clipboard.GetFileDropList().CopyTo(f, 0); AddImages(f, ed.SelectionStart); }
            else if (Clipboard.ContainsText()) Insert(Clipboard.GetText().Replace("\r\n", "\n"));
        }
        catch (ExternalException) { } // clipboard busy
    }

    void ToggleTask(int line)
    {
        var ls = ed.Lines;
        if (line < 0 || line >= ls.Length) return;
        string l = ls[line], t = Regex.Replace(l, @"\[([ xX])\]", m => m.Groups[1].Value == " " ? "[x]" : "[ ]", RegexOptions.None);
        if (t == l) return;
        int start = ed.GetFirstCharIndexFromLine(line);
        ed.Select(start, l.Length);
        Insert(t);
        view.SetText(ed.Text);
    }

    // Enter continues lists and tables; Tab moves between table cells (lining the table up) or indents a list item.
    void EdKey(object s, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V || e.Shift && e.KeyCode == Keys.Insert) { Paste(); e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.Z && !e.Shift) { Undo(); e.SuppressKeyPress = true; return; }
        if (e.Control && (e.KeyCode == Keys.Y || e.Shift && e.KeyCode == Keys.Z)) { Redo(); e.SuppressKeyPress = true; return; }
        int line = ed.GetLineFromCharIndex(ed.SelectionStart), start = ed.GetFirstCharIndexFromLine(line);
        string l = LineText(line);
        if (e.KeyCode == Keys.Enter && !e.Shift && ed.SelectionLength == 0)
        {
            var m = Md.Item.Match(l);
            if (m.Success)
            {
                e.SuppressKeyPress = true;
                if (m.Groups[4].Value.Trim().Length == 0) { ed.Select(start, l.Length); Insert(""); return; } // empty item: the list ends
                string mark = m.Groups[2].Value;
                int n;
                if (int.TryParse(mark.TrimEnd('.', ')'), out n)) mark = (n + 1) + mark.Substring(mark.Length - 1);
                Insert("\n" + m.Groups[1].Value + mark + " " + (m.Groups[3].Success ? "[ ] " : ""));
                return;
            }
            if (Md.IsTableRow(l) && ed.SelectionStart == start + l.Length)
            {
                e.SuppressKeyPress = true;
                int cols = Md.Cells(l).Length;
                var empty = new string[cols];
                for (int i = 0; i < cols; i++) empty[i] = " ";
                int at = ed.SelectionStart;
                Insert("\n|" + string.Join("|", empty) + "|");
                ed.SelectionStart = at + 3;
                return;
            }
        }
        if (e.KeyCode == Keys.Tab && !e.Control)
        {
            e.SuppressKeyPress = true;
            if (Md.IsTableRow(l)) { TableTab(line, e.Shift); return; }
            if (Md.Item.IsMatch(l)) { ed.Select(start, l.Length); Insert(e.Shift ? (l.StartsWith("  ") ? l.Substring(2) : l.TrimStart()) : "  " + l); return; }
            Insert("    ");
        }
    }

    void TableTab(int line, bool back)
    {
        var ls = ed.Lines;
        int first = line, last = line;
        while (first > 0 && Md.IsTableRow(ls[first - 1])) first--;
        while (last + 1 < ls.Length && Md.IsTableRow(ls[last + 1])) last++;
        int cell = 0, caretCol = ed.SelectionStart - ed.GetFirstCharIndexFromLine(line);
        for (int i = 0; i < caretCol && i < ls[line].Length; i++) if (ls[line][i] == '|') cell++;
        var rows = new List<string>();
        for (int i = first; i <= last; i++) rows.Add(ls[i]);
        var fmt = Md.FormatTable(rows);
        int a = ed.GetFirstCharIndexFromLine(first), b = ed.GetFirstCharIndexFromLine(last) + ls[last].Length;
        ed.Select(a, b - a);
        Insert(string.Join("\n", fmt));
        int cols = Md.Cells(fmt[0]).Length, row = line - first;
        cell += back ? -1 : 1;
        if (cell > cols) { cell = 1; row++; }
        if (cell < 1) { cell = cols; row--; }
        if (row < fmt.Count && row >= 0 && Md.IsSeparator(Md.Cells(fmt[row]))) row += back ? -1 : 1;
        if (row >= fmt.Count) // past the last cell: a new row
        {
            var empty = new string[cols];
            for (int i = 0; i < cols; i++) empty[i] = "";
            var grown = Md.FormatTable(new List<string>(fmt) { "|" + string.Join("|", empty) + "|" });
            ed.Select(a, string.Join("\n", fmt).Length);
            Insert(string.Join("\n", grown));
            fmt = grown;
        }
        row = Math.Max(0, Math.Min(row, fmt.Count - 1));
        string target = fmt[row];
        int pipe = -1;
        for (int i = 0, seen = 0; i < target.Length; i++) if (target[i] == '|' && ++seen == cell) { pipe = i; break; }
        ed.SelectionStart = ed.GetFirstCharIndexFromLine(first + row) + Math.Max(0, pipe) + 2;
        ed.SelectionLength = 0;
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); Margins(); ed.Focus(); }

    protected override bool ProcessCmdKey(ref Message msg, Keys k)
    {
        switch (k)
        {
            case Keys.Control | Keys.N: NewNote(); return true;
            case Keys.Control | Keys.S: SaveNow(); return true;
            case Keys.Control | Keys.E: TogglePreview(); return true;
            case Keys.F11: ToggleFullScreen(); return true;
            case Keys.Escape: if (fullScreen) { ToggleFullScreen(); return true; } break;
            case Keys.Control | Keys.B: Wrap("**"); return true;
            case Keys.Control | Keys.I: Wrap("*"); return true;
            case Keys.Control | Keys.K: Link(); return true;
            case Keys.Control | Keys.D1: Heading(1); return true;
            case Keys.Control | Keys.D2: Heading(2); return true;
            case Keys.Control | Keys.D3: Heading(3); return true;
            case Keys.Control | Keys.F: if (!fullScreen) { search.Focus(); search.SelectAll(); return true; } break;
        }
        return base.ProcessCmdKey(ref msg, k);
    }

    void TogglePreview()
    {
        bool show = !view.Visible;
        if (show) view.SetText(ed.Text);
        view.Visible = show;
        ed.Visible = !show;
        if (!show) ed.Focus();
    }

    // Full screen: the note alone, edge to edge, in a centred column; F11 or Esc to come back.
    void ToggleFullScreen()
    {
        fullScreen = !fullScreen;
        if (fullScreen)
        {
            wasState = WindowState;
            wasBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;
            side.Visible = false;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = wasBounds;
            WindowState = wasState;
            side.Visible = true;
        }
        Margins();
    }

    // A readable column: the text never runs wider than ~80 characters, centred in whatever space there is.
    void Margins()
    {
        if (!ed.IsHandleCreated) return;
        int w = ed.ClientSize.Width, pad = Math.Max((int)(24 * S), (w - (int)(820 * S)) / 2);
        var r = new Native.RECT { l = pad, t = (int)(14 * S), r = w - pad, b = ed.ClientSize.Height };
        SendMessage(ed.Handle, 0xB3, IntPtr.Zero, ref r); // EM_SETRECT
    }

    // ---- live styling of the markdown source
    void Restyle(int fromLine, int toLine)
    {
        var ls = ed.Lines;
        if (ls.Length == 0) return;
        toLine = Math.Min(toLine, ls.Length - 1);
        bool code = false;
        for (int i = 0; i < fromLine && i < ls.Length; i++) if (Md.Fence.IsMatch(ls[i])) code = !code; // inside a code block where we start?
        styling = true;
        int ss = ed.SelectionStart, sl = ed.SelectionLength;
        var scroll = Point.Empty;
        SendMessage(ed.Handle, 0x4DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS
        SendMessage(ed.Handle, 0x0B, IntPtr.Zero, IntPtr.Zero);  // WM_SETREDRAW off: no flicker while styling
        try
        {
            for (int i = fromLine; i <= toLine; i++)
            {
                string l = ls[i];
                int start = ed.GetFirstCharIndexFromLine(i);
                if (start < 0) break;
                Style(start, l.Length, body, fore);
                bool fence = Md.Fence.IsMatch(l);
                if (fence || code) { Style(start, l.Length, mono, fence ? dim : fore); if (fence) code = !code; continue; }
                Match m;
                if ((m = Md.Heading.Match(l)).Success)
                {
                    int lv = m.Groups[1].Length;
                    Style(start, l.Length, lv == 1 ? h1 : lv == 2 ? h2 : lv == 3 ? h3 : h4, fore);
                    Style(start, lv, lv == 1 ? h1 : lv == 2 ? h2 : lv == 3 ? h3 : h4, dim);
                    continue;
                }
                if (Md.IsTableRow(l)) { Style(start, l.Length, mono, fore); for (int c = 0; c < l.Length; c++) if (l[c] == '|') Style(start + c, 1, mono, dim); continue; }
                if (Md.Rule.IsMatch(l)) { Style(start, l.Length, body, dim); continue; }
                if ((m = Md.Quote.Match(l)).Success) Style(start, l.Length, italic, dim);
                if ((m = Md.Item.Match(l)).Success) Style(start + m.Groups[1].Length, m.Groups[2].Length + (m.Groups[3].Success ? m.Groups[3].Length + 1 : 0), bold, accent);
                InlineStyle(l, start);
            }
        }
        finally
        {
            ed.Select(ss, sl);
            SendMessage(ed.Handle, 0x4DE, IntPtr.Zero, ref scroll); // EM_SETSCROLLPOS
            SendMessage(ed.Handle, 0x0B, (IntPtr)1, IntPtr.Zero);
            ed.Invalidate();
            styling = false;
        }
    }

    static readonly Regex InlineSrc = new Regex(@"(\*\*|__)(.+?)\1|(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?![\w*])|`[^`]+`|~~.+?~~|!?\[[^\]]*\]\([^)\s]*\)");
    void InlineStyle(string l, int start)
    {
        foreach (Match m in InlineSrc.Matches(l))
        {
            string v = m.Value;
            if (v.StartsWith("**") || v.StartsWith("__")) { Style(start + m.Index, m.Length, bold, fore); Style(start + m.Index, 2, bold, dim); Style(start + m.Index + m.Length - 2, 2, bold, dim); }
            else if (v.StartsWith("`")) Style(start + m.Index, m.Length, mono, accent);
            else if (v.StartsWith("~~")) Style(start + m.Index, m.Length, body, dim);
            else if (v.StartsWith("[") || v.StartsWith("![")) { int close = v.IndexOf("]("); Style(start + m.Index, close + 1, body, accent); Style(start + m.Index + close + 1, m.Length - close - 1, body, dim); }
            else { Style(start + m.Index, m.Length, italic, fore); }
        }
    }

    void Style(int start, int len, Font f, Color c)
    {
        if (len <= 0) return;
        ed.Select(start, len);
        ed.SelectionFont = f;
        ed.SelectionColor = c;
    }
}
