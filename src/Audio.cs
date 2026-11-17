using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Minimal Core Audio interop: default output/mic, volume, mute, change notifications, output switching.
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceCollection
{
    void GetCount(out int c);
    void Item(int i, out IMMDevice d);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice
{
    void Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
    void OpenPropertyStore(int access, out IPropertyStore ps);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    void GetCount(out int c);
    void GetAt(int i, out PropKey k);
    void GetValue(ref PropKey k, out PropVariant v);
}

[StructLayout(LayoutKind.Sequential)] public struct PropKey { public Guid fmt; public int pid; public PropKey(string g, int p) { fmt = new Guid(g); pid = p; } }
[StructLayout(LayoutKind.Sequential)] public struct PropVariant { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    void GetChannelCount(out int c);
    void SetMasterVolumeLevel(float db, ref Guid ctx);
    void SetMasterVolumeLevelScalar(float v, ref Guid ctx);
    void GetMasterVolumeLevel(out float db);
    void GetMasterVolumeLevelScalar(out float v);
    void SetChannelVolumeLevel(int ch, float db, ref Guid ctx);
    void SetChannelVolumeLevelScalar(int ch, float v, ref Guid ctx);
    void GetChannelVolumeLevel(int ch, out float db);
    void GetChannelVolumeLevelScalar(int ch, out float v);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

public class VolumeCallback : IAudioEndpointVolumeCallback { public int OnNotify(IntPtr d) { Audio.Notify(); return 0; } }

// Undocumented but stable since Vista; it is what the Windows sound flyout uses to change the default device.
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPolicyConfig
{
    [PreserveSig] int M0(); [PreserveSig] int M1(); [PreserveSig] int M2(); [PreserveSig] int M3(); [PreserveSig] int M4();
    [PreserveSig] int M5(); [PreserveSig] int M6(); [PreserveSig] int M7(); [PreserveSig] int M8(); [PreserveSig] int M9();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
}

class Endpoint
{
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);
    readonly IMMDevice dev;
    IAudioEndpointVolume vol;
    public readonly string Id, Name;
    public readonly bool Headphones;

    public Endpoint(IMMDevice d)
    {
        dev = d;
        d.GetId(out Id);
        IPropertyStore ps;
        d.OpenPropertyStore(0, out ps);
        PropVariant v;
        var k = new PropKey("a45c254e-df1c-4efd-8020-67d146a850e0", 14); // PKEY_Device_FriendlyName
        ps.GetValue(ref k, out v);
        Name = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "Audio device";
        PropVariantClear(ref v);
        k = new PropKey("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0); // PKEY_AudioEndpoint_FormFactor
        ps.GetValue(ref k, out v);
        int ff = v.vt == 19 ? (int)(v.p.ToInt64() & 0xFFFFFFFF) : -1;
        PropVariantClear(ref v);
        Headphones = ff == 3 || ff == 5; // Headphones, Headset
    }

    public IAudioEndpointVolume Vol
    {
        get
        {
            if (vol == null)
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                object o;
                dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                vol = (IAudioEndpointVolume)o;
            }
            return vol;
        }
    }

    public float Volume
    {
        get { float v = 0; try { Vol.GetMasterVolumeLevelScalar(out v); } catch (Exception) { } return v; }
        set { try { var g = Guid.Empty; Vol.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, value)), ref g); } catch (Exception) { } }
    }

    public bool Muted
    {
        get { bool m = false; try { Vol.GetMute(out m); } catch (Exception) { } return m; }
        set { try { var g = Guid.Empty; Vol.SetMute(value, ref g); } catch (Exception) { } }
    }
}

static class Audio
{
    static readonly VolumeCallback cb = new VolumeCallback();
    public static Endpoint Out, Mic; // null when there is no such device
    public static event Action Changed; // raised on an audio thread

    internal static void Notify() { var c = Changed; if (c != null) c(); }

