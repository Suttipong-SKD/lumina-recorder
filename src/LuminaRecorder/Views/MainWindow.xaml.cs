using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LuminaRecorder.Services;
using Microsoft.Win32;
using ScreenRecorderLib;

namespace LuminaRecorder.Views
{
    public partial class MainWindow : Window
    {
        readonly AppSettings _s = AppSettings.Load();
        readonly RecorderService _rec = new RecorderService();
        List<MonitorInfo> _monitors = new List<MonitorInfo>();
        HotkeyService _hotkeys;
        AudioMeter _sysMeter, _micMeter;
        double _sysLevel, _micLevel;
        readonly DispatcherTimer _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        readonly DispatcherTimer _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        IntPtr _hwnd;
        bool _loading = true;
        bool _countingDown;
        CountdownWindow _countdown;
        RecordingBarWindow _bar;
        readonly List<RegionFrameWindow> _frames = new List<RegionFrameWindow>();
        WindowState _stateBeforeRecording = WindowState.Normal;

        public MainWindow()
        {
            InitializeComponent();

            _meterTimer.Tick += (s, e) => UpdateMeters();
            _toastTimer.Tick += (s, e) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };

            _rec.StatusChanged += st => Dispatcher.BeginInvoke(new Action(() => OnRecorderStatus(st)));
            _rec.Completed += path => Dispatcher.BeginInvoke(new Action(() => OnRecordingDone(path, null)));
            _rec.Failed += err => Dispatcher.BeginInvoke(new Action(() => OnRecordingDone(null, err)));

            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            Loaded += OnLoaded;
            Closing += OnClosing;
            StateChanged += (s, e) => RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(1);
        }

        // ------------------------------------------------------------------ startup

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.UseDarkTitleBar(_hwnd);
            _hotkeys = new HotkeyService(_hwnd);
            RegisterHotkeys();
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!double.IsNaN(_s.WindowLeft) && !double.IsNaN(_s.WindowTop) && IsOnScreen(_s.WindowLeft, _s.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _s.WindowLeft; Top = _s.WindowTop;
            }

            _monitors = MonitorService.GetMonitors();
            PixelRect.TryParse(_s.LastRegion, out var region);
            _region = region;

            FillCombos();
            CursorCheck.IsChecked = _s.ShowCursor;
            ClicksCheck.IsChecked = _s.HighlightClicks;
            SysAudioCheck.IsChecked = _s.RecordSystemAudio;
            MicCheck.IsChecked = _s.RecordMicrophone;

            switch (_s.Mode)
            {
                case CaptureMode.Region: ModeRegion.IsChecked = true; break;
                case CaptureMode.Window: ModeWindow.IsChecked = true; break;
                case CaptureMode.AllScreens: ModeAll.IsChecked = true; break;
                default: ModeFull.IsChecked = true; break;
            }

