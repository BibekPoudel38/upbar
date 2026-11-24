using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// Samsung-style "tinted squircle" icons for shortcuts on the desktop and in the user's Start Menu.
// Each shortcut is pointed at a generated .ico; its original icon is recorded so Undo restores it exactly.
// shortcut: Store apps and all-users shortcuts are left alone (package icons / admin rights); apps that rewrite
// their shortcut on update lose the squircle until "Update" runs again.
static class Squircles
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath(StringBuilder file, int max, IntPtr fd, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(StringBuilder name, int max);
        void SetDescription(string name);
        void GetWorkingDirectory(StringBuilder dir, int max);
        void SetWorkingDirectory(string dir);
        void GetArguments(StringBuilder args, int max);
        void SetArguments(string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int type, width, height, widthBytes; public short planes, bitsPixel; public IntPtr bits; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr ctx, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(int ev, int flags, IntPtr a, IntPtr b);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")] static extern void Notify(int ev, int flags, string path, IntPtr b);
    static void Updated(string file) { Notify(0x2000, 0x5, file, IntPtr.Zero); } // SHCNE_UPDATEITEM: Explorer redraws this shortcut
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    // Next to the exe, like fonts\ and topbar.ini: always a real folder Explorer can read (and it travels with the app).
    static string Dir { get { return App.File("icons"); } }
    static string BackupFile { get { return Path.Combine(Dir, "backup.txt"); } } // shortcut|original icon path|index, one per line
    public static bool Applied { get { return File.Exists(BackupFile); } }

    static IEnumerable<string> Shortcuts()
    {
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) };
        // All-users folders need administrator rights; only touched when run elevated (topbar.exe --squircles).
        if (new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu));
        }
        foreach (var root in roots)
        {
            string[] files;
            try { files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files)
                if (f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) yield return f;
        }
    }

    // Gives every shortcut without a squircle one. Returns how many changed. Run on the UI (STA) thread.
    public static int Apply()
    {
        Directory.CreateDirectory(Dir);
        AllowPackagedApps();
        var backup = new List<string>(Applied ? File.ReadAllLines(BackupFile) : new string[0]);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in backup) done.Add(l.Split('|')[0]);
        int n = 0;
        foreach (var f in Shortcuts())
        {
            if (done.Contains(f)) continue;
            try
            {
                using (var icon = SourceIcon(f) ?? ShellIcon(f))
                {
                    if (icon == null || IsGeneric(icon)) continue; // no real icon available: leave this shortcut alone
                    string ico = Path.Combine(Dir, Hash(f) + "-v2.ico"); // new name per format change: the shell caches icons by path
                    using (var art = Render(icon)) WriteIco(art, ico);
                    string orig = f.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ? SetUrlIcon(f, ico, 0) : SetLnkIcon(f, ico, 0);
                    backup.Add(f + "|" + orig);
                    Updated(f);
                    File.WriteAllLines(BackupFile, backup); // after every shortcut: a crash never loses an original
                    n++;
                }
            }
            catch (Exception) { } // read-only, locked or broken shortcut: leave it as it is
        }
        if (backup.Count > 0) File.WriteAllLines(BackupFile, backup);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: refresh icon caches
        return n;
    }

    // Squircle art for any app or shortcut (used by the app grid). A shortcut that already wears our squircle is
    // returned as it is, so it never gets a squircle inside a squircle. null when the file has no icon.
    internal static Bitmap Art(string path, Color tint)
    {
        bool shortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        string file = null;
        var src = shortcut ? SourceIcon(path, out file) : null;
        // Judge by the icon the shortcut points at now, not backup.txt: app updates often reset their shortcuts.
        if (src != null && file != null && file.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) return src;
        using (var icon = src ?? ShellIcon(path))
            return icon == null ? null : Render(icon, tint);
    }

    // The Start menu is a packaged app: it may only read folders that grant "ALL APPLICATION PACKAGES" (S-1-15-2-1),
    // like Program Files does. Read-only, inherited by the icon files; no admin needed on a folder the user owns.
    static void AllowPackagedApps()
    {
        try
        {
            var sec = Directory.GetAccessControl(Dir);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier("S-1-15-2-1"),
                System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            Directory.SetAccessControl(Dir, sec);
        }
        catch (UnauthorizedAccessException) { } // not our folder to change: icons still work everywhere but Start
        catch (InvalidOperationException) { }
    }

    // Puts every original icon back and removes the generated ones.
    public static int Undo()
    {
        if (!Applied) return 0;
        int n = 0;
        foreach (var line in File.ReadAllLines(BackupFile))
        {
            var p = line.Split('|');
            if (p.Length != 3 || !File.Exists(p[0])) continue;
            int idx;
            int.TryParse(p[2], out idx);
            try
            {
                if (p[0].EndsWith(".url", StringComparison.OrdinalIgnoreCase)) SetUrlIcon(p[0], p[1], idx); else SetLnkIcon(p[0], p[1], idx);
                Updated(p[0]);
                n++;
            }
            catch (Exception) { }
        }
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        return n;
    }

    // Returns the previous "path|index".
    static string SetLnkIcon(string lnk, string icon, int index)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 2); // STGM_READWRITE
            var sb = new StringBuilder(1024);
            int old;
            link.GetIconLocation(sb, sb.Capacity, out old);
            link.SetIconLocation(icon, index);
            ((IPersistFile)link).Save(null, true);
            return sb + "|" + old;
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // Internet shortcuts are INI files: IconFile / IconIndex under [InternetShortcut]. Empty icon = remove the keys.
    static string SetUrlIcon(string url, string icon, int index)
    {
        var lines = new List<string>(File.ReadAllLines(url));
        string oldFile = "", oldIdx = "0";
        int section = lines.FindIndex(l => l.Trim().Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase));
        if (section < 0) { lines.Insert(0, "[InternetShortcut]"); section = 0; }
        for (int i = lines.Count - 1; i > section; i--)
        {
            if (lines[i].StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) { oldFile = lines[i].Substring(9); lines.RemoveAt(i); }
            else if (lines[i].StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) { oldIdx = lines[i].Substring(10); lines.RemoveAt(i); }
        }
        if (icon.Length > 0) { lines.Insert(section + 1, "IconIndex=" + index); lines.Insert(section + 1, "IconFile=" + icon); }
        File.WriteAllLines(url, lines);
        return oldFile + "|" + oldIdx;
    }

    // 256px icon with its real alpha (Image.FromHbitmap would drop it).
    internal static Bitmap ShellIcon(string path) { return ShellIcon(path, 256); }
    internal static Bitmap ShellIcon(string path, int px)
    {
        IShellItemImageFactory f;
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
        IntPtr h;
        if (f == null || f.GetImage(new SIZE { cx = px, cy = px }, 0x4, out h) != 0 || h == IntPtr.Zero) return null; // SIIGBF_ICONONLY
        try
        {
            var bm = new BITMAP();
            GetObject(h, Marshal.SizeOf(typeof(BITMAP)), ref bm);
            if (bm.bits == IntPtr.Zero || bm.bitsPixel != 32) return null;
            var b = new Bitmap(bm.width, bm.height, PixelFormat.Format32bppPArgb);
            var d = b.LockBits(new Rectangle(0, 0, bm.width, bm.height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var row = new byte[bm.widthBytes];
            for (int y = 0; y < bm.height; y++)
            {
                Marshal.Copy(bm.bits + (bm.height - 1 - y) * bm.widthBytes, row, 0, row.Length); // DIB rows are bottom-up
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, row.Length);
            }
            b.UnlockBits(d);
            return b;
        }
        finally { DeleteObject(h); }
    }

    // Tinted squircle: a soft pastel of the app's own colour, glyph at 60% like One UI.
    static Bitmap Render(Bitmap icon) { return Render(icon, Color.Empty); }

    // tint: squircle colour; Empty = a pastel of the app's own colour.
    static Bitmap Render(Bitmap icon, Color tint)
    {
        var b = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var box = new RectangleF(6, 6, 244, 244);
            using (var p = Shape(box)) using (var fill = new SolidBrush(tint.IsEmpty ? Tint(icon) : tint)) g.FillPath(fill, p);
            // Fit the visible part of the icon (small-only icons arrive padded with empty space) into 60% of the tile.
            var src = Content(icon);
            float glyph = box.Width * 0.6f, k = Math.Min(glyph / src.Width, glyph / src.Height), w = src.Width * k, h = src.Height * k;
            g.DrawImage(icon, new RectangleF(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h), src, GraphicsUnit.Pixel);
        }
        return b;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, IntPtr[] ids, uint n, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    // The icon straight from the shortcut's icon file (or its target), scaled to 256 by Windows. Unlike the shell's
    // big image this has no padding frame around small-only icons. null when there's no usable icon file.
    static Bitmap SourceIcon(string shortcut) { string f; return SourceIcon(shortcut, out f); }

    static Bitmap SourceIcon(string shortcut, out string file)
    {
        file = null;
        int index = 0;
        if (shortcut.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var l in File.ReadAllLines(shortcut))
            {
                if (l.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) file = l.Substring(9);
                else if (l.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) int.TryParse(l.Substring(10), out index);
            }
        }
        else
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(shortcut, 0);
                var sb = new StringBuilder(1024);
                link.GetIconLocation(sb, sb.Capacity, out index);
                file = sb.ToString();
                if (file.Length == 0) { sb.Length = 0; link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0); file = sb.ToString(); index = 0; }
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        if (string.IsNullOrEmpty(file)) return null;
        file = Environment.ExpandEnvironmentVariables(file);
        if (!File.Exists(file)) return null;
        var h = new IntPtr[1];
        var ids = new IntPtr[1];
        if (PrivateExtractIcons(file, index, 256, 256, h, ids, 1, 0) != 1 || h[0] == IntPtr.Zero) return null;
        try { using (var ic = Icon.FromHandle(h[0])) return ic.ToBitmap(); }
        finally { DestroyIcon(h[0]); }
    }

    // Bounds of the icon's visible pixels.
    public static RectangleF Content(Bitmap b)
    {
        int l = b.Width, t = b.Height, r = -1, bt = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).A > 24) { if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > bt) bt = y; }
        return r < 0 ? new RectangleF(0, 0, b.Width, b.Height) : new RectangleF(l, t, r - l + 1, bt - t + 1);
    }

    // The shell returns its generic "blank file" picture when it can't reach an app's real icon.
    // Compare against what it gives for a file type nobody has registered.
    static long genericSig;
    static bool IsGeneric(Bitmap b)
    {
        if (genericSig == 0)
        {
            string probe = Path.Combine(Path.GetTempPath(), "squircle-probe.zzqx");
            try { File.WriteAllText(probe, ""); using (var g = ShellIcon(probe)) genericSig = g == null ? -1 : Sig(g); }
            catch (Exception) { genericSig = -1; }
            finally { try { File.Delete(probe); } catch (IOException) { } }
        }
        return Sig(b) == genericSig;
    }

    static long Sig(Bitmap b)
    {
        long h = 17;
        using (var s = new Bitmap(b, 16, 16))
            for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) h = h * 31 + (s.GetPixel(x, y).ToArgb() & unchecked((int)0xF0F0F0F0)); // coarse: ignore tiny resampling noise
        return h;
    }

    // Samsung One UI squircle: superellipse |x|^n + |y|^n = 1, n ~ 4.2.
    // Upbar's icon: a bold white U on a teal-to-indigo squircle with a soft light across the top.
    public static Bitmap Monogram(int px)
    {
        var b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float m = px * 0.03f;
            var box = new RectangleF(m, m, px - 2 * m, px - 2 * m);
            using (var p = Shape(box))
            {
                using (var fill = new LinearGradientBrush(box, Color.FromArgb(20, 184, 166), Color.FromArgb(79, 70, 229), 45f)) g.FillPath(fill, p);
                var st = g.Save();
                g.SetClip(new RectangleF(box.X, box.Y, box.Width, box.Height * 0.5f), CombineMode.Intersect);
                using (var sheen = new LinearGradientBrush(new RectangleF(box.X, box.Y - 1, box.Width, box.Height * 0.5f + 2), Color.FromArgb(60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f)) g.FillPath(sheen, p);
                g.Restore(st);
            }
            using (var f = new Font("Segoe UI", px * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("U", f, Brushes.White, new RectangleF(0, px * 0.03f, px, px), sf);
        }
        return b;
    }

    public static GraphicsPath Shape(RectangleF r)
    {
        var pts = new PointF[180];
        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = r.Width / 2, n = 4.2f;
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length, c = Math.Cos(t), s = Math.Sin(t);
            pts[i] = new PointF(cx + a * (float)(Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n)), cy + a * (float)(Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / n)));
        }
        var path = new GraphicsPath();
        path.AddPolygon(pts);
        return path;
    }

    // Average of the icon's opaque pixels, lifted 78% toward white.
    public static Color Tint(Bitmap icon)
    {
        long r = 0, g = 0, bl = 0, n = 0;
        for (int y = 0; y < icon.Height; y += 4)
            for (int x = 0; x < icon.Width; x += 4)
            {
                var c = icon.GetPixel(x, y);
                if (c.A > 200) { r += c.R; g += c.G; bl += c.B; n++; }
            }
        if (n == 0) return Color.FromArgb(245, 245, 247);
        if ((r + g + bl) / (3 * n) > 225) return Color.FromArgb(38, 38, 42); // white artwork: dark squircle so it stays visible
        return Color.FromArgb((int)(r / n + (255 - r / n) * 0.78), (int)(g / n + (255 - g / n) * 0.78), (int)(bl / n + (255 - bl / n) * 0.78));
    }

    // Multi-size .ico. Only the 256px entry may be PNG: the shell's icon loader reads the smaller sizes
    // (the ones Explorer actually shows) only as classic 32-bit DIBs, and shows a blank icon otherwise.
    public static void WriteIco(Bitmap art, string path)
    {
        int[] sizes = { 256, 64, 48, 32, 16 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var b = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(art, 0, 0, s, s); }
                if (s >= 256) using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
                else pngs.Add(Dib(b));
            }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length); // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++) // ICONDIRENTRY
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }

    // ICO image as BITMAPINFOHEADER + bottom-up BGRA pixels + an all-zero AND mask (alpha does the masking).
    static byte[] Dib(Bitmap b)
    {
        int s = b.Width, maskRow = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); // header: height covers image + mask
            w.Write(0); w.Write(s * s * 4 + maskRow * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var d = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[s * 4];
            for (int y = s - 1; y >= 0; y--) { Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length); w.Write(row); }
            b.UnlockBits(d);
            w.Write(new byte[maskRow * s]);
            return ms.ToArray();
        }
    }

    // Stable file name per shortcut (string.GetHashCode is not guaranteed across runs).
    static string Hash(string s)
    {
        uint h = 2166136261;
        foreach (char ch in s.ToLowerInvariant()) { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }
}

