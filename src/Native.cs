using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cbMsg; public uint edge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int l, r, t, b; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int size, width, height; public short planes, bits; public int compression, sizeImage, xppm, yppm, clrUsed, clrImportant; }

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] public static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] static extern int SetDIBitsToDevice(IntPtr hdc, int x, int y, int w, int h, int sx, int sy, int start, int lines, IntPtr bits, ref BITMAPINFOHEADER bmi, int usage);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint c, out bool opaque);

    // Per-pixel alpha surface (see Blit), optionally over a Win11 system backdrop: type 1 none, 2 Mica, 3 Acrylic.
    // DWM ignores window regions for backdrops, so those windows use DWM's own rounded corners instead.
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    // DWM shows Acrylic/Mica only on windows it thinks are active; inactive ones get a flat grey fallback.
    // Our panels and widgets are rarely (or never) the active window, so keep their frame "active".
    // Call from WndProc for WM_NCACTIVATE (0x86) before the base handler.
    public static void StayActive(ref System.Windows.Forms.Message m) { if (m.Msg == 0x86) m.WParam = (IntPtr)1; }

    [StructLayout(LayoutKind.Sequential)] struct ACCENT { public int state, flags, color, anim; }
    [StructLayout(LayoutKind.Sequential)] struct WCADATA { public int attr; public IntPtr data; public int size; }
    [DllImport("user32.dll")] static extern bool SetWindowCompositionAttribute(IntPtr h, ref WCADATA d);
    public const int ClearBlur = -1, TintedBlur = -2; // Backdrop types: plain blur (Glass) / blur under a tint colour (widgets in Acrylic and Mica)

    // Blur-behind with no tint (accent state 3), or off (0). Unlike Acrylic it has no milky layer of its own,
    // so Glass can be as see-through as it likes; it also doesn't care whether the window is active.
    static void Accent(IntPtr h, int state, int color)
    {
        var a = new ACCENT { state = state, color = color };
        var mem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ACCENT)));
        try
        {
            Marshal.StructureToPtr(a, mem, false);
            var d = new WCADATA { attr = 19, data = mem, size = Marshal.SizeOf(typeof(ACCENT)) }; // WCA_ACCENT_POLICY
            SetWindowCompositionAttribute(h, ref d);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    // tint (TintedBlur only): AABBGGRR. Blur accents follow the window region and get no system shadow, unlike
    // the system backdrops, which DWM rounds itself (8px) and gives a drop shadow.
    public static void Backdrop(IntPtr h, int type, bool dark, bool round, int tint = 0)
    {
        Accent(h, type == ClearBlur ? 3 : type == TintedBlur ? 4 : 0, tint);
        if (type < 0) type = 1; // no system backdrop under a blur accent
        int darkOn = dark ? 1 : 0, corner = round ? 2 : 1, noBorder = unchecked((int)0xFFFFFFFE), backdrop = type;
        DwmSetWindowAttribute(h, 20, ref darkOn, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(h, 33, ref corner, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE: round (blur) / none (we use a region)
        DwmSetWindowAttribute(h, 34, ref noBorder, 4); // DWMWA_BORDER_COLOR = none: no square outline
        DwmSetWindowAttribute(h, 38, ref backdrop, 4); // DWMWA_SYSTEMBACKDROP_TYPE
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
    }

    // WM_NCACTIVATE: an "active" frame keeps Acrylic live, but DWM also draws a faint light edge along the top of an
    // active borderless window, so only windows that need the live material ask for it.
    public static void ActiveFrame(IntPtr h, bool on) { SendMessage(h, 0x86, (IntPtr)(on ? 1 : 0), IntPtr.Zero); }

    // Copies premultiplied ARGB pixels straight to the window so DWM sees real per-pixel alpha.
    public static void Blit(Graphics target, Bitmap bmp)
    {
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var bi = new BITMAPINFOHEADER { size = 40, width = bmp.Width, height = -bmp.Height, planes = 1, bits = 32 };
        IntPtr hdc = target.GetHdc();
        SetDIBitsToDevice(hdc, 0, 0, bmp.Width, bmp.Height, 0, 0, 0, bmp.Height, d.Scan0, ref bi, 0);
        target.ReleaseHdc(hdc);
        bmp.UnlockBits(d);
    }

    // 240+ FPS SWAR (2-multiply) per-pixel alpha fade + vertical slide directly to the window DC with zero GDI+ overhead.
    public static void BlitFade(Graphics target, Bitmap bmp, float alpha, int dy, ref int[] scratch)
    {
        int w = bmp.Width, h = bmp.Height, total = w * h;
        if (scratch == null || scratch.Length < total) scratch = new int[total];
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        uint a = (uint)Math.Max(0, Math.Min(256, (int)(alpha * 256f)));
        int copyH = h - Math.Abs(dy);
        Array.Clear(scratch, 0, total);
        if (copyH > 0 && a > 0)
        {
            int srcY = dy < 0 ? -dy : 0, dstY = dy > 0 ? dy : 0, count = copyH * w;
            Marshal.Copy(d.Scan0 + srcY * d.Stride, scratch, dstY * w, count);
            if (a < 256)
            {
                int end = dstY * w + count;
                for (int i = dstY * w; i < end; i++)
                {
                    uint p = (uint)scratch[i];
                    if (p == 0) continue;
                    uint rb = ((p & 0x00FF00FFu) * a) >> 8;
                    uint ag = ((p >> 8) & 0x00FF00FFu) * a;
                    scratch[i] = (int)((rb & 0x00FF00FFu) | (ag & 0xFF00FF00u));
                }
            }
        }
        bmp.UnlockBits(d);
        var handle = GCHandle.Alloc(scratch, GCHandleType.Pinned);
        try
        {
            var bi = new BITMAPINFOHEADER { size = 40, width = w, height = -h, planes = 1, bits = 32 };
            IntPtr hdc = target.GetHdc();
            SetDIBitsToDevice(hdc, 0, 0, w, h, 0, 0, 0, h, handle.AddrOfPinnedObject(), ref bi, 0);
            target.ReleaseHdc(hdc);
        }
        finally { handle.Free(); }
    }

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // Is any visible, uncloaked window maximized on the primary monitor? (decides floating vs docked bar)
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetRect(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT r, uint ini);

    // Windows only keeps *maximized* windows out of an app bar's strip; normal windows (Electron apps restoring
    // their last bounds) and windows maximized before the bar existed can sit under it. Nudge such a window clear.
    public static void KeepClear(IntPtr h, IntPtr self)
    {
        if (h == IntPtr.Zero || h == self || !IsWindowVisible(h) || IsIconic(h)) return;
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
        int style = GetWindowLong(h, -16);
        if ((style & 0x00040000) == 0) return; // no resizable frame: fullscreen video/game, desktop, tool windows
        if (MonitorFromWindow(h, 2) != MonitorFromPoint(new POINT(), 1)) return; // the bar is on the primary monitor
        var work = new RECT();
        SystemParametersInfo(0x30, 0, ref work, 0); // SPI_GETWORKAREA: already excludes the bar
        RECT win, vis;
        if (!GetWindowRect(h, out win) || DwmGetRect(h, 9, out vis, 16) != 0) vis = win; // 9 = extended frame (no shadow)
        if (IsZoomed(h)) return; // Windows already fits maximized windows to the app bar's work area
        var r = Clear(Rectangle.FromLTRB(win.l, win.t, win.r, win.b), Rectangle.FromLTRB(vis.l, vis.t, vis.r, vis.b), Rectangle.FromLTRB(work.l, work.t, work.r, work.b));
        if (!r.IsEmpty) SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0014); // NOZORDER | NOACTIVATE
    }

    // New window rect so the visible frame starts at the work area's top and still ends inside it; Empty = fine as is.
    public static Rectangle Clear(Rectangle win, Rectangle vis, Rectangle work)
    {
        if (vis.Top >= work.Top || vis.Bottom <= work.Top) return Rectangle.Empty;
        int down = work.Top - vis.Top, top = win.Top + down, bottom = win.Bottom + down, shadow = win.Bottom - vis.Bottom;
        if (bottom - shadow > work.Bottom) bottom = work.Bottom + shadow; // shrink rather than push past the bottom
        return Rectangle.FromLTRB(win.Left, top, win.Right, bottom);
    }

    public static bool AnyMaximizedOnPrimary(IntPtr self) { return TopMaximizedOnPrimary(self) != IntPtr.Zero; }

    // The front-most maximized window on the primary screen (windows are listed front to back), or Zero.
    public static IntPtr TopMaximizedOnPrimary(IntPtr self)
    {
        IntPtr primary = MonitorFromPoint(new POINT(), 1);
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (h == self || !IsWindowVisible(h) || !IsZoomed(h)) return true;
            int cloaked;
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true; // other virtual desktop / suspended app
            if (MonitorFromWindow(h, 2) != primary) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    // Ctrl+V into whatever window got focus back once our popup closed (never into ourselves).
    public static void PasteSoon()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            System.Threading.Thread.Sleep(150);
            uint pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
            keybd_event(0x11, 0, 0, IntPtr.Zero);
            Tap(0x56);
            keybd_event(0x11, 0, 2, IntPtr.Zero);
        });
    }

    [StructLayout(LayoutKind.Sequential)] struct JOYINFOEX { public int size, flags, x, y, z, r, u, v, buttons, buttonNumber, pov, reserved1, reserved2; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICELIST { public IntPtr device; public int type; }
    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int user, byte[] state);
    [DllImport("winmm.dll")] static extern int joyGetPosEx(int id, ref JOYINFOEX ji);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);

    // Connected game controllers: XInput (Xbox-style) first; otherwise winmm, which also sees older HID pads.
    // winmm lists XInput pads too, so only one source is counted. Called on device change, never polled.
    public static int GamepadCount()
    {
        int n = 0;
        var state = new byte[16]; // XINPUT_STATE
        try { for (int i = 0; i < 4; i++) if (XInputGetState(i, state) == 0) n++; }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        if (n > 0) return n;
        var ji = new JOYINFOEX { size = Marshal.SizeOf(typeof(JOYINFOEX)), flags = 255 }; // JOY_RETURNALL
        for (int i = 0; i < 16; i++) if (joyGetPosEx(i, ref ji) == 0) n++;
        return n;
    }

    // External (USB / Bluetooth) mice and keyboards. Their HID paths carry a vendor ID ("VID_046D", "_VID&0002046d");
    // the laptop's own keyboard (ACPI) and I2C touchpad don't, so they are left out. One device exposes several
    // collections (Col01, MI_00...), so paths are reduced to vendor+product before counting.
    public static void InputDevices(out int mice, out int keyboards)
    {
        var m = new HashSet<string>();
        var k = new HashSet<string>();
        uint count = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(null, ref count, size);
        var list = new RAWINPUTDEVICELIST[count];
        if (count > 0 && GetRawInputDeviceList(list, ref count, size) != uint.MaxValue)
            foreach (var d in list)
            {
                if (d.type > 1) continue; // 0 mouse, 1 keyboard
                string key = DeviceKey(DeviceName(d.device));
                if (key == null) continue;
                (d.type == 0 ? m : k).Add(key);
            }
        k.ExceptWith(m); // a mouse's extra button interface (MI_01) reports as a keyboard; don't count it twice
        mice = m.Count;
        keyboards = k.Count;
    }

    static string DeviceName(IntPtr device)
    {
        uint len = 0;
        GetRawInputDeviceInfo(device, 0x20000007, null, ref len); // RIDI_DEVICENAME
        var sb = new StringBuilder((int)len + 1);
        return GetRawInputDeviceInfo(device, 0x20000007, sb, ref len) == uint.MaxValue ? "" : sb.ToString();
    }

    // "\\?\HID#VID_046D&PID_C52B&MI_01&Col01#..." -> "VID_046D&PID_C52B"; null for built-in devices.
    public static string DeviceKey(string path)
    {
        string p = path.ToUpperInvariant();
        int v = p.IndexOf("VID");
        if (v < 0) return null;
        int end = p.IndexOf('#', v);
        string id = end < 0 ? p.Substring(v) : p.Substring(v, end - v);
        foreach (var cut in new[] { "&MI_", "&COL" }) { int c = id.IndexOf(cut); if (c >= 0) id = id.Substring(0, c); }
        return id;
    }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    // Runs a short animation in step with the display: one step per screen refresh (120/144 fps on such monitors,
    // instead of the ~64 a WinForms timer can manage). step() returns false when done. Input keeps flowing meanwhile.
    // All running animations share one loop, so a second one (a panel resizing while it fades in) never blocks the first.
    static readonly List<Func<bool>> anims = new List<Func<bool>>();
    static bool animating;
    public static float FrameK = 1; // this frame's length in 15 ms units (1 outside the loop)
    static readonly System.Diagnostics.Stopwatch frameClock = System.Diagnostics.Stopwatch.StartNew();
    public static void Vsync(Func<bool> step)
    {
        anims.Add(step);
        if (animating) return; // the loop already running takes it from here
        animating = true;
        double last = frameClock.Elapsed.TotalMilliseconds - 15;
        try
        {
            while (anims.Count > 0)
            {
                double now = frameClock.Elapsed.TotalMilliseconds;
                FrameK = (float)Math.Max(0.05, Math.Min(4, (now - last) / 15)); // a late frame catches up, a quick one moves less
                last = now;
                for (int i = anims.Count - 1; i >= 0; i--)
                {
                    bool more;
                    try { more = anims[i](); } catch (Exception) { more = false; } // a window closed mid-animation
                    if (!more) anims.RemoveAt(i);
                }
                if (anims.Count == 0) break;
                if (DwmFlush() != 0) System.Threading.Thread.Sleep(8); // no compositor: about 120 fps by the clock
                Application.DoEvents();
            }
        }
        finally { animating = false; FrameK = 1; }
    }

    public static void Trim() { SetProcessWorkingSetSize((IntPtr)(-1), (IntPtr)(-1), (IntPtr)(-1)); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved, lpDesktop, lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr h, int idx, StringBuilder buf, int len, out int needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // If started from a sandboxed tool desktop (not Default), re-spawn on the interactive winsta0\default desktop.
    public static bool EnsureDefaultDesktop(string exePath)
    {
        var sb = new StringBuilder(128);
        int n;
        if (!GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, sb, 256, out n) || sb.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase))
            return true;
        var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = @"winsta0\default" };
        PROCESS_INFORMATION pi;
        if (CreateProcess(exePath, null, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, System.IO.Path.GetDirectoryName(exePath), ref si, out pi))
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        return false;
    }

    // The accent shade Windows' own controls use: AccentLight2 on dark, AccentDark1 on light (Settings > Personalization > Colors).
    public static Color Accent(bool light)
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                var p = k == null ? null : k.GetValue("AccentPalette") as byte[]; // 8 RGBA swatches: Light3..Base..Dark3
                int i = light ? 4 : 1;
                if (p != null && p.Length >= 32) return Color.FromArgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch (System.Security.SecurityException) { }
        uint c; bool o;
        return DwmGetColorizationColor(out c, out o) == 0 ? Color.FromArgb((int)(c >> 16 & 255), (int)(c >> 8 & 255), (int)(c & 255)) : Color.FromArgb(70, 110, 200);
    }

    public static float Luma(Color c) { return (0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B) / 255f; }

    public static GraphicsPath Round(RectangleF r, float rad) { return Round(r, rad, rad, rad, rad); }

    public static GraphicsPath Round(RectangleF r, float tl, float tr, float br, float bl)
    {
        var p = new GraphicsPath();
        if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0) { p.AddRectangle(r); return p; } // square: tiny arcs would nick a pixel off each corner
        float m = Math.Min(r.Width, r.Height);
        Func<float, float> d = x => Math.Max(0.01f, Math.Min(x * 2, m));
        float a = d(tl), b = d(tr), c = d(br), e = d(bl);
        p.AddArc(r.X, r.Y, a, a, 180, 90);
        p.AddArc(r.Right - b, r.Y, b, b, 270, 90);
        p.AddArc(r.Right - c, r.Bottom - c, c, c, 0, 90);
        p.AddArc(r.X, r.Bottom - e, e, e, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, GraphicsPath p, Color c) { using (var b = new SolidBrush(c)) g.FillPath(b, p); }

    // "Liquid glass" finish on any surface: a soft sheen over the top half and a light rim that fades downward.
    public static void Gloss(Graphics g, GraphicsPath p, RectangleF r, float s, float strength)
    {
        if (r.Width < 2 || r.Height < 2 || !Glass.Translucent || Glass.Style == 2) return; // no sheen on Flat, Material You, or Acrylic (Windows menus have none)
        var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.55f);
        using (var sheen = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2), Color.FromArgb((int)(46 * strength), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
        {
            var st = g.Save();
            g.SetClip(top, CombineMode.Intersect);
            g.FillPath(sheen, p);
            g.Restore(st);
        }
        using (var rim = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb((int)(130 * strength), 255, 255, 255), Color.FromArgb((int)(22 * strength), 255, 255, 255), LinearGradientMode.Vertical))
        using (var pen = new Pen(rim, Math.Max(1, s)))
            g.DrawPath(pen, p);
    }

    public static StringFormat Fmt(StringAlignment h, StringAlignment v)
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.Alignment = h;
        f.LineAlignment = v;
        f.Trimming = StringTrimming.EllipsisCharacter;
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    // Moves v toward t; returns true while still moving.
    // k is "how far per 15 ms frame"; scaled by the real frame length, so easing runs at the same speed at 60 or 240 Hz.
    public static bool Ease(ref float v, float t, float k)
    {
        v += (t - v) * (FrameK == 1 ? k : 1 - (float)Math.Pow(1 - k, FrameK));
        if (Math.Abs(t - v) < 0.0015f) { v = t; return false; }
        return true;
    }

    // Damped harmonic spring solver stepped at the display's real refresh rate (60..240+ Hz).
    public static bool Spring(ref float v, ref float vel, float t, float stiffness = 320f, float damping = 26f, float eps = 0.002f)
    {
        float dt = Math.Min(0.03f, FrameK * 0.015f);
        float acc = stiffness * (t - v) - damping * vel;
        vel += acc * dt;
        v += vel * dt;
        if (Math.Abs(t - v) < eps && Math.Abs(vel) < eps * 8f) { v = t; vel = 0; return false; }
        return true;
    }

    public static void Tap(byte vk) { keybd_event(vk, 0, 0, IntPtr.Zero); keybd_event(vk, 0, 2, IntPtr.Zero); }

    public static void Win(Keys k) { Win(k, false); }

    // Win (+ Ctrl) + key: for Windows features that only have a shortcut (cast, project, live captions).
    public static void Win(Keys k, bool ctrl)
    {
        keybd_event(0x5B, 0, 0, IntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, 0, IntPtr.Zero);
        keybd_event((byte)k, 0, 0, IntPtr.Zero);
        keybd_event((byte)k, 0, 2, IntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, 2, IntPtr.Zero);
        keybd_event(0x5B, 0, 2, IntPtr.Zero);
    }
}