            _loading = false;
            ApplyMode();
            RestartMeters();
            RefreshRecent();
            UpdateHotkeyLabels();
            SetStatus($"ตรวจพบ {_monitors.Count} จอ · ไฟล์จะถูกบันทึกที่ {_s.EffectiveOutputFolder}");
        }

        static bool IsOnScreen(double left, double top)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return vs.Contains(new Point(left + 40, top + 20));
        }

        PixelRect _region;

        void FillCombos()
        {
            Fill(RegionPresetBox, _s.RegionPreset,
                ("free", "ขนาดอิสระ"), ("1920x1080", "1920 × 1080 (Full HD)"), ("1280x720", "1280 × 720 (HD)"),
                ("854x480", "854 × 480"), ("1080x1920", "1080 × 1920 (แนวตั้ง)"));
            Fill(FormatBox, _s.UseH265 ? "h265" : "h264", ("h264", "MP4 · H.264"), ("h265", "MP4 · H.265 (ไฟล์เล็ก)"));
            Fill(FpsBox, _s.Framerate.ToString(), ("24", "24 fps"), ("30", "30 fps"), ("60", "60 fps"));
            Fill(QualityBox, QualityKey(_s.Quality), ("40", "ประหยัดพื้นที่"), ("60", "มาตรฐาน"), ("75", "สูง"), ("90", "สูงสุด"));
            FillDevices();
        }

        static string QualityKey(int q) => q >= 85 ? "90" : q >= 70 ? "75" : q >= 50 ? "60" : "40";

        static void Fill(ComboBox box, string selected, params (string tag, string text)[] items)
        {
            box.Items.Clear();
            foreach (var (tag, text) in items)
                box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? box.Items[0];
        }

        void FillDevices()
        {
            var wasLoading = _loading;
            _loading = true;
            try
            {
                var speakers = SafeList(() => Recorder.GetSystemAudioLoopbackDevices().Select(d => (d.DeviceName, d.FriendlyName, d.IsDefaultDevice)).ToList());
                var mics = SafeList(() => Recorder.GetSystemAudioCaptureDevices().Select(d => (d.DeviceName, d.FriendlyName, d.IsDefaultDevice)).ToList());
                var cams = SafeList(() => Recorder.GetSystemVideoCaptureDevices().Select(d => (d.DeviceName, d.FriendlyName, false)).ToList());

                FillDeviceBox(SpeakerBox, speakers, _s.SpeakerDevice, "ลำโพงเริ่มต้นของ Windows");
                FillDeviceBox(MicBox, mics, _s.MicrophoneDevice, "ไมค์เริ่มต้นของ Windows");

                WebcamBox.Items.Clear();
                WebcamBox.Items.Add(new ComboBoxItem { Content = "ปิด", Tag = null });
                foreach (var c in cams) WebcamBox.Items.Add(new ComboBoxItem { Content = c.Item2, Tag = c.Item1 });
                WebcamBox.SelectedItem = _s.WebcamOverlay
                    ? WebcamBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.WebcamDevice) ?? WebcamBox.Items[Math.Min(1, WebcamBox.Items.Count - 1)]
                    : WebcamBox.Items[0];
                WebcamBox.IsEnabled = cams.Count > 0;
            }
            finally { _loading = wasLoading; }
        }

        static List<(string, string, bool)> SafeList(Func<List<(string, string, bool)>> f)
        {
            try { return f() ?? new List<(string, string, bool)>(); }
            catch { return new List<(string, string, bool)>(); }
        }

        static void FillDeviceBox(ComboBox box, List<(string id, string name, bool isDefault)> devices, string selected, string defaultLabel)
        {
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = defaultLabel, Tag = null });
            foreach (var d in devices) box.Items.Add(new ComboBoxItem { Content = d.name, Tag = d.id });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag != null && (string)i.Tag == selected) ?? box.Items[0];
        }

        static string SelTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        // ------------------------------------------------------------------ hotkeys

        void RegisterHotkeys()
        {
            if (_hotkeys == null) return;
            _hotkeys.UnregisterAll();
            var failed = new List<string>();
            if (!_hotkeys.Register(_s.HotkeyRecord, ToggleRecording)) failed.Add(_s.HotkeyRecord);
            if (!_hotkeys.Register(_s.HotkeyPause, () => { if (_rec.IsBusy) _rec.TogglePause(); })) failed.Add(_s.HotkeyPause);
            if (!_hotkeys.Register(_s.HotkeyScreenshot, () => TakeScreenshot())) failed.Add(_s.HotkeyScreenshot);
            if (failed.Count > 0)
                ShowToast($"ปุ่มลัด {string.Join(", ", failed)} ถูกโปรแกรมอื่นใช้อยู่ เปลี่ยนได้ในหน้าตั้งค่า");
        }

        void UpdateHotkeyLabels()
        {
            RecordKey.Text = _s.HotkeyRecord;
            ShotKey.Text = _s.HotkeyScreenshot;
        }

        // ------------------------------------------------------------------ modes & monitor map

        CaptureMode CurrentMode =>
            ModeRegion.IsChecked == true ? CaptureMode.Region :
            ModeWindow.IsChecked == true ? CaptureMode.Window :
            ModeAll.IsChecked == true ? CaptureMode.AllScreens : CaptureMode.FullScreen;

        void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.Mode = CurrentMode;
            _s.Save();
            ApplyMode();
        }

        void ApplyMode()
        {
            var mode = CurrentMode;
            RegionPanel.Visibility = mode == CaptureMode.Region ? Visibility.Visible : Visibility.Collapsed;
            WindowPanel.Visibility = mode == CaptureMode.Window ? Visibility.Visible : Visibility.Collapsed;
            MonitorCanvas.Visibility = mode == CaptureMode.Window ? Visibility.Collapsed : Visibility.Visible;

            switch (mode)
            {
                case CaptureMode.FullScreen:
                    MapTitle.Text = "เลือกจอที่จะอัด";
                    MapHint.Text = $"ตรวจพบ {_monitors.Count} จอ · คลิกจอเพื่อเลือก";
                    break;
                case CaptureMode.AllScreens:
                    MapTitle.Text = "อัดทุกจอรวมเป็นวิดีโอเดียว";
                    var vs = MonitorService.VirtualScreen(_monitors);
                    MapHint.Text = $"ขนาดวิดีโอ {vs.Width} × {vs.Height}";
                    break;
                case CaptureMode.Region:
                    MapTitle.Text = "เลือกพื้นที่ (ลากคร่อมหลายจอได้)";
                    MapHint.Text = "คลิกที่แผนผังหรือกดปุ่มด้านล่างเพื่อลากเลือกพื้นที่";
                    break;
                case CaptureMode.Window:
                    MapTitle.Text = "อัดเฉพาะหน้าต่างโปรแกรม";
                    MapHint.Text = "";
                    RefreshWindows();
                    break;
            }
            UpdateRegionInfo();
            DrawMap();
        }

        MonitorInfo SelectedMonitor =>
            _monitors.FirstOrDefault(m => m.DeviceName == _s.SelectedMonitor)
            ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();

        void MapHost_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMap();

        void DrawMap()
        {
            MonitorCanvas.Children.Clear();
            if (_monitors.Count == 0) return;
            double w = MapHost.ActualWidth, h = MapHost.ActualHeight;
            if (w < 20 || h < 20) return;

            var vs = MonitorService.VirtualScreen(_monitors);
            const double gap = 10;
            double scale = Math.Min((w - 20) / vs.Width, (h - 20) / vs.Height);
            double ox = (w - vs.Width * scale) / 2, oy = (h - vs.Height * scale) / 2;
            var mode = CurrentMode;
            var selected = SelectedMonitor;

            foreach (var m in _monitors)
            {
                bool on = mode == CaptureMode.AllScreens || (mode == CaptureMode.FullScreen && m == selected);
                var tile = new Button
                {
                    Style = (Style)FindResource("MonitorTile"),
                    Width = Math.Max(20, m.Bounds.Width * scale - gap),
                    Height = Math.Max(20, m.Bounds.Height * scale - gap),
                    Background = on ? (Brush)FindResource("AccentSoft") : new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x26)),
                    BorderBrush = on ? (Brush)FindResource("Accent") : new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
                    Tag = m,
                    ToolTip = $"จอ {m.Index}: {m.Description}{(m.IsPrimary ? " (จอหลัก)" : "")}"
                };
                AutomationPropertiesHelper.SetName(tile, $"จอ {m.Index}");
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock { Text = m.Index.ToString(), FontSize = Math.Min(30, Math.Max(14, tile.Height / 5)), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
                if (tile.Height > 70)
                {
                    stack.Children.Add(new TextBlock { Text = m.Description, FontSize = 12, Foreground = (Brush)FindResource("TextSoft"), HorizontalAlignment = HorizontalAlignment.Center });
                    stack.Children.Add(new TextBlock { Text = m.IsPrimary ? "จอหลัก" : m.DeviceName.Replace(@"\\.\", ""), FontSize = 11, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
                }
                tile.Content = stack;
                tile.Click += MonitorTile_Click;
                Canvas.SetLeft(tile, ox + (m.Bounds.X - vs.X) * scale + gap / 2);
                Canvas.SetTop(tile, oy + (m.Bounds.Y - vs.Y) * scale + gap / 2);
                MonitorCanvas.Children.Add(tile);
            }

            if (mode == CaptureMode.Region && !_region.IsEmpty)
            {
                var r = new Rectangle
                {
                    Width = Math.Max(4, _region.Width * scale),
                    Height = Math.Max(4, _region.Height * scale),
                    Stroke = (Brush)FindResource("Rec"),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xE5, 0x38, 0x4B)),
                    RadiusX = 4, RadiusY = 4,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(r, ox + (_region.X - vs.X) * scale);
                Canvas.SetTop(r, oy + (_region.Y - vs.Y) * scale);
                MonitorCanvas.Children.Add(r);

                var label = new Border
                {
                    Background = (Brush)FindResource("Rec"),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    IsHitTestVisible = false,
                    Child = new TextBlock { Text = RegionLabel(_region), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
                };
                Canvas.SetLeft(label, ox + (_region.X - vs.X) * scale);
                Canvas.SetTop(label, Math.Max(0, oy + (_region.Y - vs.Y) * scale - 24));
                MonitorCanvas.Children.Add(label);
            }
        }

        string RegionLabel(PixelRect r)
        {
            int count = _monitors.Count(m => m.Bounds.IntersectsWith(r));
            return $"{r.Width} × {r.Height}" + (count > 1 ? $" · คร่อม {count} จอ" : "");
        }

        async void MonitorTile_Click(object sender, RoutedEventArgs e)
        {
            var m = (MonitorInfo)((FrameworkElement)sender).Tag;
            switch (CurrentMode)
            {
                case CaptureMode.FullScreen:
                    _s.SelectedMonitor = m.DeviceName;
                    _s.Save();
                    DrawMap();
                    break;
                case CaptureMode.AllScreens:
                    ShowToast("โหมดนี้อัดทุกจอพร้อมกัน ถ้าต้องการจอเดียวให้เลือกโหมด เต็มจอ");
                    break;
                case CaptureMode.Region:
                    await SelectRegionAsync();
                    break;
            }
        }

        void UpdateRegionInfo()
        {
            RegionInfo.Text = _region.IsEmpty ? "ยังไม่ได้เลือกพื้นที่" : RegionLabel(_region);
        }

        void RegionPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.RegionPreset = SelTag(RegionPresetBox) ?? "free";
            _s.Save();
        }

        async void BtnSelectRegion_Click(object sender, RoutedEventArgs e) => await SelectRegionAsync();

        async Task<bool> SelectRegionAsync()
        {
            _monitors = MonitorService.GetMonitors();
            var preset = ParsePreset(_s.RegionPreset);
            var prevState = WindowState;
            Hide();
            await Task.Delay(180); // let the window disappear before the overlay appears
            PixelRect? picked;
            try { picked = await RegionSelector.SelectAsync(_monitors, preset, _region); }
            finally
            {
                Show();
                WindowState = prevState == WindowState.Minimized ? WindowState.Normal : prevState;
                Activate();
            }
            if (picked == null) return false;
            _region = picked.Value.MakeEven();
            _s.LastRegion = _region.ToString();
            _s.Save();
            UpdateRegionInfo();
            DrawMap();
            return true;
        }

        static (int w, int h)? ParsePreset(string p)
        {
            if (string.IsNullOrEmpty(p) || p == "free") return null;
            var parts = p.Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) ? (w, h) : ((int, int)?)null;
        }

        void OnDisplaysChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _monitors = MonitorService.GetMonitors();
                if (!_rec.IsBusy) ApplyMode();
                ShowToast($"การตั้งค่าจอเปลี่ยน ตอนนี้มี {_monitors.Count} จอ");
            }), DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------ windows list

        void RefreshWindows()
        {
            var wasLoading = _loading;
            _loading = true;
            try
            {
                var current = (WindowBox.SelectedItem as ComboBoxItem)?.Tag as RecordableWindow;
                WindowBox.Items.Clear();
                List<RecordableWindow> windows;
                try { windows = Recorder.GetWindows() ?? new List<RecordableWindow>(); }
                catch { windows = new List<RecordableWindow>(); }
                foreach (var w in windows.Where(w => !string.IsNullOrWhiteSpace(w.Title) && w.Handle != _hwnd))
                    WindowBox.Items.Add(new ComboBoxItem { Content = w.Title, Tag = w });
                var keep = current == null ? null : WindowBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => ((RecordableWindow)i.Tag).Handle == current.Handle);
                WindowBox.SelectedItem = keep ?? (WindowBox.Items.Count > 0 ? WindowBox.Items[0] : null);
            }
            finally { _loading = wasLoading; }
        }

        void BtnRefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

        void WindowBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        // ------------------------------------------------------------------ options

        void Option_Changed(object sender, RoutedEventArgs e)
        {
            _s.ShowCursor = CursorCheck.IsChecked == true;
            _s.HighlightClicks = ClicksCheck.IsChecked == true;
            _s.Save();
        }

        void Audio_Changed(object sender, RoutedEventArgs e)
        {
            _s.RecordSystemAudio = SysAudioCheck.IsChecked == true;
            _s.RecordMicrophone = MicCheck.IsChecked == true;
            _s.Save();
            RestartMeters();
        }

        void Device_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.SpeakerDevice = SelTag(SpeakerBox);
            _s.MicrophoneDevice = SelTag(MicBox);
            _s.Save();
            RestartMeters();
        }

        void Video_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.UseH265 = SelTag(FormatBox) == "h265";
            if (int.TryParse(SelTag(FpsBox), out int fps)) _s.Framerate = fps;
            if (int.TryParse(SelTag(QualityBox), out int q)) _s.Quality = q;
            var cam = SelTag(WebcamBox);
            _s.WebcamOverlay = cam != null;
            _s.WebcamDevice = cam;
            _s.Save();
        }

        // ------------------------------------------------------------------ meters

        void RestartMeters()
        {
            _sysMeter?.Dispose(); _sysMeter = null;
            _micMeter?.Dispose(); _micMeter = null;
            if (_s.RecordSystemAudio) _sysMeter = AudioMeter.TryCreate(false, _s.SpeakerDevice);
            if (_s.RecordMicrophone) _micMeter = AudioMeter.TryCreate(true, _s.MicrophoneDevice);
            _meterTimer.Start();
        }

        void UpdateMeters()
        {
            _sysLevel = Smooth(_sysLevel, _sysMeter?.Peak ?? 0);
            _micLevel = Smooth(_micLevel, _micMeter?.Peak ?? 0);
            SetMeter(SysMeter, _sysLevel);
            SetMeter(MicMeter, _micLevel);
        }

        static double Smooth(double prev, double now) => now > prev ? now : Math.Max(0, prev - 0.04);

        static void SetMeter(Border bar, double level)
        {
            var track = bar.Parent as FrameworkElement;
            if (track == null) return;
            // perceptual curve so speech is visible
            bar.Width = track.ActualWidth * Math.Min(1, Math.Sqrt(level));
        }

        // ------------------------------------------------------------------ recording

        void BtnRecord_Click(object sender, RoutedEventArgs e) => ToggleRecording();

        async void ToggleRecording()
        {
            if (_countingDown) { _countdown?.Cancel(); return; }
            if (_rec.IsBusy) { _rec.Stop(); return; }
            await StartRecordingAsync();
        }

        async Task<CaptureTarget> BuildTargetAsync(bool forScreenshot)
        {
            _monitors = MonitorService.GetMonitors();
            var t = new CaptureTarget { Mode = CurrentMode, Monitors = _monitors, Monitor = SelectedMonitor };
            switch (t.Mode)
            {
                case CaptureMode.Region:
                    if (_region.IsEmpty || !_monitors.Any(m => m.Bounds.IntersectsWith(_region)) || forScreenshot && !IsVisible)
                    {
                        if (!await SelectRegionAsync()) return null;
                    }
                    t.Region = _region;
                    break;
                case CaptureMode.Window:
                    var w = (WindowBox.SelectedItem as ComboBoxItem)?.Tag as RecordableWindow;
                    if (w == null || !Native.IsWindow(w.Handle))
                    {
                        RefreshWindows();
                        ShowToast("กรุณาเลือกหน้าต่างที่จะอัด");
                        return null;
                    }
                    if (Native.IsIconic(w.Handle)) Native.ShowWindow(w.Handle, Native.SW_RESTORE);
                    t.Window = w.Handle;
                    t.WindowTitle = w.Title;
                    break;
            }
            return t;
        }

        async Task StartRecordingAsync()
        {
            var target = await BuildTargetAsync(false);
            if (target == null) return;
            var bounds = target.Bounds;
            if (bounds.IsEmpty) { ShowToast("ไม่พบพื้นที่ที่จะอัด"); return; }

            var barMonitor = _monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(bounds)) ?? SelectedMonitor;

            _stateBeforeRecording = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            if (_s.MinimizeWhileRecording) WindowState = WindowState.Minimized;
            else Native.ExcludeFromCapture(_hwnd);

            if (target.Mode == CaptureMode.Window) Native.SetForegroundWindow(target.Window);

            if (_s.CountdownSeconds > 0)
            {
                _countingDown = true;
                RecordText.Text = "ยกเลิก";
                _countdown = new CountdownWindow(_s.CountdownSeconds, bounds, barMonitor);
                bool go = await _countdown.RunAsync();
                _countdown = null;
                _countingDown = false;
                if (!go) { RecordText.Text = "เริ่มอัด"; RestoreAfterRecording(); return; }
            }

            if (target.Mode == CaptureMode.Region)
                foreach (var m in _monitors.Where(m => m.Bounds.IntersectsWith(target.Region)))
                {
                    var f = new RegionFrameWindow(target.Region, m);
                    f.Show();
                    _frames.Add(f);
                }

            try
            {
                _rec.Start(target, _s);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                OnRecordingDone(null, ex.Message);
                return;
            }

            _bar = new RecordingBarWindow(_rec, bounds, barMonitor, _s);
            _bar.StopRequested += () => _rec.Stop();
            _bar.Show();
        }

        void OnRecorderStatus(RecorderStatus st)
        {
            switch (st)
            {
                case RecorderStatus.Recording:
                    RecordText.Text = "หยุดอัด";
                    SetStatus("กำลังอัด… กด " + _s.HotkeyRecord + " เพื่อหยุด, " + _s.HotkeyPause + " เพื่อพัก");
                    break;
                case RecorderStatus.Paused:
                    RecordText.Text = "หยุดอัด";
                    SetStatus("พักการอัดอยู่ กด " + _s.HotkeyPause + " เพื่ออัดต่อ");
                    break;
                case RecorderStatus.Finishing:
                    RecordText.Text = "กำลังบันทึก…";
                    BtnRecord.IsEnabled = false;
                    break;
                case RecorderStatus.Idle:
                    RecordText.Text = "เริ่มอัด";
                    BtnRecord.IsEnabled = true;
                    break;
            }
            _bar?.OnStatus(st);
        }

        void OnRecordingDone(string path, string error)
        {
            _bar?.Close(); _bar = null;
            foreach (var f in _frames) f.Close();
            _frames.Clear();
            RecordText.Text = "เริ่มอัด";
            BtnRecord.IsEnabled = true;
            RestoreAfterRecording();

            if (error != null)
            {
                SetStatus("อัดไม่สำเร็จ: " + error);
                MessageBox.Show(this, "อัดไม่สำเร็จ\n\n" + error + "\n\nลองเปลี่ยนโหมด หรือปิด 'ใช้การ์ดจอช่วยเข้ารหัส' ในหน้าตั้งค่า",
                    "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetStatus("บันทึกแล้ว: " + System.IO.Path.GetFileName(path));
            ShowToast("บันทึกวิดีโอแล้ว · " + System.IO.Path.GetFileName(path));
            RefreshRecent();
        }

        void RestoreAfterRecording()
        {
            try { Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_NONE); } catch { }
            if (WindowState == WindowState.Minimized) WindowState = _stateBeforeRecording;
            Activate();
        }

        // ------------------------------------------------------------------ screenshot

        void BtnScreenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

        async void TakeScreenshot()
        {
            if (_countingDown) return;
            var target = await BuildTargetAsync(true);
            if (target == null) return;

            bool hide = IsVisible && WindowState != WindowState.Minimized && target.Bounds.IntersectsWith(WindowPixelBounds());
            if (hide) { Opacity = 0; await Task.Delay(200); }
            try
            {
                var path = ScreenshotService.Capture(target, _s);
                ShowToast("บันทึกภาพแล้ว" + (_s.CopyScreenshotToClipboard ? " และคัดลอกไว้ในคลิปบอร์ด" : ""));
                SetStatus("ภาพหน้าจอ: " + System.IO.Path.GetFileName(path));
                RefreshRecent();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                ShowToast("จับภาพไม่สำเร็จ: " + ex.Message);
            }
            finally { if (hide) Opacity = 1; }
        }

        PixelRect WindowPixelBounds()
        {
            var r = Native.GetVisibleWindowBounds(_hwnd);
            return PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        // ------------------------------------------------------------------ recent files

        void RefreshRecent()
        {
            RecentList.Children.Clear();
            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(_s.EffectiveOutputFolder).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime).Take(6).ToList();
            }
            catch { files = new List<FileInfo>(); }

            RecentEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var f in files)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var thumb = new Border { Width = 64, Height = 40, CornerRadius = new CornerRadius(6), Background = (Brush)FindResource("Surface3"), ClipToBounds = true };
                bool isImage = f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
                if (isImage && f.Length < 40_000_000)
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.DecodePixelWidth = 128;
                        bi.UriSource = new Uri(f.FullName);
                        bi.EndInit();
                        thumb.Background = new ImageBrush(bi) { Stretch = Stretch.UniformToFill };
                    }
                    catch { }
                }
                else
                {
                    thumb.Child = new TextBlock { Text = "MP4", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                grid.Children.Add(thumb);

                var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = f.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = $"{FriendlyDate(f.LastWriteTime)} · {FriendlySize(f.Length)}", FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);

                var item = new Button { Style = (Style)FindResource("RecentItem"), Content = grid, Tag = f.FullName, ToolTip = "คลิกเพื่อเปิด · คลิกขวาเพื่อแสดงในโฟลเดอร์" };
                AutomationPropertiesHelper.SetName(item, f.Name);
                item.Click += (s, e) => Open(f.FullName);
                item.MouseRightButtonUp += (s, e) => Open("explorer.exe", $"/select,\"{f.FullName}\"");
                RecentList.Children.Add(item);
            }
        }

        static string FriendlyDate(DateTime t) =>
            t.Date == DateTime.Today ? $"วันนี้ {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"เมื่อวาน {t:HH:mm}" : t.ToString("d MMM HH:mm");

        internal static string FriendlySize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
            bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        static void Open(string file, string args = null)
        {
            try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { App.Log(ex); }
        }

        void BtnOpenFolder_Click(object sender, RoutedEventArgs e) => Open(_s.EffectiveOutputFolder);

        // ------------------------------------------------------------------ chrome

        void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SettingsWindow(_s) { Owner = this };
            _hotkeys?.UnregisterAll(); // let the dialog receive the keys being assigned
            bool saved = dlg.ShowDialog() == true;
            if (saved) _s.Save();
            RegisterHotkeys();
            if (saved)
            {
                UpdateHotkeyLabels();
                RefreshRecent();
            }
        }

        void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_rec.IsBusy)
            {
                var r = MessageBox.Show(this, "กำลังอัดอยู่ ต้องการหยุดและปิดโปรแกรมหรือไม่?", "Lumina Recorder", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                _rec.Stop();
            }
            if (WindowState == WindowState.Normal) { _s.WindowLeft = Left; _s.WindowTop = Top; }
            _s.Save();
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            _meterTimer.Stop();
            _sysMeter?.Dispose();
            _micMeter?.Dispose();
            _hotkeys?.Dispose();
            _bar?.Close();
            foreach (var f in _frames) f.Close();
            _rec.Dispose();
        }

        // ------------------------------------------------------------------ feedback

        void SetStatus(string text) => StatusText.Text = text;

        void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    internal static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);
    }
}
