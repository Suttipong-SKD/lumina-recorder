using System;
using System.IO;
using System.Xml.Serialization;

namespace LuminaRecorder.Services
{
    public enum CaptureMode { FullScreen, Region, Window, AllScreens }

    /// <summary>User settings, saved to %AppData%\LuminaRecorder\settings.xml.</summary>
    public sealed class AppSettings
    {
        public CaptureMode Mode { get; set; } = CaptureMode.FullScreen;
        public string SelectedMonitor { get; set; }       // device name
        public string LastRegion { get; set; }            // PixelRect.ToString()
        public string RegionPreset { get; set; } = "free"; // free | 1280x720 | 1920x1080 | 854x480

        public bool RecordSystemAudio { get; set; } = true;
        public bool RecordMicrophone { get; set; } = false;
        public string MicrophoneDevice { get; set; }       // null = default
        public string SpeakerDevice { get; set; }          // null = default

        public int Framerate { get; set; } = 30;
        public int Quality { get; set; } = 70;              // 0–100
        public bool UseH265 { get; set; } = false;
        public bool HardwareEncoding { get; set; } = true;

        public bool ShowCursor { get; set; } = true;
        public bool HighlightClicks { get; set; } = true;
        public bool WebcamOverlay { get; set; } = false;
        public string WebcamDevice { get; set; }

        public bool AutoPauseWithVideo { get; set; } = false;
        public int CountdownSeconds { get; set; } = 3;
        public bool MinimizeWhileRecording { get; set; } = true;
        public bool CopyScreenshotToClipboard { get; set; } = true;

        public string OutputFolder { get; set; }

        public string HotkeyRecord { get; set; } = "F9";
        public string HotkeyPause { get; set; } = "F10";
        public string HotkeyScreenshot { get; set; } = "F11";

        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;

        [XmlIgnore]
        public string EffectiveOutputFolder
        {
            get
            {
                var folder = string.IsNullOrWhiteSpace(OutputFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Lumina Recorder")
                    : OutputFolder;
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaRecorder", "settings.xml");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    using (var s = File.OpenRead(SettingsPath))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(s) ?? new AppSettings();
            }
            catch { /* corrupt file: start fresh */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                using (var s = File.Create(SettingsPath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(s, this);
            }
            catch { }
        }
    }
}