// Segoe Fluent Icons code points.
static class G
{
    public const string Wifi = "", Ethernet = "", Bluetooth = "", Brightness = "", Settings = "",
        Lock = "", Power = "", Sleep = "", SignOut = "", Restart = "", Close = "",
        Headphones = "", Usb = "", Mute = "", Cast = "", Speed = "", Share = "",
        Folder = "\uE8B7", File = "\uE7C3", Add = "\uE710", Delete = "\uE74D", Copy = "\uE8C8",
        Speaker = "", Mic = "", Camera = "", Theme = "", NightLight = "", Check = "",
        Edit = "", Up = "", Down = "", Open = "", Visible = "", Hidden = "",
        Search = "\uE721", ChevronRight = "\uE76C", Phone = "\uE8EA", Wifi1 = "\uE872", Wifi2 = "\uE873", Wifi3 = "\uE874",
        MicOff = "\uF781", Gamepad = "\uE7FC", Tiles = "\uE80A", ClockIcon = "\uE823", Mouse = "\uE962", Keyboard = "\uE765", Bar = "\uE737",
        Pin = "\uE718", Unpin = "\uE77A", Paste = "\uE77F",
        Prev = "", Next = "", Play = "", Pause = "", Music = "";

    public static string Volume(float v, bool mute)
    {
        if (mute) return Mute;
        return ((char)(v < 0.01f ? 0xE992 : v < 0.34f ? 0xE993 : v < 0.67f ? 0xE994 : 0xE995)).ToString(); // Volume0-3
    }

    // Battery0-9 = E850-E859, Battery10 = E83F; Charging0-8 = E85A-E862, Charging9 = E83E
    public static string Battery(int pct, bool charging)
    {
        int lvl = Math.Max(0, Math.Min(pct / 10, 10));
        char c = charging ? (lvl >= 9 ? '' : (char)(0xE85A + lvl)) : (lvl == 10 ? '' : (char)(0xE850 + lvl));
        return c.ToString();
    }
}
