using System;
using System.Management;
using System.Threading;
using System.Collections.Generic;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.UI.Shell;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using Windows.ApplicationModel.DataTransfer;

// Wi-Fi/Bluetooth radios, SSID and brightness. Slow calls: run these off the UI thread.
// WinRT and WMI only load the first time one of these runs, keeping the idle bar small.
static class Sys
{
    // The in-box C# 5 compiler lacks the WinRT await helpers, so wait by polling the operation.
    internal static T Wait<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(5);
        return op.GetResults();
    }

    static void Wait(IAsyncAction a)
    {
        while (a.Status == AsyncStatus.Started) Thread.Sleep(5);
        a.GetResults();
    }

    public class WifiNet { public string Ssid; public int Bars; public bool Secured, Connected; internal object Raw; }
    public class BtDev { public string Name, Kind; public bool Connected; }
    static WiFiAdapter adapter;

    static WiFiAdapter Adapter()
    {
        if (adapter != null) return adapter;
        if (Wait(WiFiAdapter.RequestAccessAsync()) != WiFiAccessStatus.Allowed) return null;
        var all = Wait(WiFiAdapter.FindAllAdaptersAsync());
        return adapter = all.Count > 0 ? all[0] : null;
    }

    // Visible networks, one entry per SSID (strongest), connected first then by signal.
    public static List<WifiNet> WifiScan()
    {
        var list = new List<WifiNet>();
        try
        {
            var a = Adapter();
            if (a == null) return list;
            Wait(a.ScanAsync());
            string cur = Ssid();
            foreach (var n in a.NetworkReport.AvailableNetworks)
            {
                if (string.IsNullOrEmpty(n.Ssid)) continue; // hidden networks
                var e = list.Find(x => x.Ssid == n.Ssid);
                if (e != null) { if (n.SignalBars > e.Bars) { e.Bars = n.SignalBars; e.Raw = n; } continue; }
                var auth = n.SecuritySettings.NetworkAuthenticationType;
                list.Add(new WifiNet { Ssid = n.Ssid, Bars = n.SignalBars, Raw = n, Connected = n.Ssid == cur,
                                       Secured = auth != NetworkAuthenticationType.Open80211 && auth != NetworkAuthenticationType.None });
            }
            list.Sort((x, y) => x.Connected != y.Connected ? (x.Connected ? -1 : 1) : y.Bars.CompareTo(x.Bars));
        }
        catch (Exception) { }
        return list;
    }

    // null = connected; otherwise a short reason. needsPassword: secured network with no saved (or a wrong) password.
    public static string WifiConnect(WifiNet n, string password, out bool needsPassword)
    {
        needsPassword = false;
        try
        {
            var a = Adapter();
            var net = (WiFiAvailableNetwork)n.Raw;
            var r = password == null
                ? Wait(a.ConnectAsync(net, WiFiReconnectionKind.Automatic))
                : Wait(a.ConnectAsync(net, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password }));
            switch (r.ConnectionStatus)
            {
                case WiFiConnectionStatus.Success: return null;
                case WiFiConnectionStatus.InvalidCredential: needsPassword = true; return password == null ? "Enter the password" : "Wrong password";
                case WiFiConnectionStatus.NetworkNotAvailable: return "Out of range";
                case WiFiConnectionStatus.Timeout: return "Timed out";
                default:
                    if (n.Secured && password == null) { needsPassword = true; return "Enter the password"; }
                    return "Couldn't connect";
            }
        }
        catch (Exception) { return "Couldn't connect"; }
    }

    public static void WifiDisconnect() { try { var a = Adapter(); if (a != null) a.Disconnect(); } catch (Exception) { } }

    public static List<BtDev> BtDevices()
    {
        var list = new List<BtDev>();
        try
        {
            foreach (var d in Wait(DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true))))
            {
                var b = Wait(BluetoothDevice.FromIdAsync(d.Id));
                if (b == null) continue;
                using (b) list.Add(new BtDev { Name = d.Name, Kind = BtKind(d.Name, b.ClassOfDevice.MajorClass.ToString(), 0, 0), Connected = b.ConnectionStatus == BluetoothConnectionStatus.Connected });
            }
            foreach (var d in Wait(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))))
            {
                var b = Wait(BluetoothLEDevice.FromIdAsync(d.Id));
                if (b == null) continue;
                using (b)
                {
                    string kind = BtKind(d.Name, null, b.Appearance == null ? 0 : b.Appearance.Category, b.Appearance == null ? 0 : b.Appearance.SubCategory);
                    bool conn = b.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    var ex = list.Find(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));
                    if (ex != null) { ex.Connected |= conn; if (ex.Kind == "Bluetooth" && kind != "Bluetooth") ex.Kind = kind; }
                    else list.Add(new BtDev { Name = d.Name, Kind = kind, Connected = conn });
                }
            }
            list.Sort((x, y) => x.Connected != y.Connected ? (x.Connected ? -1 : 1) : string.Compare(x.Name, y.Name, StringComparison.CurrentCulture));
        }
        catch (Exception) { }
        return list;
    }

    public static string BtKind(string name, string major, int cat, int sub)
    {
        if (cat == 15) return sub == 1 ? "Keyboard" : sub == 2 ? "Mouse" : sub == 3 || sub == 4 ? "Gamepad" : "Bluetooth";
        if (cat == 1 || major == "Phone") return "Phone";
        if (cat == 33 || cat == 34 || major == "AudioVideo") return "AudioVideo";
        if (System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "Mouse|ProtoArc|MX|DeathAdder|Basilisk|G Pro", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "Mouse";
        if (System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "Keyboard|Keychron|K380|MX Keys", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return "Keyboard";
        return "Bluetooth";
    }

    // ---- Do not disturb: a Windows Focus session silences notifications (no public DND switch exists).
    static bool focus;
    static int focusAt = int.MinValue;
    // Asking Windows takes ~4 ms and the bar asks on every refresh, so the answer is kept for 5 seconds.
    public static bool FocusActive
    {
        get
        {
            if (focusAt == int.MinValue || unchecked(Environment.TickCount - focusAt) > 5000)
            {
                focusAt = Environment.TickCount;
                try { focus = FocusSessionManager.IsSupported && FocusSessionManager.GetDefault().IsFocusActive; } catch (Exception) { focus = false; }
            }
            return focus;
        }
    }

    // ---- Notifications currently in Action Center
    public class Note { public uint Id; public string App, Title, Body; public DateTime When; internal UserNotification Raw; public System.Drawing.Bitmap Logo; }

    public static List<Note> Notes()
    {
        var list = new List<Note>();
        try
        {
            var l = UserNotificationListener.Current;
            if (Wait(l.RequestAccessAsync()) != UserNotificationListenerAccessStatus.Allowed) return list;
            foreach (var n in Wait(l.GetNotificationsAsync(NotificationKinds.Toast))) list.Add(ToNote(n));
            list.Sort((x, y) => y.When.CompareTo(x.When));
        }
        catch (Exception) { }
        return list;
    }

    // New notifications, as they arrive (raised on a worker thread). Uses Windows' change event when this app may
    // have it; otherwise looks every few seconds.
    public static event Action<Note> Arrived;
    static HashSet<uint> seen;
    static Timer poll;

    public static void WatchNotes()
    {
        try
        {
            UserNotificationListener.Current.NotificationChanged += (s, e) => { if (e.ChangeKind == UserNotificationChangedKind.Added) ThreadPool.QueueUserWorkItem(_ => CheckNew()); };
            ThreadPool.QueueUserWorkItem(_ => CheckNew()); // learn what is already there
        }
        catch (Exception) // shortcut: unpackaged apps may be refused the event; then a 4 s poll (a cheap call) stands in for it
        {
            poll = new Timer(_ => CheckNew(), null, 0, 4000);
        }
    }

    // The sending app's logo, small (null when the app has none). Worker thread only: it waits on Windows.
    static System.Drawing.Bitmap Logo(UserNotification n)
    {
        try
        {
            var stream = Wait(n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)).OpenReadAsync());
            using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
            {
                uint size = Wait(reader.LoadAsync((uint)stream.Size));
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                using (var img = System.Drawing.Image.FromStream(new System.IO.MemoryStream(bytes)))
                    return new System.Drawing.Bitmap(img, 48, 48); // a copy: the stream can go
            }
        }
        catch (Exception) { return null; } // no logo, or Windows refused: the bell is shown instead
    }

    static readonly object noteGate = new object();
    // Text and app name are slow to read (~35 ms per notification), so this compares ids only and reads just the new one.
    static Note ToNote(UserNotification n)
    {
        var texts = new List<string>();
        var b = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (b != null) foreach (var t in b.GetTextElements()) if (!string.IsNullOrEmpty(t.Text)) texts.Add(t.Text);
        string app = "";
        try { app = n.AppInfo.DisplayInfo.DisplayName; } catch (Exception) { }
        return new Note { Raw = n, Id = n.Id, App = app, Title = texts.Count > 0 ? texts[0] : app, Body = string.Join(" ", texts.GetRange(Math.Min(1, texts.Count), Math.Max(0, texts.Count - 1))), When = n.CreationTime.LocalDateTime };
    }

    static bool allowed;
    static void CheckNew()
    {
        try
        {
            var l = UserNotificationListener.Current;
            if (!allowed) { if (Wait(l.RequestAccessAsync()) != UserNotificationListenerAccessStatus.Allowed) return; allowed = true; }
            var raw = Wait(l.GetNotificationsAsync(NotificationKinds.Toast));
            lock (noteGate)
            {
                bool first = seen == null;
                var ids = new HashSet<uint>();
                UserNotification newest = null;
                foreach (var n in raw)
                {
                    ids.Add(n.Id);
                    if (!first && !seen.Contains(n.Id) && (newest == null || n.CreationTime > newest.CreationTime)) newest = n; // announce only the newest new one
                }
                seen = ids;
                if (newest == null) return;
                var note = ToNote(newest);
                note.Logo = Logo(newest);
                var a = Arrived;
                if (a != null) a(note);
            }
        }
        catch (Exception) { } // listener unavailable this time: try again on the next check
    }

    public static void Dismiss(uint id) { try { UserNotificationListener.Current.RemoveNotification(id); } catch (Exception) { } }
    // Opens the app that sent it and clears it, as clicking it in Windows would.
    // shortcut: Windows gives other apps no way to fire a toast's own action (e.g. open that exact chat), so this opens
    // the app itself; apps without an id fall back to Windows' notification centre.
    public static void OpenNote(Note n)
    {
        string aumid = null;
        try { aumid = n.Raw.AppInfo.AppUserModelId; } catch (Exception) { }
        if (string.IsNullOrEmpty(aumid)) { Native.Win(System.Windows.Forms.Keys.N); return; }
        // From the phone (Phone Link): show the phone's own screen instead, when ADB can reach it (USB or wireless).
        if (aumid.StartsWith("Microsoft.YourPhone_", StringComparison.OrdinalIgnoreCase) && Health.Mirror(null)) { Dismiss(n.Id); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + aumid); Dismiss(n.Id); }
        catch (Exception) { Native.Win(System.Windows.Forms.Keys.N); }
    }

    // One by one: per-id removal is the call verified to reach Windows' own panel from this unpackaged app.
    public static void ClearNotes(List<Note> notes) { foreach (var n in notes) Dismiss(n.Id); }

    public static bool WinHistoryOn { get { try { return WinClipboard.IsHistoryEnabled(); } catch (Exception) { return false; } } }

    // ---- Windows clipboard history (Win+V); null when the user has it turned off
    public static List<ClipItem> ClipHistory()
    {
        try
        {
            if (!WinClipboard.IsHistoryEnabled()) return null;
            var r = Wait(WinClipboard.GetHistoryItemsAsync());
            if (r.Status != ClipboardHistoryItemsResultStatus.Success) return null;
            var list = new List<ClipItem>();
            foreach (var it in r.Items)
            {
                var c = it.Content;
                var ci = new ClipItem { WinItem = it, When = it.Timestamp.LocalDateTime };
                if (c.Contains(StandardDataFormats.Text)) ci.Text = Wait(c.GetTextAsync());
                else if (c.Contains(StandardDataFormats.StorageItems)) ci.Files = new[] { "Files" };
                else if (c.Contains(StandardDataFormats.Bitmap)) { ci.Png = new byte[0]; try { ci.Thumb = Media.ReadArt(Wait(c.GetBitmapAsync())); } catch (Exception) { } }
                else continue;
                list.Add(ci);
            }
            return list;
        }
        catch (Exception) { return null; }
    }

    public static void ClipRestore(ClipItem c) { try { WinClipboard.SetHistoryItemAsContent((ClipboardHistoryItem)c.WinItem); } catch (Exception) { } }
    public static void ClipDelete(ClipItem c) { try { WinClipboard.DeleteItemFromHistory((ClipboardHistoryItem)c.WinItem); } catch (Exception) { } }

    static Radio Find(bool wifi)
    {
        Wait(Radio.RequestAccessAsync());
        foreach (var r in Wait(Radio.GetRadiosAsync()))
            if (r.Kind == (wifi ? RadioKind.WiFi : RadioKind.Bluetooth)) return r;
        return null;
    }

    // -1 = no such radio, 0 = off, 1 = on
    public static int GetRadio(bool wifi)
    {
        try { var r = Find(wifi); return r == null ? -1 : r.State == RadioState.On ? 1 : 0; }
        catch (Exception) { return -1; }
    }

    public static void SetRadio(bool wifi, bool on)
    {
        try { var r = Find(wifi); if (r != null) Wait(r.SetStateAsync(on ? RadioState.On : RadioState.Off)); }
        catch (Exception) { }
    }

    // Flight mode stand-in: Windows has no airplane-mode switch for apps, so this turns every radio (Wi-Fi, Bluetooth,
    // mobile broadband) off together, and back on. 1 = all off ("flight"), 0 = some on, -1 = no radios.
    public static int Flight()
    {
        try
        {
            var all = Wait(Radio.GetRadiosAsync());
            if (all.Count == 0) return -1;
            foreach (var r in all) if (r.State == RadioState.On) return 0;
            return 1;
        }
        catch (Exception) { return -1; }
    }

    public static void SetFlight(bool on)
    {
        try { foreach (var r in Wait(Radio.GetRadiosAsync())) Wait(r.SetStateAsync(on ? RadioState.Off : RadioState.On)); }
        catch (Exception) { }
    }

    // Mobile hotspot (shares the current internet connection). -1 when this PC or connection can't share.
    public class Hotspot { public int State = -1, Clients; public string Name; }
    public static Hotspot HotspotNow()
    {
        var h = new Hotspot();
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return h;
            var m = Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager.CreateFromConnectionProfile(p);
            var st = m.TetheringOperationalState;
            h.State = st == Windows.Networking.NetworkOperators.TetheringOperationalState.On ? 1 : st == Windows.Networking.NetworkOperators.TetheringOperationalState.Off ? 0 : -2;
            h.Clients = (int)m.ClientCount;
            h.Name = m.GetCurrentAccessPointConfiguration().Ssid;
        }
        catch (Exception) { h.State = -1; }
        return h;
    }

    public static string SetHotspot(bool on) // null when done, else the reason
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return "No internet connection to share";
            var m = Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager.CreateFromConnectionProfile(p);
            var r = Wait(on ? m.StartTetheringAsync() : m.StopTetheringAsync());
            return r.Status == Windows.Networking.NetworkOperators.TetheringOperationStatus.Success ? null : (string.IsNullOrEmpty(r.AdditionalErrorMessage) ? r.Status.ToString() : r.AdditionalErrorMessage);
        }
        catch (Exception) { return "Not available"; }
    }

    public static string Ssid()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return null;
            return p.IsWlanConnectionProfile ? p.WlanConnectionProfileDetails.GetConnectedSsid() : p.ProfileName;
        }
        catch (Exception) { return null; }
    }

    // -1 when the display has no software brightness (most desktop monitors).
    public static int Brightness()
    {
        try
        {
            using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (ManagementObject o in s.Get()) using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception) { }
        return -1;
    }

    static int want = -1;
    static bool busy;
    static readonly object gate = new object();

    // WMI calls are slow; coalesce slider drags so only the latest value is applied.
    public static void SetBrightness(int level)
    {
        lock (gate) { want = level; if (busy) return; busy = true; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                int v;
                lock (gate) { v = want; want = -1; if (v < 0) { busy = false; return; } }
                try
                {
                    using (var c = new ManagementClass("root\\WMI", "WmiMonitorBrightnessMethods", null))
                        foreach (ManagementObject o in c.GetInstances()) using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)v });
                }
                catch (Exception) { }
            }
        });
    }
}
