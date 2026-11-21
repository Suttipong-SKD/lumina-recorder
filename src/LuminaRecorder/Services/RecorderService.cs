using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ScreenRecorderLib;

namespace LuminaRecorder.Services
{
    /// <summary>What to record, resolved by the UI before recording starts.</summary>
    public sealed class CaptureTarget
    {
        public CaptureMode Mode { get; set; }
        public List<MonitorInfo> Monitors { get; set; } = new List<MonitorInfo>(); // every monitor on the system
        public MonitorInfo Monitor { get; set; }                                   // FullScreen
        public PixelRect Region { get; set; }                                      // Region (physical px, may span monitors)
        public IntPtr Window { get; set; }                                         // Window
        public string WindowTitle { get; set; }

        /// <summary>The area on the virtual desktop this target covers (physical pixels).</summary>
        public PixelRect Bounds
        {
            get
            {
                switch (Mode)
                {
                    case CaptureMode.FullScreen: return Monitor?.Bounds ?? new PixelRect();
                    case CaptureMode.AllScreens: return MonitorService.VirtualScreen(Monitors);
                    case CaptureMode.Region: return Region;
                    case CaptureMode.Window:
                        if (Window == IntPtr.Zero || !Native.IsWindow(Window)) return new PixelRect();
                        var r = Native.GetVisibleWindowBounds(Window);
                        return PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                }
                return new PixelRect();
            }
        }
    }

    /// <summary>Thin wrapper around ScreenRecorderLib (Media Foundation, hardware H.264/H.265 encoding).</summary>
    public sealed class RecorderService : IDisposable
    {
        Recorder _recorder;
        readonly Stopwatch _clock = new Stopwatch();
        AudioSourceBase _micSource;
        bool _micMuted;
        CaptureTarget _target;
        AppSettings _settings;
        bool _triedFallbackApi;
        bool _useDesktopDuplication;
        DateTime _startedAt;
        bool _hardware;
        int _segment;
        readonly List<string> _files = new List<string>();
        string _baseFile;

        /// <summary>Raised when the graphics card encoder failed and recording continued with the CPU encoder.</summary>
        public event Action<string> SwitchedToSoftware;   // path of the new part file
        /// <summary>All files written by the last recording (more than one if the encoder had to be switched).</summary>
        public IReadOnlyList<string> Files => _files;

        public event Action<RecorderStatus> StatusChanged;
        public event Action<string> Completed;   // file path
        public event Action<string> Failed;      // error message

        public RecorderStatus Status { get; private set; } = RecorderStatus.Idle;
        public string CurrentFile { get; private set; }
        public TimeSpan Elapsed => _clock.Elapsed;
        public bool IsBusy => Status != RecorderStatus.Idle;
        public bool IsMicMuted => _micMuted;
        public bool HasMicrophone => _micSource != null;

        public static string NewFileName(string folder, string ext) =>
            Path.Combine(folder, $"Lumina_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{ext}");

        public void Start(CaptureTarget target, AppSettings settings)
        {
            if (IsBusy) return;
            _target = target;
            _settings = settings;
            _triedFallbackApi = false;
            _useDesktopDuplication = !SupportsGraphicsCapture;
            _hardware = settings.HardwareEncoding;
            _segment = 1;
            _files.Clear();
            _baseFile = NewFileName(settings.EffectiveOutputFolder, "mp4");
            CurrentFile = _baseFile;
            StartInternal(resetClock: true);
        }

        static bool SupportsGraphicsCapture => Environment.OSVersion.Version.Build >= 19041;

        void StartInternal() => StartInternal(true);

