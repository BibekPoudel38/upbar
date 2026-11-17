using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Sound level meter: listens through the default microphone only while it is on (never at start-up), using the
// classic waveIn API with three 100 ms buffers polled on the UI thread: no extra threads, almost no CPU.
static class DbMeter
{
    [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public short tag, channels; public int rate, bytesPerSec; public short align, bits, extra; }
    [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr data; public int length, recorded; public IntPtr user; public int flags, loops; public IntPtr next, reserved; }
    [DllImport("winmm.dll")] static extern int waveInOpen(out IntPtr h, int device, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr h);

    const int Rate = 16000, Samples = 1600, Buffers = 3; // 16 kHz mono, 100 ms per buffer
    static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
    static IntPtr wave;
    static readonly IntPtr[] hdrs = new IntPtr[Buffers];
    static readonly short[] pcm = new short[Samples];
    static readonly Timer poll = new Timer { Interval = 50 };
    static float target, quietFor, loudFor;
    public static float Level, Peak;     // dB (eased for display), loudest since switched on
    public static int Alert = 85;        // dB that counts as loud
    public static bool Loud;             // above Alert for 3 s; clears after 5 s back under it
    public static event Action Changed;  // on the UI thread, every 50 ms while on
    public static bool On { get { return wave != IntPtr.Zero; } }

    static DbMeter() { poll.Tick += (s, e) => Tick(); }

    // shortcut: uncalibrated, like phone meter apps: dB SPL ~ dBFS + 100 for typical laptop mics; a per-mic offset setting if it reads off.
    public static float ToDb(double rms) { return (float)Math.Max(20, Math.Min(120, 20 * Math.Log10(Math.Max(rms, 1) / 32768.0) + 100)); }

    public static string Start() // null when listening, else why not
    {
        if (On) return null;
        var f = new WAVEFORMATEX { tag = 1, channels = 1, rate = Rate, bits = 16, align = 2, bytesPerSec = Rate * 2 };
        if (waveInOpen(out wave, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { wave = IntPtr.Zero; return "No microphone available"; }
        for (int i = 0; i < Buffers; i++)
        {
            var h = new WAVEHDR { data = Marshal.AllocHGlobal(Samples * 2), length = Samples * 2 };
            hdrs[i] = Marshal.AllocHGlobal(HdrSize); // the driver writes into it, so it lives in fixed memory
            Marshal.StructureToPtr(h, hdrs[i], false);
            waveInPrepareHeader(wave, hdrs[i], HdrSize);
            waveInAddBuffer(wave, hdrs[i], HdrSize);
        }
        Level = target = 30; Peak = 0; quietFor = loudFor = 0; Loud = false;
        waveInStart(wave);
        poll.Start();
        return null;
    }

    public static void Stop()
    {
        if (!On) return;
        poll.Stop();
        waveInReset(wave); // hands every buffer back
        for (int i = 0; i < Buffers; i++)
        {
            waveInUnprepareHeader(wave, hdrs[i], HdrSize);
            var h = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
            Marshal.FreeHGlobal(h.data);
            Marshal.FreeHGlobal(hdrs[i]);
        }
        waveInClose(wave);
        wave = IntPtr.Zero;
        Loud = false;
        var c = Changed;
        if (c != null) c();
    }

    static void Tick()
    {
        for (int i = 0; i < Buffers; i++)
        {
            var h = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
            if ((h.flags & 1) == 0) continue; // WHDR_DONE: a filled buffer
            int n = Math.Min(Samples, h.recorded / 2);
            Marshal.Copy(h.data, pcm, 0, n);
            target = ToDb(Rms(pcm, n));
            Peak = Math.Max(Peak, target);
            waveInAddBuffer(wave, hdrs[i], HdrSize); // back in the queue
        }
        Level += (target - Level) * 0.35f; // glide toward the latest reading: smooth needle
        if (target >= Alert) { loudFor += 0.05f; quietFor = 0; if (loudFor >= 3) Loud = true; }
        else { quietFor += 0.05f; loudFor = 0; if (quietFor >= 5) Loud = false; }
        var c = Changed;
        if (c != null) c();
    }

    public static double Rms(short[] s, int n)
    {
        if (n <= 0) return 0;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / n);
    }
}
