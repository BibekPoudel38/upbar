using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

// The media session Windows shows in its own media flyout (Spotify, browsers, etc.). Event driven, no polling.
static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager mgr;
    static GlobalSystemMediaTransportControlsSession cur;
    static readonly object gate = new object(), loadGate = new object();
    static bool started;
    static string artFor;
    static readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProps = (s, e) => Load();
    static readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlay = (s, e) => Load();
    static readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTime = (s, e) => Load();

    public static volatile bool Has, Playing;
    public static string Title = "", Artist = "", App = "";
    public static Image Art;
    public static TimeSpan Position, Duration;
    public static DateTime PositionAt;
    public static event Action Changed; // raised on a worker thread

    static T Wait<T>(IAsyncOperation<T> op)
    {
        while (op.Status == AsyncStatus.Started) Thread.Sleep(5);
        return op.GetResults();
    }

    public static void Start()
    {
        if (started) return;
        started = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                mgr.CurrentSessionChanged += (s, e) => Bind();
                Bind();
            }
            catch (Exception) { }
        });
    }

    static void Bind()
    {
        lock (gate)
        {
            if (cur != null)
                try { cur.MediaPropertiesChanged -= onProps; cur.PlaybackInfoChanged -= onPlay; cur.TimelinePropertiesChanged -= onTime; }
                catch (Exception) { }
            cur = mgr.GetCurrentSession();
            if (cur != null) { cur.MediaPropertiesChanged += onProps; cur.PlaybackInfoChanged += onPlay; cur.TimelinePropertiesChanged += onTime; }
        }
        Load();
    }

    static void Load()
    {
        lock (loadGate)
        {
            var s = cur;
            try
            {
                if (s == null) Has = false;
                else
                {
                    var p = Wait(s.TryGetMediaPropertiesAsync());
                    var tl = s.GetTimelineProperties();
                    Title = p.Title ?? "";
                    Artist = p.Artist ?? "";
                    App = AppName(s.SourceAppUserModelId);
                    Playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    Position = tl.Position;
                    Duration = tl.EndTime - tl.StartTime;
                    PositionAt = tl.LastUpdatedTime.UtcDateTime;
                    string key = Title + "|" + Artist;
                    if (key != artFor)
                    {
                        Art = null;
                        if (p.Thumbnail != null) { Art = ReadArt(p.Thumbnail); artFor = key; }
                    }
                    Has = Title.Length > 0;
                }
            }
            catch (Exception) { }
        }
        var c = Changed;
        if (c != null) c();
    }

    // Square 128px crop: keeps memory flat no matter how big the cover is.
    internal static Image ReadArt(IRandomAccessStreamReference r)
    {
        using (var st = Wait(r.OpenReadAsync()))
        using (var dr = new DataReader(st))
        {
            uint n = Wait(dr.LoadAsync((uint)st.Size));
            var bytes = new byte[n];
            dr.ReadBytes(bytes);
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                var bmp = new Bitmap(128, 128);
                int sz = Math.Min(img.Width, img.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, new Rectangle(0, 0, 128, 128), new Rectangle((img.Width - sz) / 2, (img.Height - sz) / 2, sz, sz), GraphicsUnit.Pixel);
                }
                return bmp;
            }
        }
    }

    static string AppName(string aumid)
    {
        string id = aumid ?? "";
        int i = id.LastIndexOf('!');
        if (i >= 0) id = id.Substring(i + 1);
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 4);
        return id;
    }

    public static TimeSpan Now
    {
        get
        {
            var p = Position;
            if (Playing) p += DateTime.UtcNow - PositionAt;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p;
        }
    }

    public static void Toggle() { Playing = !Playing; Run(s => s.TryTogglePlayPauseAsync(), 0xB3); } // optimistic; the event corrects it
    public static void Next() { Run(s => s.TrySkipNextAsync(), 0xB0); }
    public static void Prev() { Run(s => s.TrySkipPreviousAsync(), 0xB1); }
    public static void Seek(double sec) { Run(s => s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(sec).Ticks), 0); }

    // Some apps (and browsers between videos) refuse the session call; the media key reaches them anyway.
    static void Run(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> f, byte mediaKey)
    {
        var s = cur;
        if (s == null) { if (mediaKey != 0) Native.Tap(mediaKey); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool ok = false;
            try { ok = Wait(f(s)); } catch (Exception) { }
            if (!ok && mediaKey != 0) Native.Tap(mediaKey);
        });
    }
}