// Custom Windows cursor themes generated in pure GDI+ as multi-size 32-bit ARGB .cur files and loaded into Windows'
// hardware cursor plane via SPI_SETCURSORS (0% runtime CPU, 0 bytes runtime RAM). Original values are backed up first.
static class CustomCursors
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr v, uint ini);
    const uint SPI_SETCURSORS = 0x0057;
    const string RegPath = @"Control Panel\Cursors";
    public static readonly string[] Names = { "Original", "Dark", "Accent", "Minimal", "Neon" };
    static readonly string[] Roles = { "Arrow", "Hand", "IBeam", "Wait", "AppStarting", "Crosshair", "SizeWE", "SizeNS", "SizeNWSE", "SizeNESW", "SizeAll", "No" };

    static string Dir { get { return App.File("cursors"); } }
    static string BackupFile { get { return Path.Combine(Dir, "backup.txt"); } }

    public static void Apply(int style, Color accent)
    {
        if (style <= 0) { Undo(); return; }
        try
        {
            Directory.CreateDirectory(Dir);
            SaveBackupOnce();
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath))
            {
                foreach (var role in Roles)
                {
                    int hx, hy;
                    string curPath = Path.Combine(Dir, role.ToLowerInvariant() + "-s" + style + ".cur");
                    using (var bmp = DrawRole(role, style, accent, out hx, out hy))
                        WriteCur(bmp, hx, hy, curPath);
                    k.SetValue(role, curPath, Microsoft.Win32.RegistryValueKind.ExpandString);
                }
            }
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 3); // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
        }
        catch (Exception) { }
    }

    static void SaveBackupOnce()
    {
        if (File.Exists(BackupFile)) return;
        var lines = new List<string>();
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath))
        {
            if (k != null)
                foreach (var role in Roles)
                {
                    var v = k.GetValue(role, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    lines.Add(role + "|" + (v ?? ""));
                }
        }
        File.WriteAllLines(BackupFile, lines);
    }

    public static void Undo()
    {
        try
        {
            if (File.Exists(BackupFile))
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath))
                    foreach (var line in File.ReadAllLines(BackupFile))
                    {
                        var p = line.Split(new[] { '|' }, 2);
                        if (p.Length == 2) k.SetValue(p[0], p[1], Microsoft.Win32.RegistryValueKind.ExpandString);
                    }
                File.Delete(BackupFile);
            }
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 3);
        }
        catch (Exception) { }
    }

    // Draws a role on a 128x128 supersampled canvas; hotX/hotY are in 128x128 coordinates.
    static Bitmap DrawRole(string role, int style, Color accent, out int hotX, out int hotY)
    {
        var b = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        bool borderless = style == 3; // Minimal: tailless, borderless, dark colour
        Color dark = Color.FromArgb(248, 22, 22, 26);
        Color body = style == 2 ? accent : style == 3 ? dark : style == 4 ? Color.FromArgb(235, 14, 15, 22) : dark;
        Color rim = borderless ? Color.Empty : style == 4 ? Theme.Mix(accent, Color.White, 0.28f) : style == 2 && Native.Luma(accent) > 0.6f ? dark : Color.White;
        Color badge = style == 3 ? dark : accent;
        hotX = 64; hotY = 64;
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            switch (role)
            {
                case "Arrow":
                    hotX = 16; hotY = 12;
                    DrawPointer(g, style, body, rim, badge, false, false);
                    break;
                case "Hand":
                    hotX = 16; hotY = 12;
                    DrawPointer(g, style, body, rim, badge, true, false);
                    break;
                case "AppStarting":
                    hotX = 16; hotY = 12;
                    DrawPointer(g, style, body, rim, badge, false, true);
                    break;
                case "Wait":
                    DrawSpinner(g, 64, 64, 36, 11, body, rim, badge);
                    break;
                case "IBeam":
                    DrawIBeam(g, body, rim, badge);
                    break;
                case "Crosshair":
                    DrawCrosshair(g, body, rim, badge);
                    break;
                case "SizeWE":
                    DrawResize(g, 0, body, rim, badge);
                    break;
                case "SizeNS":
                    DrawResize(g, 90, body, rim, badge);
                    break;
                case "SizeNWSE":
                    DrawResize(g, 45, body, rim, badge);
                    break;
                case "SizeNESW":
                    DrawResize(g, -45, body, rim, badge);
                    break;
                case "SizeAll":
                    DrawResize(g, 0, body, rim, badge);
                    DrawResize(g, 90, body, rim, badge);
                    break;
                case "No":
                    DrawNo(g, body, rim);
                    break;
            }
        }
        return b;
    }

    // Smooth Bezier-rounded polygon (rounds each vertex i by pulling back `rad[i]` pixels along its two adjacent edges).
    static GraphicsPath RoundedPoly(PointF[] v, float[] rad)
    {
        var p = new GraphicsPath();
        int n = v.Length;
        for (int i = 0; i < n; i++)
        {
            var prev = v[(i + n - 1) % n];
            var cur = v[i];
            var next = v[(i + 1) % n];
            float dx1 = prev.X - cur.X, dy1 = prev.Y - cur.Y, l1 = (float)Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            float dx2 = next.X - cur.X, dy2 = next.Y - cur.Y, l2 = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);
            float r = Math.Min(rad[i], Math.Min(l1, l2) * 0.45f);
            var a = new PointF(cur.X + dx1 / l1 * r, cur.Y + dy1 / l1 * r);
            var b = new PointF(cur.X + dx2 / l2 * r, cur.Y + dy2 / l2 * r);
            var c1 = new PointF(a.X + (cur.X - a.X) * 0.65f, a.Y + (cur.Y - a.Y) * 0.65f);
            var c2 = new PointF(b.X + (cur.X - b.X) * 0.65f, b.Y + (cur.Y - b.Y) * 0.65f);
            p.AddBezier(a, c1, c2, b);
        }
        p.CloseFigure();
        return p;
    }

    static void DrawPointer(Graphics g, int style, Color body, Color rim, Color badge, bool linkBadge, bool busyRing)
    {
        // Tailless sculpted dart: crisp tip at (16, 12), rounded wings and smooth concave notch at (43, 68).
        var pts = new[]
        {
            new PointF(16, 12),
            new PointF(16, 90),
            new PointF(43, 68),
            new PointF(80, 64)
        };
        var rads = new[] { 3.5f, 11f, 12f, 11f };
        using (var path = RoundedPoly(pts, rads))
        {
            var st = g.Save();
            g.TranslateTransform(2.5f, 4f);
            using (var sh = new SolidBrush(Color.FromArgb(50, 0, 0, 0))) g.FillPath(sh, path);
            g.Restore(st);
            if (style == 4) // Neon outer glow
                using (var glow = new Pen(Color.FromArgb(115, badge), 14) { LineJoin = LineJoin.Round }) g.DrawPath(glow, path);
            using (var fill = new SolidBrush(body)) g.FillPath(fill, path);
            if (!rim.IsEmpty)
                using (var pen = new Pen(rim, 5.5f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
        }
        if (linkBadge)
        {
            using (var sh = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillEllipse(sh, 69, 21, 32, 32);
            using (var fill = new SolidBrush(badge)) g.FillEllipse(fill, 67, 18, 32, 32);
            if (!rim.IsEmpty) using (var pen = new Pen(rim, 4.5f)) g.DrawEllipse(pen, 67, 18, 32, 32);
        }
        if (busyRing) DrawSpinner(g, 88, 38, 20, 7f, body, rim, badge);
    }

    static void DrawSpinner(Graphics g, float cx, float cy, float r, float stroke, Color body, Color rim, Color badge)
    {
        using (var bg = new Pen(Color.FromArgb(80, body), stroke)) g.DrawEllipse(bg, cx - r, cy - r, 2 * r, 2 * r);
        using (var fg = new Pen(badge, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(fg, cx - r, cy - r, 2 * r, 2 * r, -90, 250);
    }

    static void DrawIBeam(Graphics g, Color body, Color rim, Color badge)
    {
        if (!rim.IsEmpty)
            using (var outline = new Pen( rim, 10) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(outline, 64, 24, 64, 104);
                g.DrawLine(outline, 48, 24, 80, 24);
                g.DrawLine(outline, 48, 104, 80, 104);
            }
        using (var inner = new Pen(body, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(inner, 64, 24, 64, 104);
            g.DrawLine(inner, 48, 24, 80, 24);
            g.DrawLine(inner, 48, 104, 80, 104);
        }
        using (var b = new SolidBrush(badge)) g.FillEllipse(b, 58, 58, 12, 12);
    }

    static void DrawCrosshair(Graphics g, Color body, Color rim, Color badge)
    {
        if (!rim.IsEmpty)
            using (var outline = new Pen(rim, 9.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(outline, 64, 18, 64, 48);
                g.DrawLine(outline, 64, 80, 64, 110);
                g.DrawLine(outline, 18, 64, 48, 64);
                g.DrawLine(outline, 80, 64, 110, 64);
            }
        using (var inner = new Pen(body, 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(inner, 64, 18, 64, 48);
            g.DrawLine(inner, 64, 80, 64, 110);
            g.DrawLine(inner, 18, 64, 48, 64);
            g.DrawLine(inner, 80, 64, 110, 64);
        }
        using (var b = new SolidBrush(badge)) g.FillEllipse(b, 59, 59, 10, 10);
    }

    static void DrawResize(Graphics g, float angle, Color body, Color rim, Color badge)
    {
        var st = g.Save();
        g.TranslateTransform(64, 64);
        g.RotateTransform(angle);
        if (!rim.IsEmpty)
            using (var outline = new Pen(rim, 11) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(outline, new[] { new PointF(-24, -16), new PointF(-42, 0), new PointF(-24, 16) });
                g.DrawLines(outline, new[] { new PointF(24, -16), new PointF(42, 0), new PointF(24, 16) });
            }
        using (var inner = new Pen(body, 6.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(inner, new[] { new PointF(-24, -16), new PointF(-42, 0), new PointF(-24, 16) });
            g.DrawLines(inner, new[] { new PointF(24, -16), new PointF(42, 0), new PointF(24, 16) });
        }
        using (var b = new SolidBrush(badge)) g.FillEllipse(b, -7, -7, 14, 14);
        g.Restore(st);
    }

    static void DrawNo(Graphics g, Color body, Color rim)
    {
        float cx = 64, cy = 64, r = 36;
        using (var sh = new Pen(Color.FromArgb(80, 0, 0, 0), 14)) g.DrawEllipse(sh, cx - r + 2, cy - r + 4, 2 * r, 2 * r);
        var red = Color.FromArgb(255, 95, 87);
        using (var pen = new Pen(red, 11))
        {
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            g.DrawLine(pen, cx - r * 0.7f, cy - r * 0.7f, cx + r * 0.7f, cy + r * 0.7f);
        }
    }

    // Writes a Windows .cur file (type = 2, with hotspot in ICONDIRENTRY) at 32x32 and 48x48 from a 128x128 source bitmap.
    public static void WriteCur(Bitmap art128, int hotX128, int hotY128, string path)
    {
        int[] sizes = { 32, 48 };
        var dibs = new List<byte[]>();
        var hxs = new short[sizes.Length];
        var hys = new short[sizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            hxs[i] = (short)Math.Max(0, Math.Min(s - 1, (int)Math.Round(hotX128 * s / 128.0)));
            hys[i] = (short)Math.Max(0, Math.Min(s - 1, (int)Math.Round(hotY128 * s / 128.0)));
            using (var b = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(art128, 0, 0, s, s); }
                dibs.Add(DibCur(b));
            }
        }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write((short)0); w.Write((short)2); w.Write((short)sizes.Length); // ICONDIR: type 2 = cursor
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)sizes[i]); w.Write((byte)sizes[i]);
                w.Write((byte)0); w.Write((byte)0);
                w.Write(hxs[i]); w.Write(hys[i]); // hotspot X, Y
                w.Write(dibs[i].Length); w.Write(offset);
                offset += dibs[i].Length;
            }
            foreach (var d in dibs) w.Write(d);
        }
    }

    static byte[] DibCur(Bitmap b)
    {
        int s = b.Width, maskStride = ((s + 31) / 32) * 4;
        var mask = new byte[maskStride * s];
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + mask.Length); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var d = b.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[s * 4];
            for (int y = s - 1, my = 0; y >= 0; y--, my++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                for (int x = 0; x < s; x++)
                    if (row[x * 4 + 3] == 0)
                    {
                        row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = 0;
                        mask[my * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7)); // transparent bit in 1-bit AND mask
                    }
                w.Write(row);
            }
            b.UnlockBits(d);
            w.Write(mask);
            return ms.ToArray();
        }
    }
}
