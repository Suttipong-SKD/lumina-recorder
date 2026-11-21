using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;

namespace LuminaRecorder.Services
{
    /// <summary>
    /// Watches what is playing on Windows (YouTube/Netflix in a browser, Spotify, Media Player... anything that shows
    /// in the Windows media overlay) and tells the recorder when the video being recorded pauses or resumes.
    ///
    /// The first item that is playing once recording starts becomes "the video". When it stops, <see cref="ShouldPause"/>
    /// is raised; when the same item (same app + same title) plays again, <see cref="ShouldResume"/> is raised.
    /// Something else starting to play does not resume the recording.
    ///
    /// For players that don't report to Windows, it falls back to the speaker level: silence for a few seconds pauses,
    /// sound resumes.
    /// </summary>
    public sealed class MediaWatcher : IDisposable
    {
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly string _speakerDevice;
        GlobalSystemMediaTransportControlsSessionManager _manager;
        AudioMeter _meter;
        bool _busy, _disposed;

        string _trackedApp, _trackedTitle;   // the video being recorded
        bool _isPlaying = true;              // assume playing at start: recording begins normally
        DateTime _silentSince = DateTime.MaxValue;
        DateTime _stateSince = DateTime.Now;

        static readonly TimeSpan PauseDelay = TimeSpan.FromSeconds(1.5);      // ignore very short stalls
        static readonly TimeSpan SilenceDelay = TimeSpan.FromSeconds(4);      // fallback: silence before pausing

        public event Action ShouldPause;
        public event Action ShouldResume;

        /// <summary>Shown to the user, e.g. "YouTube – Lesson 3 (Chrome)".</summary>
        public string TrackedDescription => _trackedTitle == null ? null : $"{_trackedTitle} ({FriendlyApp(_trackedApp)})";
        public bool UsingMediaSessions => _manager != null;

        public MediaWatcher(string speakerDevice)
        {
            _speakerDevice = speakerDevice;
            _timer.Tick += async (s, e) => await PollAsync();
        }

        public async Task StartAsync()
        {
            try
            {
                if (Environment.OSVersion.Version.Build >= 17763)
                    _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                _manager = null;
            }
            _meter = AudioMeter.TryCreate(false, _speakerDevice);
            _timer.Start();
        }

        async Task PollAsync()
        {
            if (_busy || _disposed) return;
            _busy = true;
            try
            {
                bool? playing = await MediaPlayingAsync();
                if (playing == null) playing = AudioPlaying();
                Apply(playing.Value);
            }
            catch (Exception ex) { App.Log(ex); }
            finally { _busy = false; }
        }

        /// <summary>True/false when a media session tells us; null when no player reports to Windows.</summary>
        async Task<bool?> MediaPlayingAsync()
        {
            if (_manager == null) return null;
            var sessions = _manager.GetSessions();
            if (sessions == null || sessions.Count == 0) return null;

            foreach (var s in sessions)
            {
                var info = s.GetPlaybackInfo();
                bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                string title = null;
                try { title = (await s.TryGetMediaPropertiesAsync())?.Title; } catch { }

                if (_trackedApp == null)
                {
                    if (playing)
                    {
                        _trackedApp = s.SourceAppUserModelId;
                        _trackedTitle = string.IsNullOrWhiteSpace(title) ? "วิดีโอ" : title;
                        return true;
                    }
                    continue;
                }

                if (s.SourceAppUserModelId == _trackedApp && SameTitle(title, _trackedTitle))
                    return playing;
            }

            // The tracked video's tab/player was closed, or it moved to another title: treat as stopped.
            return _trackedApp == null ? (bool?)null : false;
        }

        static bool SameTitle(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || b == "วิดีโอ") return true; // some players don't report a title
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        bool AudioPlaying()
        {
            float peak = _meter?.Peak ?? 1f;
            if (peak > 0.004f) { _silentSince = DateTime.MaxValue; return true; }
            if (_silentSince == DateTime.MaxValue) _silentSince = DateTime.Now;
            return DateTime.Now - _silentSince < SilenceDelay;
        }

        void Apply(bool playing)
        {
            if (playing == _isPlaying) { _stateSince = DateTime.Now; return; }

            // debounce: a pause must last a moment before we act; resume immediately
            if (!playing && DateTime.Now - _stateSince < PauseDelay) return;

            _isPlaying = playing;
            _stateSince = DateTime.Now;
            if (playing) ShouldResume?.Invoke(); else ShouldPause?.Invoke();
        }

        static string FriendlyApp(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            var s = aumid.ToLowerInvariant();
            if (s.Contains("chrome")) return "Chrome";
            if (s.Contains("msedge")) return "Edge";
            if (s.Contains("firefox")) return "Firefox";
            if (s.Contains("spotify")) return "Spotify";
            if (s.Contains("zune") || s.Contains("mediaplayer")) return "Media Player";
            if (s.Contains("vlc")) return "VLC";
            var last = aumid.Split('\\', '!', '.').LastOrDefault(p => p.Length > 0);
            return last ?? aumid;
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _meter?.Dispose();
            _meter = null;
            _manager = null;
        }
    }
}