        void StartInternal(bool resetClock)
        {
            DisposeRecorder();
            var options = BuildOptions(_target, _settings, _useDesktopDuplication, out _micSource);
            options.VideoEncoderOptions.IsHardwareEncodingEnabled = _hardware;
            if (!_files.Contains(CurrentFile)) _files.Add(CurrentFile);
            _micMuted = false;
            _recorder = Recorder.CreateRecorder(options);
            _recorder.OnStatusChanged += (s, e) => OnStatus(e.Status);
            _recorder.OnRecordingComplete += (s, e) =>
            {
                _clock.Stop();
                Status = RecorderStatus.Idle;
                StatusChanged?.Invoke(Status);
                Completed?.Invoke(e.FilePath);
            };
            _recorder.OnRecordingFailed += (s, e) =>
            {
                // Windows Graphics Capture can fail on some multi-GPU laptops: retry once with Desktop Duplication.
                if (!_triedFallbackApi && !_useDesktopDuplication && _target.Mode != CaptureMode.Window
                    && (DateTime.Now - _startedAt).TotalSeconds < 3)
                {
                    _triedFallbackApi = true;
                    _useDesktopDuplication = true;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(StartInternal));
                    return;
                }
                // The graphics card encoder can stall after a while (0xC00D36B5 MF_E_NOTACCEPTING and similar).
                // Keep recording: continue straight away into a new part file using the CPU encoder.
                if (_hardware && Status != RecorderStatus.Finishing && IsEncoderError(e.Error))
                {
                    _hardware = false;
                    _segment++;
                    CurrentFile = Path.Combine(Path.GetDirectoryName(_baseFile),
                        Path.GetFileNameWithoutExtension(_baseFile) + $"_part{_segment}.mp4");
                    var next = CurrentFile;
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StartInternal(resetClock: false);
                        SwitchedToSoftware?.Invoke(next);
                    }));
                    return;
                }
                _clock.Stop();
                Status = RecorderStatus.Idle;
                StatusChanged?.Invoke(Status);
                Failed?.Invoke(e.Error);
            };
            _startedAt = DateTime.Now;
            if (resetClock) _clock.Reset();
            _clock.Start();
            Status = RecorderStatus.Recording;
            StatusChanged?.Invoke(Status);
            _recorder.Record(CurrentFile);
        }

        static bool IsEncoderError(string error)
        {
            if (string.IsNullOrEmpty(error)) return true;
            var e = error.ToLowerInvariant();
            return e.Contains("encoder") || e.Contains("0xc00d36b5") || e.Contains("hardware") || e.Contains("not accepting")
                   || e.Contains("0x887a0005") || e.Contains("0x887a0006"); // GPU device removed / hung
        }

        void OnStatus(RecorderStatus s)
        {
            Status = s;
            if (s == RecorderStatus.Recording && !_clock.IsRunning) _clock.Start();
            if (s == RecorderStatus.Paused) _clock.Stop();
            StatusChanged?.Invoke(s);
        }

        public void Stop()
        {
            if (_recorder == null || Status == RecorderStatus.Idle || Status == RecorderStatus.Finishing) return;
            _clock.Stop();
            Status = RecorderStatus.Finishing;
            StatusChanged?.Invoke(Status);
            _recorder.Stop();
        }

        public void Pause()
        {
            if (_recorder != null && Status == RecorderStatus.Recording) { _recorder.Pause(); _clock.Stop(); }
        }

        public void Resume()
        {
            if (_recorder != null && Status == RecorderStatus.Paused) { _recorder.Resume(); _clock.Start(); }
        }

        public void TogglePause()
        {
            if (_recorder == null) return;
            if (Status == RecorderStatus.Recording) { _recorder.Pause(); _clock.Stop(); }
            else if (Status == RecorderStatus.Paused) { _recorder.Resume(); _clock.Start(); }
        }

        public bool ToggleMicMute()
        {
            if (_recorder == null || _micSource == null) return _micMuted;
            _micMuted = !_micMuted;
            _micSource.IsAudioCaptureEnabled = !_micMuted;
            try { _recorder.GetDynamicOptionsBuilder().SetUpdatedAudioSource(_micSource).Apply(); } catch { }
            return _micMuted;
        }

        public static RecorderOptions BuildOptions(CaptureTarget target, AppSettings s, bool useDesktopDuplication, out AudioSourceBase micSource)
        {
            var api = useDesktopDuplication ? RecorderApi.DesktopDuplication : RecorderApi.WindowsGraphicsCapture;
            var sources = new List<RecordingSourceBase>();
            ScreenRect sourceRect = null;
            ScreenSize frameSize = null;

            List<RecordableDisplay> displays;
            try { displays = Recorder.GetDisplays() ?? new List<RecordableDisplay>(); }
            catch { displays = new List<RecordableDisplay>(); }

            DisplayRecordingSource DisplayFor(MonitorInfo m)
            {
                var d = displays.FirstOrDefault(x => string.Equals(x.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase));
                var src = d != null ? new DisplayRecordingSource(d) : new DisplayRecordingSource(m.DeviceName);
                src.RecorderApi = api;
                src.IsCursorCaptureEnabled = s.ShowCursor;
                src.IsBorderRequired = false;
                return src;
            }

            switch (target.Mode)
            {
                case CaptureMode.FullScreen:
                    sources.Add(DisplayFor(target.Monitor ?? target.Monitors.First(m => m.IsPrimary)));
                    break;

                case CaptureMode.AllScreens:
                    foreach (var m in target.Monitors) sources.Add(DisplayFor(m));
                    break;

                case CaptureMode.Window:
                    sources.Add(new WindowRecordingSource(target.Window)
                    {
                        IsCursorCaptureEnabled = s.ShowCursor,
                        IsBorderRequired = false
                    });
                    break;

                case CaptureMode.Region:
                {
                    var region = target.Region.MakeEven();
                    var touched = target.Monitors.Where(m => m.Bounds.IntersectsWith(region)).ToList();
                    if (touched.Count == 0) touched.Add(target.Monitors.First());
                    var displaySources = touched.Select(DisplayFor).ToList();
                    sources.AddRange(displaySources);

                    // The recorder lays the chosen monitors out on one canvas. Find where the first monitor landed
                    // on that canvas, then express the region relative to it. Works for any monitor arrangement.
                    var anchor = touched[0];
                    double offX = -PixelRect.Union(touched.Select(m => m.Bounds)).X;
                    double offY = -PixelRect.Union(touched.Select(m => m.Bounds)).Y;
                    try
                    {
                        var dims = Recorder.GetOutputDimensionsForRecordingSources(displaySources.Cast<RecordingSourceBase>());
                        var c = dims?.OutputCoordinates?.FirstOrDefault(o =>
                            o.Source is DisplayRecordingSource ds &&
                            string.Equals(ds.DeviceName, displaySources[0].DeviceName, StringComparison.OrdinalIgnoreCase));
                        if (c?.Coordinates != null)
                        {
                            offX = c.Coordinates.Left - anchor.Bounds.X;
                            offY = c.Coordinates.Top - anchor.Bounds.Y;
                        }
                    }
                    catch { /* fall back to desktop layout */ }

                    sourceRect = new ScreenRect(region.X + offX, region.Y + offY, region.Width, region.Height);
                    frameSize = new ScreenSize(region.Width, region.Height);
                    break;
                }
            }

            // ---- audio ----
            micSource = null;
            var audioSources = new List<AudioSourceBase>();
            if (s.RecordSystemAudio)
            {
                try
                {
                    var outs = Recorder.GetSystemAudioLoopbackDevices();
                    var dev = outs?.FirstOrDefault(d => d.DeviceName == s.SpeakerDevice)
                              ?? outs?.FirstOrDefault(d => d.IsDefaultDevice) ?? outs?.FirstOrDefault();
                    if (dev != null) { dev.IsAudioCaptureEnabled = true; audioSources.Add(dev); }
                }
                catch { }
            }
            if (s.RecordMicrophone)
            {
                try
                {
                    var ins = Recorder.GetSystemAudioCaptureDevices();
                    var dev = ins?.FirstOrDefault(d => d.DeviceName == s.MicrophoneDevice)
                              ?? ins?.FirstOrDefault(d => d.IsDefaultDevice) ?? ins?.FirstOrDefault();
                    if (dev != null) { dev.IsAudioCaptureEnabled = true; audioSources.Add(dev); micSource = dev; }
                }
                catch { }
            }

            // ---- overlays (webcam) ----
            var overlays = new List<RecordingOverlayBase>();
            if (s.WebcamOverlay)
            {
                try
                {
                    var cams = Recorder.GetSystemVideoCaptureDevices();
                    var cam = cams?.FirstOrDefault(c => c.DeviceName == s.WebcamDevice) ?? cams?.FirstOrDefault();
                    if (cam != null)
                        overlays.Add(new VideoCaptureOverlay(cam.DeviceName)
                        {
                            AnchorPoint = Anchor.BottomRight,
                            Offset = new ScreenSize(24, 24),
                            Size = new ScreenSize(0, 240),
                            Stretch = StretchMode.Uniform
                        });
                }
                catch { }
            }

            IVideoEncoder encoder = s.UseH265
                ? (IVideoEncoder)new H265VideoEncoder { BitrateMode = H265BitrateControlMode.Quality, EncoderProfile = H265Profile.Main }
                : new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High };

            var output = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                Stretch = StretchMode.Uniform
            };
            if (sourceRect != null) output.SourceRect = sourceRect;
            if (frameSize != null) output.OutputFrameSize = frameSize;

            return new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = sources },
                OutputOptions = output,
                AudioOptions = new AudioOptions
                {
                    IsAudioEnabled = audioSources.Count > 0,
                    AudioSources = audioSources,
                    Bitrate = AudioBitrate.bitrate_192kbps,
                    Channels = AudioChannels.Stereo
                },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = encoder,
                    Framerate = Math.Max(10, Math.Min(60, s.Framerate)),
                    Quality = Math.Max(10, Math.Min(100, s.Quality)),
                    IsFixedFramerate = true,
                    IsHardwareEncodingEnabled = s.HardwareEncoding,
                    IsMp4FastStartEnabled = false,
                    IsFragmentedMp4Enabled = false,
                    IsLowLatencyEnabled = false,
                    IsThrottlingDisabled = false
                },
                MouseOptions = new MouseOptions
                {
                    IsMousePointerEnabled = s.ShowCursor,
                    IsMouseClicksDetected = s.HighlightClicks,
                    MouseLeftClickDetectionColor = "#FFFFC83D",
                    MouseRightClickDetectionColor = "#FF5B8CFF",
                    MouseClickDetectionRadius = 22,
                    MouseClickDetectionDuration = 160,
                    MouseClickDetectionMode = MouseDetectionMode.Hook
                },
                OverlayOptions = new OverLayOptions { Overlays = overlays },
                LogOptions = new LogOptions
                {
                    IsLogEnabled = true,
                    LogSeverityLevel = LogLevel.Warn,
                    LogFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaRecorder", "recorder.log")
                }
            };
        }

        void DisposeRecorder()
        {
            try { _recorder?.Dispose(); } catch { }
            _recorder = null;
        }

        public void Dispose()
        {
            try { if (IsBusy) _recorder?.Stop(); } catch { }
            DisposeRecorder();
        }
    }
}
