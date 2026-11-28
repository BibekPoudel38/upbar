using System;
using System.Drawing;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Our own volume and brightness pop-up at the bottom centre of the screen.
// Volume keys are caught by a low-level keyboard hook before Windows sees them; brightness changes are caught via WMI's
// WmiMonitorBrightnessEvent (event-driven, zero polling) and Windows' own hardware flyout is hidden when it appears.
static class VolumeKeys
{
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int val);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    const int Mute = 0xAD, Down = 0xAE, Up = 0xAF;
    static IntPtr hook, winHookShow, winHookLoc;
    static HookProc proc; // kept alive: the hook calls it for as long as it is installed
    static Native.WinEventProc winProc;
    static readonly System.Collections.Generic.HashSet<IntPtr> cloaked = new System.Collections.Generic.HashSet<IntPtr>();
    static int activeUntil; // Environment.TickCount window after a volume/brightness event
    static ManagementEventWatcher brightWatch;
    static Control uiHost;
    static VolumeOsd osd;
    static float scale;
    public static int VolStyle, BrightStyle, Pos; // VolumeOsd.Styles / Positions index, from the config
    public static Func<bool> SuppressBrightOsd; // true while the Control Center's own brightness slider is open

    // Called on the UI thread, which runs the hook.
    public static void Enable(bool on, float s, Control host)
    {
        scale = s;
        uiHost = host;
        if (on == (hook != IntPtr.Zero)) return;
        if (on)
        {
            proc = Hook;
            hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
            winProc = OnWinEvent;
            winHookShow = Native.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, winProc, 0, 0, 0); // EVENT_OBJECT_SHOW
            winHookLoc = Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, winProc, 0, 0, 0);  // EVENT_OBJECT_LOCATIONCHANGE
            StartBrightnessWatch();
        }
        else
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            if (winHookShow != IntPtr.Zero) { Native.UnhookWinEvent(winHookShow); winHookShow = IntPtr.Zero; }
            if (winHookLoc != IntPtr.Zero) { Native.UnhookWinEvent(winHookLoc); winHookLoc = IntPtr.Zero; }
            StopBrightnessWatch();
            RestoreNativeFlyouts();
            if (osd != null) { osd.Close(); osd = null; }
        }
    }

    static void ArmSuppress() { activeUntil = Environment.TickCount + 900; HideNativeFlyout(); }

    static void OnWinEvent(IntPtr hk, uint ev, IntPtr hwnd, int obj, int child, uint th, uint time)
    {
        if (obj != 0 || child != 0 || hwnd == IntPtr.Zero) return;
        // Re-cloak only if Explorer moved it back: Cloak's own SetWindowRgn fires two LOCATIONCHANGEs, so re-cloaking
        // unconditionally fed back exponentially and starved the UI thread (frozen OSD, frozen app).
        if (cloaked.Contains(hwnd)) { Native.RECT r; if (GetWindowRect(hwnd, out r) && r.l > -30000) Cloak(hwnd); return; }
        if (unchecked(activeUntil - Environment.TickCount) <= 0) return;
        if (IsNativeOsd(hwnd)) Cloak(hwnd);
    }

    static void StartBrightnessWatch()
    {
        StopBrightnessWatch();
        try
        {
            brightWatch = new ManagementEventWatcher(new ManagementScope("root\\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            brightWatch.EventArrived += (s, e) =>
            {
                try
                {
                    int b = Convert.ToInt32(e.NewEvent["Brightness"]);
                    var h = uiHost;
                    if (h != null && h.IsHandleCreated && !h.IsDisposed)
                        h.BeginInvoke((Action)(() => OnBrightness(b)));
                }
                catch (Exception) { }
            };
            brightWatch.Start();
        }
        catch (Exception) { brightWatch = null; } // desktop monitor without WMI brightness events
    }

    static void StopBrightnessWatch()
    {
        if (brightWatch == null) return;
        try { brightWatch.Stop(); brightWatch.Dispose(); } catch (Exception) { }
        brightWatch = null;
    }

    static void OnBrightness(int pct)
    {
        ArmSuppress();
        if (SuppressBrightOsd != null && SuppressBrightOsd()) return;
        if (osd == null || osd.IsDisposed) osd = new VolumeOsd(scale);
        osd.Pop(Math.Max(0, Math.Min(100, pct)) / 100f, false, true);
    }

    static bool IsNativeOsd(IntPtr h)
    {
        if (osd != null && !osd.IsDisposed && h == osd.Handle) return false;
        var sb = new StringBuilder(64);
        if (GetClassName(h, sb, sb.Capacity) <= 0) return false;
        string cls = sb.ToString();
        if (cls == "NativeHWNDHost") return FindWindowEx(h, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero;
        if (cls != "XamlExplorerHostIslandWindow") return false;
        Native.RECT r;
        if (!GetWindowRect(h, out r)) return false;
        var scr = Screen.PrimaryScreen.Bounds;
        int w = r.r - r.l, ht = r.b - r.t, cx = (r.l + r.r) / 2, mid = scr.Left + scr.Width / 2;
        return w >= (int)(130 * scale) && w <= (int)(380 * scale) && ht >= (int)(30 * scale) && ht <= (int)(120 * scale)
            && Math.Abs(cx - mid) <= (int)(24 * scale) && r.t >= scr.Bottom - (int)(240 * scale);
    }

    // Keeps the window "visible" to Explorer (so Explorer never re-shows it on key repeat) while making it 0-alpha and off-screen.
    static void Cloak(IntPtr h)
    {
        try
        {
            cloaked.Add(h);
            int ex = GetWindowLong(h, -20);
            if ((ex & 0x00080000) == 0) SetWindowLong(h, -20, ex | 0x00080000); // WS_EX_LAYERED
            SetLayeredWindowAttributes(h, 0, 0, 2); // LWA_ALPHA = 0 (invisible)
            Native.SetWindowRgn(h, CreateRectRgn(0, 0, 0, 0), false);
            SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, 0x0015); // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        }
        catch (Exception) { }
    }

    static void RestoreNativeFlyouts()
    {
        foreach (var h in cloaked)
        {
            try
            {
                SetLayeredWindowAttributes(h, 0, 255, 2);
                Native.SetWindowRgn(h, IntPtr.Zero, true);
            }
            catch (Exception) { }
        }
        cloaked.Clear();
    }

    static void HideNativeFlyout()
    {
        try
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "NativeHWNDHost", "")) != IntPtr.Zero)
                if (FindWindowEx(h, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero) Cloak(h);
            h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
                if (cloaked.Contains(h) || (IsWindowVisible(h) && IsNativeOsd(h))) Cloak(h);
        }
        catch (Exception) { }
    }

    // Volume keys: return immediately from the low-level hook so Windows never times out or passes the key to Explorer.
    static IntPtr Hook(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(l);
            if (vk == Mute || vk == Down || vk == Up)
            {
                int msg = (int)w;
                if (msg == 0x100 || msg == 0x104)
                {
                    var h = uiHost;
                    if (h != null && h.IsHandleCreated && !h.IsDisposed) h.BeginInvoke((Action)(() => Press(vk)));
                }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(hook, code, w, l);
    }

    // shortcut: fixed 2% steps like Windows' default; no per-app volume.
    public static float Step(float v, int vk) { return vk == Up ? Math.Min(1, (float)Math.Round(v * 50 + 1) / 50) : vk == Down ? Math.Max(0, (float)Math.Round(v * 50 - 1) / 50) : v; }

    static void Press(int vk)
    {
        ArmSuppress();
        var o = Audio.Out;
        if (o == null) return;
        if (vk == Mute) o.Muted = !o.Muted;
        else { o.Volume = Step(o.Volume, vk); if (o.Muted) o.Muted = false; } // like Windows: changing the level unmutes
        if (osd == null || osd.IsDisposed) osd = new VolumeOsd(scale);
        osd.Pop(o.Volume, o.Muted, false);
    }
}
