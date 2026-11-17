using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Android-style privacy indicators: which apps are using the microphone or a camera right now.
// (The registry usage records this used to read stopped being written on newer Windows builds.)
//  - Mic: active recording sessions on any capture device, from Core Audio.
//  - Camera: Media Foundation's sensor activity monitor, which pushes a report whenever camera use changes.
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr a, int b, out IntPtr c);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr a, int b, out IntPtr c);
    void GetSessionEnumerator(out IAudioSessionEnumerator e);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEnumerator
{
    void GetCount(out int n);
    void GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl2
{
    void GetState(out int state); // 1 = active
    [PreserveSig] int M1(); [PreserveSig] int M2(); [PreserveSig] int M3(); [PreserveSig] int M4();
    [PreserveSig] int M5(); [PreserveSig] int M6(); [PreserveSig] int M7(); [PreserveSig] int M8();
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    void GetProcessId(out uint pid);
}

[ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSensorActivityMonitor { void Start(); void Stop(); }

[ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSensorActivitiesReportCallback { [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report); }

[ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSensorActivitiesReport
{
    void GetCount(out uint n);
    void GetActivityReport(uint i, out IMFSensorActivityReport report);
}

[ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSensorActivityReport
{
    void GetFriendlyName([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, uint cch, out uint written);
    void GetSymbolicLink([MarshalAs(UnmanagedType.LPWStr)] StringBuilder link, uint cch, out uint written);
    void GetProcessCount(out uint n);
    void GetProcessActivity(uint i, out IMFSensorProcessActivity activity);
}

[ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSensorProcessActivity
{
    void GetProcessId(out uint pid);
    void GetStreamingState([MarshalAs(UnmanagedType.Bool)] out bool streaming);
}

public class CameraWatch : IMFSensorActivitiesReportCallback
{
    public int OnActivitiesReport(IMFSensorActivitiesReport report) { Privacy.OnCamera(report); return 0; }
}

static class Privacy
{
    [DllImport("mfsensorgroup.dll")] static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);
    [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);

    public static volatile bool Mic, Cam;
    public static List<string> Who = new List<string>(); // "App  ·  Microphone"
    public static event Action Changed; // raised on a worker thread
    static readonly object gate = new object();
    static List<string> mic = new List<string>(), cam = new List<string>();
    static Timer poll;
    static IMFSensorActivityMonitor monitor;
    static CameraWatch watch; // kept alive: Media Foundation holds only a COM pointer to it

    public static void Start()
    {
        // shortcut: mic sessions are checked every 2 s (a few COM calls); move to IAudioSessionNotification events if it ever shows in a profile.
        poll = new Timer(_ => ScanMic(), null, 0, 2000);
        try
        {
            watch = new CameraWatch();
            MFStartup(0x20070, 0); // MF_VERSION; without it the monitor fails with MF_E_PLATFORM_NOT_INITIALIZED
            if (MFCreateSensorActivityMonitor(watch, out monitor) == 0) monitor.Start();
        }
        catch (Exception) { } // no sensor monitor on this PC: the camera indicator stays off
    }

    static void ScanMic()
    {
        var who = new List<string>();
        var junk = new List<object>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            junk.Add(en);
            IMMDeviceCollection devices;
            en.EnumAudioEndpoints(1, 1, out devices); // capture, active
            junk.Add(devices);
            int n;
            devices.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice d;
                devices.Item(i, out d);
                junk.Add(d);
                Guid iid = typeof(IAudioSessionManager2).GUID;
                object o;
                d.Activate(ref iid, 23, IntPtr.Zero, out o);
                junk.Add(o);
                IAudioSessionEnumerator sessions;
                ((IAudioSessionManager2)o).GetSessionEnumerator(out sessions);
                junk.Add(sessions);
                int k;
                sessions.GetCount(out k);
                for (int j = 0; j < k; j++)
                {
                    object so;
                    sessions.GetSession(j, out so);
                    junk.Add(so);
                    var s = (IAudioSessionControl2)so;
                    int state;
                    s.GetState(out state);
                    if (state != 1) continue;
                    uint pid;
                    s.GetProcessId(out pid);
                    Add(who, pid, "Microphone");
                }
            }
        }
        catch (Exception) { } // device unplugged mid-scan: try again next tick
        finally { foreach (var j in junk) try { Marshal.ReleaseComObject(j); } catch (ArgumentException) { } }
        Update(who, null);
    }

    public static void OnCamera(IMFSensorActivitiesReport report)
    {
        var who = new List<string>();
        try
        {
            uint n;
            report.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IMFSensorActivityReport r;
                report.GetActivityReport(i, out r);
                uint p;
                r.GetProcessCount(out p);
                for (uint j = 0; j < p; j++)
                {
                    IMFSensorProcessActivity a;
                    r.GetProcessActivity(j, out a);
                    bool streaming;
                    a.GetStreamingState(out streaming);
                    uint pid;
                    a.GetProcessId(out pid);
                    if (streaming) Add(who, pid, "Camera");
                }
            }
        }
        catch (Exception) { }
        Update(null, who);
    }

    static void Add(List<string> who, uint pid, string what)
    {
        if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return; // system sounds / ourselves
        string name;
        try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; }
        catch (ArgumentException) { return; } // already exited
        string line = name + "  ·  " + what;
        if (!who.Contains(line)) who.Add(line);
    }

    static void Update(List<string> newMic, List<string> newCam)
    {
        lock (gate)
        {
            var m = newMic ?? mic;
            var c = newCam ?? cam;
            if (string.Join("|", m) == string.Join("|", mic) && string.Join("|", c) == string.Join("|", cam)) return;
            mic = m;
            cam = c;
            Mic = m.Count > 0;
            Cam = c.Count > 0;
            var all = new List<string>(c);
            all.AddRange(m);
            Who = all;
        }
        var h = Changed;
        if (h != null) h();
    }
}