    static IMMDeviceEnumerator En() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); }

    static Endpoint Default(int flow)
    {
        try { IMMDevice d; En().GetDefaultAudioEndpoint(flow, 1, out d); return new Endpoint(d); }
        catch (Exception) { return null; }
    }

    // Re-binds to the current default devices (call after device changes).
    public static void Reset()
    {
        if (Out != null) try { Out.Vol.UnregisterControlChangeNotify(cb); } catch (Exception) { }
        Out = Default(0);
        Mic = Default(1);
        if (Out != null) try { Out.Vol.RegisterControlChangeNotify(cb); } catch (Exception) { }
    }

    public static List<Endpoint> Outputs()
    {
        var list = new List<Endpoint>();
        try
        {
            IMMDeviceCollection c;
            En().EnumAudioEndpoints(0, 1, out c); // render, DEVICE_STATE_ACTIVE
            int n;
            c.GetCount(out n);
            for (int i = 0; i < n; i++) { IMMDevice d; c.Item(i, out d); list.Add(new Endpoint(d)); }
        }
        catch (Exception) { }
        return list;
    }

    public static void SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) pc.SetDefaultEndpoint(id, role); // console, multimedia, communications
        }
        catch (Exception) { }
        Reset();
    }
}

// Per-app volume: each app's audio session on the speakers, with its own level and mute.
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid ctx);
    void GetMasterVolume(out float level);
    void SetMute(bool mute, ref Guid ctx);
    void GetMute(out bool mute);
}

class AppVolume
{
    public string Name;
    public ISimpleAudioVolume Vol;
    public float Level { get { float v = 0; try { Vol.GetMasterVolume(out v); } catch (Exception) { } return v; } set { try { var g = Guid.Empty; Vol.SetMasterVolume(Math.Max(0, Math.Min(1, value)), ref g); } catch (Exception) { } } }
    public bool Muted { get { bool m = false; try { Vol.GetMute(out m); } catch (Exception) { } return m; } set { try { var g = Guid.Empty; Vol.SetMute(value, ref g); } catch (Exception) { } } }

    // Apps with sound on the current output (playing or paused, not closed), one row per app, at most `max`.
    public static List<AppVolume> List(int max)
    {
        var list = new List<AppVolume>();
        var seen = new HashSet<string>();
        try
        {
            IMMDevice d;
            ((IMMDeviceEnumerator)new MMDeviceEnumeratorCo()).GetDefaultAudioEndpoint(0, 1, out d); // render, multimedia
            Guid iid = typeof(IAudioSessionManager2).GUID;
            object o;
            d.Activate(ref iid, 23, IntPtr.Zero, out o);
            IAudioSessionEnumerator sessions;
            ((IAudioSessionManager2)o).GetSessionEnumerator(out sessions);
            int n;
            sessions.GetCount(out n);
            for (int i = 0; i < n && list.Count < max; i++)
            {
                object so;
                sessions.GetSession(i, out so);
                var ctl = (IAudioSessionControl2)so;
                int state;
                ctl.GetState(out state);
                uint pid;
                ctl.GetProcessId(out pid);
                if (state == 2 || pid == 0 || pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) continue; // closed, system sounds, ourselves
                string name = AppName(pid, state == 1);
                if (name == null || !seen.Add(name)) continue; // one row per app (browsers open several sessions)
                list.Add(new AppVolume { Name = name, Vol = (ISimpleAudioVolume)so });
            }
        }
        catch (Exception) { } // no output device
        return list;
    }

    // null for apps to leave out: gone, or a windowless background service that isn't playing anything right now.
    static string AppName(uint pid, bool playing)
    {
        try
        {
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
            {
                if (!playing && p.MainWindowHandle == IntPtr.Zero) return null;
                try { string d = p.MainModule.FileVersionInfo.FileDescription; if (!string.IsNullOrEmpty(d)) return d; } catch (Exception) { } // elevated apps hide their details
                return p.ProcessName;
            }
        }
        catch (ArgumentException) { return null; } // already gone
    }
}
