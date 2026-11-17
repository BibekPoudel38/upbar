using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Windows settings the control center toggles: power mode, camera access, dark theme, tray overflow.
static class Shell
{
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid g);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid g);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);

    // The three Windows 11 power modes (Settings > System > Power).
    static readonly Guid[] Modes = { new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), Guid.Empty, new Guid("ded574b5-45a0-4f42-8737-46345c09c238") };
    public static readonly string[] ModeNames = { "Efficiency", "Balanced", "Performance" };

    // Asking Windows for the power mode takes about a third of a second, so the answer is kept: PowerMode returns the
    // last one read (-2 = not read yet, -1 = unavailable). ReadPowerMode does the slow query; call it off the UI thread.
    static volatile int mode = -2;
    public static int PowerMode
    {
        get { return mode; }
        set { try { PowerSetActiveOverlayScheme(Modes[value]); mode = value; } catch (EntryPointNotFoundException) { } }
    }

    public static int ReadPowerMode()
    {
        try { Guid g; if (PowerGetEffectiveOverlayScheme(out g) != 0) mode = -1; else { int i = Array.IndexOf(Modes, g); mode = i < 0 ? 1 : i; } }
        catch (EntryPointNotFoundException) { mode = -1; }
        return mode;
    }

    // ---- Keyboard / input language picker
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    public static IntPtr KeyboardOf(IntPtr window) { return GetKeyboardLayout(GetWindowThreadProcessId(window, IntPtr.Zero)); }
    // Asks the app you were in (and every other window) to switch, like Win+Space does.
    public static void UseKeyboard(IntPtr window, IntPtr layout)
    {
        if (window != IntPtr.Zero) PostMessage(window, 0x50, IntPtr.Zero, layout); // WM_INPUTLANGCHANGEREQUEST
        PostMessage((IntPtr)0xFFFF, 0x50, IntPtr.Zero, layout);                  // HWND_BROADCAST
    }

    const string LocKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
    // The per-user "Let apps access your location" switch (like the camera one).
    public static bool LocationAllowed
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(LocKey)) return k == null || !"Deny".Equals(k.GetValue("Value") as string); }
        set { using (var k = Registry.CurrentUser.CreateSubKey(LocKey)) k.SetValue("Value", value ? "Allow" : "Deny"); }
    }

    const string CamKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    // The per-user "Let apps access your camera" switch.
    public static bool CameraAllowed
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(CamKey)) return k == null || !"Deny".Equals(k.GetValue("Value") as string); }
        set { using (var k = Registry.CurrentUser.CreateSubKey(CamKey)) k.SetValue("Value", value ? "Allow" : "Deny"); }
    }

    const string ThemeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool Dark
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(ThemeKey)) { var v = k == null ? null : k.GetValue("AppsUseLightTheme"); return v is int && (int)v == 0; } }
        set
        {
            using (var k = Registry.CurrentUser.CreateSubKey(ThemeKey))
            {
                k.SetValue("AppsUseLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
                k.SetValue("SystemUsesLightTheme", value ? 0 : 1, RegistryValueKind.DWord);
            }
            // Tell running apps; a hung window could block a plain broadcast, so time out off the UI thread.
            ThreadPool.QueueUserWorkItem(_ => { IntPtr r; SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "ImmersiveColorSet", 2, 500, out r); });
        }
    }

    public class TrayApp { public string Name, Title; public Image Icon; } // Name: process; Title: what people call it

    // Running apps whose tray icon is in the "Show hidden icons" overflow, with the icon Windows cached.
    public static List<TrayApp> HiddenTray()
    {
        var list = new List<TrayApp>();
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) using (p) running.Add(p.ProcessName);
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
                if (root != null)
                    foreach (var id in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(id))
                        {
                            var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                            var promoted = k == null ? null : k.GetValue("IsPromoted");
                            var snap = k == null ? null : k.GetValue("IconSnapshot") as byte[];
                            if (path == null || snap == null || (promoted is int && (int)promoted == 1)) continue;
                            string name = Path.GetFileNameWithoutExtension(path);
                            if (!running.Contains(name) || list.Exists(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                            // The tray tooltip is the app's own name ("WhatsApp"); the exe name ("WhatsApp.Root") is the fallback.
                            var tip = (k.GetValue("InitialTooltip") as string ?? "").Split('\n')[0].Trim();
                            string title = tip.Length > 0 && tip.Length <= 40 ? tip : name.Split('.')[0];
                            try { using (var ms = new MemoryStream(snap)) using (var img = Image.FromStream(ms)) list.Add(new TrayApp { Name = name, Title = title, Icon = new Bitmap(img) }); }
                            catch (ArgumentException) { } // not an image
                        }
        }
        catch (Exception) { }
        return list;
    }

    // Brings the app's window forward; tray-only apps get Windows' own overflow flyout so their menu is reachable.
    public static void Activate(TrayApp a)
    {
        foreach (var p in Process.GetProcessesByName(a.Name))
            using (p)
            {
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                if (IsIconic(h)) ShowWindow(h, 9);
                SetForegroundWindow(h);
                return;
            }
        OpenOverflow();
    }

    // Quit: ask every process of the app to close, then force whatever is still running after 3 s.
    public static void Quit(TrayApp a)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var procs = Process.GetProcessesByName(a.Name);
            foreach (var p in procs) try { p.CloseMainWindow(); } catch (InvalidOperationException) { }
            Thread.Sleep(3000);
            foreach (var p in procs)
                using (p)
                    try { if (!p.HasExited) p.Kill(); }
                    catch (InvalidOperationException) { }                  // already gone
                    catch (System.ComponentModel.Win32Exception) { }       // elevated app: not ours to kill
        });
    }

    public static void OpenOverflow()
    {
        Native.Win(Keys.B); // focuses the "Show hidden icons" chevron
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(250); Native.Tap(0x0D); });
    }

    public static void Open(string uri) { try { Process.Start(uri); } catch (System.ComponentModel.Win32Exception) { } }
}
